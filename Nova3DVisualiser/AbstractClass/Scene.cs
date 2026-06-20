using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.UI;

namespace Nova3DVisualiser.AbstractClass;

public abstract class Scene(IDisplaysManagerAsync displaysManager)
{
    private readonly IDisplaysManagerAsync _displaysManager = displaysManager;
    private readonly List<IDisplays> _allDisplays = new List<IDisplays>();
    private readonly List<Light> _allLight = new List<Light>();
    private Camera _renderCamera = new Camera(Vector3.Zero, Vector3.Zero);
    
    public readonly UIManager UI = new UIManager();

    protected float Exposure = 0.05f;
    protected float Ambient = 0.1f;

    public bool EnableShadows = true;

    protected void SetMainCamera(Camera camera)
    { _renderCamera = camera; }
    protected void AddDisplaysObject(IDisplays @object)
    { _allDisplays.Add(@object); }
    protected void RemoveDisplaysObject(IDisplays @object)
    { _allDisplays.Remove(@object); }

    protected void AddLight(Light light)
    { _allLight.Add(light); }
    
    public abstract void Start();
    public abstract void Update();

    public virtual (float Brightness, ConsoleColor Color) GetPixelData(Vector2 uv)
    {
        Ray ray = _renderCamera.GetRayForUv(uv);

        var renderData = _displaysManager.FindClosestIntersection(ray, _allDisplays);

        if (renderData.Intersection == -1)
        {
            return (0f, ConsoleColor.Black);
        }

        float raw = 0f;
        foreach (var light in _allLight)
            raw += EnableShadows
                ? light.PointBright(renderData, _allDisplays, _displaysManager)
                : light.PointBright(renderData);   // shadows off: light fully visible, no shadow ray cast

        float lit = Ambient + raw * Exposure;
        float toneMapped = lit / (lit + 1f);   // Reinhard → [0,1)
        return (toneMapped, renderData.Color);
    }
}