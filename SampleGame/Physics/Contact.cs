using System;
using System.Collections.Generic;
using Nova3DVisualiser;
using SampleGame.Scenes;
using Quat = SampleGame.Scenes.PriviewNetworkScene.Quat;

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
// Pure + testable. Reuses PriviewNetworkScene.ClosestPointOnTriangle for the mesh case.
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
            Vector3 q = PriviewNetworkScene.ClosestPointOnTriangle(sph.Position, verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]]);
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
                Vector3 q = PriviewNetworkScene.ClosestPointOnTriangle(p, v0, v1, v2);
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

        var (hit, n, _) = PriviewNetworkScene.SatBox3D(A.Position, aX, aY, aZ, hAi, B.Position, bX, bY, bZ, hB);
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
}
