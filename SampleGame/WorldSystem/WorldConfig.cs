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
}

public class GraphicsConfig
{
    public bool Shadows { get; set; } = true;
    public bool Bvh { get; set; } = true;
    public bool ExtraLight { get; set; } = false;
    public bool DisableCameraLight { get; set; } = false;
}

public class PlatformConfig
{
    public bool Enabled { get; set; } = true;
    // "square" (default) | "rectangle" | "circle". A world with no Shape (every existing
    // world + the default) is treated as "square" using Size — unchanged geometry.
    public string Shape { get; set; } = "square";
    public float Size { get; set; } = 10f;     // square: half-extent (side = 2*Size); circle: diameter (radius = Size/2)
    public float Width { get; set; } = 20f;    // rectangle only: full X extent
    public float Depth { get; set; } = 20f;    // rectangle only: full Z extent
    public string Color { get; set; } = "Yellow";
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
    public string Type { get; set; } = "mesh";        // "mesh" | "cube" | "sphere" | primitives | "light"
    public string? Mesh { get; set; }                 // mesh name in models/ (Type "mesh")
    public Vec3Config Position { get; set; } = new();
    public Vec3Config Rotation { get; set; } = new();
    public float Scale { get; set; } = 1f;            // uniform, matches Object3d.Scale
    public string Color { get; set; } = "White";
    public string Anchor { get; set; } = "Bottom";    // mesh only: Bottom | Center | Origin
    public float RotateSpeed { get; set; } = 0f;      // continuous spin about Y
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
}
