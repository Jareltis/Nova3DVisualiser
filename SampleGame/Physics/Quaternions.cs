using Nova3DVisualiser;

namespace SampleGame.Physics;

// ---- Quaternion orientation (drift-free SO(3) integration) ----
// Phase 7 stores a spinning object's orientation as a unit quaternion (the source of truth) and
// converts it to the engine's Euler LocalRotate each frame for rendering. This removes the Euler-
// integration drift of phase 6: angular velocity integrates correctly in SO(3), then we project to
// Euler in the ENGINE'S rotation order (Vector3.Rotate = Rx then Ry then Rz, i.e. R = Rz·Ry·Rx).
// The type + math were extracted verbatim from PriviewNetworkScene (zero behaviour change); covered by
// physicstest and reused by RigidBody / ImpulseWorld / Contact.
public readonly struct Quat
{
    public readonly float X, Y, Z, W;
    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);
}

public static class Quaternions
{
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
}
