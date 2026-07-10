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
    private const int MaxSyncEntries = 20;                             // E4: chunk a flush at ≤20 entries/datagram — 20×52B + 4 count + 12 UDP header ≈ 1056B, safely under ~1200 (no IP fragmentation)
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
    // C2-5: the o.Scale each dynamic-MESH hull body was built with — a hull's geometry/inertia depend on scale, so a
    // scale edit REBUILDS the ConvexHull (expensive quickhull, so build-once otherwise). Pruned alongside _impBodies.
    private readonly Dictionary<GameObject, float> _hullScale = new();

    // C1-4a joints: persistent Joint objects (so their accumulated-impulse warm-start survives frames, like bodies).
    private readonly Dictionary<JointConfig, Joint> _impJoints = new();                          // one live Joint per world JointConfig
    private readonly Dictionary<JointConfig, (RigidBody a, RigidBody b)> _impJointBuiltWith = new();  // the bodies each joint was built against (rebuild on change)
    private readonly HashSet<JointConfig> _impJointsWarned = new();                              // coalesce the "joint skipped" warning to once per config
    private RigidBody? _worldAnchor;                                                             // shared immovable WORLD anchor (origin) for BodyId -1 joint endpoints

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
                float mass = MathF.Max(1e-4f, inst.Mass);
                // C2-5: a gravity+collides custom MESH (descriptor Type "mesh") simulates as its true convex HULL, so
                // it rests on faces / tumbles like its real shape; every box-like PRIMITIVE (cube/ramp/pyramid/…) stays
                // an OBB exactly as before (a cube's OBB == its hull, and BoxVsBox is cheaper).
                bool isMesh = string.Equals(e.Descriptor.Type, "mesh", StringComparison.OrdinalIgnoreCase);
                RigidBody rb;
                if (isMesh)
                {
                    // Build the hull ONCE (quickhull is expensive) — rebuild ONLY when o.Scale changed (the hull
                    // geometry/inertia depend on scale), preserving velocity/sleep so a live scale edit keeps motion.
                    bool have = _impBodies.TryGetValue(inst, out rb!);
                    bool scaleChanged = !have || !_hullScale.TryGetValue(inst, out float bs) || bs != o.Scale;
                    if (scaleChanged)
                    {
                        Vector3 lin = Vector3.Zero, angv = Vector3.Zero; bool sleeping = false;
                        if (have) { lin = rb.LinVel; angv = rb.AngVel; sleeping = rb.Sleeping; }
                        var locals = new Vector3[o.LocalVertices.Length];
                        for (int i = 0; i < locals.Length; i++) locals[i] = o.LocalVertices[i] * o.Scale;   // scaled locals (matches the OBB's Size·Scale)
                        rb = RigidBody.DynamicHull(locals, mass);
                        rb.Position = rb.HullLocalCom.Rotate(o.LocalRotate) + o.Position;   // COM offset replaces LocalCenter
                        rb.Orientation = Quaternions.QuatFromEuler(o.LocalRotate);
                        rb.LinVel = lin; rb.AngVel = angv; rb.Sleeping = sleeping;
                        _impBodies[inst] = rb; _hullScale[inst] = o.Scale;
                    }
                    // else: reuse as-is — the solver owns Position/Orientation/velocity across frames.
                }
                else
                {
                    Vector3 half = o.Size * (0.5f * o.Scale);
                    if (!_impBodies.TryGetValue(inst, out rb!))
                    {
                        Vector3 center = (o.LocalCenter * o.Scale).Rotate(o.LocalRotate) + o.Position;
                        rb = RigidBody.DynamicBox(center, half, Quaternions.QuatFromEuler(o.LocalRotate), mass);
                        _impBodies[inst] = rb;
                    }
                    else rb.SetBoxShape(half, mass);   // refresh shape/inertia (scale/mass may have been edited)
                }
                rb.Restitution = inst.Restitution; rb.Friction = inst.Friction; rb.RollingFriction = inst.RollingFriction; rb.Tag = e;
                w.Bodies.Add(rb);
            }
        }
        // Prune bodies whose object was deleted / lost gravity.
        if (_impBodies.Count != live.Count)
            foreach (var k in _impBodies.Keys.Where(k => !live.Contains(k)).ToList()) { _impBodies.Remove(k); _hullScale.Remove(k); }

        // Static collidables -> fresh static bodies each frame (they carry no solver state). A gravity+collides
        // object is a DYNAMIC body (added above); everything else that collides is a static support.
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!inst.Collides || inst.Gravity) continue;
            if (inst is Sphere ss) w.Bodies.Add(RigidBody.StaticSphere(ss.Position, ss.R));
            else if (inst is Object3d o) w.Bodies.Add(BuildStaticMeshBody(o));
        }

        // C1-4a: (re)build the world's joints now that every solver body exists this frame. No-op if joint-free.
        BuildFrameJoints(w);

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
                // Back out the anchor offset: a HULL body's Position is its COM (HullLocalCom, already scale-baked); a
                // box/primitive body's is the OBB centre (LocalCenter·Scale). Same rotate-and-subtract either way.
                Vector3 anchorOff = rb.Kind == ColliderKind.Hull ? rb.HullLocalCom : o.LocalCenter * o.Scale;
                Vector3 newPos = rb.Position - anchorOff.Rotate(newRot);
                moved = (newPos - o.Position).Length() > 1e-6f || (newRot - o.LocalRotate).Length() > 1e-5f;
                o.Position = newPos;
                o.LocalRotate = newRot;
                o.UpdateGeometry();
            }
            if (moved && _online && _isServer && rb.Tag is EditEntry ee) _physMoved.Add(ee.Descriptor.Id);
        }
    }

    // C1-4a: (re)build the ImpulseWorld's joints from _world.Joints each frame, PRESERVING warm-start like the
    // bodies do — the live Joint objects live in _impJoints across frames (their accumulated-impulse warm-start
    // persists) and are only rebuilt when a referenced body actually changes (deleted/recreated). A BodyId of -1
    // is the fixed WORLD: a SHARED immovable anchor at the origin, so the joint's LocalAnchor (= the config's
    // AnchorA/B) is the world anchor point. A joint whose body isn't a solver body this frame is skipped
    // (Logger.Warning, coalesced to once per config). No-op for a joint-free world -> bit-identical to before.
    private void BuildFrameJoints(ImpulseWorld w)
    {
        w.Joints.Clear();
        var cfgs = _world.Joints;
        if ((cfgs == null || cfgs.Count == 0) && _impJoints.Count == 0) return;   // fast path: this world has no joints

        // Resolve an object id -> its solver RigidBody: id == -1 -> the shared WORLD anchor (origin); a real id ->
        // the object's persistent dynamic body (null if it isn't a solver body this frame — e.g. static, deleted,
        // or gravity-off). A static anchor at the origin makes AnchorA/B behave as a WORLD position for -1 sides.
        RigidBody? Resolve(int id)
        {
            if (id == -1) return _worldAnchor ??= RigidBody.StaticSphere(Vector3.Zero, 0.001f);
            int idx = _editables.FindIndex(e => e.Descriptor.Id == id);
            if (idx < 0) return null;
            return _impBodies.TryGetValue(_editables[idx].Instance, out var rb) ? rb : null;
        }

        // Drop persistent joints whose config was removed from the world (robust to future edits/deletes).
        if (_impJoints.Count > 0 && cfgs != null)
            foreach (var dead in _impJoints.Keys.Where(k => !cfgs.Contains(k)).ToList())
            { _impJoints.Remove(dead); _impJointBuiltWith.Remove(dead); _impJointsWarned.Remove(dead); }

        if (cfgs == null) return;
        foreach (var cfg in cfgs)
        {
            var a = Resolve(cfg.BodyA);
            var b = Resolve(cfg.BodyB);
            if (a == null || b == null)
            {
                WarnJointOnce(cfg, $"Joint '{cfg.Kind}' ({cfg.BodyA}->{cfg.BodyB}) skipped: a referenced body is not a solver body this frame.");
                _impJoints.Remove(cfg); _impJointBuiltWith.Remove(cfg);
                continue;
            }
            // Reuse the persistent joint (warm-start intact) unless a referenced body changed (deleted/recreated).
            if (!_impJoints.TryGetValue(cfg, out var joint)
                || !_impJointBuiltWith.TryGetValue(cfg, out var built) || built.a != a || built.b != b)
            {
                joint = JointBuilder.BuildJoint(cfg, Resolve);
                if (joint == null)   // unknown Kind
                {
                    WarnJointOnce(cfg, $"Joint kind '{cfg.Kind}' unknown; skipped.");
                    _impJoints.Remove(cfg); _impJointBuiltWith.Remove(cfg);
                    continue;
                }
                _impJoints[cfg] = joint;
                _impJointBuiltWith[cfg] = (a, b);
            }
            _impJointsWarned.Remove(cfg);   // successfully added -> reset the warn latch (re-warns only if it breaks again)
            w.Joints.Add(joint);
        }
    }

    private void WarnJointOnce(JointConfig cfg, string msg) { if (_impJointsWarned.Add(cfg)) Logger.Warning(msg); }

    // C1-4b: the WORLD position of a joint anchor — the placeholder marker + spawn use this to place a joint
    // at the midpoint of its two anchors. A body id of -1 means the anchor is a WORLD point (matching the
    // bridge's world-anchor convention); a real id resolves to the live instance and applies its world
    // transform to the LOCAL anchor. For an Object3d the solver body is its OBB centre (LocalCenter·Scale
    // rotated + Position), so mirror that here so the marker sits where the bridge pins. An unresolved id
    // falls back to treating the stored anchor as a world point.
    private Vector3 JointAnchorWorld(int bodyId, Vec3Config anchor)
    {
        Vector3 a = ToVec(anchor);
        if (bodyId == -1) return a;
        int idx = _editables.FindIndex(e => e.Descriptor.Id == bodyId);
        if (idx < 0) return a;
        var inst = _editables[idx].Instance;
        if (inst is Object3d o)
            return (o.LocalCenter * o.Scale).Rotate(o.LocalRotate) + o.Position + a.Rotate(o.LocalRotate);
        return inst.Position + a;   // a sphere/other: its position is the centre
    }

    // The midpoint of a joint's two world anchors (kept for the descriptor's informational Position).
    private Vector3 JointMidpoint(JointConfig cfg) =>
        (JointAnchorWorld(cfg.BodyA, cfg.AnchorA) + JointAnchorWorld(cfg.BodyB, cfg.AnchorB)) * 0.5f;

    // C1-4c: last-built anchors/axis per joint marker, so UpdateJointMarkers only rebuilds the line mesh when
    // an endpoint (or the hinge axis) actually moved — a resting joint's marker isn't rebuilt every frame.
    private readonly Dictionary<JointConfig, (Vector3 a, Vector3 b, Vector3 axis)> _jointMarkerCache = new();
    private const float JointMarkerEps = 1e-3f;   // rebuild threshold (world units)

    // C1-4c: the hinge rotation axis in WORLD space (Rotate(bodyA.Orientation, cfg.Axis)) — null when the
    // joint is not a hinge or its axis is unset (the marker then draws just the connection line). BodyA -1 /
    // an unresolved id has identity orientation, so the local axis IS the world axis.
    private Vector3? HingeAxisWorld(JointConfig cfg)
    {
        if (!string.Equals(cfg.Kind?.Trim(), "hinge", StringComparison.OrdinalIgnoreCase)) return null;
        Vector3 axis = ToVec(cfg.Axis);
        if (axis.Length() < 1e-6f) return null;
        int idx = _editables.FindIndex(e => e.Descriptor.Id == cfg.BodyA);   // orient by BodyA's world rotation
        if (idx >= 0) axis = axis.Rotate(_editables[idx].Instance.LocalRotate);
        return axis.Norm();
    }

    // C1-4c: keep every joint entry's LINE marker spanning its live anchors (its bodies move under physics /
    // interpolation, and an anchor/body/kind/axis edit reshapes it) — rebuild the tiny mesh + swap it in via
    // ReplaceMarker only when an endpoint or the hinge axis moved past a small epsilon (a resting joint isn't
    // rebuilt). No joint entries → no work, so a joint-free world is bit-identical to before.
    private void UpdateJointMarkers()
    {
        foreach (var e in _editables)
        {
            var cfg = e.Joint;
            if (cfg == null) continue;
            Vector3 wa = JointAnchorWorld(cfg.BodyA, cfg.AnchorA);
            Vector3 wb = JointAnchorWorld(cfg.BodyB, cfg.AnchorB);
            Vector3? axis = HingeAxisWorld(cfg);
            Vector3 axisV = axis ?? Vector3.Zero;
            if (_jointMarkerCache.TryGetValue(cfg, out var last)
                && (wa - last.a).Length() < JointMarkerEps
                && (wb - last.b).Length() < JointMarkerEps
                && (axisV - last.axis).Length() < JointMarkerEps)
                continue;   // nothing moved past epsilon — keep the current marker

            var fresh = BuildJointMarkerMesh(wa, wb, cfg.Kind, axis);
            ReplaceMarker(e, fresh);                     // swap display + _models, keep it non-colliding
            e.Instance.Color = JointColor(cfg.Kind);     // re-apply the kind colour (a kind edit changed it; ReplaceMarker kept the OLD)
            if (e.Instance.Position.Length() > 1e-9f)    // world-space verts → keep the transform at the origin (guards a stray move-key nudge)
            { e.Instance.Position = Vector3.Zero; if (e.Instance is Object3d o) o.UpdateGeometry(); }
            _jointMarkerCache[cfg] = (wa, wb, axisV);
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
        // E3/E4: server->clients physics batch rides UDP. E4 splits a flush into ≤MaxSyncEntries-entry
        // datagrams (each < ~1200 B → no IP fragmentation) that SHARE one seq, so the receive-side filter
        // (monotonic non-decreasing) can't drop a chunk on intra-flush reorder. A strictly-older flush's
        // stragglers are still dropped (latest full frame wins), and a lost chunk/flush is absorbed by the
        // per-id apply + dead-reckoning/easing (OnPhysicsSyncReceived overwrites _physTargets[id];
        // StepInterpolate coasts between batches). A flush of ≤MaxSyncEntries objects is exactly one
        // datagram — identical to E3. SendPacketUnreliableGroup falls back to TCP until the server has
        // learned the client's UDP endpoint (~1 frame after join, via E2).
        var chunks = PhysicsSyncPacket.SplitIntoChunks(ids.ToArray(), pos.ToArray(), vel.ToArray(), rot.ToArray(), ang.ToArray(), MaxSyncEntries);
        _netManager.SendPacketUnreliableGroup(chunks, _myNetId);
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
