using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.Shape;

public class Sphere(Vector3 position, Vector3 localRotate, float r = 1) : GameObject(position, localRotate), IDisplays
{
    public float R = r;

    public bool BoundingSphereMissed(Vector3 rayStart, Vector3 unitDir)
    {
        Vector3 L = Position - rayStart;
        float tca = L * unitDir;
        float d2 = L * L - tca * tca;
        return d2 > R * R;
    }

    public RenderData GetRenderData(Ray ray)
    {
        
        Vector3 l = (ray.RayStart - Position).Rotate(LocalRotate);
        float a = ray.RayDirection * ray.RayDirection;
        float b = 2 * (l * ray.RayDirection);
        float c = l * l - R * R;

        float d = b * b - 4 * a * c;
        if (d < 0)
        { return RenderData.NoRender; }

        d = (float)Math.Sqrt(d);

        float intersection = (-b - d) / (2 * a);
        Vector3 intersectionPoint = ray.GetIntersectionPoint(intersection);
        Vector3 normal = (intersectionPoint - Position).Norm();

        // Untextured (or a TextureFace that isn't the sphere's single "whole" group 0/ALL): flat colour,
        // EXACTLY as before (the byte-identical invariant). Textured: an equirectangular (lat/long) UV
        // from the surface direction expressed in the sphere's LOCAL frame (undo LocalRotate), so the
        // image rotates with the object — scaled by TextureScale (tiling) then sampled + paled.
        if (this.Texture == null || !(this.TextureFace == -1 || this.TextureFace == 0))
            return new RenderData(intersection, normal, intersectionPoint, this.EffectiveColor);

        Vector2 uv = EquirectangularUv(normal.RotateInverse(LocalRotate));
        float s = this.TextureScale;
        Rgba32 texel = this.TextureFilter == TextureFilterMode.Bilinear
            ? this.Texture.SampleBilinear(uv.X * s, uv.Y * s)
            : this.Texture.Sample(uv.X * s, uv.Y * s);
        return new RenderData(intersection, normal, intersectionPoint, ShadeTexel(texel), uv);
    }

    // Equirectangular (lat/long) mapping of a UNIT direction to a texture coordinate: u wraps around
    // the equator via atan2(z,x) (seam at -X where u wraps 0↔1), v runs pole-to-pole via asin(y).
    // The GPU kernel's SphereUv replicates this exactly, but its transcendental atan2/asin may round
    // slightly differently — hence a thin seam/pole band is tolerated in gputest (like the shadow band).
    public static Vector2 EquirectangularUv(Vector3 dir)
    {
        const float Tau = 6.2831853f;
        const float Pi = 3.14159265f;
        float u = 0.5f + MathF.Atan2(dir.Z, dir.X) / Tau;
        float v = 0.5f - MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) / Pi;
        return new Vector2(u, v);
    }
}