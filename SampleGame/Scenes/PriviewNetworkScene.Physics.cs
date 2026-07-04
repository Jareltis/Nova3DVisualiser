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

    // Per-object coefficient of restitution, resolving the "inherit world" sentinel: a non-negative
    // stored value is used as-is, a negative one falls back to the world default (PhysicsConfig.Restitution).
    private float RestitutionOf(GameObject o) => o.Restitution >= 0f ? o.Restitution : _world.Physics.Restitution;

    // Quaternion orientation type (Quat) + drift-free SO(3) math (QuatMul/QuatFromEuler/EulerFromQuat/
    // IntegrateQuat) moved to Quaternions (SampleGame.Physics).

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
                    rb = RigidBody.DynamicBox(center, half, Quaternions.QuatFromEuler(o.LocalRotate), mass);
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
                Vector3 newRot = Quaternions.EulerFromQuat(rb.Orientation);
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

    // SatBox3D (full 3D box-vs-box SAT) moved to CollisionMath (SampleGame.Physics).

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
            var (cur, tgt) = NetInterp.StepInterpolate(entry.Instance.Position, kv.Value.Pos, kv.Value.Lin, dt, PhysLerpRate);
            kv.Value.Pos = tgt;                                  // remember the dead-reckoned target

            // Dead-reckon the spin between sparse batches by the synced angular velocity (advance the target
            // pose by ω·dt — mirrors the position dead-reckon), then ease toward it (shortest-angle per axis,
            // so a ±π wrap never spins the long way). Lets peers see physics-driven spin, not just position.
            Vector3 advRot = kv.Value.Rot + kv.Value.AngVel * dt;
            kv.Value.Rot = advRot;                               // remember the dead-reckoned pose
            Vector3 lr = entry.Instance.LocalRotate;
            float f = Math.Clamp(PhysLerpRate * dt, 0f, 1f);
            Vector3 rot = new Vector3(NetInterp.LerpAngle(lr.X, advRot.X, f), NetInterp.LerpAngle(lr.Y, advRot.Y, f), NetInterp.LerpAngle(lr.Z, advRot.Z, f));

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

}
