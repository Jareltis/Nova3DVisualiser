using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.Globalization;
using System.Text.Json;

namespace SampleGame.Scenes;

public partial class PriviewNetworkScene
{
    // Object-dynamics: the impulse-solver step + static-mesh body build, shared physics math (Quat/BoxInertia/SatBox3D/CombineRestitution), and the physics network sync (flush/receive/dead-reckon).

    // Server: ids whose physics moved them since the last sync flush (the "changed" set). Client:
    // per-id interpolation targets it eases the live instance toward. Authority streams a compact
    // PhysicsSyncPacket every PhysicsSyncEvery frames; clients dead-reckon + lerp so falling is smooth.
    private readonly HashSet<int> _physMoved = new();                  // server: changed-object ids
    private readonly Dictionary<int, NetTarget> _physTargets = new();  // client: id -> eased target
    private int _physFrame = 0;                                        // server: sync throttle counter
    private const int PhysicsSyncEvery = 3;                            // send a batch every Nth frame (~20 Hz @60fps)
    private const float PhysLerpRate = 12f;                            // client ease speed toward target (1/sec)

    // Client-side interpolation target for one synced object: last authoritative position + velocity.
    private sealed class NetTarget { public Vector3 Pos; public Vector3 Lin; public Vector3 Rot; public Vector3 AngVel; }

    // Combined coefficient of restitution for a contact between two bodies: the geometric mean
    // sqrt(eA·eB), so a rebound needs BOTH surfaces elastic — a dead/soft surface kills the bounce
    // (a superball on mud barely bounces), two springy ones stay springy. Negatives clamp to 0.
    // Pure + tested. (max() would be "trampoline" semantics; the geometric mean is the energy model.)
    public static float CombineRestitution(float a, float b) => MathF.Sqrt(MathF.Max(0f, a) * MathF.Max(0f, b));

    // Per-object coefficient of restitution, resolving the "inherit world" sentinel: a non-negative
    // stored value is used as-is, a negative one falls back to the world default (PhysicsConfig.Restitution).
    private float RestitutionOf(GameObject o) => o.Restitution >= 0f ? o.Restitution : _world.Physics.Restitution;

    // Diagonal box inertia tensor — the principal moments about X/Y/Z for a solid box of size
    // (sx,sy,sz) and mass: I_x = m(sy²+sz²)/12, I_y = m(sx²+sz²)/12, I_z = m(sx²+sy²)/12. Returned as
    // a Vector3 of the three moments (a world-axis approximation; exact when the box is axis-aligned).
    public static Vector3 BoxInertia(float mass, float sx, float sy, float sz) => new Vector3(
        mass * (sy * sy + sz * sz) / 12f,
        mass * (sx * sx + sz * sz) / 12f,
        mass * (sx * sx + sy * sy) / 12f);

    // ---- Quaternion orientation (drift-free SO(3) integration) ----
    // Phase 7 stores a spinning object's orientation as a unit quaternion (the source of truth) and
    // converts it to the engine's Euler LocalRotate each frame for rendering. This removes the Euler-
    // integration drift of phase 6: angular velocity integrates correctly in SO(3), then we project to
    // Euler in the ENGINE'S rotation order (Vector3.Rotate = Rx then Ry then Rz, i.e. R = Rz·Ry·Rx).
    public readonly struct Quat
    {
        public readonly float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);
    }

    // Hamilton product a⊗b.
    public static Quat QuatMul(Quat a, Quat b) => new Quat(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static Quat QuatNorm(Quat q)
    {
        float m = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return m > 1e-12f ? new Quat(q.X / m, q.Y / m, q.Z / m, q.W / m) : Quat.Identity;
    }

    // Euler (engine order: apply Rx, then Ry, then Rz → q = qz·qy·qx) → unit quaternion.
    public static Quat QuatFromEuler(Vector3 e)
    {
        float hx = e.X * 0.5f, hy = e.Y * 0.5f, hz = e.Z * 0.5f;
        Quat qx = new Quat(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
        Quat qy = new Quat(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
        Quat qz = new Quat(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));
        return QuatMul(qz, QuatMul(qy, qx));
    }

    // Quaternion → Euler (X,Y,Z) in the engine's order, extracted from R = Rz·Ry·Rx. Gimbal lock at
    // |Y|≈90° is a representation ambiguity only (the quaternion remains the source of truth).
    public static Vector3 EulerFromQuat(Quat q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float r20 = 2f * (x * z - w * y);
        float r21 = 2f * (y * z + w * x);
        float r22 = 1f - 2f * (x * x + y * y);
        float r10 = 2f * (x * y + w * z);
        float r00 = 1f - 2f * (y * y + z * z);
        return new Vector3(MathF.Atan2(r21, r22), MathF.Asin(Math.Clamp(-r20, -1f, 1f)), MathF.Atan2(r10, r00));
    }

    // Integrate a WORLD-frame angular velocity ω for dt: q' = normalize(q + ½·(ω⊗q)·dt). First-order
    // but renormalized, so the orientation stays a unit quaternion and never drifts/scales.
    public static Quat IntegrateQuat(Quat q, Vector3 omega, float dt)
    {
        Quat dq = QuatMul(new Quat(omega.X, omega.Y, omega.Z, 0f), q);
        float h = 0.5f * dt;
        return QuatNorm(new Quat(q.X + dq.X * h, q.Y + dq.Y * h, q.Z + dq.Z * h, q.W + dq.W * h));
    }

    // Physics tick (authority/solo only): the impulse-based rigid-body constraint solver. It SUBSTEPS a large
    // frame time internally (ImpulseWorld.Step: each substep <= 1/60 s, capped) so a slow render frame can't
    // make a fast fall tunnel and explode — frame-rate INDEPENDENT. This is the sole object-dynamics engine.
    private void StepPhysics(float dt)
    {
        if (!CanEdit || !_world.Physics.GravityEnabled) return;
        if (dt <= 0f) return;
        StepImpulse(dt);
    }

    // ---- impulse-solver runtime wiring (see PHYSICS.md). ----
    // Simulates dynamic SPHERES and BOXES (any collidable dynamic Object3d as its OBB) against STATIC
    // collidables (boxes + real triangle meshes) and each other; the platform is the primary support.
    // Legacy mode ("legacy", the default) never reaches this path and is unchanged.
    private ImpulseWorld? _impWorld;
    private readonly Dictionary<GameObject, RigidBody> _impBodies = new();   // persistent dynamic-sphere bodies (velocity/sleep/warm-start state survives frames)

    private void StepImpulse(float dt)
    {
        var w = _impWorld ??= new ImpulseWorld();
        w.Gravity = new Vector3(0f, -_world.Physics.GravityStrength, 0f);
        w.Restitution = _world.Physics.Restitution;
        w.Bodies.Clear();

        // Dynamic bodies (Gravity + Collides) -> persistent RigidBodies (the solver owns their transform +
        // velocity + sleep/warm-start state across frames). A sphere is a solver Sphere; any other collidable
        // Object3d is simulated as its OBB (Stage 2: dynamic meshes are boxed; real-triangle dynamic meshes
        // are Stage 5). Two-way dynamic-vs-dynamic is still inert (only dynamic-vs-static generates contacts).
        var live = new HashSet<GameObject>();
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!(inst.Gravity && inst.Collides)) continue;
            if (inst is Sphere sph)
            {
                live.Add(inst);
                if (!_impBodies.TryGetValue(inst, out var rb)) { rb = RigidBody.Sphere(sph.Position, sph.R, MathF.Max(1e-4f, inst.Mass)); _impBodies[inst] = rb; }
                rb.Radius = sph.R;
                rb.Restitution = inst.Restitution; rb.Friction = inst.Friction; rb.RollingFriction = inst.RollingFriction; rb.Tag = e;
                w.Bodies.Add(rb);
            }
            else if (inst is Object3d o)
            {
                live.Add(inst);
                Vector3 half = o.Size * (0.5f * o.Scale);
                float mass = MathF.Max(1e-4f, inst.Mass);
                if (!_impBodies.TryGetValue(inst, out var rb))
                {
                    Vector3 center = (o.LocalCenter * o.Scale).Rotate(o.LocalRotate) + o.Position;
                    rb = RigidBody.DynamicBox(center, half, QuatFromEuler(o.LocalRotate), mass);
                    _impBodies[inst] = rb;
                }
                else rb.SetBoxShape(half, mass);   // refresh shape/inertia (scale/mass may have been edited)
                rb.Restitution = inst.Restitution; rb.Friction = inst.Friction; rb.RollingFriction = inst.RollingFriction; rb.Tag = e;
                w.Bodies.Add(rb);
            }
        }
        // Prune bodies whose object was deleted / lost gravity.
        if (_impBodies.Count != live.Count)
            foreach (var k in _impBodies.Keys.Where(k => !live.Contains(k)).ToList()) _impBodies.Remove(k);

        // Static collidables -> fresh static bodies each frame (they carry no solver state). A gravity+collides
        // object is a DYNAMIC body (added above); everything else that collides is a static support.
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!inst.Collides || inst.Gravity) continue;
            if (inst is Sphere ss) w.Bodies.Add(RigidBody.StaticSphere(ss.Position, ss.R));
            else if (inst is Object3d o) w.Bodies.Add(BuildStaticMeshBody(o));
        }

        w.Step(dt);

        // Write solved transforms back to the engine instances (+ mark for network sync).
        foreach (var kv in _impBodies)
        {
            var go = kv.Key; var rb = kv.Value;
            bool moved = false;
            if (go is Sphere sph)
            {
                moved = (sph.Position - rb.Position).Length() > 1e-6f;
                sph.Position = rb.Position;
            }
            else if (go is Object3d o)
            {
                Vector3 newRot = EulerFromQuat(rb.Orientation);
                Vector3 newPos = rb.Position - (o.LocalCenter * o.Scale).Rotate(newRot);   // solver Position is the OBB centre; back out the anchor offset
                moved = (newPos - o.Position).Length() > 1e-6f || (newRot - o.LocalRotate).Length() > 1e-5f;
                o.Position = newPos;
                o.LocalRotate = newRot;
                o.UpdateGeometry();
            }
            if (moved && _online && _isServer && rb.Tag is EditEntry ee) _physMoved.Add(ee.Descriptor.Id);
        }
    }

    // Build a STATIC support body from an Object3d. It carries the world triangles (the sphere-vs-mesh contact
    // uses the REAL surface) AND a solid BOX VIEW (its world AABB, floored to a minimum downward thickness so a
    // zero-thickness platform quad is still a solid support) that the dynamic-box manifold rests on. A rotated
    // static mesh is boxed by its (loose) AABB — the real-triangle box-vs-mesh contact is Stage 5.
    private static RigidBody BuildStaticMeshBody(Object3d o)
    {
        var wv = o.WorldVertices;
        var verts = new Vector3[wv.Length];
        System.Array.Copy(wv, verts, wv.Length);
        var faces = o.Faces;
        var tris = new int[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++) { var f = faces[i]; tris[i * 3] = f.I0; tris[i * 3 + 1] = f.I1; tris[i * 3 + 2] = f.I2; }
        var body = RigidBody.StaticMesh(verts, tris);

        const float MinThick = 0.5f;
        Vector3 mn = o.WorldMin, mx = o.WorldMax;
        if (mx.Y - mn.Y < MinThick) mn.Y = mx.Y - MinThick;              // extend downward, keep the top face
        body.Position = (mn + mx) * 0.5f;
        body.HalfExtents = (mx - mn) * 0.5f;
        body.Orientation = Quat.Identity;
        body.BoxView = true;
        return body;
    }

    // Full 3D Separating-Axis-Theorem between two oriented boxes A and B (center, 3 orthonormal axes,
    // half-extents). Tests 15 axes — the 3 faces of each box + the 9 edge×edge cross products — and
    // returns overlap + the minimum-penetration UNIT normal (pointing A->B) + the penetration depth, or
    // hit=false at the first separating axis. Near-degenerate cross axes (parallel edges) are skipped;
    // the face axes already cover those. Pure + tested. The impulse box-box manifold uses this normal.
    public static (bool hit, Vector3 normal, float depth) SatBox3D(
        Vector3 cA, Vector3 ax0, Vector3 ax1, Vector3 ax2, Vector3 hA,
        Vector3 cB, Vector3 bx0, Vector3 bx1, Vector3 bx2, Vector3 hB)
    {
        Span<Vector3> axes = stackalloc Vector3[15];
        axes[0] = ax0; axes[1] = ax1; axes[2] = ax2;
        axes[3] = bx0; axes[4] = bx1; axes[5] = bx2;
        Span<Vector3> ea = stackalloc Vector3[3]; ea[0] = ax0; ea[1] = ax1; ea[2] = ax2;
        Span<Vector3> eb = stackalloc Vector3[3]; eb[0] = bx0; eb[1] = bx1; eb[2] = bx2;
        int k = 6;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                axes[k++] = Vector3.Cross(ea[i], eb[j]);

        Vector3 dC = cB - cA;
        float minOv = float.MaxValue; Vector3 best = new Vector3(0f, 1f, 0f);
        for (int a = 0; a < 15; a++)
        {
            Vector3 L = axes[a];
            float len2 = L * L;                                  // Vector3*Vector3 = dot
            if (len2 < 1e-9f) continue;                          // degenerate axis (parallel edges) — faces cover it
            Vector3 Ln = L * (1f / MathF.Sqrt(len2));
            float rA = MathF.Abs(ax0 * Ln) * hA.X + MathF.Abs(ax1 * Ln) * hA.Y + MathF.Abs(ax2 * Ln) * hA.Z;
            float rB = MathF.Abs(bx0 * Ln) * hB.X + MathF.Abs(bx1 * Ln) * hB.Y + MathF.Abs(bx2 * Ln) * hB.Z;
            float dist = dC * Ln;
            float ov = rA + rB - MathF.Abs(dist);
            if (ov <= 0f) return (false, new Vector3(0f, 1f, 0f), 0f);          // separating axis -> no contact
            if (ov < minOv) { minOv = ov; best = dist < 0f ? Ln * -1f : Ln; }   // normal points A->B
        }
        return (true, best, minOv);
    }

    // Server: every PhysicsSyncEvery frames, stream the positions of objects physics moved since the
    // last flush as one compact PhysicsSyncPacket, then clear the changed set. Only changed objects
    // travel; a resting object's final settling move is its last entry, after which it goes quiet.
    private void FlushPhysicsSync()
    {
        if (!_online || !_isServer || _netManager == null) return;
        if (++_physFrame < PhysicsSyncEvery) return;
        _physFrame = 0;
        if (_physMoved.Count == 0) return;

        var ids = new List<int>(_physMoved.Count);
        var pos = new List<Vector3>(_physMoved.Count);
        var vel = new List<Vector3>(_physMoved.Count);
        var rot = new List<Vector3>(_physMoved.Count);
        var ang = new List<Vector3>(_physMoved.Count);
        foreach (int id in _physMoved)
        {
            int idx = _editables.FindIndex(e => e.Descriptor.Id == id);
            if (idx < 0) continue;                               // deleted since it moved
            var inst = _editables[idx].Instance;
            // full velocity from the solver's RigidBody (every moved object is a solver body).
            Vector3 lin = Vector3.Zero, angv = Vector3.Zero;
            if (_impBodies.TryGetValue(inst, out var rb)) { lin = rb.LinVel; angv = rb.AngVel; }
            ids.Add(id);
            pos.Add(inst.Position);
            vel.Add(lin);
            rot.Add(inst.LocalRotate);
            ang.Add(angv);
        }
        _physMoved.Clear();
        if (ids.Count == 0) return;
        _netManager.SendPacket(new PhysicsSyncPacket
        {
            Ids = ids.ToArray(), Positions = pos.ToArray(), LinVel = vel.ToArray(), Rotations = rot.ToArray(), AngVel = ang.ToArray(),
        }, _myNetId);
    }

    // Client: a position batch arrived (main thread, via ProcessEvents). Store/refresh each object's
    // interpolation target; StepNetworkPhysics eases the live instance toward it every frame.
    private void OnPhysicsSyncReceived(PhysicsSyncPacket packet, int senderId)
    {
        for (int i = 0; i < packet.Ids.Length; i++)
        {
            int id = packet.Ids[i];
            if (!_physTargets.TryGetValue(id, out var t)) { t = new NetTarget(); _physTargets[id] = t; }
            t.Pos = packet.Positions[i];
            t.Lin = packet.LinVel[i];
            t.Rot = packet.Rotations[i];
            t.AngVel = packet.AngVel[i];
        }
    }

    // Client: ease each synced object toward its target (dead-reckoning the ongoing fall by velocity
    // between sparse batches), so falling/resting looks smooth even though the client never simulates.
    // View-only peers only; the authority moves objects directly in StepPhysics.
    private void StepNetworkPhysics(float dt)
    {
        if (CanEdit || _physTargets.Count == 0) return;
        DoNetworkInterp(dt);
    }

    private void DoNetworkInterp(float dt)
    {
        foreach (var kv in _physTargets)
        {
            int idx = _editables.FindIndex(e => e.Descriptor.Id == kv.Key);
            if (idx < 0) continue;
            var entry = _editables[idx];
            var (cur, tgt) = StepInterpolate(entry.Instance.Position, kv.Value.Pos, kv.Value.Lin, dt, PhysLerpRate);
            kv.Value.Pos = tgt;                                  // remember the dead-reckoned target

            // Dead-reckon the spin between sparse batches by the synced angular velocity (advance the target
            // pose by ω·dt — mirrors the position dead-reckon), then ease toward it (shortest-angle per axis,
            // so a ±π wrap never spins the long way). Lets peers see physics-driven spin, not just position.
            Vector3 advRot = kv.Value.Rot + kv.Value.AngVel * dt;
            kv.Value.Rot = advRot;                               // remember the dead-reckoned pose
            Vector3 lr = entry.Instance.LocalRotate;
            float f = Math.Clamp(PhysLerpRate * dt, 0f, 1f);
            Vector3 rot = new Vector3(LerpAngle(lr.X, advRot.X, f), LerpAngle(lr.Y, advRot.Y, f), LerpAngle(lr.Z, advRot.Z, f));

            bool moved = MathF.Abs(cur.X - entry.Instance.Position.X) > 1e-6f
                      || MathF.Abs(cur.Y - entry.Instance.Position.Y) > 1e-6f
                      || MathF.Abs(cur.Z - entry.Instance.Position.Z) > 1e-6f;
            bool turned = MathF.Abs(rot.X - lr.X) > 1e-6f || MathF.Abs(rot.Y - lr.Y) > 1e-6f || MathF.Abs(rot.Z - lr.Z) > 1e-6f;
            if (moved) entry.Instance.Position = cur;
            if (turned) entry.Instance.LocalRotate = rot;
            if (moved || turned)
            {
                if (entry.Instance is Object3d oo) oo.UpdateGeometry();
                SyncLightToMarker(entry);
            }
        }
    }

    // Interpolate an angle toward a target along the SHORTEST arc (wrapping ±π), fraction t in [0,1].
    public static float LerpAngle(float a, float b, float t)
    {
        float d = MathF.Atan2(MathF.Sin(b - a), MathF.Cos(b - a));   // shortest signed delta
        return a + d * t;
    }

    // Pure interpolation step for a network-synced position: first extrapolate the target forward by its
    // FULL linear velocity (dead-reckon the ongoing motion — fall/roll/tumble — between sparse batches; a
    // no-op once lin is 0, i.e. at rest), then exponentially ease the current position toward it. rate is
    // the ease speed (1/sec). Returns (new current, advanced target). Kept pure + static so physicstest covers it.
    public static (Vector3 cur, Vector3 tgt) StepInterpolate(Vector3 cur, Vector3 tgt, Vector3 lin, float dt, float rate)
    {
        tgt += lin * dt;
        float f = Math.Clamp(rate * dt, 0f, 1f);
        cur += (tgt - cur) * f;
        return (cur, tgt);
    }

}
