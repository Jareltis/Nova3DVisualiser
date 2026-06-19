using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.AbstractClass;

public class Light (Vector3 position, float lightPower) : GameObject(position, Vector3.Zero)
{
    public float LightPower = lightPower;
    //private readonly IDisplaysManager _displaysManager = new DisplaysManager();

    private float CalculateBrightness(RenderData renderData)
    {
        Vector3 offset = Position - renderData.IntersectionPoint;
        float distance = offset.Length();

        Vector3 lightDir = offset / distance;

        float angleFactor = Math.Max(0, renderData.Normal * lightDir);

        float attenuation = LightPower / (distance * distance + 1f);

        return angleFactor * attenuation;
    }

    public virtual float PointBright(RenderData renderData)
    {
        return CalculateBrightness(renderData);
    }

    public virtual float PointBright(RenderData renderData, List<IDisplays> sceneObjects, IDisplaysManagerAsync displaysManager)
    {
        Vector3 lightDir = (Position - renderData.IntersectionPoint).Norm();

        float dot = renderData.Normal * lightDir;
        if (dot <= 0) return 0f;

        float distanceToLight = (Position - renderData.IntersectionPoint).Length();
        const float epsilon = 0.01f;

        Ray shadowRay = new Ray(renderData.IntersectionPoint + renderData.Normal * epsilon, lightDir);
        RenderData shadowHit = displaysManager.FindClosestIntersection(shadowRay, sceneObjects);

        if (shadowHit.Intersection > -1 && shadowHit.Intersection < distanceToLight)
        {
            return 0f;
        }

        return CalculateBrightness(renderData);
    }
}