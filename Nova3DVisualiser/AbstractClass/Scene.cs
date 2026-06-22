using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.StaticClass;
using Nova3DVisualiser.UI;

namespace Nova3DVisualiser.AbstractClass;

public abstract class Scene(IDisplaysManagerAsync displaysManager)
{
    private readonly IDisplaysManagerAsync _displaysManager = displaysManager;
    private readonly List<IDisplays> _allDisplays = new List<IDisplays>();
    // Subset of _allDisplays that occludes light (the per-light shadow ray tests only these). Visual-
    // only objects (e.g. a light's own marker) are rendered but excluded so they cast no shadow.
    private readonly List<IDisplays> _shadowCasters = new List<IDisplays>();
    private readonly List<Light> _allLight = new List<Light>();
    private Camera _renderCamera = new Camera(Vector3.Zero, Vector3.Zero);
    
    public readonly UIManager UI = new UIManager();

    protected float Exposure = 0.05f;
    protected float Ambient = 0.1f;

    // How strongly a surface's albedo filters a light's COLOR (diffuse term only): 1 = pure
    // multiplicative (a blue-less surface like yellow kills a light's blue); 0 = light color shows
    // independent of albedo. ~0.4 keeps the surface "specially affecting" the light while still
    // letting a colored light read on any surface. Ambient stays fully surface-filtered.
    protected float SurfaceTint = 0.4f;

    public bool EnableShadows = true;

    // Render detail level: 1 = full resolution (one ray per cell, default); 2-4 cast a ray every
    // Nth cell and block-fill the gaps (~N^2 fewer rays), trading resolution for speed. The screen
    // reads this each frame; the scene sets it from input. World-agnostic (just an int).
    public int RenderScale = 1;

    protected void SetMainCamera(Camera camera)
    { _renderCamera = camera; }
    // castsShadow: true (default) → rendered AND occludes lights; false → rendered but never blocks
    // any light (visual-only markers).
    protected void AddDisplaysObject(IDisplays @object, bool castsShadow = true)
    {
        _allDisplays.Add(@object);
        if (castsShadow) _shadowCasters.Add(@object);
    }
    protected void RemoveDisplaysObject(IDisplays @object)
    {
        _allDisplays.Remove(@object);
        _shadowCasters.Remove(@object);
    }

    protected void AddLight(Light light)
    { _allLight.Add(light); }
    protected void RemoveLight(Light light)
    { _allLight.Remove(light); }

    // Advances every light's Direction spin (no-op for point/zero-speed lights). Call once per frame.
    protected void AdvanceLights()
    { foreach (var light in _allLight) light.Spin(); }
    
    public abstract void Start();
    public abstract void Update();

    public virtual (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv)
    {
        Ray ray = _renderCamera.GetRayForUv(uv);

        var renderData = _displaysManager.FindClosestIntersection(ray, _allDisplays);

        if (renderData.Intersection == -1)
        {
            return (0f, Rgb24.Black);
        }

        // Additive, per-light, independently shadow-tested AND colored. Each light contributes its
        // scalar geometric/shadow term times its RGB color; a point shadowed from one light is still
        // lit by any other unblocked light (no global/min/multiplied shadow factor). The surface's
        // ConsoleColor is its albedo: lit = albedo ⊙ (ambient + Σ light.Color·term)·exposure.
        Vector3 accum = Vector3.Zero;
        foreach (var light in _allLight)
        {
            // Shadow occlusion tests _shadowCasters only, so visual-only markers never shadow.
            float term = light.Contribution(renderData, _shadowCasters, _displaysManager, EnableShadows);
            if (term != 0f) accum += light.Rgb * term;
        }

        Vector3 albedo = ColorRgb.ToRgb(renderData.Color);
        // Per-channel lit value, then Reinhard tone map per channel → [0,1). Ambient stays fully
        // surface-filtered (albedo·ambient), but the DIFFUSE light term is only PARTIALLY filtered by
        // a per-channel tint = mix(1, albedo, SurfaceTint), so a light's color reads even where the
        // surface albedo is 0 (e.g. blue light on a yellow floor) while the surface still tints it.
        float tR = 1f - SurfaceTint * (1f - albedo.X);
        float tG = 1f - SurfaceTint * (1f - albedo.Y);
        float tB = 1f - SurfaceTint * (1f - albedo.Z);
        float lr = albedo.X * Ambient + tR * accum.X * Exposure;
        float lg = albedo.Y * Ambient + tG * accum.Y * Exposure;
        float lb = albedo.Z * Ambient + tB * accum.Z * Exposure;
        Vector3 lit = new Vector3(lr / (lr + 1f), lg / (lg + 1f), lb / (lb + 1f));

        // Brightness drives the ASCII glyph (Rec.709 luminance); the full RGB tints the cell (24-bit).
        float brightness = 0.2126f * lit.X + 0.7152f * lit.Y + 0.0722f * lit.Z;
        return (brightness, Rgb24.FromUnit(lit));
    }
}