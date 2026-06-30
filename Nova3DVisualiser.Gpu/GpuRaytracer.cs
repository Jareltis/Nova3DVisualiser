using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace Nova3DVisualiser.Gpu;

// Per-frame scalar inputs packed into one blittable struct so the kernel takes few arguments.
public struct GpuGlobals
{
    public int Width, Height;
    public float Aspect;
    public float CamX, CamY, CamZ;
    public float BxX, BxY, BxZ;     // camera basis (ray dir = Bx*Focal + By*uv.Y + Bz*uv.X)
    public float ByX, ByY, ByZ;
    public float BzX, BzY, BzZ;
    public float Focal;
    public float Ambient, Exposure;
    public int EnableShadows;
    public int ObjectCount, SphereCount, LightCount;
}

// A ray hit returned to the compositor: world-space surface normal + albedo/alpha.
public struct GpuHit
{
    public float T;                 // -1 = miss
    public float Nx, Ny, Nz;        // world-space surface normal (unit)
    public float R, G, B, A;        // surface albedo 0..1 + alpha (for transparency compositing)
}

// A mesh-BVH face hit in LOCAL space: distance (world units, by the local-ray invariant) + the
// interpolated LOCAL normal (rotated to world once the closest object is known).
public struct GpuFaceHit
{
    public float T;
    public float Nx, Ny, Nz;
}

/// <summary>
/// The GPU raytracer: owns an ILGPU context + accelerator (NVIDIA CUDA when present, else — only for
/// headless self-tests — the managed CPU accelerator) and runs the raytracing kernel per console cell.
/// It is at FULL feature parity with the CPU renderer: front-to-back transparency compositing (depth
/// peeling), every LightKind (point / directional / spot / area), spot beam fans + circle/square/
/// triangle cone shapes, and area soft shadows — all independently shadow-tested.
///
/// Acceleration mirrors the CPU two-level scheme: each mesh keeps a LOCAL-space triangle list + BVH
/// (uploaded once, gated by GeometryVersion); per frame only the small per-object transform record
/// travels. The kernel transforms the ray into each object's local space and traverses its (stackless)
/// BVH, exactly like Object3d.GetRenderData. Spheres stay analytic. Match is validated pixel-for-pixel
/// by the `gputest` self-test.
/// </summary>
public sealed class GpuRaytracer : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private readonly Action<Index1D, GpuGlobals, ArrayView<Vector3>, ArrayView<SnapFace>,
        ArrayView<SnapBvhNode>, ArrayView<int>, ArrayView<SnapObject>, ArrayView<SnapSphere>,
        ArrayView<SnapLight>, ArrayView<float>, ArrayView<int>> _kernel;

    // Static geometry (uploaded only when the mesh set changes).
    private MemoryBuffer1D<Vector3, Stride1D.Dense>? _vertBuf;
    private MemoryBuffer1D<SnapFace, Stride1D.Dense>? _faceBuf;
    private MemoryBuffer1D<SnapBvhNode, Stride1D.Dense>? _nodeBuf;
    private MemoryBuffer1D<int, Stride1D.Dense>? _triIdxBuf;
    private int _uploadedGeomVersion = -1;

    // Per-frame data.
    private MemoryBuffer1D<SnapObject, Stride1D.Dense>? _objBuf;
    private MemoryBuffer1D<SnapSphere, Stride1D.Dense>? _sphereBuf;
    private MemoryBuffer1D<SnapLight, Stride1D.Dense>? _lightBuf;
    private MemoryBuffer1D<float, Stride1D.Dense>? _brightBuf;
    private MemoryBuffer1D<int, Stride1D.Dense>? _colorBuf;
    private int[] _colorScratch = Array.Empty<int>();

    public string AcceleratorName { get; }
    public bool IsHardwareGpu { get; }

    /// <param name="requireGpu">
    /// true (the live renderer): throw if no CUDA/OpenCL device exists, so the caller can fall back to
    /// the CPU renderer. false (self-test): allow the managed CPU accelerator so kernel logic can be
    /// validated without NVIDIA hardware.
    /// </param>
    public GpuRaytracer(bool requireGpu)
    {
        _context = Context.Create(b => b.Default().EnableAlgorithms());

        // Prefer a real GPU, and among GPUs prefer CUDA (the user's NVIDIA card) over OpenCL — so an
        // also-present Intel/AMD iGPU exposed via OpenCL doesn't get picked ahead of the NVIDIA dGPU.
        Device? cuda = null, otherGpu = null;
        foreach (var d in _context.Devices)
        {
            if (d.AcceleratorType == AcceleratorType.Cuda) { cuda ??= d; }
            else if (d.AcceleratorType != AcceleratorType.CPU) { otherGpu ??= d; }
        }

        Device chosen;
        if (cuda != null) { chosen = cuda; IsHardwareGpu = true; }
        else if (otherGpu != null) { chosen = otherGpu; IsHardwareGpu = true; }
        else if (requireGpu) { _context.Dispose(); throw new NotSupportedException("No CUDA/OpenCL GPU device available."); }
        else { chosen = _context.GetPreferredDevice(preferCPU: true); IsHardwareGpu = false; }

        _accelerator = chosen.CreateAccelerator(_context);
        AcceleratorName = $"{_accelerator.AcceleratorType} : {_accelerator.Name}";
        _kernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, GpuGlobals, ArrayView<Vector3>,
            ArrayView<SnapFace>, ArrayView<SnapBvhNode>, ArrayView<int>, ArrayView<SnapObject>,
            ArrayView<SnapSphere>, ArrayView<SnapLight>, ArrayView<float>, ArrayView<int>>(RenderKernel);
    }

    /// <summary>Renders the snapshot into the caller's brightness + color buffers (length = width*height).</summary>
    public void Render(SceneSnapshot snap, int width, int height, float aspect,
                       float[] brightnessOut, Rgb24[] colorOut)
    {
        int pixels = width * height;
        EnsureBuffers(snap);
        EnsureOutputs(pixels);

        var g = new GpuGlobals
        {
            Width = width, Height = height, Aspect = aspect,
            CamX = snap.CamPos.X, CamY = snap.CamPos.Y, CamZ = snap.CamPos.Z,
            BxX = snap.BasisX.X, BxY = snap.BasisX.Y, BxZ = snap.BasisX.Z,
            ByX = snap.BasisY.X, ByY = snap.BasisY.Y, ByZ = snap.BasisY.Z,
            BzX = snap.BasisZ.X, BzY = snap.BasisZ.Y, BzZ = snap.BasisZ.Z,
            Focal = snap.Focal,
            Ambient = snap.Ambient, Exposure = snap.Exposure, EnableShadows = snap.EnableShadows,
            ObjectCount = snap.Objects.Length, SphereCount = snap.Spheres.Length, LightCount = snap.Lights.Length,
        };

        _kernel(pixels, g, _vertBuf!.View, _faceBuf!.View, _nodeBuf!.View, _triIdxBuf!.View,
            _objBuf!.View, _sphereBuf!.View, _lightBuf!.View, _brightBuf!.View, _colorBuf!.View);
        _accelerator.Synchronize();

        _brightBuf!.View.CopyToCPU(brightnessOut);
        _colorBuf!.View.CopyToCPU(_colorScratch);
        for (int i = 0; i < pixels; i++)
        {
            int c = _colorScratch[i];
            colorOut[i] = new Rgb24((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
        }
    }

    private void EnsureBuffers(SceneSnapshot snap)
    {
        // Static mesh geometry + BVH: upload only when the mesh set changed (or first frame).
        if (snap.GeometryVersion != _uploadedGeomVersion || _vertBuf == null)
        {
            Upload(ref _vertBuf, snap.LocalVerts);
            Upload(ref _faceBuf, snap.Faces);
            Upload(ref _nodeBuf, snap.Nodes);
            Upload(ref _triIdxBuf, snap.TriIdx);
            _uploadedGeomVersion = snap.GeometryVersion;
        }
        // Per-frame: object transforms, spheres, lights.
        Upload(ref _objBuf, snap.Objects);
        Upload(ref _sphereBuf, snap.Spheres);
        Upload(ref _lightBuf, snap.Lights);
    }

    // Reallocate only when a count changes; otherwise reuse the buffer and copy into it. Lengths are
    // clamped to >=1 because ILGPU cannot allocate a zero-length buffer; the kernel loops to the real
    // counts in GpuGlobals.
    private void Upload<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? buf, T[] data) where T : unmanaged
    {
        long want = Math.Max(1, data.Length);
        if (buf == null || buf.Length < want || buf.Length > want * 2 + 16)
        {
            buf?.Dispose();
            buf = _accelerator.Allocate1D<T>(want);
        }
        if (data.Length > 0) buf.View.SubView(0, data.Length).CopyFromCPU(data);
    }

    private void EnsureOutputs(int pixels)
    {
        if (_brightBuf == null || _brightBuf.Length != pixels)
        {
            _brightBuf?.Dispose(); _colorBuf?.Dispose();
            _brightBuf = _accelerator.Allocate1D<float>(pixels);
            _colorBuf = _accelerator.Allocate1D<int>(pixels);
            _colorScratch = new int[pixels];
        }
    }

    // ===================== device code =====================

    private const float Bias = 0.01f;
    private const float MinVis = 1e-3f;   // alpha-shadow: transmittance at/below this counts as fully blocked
    private const float DirRefDistSq = 64f;
    private const float Tau = 6.2831853f;
    private const int MaxLayers = 32;   // transparency depth-peel cap (bounds the per-ray layer loop)

    private static void RenderKernel(Index1D index, GpuGlobals g,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres, ArrayView<SnapLight> lights,
        ArrayView<float> brightnessOut, ArrayView<int> colorOut)
    {
        int p = index.X;
        int i = p % g.Width;
        int j = p / g.Width;

        // uv exactly as ConsoleScreenAsync.CalculateUV (aspect on X, flipped Y).
        float uvx = ((float)i / (g.Width - 1) * 2f - 1f) * g.Aspect;
        float uvy = -((float)j / (g.Height - 1) * 2f - 1f);

        // Primary ray direction = Bx*Focal + By*uv.Y + Bz*uv.X, normalized.
        float dx = g.BxX * g.Focal + g.ByX * uvy + g.BzX * uvx;
        float dy = g.BxY * g.Focal + g.ByY * uvy + g.BzY * uvx;
        float dz = g.BxZ * g.Focal + g.ByZ * uvy + g.BzZ * uvx;
        float dinv = Rsqrt(dx * dx + dy * dy + dz * dz);
        dx *= dinv; dy *= dinv; dz *= dinv;

        // Front-to-back "over" compositing by DEPTH PEELING (see Scene.GetPixelData).
        float outR = 0f, outG = 0f, outB = 0f, accumA = 0f;
        float tMin = 0f;
        for (int layer = 0; layer < MaxLayers; layer++)
        {
            GpuHit hit = ClosestHit(g, g.CamX, g.CamY, g.CamZ, dx, dy, dz, tMin, verts, faces, nodes, triIdx, objects, spheres);
            if (hit.T < 0f) break;
            if (hit.A > 0f)
            {
                float hx = g.CamX + dx * hit.T;
                float hy = g.CamY + dy * hit.T;
                float hz = g.CamZ + dz * hit.T;
                Shade(g, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres, lights, out float lr, out float lg, out float lb);
                float w = (1f - accumA) * hit.A;
                outR += lr * w; outG += lg * w; outB += lb * w; accumA += w;
                if (accumA >= 0.99f) break;
            }
            tMin = hit.T + 1e-4f;
        }

        float brightness = 0.2126f * outR + 0.7152f * outG + 0.0722f * outB;
        brightnessOut[p] = brightness;
        colorOut[p] = (ToByte(outR) << 16) | (ToByte(outG) << 8) | ToByte(outB);
    }

    // Closest hit with T strictly greater than tMin, over every mesh (two-level BVH) + every sphere.
    // Visibility uses all objects (markers included); the closest mesh hit's local normal is rotated
    // to world here, once the winner is known.
    private static GpuHit ClosestHit(GpuGlobals g, float ox, float oy, float oz, float dx, float dy, float dz, float tMin,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres)
    {
        float bestT = -1f; int kind = 0;
        float bnx = 0f, bny = 0f, bnz = 0f, br = 0f, bg = 0f, bb = 0f, ba = 0f;

        for (int o = 0; o < g.ObjectCount; o++)
        {
            SnapObject ob = objects[o];
            if (!RayHitsAabb(ox, oy, oz, dx, dy, dz, ob.WorldMin.X, ob.WorldMin.Y, ob.WorldMin.Z, ob.WorldMax.X, ob.WorldMax.Y, ob.WorldMax.Z))
                continue;

            // World ray -> object local: local = (Rfwd^T * (p - Position)) * invScale (no trig).
            float rx = ox - ob.Position.X, ry = oy - ob.Position.Y, rz = oz - ob.Position.Z;
            float lsx = (ob.ColX.X * rx + ob.ColX.Y * ry + ob.ColX.Z * rz) * ob.InvScale;
            float lsy = (ob.ColY.X * rx + ob.ColY.Y * ry + ob.ColY.Z * rz) * ob.InvScale;
            float lsz = (ob.ColZ.X * rx + ob.ColZ.Y * ry + ob.ColZ.Z * rz) * ob.InvScale;
            float ldx = (ob.ColX.X * dx + ob.ColX.Y * dy + ob.ColX.Z * dz) * ob.InvScale;
            float ldy = (ob.ColY.X * dx + ob.ColY.Y * dy + ob.ColY.Z * dz) * ob.InvScale;
            float ldz = (ob.ColZ.X * dx + ob.ColZ.Y * dy + ob.ColZ.Z * dz) * ob.InvScale;

            GpuFaceHit fh = TraverseClosest(ob, lsx, lsy, lsz, ldx, ldy, ldz, tMin, verts, faces, nodes, triIdx);
            if (fh.T > tMin && (bestT < 0f || fh.T < bestT))
            {
                bestT = fh.T; kind = 1;
                // Local normal -> world: wn = ColX*n.x + ColY*n.y + ColZ*n.z.
                bnx = ob.ColX.X * fh.Nx + ob.ColY.X * fh.Ny + ob.ColZ.X * fh.Nz;
                bny = ob.ColX.Y * fh.Nx + ob.ColY.Y * fh.Ny + ob.ColZ.Y * fh.Nz;
                bnz = ob.ColX.Z * fh.Nx + ob.ColY.Z * fh.Ny + ob.ColZ.Z * fh.Nz;
                br = ob.R; bg = ob.G; bb = ob.B; ba = ob.A;
            }
        }

        for (int s = 0; s < g.SphereCount; s++)
        {
            SnapSphere sp = spheres[s];
            GpuHit hs = HitSphere(sp, ox, oy, oz, dx, dy, dz);
            if (hs.T > tMin && (bestT < 0f || hs.T < bestT))
            {
                bestT = hs.T; kind = 2;
                bnx = hs.Nx; bny = hs.Ny; bnz = hs.Nz; br = sp.R; bg = sp.G; bb = sp.B; ba = sp.A;
            }
        }

        GpuHit hit;
        hit.T = bestT;
        if (kind == 1) { float inv = Rsqrt(bnx * bnx + bny * bny + bnz * bnz); hit.Nx = bnx * inv; hit.Ny = bny * inv; hit.Nz = bnz * inv; }
        else { hit.Nx = bnx; hit.Ny = bny; hit.Nz = bnz; }   // sphere normal already unit (or unused on miss)
        hit.R = br; hit.G = bg; hit.B = bb; hit.A = ba;
        return hit;
    }

    // Closest face hit (t > tMin) inside one mesh's local BVH — stackless: index+1 is the left child on
    // an AABB hit, Exit is the miss/after-leaf jump (-1 ends). Returns the LOCAL interpolated normal.
    private static GpuFaceHit TraverseClosest(SnapObject ob, float sx, float sy, float sz, float dx, float dy, float dz, float tMin,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx)
    {
        GpuFaceHit best; best.T = -1f; best.Nx = 0f; best.Ny = 0f; best.Nz = 0f;
        int i = 0;
        while (i != -1)
        {
            SnapBvhNode nd = nodes[ob.NodeBase + i];
            if (RayHitsAabb(sx, sy, sz, dx, dy, dz, nd.Min.X, nd.Min.Y, nd.Min.Z, nd.Max.X, nd.Max.Y, nd.Max.Z))
            {
                if (nd.TriCount > 0)
                {
                    for (int k = 0; k < nd.TriCount; k++)
                    {
                        int faceLocal = triIdx[ob.TriIdxBase + nd.TriStart + k];
                        SnapFace f = faces[ob.FaceBase + faceLocal];
                        GpuFaceHit h = HitFaceLocal(f, verts, ob.VertBase, sx, sy, sz, dx, dy, dz);
                        if (h.T > tMin && (best.T < 0f || h.T < best.T)) best = h;
                    }
                    i = nd.Exit;
                }
                else i = i + 1;   // internal hit -> left child
            }
            else i = nd.Exit;
        }
        return best;
    }

    // Möller–Trumbore in LOCAL space (matches Triangle.GetRenderData): back-face cull, barycentric
    // interpolation of the local vertex normals (left un-normalized; the caller rotates then normalizes,
    // which commutes). t is the world distance by the local-ray invariant (local dir is not unit).
    private static GpuFaceHit HitFaceLocal(SnapFace f, ArrayView<Vector3> verts, int vertBase,
        float ox, float oy, float oz, float dx, float dy, float dz)
    {
        GpuFaceHit miss; miss.T = -1f; miss.Nx = 0f; miss.Ny = 0f; miss.Nz = 0f;

        Vector3 v0 = verts[vertBase + f.I0], v1 = verts[vertBase + f.I1], v2 = verts[vertBase + f.I2];
        float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
        float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

        float gnx = e1y * e2z - e1z * e2y;
        float gny = e1z * e2x - e1x * e2z;
        float gnz = e1x * e2y - e1y * e2x;
        if (gnx * dx + gny * dy + gnz * dz > 0f) return miss;

        float px = dy * e2z - dz * e2y;
        float py = dz * e2x - dx * e2z;
        float pz = dx * e2y - dy * e2x;
        float det = e1x * px + e1y * py + e1z * pz;
        if (XMath.Abs(det) < 1e-6f) return miss;

        float invDet = 1f / det;
        float tx = ox - v0.X, ty = oy - v0.Y, tz = oz - v0.Z;
        float u = (tx * px + ty * py + tz * pz) * invDet;
        if (u < 0f || u > 1f) return miss;

        float qx = ty * e1z - tz * e1y;
        float qy = tz * e1x - tx * e1z;
        float qz = tx * e1y - ty * e1x;
        float v = (dx * qx + dy * qy + dz * qz) * invDet;
        if (v < 0f || u + v > 1f) return miss;

        float dist = (e2x * qx + e2y * qy + e2z * qz) * invDet;
        if (dist < 0f) return miss;

        float w0 = 1f - u - v;
        GpuFaceHit h;
        h.T = dist;
        h.Nx = f.N0.X * w0 + f.N1.X * u + f.N2.X * v;
        h.Ny = f.N0.Y * w0 + f.N1.Y * u + f.N2.Y * v;
        h.Nz = f.N0.Z * w0 + f.N1.Z * u + f.N2.Z * v;
        return h;
    }

    private static GpuHit HitSphere(SnapSphere s, float ox, float oy, float oz, float dx, float dy, float dz)
    {
        GpuHit miss = default; miss.T = -1f;
        float lx = ox - s.Center.X, ly = oy - s.Center.Y, lz = oz - s.Center.Z;
        float a = dx * dx + dy * dy + dz * dz;          // ~1 (dir is unit)
        float b = 2f * (lx * dx + ly * dy + lz * dz);
        float c = lx * lx + ly * ly + lz * lz - s.Radius * s.Radius;
        float disc = b * b - 4f * a * c;
        if (disc < 0f) return miss;
        float sd = XMath.Sqrt(disc);
        float dist = (-b - sd) / (2f * a);
        if (dist < 0f) return miss;

        float hx = ox + dx * dist, hy = oy + dy * dist, hz = oz + dz * dist;
        float nx = hx - s.Center.X, ny = hy - s.Center.Y, nz = hz - s.Center.Z;
        float ninv = Rsqrt(nx * nx + ny * ny + nz * nz);

        GpuHit h;
        h.T = dist;
        h.Nx = nx * ninv; h.Ny = ny * ninv; h.Nz = nz * ninv;
        h.R = s.R; h.G = s.G; h.B = s.B; h.A = s.A;
        return h;
    }

    // Slab AABB test (matches BvhNode.RayHitsAabb); valid for a non-unit ray dir (local space).
    private static bool RayHitsAabb(float sx, float sy, float sz, float dx, float dy, float dz,
        float minx, float miny, float minz, float maxx, float maxy, float maxz)
    {
        float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
        if (XMath.Abs(dx) < 1e-9f) { if (sx < minx || sx > maxx) return false; }
        else { float inv = 1f / dx, t0 = (minx - sx) * inv, t1 = (maxx - sx) * inv; if (t0 > t1) { float tt = t0; t0 = t1; t1 = tt; } if (t0 > tmin) tmin = t0; if (t1 < tmax) tmax = t1; if (tmin > tmax) return false; }
        if (XMath.Abs(dy) < 1e-9f) { if (sy < miny || sy > maxy) return false; }
        else { float inv = 1f / dy, t0 = (miny - sy) * inv, t1 = (maxy - sy) * inv; if (t0 > t1) { float tt = t0; t0 = t1; t1 = tt; } if (t0 > tmin) tmin = t0; if (t1 < tmax) tmax = t1; if (tmin > tmax) return false; }
        if (XMath.Abs(dz) < 1e-9f) { if (sz < minz || sz > maxz) return false; }
        else { float inv = 1f / dz, t0 = (minz - sz) * inv, t1 = (maxz - sz) * inv; if (t0 > t1) { float tt = t0; t0 = t1; t1 = tt; } if (t0 > tmin) tmin = t0; if (t1 < tmax) tmax = t1; if (tmin > tmax) return false; }
        return tmax >= XMath.Max(tmin, 0f);
    }

    // Additive per-light shading matching Scene.ShadeHit.
    private static void Shade(GpuGlobals g, GpuHit hit, float hx, float hy, float hz,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres, ArrayView<SnapLight> lights,
        out float oR, out float oG, out float oB)
    {
        float lr = hit.R * g.Ambient, lg = hit.G * g.Ambient, lb = hit.B * g.Ambient;
        for (int k = 0; k < g.LightCount; k++)
        {
            SnapLight L = lights[k];
            float term = LightTerm(g, L, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);
            if (term <= 0f) continue;
            float tR = hit.R + L.ColorInfluence * (1f - hit.R);
            float tG = hit.G + L.ColorInfluence * (1f - hit.G);
            float tB = hit.B + L.ColorInfluence * (1f - hit.B);
            lr += tR * (L.Rgb.X * term) * g.Exposure;
            lg += tG * (L.Rgb.Y * term) * g.Exposure;
            lb += tB * (L.Rgb.Z * term) * g.Exposure;
        }
        oR = lr / (lr + 1f); oG = lg / (lg + 1f); oB = lb / (lb + 1f);
    }

    private static float LightTerm(GpuGlobals g, SnapLight L, GpuHit hit, float hx, float hy, float hz,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres)
    {
        if (L.Kind == 1)
        {
            float lx = -L.Direction.X, ly = -L.Direction.Y, lz = -L.Direction.Z;
            float ndl = hit.Nx * lx + hit.Ny * ly + hit.Nz * lz;
            if (ndl <= 0f) return 0f;
            float visD = g.EnableShadows == 1
                ? ShadowTransmittance(g, hx, hy, hz, hit, lx, ly, lz, 1e4f, verts, faces, nodes, triIdx, objects, spheres)
                : 1f;
            if (visD <= 0f) return 0f;
            return ndl * (L.Power / (DirRefDistSq + 1f)) * visD;
        }

        if (L.Kind == 3) return AreaTerm(g, L, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);

        float cone = 1f;
        if (L.Kind == 2)
        {
            cone = SpotConeMax(L, hx, hy, hz);
            if (cone <= 0f) return 0f;
        }
        return PointTermAt(g, L.Power, L.Position.X, L.Position.Y, L.Position.Z, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres) * cone;
    }

    private static float PointTermAt(GpuGlobals g, float power, float lx, float ly, float lz,
        GpuHit hit, float hx, float hy, float hz,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres)
    {
        float ox = lx - hx, oy = ly - hy, oz = lz - hz;
        float dist = XMath.Sqrt(ox * ox + oy * oy + oz * oz);
        if (dist < 1e-6f) return 0f;
        float ux = ox / dist, uy = oy / dist, uz = oz / dist;
        float ndl = hit.Nx * ux + hit.Ny * uy + hit.Nz * uz;
        if (ndl <= 0f) return 0f;
        float vis = g.EnableShadows == 1
            ? ShadowTransmittance(g, hx, hy, hz, hit, ux, uy, uz, dist, verts, faces, nodes, triIdx, objects, spheres)
            : 1f;
        if (vis <= 0f) return 0f;
        return ndl * (power / (dist * dist + 1f)) * vis;
    }

    private static float SpotConeMax(SnapLight L, float hx, float hy, float hz)
    {
        int beams = L.BeamCount < 1 ? 1 : L.BeamCount;
        bool nearVertical = XMath.Abs(L.Direction.Y) > 0.99f;
        float maxCone = 0f;
        for (int k = 0; k < beams; k++)
        {
            float ang = k * (Tau / beams);
            float ax, ay, az;
            if (nearVertical) RotX(L.Direction.X, L.Direction.Y, L.Direction.Z, ang, out ax, out ay, out az);
            else RotY(L.Direction.X, L.Direction.Y, L.Direction.Z, ang, out ax, out ay, out az);
            float ai = Rsqrt(ax * ax + ay * ay + az * az); ax *= ai; ay *= ai; az *= ai;
            float c = L.ConeShape == 0 ? CircleCone(L, ax, ay, az, hx, hy, hz) : ShapedCone(L, ax, ay, az, hx, hy, hz);
            if (c > maxCone) maxCone = c;
        }
        return maxCone;
    }

    private static float CircleCone(SnapLight L, float ax, float ay, float az, float hx, float hy, float hz)
    {
        float tpx = hx - L.Position.X, tpy = hy - L.Position.Y, tpz = hz - L.Position.Z;
        float d = XMath.Sqrt(tpx * tpx + tpy * tpy + tpz * tpz);
        if (d < 1e-6f) return 0f;
        float cosAng = (tpx * ax + tpy * ay + tpz * az) / d;
        if (cosAng <= L.ConeCosOuter) return 0f;
        float tt = L.ConeCosInner > L.ConeCosOuter
            ? XMath.Clamp((cosAng - L.ConeCosOuter) / (L.ConeCosInner - L.ConeCosOuter), 0f, 1f)
            : 1f;
        return tt * tt * (3f - 2f * tt);
    }

    private static float ShapedCone(SnapLight L, float ax, float ay, float az, float hx, float hy, float hz)
    {
        float dx = hx - L.Position.X, dy = hy - L.Position.Y, dz = hz - L.Position.Z;
        float axial = dx * ax + dy * ay + dz * az;
        if (axial <= 1e-6f) return 0f;

        float ux, uy, uz;
        if (XMath.Abs(ay) > 0.99f) { ux = 0f; uy = az; uz = -ay; }          // cross(a, (1,0,0))
        else { ux = -az; uy = 0f; uz = ax; }                                // cross(a, (0,1,0))
        float ui = Rsqrt(ux * ux + uy * uy + uz * uz); ux *= ui; uy *= ui; uz *= ui;
        float vx = ay * uz - az * uy, vy = az * ux - ax * uz, vz = ax * uy - ay * ux;

        float nu = (dx * ux + dy * uy + dz * uz) / axial;
        float nv = (dx * vx + dy * vy + dz * vz) / axial;
        float t = L.ConeTan;

        if (L.ConeShape == 1)
            return SmoothEdge(XMath.Max(XMath.Abs(nu), XMath.Abs(nv)), t);

        float p0 = -nv;
        float p1 = 0.8660254f * nu + 0.5f * nv;
        float p2 = -0.8660254f * nu + 0.5f * nv;
        return SmoothEdge(XMath.Max(p0, XMath.Max(p1, p2)), t * 0.5f);
    }

    private static float SmoothEdge(float metric, float boundary)
    {
        if (boundary <= 0f) return 0f;
        float inner = 0.8f * boundary;
        float s = XMath.Clamp((boundary - metric) / (boundary - inner), 0f, 1f);
        return s * s * (3f - 2f * s);
    }

    private static float AreaTerm(GpuGlobals g, SnapLight L, GpuHit hit, float hx, float hy, float hz,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres)
    {
        float nx = L.Direction.X, ny = L.Direction.Y, nz = L.Direction.Z;
        float upx, upy, upz;
        if (XMath.Abs(ny) > 0.99f) { upx = 1f; upy = 0f; upz = 0f; } else { upx = 0f; upy = 1f; upz = 0f; }
        float t1x = upy * nz - upz * ny, t1y = upz * nx - upx * nz, t1z = upx * ny - upy * nx;
        float t1i = Rsqrt(t1x * t1x + t1y * t1y + t1z * t1z); t1x *= t1i; t1y *= t1i; t1z *= t1i;
        float t2x = ny * t1z - nz * t1y, t2y = nz * t1x - nx * t1z, t2z = nx * t1y - ny * t1x;
        float t2i = Rsqrt(t2x * t2x + t2y * t2y + t2z * t2z); t2x *= t2i; t2y *= t2i; t2z *= t2i;

        float h = L.AreaSize;
        float du0, dv0, du1, dv1, du2, dv2, du3, dv3;
        if (L.AreaShape == 0)
        { du0 = h; dv0 = 0f; du1 = -h; dv1 = 0f; du2 = 0f; dv2 = h; du3 = 0f; dv3 = -h; }
        else if (L.AreaShape == 2)
        { du0 = 0f; dv0 = h; du1 = 0.8660254f * h; dv1 = -0.5f * h; du2 = -0.8660254f * h; dv2 = -0.5f * h; du3 = 0f; dv3 = 0f; }
        else
        { du0 = -h; dv0 = -h; du1 = h; dv1 = -h; du2 = -h; dv2 = h; du3 = h; dv3 = h; }

        float sum = 0f;
        sum += PointTermAt(g, L.Power, L.Position.X + t1x * du0 + t2x * dv0, L.Position.Y + t1y * du0 + t2y * dv0, L.Position.Z + t1z * du0 + t2z * dv0, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);
        sum += PointTermAt(g, L.Power, L.Position.X + t1x * du1 + t2x * dv1, L.Position.Y + t1y * du1 + t2y * dv1, L.Position.Z + t1z * du1 + t2z * dv1, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);
        sum += PointTermAt(g, L.Power, L.Position.X + t1x * du2 + t2x * dv2, L.Position.Y + t1y * du2 + t2y * dv2, L.Position.Z + t1z * du2 + t2z * dv2, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);
        sum += PointTermAt(g, L.Power, L.Position.X + t1x * du3 + t2x * dv3, L.Position.Y + t1y * du3 + t2y * dv3, L.Position.Z + t1z * du3 + t2z * dv3, hit, hx, hy, hz, verts, faces, nodes, triIdx, objects, spheres);
        return sum * 0.25f;
    }

    // Light transmittance along the (biased) segment toward the light: 1 = fully lit, 0 = fully blocked.
    // An OPAQUE shadow-caster (alpha >= 1) blocks completely; TRANSPARENT ones attenuate by their alpha,
    // one per object/sphere — the exact analog of the CPU Light.ShadowTransmittance, so a shadow cast
    // through glass is correspondingly lighter. Order-independent (a product + an opaque short-circuit).
    private static float ShadowTransmittance(GpuGlobals g, float hx, float hy, float hz, GpuHit hit,
        float lx, float ly, float lz, float maxDist,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres)
    {
        float ox = hx + hit.Nx * Bias, oy = hy + hit.Ny * Bias, oz = hz + hit.Nz * Bias;
        float t = 1f;

        for (int o = 0; o < g.ObjectCount; o++)
        {
            SnapObject ob = objects[o];
            if (ob.CastsShadow == 0) continue;
            if (!RayHitsAabb(ox, oy, oz, lx, ly, lz, ob.WorldMin.X, ob.WorldMin.Y, ob.WorldMin.Z, ob.WorldMax.X, ob.WorldMax.Y, ob.WorldMax.Z))
                continue;

            float rx = ox - ob.Position.X, ry = oy - ob.Position.Y, rz = oz - ob.Position.Z;
            float lsx = (ob.ColX.X * rx + ob.ColX.Y * ry + ob.ColX.Z * rz) * ob.InvScale;
            float lsy = (ob.ColY.X * rx + ob.ColY.Y * ry + ob.ColY.Z * rz) * ob.InvScale;
            float lsz = (ob.ColZ.X * rx + ob.ColZ.Y * ry + ob.ColZ.Z * rz) * ob.InvScale;
            float ldx = (ob.ColX.X * lx + ob.ColX.Y * ly + ob.ColX.Z * lz) * ob.InvScale;
            float ldy = (ob.ColY.X * lx + ob.ColY.Y * ly + ob.ColY.Z * lz) * ob.InvScale;
            float ldz = (ob.ColZ.X * lx + ob.ColZ.Y * ly + ob.ColZ.Z * lz) * ob.InvScale;

            if (AnyHit(ob, lsx, lsy, lsz, ldx, ldy, ldz, maxDist, verts, faces, nodes, triIdx))
            {
                if (ob.A >= 1f) return 0f;                 // opaque occluder -> full shadow
                t *= 1f - ob.A;
                if (t <= MinVis) return 0f;
            }
        }

        for (int s = 0; s < g.SphereCount; s++)
        {
            SnapSphere sp = spheres[s];
            if (sp.CastsShadow == 0) continue;
            GpuHit h = HitSphere(sp, ox, oy, oz, lx, ly, lz);
            if (h.T > 0f && h.T < maxDist)
            {
                if (sp.A >= 1f) return 0f;
                t *= 1f - sp.A;
                if (t <= MinVis) return 0f;
            }
        }
        return t;
    }

    // Stackless any-hit over one mesh's local BVH: returns on the first face hit with 0 < t < maxDist.
    private static bool AnyHit(SnapObject ob, float sx, float sy, float sz, float dx, float dy, float dz, float maxDist,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx)
    {
        int i = 0;
        while (i != -1)
        {
            SnapBvhNode nd = nodes[ob.NodeBase + i];
            if (RayHitsAabb(sx, sy, sz, dx, dy, dz, nd.Min.X, nd.Min.Y, nd.Min.Z, nd.Max.X, nd.Max.Y, nd.Max.Z))
            {
                if (nd.TriCount > 0)
                {
                    for (int k = 0; k < nd.TriCount; k++)
                    {
                        int faceLocal = triIdx[ob.TriIdxBase + nd.TriStart + k];
                        SnapFace f = faces[ob.FaceBase + faceLocal];
                        GpuFaceHit h = HitFaceLocal(f, verts, ob.VertBase, sx, sy, sz, dx, dy, dz);
                        if (h.T > 0f && h.T < maxDist) return true;
                    }
                    i = nd.Exit;
                }
                else i = i + 1;
            }
            else i = nd.Exit;
        }
        return false;
    }

    // Rotate (x,y,z) about world Y / world X by ang (matches Vector3.RotateY / RotateX).
    private static void RotY(float x, float y, float z, float ang, out float ox, out float oy, out float oz)
    {
        float c = XMath.Cos(ang), s = XMath.Sin(ang);
        ox = x * c + z * s; oy = y; oz = -x * s + z * c;
    }
    private static void RotX(float x, float y, float z, float ang, out float ox, out float oy, out float oz)
    {
        float c = XMath.Cos(ang), s = XMath.Sin(ang);
        ox = x; oy = y * c - z * s; oz = y * s + z * c;
    }

    private static float Rsqrt(float x) => x > 0f ? 1f / XMath.Sqrt(x) : 0f;
    private static int ToByte(float f) => (int)XMath.Clamp(f * 255f + 0.5f, 0f, 255f);

    public void Dispose()
    {
        _vertBuf?.Dispose(); _faceBuf?.Dispose(); _nodeBuf?.Dispose(); _triIdxBuf?.Dispose();
        _objBuf?.Dispose(); _sphereBuf?.Dispose(); _lightBuf?.Dispose();
        _brightBuf?.Dispose(); _colorBuf?.Dispose();
        _accelerator.Dispose();
        _context.Dispose();
    }
}
