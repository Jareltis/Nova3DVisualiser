using Nova3DVisualiser;

namespace SampleGame.Physics;

// Pure rigid-body material/mass math extracted verbatim from PriviewNetworkScene (zero behaviour change).
// CombineRestitution is covered by physicstest; BoxInertia is used by RigidBody.DynamicBox/StaticBox.
public static class PhysicsMath
{
    // Combined coefficient of restitution for a contact between two bodies: the geometric mean
    // sqrt(eA·eB), so a rebound needs BOTH surfaces elastic — a dead/soft surface kills the bounce
    // (a superball on mud barely bounces), two springy ones stay springy. Negatives clamp to 0.
    // Pure + tested. (max() would be "trampoline" semantics; the geometric mean is the energy model.)
    public static float CombineRestitution(float a, float b) => MathF.Sqrt(MathF.Max(0f, a) * MathF.Max(0f, b));

    // Diagonal box inertia tensor — the principal moments about X/Y/Z for a solid box of size
    // (sx,sy,sz) and mass: I_x = m(sy²+sz²)/12, I_y = m(sx²+sz²)/12, I_z = m(sx²+sy²)/12. Returned as
    // a Vector3 of the three moments (a world-axis approximation; exact when the box is axis-aligned).
    public static Vector3 BoxInertia(float mass, float sx, float sy, float sz) => new Vector3(
        mass * (sy * sy + sz * sz) / 12f,
        mass * (sx * sx + sz * sz) / 12f,
        mass * (sx * sx + sy * sy) / 12f);
}
