using System.Collections.Generic;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace Nova3DVisualiser.Gpu;

// Per-frame scalar inputs packed into one blittable struct so the kernel takes few arguments. One
// dispatch renders ONE viewport: the kernel maps its index within the RW×RH region to the region pixel
// (RX0+rx, RY0+ry), the region-relative uv (using Aspect = the REGION's aspect), and the full-buffer
// output index (y*Width+x). Width/Height are the FULL buffer dims (the output stride); a full-screen
// viewport (RX0=RY0=0, RW=Width, RH=Height) reproduces the old single-view mapping exactly.
public struct GpuGlobals
{
    public int Width, Height;       // full buffer dims (output stride)
    public int RX0, RY0, RW, RH;    // this viewport's region within the buffer
    public float Aspect;            // the REGION's aspect
    public float CamX, CamY, CamZ;
    public float BxX, BxY, BxZ;     // camera basis (ray dir = Bx*Focal + By*uv.Y + Bz*uv.X)
    public float ByX, ByY, ByZ;
    public float BzX, BzY, BzZ;
    public float Focal;
    public float PixelCone;          // per-pixel ray-cone spread for texture mip selection (== ConsoleScreenAsync.PixelConeSpread)
    public float Ambient, Exposure;
    public int EnableShadows;
    public int UseBvh;               // 1 = traverse the two-level BVH; 0 = brute-force all triangles (F3)
    public int ObjectCount, SphereCount, LightCount;
}

// One viewport for the GPU: a screen region [X0,Y0,W,H] within the full buffer + the region's aspect +
// the camera reduction to render it from. RenderViews dispatches one kernel per GpuViewport into the
// SAME output buffers, so a split frame accumulates several regions before one copy-back.
public struct GpuViewport
{
    public int X0, Y0, W, H;
    public float Aspect;
    public Vector3 CamPos, BasisX, BasisY, BasisZ;
    public float Focal;
}

// A ray hit returned to the compositor: world-space surface normal + albedo/alpha.
public struct GpuHit
{
    public float T;                 // -1 = miss
    public float Nx, Ny, Nz;        // world-space surface normal (unit)
    public float R, G, B, A;        // surface albedo 0..1 + alpha (for transparency compositing)
}

// A mesh-BVH face hit in LOCAL space: distance (world units, by the local-ray invariant) + the
// interpolated LOCAL normal (rotated to world once the closest object is known) + the interpolated
// texture coordinate (barycentric, same weights as the normal).
public struct GpuFaceHit
{
    public float T;
    public float Nx, Ny, Nz;
    public float U, V;
    public int Group;   // face-group id of the hit triangle (for texture-face selection)
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
        ArrayView<SnapBvhNode>, ArrayView<int>, ArrayView<SnapTexture>, ArrayView<Rgba32>,
        ArrayView<SnapObject>, ArrayView<SnapSphere>,
        ArrayView<SnapLight>, ArrayView<float>, ArrayView<int>> _kernel;

    // Static geometry (uploaded only when the mesh set changes).
    private MemoryBuffer1D<Vector3, Stride1D.Dense>? _vertBuf;
    private MemoryBuffer1D<SnapFace, Stride1D.Dense>? _faceBuf;
    private MemoryBuffer1D<SnapBvhNode, Stride1D.Dense>? _nodeBuf;
    private MemoryBuffer1D<int, Stride1D.Dense>? _triIdxBuf;
    private MemoryBuffer1D<SnapTexture, Stride1D.Dense>? _texBuf;
    private MemoryBuffer1D<Rgba32, Stride1D.Dense>? _texPixelBuf;
    private int _uploadedGeomVersion = -1;
    private int _uploadedTextureVersion = -1;   // the texture pool is versioned separately (A3)

    // Upload counters (diagnostics / tests): how many times the static geometry+BVH vs the texture pool
    // were actually (re-)uploaded. A targeted texture swap bumps only the texture count.
    public int GeometryUploads { get; private set; }
    public int TextureUploads { get; private set; }

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
            ArrayView<SnapFace>, ArrayView<SnapBvhNode>, ArrayView<int>, ArrayView<SnapTexture>, ArrayView<Rgba32>,
            ArrayView<SnapObject>, ArrayView<SnapSphere>, ArrayView<SnapLight>, ArrayView<float>, ArrayView<int>>(RenderKernel);
    }

    /// <summary>
    /// Single full-screen view: renders the snapshot's render camera across the whole buffer. A thin wrapper
    /// over RenderViews (one full-screen viewport), so it stays BYTE-IDENTICAL to the old single-view path.
    /// </summary>
    public void Render(SceneSnapshot snap, int width, int height, float aspect,
                       float[] brightnessOut, Rgb24[] colorOut)
    {
        var view = new GpuViewport
        {
            X0 = 0, Y0 = 0, W = width, H = height, Aspect = aspect,
            CamPos = snap.CamPos, BasisX = snap.BasisX, BasisY = snap.BasisY, BasisZ = snap.BasisZ, Focal = snap.Focal,
        };
        RenderViews(snap, width, height, new[] { view }, brightnessOut, colorOut);
    }

    /// <summary>
    /// Renders each viewport (its own region + camera) into the caller's full-size brightness + color
    /// buffers (length = width*height). The static geometry + per-frame object/light data are uploaded ONCE
    /// (shared by all viewports); each viewport dispatches the kernel over its RW×RH region, writing only
    /// that region of the SAME device buffers, then one copy-back. The viewports must together cover every
    /// pixel each frame (as the single view and the 2-way split do) so nothing ghosts from a prior frame.
    /// </summary>
    public void RenderViews(SceneSnapshot snap, int width, int height, IReadOnlyList<GpuViewport> views,
                            float[] brightnessOut, Rgb24[] colorOut)
    {
        int pixels = width * height;
        EnsureBuffers(snap);
        EnsureOutputs(pixels);

        for (int v = 0; v < views.Count; v++)
        {
            GpuViewport vp = views[v];
            var g = new GpuGlobals
            {
                Width = width, Height = height,
                RX0 = vp.X0, RY0 = vp.Y0, RW = vp.W, RH = vp.H, Aspect = vp.Aspect,
                CamX = vp.CamPos.X, CamY = vp.CamPos.Y, CamZ = vp.CamPos.Z,
                BxX = vp.BasisX.X, BxY = vp.BasisX.Y, BxZ = vp.BasisX.Z,
                ByX = vp.BasisY.X, ByY = vp.BasisY.Y, ByZ = vp.BasisY.Z,
                BzX = vp.BasisZ.X, BzY = vp.BasisZ.Y, BzZ = vp.BasisZ.Z,
                Focal = vp.Focal,
                // Same ray-cone spread the CPU sampler uses (one shared formula) so mip levels agree CPU↔GPU.
                PixelCone = Nova3DVisualiser.Implementation.ConsoleScreenAsync.PixelConeSpread(vp.W, vp.H, vp.Aspect, vp.Focal),
                Ambient = snap.Ambient, Exposure = snap.Exposure, EnableShadows = snap.EnableShadows, UseBvh = snap.UseBvh,
                ObjectCount = snap.Objects.Length, SphereCount = snap.Spheres.Length, LightCount = snap.Lights.Length,
            };

            _kernel(vp.W * vp.H, g, _vertBuf!.View, _faceBuf!.View, _nodeBuf!.View, _triIdxBuf!.View,
                _texBuf!.View, _texPixelBuf!.View,
                _objBuf!.View, _sphereBuf!.View, _lightBuf!.View, _brightBuf!.View, _colorBuf!.View);
        }
        _accelerator.Synchronize();

        _brightBuf!.View.CopyToCPU(brightnessOut);
        _colorBuf!.View.CopyToCPU(_colorScratch);
        for (int i = 0; i < pixels; i++)
        {
            int c = _colorScratch[i];
            colorOut[i] = new Rgb24((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
        }
    }

    /// <summary>
    /// Forgets the cached static geometry so the next <see cref="Render"/> re-uploads it. Needed only
    /// when the SAME raytracer is reused for a DIFFERENT scene (e.g. the self-tests): GeometryVersion is a
    /// per-scene counter, so two scenes can share a value and would otherwise skip the re-upload, leaving
    /// the device holding one scene's geometry while the other scene's per-object offsets index into it
    /// (out of bounds). The live app uses one raytracer per scene with a monotonic version, so never needs this.
    /// </summary>
    public void ResetGeometryCache() { _uploadedGeomVersion = -1; _uploadedTextureVersion = -1; }

    private void EnsureBuffers(SceneSnapshot snap)
    {
        // Static mesh geometry + BVH: upload only when the mesh set changed (or first frame).
        bool geomChanged = snap.GeometryVersion != _uploadedGeomVersion || _vertBuf == null;
        if (geomChanged)
        {
            Upload(ref _vertBuf, snap.LocalVerts);
            Upload(ref _faceBuf, snap.Faces);
            Upload(ref _nodeBuf, snap.Nodes);
            Upload(ref _triIdxBuf, snap.TriIdx);
            _uploadedGeomVersion = snap.GeometryVersion;
            GeometryUploads++;
        }
        // Texture pool: re-upload when the geometry changed (its texture set/offsets may differ) OR only the
        // texture version bumped (a live swap) — decoupled from geometry/BVH so a swap is a pool-only upload.
        if (geomChanged || snap.TextureVersion != _uploadedTextureVersion || _texBuf == null)
        {
            Upload(ref _texBuf, snap.Textures);
            Upload(ref _texPixelBuf, snap.TexPixels);
            _uploadedTextureVersion = snap.TextureVersion;
            TextureUploads++;
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
    private const float MipMinCos = 0.25f;   // grazing-incidence clamp for mip footprint — MUST match Texture.MipMinCos
    private const float DirRefDistSq = 64f;
    private const float Tau = 6.2831853f;
    private const int MaxLayers = 32;   // transparency depth-peel cap (bounds the per-ray layer loop)

    private static void RenderKernel(Index1D index, GpuGlobals g,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres, ArrayView<SnapLight> lights,
        ArrayView<float> brightnessOut, ArrayView<int> colorOut)
    {
        // Index runs 0..RW*RH-1 WITHIN this viewport's region. Map it to the region pixel (rx,ry), then to
        // the full-buffer pixel (x,y), the region-relative uv (region aspect), and the output index below.
        // A full-screen viewport (RX0=RY0=0, RW=Width, RH=Height) reduces this to the old i/j mapping.
        int p = index.X;
        int rx = p % g.RW;
        int ry = p / g.RW;
        int x = g.RX0 + rx;
        int y = g.RY0 + ry;

        // uv exactly as ConsoleScreenAsync.RegionUV (region-relative, aspect on X, flipped Y).
        float uvx = ((float)rx / (g.RW - 1) * 2f - 1f) * g.Aspect;
        float uvy = -((float)ry / (g.RH - 1) * 2f - 1f);

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
            GpuHit hit = ClosestHit(g, g.CamX, g.CamY, g.CamZ, dx, dy, dz, tMin, verts, faces, nodes, triIdx, objects, spheres, textures, texPixels);
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
        int outIdx = y * g.Width + x;   // write to the full-buffer pixel (regions tile the buffer)
        brightnessOut[outIdx] = brightness;
        colorOut[outIdx] = (ToByte(outR) << 16) | (ToByte(outG) << 8) | ToByte(outB);
    }

    // Closest hit with T strictly greater than tMin, over every mesh (two-level BVH) + every sphere.
    // Visibility uses all objects (markers included); the closest mesh hit's local normal is rotated
    // to world here, once the winner is known.
    private static GpuHit ClosestHit(GpuGlobals g, float ox, float oy, float oz, float dx, float dy, float dz, float tMin,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces, ArrayView<SnapBvhNode> nodes, ArrayView<int> triIdx,
        ArrayView<SnapObject> objects, ArrayView<SnapSphere> spheres,
        ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels)
    {
        float bestT = -1f; int kind = 0;
        float bnx = 0f, bny = 0f, bnz = 0f, br = 0f, bg = 0f, bb = 0f, ba = 0f;
        int bTex = -1; float bFade = 0f, bu = 0f, bv = 0f;   // winning hit's texture index / paleness / UV
        float bScale = 1f; int bTexFace = -1, bGroup = 0;    // winning hit's UV tiling + texture-face gate + hit face-group
        int bFilter = 0;                                     // winning hit's texture filter (0 nearest, 1 bilinear)
        float blnx = 0f, blny = 0f, blnz = 0f;               // winning sphere's LOCAL surface dir (for the equirect UV)

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

            // F3: traverse the BVH, or brute-force every triangle when it's off. Both find the SAME closest
            // hit (a BVH only prunes; it doesn't change the result), so the rendered image is IDENTICAL.
            GpuFaceHit fh = g.UseBvh == 1
                ? TraverseClosest(ob, lsx, lsy, lsz, ldx, ldy, ldz, tMin, verts, faces, nodes, triIdx)
                : BruteClosest(ob, lsx, lsy, lsz, ldx, ldy, ldz, tMin, verts, faces);
            if (fh.T > tMin && (bestT < 0f || fh.T < bestT))
            {
                bestT = fh.T; kind = 1;
                // Local normal -> world: wn = ColX*n.x + ColY*n.y + ColZ*n.z.
                bnx = ob.ColX.X * fh.Nx + ob.ColY.X * fh.Ny + ob.ColZ.X * fh.Nz;
                bny = ob.ColX.Y * fh.Nx + ob.ColY.Y * fh.Ny + ob.ColZ.Y * fh.Nz;
                bnz = ob.ColX.Z * fh.Nx + ob.ColY.Z * fh.Ny + ob.ColZ.Z * fh.Nz;
                br = ob.R; bg = ob.G; bb = ob.B; ba = ob.A;
                bTex = ob.TextureIndex; bFade = ob.ColorFade; bu = fh.U; bv = fh.V;
                bScale = ob.TextureScale; bTexFace = ob.TextureFace; bGroup = fh.Group; bFilter = ob.TextureFilter;
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
                bTex = sp.TextureIndex; bFade = sp.ColorFade; bScale = sp.TextureScale; bTexFace = sp.TextureFace; bGroup = 0; bFilter = sp.TextureFilter;
                // Surface dir in the sphere's LOCAL frame = world normal * basis-transpose (== RotateInverse
                // on the CPU), so the equirect UV rotates with the object exactly like Sphere.GetRenderData.
                blnx = sp.ColX.X * hs.Nx + sp.ColX.Y * hs.Ny + sp.ColX.Z * hs.Nz;
                blny = sp.ColY.X * hs.Nx + sp.ColY.Y * hs.Ny + sp.ColY.Z * hs.Nz;
                blnz = sp.ColZ.X * hs.Nx + sp.ColZ.Y * hs.Ny + sp.ColZ.Z * hs.Nz;
            }
        }

        // A textured sphere's UV is the equirectangular map of its local surface dir (mesh UVs came from
        // face interpolation above). Then texture the winning hit — but only when the texture is present
        // AND this face is selected (TextureFace == ALL, or matches the hit's face-group; the sphere's
        // group is 0). The UV is scaled by TextureScale (tiling) before the exact nearest+wrap SampleTexel,
        // then ColorFade paling uses the SAME byte math as GameObject.ShadeTexel — CPU/GPU lockstep.
        if (kind == 2 && bTex >= 0)
            SphereUv(blnx, blny, blnz, out bu, out bv);

        if (bTex >= 0 && (bTexFace == -1 || bTexFace == bGroup))
        {
            int tr, tg, tb;
            float su = bu * bScale, sv = bv * bScale;
            // Mipmapped (2) picks a mip level from the ray-cone footprint and blends the two nearest levels
            // (trilinear) — MESH hits only (kind 1). A textured SPHERE (kind 2) is NOT mip-selected this
            // stage (its analytic equirect footprint is awkward) → bilinear fallback, mirroring the CPU
            // Sphere path. Bilinear (1) blends 4 texels; Nearest (0) picks one (bit-exact). Same gate/math as the CPU.
            if (bFilter == 2 && kind == 1)
            {
                float nlen = XMath.Sqrt(bnx * bnx + bny * bny + bnz * bnz);
                float incid = nlen > 1e-8f ? XMath.Abs(bnx * dx + bny * dy + bnz * dz) / nlen : 1f;   // D is unit
                float lod = MipLod(textures[bTex].Width, textures[bTex].LevelCount, bestT, g.PixelCone, incid, bScale);
                SampleTexelTrilinear(textures, texPixels, bTex, su, sv, lod, out tr, out tg, out tb);
            }
            else if (bFilter >= 1)   // Bilinear, or the sphere's Mipmapped→bilinear fallback
                SampleTexelBilinear(textures, texPixels, bTex, su, sv, out tr, out tg, out tb);
            else
                SampleTexel(textures, texPixels, bTex, su, sv, out tr, out tg, out tb);
            if (bFade <= 0f) { br = tr / 255f; bg = tg / 255f; bb = tb / 255f; }
            else
            {
                float f = bFade >= 1f ? 1f : bFade;
                br = ((int)(tr + (255 - tr) * f + 0.5f)) / 255f;
                bg = ((int)(tg + (255 - tg) * f + 0.5f)) / 255f;
                bb = ((int)(tb + (255 - tb) * f + 0.5f)) / 255f;
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
        GpuFaceHit best; best.T = -1f; best.Nx = 0f; best.Ny = 0f; best.Nz = 0f; best.U = 0f; best.V = 0f; best.Group = 0;
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

    // BVH-OFF closest hit (F3): test EVERY triangle of the mesh (no acceleration). Returns the same closest
    // face hit (t > tMin) as TraverseClosest — a BVH only prunes which triangles are tested, not the result —
    // so the image is identical; only the work (and thus FPS) differs. Faces are contiguous from FaceBase.
    private static GpuFaceHit BruteClosest(SnapObject ob, float sx, float sy, float sz, float dx, float dy, float dz, float tMin,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces)
    {
        GpuFaceHit best; best.T = -1f; best.Nx = 0f; best.Ny = 0f; best.Nz = 0f; best.U = 0f; best.V = 0f; best.Group = 0;
        for (int k = 0; k < ob.FaceCount; k++)
        {
            GpuFaceHit h = HitFaceLocal(faces[ob.FaceBase + k], verts, ob.VertBase, sx, sy, sz, dx, dy, dz);
            if (h.T > tMin && (best.T < 0f || h.T < best.T)) best = h;
        }
        return best;
    }

    // Möller–Trumbore in LOCAL space (matches Triangle.GetRenderData): back-face cull, barycentric
    // interpolation of the local vertex normals (left un-normalized; the caller rotates then normalizes,
    // which commutes). t is the world distance by the local-ray invariant (local dir is not unit).
    private static GpuFaceHit HitFaceLocal(SnapFace f, ArrayView<Vector3> verts, int vertBase,
        float ox, float oy, float oz, float dx, float dy, float dz)
    {
        GpuFaceHit miss; miss.T = -1f; miss.Nx = 0f; miss.Ny = 0f; miss.Nz = 0f; miss.U = 0f; miss.V = 0f; miss.Group = 0;

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
        // Texture coordinate: same barycentric blend as the normal (matches Triangle.GetRenderData).
        h.U = f.UV0.X * w0 + f.UV1.X * u + f.UV2.X * v;
        h.V = f.UV0.Y * w0 + f.UV1.Y * u + f.UV2.Y * v;
        h.Group = f.Group;
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

            bool blocked = g.UseBvh == 1
                ? AnyHit(ob, lsx, lsy, lsz, ldx, ldy, ldz, maxDist, verts, faces, nodes, triIdx)
                : BruteAnyHit(ob, lsx, lsy, lsz, ldx, ldy, ldz, maxDist, verts, faces);
            if (blocked)
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

    // BVH-OFF any-hit (F3): test EVERY triangle for a shadow-ray occlusion (0 < t < maxDist). Same result as
    // AnyHit (the BVH only prunes), so shadows are identical — only the work differs.
    private static bool BruteAnyHit(SnapObject ob, float sx, float sy, float sz, float dx, float dy, float dz, float maxDist,
        ArrayView<Vector3> verts, ArrayView<SnapFace> faces)
    {
        for (int k = 0; k < ob.FaceCount; k++)
        {
            GpuFaceHit h = HitFaceLocal(faces[ob.FaceBase + k], verts, ob.VertBase, sx, sy, sz, dx, dy, dz);
            if (h.T > 0f && h.T < maxDist) return true;
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

    // Sample one texture's texel — EXACTLY replicating the CPU Texture.Sample: nearest-neighbour with
    // WRAP (repeat) addressing and the identical integer texel arithmetic, so CPU and GPU fetch the SAME
    // texel bit-for-bit. Returns the raw RGB bytes (as ints); the caller applies ColorFade + alpha.
    private static void SampleTexel(ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels, int texIndex,
        float u, float v, out int r, out int gg, out int b)
    {
        SnapTexture t = textures[texIndex];
        int x = WrapIndex(u, t.Width);
        int y = WrapIndex(v, t.Height);
        Rgba32 px = texPixels[t.Offset + y * t.Width + x];
        r = px.R; gg = px.G; b = px.B;
    }

    // Bilinear-filtered texel — an EXACT replica of Texture.SampleBilinear (4-texel blend, WRAP addressing,
    // identical lerp/round math). Only float hardware rounding (XMath vs MathF) may differ → the thin
    // tolerated band. Returns the blended RGB bytes (as ints); the caller applies ColorFade + alpha.
    private static void SampleTexelBilinear(ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels, int texIndex,
        float u, float v, out int r, out int gg, out int b)
    {
        SnapTexture t = textures[texIndex];
        float fx = u * t.Width, fy = v * t.Height;
        float flx = XMath.Floor(fx), fly = XMath.Floor(fy);
        float fu = fx - flx, fv = fy - fly;

        int x0 = (int)flx % t.Width;  if (x0 < 0) x0 += t.Width;
        int y0 = (int)fly % t.Height; if (y0 < 0) y0 += t.Height;
        int x1 = x0 + 1; if (x1 >= t.Width)  x1 -= t.Width;
        int y1 = y0 + 1; if (y1 >= t.Height) y1 -= t.Height;

        Rgba32 p00 = texPixels[t.Offset + y0 * t.Width + x0], p10 = texPixels[t.Offset + y0 * t.Width + x1];
        Rgba32 p01 = texPixels[t.Offset + y1 * t.Width + x0], p11 = texPixels[t.Offset + y1 * t.Width + x1];

        r  = BLerp(p00.R, p10.R, p01.R, p11.R, fu, fv);
        gg = BLerp(p00.G, p10.G, p01.G, p11.G, fu, fv);
        b  = BLerp(p00.B, p10.B, p01.B, p11.B, fu, fv);
    }

    // Bilinear blend of 4 texel channel bytes — identical expression to Texture.BLerp (the parity rule).
    private static int BLerp(byte c00, byte c10, byte c01, byte c11, float fu, float fv)
    {
        float top = c00 + (c10 - c00) * fu;
        float bot = c01 + (c11 - c01) * fu;
        float val = top + (bot - top) * fv;
        return (int)(val + 0.5f);
    }

    // One mip level's dimension along an axis: max(1, baseDim >> level) — matches Texture.MipDim.
    private static int MipDimI(int baseDim, int level)
    {
        int d = baseDim >> level;
        return d < 1 ? 1 : d;
    }

    // Pixel-pool offset of a texture's mip level L = Offset + Σ_{k<L} dim(k) — the chain is stored
    // contiguously from Offset (level 0 first), each level's block being MipDim(W,k)*MipDim(H,k).
    private static int MipLevelOffset(SnapTexture t, int level)
    {
        int off = t.Offset, w = t.Width, h = t.Height;
        for (int l = 0; l < level; l++)
        {
            off += w * h;
            w >>= 1; if (w < 1) w = 1;
            h >>= 1; if (h < 1) h = 1;
        }
        return off;
    }

    // Fractional mip LOD from a ray-cone footprint — an EXACT replica of Texture.MipLod (only XMath vs MathF
    // Log2/Abs rounding may differ → the thin tolerated band). See Texture.MipLod for the documented formula.
    private static float MipLod(int width, int levelCount, float distance, float cone, float incidenceCos, float texScale)
    {
        float cos = XMath.Abs(incidenceCos);
        if (cos < MipMinCos) cos = MipMinCos;
        float footprint = distance * cone / cos;
        float texels = footprint * texScale * width;
        if (texels < 1f) texels = 1f;
        float lod = XMath.Log2(texels);
        if (lod < 0f) lod = 0f;
        float maxLod = levelCount - 1;
        if (lod > maxLod) lod = maxLod;
        return lod;
    }

    // Bilinear sample of one mip LEVEL (its own w/h + block offset) — an EXACT replica of
    // Texture.SampleBilinearLevel (same WRAP + BLerp). Level 0 equals SampleTexelBilinear.
    private static void SampleBilinearLevel(ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels, int texIndex,
        int level, float u, float v, out int r, out int gg, out int b)
    {
        SnapTexture t = textures[texIndex];
        int w = MipDimI(t.Width, level), h = MipDimI(t.Height, level);
        int off = MipLevelOffset(t, level);

        float fx = u * w, fy = v * h;
        float flx = XMath.Floor(fx), fly = XMath.Floor(fy);
        float fu = fx - flx, fv = fy - fly;

        int x0 = (int)flx % w; if (x0 < 0) x0 += w;
        int y0 = (int)fly % h; if (y0 < 0) y0 += h;
        int x1 = x0 + 1; if (x1 >= w) x1 -= w;
        int y1 = y0 + 1; if (y1 >= h) y1 -= h;

        Rgba32 p00 = texPixels[off + y0 * w + x0], p10 = texPixels[off + y0 * w + x1];
        Rgba32 p01 = texPixels[off + y1 * w + x0], p11 = texPixels[off + y1 * w + x1];

        r  = BLerp(p00.R, p10.R, p01.R, p11.R, fu, fv);
        gg = BLerp(p00.G, p10.G, p01.G, p11.G, fu, fv);
        b  = BLerp(p00.B, p10.B, p01.B, p11.B, fu, fv);
    }

    // Trilinear texel — bilinear within the two nearest levels, blended by the fractional lod. An EXACT
    // replica of Texture.SampleTrilinear (the level-selection + blend float math is the tolerated band).
    private static void SampleTexelTrilinear(ArrayView<SnapTexture> textures, ArrayView<Rgba32> texPixels, int texIndex,
        float u, float v, float lod, out int r, out int gg, out int b)
    {
        int maxL = textures[texIndex].LevelCount - 1;
        if (lod < 0f) lod = 0f;
        if (lod > maxL) lod = maxL;
        int l0 = (int)XMath.Floor(lod);
        if (l0 >= maxL) { SampleBilinearLevel(textures, texPixels, texIndex, maxL, u, v, out r, out gg, out b); return; }

        int l1 = l0 + 1;
        float f = lod - l0;
        SampleBilinearLevel(textures, texPixels, texIndex, l0, u, v, out int r0, out int g0, out int b0);
        SampleBilinearLevel(textures, texPixels, texIndex, l1, u, v, out int r1, out int g1, out int b1);
        r  = (int)(r0 + (r1 - r0) * f + 0.5f);
        gg = (int)(g0 + (g1 - g0) * f + 0.5f);
        b  = (int)(b0 + (b1 - b0) * f + 0.5f);
    }

    // Equirectangular (lat/long) UV from a UNIT local surface direction — an EXACT replica of
    // Sphere.EquirectangularUv, except atan2/asin come from XMath and may round slightly differently
    // (the tolerated seam/pole band). Tau = 2π; Tau*0.5f = π. u wraps at the -X seam; v spans the poles.
    private static void SphereUv(float dx, float dy, float dz, out float u, out float v)
    {
        u = 0.5f + XMath.Atan2(dz, dx) / Tau;
        v = 0.5f - XMath.Asin(XMath.Clamp(dy, -1f, 1f)) / (Tau * 0.5f);
    }

    // floor(t*n) reduced into [0,n) with a true modulo — matches Texture.WrapIndex exactly.
    private static int WrapIndex(float t, int n)
    {
        int i = (int)XMath.Floor(t * n);
        i %= n;
        if (i < 0) i += n;
        return i;
    }

    private static float Rsqrt(float x) => x > 0f ? 1f / XMath.Sqrt(x) : 0f;
    private static int ToByte(float f) => (int)XMath.Clamp(f * 255f + 0.5f, 0f, 255f);

    public void Dispose()
    {
        _vertBuf?.Dispose(); _faceBuf?.Dispose(); _nodeBuf?.Dispose(); _triIdxBuf?.Dispose();
        _texBuf?.Dispose(); _texPixelBuf?.Dispose();
        _objBuf?.Dispose(); _sphereBuf?.Dispose(); _lightBuf?.Dispose();
        _brightBuf?.Dispose(); _colorBuf?.Dispose();
        _accelerator.Dispose();
        _context.Dispose();
    }
}
