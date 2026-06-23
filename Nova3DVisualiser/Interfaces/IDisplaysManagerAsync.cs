using Nova3DVisualiser.Interfaces.modifier;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nova3DVisualiser.Interfaces;
public interface IDisplaysManagerAsync
{
    RenderData FindClosestIntersection(Ray ray, List<IDisplays> displays);

    // Each display's NEAREST hit, sorted front-to-back (Intersection ascending). Used by the primary
    // ray to composite transparent layers; the shadow path still uses FindClosestIntersection.
    List<RenderData> FindSortedIntersections(Ray ray, List<IDisplays> displays);
}
