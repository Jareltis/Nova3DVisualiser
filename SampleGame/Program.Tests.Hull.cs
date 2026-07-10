using System;
using System.Collections.Generic;
using Nova3DVisualiser;
using SampleGame.Physics;

namespace SampleGame;

partial class Program
{
    // C2-1 self-test: the pure convex-hull geometry — quickhull (welding duplicates + filtering interior
    // points), the Euler/outward-winding invariants, the GJK support function, closed-polyhedron mass
    // properties (against the analytic box + tetrahedron anchors), and degenerate-input safety.
    static void HullSelfTest()
    {
        Console.WriteLine("=== HULL SELF-TEST (C2-1 convex hull + mass properties + support) ===");
        bool ok = true;

        // Count unique UNDIRECTED edges of a flattened triangle list (for the Euler check V − E + F = 2).
        static int EdgeCount(int[] tris)
        {
            var set = new HashSet<(int, int)>();
            for (int f = 0; f < tris.Length; f += 3)
            {
                void Add(int a, int b) => set.Add(a < b ? (a, b) : (b, a));
                Add(tris[f], tris[f + 1]); Add(tris[f + 1], tris[f + 2]); Add(tris[f + 2], tris[f]);
            }
            return set.Count;
        }
        // Hull centroid = the vertex average.
        static Vector3 Centroid(Vector3[] v)
        {
            Vector3 s = Vector3.Zero; foreach (var p in v) s += p; return v.Length > 0 ? s / (float)v.Length : Vector3.Zero;
        }
        // Every face's winding must give an OUTWARD normal: dot(normal, faceCentroid − hullCentroid) > 0.
        static bool AllFacesOutward(ConvexHull h)
        {
            Vector3 hc = Centroid(h.Vertices);
            for (int f = 0; f < h.Triangles.Length; f += 3)
            {
                Vector3 a = h.Vertices[h.Triangles[f]], b = h.Vertices[h.Triangles[f + 1]], c = h.Vertices[h.Triangles[f + 2]];
                Vector3 nn = Vector3.Cross(b - a, c - a);
                if (nn * (((a + b + c) / 3f) - hc) <= 0f) return false;
            }
            return true;
        }

        var cubeCorners = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1,  1), new Vector3(1, -1,  1), new Vector3(1, 1,  1), new Vector3(-1, 1,  1),
        };

        // 1) CUBE HULL — corners + duplicates + interior points → the exact 8-vertex / 12-face corner hull.
        ConvexHull? cube;
        {
            var input = new List<Vector3>(cubeCorners);
            input.Add(cubeCorners[0]); input.Add(cubeCorners[3]); input.Add(cubeCorners[6]); input.Add(cubeCorners[6]);  // duplicates
            input.Add(Vector3.Zero);
            input.Add(new Vector3(0.5f, 0.5f, 0.5f));
            input.Add(new Vector3(-0.3f, 0.2f, 0.7f));
            input.Add(new Vector3(0.1f, -0.6f, -0.4f));

            cube = ConvexHull.Build(input.ToArray());
            bool built = cube != null;
            int V = cube?.Vertices.Length ?? -1;
            int T = cube?.Triangles.Length ?? -1;
            int F = T / 3;
            int E = cube != null ? EdgeCount(cube.Triangles) : -1;
            bool euler = cube != null && (V - E + F) == 2 && E == 18;
            bool allIn = true;
            if (cube != null) foreach (var p in input) if (!cube.Contains(p)) { allIn = false; break; }
            bool outward = cube != null && AllFacesOutward(cube);
            bool good = built && V == 8 && T == 36 && euler && allIn && outward;
            Console.WriteLine($"  cube-hull: built={built}, V={V}(8), T={T}(36), edges={E}(18), euler(V-E+F)={(cube != null ? V - E + F : -1)}, allInputContained={allIn}, outward={outward} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) SUPPORT — argmax over the cube hull along assorted directions hits the expected corner/coordinate.
        if (cube != null)
        {
            Vector3 sPx = cube.Support(new Vector3(1, 0, 0));
            Vector3 sNx = cube.Support(new Vector3(-1, 0, 0));
            Vector3 sPy = cube.Support(new Vector3(0, 1, 0));
            Vector3 sNz = cube.Support(new Vector3(0, 0, -1));
            Vector3 sDiag = cube.Support(new Vector3(1, 1, 1).Norm());
            bool sok =
                MathF.Abs(sPx.X - 1f) < 1e-4f && MathF.Abs(sNx.X + 1f) < 1e-4f &&
                MathF.Abs(sPy.Y - 1f) < 1e-4f && MathF.Abs(sNz.Z + 1f) < 1e-4f &&
                MathF.Abs(sDiag.X - 1f) < 1e-4f && MathF.Abs(sDiag.Y - 1f) < 1e-4f && MathF.Abs(sDiag.Z - 1f) < 1e-4f;
            Console.WriteLine($"  support: +X.X={sPx.X:F1}(1), -X.X={sNx.X:F1}(-1), +Y.Y={sPy.Y:F1}(1), -Z.Z={sNz.Z:F1}(-1), diag=({sDiag.X:F1},{sDiag.Y:F1},{sDiag.Z:F1})(1,1,1) -> {(sok ? "ok" : "BAD")}");
            ok &= sok;
        }
        else ok = false;

        // 3) MASS — cube hull (mass 3): volume 8, COM at the origin, inertia == the analytic BoxInertia anchor.
        if (cube != null)
        {
            float vol = cube.Volume();
            cube.ComputeMassProperties(3f, out Vector3 com, out Vector3 inertia);
            Vector3 anchor = PhysicsMath.BoxInertia(3f, 2f, 2f, 2f);
            bool mok =
                MathF.Abs(vol - 8f) < 1e-3f &&
                com.Length() < 1e-3f &&
                MathF.Abs(inertia.X - anchor.X) < 1e-3f && MathF.Abs(inertia.Y - anchor.Y) < 1e-3f && MathF.Abs(inertia.Z - anchor.Z) < 1e-3f;
            Console.WriteLine($"  cube-mass: V={vol:F4}(8), COM=({com.X:F4},{com.Y:F4},{com.Z:F4})(0), inertia=({inertia.X:F4},{inertia.Y:F4},{inertia.Z:F4}) vs BoxInertia=({anchor.X:F2},{anchor.Y:F2},{anchor.Z:F2}) -> {(mok ? "ok" : "BAD")}");
            ok &= mok;
        }
        else ok = false;

        // 4) TETRAHEDRON — a regular tetra inscribed in the cube: 4 verts / 4 faces / Euler; volume matches the
        //    analytic |det|/6; COM = the vertex average.
        {
            var tv = new[] { new Vector3(1, 1, 1), new Vector3(1, -1, -1), new Vector3(-1, 1, -1), new Vector3(-1, -1, 1) };
            var tet = ConvexHull.Build(tv);
            float analyticVol = MathF.Abs((tv[1] - tv[0]) * Vector3.Cross(tv[2] - tv[0], tv[3] - tv[0])) / 6f;   // |det|/6
            Vector3 analyticCom = (tv[0] + tv[1] + tv[2] + tv[3]) / 4f;
            bool tok = tet != null;
            if (tet != null)
            {
                int V = tet.Vertices.Length, F = tet.Triangles.Length / 3, E = EdgeCount(tet.Triangles);
                float vol = tet.Volume();
                tet.ComputeMassProperties(1f, out Vector3 com, out _);
                tok = V == 4 && F == 4 && (V - E + F) == 2 && AllFacesOutward(tet)
                    && MathF.Abs(vol - analyticVol) < 1e-3f && (com - analyticCom).Length() < 1e-3f;
                Console.WriteLine($"  tetra: V={V}(4), F={F}(4), euler={(V - E + F)}, V={vol:F4} vs |det|/6={analyticVol:F4}, COM=({com.X:F3},{com.Y:F3},{com.Z:F3}) vs avg=({analyticCom.X:F3},{analyticCom.Y:F3},{analyticCom.Z:F3}) -> {(tok ? "ok" : "BAD")}");
            }
            else Console.WriteLine("  tetra: Build returned null -> BAD");
            ok &= tok;
        }

        // 5) INTERIOR FILTERING — cube corners + 50 FIXED pseudo-random interior points still hull to 8 vertices.
        {
            var rng = new Random(12345);
            var input = new List<Vector3>(cubeCorners);
            for (int i = 0; i < 50; i++)
                input.Add(new Vector3(
                    (float)(rng.NextDouble() * 1.8 - 0.9),   // strictly inside (−0.9..0.9)
                    (float)(rng.NextDouble() * 1.8 - 0.9),
                    (float)(rng.NextDouble() * 1.8 - 0.9)));
            var h = ConvexHull.Build(input.ToArray());
            bool fok = h != null && h.Vertices.Length == 8;
            Console.WriteLine($"  interior-filter: {input.Count} input pts (8 corners + 50 interior) -> hull verts={h?.Vertices.Length}(8) -> {(fok ? "ok" : "BAD")}");
            ok &= fok;
        }

        // 6) DEGENERATE — coplanar / too-few clouds must return null (no crash, no hang).
        {
            var coplanar = new[]
            {
                new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 3, 0),
                new Vector3(0, 3, 0), new Vector3(1, 1, 0), new Vector3(-1, 2, 0),
            };
            bool coNull = ConvexHull.Build(coplanar) == null;
            bool threeNull = ConvexHull.Build(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }) == null;
            bool twoNull = ConvexHull.Build(new[] { new Vector3(0, 0, 0), new Vector3(1, 1, 1) }) == null;
            bool oneNull = ConvexHull.Build(new[] { new Vector3(0, 0, 0) }) == null;
            bool zeroNull = ConvexHull.Build(Array.Empty<Vector3>()) == null;
            bool dok = coNull && threeNull && twoNull && oneNull && zeroNull;
            Console.WriteLine($"  degenerate: coplanar={coNull}, 3pts={threeNull}, 2pts={twoNull}, 1pt={oneNull}, 0pts={zeroNull} -> {(dok ? "ok" : "BAD")}");
            ok &= dok;
        }

        Console.WriteLine(ok ? "HULL TEST PASSED" : "HULL TEST FAILED");
    }

    // C2-2 self-test: GJK (separated distance + witness points, overlap boolean) and EPA (penetration
    // normal/depth with the shift-separates invariant), driven by convex-hull cubes at chosen transforms.
    static void GjkSelfTest()
    {
        Console.WriteLine("=== GJK SELF-TEST (C2-2 GJK distance/boolean + EPA penetration) ===");
        bool ok = true;

        // A [-h,h]³ cube hull.
        static ConvexHull Cube(float h)
        {
            var c = new List<Vector3>();
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        c.Add(new Vector3(sx * h, sy * h, sz * h));
            return ConvexHull.Build(c.ToArray())!;
        }
        var unit = Cube(1f);
        Gjk.SupportFn AtQ(ConvexHull hull, Vector3 pos, Quat q) => Gjk.HullSupport(hull, pos, q);
        Gjk.SupportFn At(ConvexHull hull, Vector3 pos) => Gjk.HullSupport(hull, pos, Quat.Identity);

        // 1) SEPARATED distance — axis-aligned + diagonal, with witness points on the facing features.
        {
            var a = At(unit, Vector3.Zero);
            var b = At(unit, new Vector3(5f, 0f, 0f));
            bool inter = Gjk.Intersect(a, b);
            bool sep = Gjk.Distance(a, b, out Vector3 pA, out Vector3 pB, out float dist);
            bool axis = !inter && sep && MathF.Abs(dist - 3f) < 1e-3f
                      && MathF.Abs(pA.X - 1f) < 1e-3f && MathF.Abs(pB.X - 4f) < 1e-3f;
            Console.WriteLine($"  sep-axis: intersect={inter}(F), dist={dist:F4}(3), pA.X={pA.X:F3}(1), pB.X={pB.X:F3}(4) -> {(axis ? "ok" : "BAD")}");
            ok &= axis;

            var bd = At(unit, new Vector3(4f, 4f, 0f));
            bool sepD = Gjk.Distance(a, bd, out Vector3 dA, out Vector3 dB, out float distD);
            float diag = 2f * MathF.Sqrt(2f);   // corner/edge-to-edge in XY (2,2)
            bool diagOk = sepD && MathF.Abs(distD - diag) < 1e-2f
                        && MathF.Abs(dA.X - 1f) < 1e-2f && MathF.Abs(dA.Y - 1f) < 1e-2f
                        && MathF.Abs(dB.X - 3f) < 1e-2f && MathF.Abs(dB.Y - 3f) < 1e-2f;
            Console.WriteLine($"  sep-diag: dist={distD:F4}(2√2={diag:F4}), pA=({dA.X:F2},{dA.Y:F2}), pB=({dB.X:F2},{dB.Y:F2}) -> {(diagOk ? "ok" : "BAD")}");
            ok &= diagOk;
        }

        // 2) OVERLAP / JUST-SEPARATED boolean.
        {
            var a = At(unit, Vector3.Zero);
            bool over = Gjk.Intersect(a, At(unit, new Vector3(1.5f, 0f, 0f)));    // overlap 0.5 on X
            bool just = Gjk.Intersect(a, At(unit, new Vector3(2.001f, 0f, 0f)));  // gap 0.001
            bool bok = over && !just;
            Console.WriteLine($"  boolean: overlap(1.5)={over}(T), just-sep(2.001)={just}(F) -> {(bok ? "ok" : "BAD")}");
            ok &= bok;
        }

        // 3) EPA on known overlaps — X and Y, normal axis-aligned + depth 0.5, and the SHIFT-SEPARATES invariant
        //    (move B by −(depth+margin)·normal per the documented sign convention → no longer intersecting).
        {
            const float margin = 1e-2f;
            var a = At(unit, Vector3.Zero);

            Vector3 posBx = new Vector3(1.5f, 0f, 0f);
            bool epaX = Gjk.Penetration(a, At(unit, posBx), out Vector3 nX, out float dX, out _);
            bool unitX = MathF.Abs(nX.Length() - 1f) < 1e-3f;
            bool axisX = MathF.Abs(MathF.Abs(nX.X) - 1f) < 1e-2f && MathF.Abs(nX.Y) < 1e-2f && MathF.Abs(nX.Z) < 1e-2f;
            bool depthX = MathF.Abs(dX - 0.5f) < 1e-2f;
            bool shiftX = !Gjk.Intersect(a, At(unit, posBx - nX * (dX + margin)));
            bool xok = epaX && unitX && axisX && depthX && shiftX;
            Console.WriteLine($"  epa-X: normal=({nX.X:F3},{nX.Y:F3},{nX.Z:F3}) |n|={nX.Length():F3}, depth={dX:F4}(0.5), shift-separates={shiftX} -> {(xok ? "ok" : "BAD")}");
            ok &= xok;

            Vector3 posBy = new Vector3(0f, 1.5f, 0f);
            bool epaY = Gjk.Penetration(a, At(unit, posBy), out Vector3 nY, out float dY, out _);
            bool axisY = MathF.Abs(MathF.Abs(nY.Y) - 1f) < 1e-2f && MathF.Abs(nY.X) < 1e-2f && MathF.Abs(nY.Z) < 1e-2f;
            bool depthY = MathF.Abs(dY - 0.5f) < 1e-2f;
            bool shiftY = !Gjk.Intersect(a, At(unit, posBy - nY * (dY + margin)));
            bool yok = epaY && MathF.Abs(nY.Length() - 1f) < 1e-3f && axisY && depthY && shiftY;
            Console.WriteLine($"  epa-Y: normal=({nY.X:F3},{nY.Y:F3},{nY.Z:F3}), depth={dY:F4}(0.5), shift-separates={shiftY} -> {(yok ? "ok" : "BAD")}");
            ok &= yok;
        }

        // 4) ROTATED overlap — a 45°-about-Z cube overlapping an axis-aligned one: sane unit normal, a positive
        //    depth below the pre-shift X-overlap, and shifting by depth·normal (+margin) separates them.
        {
            const float margin = 1e-2f;
            var a = At(unit, Vector3.Zero);
            var q = Quaternions.QuatFromEuler(new Vector3(0f, 0f, MathF.PI / 4f));
            Vector3 posB = new Vector3(1.5f, 0f, 0f);
            var b = AtQ(unit, posB, q);
            float preOverlap = 1f - (1.5f - MathF.Sqrt(2f));   // A's +X face (1) minus B's rotated −X extent
            bool epa = Gjk.Penetration(a, b, out Vector3 nR, out float dR, out _);
            bool sane = epa && MathF.Abs(nR.Length() - 1f) < 1e-3f && dR > 0f && dR < preOverlap + 1e-2f
                      && !float.IsNaN(dR) && !float.IsInfinity(dR);
            bool shift = !Gjk.Intersect(a, AtQ(unit, posB - nR * (dR + margin), q));
            bool rok = sane && shift;
            Console.WriteLine($"  epa-rot: |n|={nR.Length():F3}, depth={dR:F4} (<preOverlap {preOverlap:F3}), shift-separates={shift} -> {(rok ? "ok" : "BAD")}");
            ok &= rok;
        }

        // 5) ROBUSTNESS — exactly coincident cubes + deep containment: finite unit normal + positive depth, no
        //    NaN/hang; and a separated pair never reports a false intersection.
        {
            var a = At(unit, Vector3.Zero);
            bool coin = Gjk.Penetration(a, At(unit, Vector3.Zero), out Vector3 nC, out float dC, out _);
            bool coinOk = coin && !float.IsNaN(dC) && !float.IsInfinity(dC) && dC > 0f
                        && !float.IsNaN(nC.X) && MathF.Abs(nC.Length() - 1f) < 1e-3f;
            Console.WriteLine($"  robust-coincident: pen={coin}, |n|={nC.Length():F3}, depth={dC:F4} (finite>0) -> {(coinOk ? "ok" : "BAD")}");
            ok &= coinOk;

            var big = Cube(2f);
            var small = Cube(0.3f);
            bool cont = Gjk.Penetration(Gjk.HullSupport(big, Vector3.Zero, Quat.Identity), Gjk.HullSupport(small, Vector3.Zero, Quat.Identity), out Vector3 nD, out float dD, out _);
            bool contOk = cont && !float.IsNaN(dD) && !float.IsInfinity(dD) && dD > 0f && MathF.Abs(nD.Length() - 1f) < 1e-3f;
            Console.WriteLine($"  robust-contained: pen={cont}, |n|={nD.Length():F3}, depth={dD:F4} (finite>0) -> {(contOk ? "ok" : "BAD")}");
            ok &= contOk;

            bool falsePos = Gjk.Intersect(a, At(unit, new Vector3(10f, 0f, 0f)));
            Console.WriteLine($"  robust-nofalsepos: far-apart intersect={falsePos}(F) -> {(!falsePos ? "ok" : "BAD")}");
            ok &= !falsePos;
        }

        Console.WriteLine(ok ? "GJK TEST PASSED" : "GJK TEST FAILED");
    }

    // C2-3a/C2-3b self-test: a dynamic convex HULL as a real collider in the impulse solver. Built directly (no
    // scene): (C2-3a) a hull-cube rests FLAT + stable on a static box like an equivalent DynamicBox, settles onto a
    // static sphere without exploding/sinking, a tilted hull settles onto a face; (C2-3b) two hull-cubes STACK, a
    // hull rests on a DYNAMIC box, and a hull + a dynamic sphere collide + settle. A hull-FREE box stack is
    // unchanged (additive: impulsetest/collisiontest carry the full regression).
    static void HullPhysSelfTest()
    {
        Console.WriteLine("=== HULL PHYS SELF-TEST (C2-3a/C2-3b dynamic hull colliders) ===");
        bool ok = true;
        const float g = 9.8f;
        const float dt = 1f / 60f;

        // The 8 corners of a cube (half-extent h) centred at c.
        static Vector3[] CubeCorners(Vector3 c, float h)
        {
            var list = new List<Vector3>(8);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        list.Add(c + new Vector3(sx * h, sy * h, sz * h));
            return list.ToArray();
        }
        // Tilt (rad) of a body's local up-axis away from world vertical (0 = upright).
        static float Tilt(RigidBody b) { Vector3 up = ImpulseMath.Rotate(b.Orientation, new Vector3(0f, 1f, 0f)); return MathF.Acos(Math.Clamp(up.Y, -1f, 1f)); }
        // "Rests flat on SOME face" error for a cube: after a TUMBLE it may settle on any of its 6 faces, so its
        // local +Y need not be vertical (Tilt is then the wrong metric). A face-rest means ONE local axis is world-
        // vertical -> max(|localAxis·up|) ≈ 1; err = 1 − that (0 = perfectly axis-aligned/flat). (Its face-rest centre
        // height is ~0.5 vs ~0.707 on an edge / ~0.866 on a corner, so height + this err together prove a flat face-rest.)
        static float FaceAlignErr(RigidBody b)
        {
            Vector3 wx = ImpulseMath.Rotate(b.Orientation, new Vector3(1f, 0f, 0f));
            Vector3 wy = ImpulseMath.Rotate(b.Orientation, new Vector3(0f, 1f, 0f));
            Vector3 wz = ImpulseMath.Rotate(b.Orientation, new Vector3(0f, 0f, 1f));
            return 1f - MathF.Max(MathF.Abs(wx.Y), MathF.Max(MathF.Abs(wy.Y), MathF.Abs(wz.Y)));
        }
        // A static MESH body with a valid box view (its solid world AABB) — mirrors the runtime's BuildStaticMeshBody.
        // Kept LOW-poly (≤ MaxFaces) so HullVsMesh uses the REAL triangles; the box view is only what the high-poly
        // fallback would use (never exercised by these 2-triangle fixtures — and it's inert for a static mesh anyway,
        // since a static body's Position feeds no impulse). Included to honour the C2-4 fixture spec.
        static RigidBody MeshWithBoxView(Vector3[] verts, int[] tris)
        {
            var m = RigidBody.StaticMesh(verts, tris);
            Vector3 mn = verts[0], mx = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                mn = new Vector3(MathF.Min(mn.X, v.X), MathF.Min(mn.Y, v.Y), MathF.Min(mn.Z, v.Z));
                mx = new Vector3(MathF.Max(mx.X, v.X), MathF.Max(mx.Y, v.Y), MathF.Max(mx.Z, v.Z));
            }
            m.Position = (mn + mx) * 0.5f;
            m.HalfExtents = (mx - mn) * 0.5f;
            m.Orientation = Quat.Identity;
            m.BoxView = true;
            return m;
        }

        // 1) HULL RESTS FLAT ON A STATIC BOX — and its rest matches an equivalent DynamicBox.
        {
            var wh = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            wh.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));   // top at y=0
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 2f, 0f), 0.5f), 1f);
            wh.Bodies.Add(hull);
            for (int i = 0; i < 240; i++) wh.Step(dt);                          // ~4 s

            var wb = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            wb.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var box = RigidBody.DynamicBox(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 0.5f, 0.5f), Quat.Identity, 1f);
            wb.Bodies.Add(box);
            for (int i = 0; i < 240; i++) wb.Step(dt);

            float restV = hull.LinVel.Length() + hull.AngVel.Length();
            float pen = 0f - (hull.Position.Y - 0.5f);                          // floor top (0) − hull bottom
            bool settled = restV < 0.05f;
            bool onFloor = MathF.Abs(hull.Position.Y - 0.5f) < 0.05f;
            bool level = Tilt(hull) < 0.05f;
            bool tinyPen = MathF.Abs(pen) < 0.02f;
            bool matchesBox = MathF.Abs(hull.Position.Y - box.Position.Y) < 0.03f;
            bool good = settled && onFloor && level && tinyPen && matchesBox;
            Console.WriteLine($"  hull-on-box: y={hull.Position.Y:F4}(0.5), tilt={Tilt(hull):F4}, |v|+|w|={restV:F4}, pen={pen:F4}, box.y={box.Position.Y:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) HULL SETTLES ON A STATIC SPHERE — makes contact and comes to rest without exploding or sinking through.
        {
            const float R = 3f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticSphere(new Vector3(0f, -R, 0f), R));   // sphere top at y=0
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 2f, 0f), 0.5f), 1f);
            w.Bodies.Add(hull);
            float maxV = 0f;
            for (int i = 0; i < 360; i++) { w.Step(dt); maxV = MathF.Max(maxV, hull.LinVel.Length()); }
            float distC = (hull.Position - new Vector3(0f, -R, 0f)).Length();   // COM distance from the sphere centre
            bool bounded = MathF.Abs(hull.Position.X) < 5f && hull.Position.Y > 0.2f && hull.Position.Y < 10f && MathF.Abs(hull.Position.Z) < 5f;
            bool notSunk = distC > R - 0.05f;                                   // the hull didn't sink into the sphere
            bool calm = hull.LinVel.Length() < 0.2f && hull.AngVel.Length() < 0.6f;
            bool sane = maxV < 30f;                                             // never exploded en route
            bool good = bounded && notSunk && calm && sane;
            Console.WriteLine($"  hull-on-sphere: pos=({hull.Position.X:F3},{hull.Position.Y:F3},{hull.Position.Z:F3}), distFromCentre={distC:F3}(>= {R}), |v|={hull.LinVel.Length():F3}, |w|={hull.AngVel.Length():F3}, maxV={maxV:F2} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 3) TILTED HULL SETTLES FLAT on the static box (the manifold catches the face landing, no perpetual tumble).
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 2f, 0f), 0.5f), 1f);
            hull.Orientation = Quaternions.QuatFromEuler(new Vector3(0.15f, 0f, 0.1f));   // small initial tilt
            w.Bodies.Add(hull);
            for (int i = 0; i < 420; i++) w.Step(dt);                           // ~7 s
            float restV = hull.LinVel.Length() + hull.AngVel.Length();
            bool settled = restV < 0.05f;
            bool level = Tilt(hull) < 0.06f;
            bool onFloor = MathF.Abs(hull.Position.Y - 0.5f) < 0.06f;
            bool good = settled && level && onFloor;
            Console.WriteLine($"  tilted-hull: y={hull.Position.Y:F4}(0.5), tilt={Tilt(hull):F4}(<0.06), |v|+|w|={restV:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 4) HULL-ON-HULL STACK (the canonical dynamic-hull test): a static box floor + a bottom DynamicHull cube +
        //    a SECOND DynamicHull cube dropped onto it. Both settle; the top rests one cube-height above the bottom
        //    (centres ≈ 0.5 and 1.5); neither sinks into the other nor drifts; the stack stays axis-aligned.
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));   // top at y=0
            var lo = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 0.5f, 0f), 0.5f), 1f);
            var hi = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 1.55f, 0f), 0.5f), 1f);                       // small gap -> drops onto lo
            w.Bodies.Add(lo); w.Bodies.Add(hi);
            for (int i = 0; i < 300; i++) w.Step(dt);                           // ~5 s
            float vSum = lo.LinVel.Length() + lo.AngVel.Length() + hi.LinVel.Length() + hi.AngVel.Length();
            bool settled = vSum < 0.06f;
            bool loRests = MathF.Abs(lo.Position.Y - 0.5f) < 0.05f;
            bool hiRests = MathF.Abs(hi.Position.Y - 1.5f) < 0.06f;            // one cube-height above lo
            bool gap = MathF.Abs((hi.Position.Y - lo.Position.Y) - 1.0f) < 0.04f;   // no interpenetration/float apart
            bool noDrift = MathF.Abs(lo.Position.X) < 0.05f && MathF.Abs(lo.Position.Z) < 0.05f
                        && MathF.Abs(hi.Position.X) < 0.08f && MathF.Abs(hi.Position.Z) < 0.08f;
            bool aligned = Tilt(lo) < 0.05f && Tilt(hi) < 0.05f;
            bool good = settled && loRests && hiRests && gap && noDrift && aligned;
            Console.WriteLine($"  hull-stack: lo.y={lo.Position.Y:F4}(0.5), hi.y={hi.Position.Y:F4}(1.5), gap={hi.Position.Y - lo.Position.Y:F4}(1.0), tilt=({Tilt(lo):F3},{Tilt(hi):F3}), Σ|v|+|w|={vSum:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 5) HULL ON A DYNAMIC BOX: a DynamicHull cube resting on a DynamicBox (itself on the floor). Both settle,
        //    the hull rests one cube-height above the box, no sink/explosion.
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var box = RigidBody.DynamicBox(new Vector3(0f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0.5f), Quat.Identity, 1f);
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 1.55f, 0f), 0.5f), 1f);
            w.Bodies.Add(box); w.Bodies.Add(hull);
            for (int i = 0; i < 300; i++) w.Step(dt);
            float vSum = box.LinVel.Length() + box.AngVel.Length() + hull.LinVel.Length() + hull.AngVel.Length();
            bool settled = vSum < 0.06f;
            bool boxRests = MathF.Abs(box.Position.Y - 0.5f) < 0.05f;
            bool hullRests = MathF.Abs(hull.Position.Y - 1.5f) < 0.06f;
            bool aligned = Tilt(box) < 0.05f && Tilt(hull) < 0.05f;
            bool good = settled && boxRests && hullRests && aligned;
            Console.WriteLine($"  hull-on-dynbox: box.y={box.Position.Y:F4}(0.5), hull.y={hull.Position.Y:F4}(1.5), tilt=({Tilt(box):F3},{Tilt(hull):F3}), Σ|v|+|w|={vSum:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 6) HULL + DYNAMIC SPHERE: a dynamic sphere dropped onto a resting hull cube -> both settle, velocities
        //    bound, no interpenetration blow-up (the sphere rests on the hull's top face).
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0f, 0.5f, 0f), 0.5f), 1f);
            var ball = RigidBody.Sphere(new Vector3(0f, 1.9f, 0f), 0.4f, 1f);
            w.Bodies.Add(hull); w.Bodies.Add(ball);
            float maxV = 0f;
            for (int i = 0; i < 360; i++) { w.Step(dt); maxV = MathF.Max(maxV, ball.LinVel.Length() + hull.LinVel.Length()); }
            float vSum = hull.LinVel.Length() + hull.AngVel.Length() + ball.LinVel.Length() + ball.AngVel.Length();
            bool settled = vSum < 0.1f;
            bool hullRests = MathF.Abs(hull.Position.Y - 0.5f) < 0.06f;          // hull stays on the floor
            bool ballRests = MathF.Abs(ball.Position.Y - (1.0f + 0.4f)) < 0.08f; // ball centre ≈ hull top (1.0) + radius
            bool bounded = MathF.Abs(ball.Position.X) < 1.5f && MathF.Abs(hull.Position.X) < 0.2f;
            bool sane = maxV < 30f;
            bool good = settled && hullRests && ballRests && bounded && sane;
            Console.WriteLine($"  hull+sphere: hull.y={hull.Position.Y:F4}(0.5), ball.y={ball.Position.Y:F4}(1.4), Σ|v|+|w|={vSum:F4}, maxV={maxV:F2} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 7) NO-OP REGRESSION: a hull-free box stack settles unchanged (impulsetest/collisiontest carry the rest).
        {
            const float bh = 0.5f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var boxes = new List<RigidBody>();
            for (int k = 0; k < 3; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * 2f * bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f); boxes.Add(bx); w.Bodies.Add(bx); }
            for (int i = 0; i < 300; i++) w.Step(dt);
            bool stacked = true;
            for (int k = 0; k < 3; k++) if (MathF.Abs(boxes[k].Position.Y - (bh + k * 2f * bh)) > 0.05f) stacked = false;
            Console.WriteLine($"  box-stack-regression: y=({boxes[0].Position.Y:F3},{boxes[1].Position.Y:F3},{boxes[2].Position.Y:F3}) -> {(stacked ? "ok" : "BAD")}");
            ok &= stacked;
        }

        // ===== C2-4: a dynamic convex HULL vs a static real TRIANGLE MESH (rests/slides on the TRUE surface) =====

        // 8) HULL RESTS FLAT ON A MESH FLOOR — the REAL 2-triangle floor (not its flat AABB) rests the hull at the
        //    SAME height as a static BOX floor (a mesh floor and a box floor must rest a hull identically).
        {
            Vector3[] fverts = { new Vector3(-20f, 0f, -20f), new Vector3(-20f, 0f, 20f), new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, -20f) };
            int[] ftris = { 0, 1, 2, 0, 2, 3 };
            Vector3 drop = new Vector3(0.3f, 2f, 0.15f);                        // slight XZ offset so no corner lands exactly on the split diagonal

            var wm = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            wm.Bodies.Add(MeshWithBoxView(fverts, ftris));                      // real mesh floor, top at y=0
            var hullM = RigidBody.DynamicHull(CubeCorners(drop, 0.5f), 1f);
            wm.Bodies.Add(hullM);
            for (int i = 0; i < 240; i++) wm.Step(dt);                          // ~4 s

            var wb = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            wb.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));   // box floor, top at y=0
            var hullB = RigidBody.DynamicHull(CubeCorners(drop, 0.5f), 1f);
            wb.Bodies.Add(hullB);
            for (int i = 0; i < 240; i++) wb.Step(dt);

            float restV = hullM.LinVel.Length() + hullM.AngVel.Length();
            float pen = 0f - (hullM.Position.Y - 0.5f);                         // floor top (0) − hull bottom
            bool settled = restV < 0.05f;
            bool onFloor = MathF.Abs(hullM.Position.Y - 0.5f) < 0.05f;
            bool level = Tilt(hullM) < 0.05f;
            bool tinyPen = MathF.Abs(pen) < 0.02f;
            bool matchesBoxFloor = MathF.Abs(hullM.Position.Y - hullB.Position.Y) < 0.03f;
            bool good = settled && onFloor && level && tinyPen && matchesBoxFloor;
            Console.WriteLine($"  hull-on-mesh-floor: y={hullM.Position.Y:F4}(0.5), tilt={Tilt(hullM):F4}, |v|+|w|={restV:F4}, pen={pen:F4}, boxFloor.y={hullB.Position.Y:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 9) HULL ON A REAL MESH RAMP (true slope): rests on the REAL inclined triangle, NOT its flat AABB. HIGH μ
        //    settles/sticks on the face; LOW μ slides DOWN-slope (+X). At rest/while sliding the centre sits at the
        //    ramp's true surface height under the hull (well BELOW the flat-AABB top ≈ y=H+bh), proving the real
        //    triangle is used — assert the surface band + down-slope sign + no tunnel/explosion (not an exact pos).
        {
            const float bh = 0.5f, H = 5f, Xr = 7f, Zr = 5f;                    // slope 0.357, ~19.6°
            float theta = MathF.Atan(H / (2f * Xr));
            Vector3 nRamp = new Vector3(H / (2f * Xr), 1f, 0f); nRamp *= 1f / nRamp.Length();   // ramp face outward (up) normal
            var alignedQ = Quaternions.QuatFromEuler(new Vector3(0f, 0f, -theta));
            // ramp: HIGH (y=H) at x=-Xr, LOW (y=0) at x=+Xr, z∈[-Zr,Zr]; downhill = +X.
            RigidBody Ramp(float mu) { var r = RigidBody.StaticMesh(new[] { new Vector3(-Xr, H, -Zr), new Vector3(-Xr, H, Zr), new Vector3(Xr, 0f, Zr), new Vector3(Xr, 0f, -Zr) }, new[] { 0, 1, 2, 0, 2, 3 }); r.Friction = mu; return r; }
            float Surface(float x) => H * (Xr - x) / (2f * Xr);                  // ramp surface height at x

            foreach (var (mu, shouldStick) in new[] { (0.9f, true), (0.03f, false) })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));   // catch floor far below
                w.Bodies.Add(Ramp(mu));
                float sx0 = -3f;
                Vector3 start = new Vector3(sx0, Surface(sx0), 0f) + nRamp * (bh + 0.3f);   // aligned, just above the real face
                var hull = RigidBody.DynamicHull(CubeCorners(start, bh), 1f); hull.Orientation = alignedQ; hull.Friction = mu;
                w.Bodies.Add(hull);
                Vector3 startPos = hull.Position, prev = hull.Position; float maxStep = 0f, maxY = hull.Position.Y;
                int steps = (int)((shouldStick ? 3f : 1.5f) / dt);              // slide: a short window keeps the low-μ hull ON the ramp
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    float s = MathF.Sqrt((hull.Position.X - prev.X) * (hull.Position.X - prev.X) + (hull.Position.Z - prev.Z) * (hull.Position.Z - prev.Z));
                    if (s > maxStep) maxStep = s; prev = hull.Position;
                    if (hull.Position.Y > maxY) maxY = hull.Position.Y;
                }
                float aboveSurf = hull.Position.Y - Surface(hull.Position.X);   // centre height above the LOCAL real surface
                float dispX = hull.Position.X - startPos.X;
                float dispZ = hull.Position.Z - startPos.Z;
                bool onRealFace = aboveSurf > 0.2f && aboveSurf < 1.0f;         // on the REAL slope (not tunnelled, not floated to the AABB top)
                bool belowBboxTop = maxY < H + bh + 0.3f;                       // never rose to the flat-AABB rest (~H+bh) -> real triangle
                bool noTeleport = maxStep < 0.5f;
                bool bounded = MathF.Abs(hull.Position.X) < 30f && hull.Position.Y > -1f && hull.Position.Y < H + 2f;
                bool straight = MathF.Abs(dispZ) < 0.3f;                        // moved along the incline, not sideways
                bool motion = shouldStick
                    ? (MathF.Abs(dispX) < 0.3f && hull.LinVel.Length() < 0.15f) // sticks: barely moves + settles
                    : (dispX > 0.5f);                                          // slides DOWNHILL (+X sign)
                bool good = onRealFace && belowBboxTop && noTeleport && bounded && straight && motion;
                Console.WriteLine($"  hull-on-ramp mu={mu} ({(shouldStick ? "stick" : "slide")}): x {startPos.X:F2}->{hull.Position.X:F2} (dispX={dispX:F2},dispZ={dispZ:F2}), aboveSurf={aboveSurf:F3} (real face), maxY={maxY:F2} (< bbox {H + bh:F1}) -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 10) TUMBLE-TO-REST on a MESH floor: a hull dropped clearly OFF-face with a spin tumbles, then settles FLAT
        //     and at rest on the real triangles (a cube can't balance on an edge/corner — it lands on a face).
        {
            Vector3[] fverts = { new Vector3(-20f, 0f, -20f), new Vector3(-20f, 0f, 20f), new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, -20f) };
            int[] ftris = { 0, 1, 2, 0, 2, 3 };
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(MeshWithBoxView(fverts, ftris));
            var hull = RigidBody.DynamicHull(CubeCorners(new Vector3(0.2f, 2.2f, -0.15f), 0.5f), 1f);
            hull.Orientation = Quaternions.QuatFromEuler(new Vector3(0.6f, 0.2f, 0.4f));   // off-face tilt
            hull.AngVel = new Vector3(0.6f, 0.3f, -0.4f);                                  // initial spin -> tumbles as it lands
            w.Bodies.Add(hull);
            float startTilt = Tilt(hull), maxTilt = startTilt;
            for (int i = 0; i < 540; i++) { w.Step(dt); float t = Tilt(hull); if (t > maxTilt) maxTilt = t; }   // ~9 s
            float restV = hull.LinVel.Length() + hull.AngVel.Length();
            bool startedTilted = startTilt > 0.3f;                              // dropped clearly off-face (a genuine tumble)
            bool settled = restV < 0.06f;
            bool flat = FaceAlignErr(hull) < 0.02f;                             // settled FLAT on SOME face (axis-aligned), not on an edge/corner
            bool onFloor = MathF.Abs(hull.Position.Y - 0.5f) < 0.06f;           // face-rest height (0.5), not edge (~0.707)/corner (~0.866)
            bool bounded = MathF.Abs(hull.Position.X) < 5f && MathF.Abs(hull.Position.Z) < 5f;
            bool good = startedTilted && settled && flat && onFloor && bounded;
            Console.WriteLine($"  hull-tumble-mesh: startTilt={startTilt:F3}, maxTilt={maxTilt:F3}, y={hull.Position.Y:F4}(0.5), faceAlignErr={FaceAlignErr(hull):F5}(<0.02), |v|+|w|={restV:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 11) REGRESSION — a box-vs-mesh scene is UNCHANGED by the hull addition (HullVsMesh fires ONLY for Hull
        //     bodies; a box still takes BoxVsMesh). A box on the same flat mesh floor rests FLAT at 0.5 and SLEEPS,
        //     exactly as before (impulsetest/collisiontest carry the full regression — this is the cheap direct check).
        {
            Vector3[] fverts = { new Vector3(-20f, 0f, -20f), new Vector3(-20f, 0f, 20f), new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, -20f) };
            int[] ftris = { 0, 1, 2, 0, 2, 3 };
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticMesh(fverts, ftris));
            var box = RigidBody.DynamicBox(new Vector3(0.7f, 1f, -0.3f), new Vector3(0.5f, 0.5f, 0.5f), Quat.Identity, 1f);
            w.Bodies.Add(box);
            for (int i = 0; i < 400; i++) w.Step(dt);
            bool rested = MathF.Abs(box.Position.Y - 0.5f) < 0.01f;
            bool level = Tilt(box) < 0.02f;
            bool good = rested && level && box.Sleeping;
            Console.WriteLine($"  box-on-mesh-regression: y={box.Position.Y:F4}(0.5), tilt={Tilt(box):F5}, sleep={box.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        Console.WriteLine(ok ? "HULL PHYS TEST PASSED" : "HULL PHYS TEST FAILED");
    }
}
