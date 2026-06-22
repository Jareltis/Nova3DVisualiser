using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.StaticClass;

namespace Nova3DVisualiser.AbstractClass;

public enum LightKind { Point, Directional, Spot, Area }
public enum ConeShapeKind { Circle, Square, Triangle }

public class Light (Vector3 position, float lightPower) : GameObject(position, Vector3.Zero)
{
    public float LightPower = lightPower;

    // Rich light parameters. Defaults reproduce a plain omnidirectional point light, so an
    // unconfigured Light behaves exactly as before.
    public LightKind Kind = LightKind.Point;
    public Vector3 Rgb = new Vector3(1f, 1f, 1f);          // RGB emission (0..1); white => today's look
    public Vector3 Direction = new Vector3(0f, -1f, 0f);   // aim (normalized) for Directional/Spot/Area
    public float ConeAngleDeg = 30f;                       // Spot: half-angle of the cone (degrees)
    public float AreaSize = 1f;                            // Area: half-extent of the square emitter
    public float SpinSpeed = 0f;                           // rad/sec: sweeps Direction about world Y (ignored for Point)
    public int BeamCount = 1;                              // Spot: cones fanned evenly about the aim (>=1; 1 == today)
    public ConeShapeKind ConeShape = ConeShapeKind.Circle; // Spot: cone cross-section shape

    private const float Bias = 0.01f;        // shadow-ray self-occlusion bias (matches the old epsilon)
    private const float DirRefDistSq = 64f;  // Directional: fixed mild attenuation (no real distance)

    // Sweeps Direction about world Y by SpinSpeed*dt. No-op for a point light or zero speed.
    public void Spin()
    {
        if (SpinSpeed == 0f || Kind == LightKind.Point) return;
        Direction = Direction.Rotate(new Vector3(0f, SpinSpeed * GameTime.GetDeltaTime(), 0f)).Norm();
    }

    /// <summary>
    /// One light's ADDITIVE contribution at the shaded point. Each light is shadow-tested on its
    /// OWN: a point blocked from THIS light contributes 0, but the caller still adds every other
    /// unblocked light, so a surface shadowed from one light is lit by another. There is no global,
    /// min, or multiplied shadow factor anywhere — that is the multi-light dominance fix.
    /// </summary>
    public float Contribution(RenderData rd, List<IDisplays> sceneObjects, IDisplaysManagerAsync mgr, bool shadows)
    {
        return Kind switch
        {
            LightKind.Directional => DirectionalTerm(rd, sceneObjects, mgr, shadows),
            LightKind.Spot        => SpotTerm(rd, sceneObjects, mgr, shadows),
            LightKind.Area        => AreaTerm(rd, sceneObjects, mgr, shadows),
            _                     => PointTerm(Position, rd, sceneObjects, mgr, shadows),
        };
    }

    // Lambert * inverse-square from a point emitter at lightPos, with an independent occlusion ray.
    private float PointTerm(Vector3 lightPos, RenderData rd, List<IDisplays> objs, IDisplaysManagerAsync mgr, bool shadows)
    {
        Vector3 offset = lightPos - rd.IntersectionPoint;
        float dist = offset.Length();
        if (dist < 1e-6f) return 0f;
        Vector3 l = offset / dist;

        float ndl = rd.Normal * l;
        if (ndl <= 0f) return 0f;                          // surface faces away from this light

        if (shadows && Occluded(rd, l, dist, objs, mgr)) return 0f;
        return ndl * (LightPower / (dist * dist + 1f));
    }

    // Parallel rays along -Direction (a sun): no real distance attenuation, occlusion out to far.
    private float DirectionalTerm(RenderData rd, List<IDisplays> objs, IDisplaysManagerAsync mgr, bool shadows)
    {
        Vector3 l = (Direction * -1f).Norm();              // toward the (infinitely far) light
        float ndl = rd.Normal * l;
        if (ndl <= 0f) return 0f;

        if (shadows && Occluded(rd, l, 1e4f, objs, mgr)) return 0f;
        return ndl * (LightPower / (DirRefDistSq + 1f));   // mild constant attenuation
    }

    // A spot throws BeamCount cones whose axes are the aim fanned evenly about world Y (or world X if
    // the aim is near-vertical, where a Y-fan would degenerate). The cone factor is the MAX over the
    // beams; the point + shadow term is shared (all beams emit from Position), so it is computed once.
    // BeamCount==1 (and the default Circle shape) reproduces the original single round cone exactly.
    private float SpotTerm(RenderData rd, List<IDisplays> objs, IDisplaysManagerAsync mgr, bool shadows)
    {
        int beams = Math.Max(1, BeamCount);
        bool nearVertical = MathF.Abs(Direction.Norm().Y) > 0.99f;

        float maxCone = 0f;
        for (int k = 0; k < beams; k++)
        {
            float ang = k * (MathF.Tau / beams);           // even fan: 360/N per beam
            Vector3 axis = (nearVertical
                ? Direction.Rotate(new Vector3(ang, 0f, 0f))    // aim ~vertical: fan about world X
                : Direction.Rotate(new Vector3(0f, ang, 0f))).Norm();   // otherwise fan about world Y
            float c = ConeFactor(axis, rd);
            if (c > maxCone) maxCone = c;
        }
        if (maxCone <= 0f) return 0f;                      // outside every beam
        return PointTerm(Position, rd, objs, mgr, shadows) * maxCone;
    }

    // Smooth cone factor [0,1] for one beam axis. Circle reuses the original cosine-of-angle edge so a
    // single circular beam is byte-identical to before; Square/Triangle use a cross-section projection.
    private float ConeFactor(Vector3 axis, RenderData rd)
        => ConeShape == ConeShapeKind.Circle ? CircleConeFactor(axis, rd) : ShapedConeFactor(axis, rd);

    // Original round cone: angle between (P - light) and the beam axis vs ConeAngle, smoothstep edge.
    private float CircleConeFactor(Vector3 axis, RenderData rd)
    {
        Vector3 toPoint = rd.IntersectionPoint - Position;
        float d = toPoint.Length();
        if (d < 1e-6f) return 0f;
        float cosAng = (toPoint / d) * axis;               // axis is unit

        float outer = MathF.Cos(ConeAngleDeg * (MathF.PI / 180f));
        if (cosAng <= outer) return 0f;
        float inner = MathF.Cos(ConeAngleDeg * 0.8f * (MathF.PI / 180f));
        float t = inner > outer ? Math.Clamp((cosAng - outer) / (inner - outer), 0f, 1f) : 1f;
        return t * t * (3f - 2f * t);
    }

    // Square/Triangle cross-section: project (P - light) onto a plane perpendicular to the beam axis,
    // normalized by axial distance, then test the 2D footprint. tan(ConeAngle) is the circle's radius
    // at unit axial distance; the square uses it as a half-extent, the triangle as a circumradius.
    private float ShapedConeFactor(Vector3 a, RenderData rd)
    {
        Vector3 d = rd.IntersectionPoint - Position;
        float axial = d * a;
        if (axial <= 1e-6f) return 0f;                     // point is behind the beam

        Vector3 worldUp = new Vector3(0f, 1f, 0f);
        Vector3 u = (MathF.Abs(a * worldUp) > 0.99f
            ? Vector3.Cross(a, new Vector3(1f, 0f, 0f))
            : Vector3.Cross(a, worldUp)).Norm();
        Vector3 v = Vector3.Cross(a, u);                   // a,u orthonormal -> v is unit
        float nu = (d * u) / axial;
        float nv = (d * v) / axial;
        float t = MathF.Tan(ConeAngleDeg * (MathF.PI / 180f));

        if (ConeShape == ConeShapeKind.Square)
            return SmoothEdge(MathF.Max(MathF.Abs(nu), MathF.Abs(nv)), t);

        // Equilateral triangle (vertex up), circumradius t -> inradius t/2; inside iff every outward-
        // normal projection is within the inradius (three half-plane tests).
        float p0 = -nv;                                    // bottom edge, outward normal (0,-1)
        float p1 = 0.8660254f * nu + 0.5f * nv;           // outward normal (cos30, sin30)
        float p2 = -0.8660254f * nu + 0.5f * nv;          // outward normal (cos150, sin150)
        return SmoothEdge(MathF.Max(p0, MathF.Max(p1, p2)), t * 0.5f);
    }

    // Full just inside the boundary, smoothstep down to 0 at it (inner edge at 0.8*boundary, matching
    // the round cone's 0.8 inner ramp).
    private static float SmoothEdge(float metric, float boundary)
    {
        if (boundary <= 0f) return 0f;
        float inner = 0.8f * boundary;
        float s = Math.Clamp((boundary - metric) / (boundary - inner), 0f, 1f);
        return s * s * (3f - 2f * s);
    }

    // A square emitter (normal = Direction, half-extent = AreaSize) sampled on a fixed 2x2 grid:
    // each sample is an independently occluded point term, so the average gives area falloff + soft
    // shadows. Sample count is capped at 4 for performance.
    private float AreaTerm(RenderData rd, List<IDisplays> objs, IDisplaysManagerAsync mgr, bool shadows)
    {
        Vector3 n = Direction.Norm();
        Vector3 up = MathF.Abs(n.Y) > 0.99f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
        Vector3 t1 = Vector3.Cross(up, n).Norm();
        Vector3 t2 = Vector3.Cross(n, t1).Norm();
        float h = AreaSize;

        float sum = 0f;
        sum += PointTerm(Position + t1 * (-h) + t2 * (-h), rd, objs, mgr, shadows);
        sum += PointTerm(Position + t1 * ( h) + t2 * (-h), rd, objs, mgr, shadows);
        sum += PointTerm(Position + t1 * (-h) + t2 * ( h), rd, objs, mgr, shadows);
        sum += PointTerm(Position + t1 * ( h) + t2 * ( h), rd, objs, mgr, shadows);
        return sum * 0.25f;
    }

    // True if an occluder blocks the segment from the (biased) shaded point toward the light.
    private static bool Occluded(RenderData rd, Vector3 l, float maxDist, List<IDisplays> objs, IDisplaysManagerAsync mgr)
    {
        Ray shadowRay = new Ray(rd.IntersectionPoint + rd.Normal * Bias, l);
        RenderData hit = mgr.FindClosestIntersection(shadowRay, objs);
        return hit.Intersection > -1f && hit.Intersection < maxDist;
    }
}
