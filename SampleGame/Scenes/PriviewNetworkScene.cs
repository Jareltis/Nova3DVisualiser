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
using SampleGame.Worlds;
using System.Text.Json;

namespace SampleGame.Scenes;

public class PriviewNetworkScene : Scene
{
    private readonly bool _online;
    private readonly bool _isServer;
    private readonly NetworkManager? _netManager;
    private readonly int _myNetId;
    private readonly Dictionary<int, Object3d> _remotePlayers = new();

    readonly Camera _myCamera;
    readonly Light _mainLight;

    private WorldConfig _world;
    private readonly List<Object3d> _models = new();   // spinnable Object3d objects (meshes + cubes)
    private string _modelsFolder = AppPaths.ModelsFolder;   // where world meshes load from (server world -> received/ on a client)
    private Object3d? _floor;                              // the platform, tracked so a client can replace it

    private bool _ownLightEnabled;

    // ---- Client world download (4b) ----
    private bool _awaitingWorld = false;   // client only: true until the server's world arrives
    private bool _worldReceived = false;   // client only: a world has been applied
    private float _requestTimer = 0f;      // client only: countdown to the next WorldRequestPacket

    private bool _isChatting = false;
    private string _currentInput = "";
    private readonly List<string> _chatHistory = new();
    private const int MaxHistory = 5;

    // ---- In-scene editor (3b-i: toggle, spawn, select, move, save) ----
    // Pairs a saved WorldObject descriptor (Type/Mesh/Anchor/Radius metadata) with its
    // live scene instance (Object3d for mesh/cube, Sphere for sphere — both GameObjects).
    public sealed class EditEntry
    {
        public required WorldObject Descriptor;
        public required GameObject Instance;
        // For a "light" object: the engine Light paired with the visible marker (Instance), so
        // move/power/delete update BOTH. Null for ordinary mesh/primitive/sphere objects.
        public Light? Light;
    }

    private bool _editMode = false;
    private readonly List<EditEntry> _editables = new();
    private int _selected = -1;
    private int _nextObjectId = 0;      // next id to hand a spawned object (server/local authority; 5b broadcasts spawns)

    // Server streams its edits as deltas; clients are view-only over the synced world.
    // CanEdit gates every world-mutating action; camera/selection/inspection stay open.
    private bool _editDirty = false;    // set by an edit-action this frame; coalesced into one Modify broadcast
    private bool CanEdit => !_online || _isServer;
    private readonly List<string> _spawnTypes = new();   // "cube", "sphere", then each library mesh
    private int _spawnIndex = 0;
    private const float MoveStep = 0.5f;
    private const float RotStep = MathF.PI / 12f;        // 15 degrees in the engine's radians
    private const float ScaleStep = 0.1f;
    private const float SpinStep = 0.1f;
    private const float RadiusStep = 0.1f;
    private const float PowerStep = 50f;                 // light power adjust per N/M press
    private const float DirStep = 0.15f;                 // light direction component adjust (then re-normalized)
    private const float ConeStep = 2f;                   // spot cone half-angle, degrees per press
    private const float AreaStep = 0.1f;                 // area light half-extent per press
    private const float LightSpinStep = 0.2f;            // light direction sweep speed, rad/s per press
    private const float LightMarkerScale = 0.3f;         // size of the small cube that marks a light
    private static readonly Vector3 LightMarkerOffset = Vector3.Zero;  // Light sits exactly at its marker; the marker is a non-shadow-caster so there's no self-shadow to avoid
    private readonly HashSet<ConsoleKey> _prevDown = new();
    private int _saveFlash = 0;
    private string _saveMsg = "";

    // Properties-panel editable fields (read-only Type/Mesh are not in here).
    private enum Field { PosX, PosY, PosZ, RotX, RotY, RotZ, Scale, RotateSpeed, Color, Radius,
                         Power, Kind, DirX, DirY, DirZ, ConeAngle, AreaSize, Spin, Beams, Shape }
    private int _fieldIndex = 0;

    // Editable fields for the selected entry. A light's set depends on its Kind: every light shows
    // Pos/Power/Color/Kind; directional/spot/area add Direction + Spin, spot adds Cone, area adds
    // Size. Spheres and the rest are by type, as before.
    private static Field[] FieldsFor(EditEntry e)
    {
        string type = e.Descriptor.Type ?? "";
        if (type == "sphere")
            return new[] { Field.PosX, Field.PosY, Field.PosZ, Field.Radius, Field.Color };
        if (type == "light")
        {
            var f = new List<Field> { Field.PosX, Field.PosY, Field.PosZ, Field.Power, Field.Color, Field.Kind };
            switch (e.Light?.Kind ?? LightKind.Point)
            {
                case LightKind.Directional: f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.Spin }); break;
                case LightKind.Spot:        f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.ConeAngle, Field.Beams, Field.Shape, Field.Spin }); break;
                case LightKind.Area:        f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.AreaSize, Field.Spin }); break;
            }
            return f.ToArray();
        }
        return new[] { Field.PosX, Field.PosY, Field.PosZ, Field.RotX, Field.RotY, Field.RotZ, Field.Scale, Field.RotateSpeed, Field.Color };
    }


    public PriviewNetworkScene(IDisplaysManagerAsync manager, WorldConfig world, bool isServer, string targetIp, int port, bool online = true) : base(manager)
    {
        _online = online;
        _isServer = isServer;
        Exposure = 0.05f;
        Ambient = 0.1f;

        _world = world;
        _ownLightEnabled = !world.Graphics.DisableCameraLight;
        // The settings "extra light" is no longer a bare code Light here — the authority/solo injects
        // it as a real editable "light" WorldObject in Start() (see MaybeInjectExtraLight).

        // Tunable starting framing: lower and nearly level so models sit near vertical center.
        _myCamera = new Camera(new Vector3(-5.5f, 1.5f, 0), new Vector3(0, 0, -0.05f));
        _mainLight = new Light(new Vector3(0, 2, -2), 500);

        _myNetId = isServer ? 1 : Random.Shared.Next(2, 10000);

        if (!_online)
        {
            Logger.Info("Local (offline) mode - networking disabled");
            Console.Title = "LOCAL (Offline)";
            return;
        }

        _netManager = new NetworkManager();

        PacketManager.RegisterPacket<TransformPacket>();
        PacketManager.RegisterPacket<ChatPacket>();
        PacketManager.RegisterPacket<WorldSyncPacket>();
        PacketManager.RegisterPacket<WorldRequestPacket>();
        PacketManager.RegisterPacket<WorldEditPacket>();

        PacketManager.Subscribe<TransformPacket>(OnTransformReceived);
        PacketManager.Subscribe<ChatPacket>(OnChatReceived);

        if (isServer)
        {
            // The server answers a joining client's world request with its world.
            PacketManager.Subscribe<WorldRequestPacket>(OnWorldRequested);
            _netManager.StartServer(port);
            Logger.Info($"Server listening on port {port}");
            Console.Title = $"SERVER (Port: {port}) | ID: {_myNetId}";
        }
        else
        {
            // The client downloads the server's world and rebuilds the scene from it,
            // then applies the server's live edit deltas in place (view-only).
            PacketManager.Subscribe<WorldSyncPacket>(OnWorldSyncReceived);
            PacketManager.Subscribe<WorldEditPacket>(OnWorldEditReceived);
            _awaitingWorld = true;
            _netManager.Connect(targetIp, port);
            Logger.Info($"Connecting to {targetIp}:{port}");
            Console.Title = $"CLIENT (ID: {_myNetId}) -> {targetIp}:{port}";
        }


    }

    public override void Start()
    {
        BuildPlatform();

        if (_ownLightEnabled) AddLight(_mainLight);

        SetMainCamera(_myCamera);

        // Spawn palette for the editor: the two primitives, then every library mesh.
        _spawnTypes.Add("cube");
        _spawnTypes.Add("sphere");
        _spawnTypes.Add("cylinder");
        _spawnTypes.Add("cone");
        _spawnTypes.Add("pyramid");
        _spawnTypes.Add("light");
        _spawnTypes.AddRange(WorldManager.ListAvailableMeshes());

        // The world authority (server, and local solo) stamps stable sequential ids onto its
        // objects, ignoring any file values so ids are guaranteed unique. A client leaves them
        // alone — it adopts the server's ids verbatim when the world arrives (ApplyReceivedWorld).
        if (CanEdit)
        {
            MaybeInjectExtraLight();   // authority/solo: add the settings extra light as a real object (once)
            for (int i = 0; i < _world.Objects.Count; i++)
                _world.Objects[i].Id = i;
            _nextObjectId = _world.Objects.Count;
        }

        foreach (var obj in _world.Objects)
            BuildWorldObject(obj);
    }

    // The settings "extra light" toggle, realized as a full editable "light" WorldObject (so it gets
    // a marker + Light via BuildWorldObject and is selectable/editable/saveable/syncable). Only the
    // authority/solo injects (the client receives it through the synced config); idempotent — skipped
    // when the world already contains any light (a saved/synced world won't be doubled). Call before
    // the id-stamp so it earns a stable id like any other object.
    private void MaybeInjectExtraLight()
    {
        if (!_world.Graphics.ExtraLight) return;
        if (_world.Objects.Any(o => string.Equals(o.Type?.Trim(), "light", StringComparison.OrdinalIgnoreCase)))
            return;
        _world.Objects.Add(new WorldObject
        {
            Type = "light",
            Position = new Vec3Config { X = 2f, Y = 6f, Z = 0f },
            Power = 600f,
            Color = "White",
            LightKind = "point",
        });
    }

    // Builds the platform from the current world (tracked in _floor so it can be replaced).
    private void BuildPlatform()
    {
        if (!_world.Platform.Enabled) return;
        Object3d floor = CreatePlatform(_world.Platform);
        floor.Position = new Vector3(0, 0, 0);
        floor.Color = ParseColor(_world.Platform.Color, ConsoleColor.Yellow);
        AddDisplaysObject(floor);
        _floor = floor;
    }

    // Builds one world object (mesh / cube / sphere), adds it to the display, and records
    // an editable entry pairing the descriptor with its live instance. Returns the entry
    // (null if it did not resolve). The platform, lights and remote players are NOT recorded.
    private EditEntry? BuildWorldObject(WorldObject o)
    {
        GameObject? instance = null;
        Light? markerLight = null;

        string type = o.Type?.Trim().ToLowerInvariant() ?? "";
        switch (type)
        {
            case "mesh":
            {
                if (string.IsNullOrWhiteSpace(o.Mesh))
                {
                    Logger.Warning("World mesh object has no Mesh name; skipping.");
                    return null;
                }

                Object3d? mesh = ModelLoader.LoadRawMesh(_modelsFolder, o.Mesh);
                if (mesh == null)
                {
                    Logger.Warning($"World mesh '{o.Mesh}' did not resolve; skipping.");
                    return null;
                }

                // ApplyToInstance anchors + transforms the mesh; BuildAcceleration then builds the
                // BVH from the (anchored, scale-independent) local verts — order is independent.
                ApplyToInstance(o, mesh);
                mesh.BuildAcceleration();

                _models.Add(mesh);
                AddDisplaysObject(mesh);
                instance = mesh;
                break;
            }

            // Generated primitives all ride the same path: build the mesh, transform it (no anchor —
            // gated to "mesh" inside ApplyToInstance, so they stay origin-centred like the cube), add.
            case "cube":
            case "cylinder":
            case "cone":
            case "pyramid":
            {
                Object3d prim = type switch
                {
                    "cylinder" => CreateCylinder(),
                    "cone"     => CreateCone(),
                    "pyramid"  => CreatePyramid(),
                    _          => CreateCube(),
                };
                ApplyToInstance(o, prim);

                _models.Add(prim);
                AddDisplaysObject(prim);
                instance = prim;
                break;
            }

            case "sphere":
            {
                var sphere = new Sphere(ToVec(o.Position), ToVec(o.Rotation), o.Radius);
                ApplyToInstance(o, sphere);
                AddDisplaysObject(sphere);
                instance = sphere;
                break;
            }

            // A light = an engine Light PLUS a small bright MARKER that reflects its kind: Point => a
            // cube; Directional/Spot => a cone/arrow aimed along Direction; Area => a flat square
            // oriented by Direction (sized to the area half-extent). The Light sits a little above
            // the marker (LightMarkerOffset) so the marker doesn't enclose it and self-shadow it.
            // The EditEntry carries the Light (markerLight) so move/power/delete update BOTH.
            case "light":
            {
                LightKind kind = ParseLightKind(o.LightKind);
                Object3d marker = BuildLightMarker(kind, ToVec(o.Direction), o.LightSize);
                marker.Position = ToVec(o.Position);
                marker.Color = ParseColor(o.Color, ConsoleColor.Yellow);
                marker.UpdateGeometry();
                _models.Add(marker);
                AddDisplaysObject(marker, castsShadow: false);   // a light marker is visual-only; it must not shadow
                instance = marker;

                markerLight = new Light(ToVec(o.Position) + LightMarkerOffset, o.Power);
                ApplyLightFields(markerLight, o, ToVec(o.Position));   // kind/direction/cone/size/spin/color
                AddLight(markerLight);
                break;
            }

            default:
                Logger.Warning($"Unknown world object type '{o.Type}'; skipping.");
                return null;
        }

        var entry = new EditEntry { Descriptor = o, Instance = instance, Light = markerLight };
        _editables.Add(entry);
        return entry;
    }

    /// <summary>
    /// Returns the index of the editable entry whose instance the ray hits nearest, or -1 for no hit.
    /// Reuses the renderer's own ray-object intersection (IDisplays.GetRenderData) — no new math.
    /// </summary>
    public static int PickNearest(Ray ray, IReadOnlyList<EditEntry> editables)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < editables.Count; i++)
        {
            if (editables[i].Instance is not IDisplays disp) continue;
            var rd = disp.GetRenderData(ray);
            if (rd.Intersection > -1f && rd.Intersection < bestDist)
            {
                bestDist = rd.Intersection;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Applies a WorldObject's transform/properties to an EXISTING live instance and refreshes
    /// its geometry — never reloads the mesh. Shared by BuildWorldObject (post-creation) and the
    /// client's Modify handler, so creation and a server delta stay identical. ApplyAnchor is only
    /// for meshes (a cube has none) and is idempotent when re-applied with the same anchor.
    /// </summary>
    private static void ApplyToInstance(WorldObject o, GameObject instance)
    {
        instance.Position = ToVec(o.Position);
        instance.LocalRotate = ToVec(o.Rotation);
        instance.Color = ParseColor(o.Color, ConsoleColor.White);

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
            Type = descriptor.Type,
            Mesh = descriptor.Mesh,
            Anchor = descriptor.Anchor,
            Radius = (instance as Sphere)?.R ?? descriptor.Radius, // read the live radius for spheres
            Position = FromVec(instance.Position),                 // light: the marker's position
            Rotation = FromVec(instance.LocalRotate),
            Scale = o3d?.Scale ?? descriptor.Scale,
            Color = instance.Color.ToString(),
            RotateSpeed = o3d?.RotateSpeed ?? descriptor.RotateSpeed,
            // ---- light read-back: every live light field flows back from the paired Light ----
            Power = light?.LightPower ?? descriptor.Power,
            LightKind = light != null ? LightKindToString(light.Kind) : descriptor.LightKind,
            Direction = light != null ? FromVec(light.Direction) : descriptor.Direction,
            ConeAngle = light?.ConeAngleDeg ?? descriptor.ConeAngle,
            LightSize = light?.AreaSize ?? descriptor.LightSize,
            LightSpin = light?.SpinSpeed ?? descriptor.LightSpin,
            BeamCount = light?.BeamCount ?? descriptor.BeamCount,
            ConeShape = light != null ? ConeShapeToString(light.ConeShape) : descriptor.ConeShape,
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
        light.LightPower = o.Power;
        light.Rgb = ColorRgb.ToRgb(ParseColor(o.Color, ConsoleColor.White));   // the light's emission color
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
    private static Object3d BuildLightMarker(LightKind kind, Vector3 dir, float areaSize)
    {
        switch (kind)
        {
            case LightKind.Directional:
            case LightKind.Spot:
            {
                var m = CreateCone();
                m.Scale = LightMarkerScale;
                m.LocalRotate = DirToEuler(dir);
                return m;
            }
            case LightKind.Area:
            {
                var m = CreateSquareMarker();
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

    // Swaps an entry's marker mesh for a fresh one (carrying over position + color), keeping it
    // selectable in place. Used when a light's Kind changes so the marker morphs cube<->cone<->square.
    private void ReplaceMarker(EditEntry entry, Object3d fresh)
    {
        var old = entry.Instance;
        fresh.Position = old.Position;
        fresh.Color = old.Color;
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

    private static ConsoleColor ParseColor(string? s, ConsoleColor fallback) =>
        Enum.TryParse<ConsoleColor>(s, true, out var c) ? c : fallback;

    public override void Update()
    {
        UI.Clear();
        _netManager?.ProcessEvents();   // world-sync handler (if any) runs here, on this thread

        // Client: keep asking the server for its world (~once/second) until it arrives.
        if (_online && !_isServer && !_worldReceived)
        {
            _requestTimer -= GameTime.GetDeltaTime();
            if (_requestTimer <= 0f)
            {
                _netManager?.SendPacket(new WorldRequestPacket(), _myNetId);
                _requestTimer = 1f;
            }
        }

        foreach (var m in _models)
            if (m.RotateSpeed != 0f)
            {
                m.LocalRotate.Y += m.RotateSpeed * GameTime.GetDeltaTime();
                m.UpdateGeometry();
            }

        AdvanceLights();   // sweep each light's Direction by its spin (no-op for point/zero-speed lights)

        if (_isChatting)
        {
            HandleChatInput();
        }
        else
        {
            // Tab toggles the editor; camera fly stays active in edit mode.
            if (Pressed(ConsoleKey.Tab)) _editMode = !_editMode;

            HandleGameInput(GameTime.GetDeltaTime());

            if (_editMode) HandleEditorInput();

            if (Input.IsGetKey(ConsoleKey.T))
            {
                _isChatting = true;
                _currentInput = "";
                while (Console.KeyAvailable) Console.ReadKey(true);
            }
        }

        // Server is the world authority: if an edit-action changed the selected object this
        // frame, stream ONE coalesced Modify delta for it (at most one per object per frame).
        if (_online && _isServer && _editDirty && _selected >= 0 && _selected < _editables.Count)
        {
            var entry = _editables[_selected];
            var o = FromInstance(entry.Descriptor, entry.Instance, entry.Light);   // carries the stable id (+ light power)
            _netManager?.SendPacket(
                new WorldEditPacket { Op = 0, Id = o.Id, ObjectJson = JsonSerializer.Serialize(o) },
                _myNetId);
        }
        _editDirty = false;

        if (!_isChatting && _netManager != null)
        {
            var packet = new TransformPacket(_myCamera.Position, _myCamera.LocalRotate);
            _netManager.SendPacket(packet, _myNetId);
        }
        if (_ownLightEnabled) _mainLight.Position = _myCamera.Position;
        if (_saveFlash > 0) _saveFlash--;
        DrawChatInterface();
        if (_awaitingWorld)
            UI.AddText("Waiting for world from server...", new Vector2Int(2, 10), ConsoleColor.Yellow);
        if (RenderScale > 1)
            UI.AddText($"Detail {RenderScale}/4 [P]", new Vector2Int(2, 11), ConsoleColor.DarkGray);
        DrawEditorOverlay();
        if (_editMode)
        {
            DrawCrosshair();
            DrawPropertiesPanel();
        }
    }

    // Center-screen aim reticle, shown only in edit mode.
    private void DrawCrosshair()
    {
        int cx = Console.WindowWidth / 2;
        int cy = Console.WindowHeight / 2;
        UI.AddText("+", new Vector2Int(cx, cy), ConsoleColor.Red);
    }

    // ---- Editor input (only called in edit mode, never while chatting) ----
    private void HandleEditorInput()
    {
        // Cycle the spawn type.
        if (Pressed(ConsoleKey.G) && _spawnTypes.Count > 0)
            _spawnIndex = (_spawnIndex + 1) % _spawnTypes.Count;

        // Spawn the current type a couple of units in front of the camera, at ground level.
        // (A view-only client cannot mutate the synced world.)
        if (CanEdit && Pressed(ConsoleKey.Enter))
            SpawnCurrent();

        // Aim-to-select: pick the editable object under the center crosshair.
        if (Pressed(ConsoleKey.F))
        {
            int hit = PickNearest(_myCamera.GetRayForUv(Vector2.Zero), _editables);
            if (hit >= 0) { _selected = hit; _fieldIndex = 0; }
        }

        // Cycle the selection (wrap around); reset the field cursor on a new selection.
        if (_editables.Count > 0)
        {
            if (Pressed(ConsoleKey.Oem4)) // '['
            { _selected = (_selected <= 0 ? _editables.Count : _selected) - 1; _fieldIndex = 0; }
            if (Pressed(ConsoleKey.Oem6)) // ']'
            { _selected = (_selected + 1) % _editables.Count; _fieldIndex = 0; }
        }

        if (_selected >= 0 && _selected < _editables.Count)
        {
            // Properties panel: navigate fields (,/.) stays open so a client can inspect.
            var fields = FieldsFor(_editables[_selected]);
            if (Pressed(ConsoleKey.OemComma))  _fieldIndex = (_fieldIndex - 1 + fields.Length) % fields.Length;
            if (Pressed(ConsoleKey.OemPeriod)) _fieldIndex = (_fieldIndex + 1) % fields.Length;
            _fieldIndex = Math.Clamp(_fieldIndex, 0, fields.Length - 1);

            // World-mutating actions are authority-only; a client never edits the synced world.
            if (CanEdit)
            {
                // Move the selected object by a fixed step per press (world axes); fast nudge.
                var delta = Vector3.Zero;
                if (Pressed(ConsoleKey.L)) delta.X += MoveStep;
                if (Pressed(ConsoleKey.J)) delta.X -= MoveStep;
                if (Pressed(ConsoleKey.I)) delta.Z += MoveStep;
                if (Pressed(ConsoleKey.K)) delta.Z -= MoveStep;
                if (Pressed(ConsoleKey.U)) delta.Y += MoveStep;
                if (Pressed(ConsoleKey.O)) delta.Y -= MoveStep;

                if (delta.X != 0f || delta.Y != 0f || delta.Z != 0f)
                {
                    var entry = _editables[_selected];
                    var inst = entry.Instance;
                    inst.Position += delta;
                    if (inst is Object3d o) o.UpdateGeometry();
                    SyncLightToMarker(entry);   // a light's Light follows its marker
                    _editDirty = true;
                }

                // Adjust the active field (N/M) — covers pos/rot/scale/spin/color/radius/power.
                int dir = 0;
                if (Pressed(ConsoleKey.N)) dir = -1;
                if (Pressed(ConsoleKey.M)) dir = +1;
                if (dir != 0) { AdjustField(fields[_fieldIndex], _editables[_selected], dir); _editDirty = true; }

                // Delete the selected object.
                if (Pressed(ConsoleKey.Delete)) DeleteSelected();
            }
        }

        // Save the arrangement back into the world JSON (authority only).
        if (CanEdit && Pressed(ConsoleKey.F5))
            SaveWorld();
    }

    // Applies a single decrease/increase (dir = -1/+1) to the active field on the selected entry.
    private void AdjustField(Field f, EditEntry entry, int dir)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        switch (f)
        {
            case Field.PosX: inst.Position.X += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosY: inst.Position.Y += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosZ: inst.Position.Z += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.RotX: inst.LocalRotate.X += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.RotY: inst.LocalRotate.Y += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.RotZ: inst.LocalRotate.Z += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.Scale:
                if (o != null) { o.Scale = MathF.Max(0.01f, o.Scale + dir * ScaleStep); o.UpdateGeometry(); }
                break;
            case Field.RotateSpeed:
                if (o != null) o.RotateSpeed += dir * SpinStep;
                break;
            case Field.Color:
                inst.Color = CycleColor(inst.Color, dir);
                if (entry.Light != null) entry.Light.Rgb = ColorRgb.ToRgb(inst.Color);   // light emits the marker color
                break;
            case Field.Radius:
                if (inst is Sphere s) s.R = MathF.Max(0.01f, s.R + dir * RadiusStep);
                break;
            case Field.Power:
                if (entry.Light != null) entry.Light.LightPower = MathF.Max(0f, entry.Light.LightPower + dir * PowerStep);
                break;
            case Field.Kind:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<LightKind>().Length;
                    entry.Light.Kind = (LightKind)((((int)entry.Light.Kind + dir) % n + n) % n);
                    // Morph the marker mesh (cube <-> cone <-> square) to match the new kind.
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize));
                }
                break;
            case Field.DirX: AdjustDirection(entry, new Vector3(dir * DirStep, 0f, 0f)); break;
            case Field.DirY: AdjustDirection(entry, new Vector3(0f, dir * DirStep, 0f)); break;
            case Field.DirZ: AdjustDirection(entry, new Vector3(0f, 0f, dir * DirStep)); break;
            case Field.ConeAngle:
                if (entry.Light != null) entry.Light.ConeAngleDeg = Math.Clamp(entry.Light.ConeAngleDeg + dir * ConeStep, 1f, 89f);
                break;
            case Field.AreaSize:
                if (entry.Light != null)
                {
                    entry.Light.AreaSize = MathF.Max(0.05f, entry.Light.AreaSize + dir * AreaStep);
                    if (entry.Light.Kind == LightKind.Area && entry.Instance is Object3d am)
                    { am.Scale = entry.Light.AreaSize; am.UpdateGeometry(); }   // square marker tracks the area size
                }
                break;
            case Field.Spin:
                if (entry.Light != null) entry.Light.SpinSpeed += dir * LightSpinStep;
                break;
            case Field.Beams:
                if (entry.Light != null) entry.Light.BeamCount = Math.Clamp(entry.Light.BeamCount + dir, 1, 8);
                break;
            case Field.Shape:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<ConeShapeKind>().Length;
                    entry.Light.ConeShape = (ConeShapeKind)((((int)entry.Light.ConeShape + dir) % n + n) % n);
                }
                break;
        }
    }

    // Nudges one light's Direction by delta and re-normalizes (kept a unit vector; falls back to
    // straight down if it collapses to zero), then re-aims the marker to match.
    private static void AdjustDirection(EditEntry entry, Vector3 delta)
    {
        if (entry.Light == null) return;
        Vector3 d = entry.Light.Direction + delta;
        entry.Light.Direction = d.Length() > 1e-6f ? d.Norm() : new Vector3(0f, -1f, 0f);
        OrientLightMarker(entry);
    }

    // Re-aims a light's marker along its current Direction (no-op for a point light: its cube stays
    // axis-aligned). Only the orientation changes; the mesh is left as-is.
    private static void OrientLightMarker(EditEntry entry)
    {
        if (entry.Light == null || entry.Light.Kind == LightKind.Point) return;
        if (entry.Instance is Object3d m) { m.LocalRotate = DirToEuler(entry.Light.Direction); m.UpdateGeometry(); }
    }

    private static ConsoleColor CycleColor(ConsoleColor c, int dir)
    {
        int n = Enum.GetValues<ConsoleColor>().Length;   // 16
        return (ConsoleColor)((((int)c + dir) % n + n) % n);
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _editables.Count) return;

        int id = _editables[_selected].Descriptor.Id;   // capture before removal (for the broadcast)
        RemoveEntryAt(_selected);

        // Server is the world authority: tell viewing clients to drop this object by id.
        if (_online && _isServer)
            _netManager?.SendPacket(new WorldEditPacket { Op = 2, Id = id }, _myNetId);
    }

    // Removes one editable entry from the display + tracking lists and keeps _selected/_fieldIndex
    // consistent. Shared by DeleteSelected (server/local) and the client's Delete-delta handler,
    // so both paths stay identical.
    private void RemoveEntryAt(int index)
    {
        if (index < 0 || index >= _editables.Count) return;

        var entry = _editables[index];
        if (entry.Instance is IDisplays disp) RemoveDisplaysObject(disp);
        if (entry.Instance is Object3d o) _models.Remove(o);
        if (entry.Light != null) RemoveLight(entry.Light);   // a light: actually turn it off, not just hide the marker
        _editables.RemoveAt(index);

        if (_editables.Count == 0) _selected = -1;
        else
        {
            if (index < _selected) _selected--;   // an earlier slot vanished — keep tracking the same object
            _selected = Math.Clamp(_selected, 0, _editables.Count - 1);
        }
        _fieldIndex = 0;
    }

    private void SpawnCurrent()
    {
        if (_spawnTypes.Count == 0) return;

        Vector3 yaw = new Vector3(0, _myCamera.LocalRotate.Y, 0);
        Vector3 forward = new Vector3(1, 0, 0).Rotate(yaw);
        Vector3 spawnPos = _myCamera.Position + forward * 3f;
        spawnPos.Y = 0f; // ground level

        string label = _spawnTypes[_spawnIndex];
        // The built-in types (primitives + light) keep their label as the Type; anything else is a
        // library mesh.
        bool isBuiltIn = label is "cube" or "sphere" or "cylinder" or "cone" or "pyramid" or "light";
        var descriptor = new WorldObject
        {
            Id = _nextObjectId++,   // unique stable id (only the authority spawns; 5b broadcasts it)
            Type = isBuiltIn ? label : "mesh",
            Mesh = isBuiltIn ? null : label,
            Position = FromVec(spawnPos),
            Scale = 1f,
            Color = "White",
            Anchor = "Bottom",
            Radius = 1f,
        };

        var entry = BuildWorldObject(descriptor);
        if (entry == null) return;
        _selected = _editables.Count - 1; _fieldIndex = 0;

        // Server is the world authority: stream the new object to viewing clients. For a mesh,
        // also stream its .obj so a client that never had it can render it (idempotent overwrite).
        if (_online && _isServer)
        {
            var packet = new WorldEditPacket
            {
                Op = 1,
                Id = descriptor.Id,
                ObjectJson = JsonSerializer.Serialize(descriptor),
            };
            if (string.Equals(descriptor.Type, "mesh", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(descriptor.Mesh))
            {
                string path = Path.Combine(AppPaths.ModelsFolder, descriptor.Mesh + ".obj");
                try
                {
                    packet.MeshName = descriptor.Mesh;
                    packet.MeshObjText = File.ReadAllText(path);
                }
                catch (Exception ex) { Logger.Error($"Spawn: failed reading mesh '{descriptor.Mesh}' at {path}", ex); }
            }
            _netManager?.SendPacket(packet, _myNetId);
        }
    }

    private void SaveWorld()
    {
        _world = BuildLiveWorldConfig();
        WorldManager.Save(_world);
        _saveMsg = $"Saved to {_world.Name} ({_world.Objects.Count} objects)";
        _saveFlash = 120;
        Logger.Info(_saveMsg);
    }

    /// <summary>
    /// Builds a WorldConfig from the CURRENT live instances (the same FromInstance read-back
    /// SaveWorld does), preserving each object's id plus the platform and graphics — WITHOUT
    /// writing to disk. Used by SaveWorld and by OnWorldRequested, so a client connecting
    /// mid-edit gets the live state (with live ids), not the stale last-saved _world.
    /// </summary>
    private WorldConfig BuildLiveWorldConfig() => new WorldConfig
    {
        Name = _world.Name,
        Graphics = _world.Graphics,
        Platform = _world.Platform,
        Objects = _editables.Select(e => FromInstance(e.Descriptor, e.Instance, e.Light)).ToList(),
    };

    // Edge-triggered key press (true only on the frame the key transitions to down).
    private bool Pressed(ConsoleKey key)
    {
        bool down = Input.IsGetKey(key);
        bool was = _prevDown.Contains(key);
        if (down) _prevDown.Add(key); else _prevDown.Remove(key);
        return down && !was;
    }

    private void DrawEditorOverlay()
    {
        int x = 2, y = 12;
        if (!_editMode)
        {
            UI.AddText("[Tab] Edit mode", new Vector2Int(x, y), ConsoleColor.DarkGray);
            return;
        }

        UI.AddText("== EDIT MODE ==", new Vector2Int(x, y++), ConsoleColor.Green);
        UI.AddText($"Spawn: {_spawnTypes[_spawnIndex]}", new Vector2Int(x, y++), ConsoleColor.Cyan);

        string sel = _selected >= 0 && _selected < _editables.Count
            ? $"Selected: [{_selected + 1}/{_editables.Count}] {DescribeEntry(_editables[_selected])}"
            : $"Selected: (none) — {_editables.Count} object(s)";
        UI.AddText(sel, new Vector2Int(x, y++), ConsoleColor.Yellow);

        UI.AddText("[G] cycle type  [Enter] spawn  [ [ / ] ] select", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText("Move: J/L=X  I/K=Z  U/O=Y    [F5] save", new Vector2Int(x, y++), ConsoleColor.Gray);

        if (_saveFlash > 0)
            UI.AddText(_saveMsg, new Vector2Int(x, y), ConsoleColor.Green);
    }

    private static string DescribeEntry(EditEntry e) =>
        e.Descriptor.Type == "mesh" ? $"mesh:{e.Descriptor.Mesh}" : e.Descriptor.Type;

    // Visual-Studio-style properties panel for the selected object, framed like the Terminal.Gui
    // setup dialogs (a titled, bordered box) but drawn via the in-scene UI overlay. The box
    // auto-sizes to the current content each frame and sits in the top-right corner.
    private void DrawPropertiesPanel()
    {
        var lines = new List<(string text, ConsoleColor color)>();

        if (_selected < 0 || _selected >= _editables.Count)
        {
            lines.Add(("(no selection)", ConsoleColor.DarkGray));
        }
        else
        {
            var entry = _editables[_selected];
            string type = entry.Descriptor.Type ?? "";
            var fields = FieldsFor(entry);
            Field active = fields[Math.Clamp(_fieldIndex, 0, fields.Length - 1)];

            // Read-only header.
            lines.Add(($"  Type: {type}", ConsoleColor.DarkGray));
            if (type == "mesh")
                lines.Add(($"  Mesh: {entry.Descriptor.Mesh}", ConsoleColor.DarkGray));

            // Editable fields with the active-field cursor.
            foreach (var f in fields)
            {
                bool on = f == active;
                string line = $"{(on ? ">" : " ")} {FieldLabel(f)}: {FieldValue(f, entry)}";
                lines.Add((line, on ? ConsoleColor.Cyan : ConsoleColor.Gray));
            }

            lines.Add((",/. field   N/M value   Del", ConsoleColor.DarkGray));
        }

        DrawOverlayBox("PROPERTIES", lines);
    }

    // True when the console's output encoding can represent the box-drawing glyphs; if not, the
    // overlay box falls back to ASCII (+ - |). Probed once via an encoding round-trip.
    private static readonly bool BoxCharsSupported = DetectBoxChars();
    private static bool DetectBoxChars()
    {
        try
        {
            const string probe = "┌─┐│└┘";
            var enc = Console.OutputEncoding;
            return enc.GetString(enc.GetBytes(probe)) == probe;
        }
        catch { return false; }
    }

    // Draws a bordered box (Terminal.Gui-dialog feel: grey frame, inset green title) via the UI
    // overlay: a top border with the title seated in it, blank framed side rows with the content
    // overlaid, and a bottom border. Auto-sizes to the widest content line and is anchored to the
    // top-right corner, clear of the centre crosshair and the bottom chat.
    private void DrawOverlayBox(string title, IReadOnlyList<(string text, ConsoleColor color)> lines)
    {
        bool uni = BoxCharsSupported;
        char tl = uni ? '┌' : '+', tr = uni ? '┐' : '+', bl = uni ? '└' : '+', br = uni ? '┘' : '+';
        char hz = uni ? '─' : '-', vt = uni ? '│' : '|';
        const ConsoleColor border = ConsoleColor.Gray;
        const ConsoleColor titleColor = ConsoleColor.Green;

        // Interior width: the widest content line, but always enough to seat the title in the top
        // border, plus one space of padding on each side.
        int innerW = title.Length + 1;
        foreach (var (text, _) in lines) innerW = Math.Max(innerW, text.Length);
        innerW += 2;

        int boxW = innerW + 2;   // + the two side borders
        int left = Math.Max(0, Console.WindowWidth - boxW);
        int top = 0;

        // Top border with the inset title:  ┌─ PROPERTIES ───┐
        UI.AddText(tl + new string(hz, innerW) + tr, new Vector2Int(left, top), border);
        UI.AddText($" {title} ", new Vector2Int(left + 2, top), titleColor);

        // Content rows: a blank framed row, then the line overlaid one space in from the border.
        int row = top + 1;
        foreach (var (text, color) in lines)
        {
            UI.AddText(vt + new string(' ', innerW) + vt, new Vector2Int(left, row), border);
            UI.AddText(text, new Vector2Int(left + 2, row), color);
            row++;
        }

        UI.AddText(bl + new string(hz, innerW) + br, new Vector2Int(left, row), border);
    }

    private static string FieldLabel(Field f) => f switch
    {
        Field.PosX => "Pos X", Field.PosY => "Pos Y", Field.PosZ => "Pos Z",
        Field.RotX => "Rot X", Field.RotY => "Rot Y", Field.RotZ => "Rot Z",
        Field.Scale => "Scale", Field.RotateSpeed => "Spin", Field.Color => "Color",
        Field.Radius => "Radius", Field.Power => "Power",
        Field.Kind => "Kind", Field.DirX => "Dir X", Field.DirY => "Dir Y", Field.DirZ => "Dir Z",
        Field.ConeAngle => "Cone", Field.AreaSize => "Size", Field.Spin => "Spin",
        Field.Beams => "Beams", Field.Shape => "Shape",
        _ => f.ToString()
    };

    private static string FieldValue(Field f, EditEntry entry)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        return f switch
        {
            Field.PosX => inst.Position.X.ToString("F2"),
            Field.PosY => inst.Position.Y.ToString("F2"),
            Field.PosZ => inst.Position.Z.ToString("F2"),
            Field.RotX => inst.LocalRotate.X.ToString("F2"),
            Field.RotY => inst.LocalRotate.Y.ToString("F2"),
            Field.RotZ => inst.LocalRotate.Z.ToString("F2"),
            Field.Scale => (o?.Scale ?? 1f).ToString("F2"),
            Field.RotateSpeed => (o?.RotateSpeed ?? 0f).ToString("F2"),
            Field.Color => inst.Color.ToString(),
            Field.Radius => (inst as Sphere)?.R.ToString("F2") ?? "-",
            Field.Power => (entry.Light?.LightPower ?? entry.Descriptor.Power).ToString("F0"),
            Field.Kind => entry.Light != null ? LightKindToString(entry.Light.Kind) : "-",
            Field.DirX => (entry.Light?.Direction.X ?? 0f).ToString("F2"),
            Field.DirY => (entry.Light?.Direction.Y ?? 0f).ToString("F2"),
            Field.DirZ => (entry.Light?.Direction.Z ?? 0f).ToString("F2"),
            Field.ConeAngle => (entry.Light?.ConeAngleDeg ?? 0f).ToString("F0"),
            Field.AreaSize => (entry.Light?.AreaSize ?? 0f).ToString("F2"),
            Field.Spin => (entry.Light?.SpinSpeed ?? 0f).ToString("F2"),
            Field.Beams => (entry.Light?.BeamCount ?? 1).ToString(),
            Field.Shape => entry.Light != null ? ConeShapeToString(entry.Light.ConeShape) : "-",
            _ => ""
        };
    }

    // Inspection/test accessor: the live editable entries (each pairs a descriptor with its
    // instance, plus a Light for "light" objects). Read-only.
    public IReadOnlyList<EditEntry> EditableEntries => _editables;
    
    
    private void HandleGameInput(float dt)
    {
        float rotateSpeed = 1.5f;
        float moveSpeed = 5.0f;

        if (Input.IsGetKey(ConsoleKey.LeftArrow)) _myCamera.LocalRotate.Y += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.RightArrow)) _myCamera.LocalRotate.Y -= rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.UpArrow)) _myCamera.LocalRotate.Z += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.DownArrow)) _myCamera.LocalRotate.Z -= rotateSpeed * dt;
        _myCamera.LocalRotate.Z = Math.Clamp(_myCamera.LocalRotate.Z, -1.5f, 1.5f);

        // Camera roll about the forward axis (Q left / E right, R resets). X is the roll euler,
        // applied before pitch/yaw — it spins the view without changing the look direction.
        if (Input.IsGetKey(ConsoleKey.Q)) _myCamera.LocalRotate.X += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.E)) _myCamera.LocalRotate.X -= rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.R)) _myCamera.LocalRotate.X = 0f;

        // Field of view / zoom (Z in / X out, V resets). Narrower FOV = zoomed in.
        float fovSpeed = 30f;   // degrees/sec, held like the light controls
        if (Input.IsGetKey(ConsoleKey.Z)) _myCamera.Fov -= fovSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.X)) _myCamera.Fov += fovSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.V)) _myCamera.Fov = Camera.DefaultFov;
        _myCamera.Fov = Math.Clamp(_myCamera.Fov, 20f, 120f);

        Vector3 yawRotation = new Vector3(0, _myCamera.LocalRotate.Y, 0);
        Vector3 forward = new Vector3(1, 0, 0).Rotate(yawRotation);
        Vector3 right   = new Vector3(0, 0, 1).Rotate(yawRotation);

        if (Input.IsGetKey(ConsoleKey.W)) _myCamera.Position += forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.S)) _myCamera.Position -= forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.D)) _myCamera.Position += right * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.A)) _myCamera.Position -= right * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.Spacebar)) _myCamera.Position.Y += moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.C))        _myCamera.Position.Y -= moveSpeed * dt;
        
        if (Input.IsGetKey(ConsoleKey.OemPlus)) _mainLight.LightPower += moveSpeed * 50 * dt;
        if (Input.IsGetKey(ConsoleKey.OemMinus)) _mainLight.LightPower -= moveSpeed* 50  * dt;

        // Detail level: tap P to cycle render resolution 1->2->3->4->1 (lower = fewer rays = faster).
        if (Pressed(ConsoleKey.P)) RenderScale = RenderScale % 4 + 1;
    }

    private void OnTransformReceived(TransformPacket packet, int senderId)
    {
        if (senderId == _myNetId) return;

        if (!_remotePlayers.ContainsKey(senderId))
        {
            CreateRemotePlayer(senderId);
        }

        var player = _remotePlayers[senderId];
        player.Position = packet.Pos;
        player.LocalRotate = packet.Rot;
        player.UpdateGeometry();
    }
    
    private void CreateRemotePlayer(int netId)
    {
        Object3d newPlayerCube = CreateCube();
        ConsoleColor[] colors = { ConsoleColor.Red, ConsoleColor.Green, ConsoleColor.Blue, ConsoleColor.Yellow, ConsoleColor.Cyan, ConsoleColor.Magenta };
        newPlayerCube.Color = colors[netId % colors.Length];
        
        _remotePlayers.Add(netId, newPlayerCube);
        AddDisplaysObject(newPlayerCube);
    }
    
    private void HandleChatInput()
    {
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                if (!string.IsNullOrWhiteSpace(_currentInput))
                {
                    string msg = $"Player {_myNetId}: {_currentInput}";
                    _netManager?.SendPacket(new ChatPacket(msg), _myNetId);

                    AddChatMessage(msg);
                }
                _isChatting = false;
                _currentInput = "";
            }
            else if (keyInfo.Key == ConsoleKey.Escape)
            {
                _isChatting = false;
                _currentInput = "";
            }
            else if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (_currentInput.Length > 0)
                    _currentInput = _currentInput.Substring(0, _currentInput.Length - 1);
            }
            else
            {
                if (keyInfo.KeyChar != '\u0000')
                    _currentInput += keyInfo.KeyChar;
            }
        }
    }
    
    private void OnChatReceived(ChatPacket packet, int senderId)
    {
        AddChatMessage(packet.Message);
    }

    private void AddChatMessage(string msg)
    {
        _chatHistory.Add(msg);
        if (_chatHistory.Count > MaxHistory)
        {
            _chatHistory.RemoveAt(0);
        }
    }
    
    private void DrawChatInterface()
    {
        for (int i = 0; i < _chatHistory.Count; i++)
        {
            UI.AddText(_chatHistory[i], new Vector2Int(2, 2 + i), ConsoleColor.Gray);
        }

        if (_isChatting)
        {
            UI.AddText($"> {_currentInput}_", new Vector2Int(2, 2 + MaxHistory + 1), ConsoleColor.Yellow);
            UI.AddText("[ENTER] Send  [ESC] Cancel", new Vector2Int(2, 2 + MaxHistory + 2), ConsoleColor.DarkGray);
        }
        else
        {
            UI.AddText("Press [T] to chat", new Vector2Int(2, 2 + MaxHistory + 1), ConsoleColor.DarkGray);
        }
    }
    
    // ---- World download (4b): server answers a request; client applies the received world ----

    // Server side: a client asked for the world — pack ours (from the real models/) and send it.
    private void OnWorldRequested(WorldRequestPacket packet, int senderId)
    {
        // Send the LIVE world (current instances + live ids), not the stale last-saved _world,
        // so a client joining mid-edit sees exactly what the server has now.
        var live = BuildLiveWorldConfig();
        var sync = WorldSync.Pack(live, AppPaths.ModelsFolder);
        _netManager?.SendPacket(sync, _myNetId);
        Logger.Info($"Sent world '{live.Name}' to client {senderId} ({sync.MeshTexts.Count} mesh file(s)).");
    }

    // Client side: the world arrived. Runs on the main thread (via ProcessEvents in Update),
    // so it is safe to rebuild the scene here — Update fully precedes the render each frame.
    private void OnWorldSyncReceived(WorldSyncPacket packet, int senderId)
    {
        var (world, meshTexts) = WorldSync.Unpack(packet);
        ApplyReceivedWorld(world, meshTexts);
    }

    // Client side: a server edit delta arrived. Runs on the main thread (via ProcessEvents in
    // Update), so applying directly to the live scene is safe. Handles Modify/Spawn/Delete.
    private void OnWorldEditReceived(WorldEditPacket packet, int senderId)
    {
        if (packet.Op == 2)   // Delete: drop the object with this id
        {
            int del = _editables.FindIndex(e => e.Descriptor.Id == packet.Id);
            if (del < 0) { Logger.Warning($"WorldEdit Delete for unknown id {packet.Id}; ignoring."); return; }
            RemoveEntryAt(del);
            return;
        }

        // Modify (0) and Spawn (1) both carry a WorldObject in ObjectJson.
        WorldObject? o;
        try { o = JsonSerializer.Deserialize<WorldObject>(packet.ObjectJson); }
        catch (Exception ex) { Logger.Error("WorldEdit: bad ObjectJson", ex); return; }
        if (o == null) return;

        if (packet.Op == 1)   // Spawn: materialize a streamed mesh (if any), then build + append
        {
            if (!string.IsNullOrWhiteSpace(packet.MeshObjText) && !string.IsNullOrWhiteSpace(packet.MeshName))
                MaterializeMesh(packet.MeshName, packet.MeshObjText);

            // Idempotent: if this id already exists, drop the stale instance first (no duplicate).
            int existing = _editables.FindIndex(e => e.Descriptor.Id == o.Id);
            if (existing >= 0) RemoveEntryAt(existing);

            BuildWorldObject(o);   // loads the mesh from _modelsFolder == received/; keeps the server's id
            return;                // do NOT touch _selected — the client's selection is its own
        }

        // Modify (0): update the existing instance in place + keep the stored descriptor in sync.
        int idx = _editables.FindIndex(e => e.Descriptor.Id == packet.Id);
        if (idx < 0)
        {
            Logger.Warning($"WorldEdit Modify for unknown id {packet.Id}; ignoring.");
            return;
        }
        ApplyToInstance(o, _editables[idx].Instance);
        _editables[idx].Descriptor = o;
        // A light: push the FULL new state (kind/direction/cone/size/spin/power/color) onto the
        // paired Light, co-located with the (just-moved) marker — not just position+power — then
        // morph + re-aim the client's marker so it mirrors the server's kind/direction/size.
        if (_editables[idx].Light is Light light)
        {
            ApplyLightFields(light, o, _editables[idx].Instance.Position);
            ReplaceMarker(_editables[idx], BuildLightMarker(light.Kind, light.Direction, light.AreaSize));
        }
    }

    // Writes one received mesh to received/<name>.obj (idempotent overwrite). Shared by the 4b
    // bulk world download and the 5b Spawn handler that streams a brand-new mesh in real time.
    private static void MaterializeMesh(string name, string objText)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ReceivedFolder);
            File.WriteAllText(Path.Combine(AppPaths.ReceivedFolder, name + ".obj"), objText);
        }
        catch (Exception ex) { Logger.Error($"Failed writing received mesh '{name}'", ex); }
    }

    private void ApplyReceivedWorld(WorldConfig world, IReadOnlyDictionary<string, string> meshTexts)
    {
        // 1) Materialize the received meshes into a dedicated folder (never the user's models/).
        foreach (var kv in meshTexts)
            MaterializeMesh(kv.Key, kv.Value);
        _modelsFolder = AppPaths.ReceivedFolder;

        // 2) Tear down the current world's objects + platform (camera stays; lights reconciled below).
        foreach (var e in _editables)
        {
            if (e.Instance is IDisplays disp) RemoveDisplaysObject(disp);
            if (e.Light != null) RemoveLight(e.Light);   // drop a light object's engine Light too
        }
        _editables.Clear();
        _models.Clear();
        _selected = -1;
        if (_floor != null) { RemoveDisplaysObject(_floor); _floor = null; }

        // 3) Build the received world (BuildWorldObject loads meshes from _modelsFolder == received/).
        _world = world;
        // Adopt the server's ids verbatim (do NOT reassign); future local spawns (5b) start past them.
        _nextObjectId = _world.Objects.Count == 0 ? 0 : _world.Objects.Max(o => o.Id) + 1;
        BuildPlatform();
        foreach (var o in _world.Objects)
            BuildWorldObject(o);

        // 4) Apply its graphics and reconcile the lights against the placeholder's.
        EnableShadows = _world.Graphics.Shadows;
        Object3d.UseBvh = _world.Graphics.Bvh;

        bool wantOwn = !_world.Graphics.DisableCameraLight;
        if (wantOwn && !_ownLightEnabled) AddLight(_mainLight);
        if (!wantOwn && _ownLightEnabled) RemoveLight(_mainLight);
        _ownLightEnabled = wantOwn;

        // The settings extra light is now a normal "light" object inside the received config and was
        // already built in step 3 — no separate reconcile needed (the camera light above stays special).

        // 5) Done — drop the waiting overlay (replace on a repeat send).
        _awaitingWorld = false;
        _worldReceived = true;
        Logger.Info($"Applied received world '{_world.Name}': {_world.Objects.Count} objects, {meshTexts.Count} mesh file(s).");
    }

    public static Object3d CreateCube()
    {
        return new Object3d(
            new Vector3[]
            {
                new Vector3(-1f, -1f, 1f),   
                new Vector3(-1f, 1f, 1f),
                new Vector3(-1f, -1f, -1f),
                new Vector3(-1f, 1f, -1f),
                new Vector3(1f, -1f, 1f),
                new Vector3(1f, 1f, 1f),
                new Vector3(1f, -1f, -1f),
                new Vector3(1f, 1f, -1f)
            },
            new Vector3[]
            {
                new Vector3(-1f, 0, 0),
                new Vector3(0, 0, -1f),
                new Vector3(1f, 0, 0),
                new Vector3(0, 0, 1f),
                new Vector3(0, -1f, 0),
                new Vector3(0, 1f, 0)
            },
            new FacingInfo[]
            {
                new FacingInfo(new int[] {2,3,1}, 1),
                new FacingInfo(new int[] {4,7,3}, 2),
                new FacingInfo(new int[] {8,5,7}, 3),
                new FacingInfo(new int[] {6,1,5}, 4),
                new FacingInfo(new int[] {7,1,3}, 5),
                new FacingInfo(new int[] {4,6,8}, 6),
                new FacingInfo(new int[] {2,4,3}, 1),
                new FacingInfo(new int[] {4,8,7}, 2),
                new FacingInfo(new int[] {8,6,5}, 3),
                new FacingInfo(new int[] {6,2,1}, 4),
                new FacingInfo(new int[] {7,5,1}, 5),
                new FacingInfo(new int[] {4,2,6}, 6)
            });
    }

    // Builds a flat-shaded Object3d from local vertices + 1-based triangle index triples: one
    // normal per face, taken from the winding (Cross(b-a, c-a)) so the supplied normal always
    // agrees with the renderer's winding-based back-face test. Mirrors CreateCube's flat style.
    private static Object3d BuildFlat(List<Vector3> verts, List<(int a, int b, int c)> tris)
    {
        var vertArr = verts.ToArray();
        var normals = new Vector3[tris.Count];
        var faces = new FacingInfo[tris.Count];
        for (int k = 0; k < tris.Count; k++)
        {
            var (a, b, c) = tris[k];
            Vector3 e1 = vertArr[b - 1] - vertArr[a - 1];
            Vector3 e2 = vertArr[c - 1] - vertArr[a - 1];
            normals[k] = Vector3.Cross(e1, e2).Norm();
            faces[k] = new FacingInfo(new int[] { a, b, c }, k + 1);
        }
        return new Object3d(vertArr, normals, faces);
    }

    // Unit-ish cylinder: radius 1, y in [-1, 1], origin-centred like the cube, with end caps.
    public static Object3d CreateCylinder()
    {
        const int seg = 16;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a), -1f, MathF.Sin(a))); }
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a),  1f, MathF.Sin(a))); }
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // bottom centre
        int tc = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));   // top centre

        int B(int s) => (s % seg) + 1;          // bottom ring (1-based)
        int T(int s) => seg + (s % seg) + 1;    // top ring (1-based)

        var tris = new List<(int, int, int)>();
        for (int s = 0; s < seg; s++)
        {
            int b0 = B(s), b1 = B(s + 1), t0 = T(s), t1 = T(s + 1);
            tris.Add((b0, t1, b1));   // side quad (two outward-wound tris)
            tris.Add((b0, t0, t1));
            tris.Add((tc, t1, t0));   // top cap (+Y)
            tris.Add((bc, b0, b1));   // bottom cap (-Y)
        }
        return BuildFlat(verts, tris);
    }

    // Unit-ish cone: base radius 1 at y=-1, apex at (0,1,0), with a base cap.
    public static Object3d CreateCone()
    {
        const int seg = 16;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a), -1f, MathF.Sin(a))); }
        int apex = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // base centre

        int B(int s) => (s % seg) + 1;

        var tris = new List<(int, int, int)>();
        for (int s = 0; s < seg; s++)
        {
            tris.Add((B(s), apex, B(s + 1)));   // side
            tris.Add((bc, B(s), B(s + 1)));     // base cap (-Y)
        }
        return BuildFlat(verts, tris);
    }

    // Unit-ish pyramid: square base ±1 at y=-1, apex at (0,1,0).
    public static Object3d CreatePyramid()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f, -1f, -1f),   // 1
            new Vector3( 1f, -1f, -1f),   // 2
            new Vector3( 1f, -1f,  1f),   // 3
            new Vector3(-1f, -1f,  1f),   // 4
            new Vector3( 0f,  1f,  0f),   // 5 apex
        };
        var tris = new List<(int, int, int)>
        {
            (1, 2, 3), (1, 3, 4),                          // base (-Y)
            (1, 5, 2), (2, 5, 3), (3, 5, 4), (4, 5, 1),    // sides
        };
        return BuildFlat(verts, tris);
    }

    // Builds the platform floor for the given config: a square (Size half-extent), a
    // Width x Depth rectangle, or a circular disc (diameter Size). All face +Y (up), exactly
    // like the square floor, so the renderer's winding-based cull keeps them visible from above.
    public static Object3d CreatePlatform(PlatformConfig p)
    {
        switch (p.Shape?.Trim().ToLowerInvariant())
        {
            case "rectangle": return CreatePlane(p.Width * 0.5f, p.Depth * 0.5f);
            case "circle":    return CreateDisc(p.Size * 0.5f);
            default:          return CreatePlane(p.Size, p.Size);   // "square" (and any legacy/unknown)
        }
    }

    // A flat quad on y=0 spanning [-halfX, halfX] x [-halfZ, halfZ], +Y normal (the square uses
    // halfX == halfZ == Size, preserving the original geometry exactly).
    private static Object3d CreatePlane(float halfX, float halfZ)
    {
        return new Object3d(
            new Vector3[]
            {
                new Vector3(-halfX, 0f, halfZ),
                new Vector3(halfX, 0f, halfZ),
                new Vector3(-halfX, 0f, -halfZ),
                new Vector3(halfX, 0f, -halfZ)
            },
            new Vector3[]
            {
                new Vector3(0f, 1f, 0f)
            },
            new FacingInfo[]
            {
                new FacingInfo(new int[] {2,3,1}, 1),
                new FacingInfo(new int[] {2,4,3}, 1),
            });
    }

    // A flat disc on y=0: a triangle fan from the centre to a ring at the given radius, wound
    // (centre, ring[s+1], ring[s]) so each face's normal is +Y — same facing as the square floor.
    private static Object3d CreateDisc(float radius)
    {
        const int seg = 32;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++)
        {
            float a = MathF.Tau * s / seg;
            verts.Add(new Vector3(radius * MathF.Cos(a), 0f, radius * MathF.Sin(a)));
        }
        int centre = verts.Count + 1; verts.Add(new Vector3(0f, 0f, 0f));

        int R(int s) => (s % seg) + 1;   // ring vertex (1-based)
        var tris = new List<(int, int, int)>();
        for (int s = 0; s < seg; s++)
            tris.Add((centre, R(s + 1), R(s)));   // wound so Cross(b-a,c-a) points +Y

        return BuildFlat(verts, tris);
    }
}