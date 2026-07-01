using System;
using System.Collections.Generic;
using Nova3DVisualiser;
using SampleGame.Scenes;

namespace SampleGame.Physics;

// The Stage-1/2/3 impulse world: a set of RigidBodies stepped by a fixed-substep sequential-impulse solver.
//   per substep: apply gravity -> generate contacts -> warm-start -> solve NORMAL + FRICTION velocity
//   constraints (restitution from the initial impact; Coulomb-clamped tangent impulses) -> SPLIT-IMPULSE
//   positional correction (removes penetration WITHOUT injecting energy) -> integrate -> sleeping.
// Contacts are generated for a DYNAMIC sphere OR dynamic BOX vs STATIC shapes AND for dynamic BOX vs dynamic
// BOX (Stage 3 -> stable stacks); sphere-vs-dynamic and real-triangle-mesh contact are later stages.
public sealed class ImpulseWorld
{
    public readonly List<RigidBody> Bodies = new();
    public Vector3 Gravity = new Vector3(0f, -9.8f, 0f);
    public float Restitution = 0f;                 // world default restitution (a body's <0 restitution inherits this)

    // ---- tunables (chosen for stability; see the acceptance criteria in PHYSICS.md) ----
    public const float MaxStep = 1f / 60f;         // fixed substep size (frame-rate independence)
    public const int MaxSubSteps = 8;              // cap substeps per Step() so a monster frame can't stall
    private const int VelIters = 10;               // sequential-impulse velocity iterations (enough for a tall stack)
    private const int PosIters = 8;                // split-impulse position iterations (a stack must not sag/sink)
    private const float Slop = 0.005f;             // allowed resting penetration
    private const float Margin = 0.02f;            // speculative contact margin (a resting box is caught before it sinks)
    private const float Beta = 0.2f;               // fraction of excess penetration removed per substep (position bias)
    private const float RestThreshold = 1.0f;      // |approach speed| below this = a resting contact -> NO restitution (kills micro-bounce)
    private const float SleepLin = 0.05f;          // |LinVel| below this (units/s) counts as calm
    private const float SleepAng = 0.05f;          // |AngVel| below this (rad/s) counts as calm
    private const int SleepSteps = 30;             // consecutive calm substeps before a body sleeps
    // ---- anti-fling rails (mirror the legacy engine's: nothing may explode even at coarse dt / deep contact) ----
    private const float MaxBiasSpeed = 4f;         // cap the split-impulse position-correction speed (a deep coarse-dt penetration can't fling)
    private const float MaxLinSpeed = 60f;         // clamp |LinVel| (units/s) — anti-explosion
    private const float MaxAngSpeed = 30f;         // clamp |AngVel| (rad/s)  — anti-explosion
    private const float RunawayBound = 300f;       // a body past this has escaped -> zero its velocity (backstop, mirrors the legacy engine)

    private readonly List<Contact> _contacts = new();
    // warm-start cache: (body-pair key, manifold Feature) -> last (normal, tangent1, tangent2) impulses.
    private readonly Dictionary<(ulong, int), (float n, float t1, float t2)> _warm = new();

    // Advance the world by dt using fixed substeps (each <= MaxStep). Frame-rate independent.
    public void Step(float dt)
    {
        if (dt <= 0f) return;
        int sub = System.Math.Clamp((int)MathF.Ceiling(dt / MaxStep), 1, MaxSubSteps);
        float h = dt / sub;
        for (int s = 0; s < sub; s++) SubStep(h);
    }

    private void SubStep(float h)
    {
        // 1) integrate velocity: gravity on awake dynamic bodies.
        foreach (var b in Bodies)
            if (!b.IsStatic && !b.Sleeping) b.LinVel += Gravity * h;

        // 2) generate contacts (dynamic sphere vs static shapes).
        GenerateContacts();

        // 3a) prepare each constraint: lever arms, effective normal + tangent masses, combined material
        //     coefficients, and the restitution bias from the CURRENT (post-gravity, pre-warm-start) approach.
        foreach (var c in _contacts)
        {
            c.Ra = c.Point - c.A.Position;
            c.Rb = c.Point - c.B.Position;
            Vector3 n = c.Normal;
            c.NormalMass = EffMass(c, n);
            ImpulseMath.TangentBasis(n, out var t1, out var t2);
            c.Tan1 = t1; c.Tan2 = t2;
            c.TanMass1 = EffMass(c, t1);
            c.TanMass2 = EffMass(c, t2);
            c.Friction = MathF.Sqrt(MathF.Max(0f, c.A.Friction) * MathF.Max(0f, c.B.Friction));
            c.Restitution = MathF.Sqrt(MathF.Max(0f, RestOf(c.A)) * MathF.Max(0f, RestOf(c.B)));
            float vn0 = RelVelN(c);
            c.RestitutionBias = vn0 < -RestThreshold ? -c.Restitution * vn0 : 0f;
        }

        // 3b) warm-start: re-apply the cached normal + friction impulses from the previous substep.
        foreach (var c in _contacts)
        {
            var w = _warm.GetValueOrDefault((c.PairKey, c.Feature));
            c.NormalImpulse = w.n; c.TanImpulse1 = w.t1; c.TanImpulse2 = w.t2;
            ApplyImpulse(c, c.Normal * c.NormalImpulse + c.Tan1 * c.TanImpulse1 + c.Tan2 * c.TanImpulse2);
        }

        // 4) solve velocity constraints: per iteration, NORMAL first (accumulated impulse clamped >= 0 so
        //    contacts push, never pull), then the two FRICTION tangents (accumulated 2D magnitude clamped to
        //    μ · normal impulse — Coulomb, so friction never exceeds μN and never adds energy).
        for (int it = 0; it < VelIters; it++)
            foreach (var c in _contacts)
            {
                float vn = RelVelN(c);
                float dl = c.NormalMass * (-(vn - c.RestitutionBias));
                float oldN = c.NormalImpulse;
                c.NormalImpulse = MathF.Max(oldN + dl, 0f);
                ApplyImpulse(c, c.Normal * (c.NormalImpulse - oldN));

                float maxF = c.Friction * c.NormalImpulse;
                float new1 = c.TanImpulse1 - c.TanMass1 * RelVelT(c, c.Tan1);
                float new2 = c.TanImpulse2 - c.TanMass2 * RelVelT(c, c.Tan2);
                float mag = MathF.Sqrt(new1 * new1 + new2 * new2);
                if (mag > maxF) { float s = maxF / mag; new1 *= s; new2 *= s; }
                ApplyImpulse(c, c.Tan1 * (new1 - c.TanImpulse1) + c.Tan2 * (new2 - c.TanImpulse2));
                c.TanImpulse1 = new1; c.TanImpulse2 = new2;
            }

        // cache accumulated impulses for the next substep's warm-start.
        _warm.Clear();
        foreach (var c in _contacts) _warm[(c.PairKey, c.Feature)] = (c.NormalImpulse, c.TanImpulse1, c.TanImpulse2);

        // 4c) ROLLING FRICTION (Stage 6): each body in contact gets a bounded resistance to SPIN, scaled by the
        //     normal impulse it carried (more load -> more resistance). It removes angular momentum OPPOSING ω,
        //     CLAMPED so it can never reverse the spin or add energy — a rolling ball decelerates and STOPS, a
        //     tumble damps to rest. The coefficient is small, so gravity still wins on a slope (a ball rolls
        //     DOWN, a box on a steep ramp still TUMBLES); rolling friction only damps.
        foreach (var b in Bodies) b.RollBudget = 0f;
        foreach (var c in _contacts)
        {
            float roll = MathF.Sqrt(MathF.Max(0f, c.A.RollingFriction) * MathF.Max(0f, c.B.RollingFriction)) * c.NormalImpulse;
            if (!c.A.Sleeping) c.A.RollBudget += roll;
            if (!c.B.Sleeping) c.B.RollBudget += roll;
        }
        foreach (var b in Bodies)
        {
            if (b.IsStatic || b.Sleeping || b.RollBudget <= 0f) continue;
            if (b.AngVel * b.AngVel < SleepAng * SleepAng) continue;    // rotationally calm already -> don't perturb it (a resting stacked box would drift from phantom slip)
            Vector3 L = InertiaWorldMul(b, b.AngVel);                   // angular momentum (world) = I·ω
            float Llen = L.Length();
            if (Llen < 1e-8f) continue;
            float drop = MathF.Min(b.RollBudget, Llen);                 // clamp to |L| -> no reversal, no energy gain
            b.AngVel *= 1f - drop / Llen;                               // ω·(1 − drop/|L|): reduces |ω| toward 0
        }

        // 5) positional correction via SPLIT IMPULSE: solve pseudo velocities against the penetration, then
        //    move positions by them and DISCARD them — penetration is removed without touching real velocity,
        //    so no energy is injected (the flaw the legacy Baumgarte-in-velocity model had).
        foreach (var b in Bodies) { b.PseudoLin = Vector3.Zero; b.PseudoAng = Vector3.Zero; }
        foreach (var c in _contacts) c.PosImpulse = 0f;
        for (int it = 0; it < PosIters; it++)
            foreach (var c in _contacts)
            {
                float bias = MathF.Min(Beta * MathF.Max(c.Penetration - Slop, 0f) / h, MaxBiasSpeed);   // desired separation speed (clamped: a deep coarse-dt penetration can't fling)
                float vnp = PseudoRelVelN(c);
                float dl = c.NormalMass * (bias - vnp);
                float old = c.PosImpulse;
                c.PosImpulse = MathF.Max(old + dl, 0f);
                ApplyPseudo(c, c.Normal * (c.PosImpulse - old));
            }

        // 6) integrate position (real + pseudo) and orientation (reuse the engine's drift-free quaternion step),
        //    with anti-fling rails: clamp the real velocities before integrating, and a runaway backstop that
        //    zeroes the velocity of any body that has escaped to absurd coordinates (nothing flings to infinity).
        foreach (var b in Bodies)
        {
            if (b.IsStatic || b.Sleeping) continue;
            float ls2 = b.LinVel * b.LinVel; if (ls2 > MaxLinSpeed * MaxLinSpeed) b.LinVel *= MaxLinSpeed / MathF.Sqrt(ls2);
            float as2 = b.AngVel * b.AngVel; if (as2 > MaxAngSpeed * MaxAngSpeed) b.AngVel *= MaxAngSpeed / MathF.Sqrt(as2);
            b.Position += (b.LinVel + b.PseudoLin) * h;
            b.Orientation = PriviewNetworkScene.IntegrateQuat(b.Orientation, b.AngVel + b.PseudoAng, h);
            if (MathF.Abs(b.Position.X) > RunawayBound || MathF.Abs(b.Position.Y) > RunawayBound || MathF.Abs(b.Position.Z) > RunawayBound)
            { b.LinVel = Vector3.Zero; b.AngVel = Vector3.Zero; }
        }

        // 7) sleeping ISLANDS (Stage 6): bodies connected by contacts form an island (union-find over the
        //    dynamic-dynamic contacts; a static body doesn't merge islands). An island sleeps only when ALL its
        //    members are calm — so a pile (boxes + a settled ball) sleeps cleanly as ONE group and never half-
        //    sleeps or leans; and a moving body still WAKES the sleeping neighbours it touches (disturbances
        //    propagate). This generalizes the Stage-3 direct-neighbour coupling (transitive, not just adjacent).
        // 7a) per-body calm flag (a sleeping body counts as calm) + reset the union-find forest.
        foreach (var b in Bodies)
        {
            if (b.IsStatic) continue;
            b.Calm = b.Sleeping || (b.LinVel * b.LinVel < SleepLin * SleepLin && b.AngVel * b.AngVel < SleepAng * SleepAng);
            b.UF = b;
        }
        // 7b) union bodies sharing a contact; a moving body wakes the sleeping ones it touches.
        foreach (var c in _contacts)
        {
            RigidBody a = c.A, b = c.B;
            if (a.IsStatic || b.IsStatic) continue;
            Union(a, b);
            if (!a.Sleeping && !a.Calm && b.Sleeping) Wake(b);
            if (!b.Sleeping && !b.Calm && a.Sleeping) Wake(a);
        }
        // 7c) island calm = AND of every member's calm (accumulated on the island root).
        foreach (var b in Bodies) if (!b.IsStatic) Find(b).IslandCalm = true;
        foreach (var b in Bodies) if (!b.IsStatic) { var r = Find(b); r.IslandCalm = r.IslandCalm && b.Calm; }
        // 7d) sleep decision: an awake body accumulates calm substeps only while its WHOLE island is calm.
        foreach (var b in Bodies)
        {
            if (b.IsStatic || b.Sleeping) continue;
            if (Find(b).IslandCalm)
            {
                if (++b.CalmSteps >= SleepSteps) { b.Sleeping = true; b.LinVel = Vector3.Zero; b.AngVel = Vector3.Zero; }
            }
            else b.CalmSteps = 0;
        }
    }

    // Union-find over the dynamic bodies (islands connected by contacts).
    private static RigidBody Find(RigidBody x) { while (x.UF != x) { x.UF = x.UF!.UF; x = x.UF!; } return x; }
    private static void Union(RigidBody a, RigidBody b) { var ra = Find(a); var rb = Find(b); if (ra != rb) ra.UF = rb; }

    // Forward world inertia applied to a world vector: R · diag(1/InvInertiaLocal) · Rᵀ · v (angular momentum I·ω).
    private static Vector3 InertiaWorldMul(RigidBody b, Vector3 v)
    {
        Vector3 local = ImpulseMath.RotateInv(b.Orientation, v);
        local = new Vector3(
            b.InvInertiaLocal.X > 0f ? local.X / b.InvInertiaLocal.X : 0f,
            b.InvInertiaLocal.Y > 0f ? local.Y / b.InvInertiaLocal.Y : 0f,
            b.InvInertiaLocal.Z > 0f ? local.Z / b.InvInertiaLocal.Z : 0f);
        return ImpulseMath.Rotate(b.Orientation, local);
    }

    // Wake a sleeping body (a later stage calls this on a disturbance; Stage 1 rarely needs it).
    public static void Wake(RigidBody b) { b.Sleeping = false; b.CalmSteps = 0; }

    private void GenerateContacts()
    {
        _contacts.Clear();
        foreach (var dyn in Bodies)
        {
            if (dyn.IsStatic || dyn.Sleeping) continue;
            if (dyn.Kind == ColliderKind.Sphere)                                               // dynamic sphere vs static (Stage 1)
            {
                foreach (var stat in Bodies)
                {
                    if (!stat.IsStatic) continue;                                              // vs static only
                    Contact? c = stat.Kind switch
                    {
                        ColliderKind.Box    => ContactGen.SphereVsBox(stat, dyn, Slop, out var cb) ? cb : null,
                        ColliderKind.Mesh   => ContactGen.SphereVsMesh(stat, dyn, Slop, out var cm) ? cm : null,
                        ColliderKind.Sphere => ContactGen.SphereVsSphere(stat, dyn, Slop, out var cs) ? cs : null,
                        _ => null,
                    };
                    if (c != null) _contacts.Add(c);
                }
            }
            else if (dyn.Kind == ColliderKind.Box)                                             // dynamic box vs static (Stage 2)
            {
                foreach (var stat in Bodies)
                {
                    if (!stat.IsStatic) continue;
                    if (stat.Kind == ColliderKind.Sphere) { if (ContactGen.BoxVsStaticSphere(stat, dyn, Margin, out var cs)) _contacts.Add(cs); }
                    else if (stat.Kind == ColliderKind.Mesh) ContactGen.BoxVsMesh(stat, dyn, Margin, _contacts);   // Stage 5: REAL triangles (rests/tumbles on the true slope); high-poly falls back to the OBB box view
                    else if (stat.Kind == ColliderKind.Box) ContactGen.BoxVsBox(stat, dyn, Margin, _contacts);      // a genuine static OBB
                }
            }
            // dynamic Mesh: inert (later stages). The runtime represents dynamic meshes as OBBs (ColliderKind.Box).
        }

        // dynamic PRIMITIVE vs dynamic PRIMITIVE (Stage 3: box-box stacks; Stage 4: sphere-box + sphere-sphere).
        // Each unordered pair once; a fully-asleep pair is skipped (both frozen), but an (awake, sleeping) pair
        // IS generated with the sleeper as an immovable anchor. The A/B assignment is DETERMINISTIC per pair
        // (box-box + sphere-sphere ordered by Id; sphere-box always box=A) so the reference/incident choice,
        // feature ids and the contact NORMAL are stable frame-to-frame -> warm-start catches -> no jitter.
        // Boxes get the clipped multi-point manifold; sphere pairs get a single analytic point. Real-triangle
        // mesh contact stays inert (Stage 5): a dynamic Mesh never reaches here (the runtime boxes it as an OBB).
        for (int i = 0; i < Bodies.Count; i++)
        {
            var a = Bodies[i];
            if (a.IsStatic || a.Kind == ColliderKind.Mesh) continue;                       // dynamic sphere or box
            for (int j = i + 1; j < Bodies.Count; j++)
            {
                var b = Bodies[j];
                if (b.IsStatic || b.Kind == ColliderKind.Mesh) continue;
                if (a.Sleeping && b.Sleeping) continue;

                bool aBox = a.Kind == ColliderKind.Box, bBox = b.Kind == ColliderKind.Box;
                if (aBox && bBox)                                                           // box-box: clipped manifold
                {
                    var (lo, hi) = a.Id < b.Id ? (a, b) : (b, a);
                    ContactGen.BoxVsBox(lo, hi, Margin, _contacts);
                }
                else if (aBox != bBox)                                                     // sphere-box: A=box, B=sphere
                {
                    var box = aBox ? a : b;
                    var sph = aBox ? b : a;
                    if (ContactGen.SphereVsBox(box, sph, Slop, out var cb)) _contacts.Add(cb);
                }
                else                                                                       // sphere-sphere: A=lower Id
                {
                    var (lo, hi) = a.Id < b.Id ? (a, b) : (b, a);
                    if (ContactGen.SphereVsSphere(lo, hi, Slop, out var cs)) _contacts.Add(cs);
                }
            }
        }
    }

    // Relative normal velocity at the contact (B minus A): approaching < 0, separating > 0.
    private static float RelVelN(Contact c)
        => (c.B.VelocityAt(c.Point) - c.A.VelocityAt(c.Point)) * c.Normal;

    // Relative velocity at the contact along a tangent direction 't'.
    private static float RelVelT(Contact c, Vector3 t)
        => (c.B.VelocityAt(c.Point) - c.A.VelocityAt(c.Point)) * t;

    // A SLEEPING dynamic body acts as an immovable ANCHOR in the solve (effective inverse mass/inertia 0) —
    // an awake box rests on a sleeping one below exactly as it would on the static ground, so a partly-slept
    // stack stays solid until the sleeper is woken.
    private static float InvMassOf(RigidBody b) => b.Sleeping ? 0f : b.InvMass;

    // Effective mass of the pair along a unit direction 'dir' at the contact (linear + both angular terms).
    // Requires c.Ra/c.Rb already set.
    private static float EffMass(Contact c, Vector3 dir)
    {
        float k = InvMassOf(c.A) + InvMassOf(c.B) + DotAngular(c.A, c.Ra, dir) + DotAngular(c.B, c.Rb, dir);
        return k > 0f ? 1f / k : 0f;
    }

    // Resolve a body's restitution: a body value < 0 inherits the world default.
    private float RestOf(RigidBody b) => b.Restitution >= 0f ? b.Restitution : Restitution;

    private static float PseudoRelVelN(Contact c)
        => (c.B.PseudoVelocityAt(c.Point) - c.A.PseudoVelocityAt(c.Point)) * c.Normal;

    // n·((I⁻¹(r×n))×r) — the angular contribution of a body to the effective normal mass (0 if sleeping/static).
    private static float DotAngular(RigidBody b, Vector3 r, Vector3 n)
    {
        if (b.Sleeping) return 0f;
        Vector3 rn = Vector3.Cross(r, n);
        return Vector3.Cross(ImpulseMath.MulInvInertiaWorld(b, rn), r) * n;
    }

    private static void ApplyImpulse(Contact c, Vector3 p)
    {
        if (c.B.InvMass > 0f && !c.B.Sleeping) { c.B.LinVel += p * c.B.InvMass; c.B.AngVel += ImpulseMath.MulInvInertiaWorld(c.B, Vector3.Cross(c.Rb, p)); }
        if (c.A.InvMass > 0f && !c.A.Sleeping) { c.A.LinVel -= p * c.A.InvMass; c.A.AngVel -= ImpulseMath.MulInvInertiaWorld(c.A, Vector3.Cross(c.Ra, p)); }
    }

    private static void ApplyPseudo(Contact c, Vector3 p)
    {
        if (c.B.InvMass > 0f && !c.B.Sleeping) { c.B.PseudoLin += p * c.B.InvMass; c.B.PseudoAng += ImpulseMath.MulInvInertiaWorld(c.B, Vector3.Cross(c.Rb, p)); }
        if (c.A.InvMass > 0f && !c.A.Sleeping) { c.A.PseudoLin -= p * c.A.InvMass; c.A.PseudoAng -= ImpulseMath.MulInvInertiaWorld(c.A, Vector3.Cross(c.Ra, p)); }
    }
}
