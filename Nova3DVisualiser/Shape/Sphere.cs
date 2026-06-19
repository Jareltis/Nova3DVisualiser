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

        return new RenderData(intersection, normal, intersectionPoint, this.Color);
    }
}