using System;
using System.Collections.Generic;
using Nova3DVisualiser;

namespace SampleGame.Physics;

// ---- Plan C2-2: GJK (boolean intersection + closest distance) and EPA (penetration normal + depth), over
// generic SUPPORT FUNCTIONS so they work for ANY convex shape (hull/box/sphere). PURE math — no RigidBody /
// ImpulseWorld / ContactGen / scene coupling (that's C2-3). Matches the codebase's custom Vector3 (dot = the
// `*` operator, cross via Vector3.Cross); reuses ImpulseMath.Rotate / RotateInv for the world support adapter.
// No third-party dependencies. Numerically disciplined: both loops are iteration-capped with progress epsilons,
// degenerate/coincident input returns a sane result (never NaN, never an infinite loop).
public static class Gjk
{
    // A convex shape for the algorithms = a world-space support map: the FARTHEST point of the shape along dir.
    public delegate Vector3 SupportFn(Vector3 dir);

    // A ConvexHull placed at (position, orientation) as a world-space support map: rotate dir into the hull's
    // local frame, take the local support, rotate back and translate. Reuses the solver's quaternion helpers.
    public static SupportFn HullSupport(ConvexHull hull, Vector3 position, Quat orientation)
        => dir => position + ImpulseMath.Rotate(orientation, hull.Support(ImpulseMath.RotateInv(orientation, dir)));

    // One Minkowski-difference (CSO = A ⊖ B) vertex, carrying the A- and B-supports that produced it so the
    // closest/contact WITNESS points can be recovered from a terminating simplex's barycentric weights.
    private struct SV { public Vector3 P; public Vector3 A; public Vector3 B; }

    // CSO support: supportA(dir) − supportB(−dir), keeping both witnesses.
    private static SV Cso(SupportFn a, SupportFn b, Vector3 dir)
    {
        Vector3 sa = a(dir);
        Vector3 sb = b(-dir);
        return new SV { P = sa - sb, A = sa, B = sb };
    }

    private const int GjkMaxIter = 32;
    private const float ProgressEps = 1e-7f;    // relative "no closer to the origin" threshold
    private const float ContainEps2 = 1e-10f;   // |closest|² below this → the origin is on/inside the simplex (overlap)
    private const float DupEps2 = 1e-12f;       // a repeated support point → converged

    private const int EpaMaxIter = 64;
    private const int EpaMaxVerts = 128;
    private const float EpaEps = 1e-4f;         // a new support no farther than the current face → converged

    // =============================== GJK ===============================

    /// <summary>Boolean intersection test: true when the two convex shapes overlap (their CSO contains the
    /// origin). Robust to coincident/degenerate input; never hangs.</summary>
    public static bool Intersect(SupportFn a, SupportFn b)
    {
        var simplex = new SV[4]; var w = new float[4];
        return GjkCore(a, b, simplex, out _, w, out _);
    }

    /// <summary>Closest distance between two SEPARATED convex shapes. Returns true with the witness points on A
    /// and B (their barycentric reconstruction from the terminating simplex) and their distance. Returns FALSE
    /// when the shapes actually intersect (distance 0 — the caller uses <see cref="Penetration"/> instead).</summary>
    public static bool Distance(SupportFn a, SupportFn b, out Vector3 pointA, out Vector3 pointB, out float distance)
    {
        pointA = Vector3.Zero; pointB = Vector3.Zero; distance = 0f;
        var simplex = new SV[4]; var w = new float[4];
        if (GjkCore(a, b, simplex, out int n, w, out Vector3 closest)) return false;   // overlapping → not separated

        Vector3 pa = Vector3.Zero, pb = Vector3.Zero;
        for (int i = 0; i < n; i++) { pa += simplex[i].A * w[i]; pb += simplex[i].B * w[i]; }
        pointA = pa; pointB = pb;
        distance = closest.Length();   // == |pointA − pointB|
        return true;
    }

    // Closest-point GJK over the CSO. Fills `simplex` + `n` with the terminating feature, `weights` with its
    // barycentric weights, `closest` with the closest CSO point to the origin. Returns true when the origin is
    // inside/on the CSO (overlap); false when the shapes are separated.
    private static bool GjkCore(SupportFn a, SupportFn b, SV[] simplex, out int n, float[] weights, out Vector3 closest)
    {
        simplex[0] = Cso(a, b, new Vector3(1f, 0f, 0f));
        n = 1; weights[0] = 1f;
        closest = simplex[0].P;

        for (int iter = 0; iter < GjkMaxIter; iter++)
        {
            float distSq = closest * closest;
            if (distSq < ContainEps2) return true;   // origin on/inside the simplex → overlap

            SV w = Cso(a, b, -closest);              // support toward the origin from the closest feature

            // No progress toward the origin → converged; the shapes are separated and `closest` is the answer.
            float proj = w.P * closest;
            if (distSq - proj <= ProgressEps * distSq) return false;

            // A repeated support can't get us closer → converged (separated).
            bool dup = false;
            for (int k = 0; k < n; k++) { Vector3 d = w.P - simplex[k].P; if (d * d < DupEps2) { dup = true; break; } }
            if (dup) return false;

            simplex[n] = w; n++;
            closest = DoClosest(simplex, ref n, weights);
        }
        return closest * closest < ContainEps2;   // iteration cap: best-effort classification
    }

    // Closest point on the current simplex (1–4 CSO vertices) to the origin; REDUCES the simplex in place to the
    // minimal feature carrying that point and fills `weights` (over the reduced set). Reorders `simplex` so the
    // survivors occupy [0..n-1] with their witnesses intact.
    private static Vector3 DoClosest(SV[] s, ref int n, float[] weights)
    {
        switch (n)
        {
            case 1: weights[0] = 1f; return s[0].P;
            case 2: return Closest2(s, ref n, weights);
            case 3: return Closest3(s, ref n, weights);
            default: return Closest4(s, ref n, weights);
        }
    }

    private static Vector3 Closest2(SV[] s, ref int n, float[] w)
    {
        Vector3 a = s[0].P, b = s[1].P, ab = b - a;
        float ab2 = ab * ab;
        if (ab2 < 1e-20f) { n = 1; w[0] = 1f; return a; }        // degenerate → the vertex
        float t = (-a * ab) / ab2;                               // closest param of the origin onto the segment
        if (t <= 0f) { n = 1; w[0] = 1f; return a; }
        if (t >= 1f) { s[0] = s[1]; n = 1; w[0] = 1f; return b; }
        w[0] = 1f - t; w[1] = t; return a + ab * t;
    }

    private static Vector3 Closest3(SV[] s, ref int n, float[] w)
    {
        SV A = s[0], B = s[1], C = s[2];
        var (wa, wb, wc, cp) = ClosestTri(A.P, B.P, C.P);
        int m = 0;
        if (wa > 1e-9f) { s[m] = A; w[m] = wa; m++; }
        if (wb > 1e-9f) { s[m] = B; w[m] = wb; m++; }
        if (wc > 1e-9f) { s[m] = C; w[m] = wc; m++; }
        n = m > 0 ? m : 1;
        if (m == 0) { s[0] = A; w[0] = 1f; }                     // numerical safety
        return cp;
    }

    private static Vector3 Closest4(SV[] s, ref int n, float[] w)
    {
        SV A = s[0], B = s[1], C = s[2], D = s[3];
        Vector3 O = Vector3.Zero;

        float bestSq = float.MaxValue;
        Vector3 bestCp = O;
        SV fp = A, fq = B, fr = C; float fwa = 1f, fwb = 0f, fwc = 0f;
        bool any = false;

        void Consider(SV p, SV q, SV r)
        {
            var (wa, wb, wc, cp) = ClosestTri(p.P, q.P, r.P);
            float sq = cp * cp;
            if (sq < bestSq) { bestSq = sq; bestCp = cp; fp = p; fq = q; fr = r; fwa = wa; fwb = wb; fwc = wc; any = true; }
        }

        // Four faces, each ordered so the opposite vertex is "inside"; test the origin against each plane.
        if (PointOutsidePlane(O, A.P, B.P, C.P, D.P)) Consider(A, B, C);
        if (PointOutsidePlane(O, A.P, C.P, D.P, B.P)) Consider(A, C, D);
        if (PointOutsidePlane(O, A.P, D.P, B.P, C.P)) Consider(A, D, B);
        if (PointOutsidePlane(O, B.P, D.P, C.P, A.P)) Consider(B, D, C);

        if (!any)   // the origin is inside the tetrahedron → overlap. Keep all four (EPA re-seeds from them).
        {
            w[0] = w[1] = w[2] = w[3] = 0.25f;
            n = 4;
            return O;
        }

        int m = 0;
        if (fwa > 1e-9f) { s[m] = fp; w[m] = fwa; m++; }
        if (fwb > 1e-9f) { s[m] = fq; w[m] = fwb; m++; }
        if (fwc > 1e-9f) { s[m] = fr; w[m] = fwc; m++; }
        n = m > 0 ? m : 1;
        if (m == 0) { s[0] = fp; w[0] = 1f; }
        return bestCp;
    }

    // Closest point on triangle (a,b,c) to the ORIGIN + its barycentric weights (Ericson, specialised to P=0).
    private static (float wa, float wb, float wc, Vector3 cp) ClosestTri(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a;
        Vector3 ap = -a;                    // P − A with P = origin
        float d1 = ab * ap, d2 = ac * ap;
        if (d1 <= 0f && d2 <= 0f) return (1f, 0f, 0f, a);

        Vector3 bp = -b;
        float d3 = ab * bp, d4 = ac * bp;
        if (d3 >= 0f && d4 <= d3) return (0f, 1f, 0f, b);

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); return (1f - v, v, 0f, a + ab * v); }

        Vector3 cp0 = -c;
        float d5 = ab * cp0, d6 = ac * cp0;
        if (d6 >= 0f && d5 <= d6) return (0f, 0f, 1f, c);

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float wv = d2 / (d2 - d6); return (1f - wv, 0f, wv, a + ac * wv); }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        { float wv = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (0f, 1f - wv, wv, b + (c - b) * wv); }

        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return (1f - vv - ww, vv, ww, a + ab * vv + ac * ww);
    }

    // True when p is on the OPPOSITE side of plane(a,b,c) from d (i.e. "outside" that tetra face). A degenerate
    // face (d on the plane) reports not-outside so it is skipped.
    private static bool PointOutsidePlane(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 nrm = Vector3.Cross(b - a, c - a);
        float sp = (p - a) * nrm;
        float sd = (d - a) * nrm;
        if (MathF.Abs(sd) < 1e-12f) return false;
        return sp * sd < 0f;
    }

    // =============================== EPA ===============================

    private sealed class Face { public int A, B, C; public Vector3 N; public float Dist; }

    /// <summary>
    /// Penetration depth + normal for an OVERLAPPING pair (precondition: <see cref="Intersect"/> returned true).
    /// SIGN CONVENTION: `normal` is a UNIT vector such that translating B by <c>−depth·normal</c> (equivalently A
    /// by <c>+depth·normal</c>) brings the shapes to just-touching — i.e. it points from B toward A. It is the
    /// NEGATIVE of the CSO (A⊖B) closest-face outward normal. `depth` ≥ 0 is the origin-to-face distance;
    /// `contactPoint` is the midpoint of the recovered A/B witnesses on the closest feature. Returns false on
    /// degenerate seeding (the caller falls back). Never hangs / NaNs.
    /// </summary>
    public static bool Penetration(SupportFn a, SupportFn b, out Vector3 normal, out float depth, out Vector3 contactPoint)
    {
        normal = new Vector3(0f, 1f, 0f); depth = 0f; contactPoint = Vector3.Zero;

        var simplex = new SV[4]; var w = new float[4];
        if (!GjkCore(a, b, simplex, out int n, w, out _)) return false;   // not actually overlapping
        if (!BuildTetra(a, b, simplex, ref n)) return false;             // grow the terminating simplex to a tetra

        // Vertex pool + the tetra's four outward faces (oriented away from a fixed interior point that stays
        // inside as the polytope only grows outward).
        var verts = new List<SV>(EpaMaxVerts) { simplex[0], simplex[1], simplex[2], simplex[3] };
        Vector3 interior = (simplex[0].P + simplex[1].P + simplex[2].P + simplex[3].P) * 0.25f;
        var faces = new List<Face>(4 * EpaMaxIter);
        void Add(int ia, int ib, int ic) { var f = MakeFace(verts, ia, ib, ic, interior); if (f != null) faces.Add(f); }
        Add(0, 1, 2); Add(0, 1, 3); Add(0, 2, 3); Add(1, 2, 3);
        if (faces.Count < 4) return false;

        for (int iter = 0; iter < EpaMaxIter; iter++)
        {
            // Closest face to the origin.
            int fi = 0; float best = faces[0].Dist;
            for (int i = 1; i < faces.Count; i++) if (faces[i].Dist < best) { best = faces[i].Dist; fi = i; }
            Face F = faces[fi];

            // New support along the face's outward normal. If it is no farther than the face, the face lies on
            // the CSO boundary → we have the answer.
            SV s = Cso(a, b, F.N);
            float d = s.P * F.N;
            if (d - F.Dist < EpaEps)
            {
                depth = F.Dist;
                normal = -F.N;   // sign convention (B moves by −depth·normal to separate)
                var (wa, wb, wc, _) = ClosestTri(verts[F.A].P, verts[F.B].P, verts[F.C].P);
                Vector3 pa = verts[F.A].A * wa + verts[F.B].A * wb + verts[F.C].A * wc;
                Vector3 pb = verts[F.A].B * wa + verts[F.B].B * wb + verts[F.C].B * wc;
                contactPoint = (pa + pb) * 0.5f;
                return true;
            }

            // Otherwise expand: insert the support, carve away the faces it can see, re-close the horizon (the
            // same directed-edge horizon as the convex-hull builder).
            int wi = verts.Count;
            verts.Add(s);
            if (verts.Count > EpaMaxVerts) return false;

            var edges = new HashSet<(int, int)>();
            var visible = new List<int>();
            for (int i = 0; i < faces.Count; i++)
            {
                Face f = faces[i];
                if (f.N * s.P - f.Dist > 1e-9f)   // s is in front of face f → visible
                {
                    visible.Add(i);
                    edges.Add((f.A, f.B)); edges.Add((f.B, f.C)); edges.Add((f.C, f.A));
                }
            }
            if (visible.Count == 0) { depth = F.Dist; normal = -F.N; contactPoint = Vector3.Zero; return true; }

            var horizon = new List<(int, int)>();
            foreach (int i in visible)
            {
                Face f = faces[i];
                if (!edges.Contains((f.B, f.A))) horizon.Add((f.A, f.B));
                if (!edges.Contains((f.C, f.B))) horizon.Add((f.B, f.C));
                if (!edges.Contains((f.A, f.C))) horizon.Add((f.C, f.A));
            }
            // Remove visible faces (descending index so removals don't shift the rest).
            visible.Sort();
            for (int i = visible.Count - 1; i >= 0; i--) faces.RemoveAt(visible[i]);
            foreach (var (u, v) in horizon) Add(wi, u, v);
            if (faces.Count == 0 || faces.Count > 4 * EpaMaxIter) return false;
        }
        return false;   // iteration cap — degenerate; caller falls back
    }

    // The GJK terminating simplex may have < 4 vertices (a shared point/edge/face). Grow it to a non-degenerate
    // tetrahedron in CSO space so EPA has a seed enclosing the origin (the classic EPA-seeding fragility).
    private static bool BuildTetra(SupportFn a, SupportFn b, SV[] s, ref int n)
    {
        if (n == 1)
        {
            foreach (var dir in Axes())
            {
                SV w = Cso(a, b, dir);
                if ((w.P - s[0].P).Length() > 1e-5f) { s[1] = w; n = 2; break; }
            }
            if (n < 2) return false;
        }
        if (n == 2)
        {
            Vector3 line = s[1].P - s[0].P;
            Vector3 axis = LeastAlignedAxis(line);
            Vector3 dir = Vector3.Cross(line, axis);
            if (dir * dir < 1e-12f) return false;
            SV w = Cso(a, b, dir);
            if (LineDist(w.P, s[0].P, s[1].P) < 1e-5f) w = Cso(a, b, -dir);
            if (LineDist(w.P, s[0].P, s[1].P) < 1e-5f) return false;
            s[2] = w; n = 3;
        }
        if (n == 3)
        {
            Vector3 nrm = Vector3.Cross(s[1].P - s[0].P, s[2].P - s[0].P);
            float nl = nrm.Length();
            if (nl < 1e-10f) return false;
            nrm = nrm / nl;
            SV w = Cso(a, b, nrm);
            if (MathF.Abs((w.P - s[0].P) * nrm) < 1e-5f) w = Cso(a, b, -nrm);
            if (MathF.Abs((w.P - s[0].P) * nrm) < 1e-5f) return false;
            s[3] = w; n = 4;
        }
        return n == 4;
    }

    // An EPA face from three pool vertices, wound OUTWARD (normal away from `interior`). Null on a zero-area face.
    private static Face? MakeFace(List<SV> verts, int ia, int ib, int ic, Vector3 interior)
    {
        Vector3 a = verts[ia].P, b = verts[ib].P, c = verts[ic].P;
        Vector3 nn = Vector3.Cross(b - a, c - a);
        float len = nn.Length();
        if (len < 1e-12f) return null;
        nn = nn / len;
        if (nn * (a - interior) < 0f) { int t = ib; ib = ic; ic = t; nn = -nn; a = verts[ia].P; }
        return new Face { A = ia, B = ib, C = ic, N = nn, Dist = nn * a };
    }

    private static IEnumerable<Vector3> Axes()
    {
        yield return new Vector3(1f, 0f, 0f); yield return new Vector3(-1f, 0f, 0f);
        yield return new Vector3(0f, 1f, 0f); yield return new Vector3(0f, -1f, 0f);
        yield return new Vector3(0f, 0f, 1f); yield return new Vector3(0f, 0f, -1f);
    }

    private static Vector3 LeastAlignedAxis(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax <= ay && ax <= az) return new Vector3(1f, 0f, 0f);
        return ay <= az ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
    }

    private static float LineDist(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a; float ab2 = ab * ab;
        if (ab2 < 1e-20f) return (p - a).Length();
        float t = ((p - a) * ab) / ab2;
        return (p - (a + ab * t)).Length();
    }
}
