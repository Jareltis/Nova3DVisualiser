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
}
