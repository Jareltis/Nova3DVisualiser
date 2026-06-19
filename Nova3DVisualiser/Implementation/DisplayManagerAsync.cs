using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nova3DVisualiser.Implementation;
public class DisplayManagerAsync : IDisplaysManagerAsync
{
    public RenderData FindClosestIntersection(Ray ray, List<IDisplays> displays)
    {
        RenderData closestData = RenderData.NoRender;

        foreach (var display in displays)
        {
            if (display.BoundingSphereMissed(ray.RayStart, ray.RayDirection)) continue;

            var currentData = display.GetRenderData(ray);
            if (currentData.Intersection > -1)
            {
                if (closestData.Intersection == -1 || currentData.Intersection < closestData.Intersection)
                {
                    closestData = currentData;
                }
            }
        }
        return closestData;
    }
}
