using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Shape;
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

    // Bumped whenever the static mesh set changes (object added/removed) so the GPU re-uploads the
    // cached local geometry + BVH only then (transforms refresh every frame regardless).
    private int _geomVersion = 0;

    // Bumped when only the TEXTURE POOL changes (a live editor texture swap) — separate from the geometry
    // version so the GPU re-uploads just the pixels, leaving the static geometry + BVH cached.
    private int _textureVersion = 0;

    public readonly UIManager UI = new UIManager();

    protected float Exposure = 0.05f;
    protected float Ambient = 0.1f;

    public bool EnableShadows = true;

    // Whether the render loop (Frame.MainLoop) draws its standalone top-left "Fps: N" readout. A scene can
    // suppress it when it shows fps inside its OWN HUD (e.g. the docked editor's Status bar), so the two
    // don't collide. Default true (unchanged for every other scene).
    public virtual bool ShowFrameFps => true;

    // Whether the render loop's ESC→quit may fire this frame. A scene captures ESC for its own use (e.g. the
    // chat / field-entry cancel) by returning false while doing so, so ESC closes that instead of quitting.
    // Default true (unchanged for every other scene). Frame also edge-triggers the quit so a held ESC that
    // closed the chat can't quit the following frame.
    public virtual bool AllowQuit => true;

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
        _geomVersion++;
    }
    protected void RemoveDisplaysObject(IDisplays @object)
    {
        _allDisplays.Remove(@object);
        _shadowCasters.Remove(@object);
        _geomVersion++;
    }

    // Forces the cached GPU geometry (incl. the texture pool) to rebuild + re-upload next frame, WITHOUT
    // Targeted invalidation for a runtime TEXTURE swap (an editor Field.Texture change): bumps ONLY the
    // texture version, so the GPU re-uploads just the texture pool — the static geometry + BVH stay cached
    // (A3). A genuine mesh add/remove bumps _geomVersion via Add/RemoveDisplaysObject as before.
    protected void InvalidateGpuTextures()
    { _textureVersion++; }

    protected void AddLight(Light light)
    { _allLight.Add(light); }
    protected void RemoveLight(Light light)
    { _allLight.Remove(light); }

    // Advances every light's Direction spin (no-op for point/zero-speed lights). Call once per frame.
    protected void AdvanceLights()
    { foreach (var light in _allLight) light.Spin(); }
    
    public abstract void Start();
    public abstract void Update();

    // The viewports to render this frame: each a screen region + the camera to render into it. The default
    // is a SINGLE full-screen viewport with the render camera (unchanged output). A scene overrides this to
    // split the screen (e.g. 2-way) across several cameras. Called once per frame by the screen, which then
    // renders each region from its camera via the region-relative uv path (CPU FillBuffers / GPU RenderViews).
    public virtual IReadOnlyList<Viewport> GetViewports(int width, int height)
        => new[] { new Viewport(0, 0, width, height, _renderCamera) };

    // The per-view camera reduction (position + trig-free basis + focal) shared by the CPU and GPU paths;
    // mirrors Camera.GetRayForUv's rotation order via CameraAxis. Public so a split-screen scene / test can
    // build a view for an ARBITRARY camera (not just the render camera).
    public CameraView BuildCameraView(Camera cam)
    {
        Vector3 rot = cam.LocalRotate;
        float focal = 1f / MathF.Tan(cam.Fov * (MathF.PI / 360f));
        return new CameraView(
            cam.Position,
            CameraAxis(new Vector3(1f, 0f, 0f), rot),
            CameraAxis(new Vector3(0f, 1f, 0f), rot),
            CameraAxis(new Vector3(0f, 0f, 1f), rot),
            focal);
    }

    // ---- GPU geometry cache: the concatenated local verts / faces / BVH nodes / triangle indices of
    // every mesh, rebuilt only when the mesh set changes (versioned). Per-object transforms refresh
    // every frame in BuildSnapshot. ----
    private int _cachedGeomVersion = -1;
    private int _cachedTextureVersion = -1;
    private Vector3[] _cVerts = Array.Empty<Vector3>();
    private SnapFace[] _cFaces = Array.Empty<SnapFace>();
    private SnapBvhNode[] _cNodes = Array.Empty<SnapBvhNode>();
    private int[] _cTriIdx = Array.Empty<int>();
    private SnapTexture[] _cTextures = Array.Empty<SnapTexture>();
    private Rgba32[] _cTexPixels = Array.Empty<Rgba32>();
    private readonly List<CachedObj> _cObjList = new List<CachedObj>();
    // Per-object texture index into _cTextures (-1 = untextured), aligned to _cObjList. Rebuilt with the
    // TEXTURE pool (which is versioned separately from the geometry), so a live swap refreshes it alone.
    private int[] _cObjTexIndex = Array.Empty<int>();
    // Per-sphere texture index into _cTextures (spheres are analytic — gathered fresh each frame in
    // BuildSnapshot — but their textures ride the same pool, so cache the index here).
    private readonly Dictionary<Sphere, int> _cSphereTex = new Dictionary<Sphere, int>();

    private readonly struct CachedObj
    {
        public readonly Object3d Obj;
        public readonly int VertBase, FaceBase, NodeBase, TriIdxBase;
        public readonly bool Casts;
        public CachedObj(Object3d obj, int vb, int fb, int nb, int tb, bool casts)
        { Obj = obj; VertBase = vb; FaceBase = fb; NodeBase = nb; TriIdxBase = tb; Casts = casts; }
    }

    // Rebuilds the two GPU caches independently: the static geometry + BVH (gated by _geomVersion) and the
    // texture pool (gated by _textureVersion, but ALSO rebuilt on a geometry change since adding/removing a
    // mesh can change which textures exist and their pool offsets). This decoupling is what lets a live
    // texture swap refresh only the pool (A3).
    private void RebuildGpuCachesIfNeeded()
    {
        bool geomChanged = _cachedGeomVersion != _geomVersion;
        if (geomChanged) RebuildGpuGeometry();
        if (geomChanged || _cachedTextureVersion != _textureVersion) RebuildGpuTexturePool();
    }

    private void RebuildGpuGeometry()
    {
        var verts = new List<Vector3>();
        var faces = new List<SnapFace>();
        var nodes = new List<SnapBvhNode>();
        var triIdx = new List<int>();
        var shadowSet = new HashSet<IDisplays>(_shadowCasters);

        _cObjList.Clear();
        foreach (var d in _allDisplays)
        {
            if (d is not Object3d o) continue;
            int vb = verts.Count, fb = faces.Count, nb = nodes.Count, tb = triIdx.Count;
            verts.AddRange(o.LocalVertices);
            faces.AddRange(o.GpuFaces);
            nodes.AddRange(o.GpuNodes);
            triIdx.AddRange(o.GpuTriIdx);
            _cObjList.Add(new CachedObj(o, vb, fb, nb, tb, shadowSet.Contains(o)));
        }

        _cVerts = verts.ToArray();
        _cFaces = faces.ToArray();
        _cNodes = nodes.ToArray();
        _cTriIdx = triIdx.ToArray();
        _cachedGeomVersion = _geomVersion;
    }

    // Texture pool: dedup by Texture reference (the cache hands out one instance per file), pack all pixels
    // into one flat Rgba32 array with a per-texture (offset,w,h) record; each object/sphere gets the index
    // of its texture (or -1). Aligned to _cObjList by the same _allDisplays Object3d order.
    private void RebuildGpuTexturePool()
    {
        var textures = new List<SnapTexture>();
        var texPixels = new List<Rgba32>();
        var texMap = new Dictionary<Texture, int>();

        _cObjTexIndex = new int[_cObjList.Count];
        int k = 0;
        foreach (var d in _allDisplays)
        {
            if (d is not Object3d o) continue;
            int texIndex = -1;
            if (o.Texture is Texture tex)
            {
                if (!texMap.TryGetValue(tex, out texIndex))
                {
                    texIndex = textures.Count;
                    textures.Add(AppendTexture(tex, texPixels));
                    texMap[tex] = texIndex;
                }
            }
            _cObjTexIndex[k++] = texIndex;
        }

        // Sphere textures share the same dedup'd pool (a texture referenced by both a mesh and a sphere is
        // uploaded once). The index is cached per sphere for the per-frame snapshot.
        _cSphereTex.Clear();
        foreach (var d in _allDisplays)
        {
            if (d is not Sphere sp || sp.Texture is not Texture stex) continue;
            if (!texMap.TryGetValue(stex, out int si))
            {
                si = textures.Count;
                textures.Add(AppendTexture(stex, texPixels));
                texMap[stex] = si;
            }
            _cSphereTex[sp] = si;
        }

        _cTextures = textures.ToArray();
        _cTexPixels = texPixels.ToArray();
        _cachedTextureVersion = _textureVersion;
    }

    // Appends a texture's WHOLE box-filter mip chain (levels 0..LevelCount-1, contiguous, level 0 first) to
    // the flat pool and returns its SnapTexture record. Offset points at level 0 (== Pixels), so nearest/
    // bilinear sampling is unchanged (they only touch level 0); the extra levels ride along for the GPU's
    // trilinear sampler, gated by the same version as the geometry. LevelCount lets the kernel find each
    // level's offset/dims. The chain is always uploaded (a small ~33% overhead) so a live switch to the
    // Mipmapped filter needs no re-upload — it is already resident.
    private static SnapTexture AppendTexture(Texture tex, List<Rgba32> texPixels)
    {
        var rec = new SnapTexture { Offset = texPixels.Count, Width = tex.Width, Height = tex.Height, LevelCount = tex.LevelCount };
        Rgba32[][] mips = tex.Mips;
        for (int l = 0; l < mips.Length; l++) texPixels.AddRange(mips[l]);
        return rec;
    }

    // Builds a flat, value-only snapshot of the current frame for the GPU renderer. Mesh geometry +
    // per-object BVH live in LOCAL space and are cached (rebuilt only when the mesh set changes); the
    // per-object transforms, spheres, lights and camera refresh every frame. The CPU path never calls
    // this. See SceneSnapshot.cs.
    public SceneSnapshot BuildSnapshot()
    {
        RebuildGpuCachesIfNeeded();

        // Per-frame object instances: current transform (as a rotation basis so the kernel needs no
        // trig), world AABB (cheap pre-cull) and color, plus the cached geometry base offsets.
        var objects = new SnapObject[_cObjList.Count];
        for (int k = 0; k < _cObjList.Count; k++)
        {
            CachedObj e = _cObjList[k];
            Object3d o = e.Obj;
            Vector3 rot = o.TotalRotation;
            Rgba32 c = o.EffectiveColor;
            objects[k] = new SnapObject
            {
                Position = o.Position,
                ColX = new Vector3(1f, 0f, 0f).Rotate(rot),
                ColY = new Vector3(0f, 1f, 0f).Rotate(rot),
                ColZ = new Vector3(0f, 0f, 1f).Rotate(rot),
                InvScale = 1f / o.Scale,
                WorldMin = o.WorldMin, WorldMax = o.WorldMax,
                VertBase = e.VertBase, FaceBase = e.FaceBase, NodeBase = e.NodeBase, TriIdxBase = e.TriIdxBase,
                FaceCount = o.FaceCount,

                R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f,
                CastsShadow = e.Casts ? 1 : 0,
                TextureIndex = _cObjTexIndex[k],
                ColorFade = o.ColorFade,
                TextureScale = o.TextureScale,
                TextureFace = o.TextureFace,
                TextureFilter = (int)o.TextureFilter,
            };
        }

        // Spheres are analytic (no BVH) — gathered fresh each frame in world space.
        var shadowSetS = new HashSet<IDisplays>(_shadowCasters);
        var spheres = new List<SnapSphere>();
        foreach (var d in _allDisplays)
        {
            if (d is not Sphere sp) continue;
            Rgba32 c = sp.EffectiveColor;
            Vector3 srot = sp.LocalRotate;
            spheres.Add(new SnapSphere
            {
                Center = sp.Position, Radius = sp.R,
                R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f,
                CastsShadow = shadowSetS.Contains(sp) ? 1 : 0,
                ColX = new Vector3(1f, 0f, 0f).Rotate(srot),
                ColY = new Vector3(0f, 1f, 0f).Rotate(srot),
                ColZ = new Vector3(0f, 0f, 1f).Rotate(srot),
                TextureIndex = _cSphereTex.TryGetValue(sp, out int sti) ? sti : -1,
                ColorFade = sp.ColorFade,
                TextureScale = sp.TextureScale,
                TextureFace = sp.TextureFace,
                TextureFilter = (int)sp.TextureFilter,
            });
        }

        var lights = new List<SnapLight>(_allLight.Count);
        foreach (var l in _allLight)
        {
            int kind = l.Kind switch
            {
                LightKind.Directional => 1,
                LightKind.Spot        => 2,
                LightKind.Area        => 3,
                _                     => 0,
            };
            float outer = MathF.Cos(l.ConeAngleDeg * (MathF.PI / 180f));
            float inner = MathF.Cos(l.ConeAngleDeg * 0.8f * (MathF.PI / 180f));
            float tan = MathF.Tan(l.ConeAngleDeg * (MathF.PI / 180f));
            lights.Add(new SnapLight
            {
                Kind = kind,
                Position = l.Position,
                Rgb = l.EffectiveRgb,
                Power = l.LightPower,
                Direction = l.Direction.Norm(),
                ConeCosOuter = outer,
                ConeCosInner = inner,
                ConeTan = tan,
                BeamCount = Math.Max(1, l.BeamCount),
                ConeShape = ShapeToInt(l.ConeShape),
                AreaShape = ShapeToInt(l.AreaShape),
                AreaSize = l.AreaSize,
                ColorInfluence = l.ColorInfluence,
            });
        }

        // Camera basis (the render camera): reduced via BuildCameraView, which replicates
        // Camera.GetRayForUv's rotation order. Carried in the snapshot for the single-view GPU path; the
        // multi-view (split) path overrides it per viewport with each region's own camera.
        CameraView cv = BuildCameraView(_renderCamera);

        return new SceneSnapshot
        {
            GeometryVersion = _cachedGeomVersion,
            TextureVersion = _cachedTextureVersion,
            LocalVerts = _cVerts,
            Faces = _cFaces,
            Nodes = _cNodes,
            TriIdx = _cTriIdx,
            Textures = _cTextures,
            TexPixels = _cTexPixels,
            Objects = objects,
            Spheres = spheres.ToArray(),
            Lights = lights.ToArray(),
            CamPos = cv.CamPos,
            BasisX = cv.BasisX,
            BasisY = cv.BasisY,
            BasisZ = cv.BasisZ,
            Focal = cv.Focal,
            Ambient = Ambient,
            Exposure = Exposure,
            EnableShadows = EnableShadows ? 1 : 0,
            UseBvh = Object3d.UseBvh ? 1 : 0,   // F3: the GPU switches BVH<->brute-force too (identical output, measurable FPS)
        };
    }

    private static int ShapeToInt(ConeShapeKind s) => s switch
    {
        ConeShapeKind.Square   => 1,
        ConeShapeKind.Triangle => 2,
        _                      => 0,   // Circle
    };

    private static Vector3 CameraAxis(Vector3 axis, Vector3 rot)
    {
        Vector3 v = axis.Rotate(new Vector3(rot.X, 0f, 0f));
        v = v.Rotate(new Vector3(0f, 0f, rot.Z));
        v = v.Rotate(new Vector3(0f, rot.Y, 0f));
        return v;
    }

    // Single-view entry point: raytrace one pixel from the RENDER camera. The split-screen path calls the
    // camera-taking overload with each region's own camera. The optional `cone` is the per-pixel ray-cone
    // spread for texture mip selection (0 = base level; the default keeps nearest/bilinear content exact).
    public virtual (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv) => GetPixelData(uv, _renderCamera, 0f);
    public (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv, float cone) => GetPixelData(uv, _renderCamera, cone);
    public (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv, Camera camera) => GetPixelData(uv, camera, 0f);

    public (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv, Camera camera, float cone)
    {
        Ray ray = camera.GetRayForUv(uv);
        ray.Cone = cone;   // carried to the texture sampler for mip-level selection (0 for non-mip content)
        var hits = _displaysManager.FindSortedIntersections(ray, _allDisplays);
        if (hits.Count == 0) return (0f, Rgb24.Black);

        // Front-to-back "over" compositing: each layer's LIT color contributes weighted by its alpha
        // and the remaining transmittance. The terminal cell has no alpha, so this flattens to opaque
        // Rgb24 over a black background. An opaque first hit (a=1 -> w=1 -> break) is identical to the
        // old single-hit path — no regression for opaque scenes.
        Vector3 outRgb = Vector3.Zero; float accumA = 0f;
        foreach (var hit in hits)
        {
            float a = hit.Color.AUnit;
            if (a <= 0f) continue;                       // fully transparent layer contributes nothing
            Vector3 lit = ShadeHit(hit);
            float w = (1f - accumA) * a;                 // front-to-back "over"
            outRgb += lit * w; accumA += w;
            if (accumA >= 0.99f) break;                  // opaque ahead — stop
        }

        // Brightness drives the ASCII glyph (Rec.709 luminance); the full RGB tints the cell (24-bit).
        float brightness = 0.2126f * outRgb.X + 0.7152f * outRgb.Y + 0.0722f * outRgb.Z;
        return (brightness, Rgb24.FromUnit(outRgb));
    }

    // Lit RGB [0,1] for one hit. Additive, per-light, independently shadow-tested AND colored: each
    // light contributes its scalar geometric/shadow term times its RGB color; a point shadowed from
    // one light is still lit by any other unblocked light (no global/min/multiplied shadow factor).
    // Ambient stays fully surface-filtered (albedo·ambient), but each light's DIFFUSE term is only
    // PARTIALLY filtered by a per-channel tint = mix(albedo, 1, light.ColorInfluence): a HIGHER
    // per-light ColorInfluence makes that light's color read even where the surface albedo is 0 (e.g.
    // blue light on a yellow floor) while a lower value lets the surface filter it toward true albedo.
    private Vector3 ShadeHit(RenderData hit)
    {
        Vector3 albedo = hit.Color.ToUnit();
        float lr = albedo.X * Ambient;
        float lg = albedo.Y * Ambient;
        float lb = albedo.Z * Ambient;
        foreach (var light in _allLight)
        {
            // Shadow occlusion tests _shadowCasters only, so visual-only markers never shadow.
            float term = light.Contribution(hit, _shadowCasters, _displaysManager, EnableShadows);
            if (term == 0f) continue;
            float tR = albedo.X + light.ColorInfluence * (1f - albedo.X);
            float tG = albedo.Y + light.ColorInfluence * (1f - albedo.Y);
            float tB = albedo.Z + light.ColorInfluence * (1f - albedo.Z);
            Vector3 erg = light.EffectiveRgb;                       // emission paled by the light's ColorFade
            lr += tR * (erg.X * term) * Exposure;
            lg += tG * (erg.Y * term) * Exposure;
            lb += tB * (erg.Z * term) * Exposure;
        }
        // Reinhard tone map per channel → [0,1).
        return new Vector3(lr / (lr + 1f), lg / (lg + 1f), lb / (lb + 1f));
    }
}