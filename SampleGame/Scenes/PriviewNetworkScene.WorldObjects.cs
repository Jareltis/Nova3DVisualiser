using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.Globalization;
using System.Text.Json;

namespace SampleGame.Scenes;

public partial class PriviewNetworkScene
{
    // WorldObject <-> live-instance mapping (ApplyToInstance/FromInstance/physics flags), light-field + light-marker building, and the enum/color parse helpers.

    /// <summary>
    /// Applies a WorldObject's transform/properties to an EXISTING live instance and refreshes
    /// its geometry — never reloads the mesh. Shared by BuildWorldObject (post-creation) and the
    /// client's Modify handler, so creation and a server delta stay identical. ApplyAnchor is only
    /// for meshes (a cube has none) and is idempotent when re-applied with the same anchor.
    /// </summary>
    // Bakes the EFFECTIVE physics flags onto a live instance: a per-object flag only takes effect
    // when the matching world switch is on (collision OFF in the world -> nothing collides; gravity
    // OFF -> nothing falls). This is the single place the world-level gate is applied.
    private void ApplyPhysicsFlags(GameObject instance, bool collidesIntent, bool gravityIntent)
    {
        instance.Collides = collidesIntent && _world.Physics.CollisionEnabled;
        instance.Gravity  = gravityIntent  && _world.Physics.GravityEnabled;
    }

    private void ApplyToInstance(WorldObject o, GameObject instance)
    {
        instance.Position = ToVec(o.Position);
        instance.LocalRotate = ToVec(o.Rotation);
        instance.Color = ParseColor(o.Color, Rgba32.White);
        ApplyPhysicsFlags(instance, o.Collides, o.Gravity);
        instance.Collider = ParseColliderShape(o.Collider);
        instance.Mass = o.Mass > 0f ? o.Mass : 1f;
        instance.Restitution = o.Restitution;          // <0 stays "inherit world default"; 0..1 explicit
        instance.Friction = o.Friction >= 0f ? o.Friction : 0.5f;   // Coulomb μ for the impulse solver
        instance.RollingFriction = o.RollingFriction >= 0f ? o.RollingFriction : 0.05f;   // rolling resistance (Stage 6)
        instance.ColorFade = o.ColorFade;              // colour paleness (separate from the alpha channel)
        instance.Texture = TextureLoader.Get(_texturesFolder, o.Texture);   // decoded PNG (cached); null (logged) if empty/missing/undecodable -> flat colour. On a client _texturesFolder is received/textures.
        // LOUD: a world asked for a texture but it didn't attach (missing file OR unsupported format —
        // TextureLoader logged the exact reason above). Without this, the object silently renders flat.
        if (!string.IsNullOrWhiteSpace(o.Texture) && instance.Texture == null)
            Logger.Warning($"Object id={o.Id} type='{o.Type}': texture '{o.Texture}' did NOT attach (missing file or unsupported format — see the texture log line above). Rendering flat colour.");
        instance.TextureScale = o.TextureScale > 0f ? o.TextureScale : 1f;   // UV tiling (guard a 0/neg value to 1)
        instance.TextureFace = o.TextureFace;              // which face-group wears the texture (-1 = all)
        instance.TextureFilter = o.TextureFilter == 1 ? TextureFilterMode.Bilinear : TextureFilterMode.Nearest;   // magnification filter

        if (instance is Object3d mesh)
        {
            mesh.Scale = o.Scale;
            mesh.RotateSpeed = o.RotateSpeed;
            if (string.Equals(o.Type?.Trim(), "mesh", StringComparison.OrdinalIgnoreCase))
                mesh.ApplyAnchor(ParseAnchor(o.Anchor));
            mesh.UpdateGeometry();
        }
        else if (instance is Sphere s)
        {
            s.R = o.Radius;
        }
    }

    private static Vec3Config FromVec(Vector3 v) => new Vec3Config { X = v.X, Y = v.Y, Z = v.Z };

    /// <summary>
    /// Save-back conversion: reads the live instance's Position/Rotation/Scale/Color into a
    /// fresh WorldObject, keeping Type/Mesh/Anchor/Radius from the descriptor. Takes a
    /// GameObject so it covers both Object3d (mesh/cube) and Sphere instances.
    /// </summary>
    public static WorldObject FromInstance(WorldObject descriptor, GameObject instance, Light? light = null)
    {
        var o3d = instance as Object3d;
        return new WorldObject
        {
            Id = descriptor.Id,                                   // carry the stable id through live read-back
            Name = descriptor.Name,                               // user name (not derived from the instance)
            Type = descriptor.Type,
            Mesh = descriptor.Mesh,
            Anchor = descriptor.Anchor,
            Radius = (instance as Sphere)?.R ?? descriptor.Radius, // read the live radius for spheres
            Position = FromVec(instance.Position),                 // light: the marker's position
            Rotation = FromVec(instance.LocalRotate),
            Scale = o3d?.Scale ?? descriptor.Scale,
            Color = ToHex(instance.Color),
            RotateSpeed = o3d?.RotateSpeed ?? descriptor.RotateSpeed,
            Collides = instance.Collides,
            Gravity = instance.Gravity,
            Collider = ColliderShapeToString(instance.Collider),
            Mass = instance.Mass,
            Restitution = instance.Restitution,
            Friction = instance.Friction,
            RollingFriction = instance.RollingFriction,
            ColorFade = instance.ColorFade,
            Texture = instance.Texture?.Name ?? descriptor.Texture,   // live texture name (falls back to descriptor if none attached)
            TextureScale = instance.TextureScale,
            TextureFace = instance.TextureFace,
            TextureFilter = (int)instance.TextureFilter,
            // ---- light read-back: every live light field flows back from the paired Light ----
            Power = light?.LightPower ?? descriptor.Power,
            LightKind = light != null ? LightKindToString(light.Kind) : descriptor.LightKind,
            Direction = light != null ? FromVec(light.Direction) : descriptor.Direction,
            ConeAngle = light?.ConeAngleDeg ?? descriptor.ConeAngle,
            LightSize = light?.AreaSize ?? descriptor.LightSize,
            LightSpin = light?.SpinSpeed ?? descriptor.LightSpin,
            BeamCount = light?.BeamCount ?? descriptor.BeamCount,
            ConeShape = light != null ? ConeShapeToString(light.ConeShape) : descriptor.ConeShape,
            AreaShape = light != null ? ConeShapeToString(light.AreaShape) : descriptor.AreaShape,
            ColorInfluence = light?.ColorInfluence ?? descriptor.ColorInfluence,
        };
    }

    // Pushes a light WorldObject's full state onto its engine Light (kind/direction/cone/size/spin/
    // power + position from the marker). Shared by BuildWorldObject and the client's Modify handler.
    private static void ApplyLightFields(Light light, WorldObject o, Vector3 markerPos)
    {
        light.Kind = ParseLightKind(o.LightKind);
        Vector3 dir = ToVec(o.Direction);
        light.Direction = dir.Length() > 1e-6f ? dir.Norm() : new Vector3(0f, -1f, 0f);
        light.ConeAngleDeg = o.ConeAngle;
        light.AreaSize = o.LightSize;
        light.SpinSpeed = o.LightSpin;
        light.BeamCount = Math.Max(1, o.BeamCount);
        light.ConeShape = ParseConeShape(o.ConeShape);
        light.AreaShape = ParseConeShape(o.AreaShape);
        light.LightPower = o.Power;
        light.Rgb = ParseColor(o.Color, Rgba32.White).ToUnit();   // the light's emission color (RGB only)
        light.ColorInfluence = o.ColorInfluence;
        light.ColorFade = o.ColorFade;                            // pales the emitted colour toward white
        light.Position = markerPos + LightMarkerOffset;
    }

    private static LightKind ParseLightKind(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "directional" => LightKind.Directional,
        "spot"        => LightKind.Spot,
        "area"        => LightKind.Area,
        _             => LightKind.Point,
    };

    private static string LightKindToString(LightKind k) => k switch
    {
        LightKind.Directional => "directional",
        LightKind.Spot        => "spot",
        LightKind.Area        => "area",
        _                     => "point",
    };

    private static ConeShapeKind ParseConeShape(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "square"   => ConeShapeKind.Square,
        "triangle" => ConeShapeKind.Triangle,
        _          => ConeShapeKind.Circle,
    };

    private static string ConeShapeToString(ConeShapeKind k) => k switch
    {
        ConeShapeKind.Square   => "square",
        ConeShapeKind.Triangle => "triangle",
        _                      => "circle",
    };

    // Keeps a light's engine Light co-located with its (movable) marker: the Light sits a small
    // fixed offset above the marker so the marker doesn't enclose it. No-op for non-light entries.
    private static void SyncLightToMarker(EditEntry entry)
    {
        if (entry.Light != null)
            entry.Light.Position = entry.Instance.Position + LightMarkerOffset;
    }

    // The kind-specific marker mesh, pre-oriented along Direction: Point => cube; Directional/Spot
    // => a cone whose apex points along Direction (the way it shines); Area => a flat double-sided
    // square whose face faces Direction, scaled to the area half-extent. (A single oriented mesh per
    // kind, rather than cube+cone, keeps one pickable Instance per entry.)
    private static Object3d BuildLightMarker(LightKind kind, Vector3 dir, float areaSize,
                                             ConeShapeKind coneShape = ConeShapeKind.Circle, float coneAngleDeg = 30f,
                                             ConeShapeKind areaShape = ConeShapeKind.Square, int beamCount = 1)
    {
        switch (kind)
        {
            case LightKind.Spot:
            {
                // A multi-beam spot fans BeamCount cones (mirrors SpotTerm); a single beam keeps the
                // cheap one-cone-plus-LocalRotate marker.
                if (beamCount > 1) return BuildSpotFanMarker(dir, beamCount, coneShape, coneAngleDeg);
                // The spot marker shows its cross-section (coneShape) and aperture (wider cone = wider base).
                float baseRadius = Math.Clamp(2f * MathF.Tan(coneAngleDeg * MathF.PI / 180f), 0.2f, 3f);
                var m = BuildConeMarker(coneShape, baseRadius);
                m.Scale = LightMarkerScale;
                m.LocalRotate = DirToEuler(dir);
                return m;
            }
            case LightKind.Directional:
            {
                var m = BuildConeMarker(coneShape, 1.15f);   // fixed aperture (a sun has no cone angle)
                m.Scale = LightMarkerScale;
                m.LocalRotate = DirToEuler(dir);
                return m;
            }
            case LightKind.Area:
            {
                var m = BuildAreaMarker(areaShape);
                m.Scale = MathF.Max(0.05f, areaSize);
                m.LocalRotate = DirToEuler(dir);
                return m;
            }
            default:   // Point: direction is meaningless, so the cube stays axis-aligned
            {
                var m = CreateCube();
                m.Scale = LightMarkerScale;
                return m;
            }
        }
    }

    // Euler (X,Y,Z) angles that rotate the marker's local +Y axis onto unit direction d, with roll
    // left at 0 (Object3d applies X then Y then Z). Falls back to straight-down if d collapses.
    private static Vector3 DirToEuler(Vector3 d)
    {
        if (d.Length() < 1e-6f) d = new Vector3(0f, -1f, 0f);
        d = d.Norm();
        float rx = MathF.Acos(Math.Clamp(d.Y, -1f, 1f));   // tilt away from +Y
        float ry = MathF.Atan2(d.X, d.Z);                  // heading in the XZ plane
        return new Vector3(rx, ry, 0f);
    }

    // A flat unit square in the XZ plane (±1), double-sided so it shows from either side once
    // oriented (a single-sided plane would vanish when its back faces the camera).
    private static Object3d CreateSquareMarker()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f, 0f,  1f),   // 1
            new Vector3( 1f, 0f,  1f),   // 2
            new Vector3(-1f, 0f, -1f),   // 3
            new Vector3( 1f, 0f, -1f),   // 4
        };
        var tris = new List<(int, int, int)>
        {
            (2, 3, 1), (2, 4, 3),   // +Y face
            (1, 3, 2), (3, 4, 2),   // -Y face (reverse winding)
        };
        return BuildFlat(verts, tris);
    }

    // A flat, double-sided UNIT (extent 1) emitter polygon in the XZ plane (y=0), matching the
    // Area light's footprint: Square = the quad above; Circle = 16-seg disc; Triangle = equilateral
    // (vertex at +Z). Both windings so it shows from either side once oriented. Scaled by AreaSize
    // at the call site so the marker tracks the lighting half-extent.
    private static Object3d BuildAreaMarker(ConeShapeKind shape)
    {
        if (shape == ConeShapeKind.Square) return CreateSquareMarker();

        var verts = new List<Vector3>();
        if (shape == ConeShapeKind.Triangle)
        {
            verts.Add(new Vector3(0f, 0f, 1f));               // 1
            verts.Add(new Vector3(0.8660254f, 0f, -0.5f));    // 2
            verts.Add(new Vector3(-0.8660254f, 0f, -0.5f));   // 3
        }
        else   // Circle: 16-seg ring, radius 1
        {
            for (int s = 0; s < 16; s++)
            {
                float a = MathF.Tau * s / 16;
                verts.Add(new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
            }
        }

        int n = verts.Count;
        int c = verts.Count + 1; verts.Add(new Vector3(0f, 0f, 0f));   // centre
        int B(int s) => (s % n) + 1;

        var tris = new List<(int, int, int)>();
        for (int s = 0; s < n; s++)
        {
            tris.Add((c, B(s), B(s + 1)));   // one winding
            tris.Add((c, B(s + 1), B(s)));   // reverse winding (double-sided)
        }
        return BuildFlat(verts, tris);
    }

    // Swaps an entry's marker mesh for a fresh one (carrying over position + color), keeping it
    // selectable in place. Used when a light's Kind changes so the marker morphs cube<->cone<->square.
    private void ReplaceMarker(EditEntry entry, Object3d fresh)
    {
        var old = entry.Instance;
        fresh.Position = old.Position;
        fresh.Color = old.Color;
        fresh.Collides = false;          // markers never collide
        fresh.Gravity = old.Gravity;     // but a falling light keeps falling across a kind/shape morph
        fresh.UpdateGeometry();

        if (old is IDisplays oldDisp) RemoveDisplaysObject(oldDisp);
        if (old is Object3d oldO) _models.Remove(oldO);
        _models.Add(fresh);
        AddDisplaysObject(fresh, castsShadow: false);   // markers stay visual-only after a kind morph
        entry.Instance = fresh;
    }

    private static Vector3 ToVec(Vec3Config v) => new Vector3(v.X, v.Y, v.Z);

    private static AnchorMode ParseAnchor(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "center" => AnchorMode.Center,
        "origin" => AnchorMode.Origin,
        _ => AnchorMode.Bottom
    };

    private static ColliderShape ParseColliderShape(string? s) =>
        s?.Trim().ToLowerInvariant() == "obb" ? ColliderShape.Obb : ColliderShape.Aabb;
    private static string ColliderShapeToString(ColliderShape c) => c == ColliderShape.Obb ? "obb" : "aabb";

    // Parses a scene color: "#RRGGBBAA"/"#RRGGBB"(A=255) hex, "r,g,b" / "r,g,b,a" bytes, or a legacy
    // ConsoleColor name (so old worlds still load). Returns fallback for null/blank/unparseable.
    public static Rgba32 ParseColor(string? s, Rgba32 fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        string hex = s.StartsWith("#") ? s.Substring(1) : s;
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v8))
            return new Rgba32((byte)((v8 >> 24) & 0xFF), (byte)((v8 >> 16) & 0xFF), (byte)((v8 >> 8) & 0xFF), (byte)(v8 & 0xFF));
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return new Rgba32((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        var p = s.Split(',');
        if (p.Length == 3 && byte.TryParse(p[0], out var r) && byte.TryParse(p[1], out var g) && byte.TryParse(p[2], out var b))
            return new Rgba32(r, g, b);
        if (p.Length == 4 && byte.TryParse(p[0], out var r2) && byte.TryParse(p[1], out var g2) && byte.TryParse(p[2], out var b2) && byte.TryParse(p[3], out var a2))
            return new Rgba32(r2, g2, b2, a2);
        if (Enum.TryParse<ConsoleColor>(s, true, out var cc)) return Rgba32.FromUnit(ColorRgb.ToRgb(cc));
        return fallback;
    }

    // "#RRGGBB" when opaque, else "#RRGGBBAA" (so alpha persists through JSON + sync).
    public static string ToHex(Rgba32 c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

}
