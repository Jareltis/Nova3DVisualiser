using System;
using System.Collections.Generic;
using Nova3DVisualiser;
using Nova3DVisualiser.Logging;

namespace SampleGame.Physics;

// ---- Plan C2-1: the geometric foundation for dynamic triangle-mesh rigid bodies. ----
// A 3-D convex hull (quickhull) over a point cloud, plus closed-polyhedron mass properties and a support
// function for GJK (C2-2). PURE geometry — no RigidBody/ImpulseWorld/scene coupling (that's C2-3). Matches the
// codebase's custom Vector3: dot(a,b) == the `*` operator (Vector3*Vector3 → float), cross via Vector3.Cross,
// `* float` scales. No third-party dependencies.
//
// Robustness target: game meshes of ~8–5,000 input verts. One consistent epsilon strategy (an absolute
// tolerance scaled by the cloud extent); degenerate input (coincident/collinear/coplanar) returns null rather
// than crashing or looping; the quickhull loop is defensively capped.
public sealed class ConvexHull
{
    public Vector3[] Vertices = Array.Empty<Vector3>();   // the hull's UNIQUE vertices (a subset of the welded input)
    public int[] Triangles = Array.Empty<int>();          // flattened, 3 indices per face, OUTWARD wound (CCW seen from outside)

    // Per-face outward unit normal + plane offset (Normal·x = Offset), precomputed in Build for Contains() and
    // to guarantee the outward-winding invariant the mass properties rely on.
    private Vector3[] _normals = Array.Empty<Vector3>();
    private float[] _offsets = Array.Empty<float>();

    // ---- polygonal faces (C2-3a): coplanar adjacent triangles merged into one convex face ----
    /// <summary>A merged polygonal face: its boundary vertex indices in outward-CCW order plus the outward
    /// unit normal. A cube hull yields 6 quad faces (not 12 triangles). Used by the hull contact manifold
    /// clipping (C2-3a) and reused by hull-vs-hull (C2-3b).</summary>
    public readonly struct HullFace
    {
        public readonly int[] Loop;      // boundary vertex indices (into Vertices), outward-CCW order
        public readonly Vector3 Normal;  // outward unit normal
        public HullFace(int[] loop, Vector3 normal) { Loop = loop; Normal = normal; }
    }

    private HullFace[]? _faces;

    /// <summary>The hull's polygonal faces (coplanar adjacent triangles merged into a single convex CCW vertex
    /// loop). Built lazily from the final triangulation + per-face outward normals; translation-invariant, so a
    /// COM re-centre (<see cref="Translate"/>) keeps them valid.</summary>
    public HullFace[] Faces => _faces ??= BuildFaces();

    // ---- quickhull working face ----
    private sealed class Face
    {
        public int A, B, C;                  // vertex indices into the welded point array
        public Vector3 N;                    // outward unit normal
        public float Off;                    // plane offset: N·pts[A]
        public readonly List<int> Outside = new();   // welded-point indices strictly beyond this face's plane
        public bool Visible;                 // scratch: seen from the current eye point this iteration
    }

    /// <summary>
    /// Builds the convex hull of a point cloud via quickhull. Duplicates are welded first (mesh vertex arrays
    /// repeat corners per-face), so a cube's 8 corners collapse to 8 unique points. Returns null on degenerate
    /// input (fewer than 4 non-coplanar points) — the caller falls back; it never throws or hangs.
    /// </summary>
    public static ConvexHull? Build(Vector3[] points)
    {
        if (points == null || points.Length < 4) return null;

        // Cloud extent → a single absolute epsilon used consistently for welding + plane tests. Scaling by the
        // extent keeps near-coplanar meshes (a cube!) producing their exact corner hull with no sliver faces.
        Vector3 lo = points[0], hi = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            var p = points[i];
            lo = new Vector3(MathF.Min(lo.X, p.X), MathF.Min(lo.Y, p.Y), MathF.Min(lo.Z, p.Z));
            hi = new Vector3(MathF.Max(hi.X, p.X), MathF.Max(hi.Y, p.Y), MathF.Max(hi.Z, p.Z));
        }
        Vector3 size = hi - lo;
        float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (extent < 1e-9f) return null;   // all points coincident
        float weldEps = MathF.Max(1e-6f, 1e-5f * extent);
        float eps = MathF.Max(1e-7f, 1e-5f * extent);   // "strictly beyond a plane" tolerance

        // 1) WELD duplicates onto a coarse grid (exact repeats collapse; near-coincident within weldEps merge).
        var cell = new Dictionary<(long, long, long), int>();
        var pts = new List<Vector3>();
        foreach (var p in points)
        {
            var key = ((long)MathF.Round(p.X / weldEps), (long)MathF.Round(p.Y / weldEps), (long)MathF.Round(p.Z / weldEps));
            if (!cell.ContainsKey(key)) { cell[key] = pts.Count; pts.Add(p); }
        }
        if (pts.Count < 4) return null;
        int n = pts.Count;

        // 2) INITIAL SIMPLEX. The base pair = the two most-separated axis extremes; then the point farthest from
        //    that line, then the point farthest from that triangle's plane. Any step failing the eps test means
        //    the cloud is collinear/coplanar → degenerate → null.
        int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        for (int i = 1; i < n; i++)
        {
            if (pts[i].X < pts[minX].X) minX = i; if (pts[i].X > pts[maxX].X) maxX = i;
            if (pts[i].Y < pts[minY].Y) minY = i; if (pts[i].Y > pts[maxY].Y) maxY = i;
            if (pts[i].Z < pts[minZ].Z) minZ = i; if (pts[i].Z > pts[maxZ].Z) maxZ = i;
        }
        int[] cand = { minX, maxX, minY, maxY, minZ, maxZ };
        int s0 = 0, s1 = 0; float bestD2 = -1f;
        for (int i = 0; i < cand.Length; i++)
            for (int j = i + 1; j < cand.Length; j++)
            {
                Vector3 d = pts[cand[i]] - pts[cand[j]]; float d2 = d * d;
                if (d2 > bestD2) { bestD2 = d2; s0 = cand[i]; s1 = cand[j]; }
            }
        if (bestD2 < eps * eps) return null;   // all extremes coincide → coincident cloud

        Vector3 lineDir = (pts[s1] - pts[s0]).Norm();
        int s2 = -1; float bestLine = eps;
        for (int i = 0; i < n; i++)
        {
            float dl = Vector3.Cross(pts[i] - pts[s0], lineDir).Length();   // distance from the base line
            if (dl > bestLine) { bestLine = dl; s2 = i; }
        }
        if (s2 < 0) return null;   // collinear

        Vector3 planeN = Vector3.Cross(pts[s1] - pts[s0], pts[s2] - pts[s0]).Norm();
        int s3 = -1; float bestPlane = eps;
        for (int i = 0; i < n; i++)
        {
            float dp = MathF.Abs((pts[i] - pts[s0]) * planeN);   // distance from the base plane
            if (dp > bestPlane) { bestPlane = dp; s3 = i; }
        }
        if (s3 < 0) return null;   // coplanar

        // A point strictly INSIDE the hull for all time: the simplex centroid. The hull only grows, so this
        // stays interior — orienting every face away from it gives an outward normal with no half-edge bookkeeping.
        Vector3 interior = (pts[s0] + pts[s1] + pts[s2] + pts[s3]) * 0.25f;

        // Outward-oriented triangle from 3 welded indices (normal points away from `interior`). Null on a
        // degenerate (zero-area) triangle — the caller aborts (should never happen on sane input).
        Face? MakeFace(int ia, int ib, int ic)
        {
            Vector3 a = pts[ia], b = pts[ib], c = pts[ic];
            Vector3 nn = Vector3.Cross(b - a, c - a);
            float len = nn.Length();
            if (len < 1e-20f) return null;
            nn = nn / len;
            if (nn * (a - interior) < 0f) { (ib, ic) = (ic, ib); nn = -nn; a = pts[ia]; }
            return new Face { A = ia, B = ib, C = ic, N = nn, Off = nn * a };
        }

        var faces = new List<Face>(4);
        foreach (var (ia, ib, ic) in new[] { (s0, s1, s2), (s0, s1, s3), (s0, s2, s3), (s1, s2, s3) })
        {
            var f = MakeFace(ia, ib, ic);
            if (f == null) return null;
            faces.Add(f);
        }

        // Seed each face's outside set: every non-simplex point goes to the face it is MOST beyond (interior
        // points, beyond none, are discarded).
        var simplex = new HashSet<int> { s0, s1, s2, s3 };
        for (int p = 0; p < n; p++)
        {
            if (simplex.Contains(p)) continue;
            Face? best = null; float bestDist = eps;
            foreach (var f in faces) { float d = f.N * pts[p] - f.Off; if (d > bestDist) { bestDist = d; best = f; } }
            best?.Outside.Add(p);
        }

        // 3) ITERATE. Each pass promotes a face's farthest outside point to a hull vertex, carving away the faces
        //    it sees and fanning new ones across the horizon. Terminates in ≤ n passes (one new hull vertex each);
        //    the cap is a defensive backstop that should never fire on sane input.
        int maxIter = 4 * n + 16;
        int iter = 0;
        while (true)
        {
            if (++iter > maxIter) { Logger.Warning($"ConvexHull.Build exceeded {maxIter} iterations ({n} points); aborting."); return null; }

            Face? seed = null;
            foreach (var f in faces) if (f.Outside.Count > 0) { seed = f; break; }
            if (seed == null) break;   // no outside points remain → the hull is complete

            int eye = -1; float bestEye = eps;
            foreach (int p in seed.Outside) { float d = seed.N * pts[p] - seed.Off; if (d > bestEye) { bestEye = d; eye = p; } }
            if (eye < 0) { seed.Outside.Clear(); continue; }   // numerical: nothing truly beyond

            // Faces the eye can see (its plane distance is positive) — carved away and re-fanned this pass.
            var visible = new List<Face>();
            foreach (var f in faces) { f.Visible = (f.N * pts[eye] - f.Off) > eps; if (f.Visible) visible.Add(f); }

            // HORIZON = boundary edges of the visible region: a directed edge whose twin belongs to a
            // NON-visible face (an outward-wound closed manifold makes a shared edge (u,v)/(v,u)). Orphans =
            // the visible faces' outside points, for re-distribution onto the new fan.
            var edges = new HashSet<(int, int)>();
            foreach (var f in visible) { edges.Add((f.A, f.B)); edges.Add((f.B, f.C)); edges.Add((f.C, f.A)); }
            var horizon = new List<(int, int)>();
            var orphans = new List<int>();
            foreach (var f in visible)
            {
                if (!edges.Contains((f.B, f.A))) horizon.Add((f.A, f.B));
                if (!edges.Contains((f.C, f.B))) horizon.Add((f.B, f.C));
                if (!edges.Contains((f.A, f.C))) horizon.Add((f.C, f.A));
                foreach (int p in f.Outside) if (p != eye) orphans.Add(p);
            }
            if (horizon.Count == 0) { Logger.Warning("ConvexHull.Build found an empty horizon; aborting."); return null; }

            faces.RemoveAll(f => f.Visible);

            var fresh = new List<Face>(horizon.Count);
            foreach (var (u, v) in horizon)
            {
                var nf = MakeFace(eye, u, v);
                if (nf == null) { Logger.Warning("ConvexHull.Build produced a degenerate horizon face; aborting."); return null; }
                faces.Add(nf); fresh.Add(nf);
            }

            // Re-distribute orphan points onto the NEW faces (any now inside the grown hull are discarded).
            foreach (int p in orphans)
            {
                Face? best = null; float bestDist = eps;
                foreach (var nf in fresh) { float d = nf.N * pts[p] - nf.Off; if (d > bestDist) { bestDist = d; best = nf; } }
                best?.Outside.Add(p);
            }
        }

        if (faces.Count < 4) return null;

        // 4) COMPACT the used points into Vertices, re-index Triangles.
        var remap = new Dictionary<int, int>();
        var verts = new List<Vector3>();
        var tris = new List<int>(faces.Count * 3);
        int Map(int idx)
        {
            if (!remap.TryGetValue(idx, out int ni)) { ni = verts.Count; remap[idx] = ni; verts.Add(pts[idx]); }
            return ni;
        }
        foreach (var f in faces) { tris.Add(Map(f.A)); tris.Add(Map(f.B)); tris.Add(Map(f.C)); }

        var hull = new ConvexHull { Vertices = verts.ToArray(), Triangles = tris.ToArray() };
        hull.ComputeFacePlanes();   // outward normals + offsets (also enforces the outward-winding invariant)
        return hull;
    }

    // Computes per-face outward normals + plane offsets from the final Vertices, flipping any face whose normal
    // does not point away from the hull centroid (a belt-and-suspenders guarantee of the outward invariant that
    // Contains + the signed-volume mass properties rely on).
    private void ComputeFacePlanes()
    {
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < Vertices.Length; i++) centroid += Vertices[i];
        centroid = centroid / (float)Vertices.Length;

        int fc = Triangles.Length / 3;
        _normals = new Vector3[fc];
        _offsets = new float[fc];
        for (int f = 0; f < fc; f++)
        {
            int i0 = Triangles[f * 3], i1 = Triangles[f * 3 + 1], i2 = Triangles[f * 3 + 2];
            Vector3 a = Vertices[i0], b = Vertices[i1], c = Vertices[i2];
            Vector3 nn = Vector3.Cross(b - a, c - a);
            float len = nn.Length();
            nn = len > 1e-20f ? nn / len : new Vector3(0f, 1f, 0f);
            Vector3 fc3 = (a + b + c) / 3f;
            if (nn * (fc3 - centroid) < 0f)   // inward — flip the winding + normal
            {
                Triangles[f * 3 + 1] = i2; Triangles[f * 3 + 2] = i1;
                nn = -nn;
            }
            _normals[f] = nn;
            _offsets[f] = nn * a;
        }
    }

    // Merge coplanar adjacent triangles into convex polygonal faces (C2-3a). Two triangles sharing an edge whose
    // outward normals are near-parallel (dot > 1−eps) belong to the same face (union-find). Each merged group's
    // boundary is the set of directed edges whose REVERSE is absent from the group (an interior edge appears once
    // each way); those boundary edges chain into a single outward-CCW vertex loop (the group is convex → a simple
    // cycle). Robust to a general convex hull: a lone tri face stays a single triangle.
    private HullFace[] BuildFaces()
    {
        int fc = Triangles.Length / 3;
        if (fc == 0) return Array.Empty<HullFace>();

        int[] parent = new int[fc];
        for (int i = 0; i < fc; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        const float coplanarDot = 1f - 1e-4f;
        var edgeToTri = new Dictionary<(int, int), int>();   // undirected edge -> the first triangle that used it
        for (int t = 0; t < fc; t++)
        {
            int i0 = Triangles[t * 3], i1 = Triangles[t * 3 + 1], i2 = Triangles[t * 3 + 2];
            foreach (var (a, b) in new[] { (i0, i1), (i1, i2), (i2, i0) })
            {
                var key = a < b ? (a, b) : (b, a);
                if (edgeToTri.TryGetValue(key, out int other))
                {
                    if (_normals[t] * _normals[other] > coplanarDot) Union(t, other);
                }
                else edgeToTri[key] = t;
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < fc; t++) { int r = Find(t); if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<int>(); l.Add(t); }

        var faces = new List<HullFace>(groups.Count);
        foreach (var g in groups.Values)
        {
            var dir = new HashSet<(int, int)>();
            foreach (int t in g)
            {
                int i0 = Triangles[t * 3], i1 = Triangles[t * 3 + 1], i2 = Triangles[t * 3 + 2];
                dir.Add((i0, i1)); dir.Add((i1, i2)); dir.Add((i2, i0));
            }
            var next = new Dictionary<int, int>();          // boundary edge u -> v (reverse absent from the group)
            foreach (var (u, v) in dir) if (!dir.Contains((v, u))) next[u] = v;
            if (next.Count < 3) continue;                   // degenerate group

            int start = -1; foreach (int k in next.Keys) { start = k; break; }
            var loop = new List<int>(next.Count);
            int cur = start;
            for (int guard = 0; guard <= next.Count; guard++)
            {
                loop.Add(cur);
                if (!next.TryGetValue(cur, out int nx)) break;
                cur = nx;
                if (cur == start) break;
            }
            if (loop.Count < 3) continue;

            Vector3 nrm = Vector3.Zero;
            foreach (int t in g) nrm += _normals[t];
            float nl = nrm.Length();
            nrm = nl > 1e-12f ? nrm / nl : _normals[g[0]];
            faces.Add(new HullFace(loop.ToArray(), nrm));
        }
        return faces.ToArray();
    }

    /// <summary>Translate every vertex by `delta` (used to re-centre a hull on its COM so a rigid body's Position
    /// equals its COM). Recomputes the per-face plane offsets; the outward normals + polygonal-face vertex loops
    /// are translation-invariant, so <see cref="Faces"/> stays valid.</summary>
    public void Translate(Vector3 delta)
    {
        for (int i = 0; i < Vertices.Length; i++) Vertices[i] += delta;
        for (int f = 0; f < _offsets.Length; f++) _offsets[f] = _normals[f] * Vertices[Triangles[f * 3]];
    }

    /// <summary>Support point for GJK: the hull vertex farthest along `dir` (argmax of v·dir). Linear scan —
    /// hull vertex counts are small; hill-climbing over face adjacency is a possible later optimisation.</summary>
    public Vector3 Support(Vector3 dir)
    {
        if (Vertices.Length == 0) return Vector3.Zero;
        int best = 0; float bestDot = Vertices[0] * dir;
        for (int i = 1; i < Vertices.Length; i++)
        {
            float d = Vertices[i] * dir;
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return Vertices[best];
    }

    /// <summary>True when `p` is inside-or-on the hull: within `eps` of every outward face plane (signed
    /// distance ≤ eps for all faces). `eps` is absolute (the caller scales to its own coordinates if needed).</summary>
    public bool Contains(Vector3 p, float eps = 1e-4f)
    {
        for (int f = 0; f < _normals.Length; f++)
            if (_normals[f] * p - _offsets[f] > eps) return false;
        return true;
    }

    /// <summary>Signed volume of the closed hull (Σ over faces of the origin-apex tetrahedron volumes). Positive
    /// for the outward winding Build guarantees. Handy for tests + as the density divisor.</summary>
    public float Volume()
    {
        float v6 = 0f;
        int fc = Triangles.Length / 3;
        for (int f = 0; f < fc; f++)
        {
            Vector3 a = Vertices[Triangles[f * 3]], b = Vertices[Triangles[f * 3 + 1]], c = Vertices[Triangles[f * 3 + 2]];
            v6 += a * Vector3.Cross(b, c);
        }
        return v6 / 6f;
    }

    /// <summary>
    /// Mass properties of the closed convex polyhedron by signed-tetrahedron decomposition against the origin:
    /// volume, centre of mass, and the COM-frame inertia tensor scaled to `mass`.
    ///
    /// DELIVERS A DIAGONAL: RigidBody.InvInertiaLocal is a diagonal Vector3, so the returned inertiaDiagonal is
    /// the DIAGONAL of the COM-frame tensor in the INPUT axes — the products of inertia are DROPPED. This is a
    /// standard first-pass approximation: for a skewed hull the off-diagonal terms are ignored, which is exact
    /// for an axis-symmetric shape (a cube gives PhysicsMath.BoxInertia) and small for the near-axis-symmetric
    /// game meshes we target. Principal-axis diagonalisation is a possible later refinement.
    /// A degenerate (volume ≈ 0) hull falls back to a small sphere-like diagonal rather than dividing by zero.
    /// </summary>
    public void ComputeMassProperties(float mass, out Vector3 com, out Vector3 inertiaDiagonal)
    {
        // Note: only the DIAGONAL of the origin-frame covariance is needed — the parallel-axis shift and the
        // "trace·I − C" inertia both use diagonal terms only, so the dropped products of inertia never enter.
        float v6 = 0f;                                    // 6·Volume (Σ det)
        Vector3 comAccum = Vector3.Zero;                  // Σ det·(a+b+c)
        float cxx = 0f, cyy = 0f, czz = 0f;               // 60·(origin-frame covariance diagonal)
        int fc = Triangles.Length / 3;
        for (int f = 0; f < fc; f++)
        {
            Vector3 a = Vertices[Triangles[f * 3]], b = Vertices[Triangles[f * 3 + 1]], c = Vertices[Triangles[f * 3 + 2]];
            float det = a * Vector3.Cross(b, c);          // 6·(signed volume of tetra origin-a-b-c)
            v6 += det;
            comAccum += (a + b + c) * det;
            cxx += det * (a.X * a.X + b.X * b.X + c.X * c.X + a.X * b.X + a.X * c.X + b.X * c.X);
            cyy += det * (a.Y * a.Y + b.Y * b.Y + c.Y * c.Y + a.Y * b.Y + a.Y * c.Y + b.Y * c.Y);
            czz += det * (a.Z * a.Z + b.Z * b.Z + c.Z * c.Z + a.Z * b.Z + a.Z * c.Z + b.Z * c.Z);
        }
        float vol = v6 / 6f;

        if (MathF.Abs(vol) < 1e-9f)   // degenerate (flat / near-zero volume): don't divide by zero
        {
            Vector3 avg = Vector3.Zero;
            for (int i = 0; i < Vertices.Length; i++) avg += Vertices[i];
            com = Vertices.Length > 0 ? avg / (float)Vertices.Length : Vector3.Zero;
            float r = 1e-3f;
            for (int i = 0; i < Vertices.Length; i++) r = MathF.Max(r, (Vertices[i] - com).Length());
            float sph = 0.4f * mass * r * r;   // solid-sphere-like fallback so InvInertia stays finite
            inertiaDiagonal = new Vector3(sph, sph, sph);
            return;
        }

        com = comAccum / (v6 * 4f);                       // Σ(dV·centroid)/V, dV = det/6, centroid = (a+b+c)/4
        float cxxO = cxx / 60f, cyyO = cyy / 60f, czzO = czz / 60f;   // ∫x²dV, ∫y²dV, ∫z²dV about the origin
        // Parallel-axis shift of the covariance diagonal to the COM (Cii_com = Cii − V·com_i²), then the inertia
        // diagonal is trace(C_com) − C_com,ii, i.e. the sum of the OTHER two shifted covariance terms.
        float cxxC = cxxO - vol * com.X * com.X;
        float cyyC = cyyO - vol * com.Y * com.Y;
        float czzC = czzO - vol * com.Z * com.Z;
        float density = mass / vol;
        inertiaDiagonal = new Vector3(
            density * (cyyC + czzC),
            density * (cxxC + czzC),
            density * (cxxC + cyyC));
    }
}
