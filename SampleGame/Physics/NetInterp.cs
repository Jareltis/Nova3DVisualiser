using Nova3DVisualiser;

namespace SampleGame.Physics;

// Pure network dead-reckon / interpolation math extracted verbatim from PriviewNetworkScene (zero behaviour
// change). Covered by physicstest; used by the scene's DoNetworkInterp to ease synced objects toward targets.
public static class NetInterp
{
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
