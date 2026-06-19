using Nova3DVisualiser.Implementation;

namespace Nova3DVisualiser.Interfaces.modifier;

public interface IDisplays
{
    public RenderData GetRenderData(Ray ray);

    // true => the ray's line is a guaranteed miss of this object's bounding sphere (unitDir must be unit length)
    public bool BoundingSphereMissed(Vector3 rayStart, Vector3 unitDir);
}