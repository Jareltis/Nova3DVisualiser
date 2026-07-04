using Nova3DVisualiser;
using SampleGame.Scenes;
using Quat = SampleGame.Physics.Quat;

namespace SampleGame.Physics;

// ---- Stages 1–2 of the impulse-based rigid-body constraint solver (see PHYSICS.md). ----
// A self-contained, dependency-free module. It REUSES the engine's geometry/math (Quat +
// IntegrateQuat, ClosestPointOnTriangle, the world inverse-inertia rotation R·I⁻¹·Rᵀ); it only
// replaces the integration + contact-resolution ARCHITECTURE.

public enum ColliderKind { Sphere, Box, Mesh }

// One rigid body. A STATIC body has InvMass == 0 (and InvInertiaLocal == 0) and is never moved;
// a DYNAMIC body carries inverse mass + principal inverse inertia. The collision SHAPE travels
// with the body: a Sphere (Radius), a Box (HalfExtents, centred at Position, oriented by
// Orientation), or a Mesh (a world-space triangle soup — verts + 3-index-per-face triangles).
public sealed class RigidBody
{
    public Vector3 Position;
    public Quat Orientation = Quat.Identity;
    public Vector3 LinVel;
    public Vector3 AngVel;                  // world frame
    public float InvMass;                   // 0 => static / immovable
    public Vector3 InvInertiaLocal;         // diagonal principal inverse inertia; 0 => static / no spin

    // ---- shape ----
    public ColliderKind Kind;
    public float Radius;                    // Sphere
    public Vector3 HalfExtents;             // Box (centred at Position, oriented by Orientation)
    public Vector3[]? MeshVerts;            // Mesh (world-space vertices)
    public int[]? MeshTris;                 // Mesh (flattened triangle indices, 3 per face)
    // Stage 2: a static MESH body ALSO carries a box view (its solid world AABB, Position/HalfExtents/
    // Orientation set at build time) so a dynamic BOX can rest on it via the box manifold while a dynamic
    // SPHERE still uses the real triangles (MeshVerts). BoxView is true once those box fields are valid.
    public bool BoxView;

    // ---- material (Stage 2) ----
    // Restitution: <0 => inherit the world default (ImpulseWorld.Restitution); 0..1 explicit. Combined
    // per contact (geometric mean), mirroring the scene's CombineRestitution.
    public float Restitution = -1f;
    // Coulomb friction coefficient μ (>=0). Combined per contact (geometric mean). Default 0.5.
    public float Friction = 0.5f;
    // Rolling-friction coefficient (Stage 6): a bounded resistance to SPIN while in contact, so a rolling ball
    // decelerates and stops (and a tumble damps to rest) instead of rolling forever. Small by default so it
    // never freezes legitimate motion (a ball still rolls down a slope). Combined per contact (geometric mean).
    public float RollingFriction = 0.05f;

    // ---- split-impulse scratch (position correction, discarded each substep so no energy is injected) ----
    public Vector3 PseudoLin;
    public Vector3 PseudoAng;

    // ---- sleeping ----
    public bool Sleeping;
    public int CalmSteps;
    public bool Calm;             // scratch: below the sleep threshold THIS substep
    public float RollBudget;      // scratch: accumulated rolling-friction angular-impulse budget (Σ combinedRoll·normalImpulse) this substep
    // sleeping-island (Stage 6) union-find scratch: UF = parent pointer, IslandCalm = every member calm (set on the root).
    public RigidBody? UF;
    public bool IslandCalm;

    // stable identity for warm-start contact keys + a back-reference the scene uses to write results back
    public readonly int Id = System.Threading.Interlocked.Increment(ref _nextId);
    public object? Tag;
    private static int _nextId;

    public bool IsStatic => InvMass == 0f;

    // Velocity of the world point 'p' on this body (rigid: linear + ω × r).
    public Vector3 VelocityAt(Vector3 p) => LinVel + Vector3.Cross(AngVel, p - Position);
    // Same but for the split-impulse pseudo velocities (position pass only).
    public Vector3 PseudoVelocityAt(Vector3 p) => PseudoLin + Vector3.Cross(PseudoAng, p - Position);

    // ---- factories ----
    public static RigidBody Sphere(Vector3 pos, float radius, float mass)
    {
        float invM = mass > 0f ? 1f / mass : 0f;
        // Solid sphere: I = (2/5) m r² (isotropic) -> inverse per principal axis.
        float I = 0.4f * mass * radius * radius;
        float invI = I > 0f ? 1f / I : 0f;
        return new RigidBody
        {
            Position = pos, Kind = ColliderKind.Sphere, Radius = radius,
            InvMass = invM, InvInertiaLocal = new Vector3(invI, invI, invI),
        };
    }

    // Dynamic OBB. HalfExtents are in the body's local frame (oriented by Orientation, centred at the
    // centre of mass = Position). Inertia = the solid-box tensor (BoxInertia over the FULL side lengths).
    public static RigidBody DynamicBox(Vector3 center, Vector3 halfExtents, Quat orientation, float mass)
    {
        var b = new RigidBody { Position = center, Orientation = orientation, Kind = ColliderKind.Box };
        b.SetBoxShape(halfExtents, mass);
        return b;
    }

    // Refresh a dynamic box's shape + inverse mass/inertia (called each frame so a live scale/mass edit
    // is picked up); leaves Position/Orientation/velocities untouched — the solver owns those.
    public void SetBoxShape(Vector3 halfExtents, float mass)
    {
        HalfExtents = halfExtents;
        InvMass = mass > 0f ? 1f / mass : 0f;
        Vector3 I = PhysicsMath.BoxInertia(mass, 2f * halfExtents.X, 2f * halfExtents.Y, 2f * halfExtents.Z);
        InvInertiaLocal = new Vector3(
            I.X > 0f ? 1f / I.X : 0f,
            I.Y > 0f ? 1f / I.Y : 0f,
            I.Z > 0f ? 1f / I.Z : 0f);
    }

    public static RigidBody StaticSphere(Vector3 pos, float radius)
        => new RigidBody { Position = pos, Kind = ColliderKind.Sphere, Radius = radius };

    public static RigidBody StaticBox(Vector3 center, Vector3 halfExtents, Quat orientation)
        => new RigidBody { Position = center, Orientation = orientation, Kind = ColliderKind.Box, HalfExtents = halfExtents, BoxView = true };

    public static RigidBody StaticMesh(Vector3[] worldVerts, int[] tris)
        => new RigidBody { Kind = ColliderKind.Mesh, MeshVerts = worldVerts, MeshTris = tris };
}
