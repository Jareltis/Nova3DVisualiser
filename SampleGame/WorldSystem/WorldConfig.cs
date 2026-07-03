using System.Collections.Generic;

namespace SampleGame.Worlds;

/// <summary>
/// A saved world: the single source of truth for an entire scene — graphics settings,
/// the platform, and every object with its full transform. Serialized to/from
/// worlds/&lt;name&gt;.json via System.Text.Json (PropertyNameCaseInsensitive).
/// </summary>
public class WorldConfig
{
    public string Name { get; set; } = "";
    public GraphicsConfig Graphics { get; set; } = new();
    public PlatformConfig Platform { get; set; } = new();
    public List<WorldObject> Objects { get; set; } = new();
    public PhysicsConfig Physics { get; set; } = new();
}

public class GraphicsConfig
{
    public bool Shadows { get; set; } = true;
    public bool Bvh { get; set; } = true;
    public bool ExtraLight { get; set; } = false;
    public bool DisableCameraLight { get; set; } = false;
    // Which renderer draws this world: "cpu" (default — the multithreaded CPU raytracer) or "gpu"
    // (NVIDIA via ILGPU). Chosen at world load; if "gpu" but no usable GPU is present, the app logs a
    // notice and falls back to "cpu". The GPU path is at full parity with the CPU renderer
    // (transparency, all light kinds, alpha-correct shadows) — see Nova3DVisualiser.Gpu.
    public string Renderer { get; set; } = "cpu";
}

// World physics master switches.
//  - Gravity: a LOCAL simulation (player camera always; world objects only on the authority/solo).
//    Default OFF so existing worlds are unchanged.
//  - Collision: the master switch for the camera bubble + object colliders. When OFF, every object's
//    per-object Collides is forced off and locked (nothing collides). Default ON (today's behavior).
// Each per-object flag (WorldObject.Gravity / .Collides, PlatformConfig.Gravity / .Collides) is only
// effective when the matching world switch here is on.
public class PhysicsConfig
{
    public bool GravityEnabled { get; set; } = false;
    public float GravityStrength { get; set; } = 9.8f;
    public bool CollisionEnabled { get; set; } = true;
    // Bounciness of a landing object: 0 = no bounce (dead stop, the original behavior), 1 = elastic
    // (rebounds at the full impact speed). The object settles once a bounce falls below a small floor.
    public float Restitution { get; set; } = 0f;
    // (An older per-world "engine" switch selecting "legacy" vs "impulse" was retired in Stage 7b — there is
    // now a single impulse-based rigid-body constraint solver, SampleGame/Physics/, see PHYSICS.md. A world
    // JSON that still carries a stale "engine" key loads fine: System.Text.Json ignores the unmapped member.)
}

public class PlatformConfig
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";              // user-given in-world name ("" = show the system name "platform #0")
    // "square" (default) | "rectangle" | "circle". A world with no Shape (every existing
    // world + the default) is treated as "square" using Size — unchanged geometry.
    public string Shape { get; set; } = "square";
    public float Size { get; set; } = 10f;     // square: half-extent (side = 2*Size); circle: diameter (radius = Size/2)
    public float Width { get; set; } = 20f;    // rectangle only: full X extent
    public float Depth { get; set; } = 20f;    // rectangle only: full Z extent
    public string Color { get; set; } = "Yellow";
    public Vec3Config Position { get; set; } = new();   // floor placement (old worlds default to 0,0,0)
    public bool Collides { get; set; } = true;          // platform collides by default (when world Collision is on)
    public bool Gravity { get; set; } = false;          // does the floor itself fall (when world Gravity is on); off by default
}

/// <summary>Same vector shape the old per-model JSON used.</summary>
public class Vec3Config
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

/// <summary>One scene object: a library mesh, or a built-in cube/sphere primitive.</summary>
public class WorldObject
{
    public int Id { get; set; }                       // stable per-object id (server assigns; client adopts verbatim)
    public string Name { get; set; } = "";            // user-given in-world name ("" = show the derived system name "{type} #{id}")
    public string Type { get; set; } = "mesh";        // "mesh" | "cube" | "sphere" | primitives | "light"
    public string? Mesh { get; set; }                 // mesh name in models/ (Type "mesh")
    public Vec3Config Position { get; set; } = new();
    public Vec3Config Rotation { get; set; } = new();
    public float Scale { get; set; } = 1f;            // uniform, matches Object3d.Scale
    public string Color { get; set; } = "White";
    public float ColorFade { get; set; } = 0f;        // "colour transparency" / paleness 0..1: 0 = true colour, 1 = washed out to white (separate from the alpha channel = object transparency)
    public string Texture { get; set; } = "";         // per-object PNG texture file in textures/ ("" = none/flat colour).
    public float TextureScale { get; set; } = 1f;      // UV tiling factor (1 = 1:1; 2 = tile 2×2). Multiplies the UV before sampling.
    public int TextureFace { get; set; } = -1;         // which face-group wears the texture: -1 = all faces; a cube: 0..5 (+X,-X,+Y,-Y,+Z,-Z)
    public int TextureFilter { get; set; } = 0;        // magnification filter: 0 = Nearest (exact, default); 1 = Bilinear (smooth, tolerated parity band)
    public string Anchor { get; set; } = "Bottom";    // mesh only: Bottom | Center | Origin
    public float RotateSpeed { get; set; } = 0f;      // continuous spin about Y
    public bool Collides { get; set; } = true;        // collider participation (camera + as a support); markers/avatars are false. Effective only when world Collision is on.
    public bool Gravity { get; set; } = false;        // is this object pulled down by world gravity (opt-in). Effective only when world Gravity is on.
    public string Collider { get; set; } = "aabb";    // collider shape for meshes/primitives: "aabb" (world box, default) | "obb" (oriented box)
    public float Mass { get; set; } = 1f;             // impulse-solver mass (heavier = shoved less); >0
    public float Restitution { get; set; } = -1f;     // per-object bounciness 0..1; <0 = inherit world PhysicsConfig.Restitution
    public float Friction { get; set; } = 0.5f;       // per-object Coulomb friction μ (>=0); combined per contact (geometric mean)
    public float RollingFriction { get; set; } = 0.05f; // per-object rolling-friction (>=0); bounded resistance to spin so a rolling ball stops
    public float Radius { get; set; } = 1f;           // sphere only
    public float Power { get; set; } = 500f;          // light only: engine Light.LightPower
    // ---- light-only rich fields (ignored for other types) ----
    public string LightKind { get; set; } = "point";  // point | directional | spot | area
    public Vec3Config Direction { get; set; } = new Vec3Config { X = 0f, Y = -1f, Z = 0f };  // aim (dir/spot/area)
    public float ConeAngle { get; set; } = 30f;        // spot: cone half-angle (degrees)
    public float LightSize { get; set; } = 1f;         // area: square half-extent
    public float LightSpin { get; set; } = 0f;         // sweep speed of Direction about Y (rad/s)
    public int BeamCount { get; set; } = 1;            // spot: cones fanned evenly about the aim (>=1; 1 == single cone)
    public string ConeShape { get; set; } = "circle";  // spot: cone cross-section "circle" | "square" | "triangle"
    public string AreaShape { get; set; } = "square";  // area: emitter cross-section "square" | "circle" | "triangle"
    public float ColorInfluence { get; set; } = 0.6f;  // light only: how strongly the light's color shows over surface albedo (0..1)
}
