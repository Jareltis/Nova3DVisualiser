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

    public readonly UIManager UI = new UIManager();

    protected float Exposure = 0.05f;
    protected float Ambient = 0.1f;

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
        _geomVersion++;
    }
    protected void RemoveDisplaysObject(IDisplays @object)
    {
        _allDisplays.Remove(@object);
        _shadowCasters.Remove(@object);
        _geomVersion++;
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

    // ---- GPU geometry cache: the concatenated local verts / faces / BVH nodes / triangle indices of
    // every mesh, rebuilt only when the mesh set changes (versioned). Per-object transforms refresh
    // every frame in BuildSnapshot. ----
    private int _cachedGeomVersion = -1;
    private Vector3[] _cVerts = Array.Empty<Vector3>();
    private SnapFace[] _cFaces = Array.Empty<SnapFace>();
    private SnapBvhNode[] _cNodes = Array.Empty<SnapBvhNode>();
    private int[] _cTriIdx = Array.Empty<int>();
    private readonly List<CachedObj> _cObjList = new List<CachedObj>();

    private readonly struct CachedObj
    {
        public readonly Object3d Obj;
        public readonly int VertBase, FaceBase, NodeBase, TriIdxBase;
        public readonly bool Casts;
        public CachedObj(Object3d obj, int vb, int fb, int nb, int tb, bool casts)
        { Obj = obj; VertBase = vb; FaceBase = fb; NodeBase = nb; TriIdxBase = tb; Casts = casts; }
    }

    private void RebuildGpuGeometryIfNeeded()
    {
        if (_cachedGeomVersion == _geomVersion) return;

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

    // Builds a flat, value-only snapshot of the current frame for the GPU renderer. Mesh geometry +
    // per-object BVH live in LOCAL space and are cached (rebuilt only when the mesh set changes); the
    // per-object transforms, spheres, lights and camera refresh every frame. The CPU path never calls
    // this. See SceneSnapshot.cs.
    public SceneSnapshot BuildSnapshot()
    {
        RebuildGpuGeometryIfNeeded();

        // Per-frame object instances: current transform (as a rotation basis so the kernel needs no
        // trig), world AABB (cheap pre-cull) and color, plus the cached geometry base offsets.
        var objects = new SnapObject[_cObjList.Count];
        for (int k = 0; k < _cObjList.Count; k++)
        {
            CachedObj e = _cObjList[k];
            Object3d o = e.Obj;
            Vector3 rot = o.TotalRotation;
            Rgba32 c = o.Color;
            objects[k] = new SnapObject
            {
                Position = o.Position,
                ColX = new Vector3(1f, 0f, 0f).Rotate(rot),
                ColY = new Vector3(0f, 1f, 0f).Rotate(rot),
                ColZ = new Vector3(0f, 0f, 1f).Rotate(rot),
                InvScale = 1f / o.Scale,
                WorldMin = o.WorldMin, WorldMax = o.WorldMax,
                VertBase = e.VertBase, FaceBase = e.FaceBase, NodeBase = e.NodeBase, TriIdxBase = e.TriIdxBase,
                R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f,
                CastsShadow = e.Casts ? 1 : 0,
            };
        }

        // Spheres are analytic (no BVH) — gathered fresh each frame in world space.
        var shadowSetS = new HashSet<IDisplays>(_shadowCasters);
        var spheres = new List<SnapSphere>();
        foreach (var d in _allDisplays)
        {
            if (d is not Sphere sp) continue;
            Rgba32 c = sp.Color;
            spheres.Add(new SnapSphere
            {
                Center = sp.Position, Radius = sp.R,
                R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f,
                CastsShadow = shadowSetS.Contains(sp) ? 1 : 0,
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
                Rgb = l.Rgb,
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

        // Camera basis: replicate Camera.GetRayForUv's rotation order (roll X -> pitch Z -> yaw Y)
        // applied to the three unit axes. Because rotation is linear, the per-pixel ray reduces to
        // BasisX*Focal + BasisY*uv.Y + BasisZ*uv.X (normalized on the GPU).
        Vector3 rotc = _renderCamera.LocalRotate;
        float focal = 1f / MathF.Tan(_renderCamera.Fov * (MathF.PI / 360f));

        return new SceneSnapshot
        {
            GeometryVersion = _cachedGeomVersion,
            LocalVerts = _cVerts,
            Faces = _cFaces,
            Nodes = _cNodes,
            TriIdx = _cTriIdx,
            Objects = objects,
            Spheres = spheres.ToArray(),
            Lights = lights.ToArray(),
            CamPos = _renderCamera.Position,
            BasisX = CameraAxis(new Vector3(1f, 0f, 0f), rotc),
            BasisY = CameraAxis(new Vector3(0f, 1f, 0f), rotc),
            BasisZ = CameraAxis(new Vector3(0f, 0f, 1f), rotc),
            Focal = focal,
            Ambient = Ambient,
            Exposure = Exposure,
            EnableShadows = EnableShadows ? 1 : 0,
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

    public virtual (float Brightness, Rgb24 Color) GetPixelData(Vector2 uv)
    {
        Ray ray = _renderCamera.GetRayForUv(uv);
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
            lr += tR * (light.Rgb.X * term) * Exposure;
            lg += tG * (light.Rgb.Y * term) * Exposure;
            lb += tB * (light.Rgb.Z * term) * Exposure;
        }
        // Reinhard tone map per channel → [0,1).
        return new Vector3(lr / (lr + 1f), lg / (lg + 1f), lb / (lb + 1f));
    }
}