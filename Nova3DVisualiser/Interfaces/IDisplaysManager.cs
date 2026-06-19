using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.Interfaces;

public interface IDisplaysManager
{
    public void FindAllRenderData(Ray ray, List<IDisplays> displays);
    public RenderData GetNearbyRenderData();
}