using System;
using System.Collections.Generic;
using Nova3DVisualiser;
using SampleGame.Scenes;
using Quat = SampleGame.Physics.Quat;

namespace SampleGame.Physics;

// A single contact constraint (one POINT of a manifold) between two bodies. The normal points from A to B;
// a positive normal impulse pushes B along +Normal and A along −Normal. The solver scratch (masses, bias,
// accumulated impulses) is filled per substep; the accumulated normal + tangent impulses are cached across
// substeps (keyed by the body pair + Feature) for warm-starting — the key to a jitter-free flat rest.
public sealed class Contact
{
    public RigidBody A = null!, B = null!;
    public Vector3 Point;                 // world contact point
    public Vector3 Normal;                // unit, A -> B
    public float Penetration;             // > 0 => overlapping
    public int Feature;                   // stable per-manifold-point id (box corner index etc.) for warm-start
    public float Friction;                // combined Coulomb μ for this contact (geometric mean of the two bodies)
    public float Restitution;             // combined restitution for this contact (geometric mean)

    // scratch (per substep)
    public Vector3 Ra, Rb;                // Point − body centre
    public Vector3 Tan1, Tan2;            // orthonormal tangent basis (⟂ Normal)
    public float NormalMass, TanMass1, TanMass2;
    public float RestitutionBias;         // target separation speed from the initial impact (0 for resting)
    public float NormalImpulse;           // accumulated normal impulse (warm-started)
    public float TanImpulse1, TanImpulse2;// accumulated tangent (friction) impulses (warm-started)
    public float PosImpulse;              // accumulated split-impulse (position pass, not warm-started)

    // warm-start key: unordered pair of body ids + the manifold-point Feature.
    public ulong PairKey => A.Id < B.Id ? ((ulong)(uint)A.Id << 32) | (uint)B.Id : ((ulong)(uint)B.Id << 32) | (uint)A.Id;
}

// Pure quaternion / inverse-inertia helpers (kept tiny + testable; NOT a re-implementation of the
// engine's collision geometry, which is reused).
public static class ImpulseMath
{
    // Rotate vector v by unit quaternion q: v' = v + 2w(u×v) + 2(u×(u×v)), u = q.xyz.
    public static Vector3 Rotate(Quat q, Vector3 v)
    {
        Vector3 u = new Vector3(q.X, q.Y, q.Z);
        Vector3 t = Vector3.Cross(u, v) * 2f;
        return v + t * q.W + Vector3.Cross(u, t);
    }

    // Rotate by the inverse (conjugate) of q.
    public static Vector3 RotateInv(Quat q, Vector3 v) => Rotate(new Quat(-q.X, -q.Y, -q.Z, q.W), v);

    // World inverse inertia applied to a world vector: R · diag(InvInertiaLocal) · Rᵀ · v — express v in
    // the body frame (Rᵀ), scale by the principal inverse moments, rotate back (R). Static body → 0.
    public static Vector3 MulInvInertiaWorld(RigidBody b, Vector3 v)
    {
        Vector3 local = RotateInv(b.Orientation, v);
        local = new Vector3(local.X * b.InvInertiaLocal.X, local.Y * b.InvInertiaLocal.Y, local.Z * b.InvInertiaLocal.Z);
        return Rotate(b.Orientation, local);
    }

    // Deterministic orthonormal tangent basis (t1,t2) ⟂ the unit normal n. Deterministic in n so a
    // resting contact's tangents don't flip frame-to-frame (needed for friction warm-start to catch).
    public static void TangentBasis(Vector3 n, out Vector3 t1, out Vector3 t2)
    {
        t1 = MathF.Abs(n.X) >= 0.57735f ? new Vector3(n.Y, -n.X, 0f) : new Vector3(0f, n.Z, -n.Y);
        float l = t1.Length();
        t1 = l > 1e-8f ? t1 * (1f / l) : new Vector3(1f, 0f, 0f);
        t2 = Vector3.Cross(n, t1);
    }
}

// Contact generation for Stage 1: a DYNAMIC sphere (always body B) vs a STATIC shape (always body A).
// Pure + testable. Reuses CollisionMath.ClosestPointOnTriangle for the mesh case.
public static class ContactGen
{
    // Closest point on an oriented box (centre c, half-extents h, orientation q) to world point p.
    public static Vector3 ClosestPointOnObb(Vector3 c, Vector3 h, Quat q, Vector3 p)
    {
        Vector3 local = ImpulseMath.RotateInv(q, p - c);
        local = new Vector3(
            System.Math.Clamp(local.X, -h.X, h.X),
            System.Math.Clamp(local.Y, -h.Y, h.Y),
            System.Math.Clamp(local.Z, -h.Z, h.Z));
        return c + ImpulseMath.Rotate(q, local);
    }

    // A contact is reported when the sphere is within 'margin' of touching (a small speculative margin so
    // a resting contact is caught just before it penetrates). Penetration = radius − distance.
    private static bool FromClosest(RigidBody stat, RigidBody sph, Vector3 closest, float margin, out Contact c)
    {
        c = null!;
        Vector3 d = sph.Position - closest;
        float dist2 = d * d;
        float reach = sph.Radius + margin;
        if (dist2 > reach * reach) return false;
        float dist = MathF.Sqrt(dist2);
        Vector3 n = dist > 1e-6f ? d / dist : new Vector3(0f, 1f, 0f);   // degenerate (centre on surface) -> push up
        c = new Contact { A = stat, B = sph, Point = closest, Normal = n, Penetration = sph.Radius - dist };
        return true;
    }

    public static bool SphereVsBox(RigidBody box, RigidBody sph, float margin, out Contact c)
        => FromClosest(box, sph, ClosestPointOnObb(box.Position, box.HalfExtents, box.Orientation, sph.Position), margin, out c);

    // Sphere (B) vs another sphere (A). Pure geometry — A may be static (Stage 1) or dynamic (Stage 4); the
    // solver mass-weights the impulse either way. Normal A->B along the centre line, penetration = rA+rB−dist.
    public static bool SphereVsSphere(RigidBody a, RigidBody b, float margin, out Contact c)
    {
        // closest point on A's surface toward B's centre.
        Vector3 d = b.Position - a.Position; float dl = d.Length();
        Vector3 dir = dl > 1e-6f ? d / dl : new Vector3(0f, 1f, 0f);
        Vector3 closest = a.Position + dir * a.Radius;
        return FromClosest(a, b, closest, margin, out c);
    }

    // Closest point over a mesh's world triangles (reuses the engine's ClosestPointOnTriangle). Above
    // MaxFaces it falls back to the mesh's AABB as a box (cost guard; the real-triangle path for high-poly
    // meshes is Stage 5). Returns the nearest contact.
    public const int MaxFaces = 512;
    public static bool SphereVsMesh(RigidBody mesh, RigidBody sph, float margin, out Contact c)
    {
        c = null!;
        var verts = mesh.MeshVerts!; var tris = mesh.MeshTris!;
        int faceCount = tris.Length / 3;
        if (faceCount > MaxFaces)
        {
            // AABB fallback: box from the mesh's world vertex bounds.
            Vector3 mn = verts[0], mx = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                mn = new Vector3(MathF.Min(mn.X, v.X), MathF.Min(mn.Y, v.Y), MathF.Min(mn.Z, v.Z));
                mx = new Vector3(MathF.Max(mx.X, v.X), MathF.Max(mx.Y, v.Y), MathF.Max(mx.Z, v.Z));
            }
            Vector3 closestBox = new Vector3(
                System.Math.Clamp(sph.Position.X, mn.X, mx.X),
                System.Math.Clamp(sph.Position.Y, mn.Y, mx.Y),
                System.Math.Clamp(sph.Position.Z, mn.Z, mx.Z));
            return FromClosest(mesh, sph, closestBox, margin, out c);
        }

        float best = float.MaxValue; Vector3 bestQ = default;
        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 q = CollisionMath.ClosestPointOnTriangle(sph.Position, verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]]);
            Vector3 d = sph.Position - q; float d2 = d * d;
            if (d2 < best) { best = d2; bestQ = q; }
        }
        return FromClosest(mesh, sph, bestQ, margin, out c);
    }

    // ---- Stage 2: dynamic BOX vs static shapes ----

    // Dynamic box (B) vs static sphere (A): single closest-feature contact. Normal A->B (sphere -> box).
    public static bool BoxVsStaticSphere(RigidBody sphere, RigidBody box, float margin, out Contact c)
    {
        c = null!;
        Vector3 cp = ClosestPointOnObb(box.Position, box.HalfExtents, box.Orientation, sphere.Position);
        Vector3 d = cp - sphere.Position; float dist = d.Length();
        if (dist > sphere.Radius + margin) return false;
        Vector3 n = dist > 1e-6f ? d * (1f / dist) : new Vector3(0f, 1f, 0f);   // sphere centre inside -> push up
        c = new Contact { A = sphere, B = box, Point = cp, Normal = n, Penetration = sphere.Radius - dist, Feature = 0 };
        return true;
    }

    // ---- Stage 5: dynamic BOX vs static real TRIANGLE MESH (ramps / wedges / pyramids) ----
    // Corner-sampling manifold (pragmatic + stable; reuses ClosestPointOnTriangle — NOT a full box-vs-triangle
    // SAT). Each of the box's 8 world corners that penetrates the mesh emits ONE contact AT the corner, with
    // the penetrated triangle's FACE normal (oriented toward the box) and the depth below that face. Up to 4
    // simultaneous corner contacts on a face let the sequential-impulse solver ALIGN the box to the slope and
    // TUMBLE it over its edge (emergent — no legacy tip). Cheap per-triangle AABB cull; above MaxFaces it falls
    // back to the mesh's OBB box view (the high-poly guard — real-triangle high-poly is a later refinement).
    private const float MaxCornerPen = 2f;      // a corner deeper than this has tunnelled -> skip it (the anti-fling rails handle the runaway)
    private const float InteriorEps = 1e-3f;    // the corner must project INSIDE the triangle (a face contact), not onto an edge/vertex

    public static void BoxVsMesh(RigidBody mesh, RigidBody box, float margin, List<Contact> outList)
    {
        var verts = mesh.MeshVerts!; var tris = mesh.MeshTris!;
        if (tris.Length / 3 > MaxFaces) { BoxVsBox(mesh, box, margin, outList); return; }   // high-poly -> OBB box view

        // box world corners (feature id 0..7) + a query AABB for the per-triangle cull.
        Vector3 hx = ImpulseMath.Rotate(box.Orientation, new Vector3(box.HalfExtents.X, 0f, 0f));
        Vector3 hy = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, box.HalfExtents.Y, 0f));
        Vector3 hz = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 0f, box.HalfExtents.Z));
        Vector3 c = box.Position;
        Span<Vector3> corner = stackalloc Vector3[8];
        corner[0] = c - hx - hy - hz; corner[1] = c - hx - hy + hz;
        corner[2] = c - hx + hy - hz; corner[3] = c - hx + hy + hz;
        corner[4] = c + hx - hy - hz; corner[5] = c + hx - hy + hz;
        corner[6] = c + hx + hy - hz; corner[7] = c + hx + hy + hz;
        Vector3 qmin = corner[0], qmax = corner[0];
        for (int k = 1; k < 8; k++)
        {
            Vector3 p = corner[k];
            qmin = new Vector3(MathF.Min(qmin.X, p.X), MathF.Min(qmin.Y, p.Y), MathF.Min(qmin.Z, p.Z));
            qmax = new Vector3(MathF.Max(qmax.X, p.X), MathF.Max(qmax.Y, p.Y), MathF.Max(qmax.Z, p.Z));
        }
        qmin -= new Vector3(margin, margin, margin); qmax += new Vector3(margin, margin, margin);

        Span<float> bestPen = stackalloc float[8];
        Span<Vector3> bestN = stackalloc Vector3[8];
        Span<bool> found = stackalloc bool[8];
        for (int k = 0; k < 8; k++) { bestPen[k] = float.NegativeInfinity; found[k] = false; }

        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 v0 = verts[tris[t]], v1 = verts[tris[t + 1]], v2 = verts[tris[t + 2]];
            Vector3 tmin = new Vector3(MathF.Min(v0.X, MathF.Min(v1.X, v2.X)), MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)), MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)));
            Vector3 tmax = new Vector3(MathF.Max(v0.X, MathF.Max(v1.X, v2.X)), MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)), MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)));
            if (tmax.X < qmin.X || tmin.X > qmax.X || tmax.Y < qmin.Y || tmin.Y > qmax.Y || tmax.Z < qmin.Z || tmin.Z > qmax.Z) continue;   // cull

            Vector3 fn = Vector3.Cross(v1 - v0, v2 - v0); float fl = fn.Length();
            if (fl < 1e-9f) continue;                                   // degenerate triangle
            fn *= 1f / fl;
            if (fn * (c - v0) < 0f) fn = fn * -1f;                      // orient toward the box (outward from the surface) — winding-independent

            for (int k = 0; k < 8; k++)
            {
                Vector3 p = corner[k];
                float planeDist = (p - v0) * fn;                        // signed distance to the plane (>0 above, <0 penetrating)
                if (planeDist > margin || planeDist < -MaxCornerPen) continue;
                Vector3 q = CollisionMath.ClosestPointOnTriangle(p, v0, v1, v2);
                if ((p - fn * planeDist - q).Length() > InteriorEps) continue;   // p projects OUTSIDE the triangle -> not a face contact
                float pen = -planeDist;
                if (pen > bestPen[k]) { bestPen[k] = pen; bestN[k] = fn; found[k] = true; }   // deepest penetrating face for this corner
            }
        }

        for (int k = 0; k < 8; k++)
            if (found[k])
                outList.Add(new Contact { A = mesh, B = box, Point = corner[k], Normal = bestN[k], Penetration = bestPen[k], Feature = k });
    }

    // Dynamic box (B) vs a static box/box-view (A): full OBB-vs-OBB CONTACT MANIFOLD via reference/incident
    // face clipping (Sutherland–Hodgman). Reuses SatBox3D for the separating normal; A is inflated by
    // 'margin' for the overlap test so a RESTING box is caught speculatively (penetration is still measured
    // against A's TRUE face, so it reads ~0 at rest). Emits up to 4 points (Normal A->B) into 'outList'.
    private struct ClipV { public Vector3 P; public int Feat; }

    public static void BoxVsBox(RigidBody A, RigidBody B, float margin, List<Contact> outList)
    {
        Vector3 aX = ImpulseMath.Rotate(A.Orientation, new Vector3(1f, 0f, 0f));
        Vector3 aY = ImpulseMath.Rotate(A.Orientation, new Vector3(0f, 1f, 0f));
        Vector3 aZ = ImpulseMath.Rotate(A.Orientation, new Vector3(0f, 0f, 1f));
        Vector3 bX = ImpulseMath.Rotate(B.Orientation, new Vector3(1f, 0f, 0f));
        Vector3 bY = ImpulseMath.Rotate(B.Orientation, new Vector3(0f, 1f, 0f));
        Vector3 bZ = ImpulseMath.Rotate(B.Orientation, new Vector3(0f, 0f, 1f));
        Vector3 hA = A.HalfExtents, hB = B.HalfExtents;
        Vector3 hAi = new Vector3(hA.X + margin, hA.Y + margin, hA.Z + margin);

        var (hit, n, _) = CollisionMath.SatBox3D(A.Position, aX, aY, aZ, hAi, B.Position, bX, bY, bZ, hB);
        if (!hit) return;                                            // separated beyond the speculative margin

        Vector3[] uA = { aX, aY, aZ }; float[] fA = { hA.X, hA.Y, hA.Z };
        Vector3[] uB = { bX, bY, bZ }; float[] fB = { hB.X, hB.Y, hB.Z };

        // Reference face = the face most parallel to the collision normal (n points A->B).
        int aiA = 0; float bestA = -1f;
        for (int i = 0; i < 3; i++) { float d = MathF.Abs(uA[i] * n); if (d > bestA) { bestA = d; aiA = i; } }
        int aiB = 0; float bestB = -1f;
        for (int i = 0; i < 3; i++) { float d = MathF.Abs(uB[i] * n); if (d > bestB) { bestB = d; aiB = i; } }
        bool refIsA = bestA >= bestB;

        Vector3[] uRef = refIsA ? uA : uB; float[] fRef = refIsA ? fA : fB;
        Vector3 cRef = refIsA ? A.Position : B.Position; int refAxis = refIsA ? aiA : aiB;
        Vector3 refDir = refIsA ? n : n * -1f;                       // outward from the reference toward the incident box
        Vector3 refN = uRef[refAxis] * ((uRef[refAxis] * refDir) >= 0f ? 1f : -1f);
        Vector3 refCenter = cRef + refN * fRef[refAxis];
        int r1 = (refAxis + 1) % 3, r2 = (refAxis + 2) % 3;
        Vector3 refU = uRef[r1]; float refHU = fRef[r1];
        Vector3 refV = uRef[r2]; float refHV = fRef[r2];

        // Incident face on the OTHER box = the face most anti-parallel to refN.
        Vector3[] uInc = refIsA ? uB : uA; float[] fInc = refIsA ? fB : fA;
        Vector3 cInc = refIsA ? B.Position : A.Position;
        int incAxis = 0; float incMin = float.MaxValue; float incSign = 1f;
        for (int i = 0; i < 3; i++)
        {
            float d = uInc[i] * refN;
            if (d < incMin) { incMin = d; incAxis = i; incSign = 1f; }
            if (-d < incMin) { incMin = -d; incAxis = i; incSign = -1f; }
        }
        Vector3 incN = uInc[incAxis] * incSign;
        Vector3 incCenter = cInc + incN * fInc[incAxis];
        int i1 = (incAxis + 1) % 3, i2 = (incAxis + 2) % 3;
        Vector3 incU = uInc[i1] * fInc[i1];
        Vector3 incV = uInc[i2] * fInc[i2];

        // 4 incident-face corners (stable feature ids 0..3).
        var poly = new List<ClipV>(8)
        {
            new ClipV { P = incCenter + incU + incV, Feat = 0 },
            new ClipV { P = incCenter - incU + incV, Feat = 1 },
            new ClipV { P = incCenter - incU - incV, Feat = 2 },
            new ClipV { P = incCenter + incU - incV, Feat = 3 },
        };
        // Clip the incident polygon against the reference face's 4 side planes (feature ids 4..7 for the
        // cut vertices). f(p) = (p − refCenter)·axis; keep points within [−half, +half] on refU and refV.
        poly = ClipPlane(poly, refU, refCenter,  refHU, 4);
        poly = ClipPlane(poly, refU, refCenter, -refHU, 5);
        poly = ClipPlane(poly, refV, refCenter,  refHV, 6);
        poly = ClipPlane(poly, refV, refCenter, -refHV, 7);
        if (poly.Count == 0) return;

        Vector3 abN = refIsA ? refN : refN * -1f;                    // A->B contact normal
        int featBase = refIsA ? 0 : 64;
        // Collect the penetrating/near points; keep the deepest 4 (a resting box gives exactly 4 corners,
        // no clipping — so the deepest-4 cap only trims the rare edge-overhang case).
        var pts = new List<(Vector3 p, float pen, int feat)>(poly.Count);
        foreach (var v in poly)
        {
            float sep = (v.P - refCenter) * refN;                    // >0 above the true ref face, <0 penetrating
            if (sep <= margin) pts.Add((v.P, -sep, featBase + v.Feat));
        }
        if (pts.Count > 4) { pts.Sort((x, y) => y.pen.CompareTo(x.pen)); pts.RemoveRange(4, pts.Count - 4); }
        foreach (var (p, pen, feat) in pts)
            outList.Add(new Contact { A = A, B = B, Point = p, Normal = abN, Penetration = pen, Feature = feat });
    }

    // Sutherland–Hodgman clip of a convex polygon against the slab plane f(p)=(p−origin)·axis == limit,
    // keeping the side toward the rectangle centre. A POSITIVE 'limit' keeps f<=limit; a NEGATIVE 'limit'
    // keeps f>=limit. Cut vertices are tagged with 'cutFeat'.
    private static List<ClipV> ClipPlane(List<ClipV> poly, Vector3 axis, Vector3 origin, float limit, int cutFeat)
    {
        var outp = new List<ClipV>(poly.Count + 2);
        if (poly.Count == 0) return outp;
        bool keepBelow = limit >= 0f;                                // keepBelow: inside == f <= limit
        Func<float, bool> inside = f => keepBelow ? f <= limit : f >= limit;
        for (int i = 0; i < poly.Count; i++)
        {
            ClipV cur = poly[i], nxt = poly[(i + 1) % poly.Count];
            float fc = (cur.P - origin) * axis, fn = (nxt.P - origin) * axis;
            bool ic = inside(fc), inx = inside(fn);
            if (ic) outp.Add(cur);
            if (ic != inx)
            {
                float t = (limit - fc) / (fn - fc);                  // edge parameter at the plane crossing
                t = System.Math.Clamp(t, 0f, 1f);
                outp.Add(new ClipV { P = cur.P + (nxt.P - cur.P) * t, Feat = cutFeat });
            }
        }
        return outp;
    }

    // ================= Stage C2-3a/C2-3b: dynamic convex HULL contacts =================
    // Reuses the C2-1 hull (mass properties + polygonal Faces) and the C2-2 GJK/EPA queries. A hull vs a SPHERE is
    // a single analytic point; vs a BOX or another HULL it is a clipped multi-point manifold mirroring BoxVsBox
    // (stable Feature ids -> a jitter-free flat rest/stack). The generators are TRANSFORM-based (they use each
    // body's world Position/Orientation/HalfExtents/Hull), so the non-hull partner may be STATIC or DYNAMIC — the
    // solver mass-weights both bodies by their inverse masses. Roles: A = box/sphere/lower-Id-hull, B = hull; the
    // Normal is A->B. Hull-vs-mesh is a later stage (C2-4) and stays inert.

    // The OBB (box body) as a world-space support map: the farthest corner along dir.
    private static Gjk.SupportFn BoxSupport(RigidBody b) => dir =>
    {
        Vector3 local = ImpulseMath.RotateInv(b.Orientation, dir);
        Vector3 s = new Vector3(
            local.X >= 0f ? b.HalfExtents.X : -b.HalfExtents.X,
            local.Y >= 0f ? b.HalfExtents.Y : -b.HalfExtents.Y,
            local.Z >= 0f ? b.HalfExtents.Z : -b.HalfExtents.Z);
        return b.Position + ImpulseMath.Rotate(b.Orientation, s);
    };

    // A single point (a sphere's centre) as a degenerate support map (GJK.Distance then gives the closest point
    // on the OTHER shape to this point).
    private static Gjk.SupportFn PointSupport(Vector3 p) => _ => p;

    // HULL (B) vs SPHERE (A, static OR dynamic): closest point on the hull to the sphere centre via GJK distance;
    // a single contact. Normal A->B (sphere -> hull), mirroring BoxVsStaticSphere's sign. Penetration = radius −
    // distance; emitted only when Penetration > −margin (a resting/near contact). A sphere centre INSIDE the hull
    // (a deep overlap) is skipped here (the anti-fling rails handle that corner; a rest-on-sphere never hits it).
    public static bool HullVsSphere(RigidBody sphere, RigidBody hull, float margin, out Contact c)
    {
        c = null!;
        if (hull.Hull == null) return false;
        var hullS = Gjk.HullSupport(hull.Hull, hull.Position, hull.Orientation);
        var ptS = PointSupport(sphere.Position);
        if (!Gjk.Distance(hullS, ptS, out Vector3 cp, out _, out float dist)) return false;   // centre inside -> skip
        if (dist > sphere.Radius + margin) return false;
        Vector3 d = cp - sphere.Position;                          // sphere centre (A) -> hull closest point (B)
        Vector3 n = dist > 1e-6f ? d * (1f / dist) : new Vector3(0f, 1f, 0f);
        c = new Contact { A = sphere, B = hull, Point = cp, Normal = n, Penetration = sphere.Radius - dist, Feature = 0 };
        return true;
    }

    // A face reduced to a world polygon for clipping: an ordered boundary (each vertex + a stable feature id),
    // its outward unit normal, and a representative point on the plane.
    private struct FacePoly
    {
        public Vector3 N;                              // outward unit normal (world)
        public Vector3 Center;                         // a point on the face plane
        public List<(Vector3 P, int Feat)> V;          // ordered boundary vertices (world)
    }
    private const int CutFlag = 1 << 20;               // clip-cut Feature ids carry this flag + the ref-edge index

    // Build one OBB face (outward normal = axes[axis]·sign) as a 4-corner world polygon with corner feature ids 0..3.
    private static FacePoly BoxFace(RigidBody box, Vector3[] axes, float[] hExt, int axis, float sign)
    {
        Vector3 n = axes[axis] * sign;
        Vector3 center = box.Position + n * hExt[axis];
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        Vector3 u = axes[a1] * hExt[a1];
        Vector3 v = axes[a2] * hExt[a2];
        return new FacePoly
        {
            N = n, Center = center,
            V = new List<(Vector3, int)>(4)
            {
                (center + u + v, 0), (center - u + v, 1), (center - u - v, 2), (center + u - v, 3),
            },
        };
    }

    // Build a hull polygonal face as a world polygon. Feature ids = the hull VERTEX indices (stable across frames
    // as long as the same face is chosen -> warm-start catches).
    private static FacePoly HullFace(RigidBody hb, ConvexHull hull, int f)
    {
        var face = hull.Faces[f];
        Vector3 n = ImpulseMath.Rotate(hb.Orientation, face.Normal);
        var verts = new List<(Vector3, int)>(face.Loop.Length);
        Vector3 c = Vector3.Zero;
        foreach (int idx in face.Loop)
        {
            Vector3 w = hb.Position + ImpulseMath.Rotate(hb.Orientation, hull.Vertices[idx]);
            verts.Add((w, idx));
            c += w;
        }
        c = c / (float)face.Loop.Length;
        return new FacePoly { N = n, Center = c, V = verts };
    }

    // The hull face whose world outward normal is MOST aligned with `dir` (argmax of worldNormal·dir).
    private static int HullFaceTowards(RigidBody hb, ConvexHull hull, Vector3 dir, out Vector3 worldN)
    {
        var faces = hull.Faces;
        int best = 0; float bestDot = float.NegativeInfinity; Vector3 bn = new Vector3(0f, 1f, 0f);
        for (int f = 0; f < faces.Length; f++)
        {
            Vector3 wn = ImpulseMath.Rotate(hb.Orientation, faces[f].Normal);
            float d = wn * dir;
            if (d > bestDot) { bestDot = d; best = f; bn = wn; }
        }
        worldN = bn;
        return best;
    }

    // The OBB face whose outward normal is MOST aligned with world direction `dir` (over the 6 ±axis faces), as a
    // world polygon + its alignment score (for the reference/incident choice). Equivalent to argmax|axis·dir| with
    // the sign toward `dir`.
    private static (float score, FacePoly face) BestBoxFace(RigidBody box, Vector3[] axes, float[] hExt, Vector3 dir)
    {
        int axis = 0; float sign = 1f; float best = float.NegativeInfinity;
        for (int i = 0; i < 3; i++)
        {
            float d = axes[i] * dir;
            if (d > best) { best = d; axis = i; sign = 1f; }
            if (-d > best) { best = -d; axis = i; sign = -1f; }
        }
        return (best, BoxFace(box, axes, hExt, axis, sign));
    }

    // The hull face whose outward normal is MOST aligned with world direction `dir`, as a world polygon + score.
    private static (float score, FacePoly face) BestHullFace(RigidBody hb, ConvexHull hull, Vector3 dir)
    {
        int f = HullFaceTowards(hb, hull, dir, out Vector3 wn);
        return (wn * dir, HullFace(hb, hull, f));
    }

    // HULL (B) vs BOX (A, static OR dynamic): the CONTACT MANIFOLD, mirroring BoxVsBox. A thin wrapper over the
    // shared PolyManifold core (box OBB faces vs the hull's polygonal faces). Normal A->B (box -> hull).
    public static void HullVsBox(RigidBody boxA, RigidBody hullB, float margin, List<Contact> outList)
    {
        var hull = hullB.Hull;
        if (hull == null || hull.Faces.Length == 0) return;
        Vector3 aX = ImpulseMath.Rotate(boxA.Orientation, new Vector3(1f, 0f, 0f));
        Vector3 aY = ImpulseMath.Rotate(boxA.Orientation, new Vector3(0f, 1f, 0f));
        Vector3 aZ = ImpulseMath.Rotate(boxA.Orientation, new Vector3(0f, 0f, 1f));
        Vector3[] axes = { aX, aY, aZ };
        float[] hExt = { boxA.HalfExtents.X, boxA.HalfExtents.Y, boxA.HalfExtents.Z };
        PolyManifold(boxA, hullB, BoxSupport(boxA), Gjk.HullSupport(hull, hullB.Position, hullB.Orientation), margin,
            dir => BestBoxFace(boxA, axes, hExt, dir),
            dir => BestHullFace(hullB, hull, dir),
            outList);
    }

    // HULL (A) vs HULL (B), both dynamic (C2-3b): the CONTACT MANIFOLD, the SAME "EPA normal -> ref/incident
    // polygonal-face clip" core as HullVsBox — the only difference is that BOTH candidate face sets come from a
    // hull's ConvexHull.Faces (rather than a box's OBB faces). Normal A->B. Deterministic A/B (the caller orders by
    // Id) so the reference/incident choice + Feature ids are stable frame-to-frame -> a jitter-free hull stack.
    public static void HullVsHull(RigidBody a, RigidBody b, float margin, List<Contact> outList)
    {
        var ha = a.Hull; var hb = b.Hull;
        if (ha == null || hb == null || ha.Faces.Length == 0 || hb.Faces.Length == 0) return;
        PolyManifold(a, b, Gjk.HullSupport(ha, a.Position, a.Orientation), Gjk.HullSupport(hb, b.Position, b.Orientation), margin,
            dir => BestHullFace(a, ha, dir),
            dir => BestHullFace(b, hb, dir),
            outList);
    }

    // ================= Stage C2-4: dynamic convex HULL vs static real TRIANGLE MESH =================
    // HULL (B) vs static triangle MESH (A): the corner-sampling manifold of BoxVsMesh, but QUERIED with the HULL's
    // world vertices instead of a box's 8 corners — so a hull rests/slides on the TRUE surface (a ramp, a mesh floor)
    // rather than its AABB. Each hull vertex that penetrates a triangle emits ONE contact AT the vertex, carrying that
    // triangle's FACE normal (oriented toward the hull) + the depth below the plane; the hull-vertex INDEX is the
    // stable warm-start Feature id (the key to a jitter-free mesh rest — the same role the box corner index plays in
    // BoxVsMesh). Cheap per-triangle AABB cull; above MaxFaces it falls back to the mesh's OBB box view via HullVsBox
    // (identical policy to BoxVsMesh's BoxVsBox fallback). The per-vertex × per-triangle scan stays bounded because the
    // fallback takes ALL high-poly meshes (so the real-triangle path only ever scans a LOW-poly mesh) and the AABB cull
    // rejects far triangles — a hull's contacting face has few vertices near the surface, so no extra pruning is needed.
    public static void HullVsMesh(RigidBody mesh, RigidBody hull, float margin, List<Contact> outList)
    {
        var hp = hull.Hull;
        if (hp == null) return;
        var verts = mesh.MeshVerts!; var tris = mesh.MeshTris!;
        if (tris.Length / 3 > MaxFaces) { HullVsBox(mesh, hull, margin, outList); return; }   // high-poly -> OBB box view

        var lv = hp.Vertices;                                        // hull LOCAL vertices (COM-centred)
        int vc = lv.Length;
        if (vc == 0) return;
        Vector3 hc = hull.Position;                                  // hull world centroid (== COM) — the "toward" reference for the face normal
        Quat q = hull.Orientation;

        // hull WORLD vertices (feature id = the hull vertex index) + a query AABB for the per-triangle cull. Small
        // vertex counts stay on the stack; a large hull falls back to a heap array (never an unbounded stackalloc).
        const int StackVerts = 64;
        Span<Vector3> W = vc <= StackVerts ? stackalloc Vector3[vc] : new Vector3[vc];
        W[0] = hc + ImpulseMath.Rotate(q, lv[0]);
        Vector3 qmin = W[0], qmax = W[0];
        for (int i = 1; i < vc; i++)
        {
            Vector3 p = hc + ImpulseMath.Rotate(q, lv[i]);
            W[i] = p;
            qmin = new Vector3(MathF.Min(qmin.X, p.X), MathF.Min(qmin.Y, p.Y), MathF.Min(qmin.Z, p.Z));
            qmax = new Vector3(MathF.Max(qmax.X, p.X), MathF.Max(qmax.Y, p.Y), MathF.Max(qmax.Z, p.Z));
        }
        qmin -= new Vector3(margin, margin, margin); qmax += new Vector3(margin, margin, margin);

        Span<float> bestPen = vc <= StackVerts ? stackalloc float[vc] : new float[vc];
        Span<Vector3> bestN = vc <= StackVerts ? stackalloc Vector3[vc] : new Vector3[vc];
        Span<bool> found = vc <= StackVerts ? stackalloc bool[vc] : new bool[vc];
        for (int i = 0; i < vc; i++) { bestPen[i] = float.NegativeInfinity; found[i] = false; }

        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 v0 = verts[tris[t]], v1 = verts[tris[t + 1]], v2 = verts[tris[t + 2]];
            Vector3 tmin = new Vector3(MathF.Min(v0.X, MathF.Min(v1.X, v2.X)), MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)), MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)));
            Vector3 tmax = new Vector3(MathF.Max(v0.X, MathF.Max(v1.X, v2.X)), MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)), MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)));
            if (tmax.X < qmin.X || tmin.X > qmax.X || tmax.Y < qmin.Y || tmin.Y > qmax.Y || tmax.Z < qmin.Z || tmin.Z > qmax.Z) continue;   // cull

            Vector3 fn = Vector3.Cross(v1 - v0, v2 - v0); float fl = fn.Length();
            if (fl < 1e-9f) continue;                                // degenerate triangle
            fn *= 1f / fl;
            if (fn * (hc - v0) < 0f) fn = fn * -1f;                  // orient toward the hull (outward from the surface) — winding-independent

            for (int i = 0; i < vc; i++)
            {
                Vector3 p = W[i];
                float planeDist = (p - v0) * fn;                     // signed distance to the plane (>0 above, <0 penetrating)
                if (planeDist > margin || planeDist < -MaxCornerPen) continue;
                Vector3 cpt = CollisionMath.ClosestPointOnTriangle(p, v0, v1, v2);
                if ((p - fn * planeDist - cpt).Length() > InteriorEps) continue;   // p projects OUTSIDE the triangle -> not a face contact
                float pen = -planeDist;
                if (pen > bestPen[i]) { bestPen[i] = pen; bestN[i] = fn; found[i] = true; }   // deepest penetrating face for this vertex
            }
        }

        for (int i = 0; i < vc; i++)
            if (found[i])
                outList.Add(new Contact { A = mesh, B = hull, Point = W[i], Normal = bestN[i], Penetration = bestPen[i], Feature = i });
    }

    // Shared manifold core: EPA/GJK contact normal (A->B) → pick the reference face (the A- or B-face most parallel
    // to the normal) → clip the OTHER shape's incident face against it. `bestA`/`bestB` return each body's face most
    // aligned with a given world direction + its alignment score. The contact normal comes from GJK/EPA (overlap)
    // or GJK witnesses (a small gap -> a speculative contact, like BoxVsBox's inflated-A margin); each surviving
    // clipped point carries a penetration measured against the TRUE reference plane and a stable Feature id.
    private static void PolyManifold(
        RigidBody A, RigidBody B, Gjk.SupportFn supA, Gjk.SupportFn supB, float margin,
        Func<Vector3, (float score, FacePoly face)> bestA,
        Func<Vector3, (float score, FacePoly face)> bestB,
        List<Contact> outList)
    {
        // Contact normal (A->B) + a proximity gate.
        Vector3 abN;
        if (Gjk.Intersect(supA, supB))
        {
            if (!Gjk.Penetration(supA, supB, out Vector3 epaN, out _, out _)) return;
            abN = epaN * -1f;                                      // EPA points B->A; the solver normal is A->B
        }
        else
        {
            if (!Gjk.Distance(supA, supB, out Vector3 pA, out Vector3 pB, out float dist)) return;
            if (dist > margin) return;                             // beyond the speculative reach
            abN = pB - pA;                                         // A witness -> B witness
        }
        float nl = abN.Length();
        if (nl < 1e-6f) return;
        abN = abN * (1f / nl);

        // Reference = whichever candidate face (A's toward B, or B's toward A) is more parallel to the normal;
        // incident = the OTHER shape's face pointing back toward the reference (most anti-parallel to refN).
        var (alignA, faceA) = bestA(abN);                          // A's face toward B
        var (alignB, faceB) = bestB(abN * -1f);                    // B's face toward A
        FacePoly refF, incF;
        Vector3 contactN;                                          // A->B
        if (alignA >= alignB)                                      // A is the reference (the flat-rest/stack case)
        {
            refF = faceA;
            incF = bestB(refF.N * -1f).face;                       // B's face toward the reference
            contactN = refF.N;                                     // refN points A->B
        }
        else                                                       // B is the reference
        {
            refF = faceB;
            incF = bestA(refF.N * -1f).face;                       // A's face toward the reference
            contactN = refF.N * -1f;                               // refN points B->A; A->B = −refN
        }

        ClipManifold(refF, incF, A, B, contactN, margin, outList);
    }

    // Clip the incident world polygon against the reference face's side planes (each reference edge -> an inward
    // half-space) and emit ≤4 contacts. Penetration per point is measured against the TRUE reference plane along
    // refF.N (so a resting contact reads ~0 and a small gap reads negative — speculative). contactN is A->B.
    private static void ClipManifold(FacePoly refF, FacePoly incF, RigidBody A, RigidBody B, Vector3 contactN, float margin, List<Contact> outList)
    {
        Vector3 rN = refF.N;
        Vector3 rc = Vector3.Zero;
        foreach (var (p, _) in refF.V) rc += p;
        rc = rc / (float)refF.V.Count;

        var poly = new List<(Vector3 P, int Feat)>(incF.V);
        int m = refF.V.Count;
        for (int e = 0; e < m && poly.Count > 0; e++)
        {
            Vector3 a = refF.V[e].P, b = refF.V[(e + 1) % m].P;
            Vector3 inward = Vector3.Cross(rN, b - a);
            if (inward * (rc - a) < 0f) inward = inward * -1f;     // orient toward the face interior (winding-independent)
            float il = inward.Length();
            if (il < 1e-12f) continue;
            inward = inward * (1f / il);
            float limit = inward * a;                              // inside: p·inward >= limit
            poly = ClipHalfSpace(poly, inward, limit, CutFlag | e);
        }
        if (poly.Count == 0) return;

        var pts = new List<(Vector3 P, float Pen, int Feat)>(poly.Count);
        foreach (var (p, feat) in poly)
        {
            float sep = (p - refF.Center) * rN;                    // >0 above the ref face, <0 penetrating
            if (sep <= margin) pts.Add((p, -sep, feat));
        }
        if (pts.Count > 4) { pts.Sort((x, y) => y.Pen.CompareTo(x.Pen)); pts.RemoveRange(4, pts.Count - 4); }
        foreach (var (p, pen, feat) in pts)
            outList.Add(new Contact { A = A, B = B, Point = p, Normal = contactN, Penetration = pen, Feature = feat });
    }

    // Sutherland–Hodgman half-space clip keeping p·n >= limit. Cut vertices are tagged with 'cutFeat'.
    private static List<(Vector3 P, int Feat)> ClipHalfSpace(List<(Vector3 P, int Feat)> poly, Vector3 n, float limit, int cutFeat)
    {
        var outp = new List<(Vector3, int)>(poly.Count + 1);
        if (poly.Count == 0) return outp;
        for (int i = 0; i < poly.Count; i++)
        {
            var cur = poly[i];
            var nxt = poly[(i + 1) % poly.Count];
            float fc = cur.P * n - limit;                          // >=0 inside
            float fn = nxt.P * n - limit;
            bool ic = fc >= 0f, inx = fn >= 0f;
            if (ic) outp.Add(cur);
            if (ic != inx)
            {
                float t = fc / (fc - fn);
                t = System.Math.Clamp(t, 0f, 1f);
                outp.Add((cur.P + (nxt.P - cur.P) * t, cutFeat));
            }
        }
        return outp;
    }
}
