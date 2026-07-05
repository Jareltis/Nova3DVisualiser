using System.Net;
using System.Runtime.InteropServices;
using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Scenes;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.IO.Compression;
using System.Text.Json;

namespace SampleGame;

partial class Program
{
    // Physics/dynamics self-tests: collision, physics-math, impulse-solver + cutover, and the impulse-world test helper.

    // Headless check of the pure collision resolvers (the camera-bubble math): a sphere is ejected
    // out of an AABB / another sphere when penetrating, kept exactly tangent at the surface, and
    // returned UNCHANGED when clear. Mirrors what ResolveCameraCollision does each frame.
    static void CollisionSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== COLLISION SELF-TEST ===");

        const float eps = 1e-3f;
        bool ok = true;

        // Distance from a point to an AABB (0 when inside) — the penetration check.
        static float DistToAabb(Vector3 c, Vector3 min, Vector3 max)
        {
            Vector3 cl = new(Math.Clamp(c.X, min.X, max.X), Math.Clamp(c.Y, min.Y, max.Y), Math.Clamp(c.Z, min.Z, max.Z));
            return (c - cl).Length();
        }

        Vector3 bmin = new(-1f, -1f, -1f), bmax = new(1f, 1f, 1f);
        const float r = 0.35f;

        // 1) Centre INSIDE the box -> ejected until it no longer penetrates (dist from box >= r).
        {
            Vector3 outc = CollisionMath.ResolveSphereVsAabb(new Vector3(0f, 0f, 0f), r, bmin, bmax);
            float dist = DistToAabb(outc, bmin, bmax);
            bool t = dist >= r - eps;
            Console.WriteLine($"  aabb-centre: ejected to ({outc.X:F3},{outc.Y:F3},{outc.Z:F3}), dist-from-box={dist:F3} (want >= {r}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 2) Beside the +X face -> pushed EXACTLY to the surface; tangential (Y,Z) kept.
        {
            Vector3 inc = new(1.2f, 0.5f, -0.3f);
            Vector3 outc = CollisionMath.ResolveSphereVsAabb(inc, r, bmin, bmax);
            float dist = DistToAabb(outc, bmin, bmax);
            bool t = Math.Abs(dist - r) < eps && Math.Abs(outc.Y - inc.Y) < eps && Math.Abs(outc.Z - inc.Z) < eps && outc.X > inc.X;
            Console.WriteLine($"  aabb-face: ({inc.X},{inc.Y},{inc.Z}) -> ({outc.X:F3},{outc.Y:F3},{outc.Z:F3}), dist-from-box={dist:F3} (want {r}), tangential kept -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 3) Sphere-vs-sphere overlap -> centres end up exactly r+sr apart.
        {
            Vector3 center = new(0.5f, 0f, 0f); float sr = 0.5f; float rr = r + sr;
            Vector3 outc = CollisionMath.ResolveSphereVsSphere(new Vector3(0f, 0f, 0f), r, center, sr);
            float d = (outc - center).Length();
            bool t = Math.Abs(d - rr) < eps;
            Console.WriteLine($"  sphere-sphere: resolved centre-dist={d:F3} (want {rr}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 4) Non-penetrating inputs are returned UNCHANGED (both AABB + sphere resolvers).
        {
            Vector3 farc = new(5f, 0f, 0f);
            Vector3 a = CollisionMath.ResolveSphereVsAabb(farc, r, bmin, bmax);
            Vector3 b = CollisionMath.ResolveSphereVsSphere(farc, r, new Vector3(0f, 0f, 0f), 0.5f);
            bool t = a == farc && b == farc;
            Console.WriteLine($"  no-penetration: aabb->({a.X},{a.Y},{a.Z}), sphere->({b.X},{b.Y},{b.Z}) (want unchanged 5,0,0) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 5) OBB with identity axes == AABB (a non-rotated box must resolve identically to ResolveSphereVsAabb).
        {
            Vector3 ax = new(1f, 0f, 0f), ay = new(0f, 1f, 0f), az = new(0f, 0f, 1f), half = new(1f, 1f, 1f), center = new(0f, 0f, 0f);
            Vector3 inc = new(1.2f, 0.5f, -0.3f);
            Vector3 aabb = CollisionMath.ResolveSphereVsAabb(inc, r, bmin, bmax);
            Vector3 obb = CollisionMath.ResolveSphereVsObb(inc, r, center, ax, ay, az, half);
            bool t = (obb - aabb).Length() < eps;
            Console.WriteLine($"  obb-identity: obb=({obb.X:F3},{obb.Y:F3},{obb.Z:F3}) vs aabb=({aabb.X:F3},{aabb.Y:F3},{aabb.Z:F3}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 6) OBB in a ROTATED frame (unit box turned 45° in the XZ plane): a point just past its +X
        // local face is pushed out ALONG that local axis to face+r; a far point is returned unchanged.
        {
            const float s = 0.70710678f;                       // cos/sin 45°
            Vector3 ax = new(s, 0f, s), ay = new(0f, 1f, 0f), az = new(-s, 0f, s), half = new(1f, 1f, 1f), center = new(0f, 0f, 0f);
            Vector3 inc = ax * 1.1f;                            // local ex=1.1 (just past the face at 1.0), within r
            Vector3 outc = CollisionMath.ResolveSphereVsObb(inc, r, center, ax, ay, az, half);
            Vector3 want = ax * (1f + r);                       // pushed to face + r along the local X axis
            bool faceOk = (outc - want).Length() < eps;
            Vector3 farc = ax * (1f + r + 0.5f);                // clear of the face -> unchanged
            Vector3 outf = CollisionMath.ResolveSphereVsObb(farc, r, center, ax, ay, az, half);
            bool freeOk = (outf - farc).Length() < eps;
            Console.WriteLine($"  obb-rotated: face-push dist-from-want={(outc - want).Length():F4}, free-unchanged={freeOk} -> {(faceOk && freeOk ? "ok" : "BAD")}");
            ok &= faceOk && freeOk;
        }

        // 8) SatBox3D (OBB-OBB contact, full 3D — the manifold normal the impulse box-box solver uses): the
        // axis-aligned MTV (normal +X, depth 0.2); a vertical offset separates with a +Y normal; a 45°-about-Z
        // box still overlaps with a unit normal; a far pair reports no hit.
        {
            Vector3 hU = new(0.5f, 0.5f, 0.5f);
            Vector3 AX = new(1f, 0f, 0f), AY = new(0f, 1f, 0f), AZ = new(0f, 0f, 1f);
            var (b3hit, b3n, b3d) = CollisionMath.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0.8f, 0f, 0f), AX, AY, AZ, hU);
            bool b3Axis = b3hit && Math.Abs(b3n.X - 1f) < eps && Math.Abs(b3n.Y) < eps && Math.Abs(b3n.Z) < eps && Math.Abs(b3d - 0.2f) < eps;
            var (b3vh, b3vn, b3vd) = CollisionMath.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0f, 0.8f, 0f), AX, AY, AZ, hU);
            bool b3Vert = b3vh && Math.Abs(b3vn.Y - 1f) < eps && Math.Abs(b3vd - 0.2f) < eps;
            var (b3fh, _, _) = CollisionMath.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(3f, 0f, 0f), AX, AY, AZ, hU);
            const float q = 0.70710678f;
            Vector3 rAX = new(q, q, 0f), rAY = new(-q, q, 0f);     // axes rotated 45° about Z
            var (b3rh, b3rn, b3rd) = CollisionMath.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0.9f, 0f, 0f), rAX, rAY, AZ, hU);
            bool b3Rot = b3rh && b3rd > 0f && Math.Abs(b3rn.Length() - 1f) < eps;
            bool satOk = b3Axis && b3Vert && !b3fh && b3Rot;
            Console.WriteLine($"  sat-box3d: axis(n={b3n.X:F2} d={b3d:F2}), vert={b3Vert}, far={!b3fh}, rot={b3Rot} -> {(satOk ? "ok" : "BAD")}");
            ok &= satOk;
        }

        Console.WriteLine(ok ? "COLLISION TEST PASSED" : "COLLISION TEST FAILED");
    }

    // Headless check of the shared pure math the physics + networking still use: drift-free quaternion
    // orientation (Euler<->Quat round-trip, ω integration, unit-norm), shortest-arc LerpAngle + the
    // StepInterpolate dead-reckon/ease (network sync), CombineRestitution, and ClosestPointOnTriangle.
    // (The legacy decoupled-pass helpers this used to exercise were retired in Stage 7b; the object-
    // dynamics behaviour is now covered end-to-end by impulsetest.)
    static void PhysicsSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== PHYSICS SELF-TEST ===");

        const float eps = 1e-2f;
        bool ok = true;

        // 3f) Quaternion orientation: Euler -> Quat -> Euler round-trips (away from gimbal lock); a unit
        // angular velocity integrated for time T rotates by exactly ω·T (drift-free) and the quaternion
        // stays unit-norm even integrating about two axes (where naive Euler integration would drift).
        {
            Vector3 e = new(0.3f, 0.5f, -0.2f);
            var rt = Quaternions.EulerFromQuat(Quaternions.QuatFromEuler(e));
            bool rtOk = Math.Abs(rt.X - e.X) < 1e-3f && Math.Abs(rt.Y - e.Y) < 1e-3f && Math.Abs(rt.Z - e.Z) < 1e-3f;

            // integrate ω=(0,2,0) for T=0.5 -> yaw 1.0 rad.
            var q = Quat.Identity; float dt = 1f / 600f;
            for (int i = 0; i < 300; i++) q = Quaternions.IntegrateQuat(q, new Vector3(0f, 2f, 0f), dt);
            var eY = Quaternions.EulerFromQuat(q);
            bool spinOk = Math.Abs(eY.Y - 1.0f) < 1e-2f && Math.Abs(eY.X) < 1e-2f && Math.Abs(eY.Z) < 1e-2f;

            // two-axis integration must keep the quaternion unit-norm (drift-free).
            var q2 = Quat.Identity;
            for (int i = 0; i < 600; i++) q2 = Quaternions.IntegrateQuat(q2, new Vector3(1.5f, -1f, 0.7f), dt);
            float norm2 = MathF.Sqrt(q2.X * q2.X + q2.Y * q2.Y + q2.Z * q2.Z + q2.W * q2.W);
            bool normOk = Math.Abs(norm2 - 1f) < 1e-4f;

            bool quatOk = rtOk && spinOk && normOk;
            Console.WriteLine($"  quaternion: roundtrip={rtOk}, spinY={eY.Y:F3}(1.000), unit-norm={norm2:F5} -> {(quatOk ? "ok" : "BAD")}");
            ok &= quatOk;
        }

        // 3g) LerpAngle (rotation sync): eases along the SHORTEST arc — lerping from +3.0 toward -3.0
        // (≈ across the ±π seam) moves the SHORT way (magnitude grows past π / wraps), not back through 0.
        {
            float half = NetInterp.LerpAngle(3.0f, -3.0f, 0.5f);   // shortest delta ≈ +0.283, half -> ~3.14
            float midNoWrap = 3.0f + (-3.0f - 3.0f) * 0.5f;                  // naive lerp would give 0 (the long way)
            bool shortArc = MathF.Abs(half) > 3.0f && Math.Abs(midNoWrap) < eps;   // short arc leaves |angle|>3; naive collapses to 0
            float plain = NetInterp.LerpAngle(0.2f, 0.8f, 0.5f);   // no wrap -> simple midpoint 0.5
            bool lerpOk = shortArc && Math.Abs(plain - 0.5f) < eps;
            Console.WriteLine($"  lerpangle: seam={half:F3}(|>3|), plain={plain:F3}(0.5) -> {(lerpOk ? "ok" : "BAD")}");
            ok &= lerpOk;
        }

        // 4) StepInterpolate (client position sync): with velY=0 the current position eases toward a
        // fixed target and converges (monotonic shrinking error); with velY<0 it dead-reckons the
        // ongoing fall, so the target keeps descending and the eased current tracks it downward.
        {
            float dt = 1f / 60f, rate = 12f;
            // converge: cur=(0,5,0) toward tgt=(0,0,0), no velocity.
            Vector3 cur = new(0f, 5f, 0f), tgt = new(0f, 0f, 0f);
            float prevErr = MathF.Abs(cur.Y - tgt.Y); bool monotonic = true;
            for (int i = 0; i < 240; i++)
            {
                (cur, tgt) = NetInterp.StepInterpolate(cur, tgt, Vector3.Zero, dt, rate);
                float err = MathF.Abs(cur.Y - tgt.Y);
                if (err > prevErr + 1e-6f) monotonic = false;
                prevErr = err;
            }
            bool conv = monotonic && MathF.Abs(cur.Y) < 1e-2f;
            Console.WriteLine($"  interp-converge: cur.Y={cur.Y:F4} (want ~0), monotonic={monotonic} -> {(conv ? "ok" : "BAD")}");
            ok &= conv;

            // dead-reckon: velY=-6, target starts at 0 and must descend ~velY*elapsed; cur tracks near it.
            Vector3 c2 = new(0f, 0f, 0f), t2 = new(0f, 0f, 0f);
            int steps = 120; float velY = -6f;
            for (int i = 0; i < steps; i++) (c2, t2) = NetInterp.StepInterpolate(c2, t2, new Vector3(0f, velY, 0f), dt, rate);
            float expected = velY * steps * dt;                 // target's extrapolated Y
            bool fell = t2.Y < -1e-2f && MathF.Abs(t2.Y - expected) < 1e-2f && MathF.Abs(c2.Y - t2.Y) < 0.5f;
            Console.WriteLine($"  interp-deadreckon: tgt.Y={t2.Y:F3} (want {expected:F3}), cur.Y={c2.Y:F3} (tracks) -> {(fell ? "ok" : "BAD")}");
            ok &= fell;
        }

        // 4b) COMBINE-RESTITUTION: a contact's bounciness is the geometric mean of the two bodies'
        // restitutions, so two elastic surfaces stay elastic, a dead one (0) kills the rebound, and a
        // negative ("inherit world") input clamps to 0 in the raw combine (callers resolve it first).
        {
            float c11 = PhysicsMath.CombineRestitution(1f, 1f);
            float c10 = PhysicsMath.CombineRestitution(1f, 0f);
            float c55 = PhysicsMath.CombineRestitution(0.5f, 0.5f);
            float cNeg = PhysicsMath.CombineRestitution(-1f, 0.5f);
            bool cok = MathF.Abs(c11 - 1f) < 1e-4f && c10 < 1e-4f && MathF.Abs(c55 - 0.5f) < 1e-4f && cNeg < 1e-4f;
            Console.WriteLine($"  combine-restitution: (1,1)={c11:F2} (1,0)={c10:F2} (.5,.5)={c55:F2} (-1,.5)={cNeg:F2} -> {(cok ? "ok" : "BAD")}");
            ok &= cok;
        }

        // 4c) CLOSEST-POINT-ON-TRIANGLE (the heart of real sphere-vs-mesh contact): a point above the face
        // projects straight down onto it; a point beyond a vertex clamps to that vertex; a point past an edge
        // clamps onto the edge. Triangle in the y=0 plane: A(0,0,0) B(1,0,0) C(0,0,1).
        {
            Vector3 a = new(0f, 0f, 0f), b = new(1f, 0f, 0f), cc = new(0f, 0f, 1f);
            Vector3 above = CollisionMath.ClosestPointOnTriangle(new Vector3(0.25f, 5f, 0.25f), a, b, cc);   // inside -> projects to (0.25,0,0.25)
            Vector3 vert = CollisionMath.ClosestPointOnTriangle(new Vector3(-2f, 0f, -2f), a, b, cc);        // beyond A -> A
            Vector3 edge = CollisionMath.ClosestPointOnTriangle(new Vector3(0.5f, 0f, -1f), a, b, cc);       // past AB -> (0.5,0,0)
            bool cptOk = MathF.Abs(above.X - 0.25f) < eps && MathF.Abs(above.Y) < eps && MathF.Abs(above.Z - 0.25f) < eps
                      && vert.Length() < eps
                      && MathF.Abs(edge.X - 0.5f) < eps && MathF.Abs(edge.Y) < eps && MathF.Abs(edge.Z) < eps;
            Console.WriteLine($"  closest-point: above=({above.X:F2},{above.Y:F2},{above.Z:F2}), vert=({vert.X:F2},{vert.Y:F2},{vert.Z:F2}), edge=({edge.X:F2},{edge.Y:F2},{edge.Z:F2}) -> {(cptOk ? "ok" : "BAD")}");
            ok &= cptOk;
        }

        Console.WriteLine(ok ? "PHYSICS TEST PASSED" : "PHYSICS TEST FAILED");
    }

    // Headless check of the STAGE-1 impulse solver (SampleGame/Physics/, see PHYSICS.md): drop a dynamic
    // sphere onto a STATIC ground box across a dt sweep and assert the acceptance criteria — settles at rest
    // WITHOUT sinking, bounces with restitution whose peaks strictly DECAY (no energy gain) and never exceed
    // the drop height, stays bounded + frame-rate independent, and SLEEPS. (No CCD — a fast impact at COARSE
    // dt may transiently penetrate for one substep before the solver pushes it out; it must still not sink
    // THROUGH and must rest within slop. Strict sub-slop penetration is asserted at fine dt.)
    static void ImpulseSelfTest()
    {
        Console.WriteLine("=== IMPULSE SELF-TEST (Stage 1: sphere/static; Stage 2: friction+box/static; Stage 3: box-box stacks; Stage 4: sphere-box + sphere-sphere) ===");
        bool ok = true;
        const float R = 0.5f, restY = 0.5f, dropY = 5f, g = 9.8f, slop = 0.02f;

        // Tilt (rad) of a body's local up-axis away from world vertical — 0 = perfectly level.
        static float BoxTilt(RigidBody b) { Vector3 up = ImpulseMath.Rotate(b.Orientation, new Vector3(0f, 1f, 0f)); return MathF.Acos(Math.Clamp(up.Y, -1f, 1f)); }

        // 1) REST + NO SINK-THROUGH + FRAME-RATE INDEPENDENCE (restitution 0). Across a dt sweep the sphere
        //    settles at ~restY, never sinks THROUGH the ground, comes to rest, stays put, and sleeps.
        foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 15f, 1f / 8f, 1f / 4f, 1f / 2f, 1f / 1f })
        {
            var w = MakeImpulseWorld(0f, g, R, dropY, out var ball);
            float maxPen = 0f;
            int steps = (int)(8f / dt);
            for (int i = 0; i < steps; i++)
            {
                w.Step(dt);
                float pen = restY - ball.Position.Y;              // > 0 = below rest (sunk)
                if (pen > maxPen) maxPen = pen;
            }
            // last-window stability: a few more steps must not move it (it's asleep).
            float y0 = ball.Position.Y, maxJit = 0f;
            for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, MathF.Abs(ball.Position.Y - y0)); }

            bool rested = MathF.Abs(ball.Position.Y - restY) < slop;      // frame-rate INDEPENDENT rest height
            bool noFallThrough = ball.Position.Y > -slop;                 // ended ON the ground, never sank below / out the bottom
            bool subSlop = dt > 1f / 8f + 1e-4f || maxPen < slop;         // STRICT sub-slop penetration at fine dt (no CCD at coarse dt)
            bool recovered = maxPen < 1.5f;                               // a coarse-dt impact may transiently penetrate, but bounded + it recovered to rest
            bool still = maxJit < 1e-4f;                                  // frozen (asleep)
            bool bounded = MathF.Abs(ball.Position.X) < 1f && MathF.Abs(ball.Position.Z) < 1f && ball.Position.Y < dropY + 1f;
            bool good = rested && noFallThrough && subSlop && recovered && still && ball.Sleeping && bounded;
            Console.WriteLine($"  rest dt=1/{1f / dt:F0}: restY={ball.Position.Y:F4} (want {restY}), maxPen={maxPen:F4}{(maxPen >= slop ? " (coarse-dt transient, no CCD)" : "")}, jitter={maxJit:F6}, sleep={ball.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) RESTITUTION (e = 0.5) — NO ENERGY GAIN: the sphere bounces; successive APEX heights strictly
        //    decrease and never exceed the drop height; then it settles and sleeps.
        {
            float dt = 1f / 120f;                                  // fine dt so apex sampling is clean
            var w = MakeImpulseWorld(0.5f, g, R, dropY, out var ball);
            var apex = new List<float>();
            int steps = (int)(12f / dt);
            for (int i = 0; i < steps; i++)
            {
                float vyBefore = ball.LinVel.Y;
                w.Step(dt);
                if (vyBefore > 0f && ball.LinVel.Y <= 0f && ball.Position.Y > restY + 0.02f) apex.Add(ball.Position.Y);   // rising -> apex
            }
            bool anyBounce = apex.Count >= 2;
            bool underDrop = apex.Count > 0 && apex[0] < dropY;
            bool decaying = true;
            for (int k = 1; k < apex.Count; k++) if (apex[k] >= apex[k - 1] - 1e-4f) decaying = false;
            bool settled = MathF.Abs(ball.Position.Y - restY) < slop && ball.Sleeping;
            bool good = anyBounce && underDrop && decaying && settled;
            Console.WriteLine($"  restitution e=0.5: apexes=[{string.Join(",", apex.ConvertAll(a => a.ToString("F2")))}] (decaying + < {dropY}, then rest+sleep) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 3) BOX REST (dt sweep): a box dropped flat onto static ground SETTLES FLAT (tilt ~0), no jitter/drift,
        //    penetration <= slop (at fine dt), rests at ~half above the ground, and SLEEPS. Frame-rate indep.
        {
            const float bh = 0.5f, boxDrop = 1f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 15f, 1f / 8f, 1f / 4f, 1f / 2f, 1f / 1f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var box = RigidBody.DynamicBox(new Vector3(0f, boxDrop, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f);
                w.Bodies.Add(box);
                float maxPen = 0f;
                int steps = (int)(8f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); float pen = bh - box.Position.Y; if (pen > maxPen) maxPen = pen; }
                Vector3 p0 = box.Position; float maxJit = 0f, maxTilt = 0f;
                for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, (box.Position - p0).Length()); maxTilt = MathF.Max(maxTilt, BoxTilt(box)); }

                bool rested = MathF.Abs(box.Position.Y - bh) < slop;
                bool flat = maxTilt < 0.02f;                              // stayed level (tilt < ~1.1°)
                bool subSlop = dt > 1f / 8f + 1e-4f || maxPen < slop;     // strict sub-slop penetration at fine dt (no CCD at coarse dt)
                bool recovered = maxPen < 1.5f;
                bool still = maxJit < 1e-4f;                              // frozen (asleep)
                bool bounded = MathF.Abs(box.Position.X) < 1f && MathF.Abs(box.Position.Z) < 1f;
                bool good = rested && flat && subSlop && recovered && still && box.Sleeping && bounded;
                Console.WriteLine($"  box-rest dt=1/{1f / dt:F0}: restY={box.Position.Y:F4} (want {bh}), tilt={maxTilt:F5}, maxPen={maxPen:F4}{(maxPen >= slop ? " (coarse-dt transient)" : "")}, jitter={maxJit:F6}, sleep={box.Sleeping} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 4) BOX FRICTION ON AN INCLINE (two μ): a box resting on a static OBB tilted ~11.5° (NOT the legacy
        //    ramp — a plain tilted static box). HIGH μ -> the box STICKS (no slide); LOW μ -> it slides downhill
        //    with a BOUNDED speed (friction caps the acceleration; it never runs away). Swept over frame times.
        {
            float theta = 0.2f;                                          // incline angle (rad); tan θ ≈ 0.203
            Quat tilt = Quaternions.QuatFromEuler(new Vector3(0f, 0f, theta));
            Vector3 topN = ImpulseMath.Rotate(tilt, new Vector3(0f, 1f, 0f));   // (-sinθ, cosθ, 0): downhill is -X
            Vector3 groundHalf = new Vector3(10f, 0.5f, 10f);
            Vector3 topFaceCenter = topN * groundHalf.Y;
            foreach (var (mu, shouldStick) in new[] { (0.8f, true), (0.05f, false) })
            {
                bool allDt = true; float lastDisp = 0f, lastSpeed = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    var ground = RigidBody.StaticBox(new Vector3(0f, 0f, 0f), groundHalf, tilt); ground.Friction = mu; w.Bodies.Add(ground);
                    Vector3 start = topFaceCenter + topN * (0.5f + 0.005f);
                    var box = RigidBody.DynamicBox(start, new Vector3(0.5f, 0.5f, 0.5f), tilt, 1f); box.Friction = mu; w.Bodies.Add(box);
                    int steps = (int)(3f / dt); float maxSpeed = 0f;
                    for (int i = 0; i < steps; i++) { w.Step(dt); float sp = box.LinVel.Length(); if (sp > maxSpeed) maxSpeed = sp; }
                    float disp = (box.Position - start).Length();
                    bool downhill = box.Position.X < start.X - 1e-3f;    // slid toward -X (downhill)
                    bool bounded = box.Position.Y > -5f && MathF.Abs(box.Position.X) < 12f && maxSpeed < 15f;
                    bool good = shouldStick ? (disp < 0.1f && bounded) : (disp > 0.3f && downhill && bounded);
                    allDt &= good; lastDisp = disp; lastSpeed = maxSpeed;
                    if (!good) Console.WriteLine($"    [detail] mu={mu} dt=1/{1f / dt:F0}: disp={disp:F3}, maxSpeed={maxSpeed:F2}, endX={box.Position.X:F2}");
                }
                Console.WriteLine($"  box-incline mu={mu} ({(shouldStick ? "stick" : "slide")}): disp={lastDisp:F3}, maxSpeed={lastSpeed:F2} -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 5) FRICTION NO CREEP: a box AND a sphere placed at rest on level ground (μ>0, zero initial velocity)
        //    must NOT drift horizontally over many steps (friction cancels any numerical tangential velocity).
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var box = RigidBody.DynamicBox(new Vector3(2f, 0.5f, -1f), new Vector3(0.5f, 0.5f, 0.5f), Quat.Identity, 1f);
            w.Bodies.Add(box);
            var sph = RigidBody.Sphere(new Vector3(-2f, 0.5f, 1f), 0.5f, 1f);
            w.Bodies.Add(sph);
            Vector3 bx0 = box.Position, sp0 = sph.Position;
            for (int i = 0; i < 600; i++) w.Step(1f / 60f);
            float boxDrift = MathF.Sqrt((box.Position.X - bx0.X) * (box.Position.X - bx0.X) + (box.Position.Z - bx0.Z) * (box.Position.Z - bx0.Z));
            float sphDrift = MathF.Sqrt((sph.Position.X - sp0.X) * (sph.Position.X - sp0.X) + (sph.Position.Z - sp0.Z) * (sph.Position.Z - sp0.Z));
            bool good = boxDrift < 1e-3f && sphDrift < 1e-3f;
            Console.WriteLine($"  friction-no-creep: box drift={boxDrift:F6}, sphere drift={sphDrift:F6} (want ~0) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // Largest penetration across a vertical stack of boxes (each half bh) on ground top y=0: the box0↔ground
        // interface (0 − (Y−bh) = bh − Y) plus every box(k-1)↔box(k) interface. > 0 means overlapping.
        static float StackMaxPen(List<RigidBody> st, float bh)
        {
            float m = bh - st[0].Position.Y;                                              // ground (top y=0) vs box0 bottom
            for (int k = 1; k < st.Count; k++) m = MathF.Max(m, (st[k - 1].Position.Y + bh) - (st[k].Position.Y - bh));
            return m;
        }

        // 6) STACK STABILITY (the headline): 4 aligned dynamic boxes stacked on static ground must stay UPRIGHT
        //    (tilt ~0), NOT sink (settled inter-box penetration <= slop), NOT drift, SETTLE and SLEEP, and stay
        //    bounded — across a realistic dt sweep. Coarse 1/1 asserts only bounded + recovered (no CCD).
        {
            const float bh = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f, 1f / 1f })
            {
                bool coarse = dt > 1f / 20f + 1e-4f;
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var st = new List<RigidBody>();
                for (int k = 0; k < 4; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * (2f * bh), 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f); st.Add(bx); w.Bodies.Add(bx); }

                float maxTilt = 0f, maxDrift = 0f, maxPen = 0f;
                int steps = (int)(10f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    for (int k = 0; k < 4; k++) maxTilt = MathF.Max(maxTilt, BoxTilt(st[k]));
                    maxDrift = MathF.Max(maxDrift, MathF.Max(MathF.Abs(st[3].Position.X), MathF.Abs(st[3].Position.Z)));   // top box XZ deviation from centre
                    maxPen = MathF.Max(maxPen, StackMaxPen(st, bh));
                }
                float finalPen = StackMaxPen(st, bh);
                bool allSleep = st.All(b => b.Sleeping);
                bool upright = maxTilt < 0.02f;
                bool noSink = finalPen < slop;                                            // settled interfaces within slop
                bool noDrift = maxDrift < 0.05f;
                bool boundedFine = st.All(b => MathF.Abs(b.Position.X) < 2f && MathF.Abs(b.Position.Z) < 2f && b.Position.Y > 0f && b.Position.Y < 5f) && maxPen < 0.6f;
                bool boundedCoarse = st.All(b => MathF.Abs(b.Position.X) < 15f && MathF.Abs(b.Position.Z) < 15f && b.Position.Y > -1f && b.Position.Y < 10f);   // no fling (a 1-FPS stack may collapse into a heap, per the no-CCD caveat)
                bool good = coarse ? boundedCoarse : (upright && noSink && noDrift && allSleep && boundedFine);
                Console.WriteLine($"  stack-stability dt=1/{1f / dt:F0}: maxTilt={maxTilt:F5}, topDrift={maxDrift:F4}, finalPen={finalPen:F4} (transient {maxPen:F3}), sleep={allSleep} -> {(good ? "ok" : "BAD")}{(coarse ? " (coarse: bounded-only)" : "")}");
                ok &= good;
            }
        }

        // 7) BOX-ON-BOX REST: one dynamic box resting on another (bottom on static ground) rests FLAT and STILL
        //    — no jitter/drift, penetration <= slop, both SLEEP.
        {
            const float bh = 0.5f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var lo = RigidBody.DynamicBox(new Vector3(0f, bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f);
            var hi = RigidBody.DynamicBox(new Vector3(0f, 3f * bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f);
            var st = new List<RigidBody> { lo, hi }; w.Bodies.Add(lo); w.Bodies.Add(hi);
            for (int i = 0; i < 400; i++) w.Step(1f / 60f);
            Vector3 pLo = lo.Position, pHi = hi.Position; float maxJit = 0f, maxTilt = 0f;
            for (int i = 0; i < 120; i++) { w.Step(1f / 60f); maxJit = MathF.Max(maxJit, MathF.Max((lo.Position - pLo).Length(), (hi.Position - pHi).Length())); maxTilt = MathF.Max(maxTilt, MathF.Max(BoxTilt(lo), BoxTilt(hi))); }
            float pen = StackMaxPen(st, bh);
            bool good = pen < slop && maxJit < 1e-4f && maxTilt < 0.02f && lo.Sleeping && hi.Sleeping;
            Console.WriteLine($"  box-on-box-rest: pen={pen:F4}, jitter={maxJit:F6}, tilt={maxTilt:F5}, sleep={lo.Sleeping && hi.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 8) BOX-BOX MOMENTUM (mass-weighted, no energy gain): a box moving at +X strikes a resting box on
        //    frictionless level ground. Inelastic (restitution 0) -> they move together at v·mL/(mL+mR); a LIGHT
        //    box hitting a HEAVY one moves it LESS than an equal-mass one would. Momentum conserved, KE not gained.
        {
            const float bh = 0.5f, v0 = 3f;
            static (float rVel, float momA, float momB, float keA, float keB) HitTest(float mR, float g, float bh, float v0)
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                var ground = RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity); ground.Friction = 0f; w.Bodies.Add(ground);
                var L = RigidBody.DynamicBox(new Vector3(-1f, bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f); L.Friction = 0f; L.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(L);
                var Rb = RigidBody.DynamicBox(new Vector3(0.5f, bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, mR); Rb.Friction = 0f; w.Bodies.Add(Rb);
                float momA = 1f * v0;                                                     // initial X-momentum (only L moves)
                float keA = 0.5f * 1f * v0 * v0;
                for (int i = 0; i < 90; i++) w.Step(1f / 120f);                            // ~0.75 s: collide, then coast together
                float momB = 1f * L.LinVel.X + mR * Rb.LinVel.X;
                float keB = 0.5f * 1f * L.LinVel.X * L.LinVel.X + 0.5f * mR * Rb.LinVel.X * Rb.LinVel.X;
                return (Rb.LinVel.X, momA, momB, keA, keB);
            }
            var heavy = HitTest(5f, g, bh, v0);                                            // light L (m=1) hits heavy R (m=5)
            var equal = HitTest(1f, g, bh, v0);                                            // equal masses
            bool heavyMovesLess = heavy.rVel < equal.rVel - 0.05f && heavy.rVel > 0.01f;   // heavy R gains less speed, but does move
            bool momOk = MathF.Abs(heavy.momB - heavy.momA) < 0.15f && MathF.Abs(equal.momB - equal.momA) < 0.15f;
            bool noGain = heavy.keB <= heavy.keA + 1e-3f && equal.keB <= equal.keA + 1e-3f;
            bool good = heavyMovesLess && momOk && noGain;
            Console.WriteLine($"  box-box-momentum: heavyR vel={heavy.rVel:F3} < equalR vel={equal.rVel:F3} (mass-weighted), momentum {heavy.momA:F2}->{heavy.momB:F2}/{equal.momB:F2}, KE {heavy.keA:F2}->{heavy.keB:F2}/{equal.keB:F2} (no gain) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 9) SPHERE-ON-BOX (dynamic-vs-dynamic): a dynamic sphere dropped onto a dynamic box (box on static
        //    ground) rests on the box (mass-weighted), the pair SETTLES with penetration <= slop and SLEEPS,
        //    no energy gain — across the realistic sweep. (1/1 is omitted for this delicate 3-body VERTICAL
        //    stack: a ball on a thin box at 1 FPS can tunnel with no CCD — the RunawayBound backstop keeps it
        //    bounded, but a meaningful "rests" assertion needs a realistic frame time.)
        {
            const float bh = 0.5f, rad = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var box = RigidBody.DynamicBox(new Vector3(0f, bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f);
                w.Bodies.Add(box);
                var ball = RigidBody.Sphere(new Vector3(0f, box.Position.Y + bh + rad + 0.5f, 0f), rad, 1f);   // small centred drop onto the box top
                w.Bodies.Add(ball);
                float PenOf() { Vector3 cp = ContactGen.ClosestPointOnObb(box.Position, box.HalfExtents, box.Orientation, ball.Position); return MathF.Max(rad - (ball.Position - cp).Length(), bh - box.Position.Y); }
                float maxPen = 0f;
                int steps = (int)(10f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); maxPen = MathF.Max(maxPen, PenOf()); }
                Vector3 pBall = ball.Position, pBox = box.Position; float maxJit = 0f;
                for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, MathF.Max((ball.Position - pBall).Length(), (box.Position - pBox).Length())); }
                float finalPen = PenOf();
                bool rested = MathF.Abs(ball.Position.Y - (box.Position.Y + bh + rad)) < 2f * slop;   // ball sits box-top + rad
                bool noSink = finalPen < slop;
                bool still = maxJit < 1e-4f;
                bool asleep = ball.Sleeping && box.Sleeping;
                bool bounded = MathF.Abs(ball.Position.X) < 1f && MathF.Abs(ball.Position.Z) < 1f && ball.Position.Y > 0f && ball.Position.Y < 4f && maxPen < 0.6f;
                bool good = rested && noSink && still && asleep && bounded;
                Console.WriteLine($"  sphere-on-box dt=1/{1f / dt:F0}: ballY={ball.Position.Y:F4} (want {box.Position.Y + bh + rad:F3}), finalPen={finalPen:F4} (transient {maxPen:F3}), jitter={maxJit:F6}, sleep={asleep} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 10) SPHERE-SPHERE MOMENTUM (mass-weighted, no energy gain): a sphere moving at +X strikes a resting
        //     sphere on frictionless level ground. Inelastic (restitution 0) -> a LIGHT sphere moves a HEAVY one
        //     less than an equal-mass one would; momentum conserved, KE not gained.
        {
            const float rad = 0.5f, v0 = 3f;
            static (float rVel, float momA, float momB, float keA, float keB) HitSphere(float mR, float g, float rad, float v0)
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                var ground = RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity); ground.Friction = 0f; w.Bodies.Add(ground);
                var L = RigidBody.Sphere(new Vector3(-1f, rad, 0f), rad, 1f); L.Friction = 0f; L.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(L);
                var Rb = RigidBody.Sphere(new Vector3(0.5f, rad, 0f), rad, mR); Rb.Friction = 0f; w.Bodies.Add(Rb);
                for (int i = 0; i < 90; i++) w.Step(1f / 120f);
                return (Rb.LinVel.X, 1f * v0, 1f * L.LinVel.X + mR * Rb.LinVel.X, 0.5f * 1f * v0 * v0, 0.5f * 1f * L.LinVel.X * L.LinVel.X + 0.5f * mR * Rb.LinVel.X * Rb.LinVel.X);
            }
            var heavy = HitSphere(5f, g, rad, v0);
            var equal = HitSphere(1f, g, rad, v0);
            bool heavyMovesLess = heavy.rVel < equal.rVel - 0.05f && heavy.rVel > 0.01f;
            bool momOk = MathF.Abs(heavy.momB - heavy.momA) < 0.15f && MathF.Abs(equal.momB - equal.momA) < 0.15f;
            bool noGain = heavy.keB <= heavy.keA + 1e-3f && equal.keB <= equal.keA + 1e-3f;
            bool good = heavyMovesLess && momOk && noGain;
            Console.WriteLine($"  sphere-sphere-momentum: heavyR vel={heavy.rVel:F3} < equalR vel={equal.rVel:F3} (mass-weighted), momentum {heavy.momA:F2}->{heavy.momB:F2}/{equal.momB:F2}, KE {heavy.keA:F2}->{heavy.keB:F2}/{equal.keB:F2} (no gain) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 11) BALL SCATTERS STACK (the whole-system test): a fast dynamic sphere fired horizontally into a
        //     3-box stack. ASSERT the stack is DISTURBED, no horizontal momentum is INJECTED (friction/ground
        //     only remove it, so peak Σm·vx <= the ball's initial), no ENERGY is injected (peak Σ½m|v|² <=
        //     initial KE + convertible gravity PE), everything stays BOUNDED (no fling), and it SETTLES/sleeps.
        {
            const float bh = 0.5f, rad = 0.5f, v0 = 8f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f, 1f / 1f })
            {
                bool coarse = dt > 1f / 20f + 1e-4f;
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var boxes = new List<RigidBody>();
                for (int k = 0; k < 3; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * 2f * bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f); boxes.Add(bx); w.Bodies.Add(bx); }
                var ball = RigidBody.Sphere(new Vector3(-2.5f, 1.5f, 0f), rad, 1f); ball.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(ball);
                var all = new List<RigidBody>(boxes) { ball };
                Vector3[] start = boxes.Select(b => b.Position).ToArray();
                float initMomX = 1f * v0, initKE = 0.5f * 1f * v0 * v0;
                float gravPE = 1f * g * ball.Position.Y; foreach (var bx in boxes) gravPE += 1f * g * bx.Position.Y;
                float peakMomX = 0f, peakKE = 0f, maxDisp = 0f, maxCoord = 0f;
                int steps = (int)(15f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    float momX = 0f, ke = 0f;
                    foreach (var b in all) { momX += b.LinVel.X; ke += 0.5f * (b.LinVel * b.LinVel); maxCoord = MathF.Max(maxCoord, MathF.Max(MathF.Abs(b.Position.X), MathF.Abs(b.Position.Z))); }
                    peakMomX = MathF.Max(peakMomX, momX); peakKE = MathF.Max(peakKE, ke);
                    for (int k = 0; k < 3; k++) maxDisp = MathF.Max(maxDisp, (boxes[k].Position - start[k]).Length());
                }
                bool disturbed = maxDisp > 0.3f;
                bool momOk = peakMomX <= initMomX + 0.5f;                       // no horizontal momentum injection
                bool energyOk = peakKE <= initKE + gravPE + 5f;                 // no energy injection
                bool bounded = maxCoord < 20f && all.All(b => b.Position.Y > -1f && b.Position.Y < 15f);
                // The STACK settles to rest; the ball may keep rolling (rolling friction is Stage 6), so it's
                // not required to sleep — only to stay bounded (above).
                bool settled = boxes.All(b => b.LinVel.Length() < 0.15f && b.AngVel.Length() < 0.4f);
                bool good = coarse ? bounded : (disturbed && momOk && energyOk && bounded && settled);
                Console.WriteLine($"  ball-scatters-stack dt=1/{1f / dt:F0}: disturbed={maxDisp:F2}, peakMomX={peakMomX:F2}/<={initMomX:F1}, peakKE={peakKE:F1}/<={initKE + gravPE:F1}, maxCoord={maxCoord:F1}, stackAtRest={settled} -> {(good ? "ok" : "BAD")}{(coarse ? " (coarse: bounded-only)" : "")}");
                ok &= good;
            }
        }

        // ---- Stage 5 fixtures: real triangle meshes (winding-independent — BoxVsMesh orients the face normal
        //      toward the box, SphereVsMesh uses centre−closest). ----
        // Ramp: HIGH (y=H) at x=-Xr, LOW (y=0) at x=+Xr, spanning z∈[-Zr,Zr]; downhill is +X, slope = H/(2·Xr).
        static RigidBody Ramp(float H, float Xr, float Zr)
            => RigidBody.StaticMesh(new[] { new Vector3(-Xr, H, -Zr), new Vector3(-Xr, H, Zr), new Vector3(Xr, 0f, Zr), new Vector3(Xr, 0f, -Zr) }, new[] { 0, 1, 2, 0, 2, 3 });
        // Pyramid: apex (0,H,0), square base (±B,0,±B); 4 side faces.
        static RigidBody Pyramid(float H, float B)
            => RigidBody.StaticMesh(new[] { new Vector3(0f, H, 0f), new Vector3(-B, 0f, -B), new Vector3(B, 0f, -B), new Vector3(B, 0f, B), new Vector3(-B, 0f, B) }, new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 });

        // 12) SPHERE-ON-RAMP: a sphere dropped on the REAL ramp face rolls DOWN the face (not its bounding box),
        //     with NO teleport (no large single-substep horizontal jump — the original-bug guard), bounded.
        {
            const float rad = 0.5f, H = 4f, Xr = 6f, Zr = 4f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                w.Bodies.Add(Ramp(H, Xr, Zr));
                float sx = -Xr + 1.5f, sy = H * (Xr - sx) / (2f * Xr);
                var ball = RigidBody.Sphere(new Vector3(sx, sy + rad + 0.3f, 0f), rad, 1f); w.Bodies.Add(ball);
                float startX = ball.Position.X, maxStep = 0f; Vector3 prev = ball.Position;
                int steps = (int)(6f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((ball.Position.X - prev.X) * (ball.Position.X - prev.X) + (ball.Position.Z - prev.Z) * (ball.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = ball.Position; }
                bool rolled = ball.Position.X > startX + 1f;                     // moved downhill (+X) on the real face
                bool noTeleport = maxStep < 0.5f;                                // no large single-substep horizontal jump
                bool bounded = MathF.Abs(ball.Position.X) < 40f && MathF.Abs(ball.Position.Z) < 5f && ball.Position.Y > -1f && ball.Position.Y < H + 2f;
                bool good = rolled && noTeleport && bounded;
                Console.WriteLine($"  sphere-on-ramp dt=1/{1f / dt:F0}: rolled {startX:F2}->{ball.Position.X:F2} (downhill), maxStep={maxStep:F3} (no teleport), Y={ball.Position.Y:F2} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 13) BOX-ON-RAMP REST: a box on a SHALLOW ramp settles FLAT ALIGNED to the face (bottom face ≈ parallel
        //     to the ramp — the "non-perpendicular" fix). HIGH friction -> STICKS; LOW friction -> SLIDES with a
        //     bounded speed (still aligned). No teleport.
        {
            const float bh = 0.5f, H = 2f, Xr = 6f, Zr = 4f;                    // slope 0.167, ~9.5°
            float theta = MathF.Atan(H / (2f * Xr));
            Vector3 nRamp = new Vector3(H / (2f * Xr), 1f, 0f); nRamp *= 1f / nRamp.Length();
            var alignedQ = Quaternions.QuatFromEuler(new Vector3(0f, 0f, -theta));
            foreach (var (mu, shouldStick) in new[] { (0.8f, true), (0.03f, false) })
            {
                bool allDt = true; float lastTilt = 0f, lastDisp = 0f, lastSpeed = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                    var ramp = Ramp(H, Xr, Zr); ramp.Friction = mu; w.Bodies.Add(ramp);
                    float sx0 = -1f, sy0 = H * (Xr - sx0) / (2f * Xr);
                    Vector3 start = new Vector3(sx0, sy0, 0f) + nRamp * (bh + 0.02f);
                    var box = RigidBody.DynamicBox(start, new Vector3(bh, bh, bh), alignedQ, 1f); box.Friction = mu; w.Bodies.Add(box);
                    Vector3 startPos = box.Position, prev = box.Position; float maxStep = 0f;
                    int steps = (int)(2f / dt);   // measured while still ON the ramp (a low-μ box slides several units; keep it on the slope)
                    for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((box.Position.X - prev.X) * (box.Position.X - prev.X) + (box.Position.Z - prev.Z) * (box.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = box.Position; }
                    Vector3 boxUp = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                    float tiltVsFace = MathF.Acos(Math.Clamp(boxUp * nRamp, -1f, 1f));
                    float disp = MathF.Sqrt((box.Position.X - startPos.X) * (box.Position.X - startPos.X) + (box.Position.Z - startPos.Z) * (box.Position.Z - startPos.Z));
                    bool aligned = tiltVsFace < 0.06f;                          // bottom face parallel to the ramp (not perpendicular)
                    bool noTeleport = maxStep < 0.5f;
                    bool bounded = MathF.Abs(box.Position.X) < 20f && box.Position.Y > -1f;
                    bool motion = shouldStick ? disp < 0.15f : (disp > 0.3f && box.LinVel.Length() < 15f);
                    allDt &= aligned && noTeleport && bounded && motion; lastTilt = tiltVsFace; lastDisp = disp; lastSpeed = box.LinVel.Length();
                }
                Console.WriteLine($"  box-on-ramp-rest mu={mu} ({(shouldStick ? "stick" : "slide")}): tiltVsFace={lastTilt:F4} (aligned), disp={lastDisp:F3}, endSpeed={lastSpeed:F2} -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 14) BOX-TUMBLES-RAMP (emergent-tumble proof): a box placed OFF-BALANCE on a STEEP ramp TUMBLES over
        //     its edge down the slope (orientation rotates well past its start), moves downhill, stays BOUNDED,
        //     NO teleport, and settles — no legacy tip, no fling.
        {
            const float bh = 0.5f, H = 8f, Xr = 5f, Zr = 4f;                    // slope 0.8, ~38.7°
            float theta = MathF.Atan(H / (2f * Xr));
            var q = Quaternions.QuatFromEuler(new Vector3(0f, 0f, -theta - 0.5f));   // aligned + extra downhill lean (past its edge)
            foreach (float dt in new[] { 1f / 60f, 1f / 30f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0.1f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var ramp = Ramp(H, Xr, Zr); ramp.Friction = 0.8f; w.Bodies.Add(ramp);
                float sx0 = -Xr + 2f, sy0 = H * (Xr - sx0) / (2f * Xr);
                var box = RigidBody.DynamicBox(new Vector3(sx0, sy0 + bh + 0.3f, 0f), new Vector3(bh, bh, bh), q, 1f); box.Friction = 0.8f; w.Bodies.Add(box);
                Vector3 startPos = box.Position; float startTilt = BoxTilt(box), maxTilt = startTilt, maxStep = 0f, lastWindow = 0f;
                Vector3 prev = box.Position, prevUp = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                int steps = (int)(12f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    float t = BoxTilt(box); if (t > maxTilt) maxTilt = t;
                    float s = MathF.Sqrt((box.Position.X - prev.X) * (box.Position.X - prev.X) + (box.Position.Z - prev.Z) * (box.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = box.Position;
                    Vector3 up = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                    if (i >= steps - 90) { float step = MathF.Acos(Math.Clamp(up * prevUp, -1f, 1f)); if (step > lastWindow) lastWindow = step; }
                    prevUp = up;
                }
                bool tumbled = maxTilt > startTilt + 0.6f;                       // rotated well past its start (a real tumble)
                bool movedDownhill = box.Position.X > startPos.X + 1.5f;
                bool noTeleport = maxStep < 0.5f;
                bool settled = lastWindow < 0.02f;                              // stopped rotating by the end
                bool bounded = MathF.Abs(box.Position.X) < 20f && box.Position.Y > -1f && box.Position.Y < H + 2f;
                bool good = tumbled && movedDownhill && noTeleport && settled && bounded;
                Console.WriteLine($"  box-tumbles-ramp dt=1/{1f / dt:F0}: tilt {startTilt:F2}->max {maxTilt:F2} (tumbled), movedX {startPos.X:F2}->{box.Position.X:F2}, maxStep={maxStep:F3}, settled(Δ={lastWindow:F4}) -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 15) BOX / SPHERE ON PYRAMID: dropped onto a pyramid FACE they rest/slide/roll on the REAL face — their
        //     final height is well BELOW the bbox top (y=H+size), proving it's the real triangle, not the box —
        //     with NO teleport, bounded.
        {
            const float H = 5f, B = 6f, bh = 0.5f, rad = 0.5f;
            foreach (bool isBox in new[] { true, false })
            {
                bool allDt = true; float lastY = 0f, lastStep = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                    var pyr = Pyramid(H, B); pyr.Friction = 0.6f; w.Bodies.Add(pyr);
                    Vector3 dropAbove = new Vector3(2f * B / 3f, H / 3f + 1.2f, 0f);     // above the +X face's centroid
                    RigidBody obj = isBox ? RigidBody.DynamicBox(dropAbove, new Vector3(bh, bh, bh), Quat.Identity, 1f) : RigidBody.Sphere(dropAbove, rad, 1f);
                    obj.Friction = 0.6f; w.Bodies.Add(obj);
                    Vector3 prev = obj.Position; float maxStep = 0f, maxY = obj.Position.Y;
                    int steps = (int)(6f / dt);
                    for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((obj.Position.X - prev.X) * (obj.Position.X - prev.X) + (obj.Position.Z - prev.Z) * (obj.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = obj.Position; if (obj.Position.Y > maxY) maxY = obj.Position.Y; }
                    bool onRealFace = maxY < H + 0.9f;                          // never floated up to the bbox top (H + halfSize)
                    bool noTeleport = maxStep < 0.5f;
                    bool bounded = MathF.Abs(obj.Position.X) < 30f && MathF.Abs(obj.Position.Z) < 10f && obj.Position.Y > -1f;
                    allDt &= onRealFace && noTeleport && bounded; lastY = obj.Position.Y; lastStep = maxStep;
                }
                Console.WriteLine($"  {(isBox ? "box" : "sphere")}-on-pyramid: endY={lastY:F2} (< bbox top {H + (isBox ? bh : rad):F1} = real face), maxStep={lastStep:F3} (no teleport) -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 16) BOX-ON-FLAT-MESH (guards the live platform, which is a flat 2-triangle MESH — not a StaticBox):
        //     a box dropped on a flat mesh quad rests FLAT and STILL (tilt ~0, no jitter, pen ≤ slop) and SLEEPS.
        {
            const float bh = 0.5f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticMesh(new[] { new Vector3(-20f, 0f, -20f), new Vector3(-20f, 0f, 20f), new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, -20f) }, new[] { 0, 1, 2, 0, 2, 3 }));
            var box = RigidBody.DynamicBox(new Vector3(0.7f, 1f, -0.3f), new Vector3(bh, bh, bh), Quat.Identity, 1f); w.Bodies.Add(box);
            for (int i = 0; i < 400; i++) w.Step(1f / 60f);
            Vector3 p0 = box.Position; float maxJit = 0f, maxTilt = 0f;
            for (int i = 0; i < 120; i++) { w.Step(1f / 60f); maxJit = MathF.Max(maxJit, (box.Position - p0).Length()); maxTilt = MathF.Max(maxTilt, BoxTilt(box)); }
            bool rested = MathF.Abs(box.Position.Y - bh) < slop;
            bool good = rested && maxTilt < 0.02f && maxJit < 1e-4f && box.Sleeping;
            Console.WriteLine($"  box-on-flat-mesh: restY={box.Position.Y:F4} (want {bh}), tilt={maxTilt:F5}, jitter={maxJit:F6}, sleep={box.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 17) BALL-ROLLING-STOPS (Stage 6 rolling friction): a sphere given horizontal velocity on flat ground
        //     rolls, DECELERATES, and comes to REST + SLEEPS within a bounded distance — NOT perpetual. Rolling
        //     friction never reverses the ball or injects energy.
        {
            const float rad = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
                var ball = RigidBody.Sphere(new Vector3(0f, rad, 0f), rad, 1f); ball.LinVel = new Vector3(4f, 0f, 0f); w.Bodies.Add(ball);
                float startX = ball.Position.X; bool reversed = false;
                int steps = (int)(40f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); if (ball.LinVel.X < -0.05f) reversed = true; if (ball.Sleeping) break; }
                float stopDist = ball.Position.X - startX;
                bool stopped = ball.Sleeping && ball.LinVel.Length() < 0.05f && ball.AngVel.Length() < 0.1f;
                bool bounded = stopDist > 0.5f && stopDist < 40f;               // rolled forward a FINITE distance (not perpetual)
                bool good = stopped && bounded && !reversed;
                Console.WriteLine($"  ball-rolling-stops dt=1/{1f / dt:F0}: stopDist={stopDist:F2}, |v|={ball.LinVel.Length():F4}, |w|={ball.AngVel.Length():F4}, sleep={ball.Sleeping}, reversed={reversed} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 18) BALL-SCATTERS-STACK-SLEEPS (fixes Stage-4's "doesn't fully sleep"): re-run the scatter; now the
        //     ball STOPS (rolling friction) so the WHOLE scene — ball + all boxes — eventually SLEEPS as one
        //     island, staying bounded.
        {
            const float bh = 0.5f, rad = 0.5f, v0 = 8f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var all = new List<RigidBody>();
            for (int k = 0; k < 3; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * 2f * bh, 0f), new Vector3(bh, bh, bh), Quat.Identity, 1f); all.Add(bx); w.Bodies.Add(bx); }
            var ball = RigidBody.Sphere(new Vector3(-2.5f, 1.5f, 0f), rad, 1f); ball.LinVel = new Vector3(v0, 0f, 0f); all.Add(ball); w.Bodies.Add(ball);
            bool allSleep = false; int sleptAt = 0;
            int steps = (int)(30f / (1f / 60f));
            for (int i = 0; i < steps; i++) { w.Step(1f / 60f); if (all.TrueForAll(b => b.Sleeping)) { allSleep = true; sleptAt = i; break; } }
            bool bounded = all.TrueForAll(b => MathF.Abs(b.Position.X) < 20f && MathF.Abs(b.Position.Z) < 10f && b.Position.Y > -1f);
            bool good = allSleep && bounded;
            Console.WriteLine($"  ball-scatters-stack-sleeps: wholeSceneSleeps={allSleep} (at ~{sleptAt / 60f:F1}s), bounded={bounded} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 19) REGRESSION — rolling friction only DAMPS, it does NOT freeze legitimate motion: a ball on a GENTLE
        //     slope STILL rolls down (gravity beats the small rolling resistance), and a box on a STEEP ramp
        //     STILL tumbles (Stage 5 preserved).
        {
            // (a) gentle slope: ball still rolls down
            const float rad = 0.5f, Hg = 2f, Xr = 8f, Zr = 4f;                  // slope 0.125, ~7.1°
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(Ramp(Hg, Xr, Zr));
            float sx = -Xr + 1.5f, sy = Hg * (Xr - sx) / (2f * Xr);
            var ball = RigidBody.Sphere(new Vector3(sx, sy + rad + 0.2f, 0f), rad, 1f); w.Bodies.Add(ball);
            for (int i = 0; i < (int)(6f / (1f / 60f)); i++) w.Step(1f / 60f);
            bool stillRolls = ball.Position.X > sx + 1.5f;                       // gravity overcame rolling friction on the gentle slope
            // (b) steep ramp: box still tumbles
            const float bh = 0.5f, Hs = 8f;
            float thetaS = MathF.Atan(Hs / (2f * 5f));
            var w2 = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0.1f };
            w2.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
            var ramp2 = Ramp(Hs, 5f, 4f); ramp2.Friction = 0.8f; w2.Bodies.Add(ramp2);
            float s2 = -5f + 2f, sy2 = Hs * (5f - s2) / (2f * 5f);
            var box = RigidBody.DynamicBox(new Vector3(s2, sy2 + bh + 0.3f, 0f), new Vector3(bh, bh, bh), Quaternions.QuatFromEuler(new Vector3(0f, 0f, -thetaS - 0.5f)), 1f); box.Friction = 0.8f; w2.Bodies.Add(box);
            float startTilt = BoxTilt(box), maxTilt = startTilt;
            for (int i = 0; i < (int)(6f / (1f / 60f)); i++) { w2.Step(1f / 60f); float t = BoxTilt(box); if (t > maxTilt) maxTilt = t; }
            bool stillTumbles = maxTilt > startTilt + 0.6f && box.Position.X > s2 + 1f;
            bool good = stillRolls && stillTumbles;
            Console.WriteLine($"  slope-still-rolls/steep-still-tumbles: ball rolled to X={ball.Position.X:F2} (>{sx + 1.5f:F2})={stillRolls}, box tumbled tilt {startTilt:F2}->{maxTilt:F2} + movedX={stillTumbles} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        Console.WriteLine(ok ? "IMPULSE TEST PASSED" : "IMPULSE TEST FAILED");
    }

    // Stage-7a CUTOVER: the impulse solver is now the DEFAULT, and its RigidBody state streams to peers.
    // Asserts (1) a world with no explicit engine resolves to "impulse" (legacy still selectable), and
    // (2) the PhysicsSync round-trip: an authority steps the impulse solver; each batch its state is
    // serialized -> deserialized -> applied on a CLIENT that dead-reckons + eases, and the client's
    // reconstructed Position AND Orientation track the authority within tolerance across batch rates.
    static void CutoverSelfTest()
    {
        Console.WriteLine("=== CUTOVER SELF-TEST (Stage 7a: impulse default + multiplayer sync) ===");
        bool ok = true;
        // Tilt (rad) of a body's local up-axis away from world vertical (0 = upright).
        static float TiltOfV(Vector3 lr) { Vector3 up = new Vector3(0f, 1f, 0f).Rotate(lr); return MathF.Acos(Math.Clamp(up.Y, -1f, 1f)); }

        // 1) SINGLE ENGINE: the "engine" switch was retired (Stage 7b) — there is one solver. A world JSON that
        //    still carries a STALE "engine" key (from an older save) must LOAD GRACEFULLY (the obsolete key is
        //    ignored, not a parse error), keeping the rest of the physics block intact.
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            WorldConfig? back = null; bool loaded = true;
            try { back = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>("{\"Name\":\"x\",\"Physics\":{\"GravityEnabled\":true,\"GravityStrength\":7.5,\"Engine\":\"legacy\"}}", opts); }
            catch { loaded = false; }
            bool fieldsKept = loaded && back != null && back.Physics.GravityEnabled && Math.Abs(back.Physics.GravityStrength - 7.5f) < 1e-4f;
            bool good = loaded && fieldsKept;
            Console.WriteLine($"  stale-engine-key-loads: parsed={loaded}, gravity-kept={fieldsKept} (obsolete \"engine\":\"legacy\" ignored) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) PHYSICS-SYNC ROUND-TRIP: authority (impulse) vs a client that only dead-reckons + eases. The box
        //    tumbles down a ramp (sustained translation X/Y + rotation), then settles — so the sync is exercised
        //    while the body is genuinely FALLING / SLIDING / TUMBLING, not just at rest.
        static WorldConfig SyncWorld() => new WorldConfig
        {
            Name = "synctest", Graphics = new GraphicsConfig { Shadows = false },
            Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 60f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0.1f },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 0, Type = "ramp", Color = "White",
                    Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 4f, Collides = true, Gravity = false },
                new WorldObject { Id = 1, Type = "cube", Color = "Red",
                    Position = new Vec3Config { X = 2f, Y = 7f, Z = 0f },
                    Rotation = new Vec3Config { X = 0f, Y = 0f, Z = 1.05f },   // off-balance -> falls, tumbles down the ramp, settles
                    Scale = 1f, Collides = true, Gravity = true },
            },
        };
        static PhysicsSyncPacket RoundTrip(PhysicsSyncPacket p)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) p.Serialize(w);
            ms.Position = 0;
            var recv = new PhysicsSyncPacket();
            using (var r = new BinaryReader(ms)) recv.Deserialize(r);
            return recv;
        }

        foreach (int batchEvery in new[] { 3, 6 })                 // ~20 Hz and ~10 Hz batches @ 60 fps
        {
            var authority = new PriviewNetworkScene(new DisplayManagerAsync(), SyncWorld(), isServer: false, "127.0.0.1", 0, online: false);
            authority.Start();
            var client = new PriviewNetworkScene(new DisplayManagerAsync(), SyncWorld(), isServer: false, "127.0.0.1", 0, online: false);
            client.Start();
            // give the authority box a little horizontal + spin so it translates in X/Z AND rotates (exercises full LinVel + AngVel).
            var aEntry = authority.EditableEntries.First(e => e.Instance.Gravity);
            var aBox = aEntry.Instance; int id = aEntry.Descriptor.Id;
            Vector3 startPos = aBox.Position; float startTilt = TiltOfV(aBox.LocalRotate);
            float dt = 1f / 60f, maxPosErr = 0f, maxRotErr = 0f, aMoved = 0f, aRotated = 0f;
            for (int frame = 0; frame < 480; frame++)
            {
                authority.StepPhysicsForTest(dt);
                if (frame % batchEvery == 0) client.ReceivePhysicsSyncForTest(RoundTrip(authority.SnapshotPhysicsSyncForTest()));
                client.StepNetworkPhysicsForTest(dt);
                aMoved = MathF.Max(aMoved, (aBox.Position - startPos).Length());
                aRotated = MathF.Max(aRotated, MathF.Abs(TiltOfV(aBox.LocalRotate) - startTilt));
                if (frame > 15)                                    // after the first couple of batches, measure tracking THROUGHOUT the motion
                {
                    var cBox = client.EditableEntries.First(e => e.Descriptor.Id == id).Instance;
                    float posErr = (cBox.Position - aBox.Position).Length();
                    Vector3 aUp = new Vector3(0f, 1f, 0f).Rotate(aBox.LocalRotate), cUp = new Vector3(0f, 1f, 0f).Rotate(cBox.LocalRotate);
                    float rotErr = MathF.Acos(Math.Clamp(aUp * cUp, -1f, 1f));
                    if (posErr > maxPosErr) maxPosErr = posErr;
                    if (rotErr > maxRotErr) maxRotErr = rotErr;
                }
            }
            var cFinal = client.EditableEntries.First(e => e.Descriptor.Id == id).Instance;
            float finalPos = (cFinal.Position - aBox.Position).Length();
            Vector3 aU = new Vector3(0f, 1f, 0f).Rotate(aBox.LocalRotate), cU = new Vector3(0f, 1f, 0f).Rotate(cFinal.LocalRotate);
            float finalRot = MathF.Acos(Math.Clamp(aU * cU, -1f, 1f));
            bool actuallyMoved = aMoved > 2f && aRotated > 0.5f;   // the authority genuinely fell + tumbled (not a trivial pass)
            bool tracks = maxPosErr < 0.6f && maxRotErr < 0.6f;    // client stayed close THROUGHOUT the motion (dead-reckon + ease)
            bool converged = finalPos < 0.05f && finalRot < 0.05f; // and matches exactly once at rest
            bool good = actuallyMoved && tracks && converged;
            Console.WriteLine($"  physics-sync-roundtrip batch=1/{batchEvery}: authorityMoved={aMoved:F2}/rotated={aRotated:F2}, maxPosErr={maxPosErr:F3}, maxRotErr={maxRotErr:F3}, finalPosErr={finalPos:F4}, finalRotErr={finalRot:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        Console.WriteLine(ok ? "CUTOVER TEST PASSED" : "CUTOVER TEST FAILED");
    }

    // A static ground box (top face at y=0) + a dynamic sphere dropped from dropY, for the impulse test.
    static ImpulseWorld MakeImpulseWorld(float e, float g, float radius, float dropY, out RigidBody ball)
    {
        var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = e };
        w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), Quat.Identity));
        ball = RigidBody.Sphere(new Vector3(0f, dropY, 0f), radius, 1f);
        w.Bodies.Add(ball);
        return w;
    }

}
