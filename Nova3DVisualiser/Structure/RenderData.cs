namespace Nova3DVisualiser;

public struct RenderData(float intersection = -1, Vector3 normal = default, Vector3 intersectionPoint = default, Rgba32 color = default)
{
    public float Intersection = intersection;
    public Vector3 Normal = normal;
    public Vector3 IntersectionPoint = intersectionPoint;
    public Rgba32 Color = color;

    public static RenderData NoRender = new RenderData(-1, Vector3.Zero, Vector3.Zero, default);
}