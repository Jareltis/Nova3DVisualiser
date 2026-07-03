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

public class PriviewNetworkScene : Scene
{
    private readonly bool _online;
    private readonly bool _isServer;
    private readonly NetworkManager? _netManager;
    private readonly int _myNetId;
    private readonly Dictionary<int, Object3d> _remotePlayers = new();
    private readonly Dictionary<int, int> _connToNet = new();   // server: connId -> peer netId (for disconnect cleanup)

    // Large-mesh streaming: a big live spawn is split into chunks sent a few per frame so other
    // packets interleave. Small meshes still inline in one WorldEditPacket.
    private const int MeshChunkThreshold = 49152;   // .obj text <= this bytes inlines as today
    private const int MeshChunkSize = 16384;        // chunk payload length (chars)
    private const int ChunksPerFrame = 2;           // outgoing actions drained per Update
    private readonly Queue<Action> _outgoing = new();                            // server: paced mesh-chunk + spawn sends
    private readonly Dictionary<string, (int total, string[] parts)> _meshChunks = new();   // client: mesh reassembly buffer
    private readonly Dictionary<string, (int total, byte[][] parts)> _textureChunks = new();   // client: texture (PNG bytes) reassembly buffer

    readonly Camera _myCamera;
    readonly Light _mainLight;

    // ---- Local player = BODY + CAMERA (B-camera) ----
    // The player is two linked things: a BODY (the physical/movement anchor — physics + collision act on
    // it, its transform is streamed so peers see the avatar) and a CAMERA that DERIVES from the body via a
    // per-mode offset (the camera RIG the future multi-view work builds on). FIRSTPERSON/THIRDPERSON are
    // live; FIXED/FOLLOW/SECONDPERSON are reserved (a clean seam) — not implemented this stage.
    public enum CameraMode { FirstPerson, ThirdPerson, Fixed, Follow, SecondPerson }
    private CameraMode _cameraMode = CameraMode.FirstPerson;   // default 1st-person (F7 toggles 1st/3rd)
    private Object3d _localBody = null!;   // the local player's visible avatar (created in Start); the movement anchor
    private bool _bodyDisplayed = false;   // whether the body avatar is currently drawn (only in 3rd person)
    private int _localBodyId;              // the local player's stable id (= network id); name is "player #<id>"
    // 3rd-person rig: the camera sits this far BEHIND the body along the look direction and this far ABOVE.
    private const float ThirdPersonBack = 4f;
    private const float ThirdPersonUp = 1.5f;

    private WorldConfig _world;
    private readonly List<Object3d> _models = new();   // spinnable Object3d objects (meshes + cubes)
    private string _modelsFolder = AppPaths.ModelsFolder;   // where world meshes load from (server world -> received/ on a client)
    private string _texturesFolder = AppPaths.TexturesFolder;   // where object textures load from (-> received/textures on a client)
    private Object3d? _floor;                              // the platform, tracked so a client can replace it

    private bool _ownLightEnabled;

    // ---- Client world download (4b) ----
    private bool _awaitingWorld = false;   // client only: true until the server's world arrives
    private bool _worldReceived = false;   // client only: a world has been applied
    private float _requestTimer = 0f;      // client only: countdown to the next WorldRequestPacket

    private bool _isChatting = false;
    private string _currentInput = "";
    // Inline field entry (B-direct-input — generalizes the B-identity rename seed to every editable field):
    // while active, keystrokes fill _entryBuffer (Enter confirms, Esc cancels). The Name field takes
    // name-safe text; a NUMERIC field takes digits/-/. and is PARSED + CLAMPED to that field's range on
    // confirm. Movement/physics pause while typing (same dispatch as chat/rename). One input system, not two.
    private bool _entryMode = false;
    private string _entryBuffer = "";
    private Field _entryField = Field.Name;   // which field the buffer is being typed into
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
        // For the special "platform" entry: the LIVE PlatformConfig (mutated in place), so editing
        // its Shape/size/Color persists on save/sync. Non-null ONLY for the platform entry.
        public PlatformConfig? Platform;
    }

    private bool _editMode = false;
    private readonly List<EditEntry> _editables = new();
    private int _selected = -1;
    // The platform reserves id 0 (a real, stable id in the same space); ordinary objects start at 1, so
    // every object — primitives/meshes/spheres/flatpictures/LIGHTS + the PLATFORM — has a unique id >= 0.
    // The platform's SPECIAL handling stays keyed on Type == "platform", never on its id value.
    private const int PlatformId = 0;
    private int _nextObjectId = PlatformId + 1;   // next id to hand a spawned object (authority; 5b broadcasts spawns)

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
    private const float InfluenceStep = 0.05f;           // light color-influence adjust per N/M press
    private const int ColorStep = 17;                    // 0..255 in 15 presses, hits both ends
    private const float DirStep = 0.15f;                 // light direction component adjust (then re-normalized)
    private const float ConeStep = 2f;                   // spot cone half-angle, degrees per press
    private const float AreaStep = 0.1f;                 // area light half-extent per press
    private const float LightSpinStep = 0.2f;            // light direction sweep speed, rad/s per press
    private const float PlatformStep = 0.5f;             // platform size/width/depth adjust per N/M press
    private const float PlatformMin = 0.5f;              // smallest platform extent (never zero/negative)
    private const float MassStep = 0.5f;                 // per-object mass adjust per N/M press
    private const float MassMin = 0.1f;                  // smallest mass (never zero/negative)
    private const float RestStep = 0.1f;                 // per-object restitution (bounce) adjust per N/M press
    private const float FrictionStep = 0.1f;             // per-object friction (μ) adjust per N/M press
    private const float FrictionMax = 2f;                // clamp for the friction editor field
    private const float RollFrictionStep = 0.02f;        // per-object rolling-friction adjust per N/M press
    private const float ColorFadeStep = 0.1f;            // per-object colour paleness adjust per N/M press
    private const float TextureScaleStep = 0.1f;         // per-object UV tiling adjust per N/M press
    private static readonly string[] PlatformShapes = { "square", "rectangle", "circle" };   // ordered for PlatShape cycle
    private const float LightMarkerScale = 0.3f;         // size of the small cube that marks a light
    private static readonly Vector3 LightMarkerOffset = Vector3.Zero;  // Light sits exactly at its marker; the marker is a non-shadow-caster so there's no self-shadow to avoid
    private readonly HashSet<ConsoleKey> _prevDown = new();
    private int _saveFlash = 0;
    private string _saveMsg = "";

    // Properties-panel editable fields (read-only Type/Mesh are not in here).
    private enum Field { Name, PosX, PosY, PosZ, RotX, RotY, RotZ, Scale, RotateSpeed, ColorR, ColorG, ColorB, ColorA, Radius,
                         Power, ClrInf, Kind, DirX, DirY, DirZ, ConeAngle, AreaSize, AreaShape, Spin, Beams, Shape,
                         PlatShape, PlatSize, PlatWidth, PlatDepth, Collides, Gravity, Collider, Mass, Restitution, Friction, RollingFriction, ColorFade,
                         Texture, TextureScale, TextureFace, TextureFilter }
    private int _fieldIndex = 0;

    // Editable fields for the selected entry. A light's set depends on its Kind: every light shows
    // Pos/Power/Color/Kind; directional/spot/area add Direction + Spin, spot adds Cone, area adds
    // Size. Spheres and the rest are by type, as before.
    private static Field[] FieldsFor(EditEntry e)
    {
        string type = e.Descriptor.Type ?? "";
        if (type == "sphere")
            return new[] { Field.PosX, Field.PosY, Field.PosZ, Field.Radius, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Texture, Field.TextureScale, Field.TextureFace, Field.TextureFilter, Field.Collides, Field.Gravity, Field.Mass, Field.Restitution, Field.Friction, Field.RollingFriction, Field.Name };
        // A flat picture is a visual panel: transform + colour + paleness + its texture params. No physics
        // fields — it defaults non-colliding.
        if (type == "flatpicture")
            return new[] { Field.PosX, Field.PosY, Field.PosZ, Field.RotX, Field.RotY, Field.RotZ, Field.Scale, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Texture, Field.TextureScale, Field.TextureFace, Field.TextureFilter, Field.Name };
        if (type == "platform")
        {
            // The floor moves like any object (Pos), then a shape-dependent size set + Color.
            return (e.Platform?.Shape?.Trim().ToLowerInvariant()) switch
            {
                "rectangle" => new[] { Field.PosX, Field.PosY, Field.PosZ, Field.PlatShape, Field.PlatWidth, Field.PlatDepth, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Collides, Field.Gravity, Field.Name },
                "circle"    => new[] { Field.PosX, Field.PosY, Field.PosZ, Field.PlatShape, Field.PlatSize, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Collides, Field.Gravity, Field.Name },
                _           => new[] { Field.PosX, Field.PosY, Field.PosZ, Field.PlatShape, Field.PlatSize, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Collides, Field.Gravity, Field.Name },   // square + legacy/unknown
            };
        }
        if (type == "light")
        {
            var f = new List<Field> { Field.PosX, Field.PosY, Field.PosZ, Field.Power, Field.ClrInf, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Kind };
            switch (e.Light?.Kind ?? LightKind.Point)
            {
                case LightKind.Directional: f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.Spin }); break;
                case LightKind.Spot:        f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.ConeAngle, Field.Beams, Field.Shape, Field.Spin }); break;
                case LightKind.Area:        f.AddRange(new[] { Field.DirX, Field.DirY, Field.DirZ, Field.AreaSize, Field.AreaShape, Field.Spin }); break;
            }
            f.Add(Field.Gravity);   // a light has no collider, but it CAN be made to fall
            f.Add(Field.Name);
            return f.ToArray();
        }
        return new[] { Field.PosX, Field.PosY, Field.PosZ, Field.RotX, Field.RotY, Field.RotZ, Field.Scale, Field.RotateSpeed, Field.ColorR, Field.ColorG, Field.ColorB, Field.ColorA, Field.ColorFade, Field.Texture, Field.TextureScale, Field.TextureFace, Field.TextureFilter, Field.Collides, Field.Gravity, Field.Collider, Field.Mass, Field.Restitution, Field.Friction, Field.RollingFriction, Field.Name };
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
        PacketManager.RegisterPacket<WorldSettingsPacket>();
        PacketManager.RegisterPacket<PlayerLeftPacket>();
        PacketManager.RegisterPacket<MeshChunkPacket>();
        PacketManager.RegisterPacket<TextureChunkPacket>();
        PacketManager.RegisterPacket<PhysicsSyncPacket>();

        PacketManager.Subscribe<TransformPacket>(OnTransformReceived);
        PacketManager.Subscribe<ChatPacket>(OnChatReceived);
        // Both sides: a client drops left peers; a server (future relayed leaves) is harmless.
        PacketManager.Subscribe<PlayerLeftPacket>(OnPlayerLeft);

        if (isServer)
        {
            // The server answers a joining client's world request with its world.
            PacketManager.Subscribe<WorldRequestPacket>(OnWorldRequested);
            _netManager.StartServer(port);
            _netManager.OnClientDisconnected += OnClientDisconnectedServer;
            Logger.Info($"Server listening on port {port}");
            Console.Title = $"SERVER (Port: {port}) | ID: {_myNetId}";
        }
        else
        {
            // The client downloads the server's world and rebuilds the scene from it,
            // then applies the server's live edit deltas in place (view-only).
            PacketManager.Subscribe<WorldSyncPacket>(OnWorldSyncReceived);
            PacketManager.Subscribe<WorldEditPacket>(OnWorldEditReceived);
            PacketManager.Subscribe<WorldSettingsPacket>(OnWorldSettingsReceived);
            PacketManager.Subscribe<MeshChunkPacket>(OnMeshChunkReceived);
            PacketManager.Subscribe<TextureChunkPacket>(OnTextureChunkReceived);
            PacketManager.Subscribe<PhysicsSyncPacket>(OnPhysicsSyncReceived);
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

        // The local player's BODY: the movement anchor, seeded at the camera's start (so 1st-person feel is
        // identical). It carries a stable id (= this peer's network id) + a system name "player #<id>", is
        // NOT added to the editables list (it's the player, not world content — like the remote avatars),
        // and is drawn only in 3rd person (ApplyCameraMode; default 1st person → not displayed).
        _localBodyId = _myNetId;
        _localBody = CreatePlayerAvatar(_localBodyId);
        _localBody.Position = _myCamera.Position;
        _localBody.UpdateGeometry();
        ApplyCameraMode();   // 1st person by default → body stays hidden

        // Spawn palette for the editor: the two primitives, then every library mesh.
        _spawnTypes.Add("cube");
        _spawnTypes.Add("sphere");
        _spawnTypes.Add("cylinder");
        _spawnTypes.Add("cone");
        _spawnTypes.Add("pyramid");
        _spawnTypes.Add("ramp");
        _spawnTypes.Add("flatpicture");
        _spawnTypes.Add("light");
        _spawnTypes.Add("platform");
        _spawnTypes.AddRange(WorldManager.ListAvailableMeshes());

        // The world authority (server, and local solo) stamps stable sequential ids onto its
        // objects, ignoring any file values so ids are guaranteed unique. A client leaves them
        // alone — it adopts the server's ids verbatim when the world arrives (ApplyReceivedWorld).
        if (CanEdit)
        {
            MaybeInjectExtraLight();   // authority/solo: add the settings extra light as a real object (once)
            for (int i = 0; i < _world.Objects.Count; i++)
                _world.Objects[i].Id = i + 1;   // ids 1..N — id 0 is reserved for the platform
            _nextObjectId = _world.Objects.Count + 1;
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
        floor.Position = ToVec(_world.Platform.Position);
        floor.Color = ParseColor(_world.Platform.Color, new Rgba32(255, 255, 0));
        ApplyPhysicsFlags(floor, _world.Platform.Collides, _world.Platform.Gravity);
        floor.UpdateGeometry();   // recompute the world AABB at its actual position (collider uses it)
        AddDisplaysObject(floor);
        _floor = floor;

        // The platform is a SPECIAL editable entry backed by the LIVE PlatformConfig (mutated in place).
        // It carries a REAL id (PlatformId = 0, reserved) — its special handling is keyed on Type, not id.
        // Runs on both authority and client (re-run by ApplyReceivedWorld), so it covers all modes.
        _editables.Add(new EditEntry
        {
            Descriptor = new WorldObject { Type = "platform", Id = PlatformId, Name = _world.Platform.Name },
            Instance   = floor,
            Platform   = _world.Platform,
        });
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
            case "ramp":
            case "flatpicture":
            {
                Object3d prim = type switch
                {
                    "cylinder"    => CreateCylinder(),
                    "cone"        => CreateCone(),
                    "pyramid"     => CreatePyramid(),
                    "ramp"        => CreateRamp(),
                    "flatpicture" => CreateFlatPicture(),
                    _             => CreateCube(),
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
                Object3d marker = BuildLightMarker(kind, ToVec(o.Direction), o.LightSize, ParseConeShape(o.ConeShape), o.ConeAngle, ParseConeShape(o.AreaShape), o.BeamCount);
                marker.Position = ToVec(o.Position);
                marker.Color = ParseColor(o.Color, new Rgba32(255, 255, 0));
                marker.ColorFade = o.ColorFade;   // pale the marker too
                marker.Collides = false;   // a light marker is visual-only — never a collider
                marker.Gravity = o.Gravity && _world.Physics.GravityEnabled;   // a light CAN fall if its gravity flag is on
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

    // ---- Collision (camera bubble vs scene colliders) ----
    private const float CameraRadius = 0.35f;

    // Push a sphere (c,r) out of an AABB. Returns c unchanged if not penetrating.
    public static Vector3 ResolveSphereVsAabb(Vector3 c, float r, Vector3 min, Vector3 max)
    {
        Vector3 cl = new(Math.Clamp(c.X, min.X, max.X), Math.Clamp(c.Y, min.Y, max.Y), Math.Clamp(c.Z, min.Z, max.Z));
        Vector3 d = c - cl; float d2 = d * d;
        if (d2 >= r * r) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return cl + d * (r / dist); }   // outside-ish: push to the surface
        // centre inside the box: eject along the least-penetrating face
        float px1 = c.X - min.X, px2 = max.X - c.X, py1 = c.Y - min.Y, py2 = max.Y - c.Y, pz1 = c.Z - min.Z, pz2 = max.Z - c.Z;
        float m = MathF.Min(MathF.Min(MathF.Min(px1, px2), MathF.Min(py1, py2)), MathF.Min(pz1, pz2));
        if (m == px1) c.X = min.X - r; else if (m == px2) c.X = max.X + r;
        else if (m == py1) c.Y = min.Y - r; else if (m == py2) c.Y = max.Y + r;
        else if (m == pz1) c.Z = min.Z - r; else c.Z = max.Z + r;
        return c;
    }

    // Push a sphere (c,r) out of an ORIENTED box: center + 3 orthonormal axes (ax/ay/az) + per-axis
    // half-extents (half). Same math as ResolveSphereVsAabb but in the box's local frame, so a rotated
    // mesh blocks at its true silhouette instead of an inflated world AABB. Returns c if not penetrating.
    public static Vector3 ResolveSphereVsObb(Vector3 c, float r, Vector3 center, Vector3 ax, Vector3 ay, Vector3 az, Vector3 half)
    {
        Vector3 d = c - center;
        float ex = d * ax, ey = d * ay, ez = d * az;                 // sphere center in the box's local coords
        float qx = Math.Clamp(ex, -half.X, half.X);
        float qy = Math.Clamp(ey, -half.Y, half.Y);
        float qz = Math.Clamp(ez, -half.Z, half.Z);
        Vector3 q = center + ax * qx + ay * qy + az * qz;            // closest point on/in the box
        Vector3 diff = c - q; float d2 = diff * diff;
        if (d2 >= r * r) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return q + diff * (r / dist); }   // outside-ish: push to the surface
        // centre inside the box: eject along the least-penetrating local face, keeping the other two axes.
        float dxp = half.X - ex, dxn = ex + half.X, dyp = half.Y - ey, dyn = ey + half.Y, dzp = half.Z - ez, dzn = ez + half.Z;
        float m = MathF.Min(MathF.Min(MathF.Min(dxp, dxn), MathF.Min(dyp, dyn)), MathF.Min(dzp, dzn));
        float nx = ex, ny = ey, nz = ez;
        if (m == dxp) nx = half.X + r; else if (m == dxn) nx = -half.X - r;
        else if (m == dyp) ny = half.Y + r; else if (m == dyn) ny = -half.Y - r;
        else if (m == dzp) nz = half.Z + r; else nz = -half.Z - r;
        return center + ax * nx + ay * ny + az * nz;
    }

    // Build the OBB params for a mesh (center / orthonormal axes / world half-extents) from its local
    // bbox + transform, then resolve the sphere against it. Mirrors Object3d's local→world transform.
    private static Vector3 ResolveSphereVsObb(Vector3 c, float r, Object3d o)
    {
        Vector3 rot = o.TotalRotation;
        Vector3 ax = new Vector3(1f, 0f, 0f).Rotate(rot);
        Vector3 ay = new Vector3(0f, 1f, 0f).Rotate(rot);
        Vector3 az = new Vector3(0f, 0f, 1f).Rotate(rot);
        Vector3 center = (o.LocalCenter * o.Scale).Rotate(rot) + o.Position;
        Vector3 half = o.Size * (0.5f * o.Scale);
        return ResolveSphereVsObb(c, r, center, ax, ay, az, half);
    }

    // Push a sphere (c,r) out of another sphere (center,sr).
    public static Vector3 ResolveSphereVsSphere(Vector3 c, float r, Vector3 center, float sr)
    {
        Vector3 d = c - center; float d2 = d * d; float rr = r + sr;
        if (d2 >= rr * rr) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return center + d * (rr / dist); }
        return c + new Vector3(0f, rr, 0f);   // coincident: pop up
    }

    // Closest point on triangle (a,b,c) to point p — the classic Voronoi-region method (Ericson, Real-Time
    // Collision Detection). Used so a sphere collides with a mesh's REAL faces, not its bounding box. Pure + tested.
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = ab * ap, d2 = ac * ap;                                  // Vector3*Vector3 = dot
        if (d1 <= 0f && d2 <= 0f) return a;                                // vertex region A
        Vector3 bp = p - b;
        float d3 = ab * bp, d4 = ac * bp;
        if (d3 >= 0f && d4 <= d3) return b;                                // vertex region B
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));   // edge AB
        Vector3 cp = p - c;
        float d5 = ab * cp, d6 = ac * cp;
        if (d6 >= 0f && d5 <= d6) return c;                                // vertex region C
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));   // edge AC
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));   // edge BC
        float denom = 1f / (va + vb + vc);                                 // interior (barycentric)
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Eject the player BODY's bubble out of every collidable scene object (a couple of passes catch
    // multiple simultaneous contacts). A sphere collider for Sphere, the world AABB for Object3d. The
    // bubble is the body's physical presence (was the camera's, pre-B-camera); the camera derives from it.
    private void ResolveBodyCollision()
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (var e in _editables)
            {
                if (e.Instance is not { Collides: true }) continue;
                if (e.Instance is Sphere s)        _localBody.Position = ResolveSphereVsSphere(_localBody.Position, CameraRadius, s.Position, s.R);
                else if (e.Instance is Object3d o) _localBody.Position = o.Collider == ColliderShape.Obb
                    ? ResolveSphereVsObb(_localBody.Position, CameraRadius, o)
                    : ResolveSphereVsAabb(_localBody.Position, CameraRadius, o.WorldMin, o.WorldMax);
            }
    }

    // ---- Player character controller (only when the world has gravity) ----
    // When gravity is on and fly mode is off, the camera is a walking character: gravity pulls it
    // down, collision rests it on the floor/objects, Space jumps. F1 toggles a free-fly (noclip)
    // mode for building/inspecting, which restores the old Space/C vertical flight. Player physics
    // runs locally for every peer (it only moves _myCamera, never world objects), so it never desyncs.
    private float _playerVelY = 0f;     // camera vertical velocity (gravity + jump)
    private bool _onGround = false;     // camera resting on a surface this frame (gates jumping)
    private bool _flyMode = false;      // F1: free-fly/noclip instead of walking
    private const float JumpSpeed = 6f; // initial upward speed of a jump
    private const float GroundEps = 1e-3f;

    // The player walks (gravity + ground + jump) only in a gravity world with fly mode off.
    private bool PlayerWalking => _world.Physics.GravityEnabled && !_flyMode;

    // One player step: jump on Space (from the ground), apply gravity, then let the camera-bubble
    // collision rest the camera on whatever is beneath it. Landing/headbonk are read from how the
    // collision nudged Y relative to the pre-collision position.
    private void StepPlayerPhysics(float dt)
    {
        if (_onGround && Pressed(ConsoleKey.Spacebar)) _playerVelY = JumpSpeed;   // jump

        _playerVelY -= _world.Physics.GravityStrength * dt;
        _localBody.Position.Y += _playerVelY * dt;

        float yBefore = _localBody.Position.Y;
        ResolveBodyCollision();                // pushes the bubble out of floor + objects (all axes)
        float yAfter = _localBody.Position.Y;

        // Landed: collision lifted us while we were falling -> rest on the surface.
        if (_playerVelY <= 0f && yAfter > yBefore + GroundEps) { _onGround = true; _playerVelY = 0f; }
        else _onGround = false;
        // Bonked a ceiling while rising -> stop the upward velocity.
        if (_playerVelY > 0f && yAfter < yBefore - GroundEps) _playerVelY = 0f;
    }

    // ---- Camera RIG: the camera derives from the body via a per-mode offset (pure math so it is unit-
    // testable). FIRSTPERSON: the camera sits AT the body (its head/eye anchor — the body is at eye level,
    // matching today's feel and the remote-avatar convention). THIRDPERSON: behind + above the body along
    // the yaw look direction. Reserved modes fall back to first-person for now. ----
    public static Vector3 CameraOffsetFor(Vector3 lookRotate, CameraMode mode)
    {
        if (mode == CameraMode.ThirdPerson)
        {
            Vector3 forward = new Vector3(1, 0, 0).Rotate(new Vector3(0, lookRotate.Y, 0));   // yaw-only look
            return forward * (-ThirdPersonBack) + new Vector3(0, ThirdPersonUp, 0);           // behind + above
        }
        return Vector3.Zero;   // first-person (and reserved modes) — camera at the body's eye
    }

    // The camera position for a given body position + look rotation + mode. body + the mode offset.
    public static Vector3 CameraPositionFor(Vector3 bodyPos, Vector3 lookRotate, CameraMode mode)
        => bodyPos + CameraOffsetFor(lookRotate, mode);

    // Places the camera at the body + mode offset, and keeps the avatar facing the look direction (for the
    // 3rd-person view + the streamed transform). Called every frame after movement/physics settle the body.
    private void SyncCameraToBody()
    {
        _localBody.LocalRotate = _myCamera.LocalRotate;                 // avatar faces where we look (matches remote avatars)
        if (_bodyDisplayed) _localBody.UpdateGeometry();               // refresh its world AABB for the GPU cull when drawn
        _myCamera.Position = CameraPositionFor(_localBody.Position, _myCamera.LocalRotate, _cameraMode);
    }

    // Adds/removes the local body avatar from the display so it is drawn ONLY in 3rd person (you never see
    // inside your own body in 1st person). Idempotent.
    private void ApplyCameraMode()
    {
        bool show = _cameraMode == CameraMode.ThirdPerson;
        if (show && !_bodyDisplayed) { AddDisplaysObject(_localBody); _bodyDisplayed = true; }
        else if (!show && _bodyDisplayed) { RemoveDisplaysObject(_localBody); _bodyDisplayed = false; }
    }

    // A player-avatar cube coloured by network id (shared by the local body + every remote peer's avatar),
    // so you and everyone else render the same kind of body. Never collides / never locally simulated.
    private static Object3d CreatePlayerAvatar(int netId)
    {
        Object3d cube = CreateCube();
        Rgba32[] palette = { new Rgba32(255, 0, 0), new Rgba32(0, 255, 0), new Rgba32(0, 0, 255), new Rgba32(255, 255, 0), new Rgba32(0, 255, 255), new Rgba32(255, 0, 255) };
        cube.Color = palette[((netId % palette.Length) + palette.Length) % palette.Length];
        cube.Collides = false;   // an avatar never blocks the local camera (the controller drives the body)
        cube.Gravity = false;    // avatars are driven by the controller / network, never local object gravity
        return cube;
    }

    // Server: ids whose physics moved them since the last sync flush (the "changed" set). Client:
    // per-id interpolation targets it eases the live instance toward. Authority streams a compact
    // PhysicsSyncPacket every PhysicsSyncEvery frames; clients dead-reckon + lerp so falling is smooth.
    private readonly HashSet<int> _physMoved = new();                  // server: changed-object ids
    private readonly Dictionary<int, NetTarget> _physTargets = new();  // client: id -> eased target
    private int _physFrame = 0;                                        // server: sync throttle counter
    private const int PhysicsSyncEvery = 3;                            // send a batch every Nth frame (~20 Hz @60fps)
    private const float PhysLerpRate = 12f;                            // client ease speed toward target (1/sec)

    // Client-side interpolation target for one synced object: last authoritative position + velocity.
    private sealed class NetTarget { public Vector3 Pos; public Vector3 Lin; public Vector3 Rot; public Vector3 AngVel; }

    // Combined coefficient of restitution for a contact between two bodies: the geometric mean
    // sqrt(eA·eB), so a rebound needs BOTH surfaces elastic — a dead/soft surface kills the bounce
    // (a superball on mud barely bounces), two springy ones stay springy. Negatives clamp to 0.
    // Pure + tested. (max() would be "trampoline" semantics; the geometric mean is the energy model.)
    public static float CombineRestitution(float a, float b) => MathF.Sqrt(MathF.Max(0f, a) * MathF.Max(0f, b));

    // Per-object coefficient of restitution, resolving the "inherit world" sentinel: a non-negative
    // stored value is used as-is, a negative one falls back to the world default (PhysicsConfig.Restitution).
    private float RestitutionOf(GameObject o) => o.Restitution >= 0f ? o.Restitution : _world.Physics.Restitution;

    // Diagonal box inertia tensor — the principal moments about X/Y/Z for a solid box of size
    // (sx,sy,sz) and mass: I_x = m(sy²+sz²)/12, I_y = m(sx²+sz²)/12, I_z = m(sx²+sy²)/12. Returned as
    // a Vector3 of the three moments (a world-axis approximation; exact when the box is axis-aligned).
    public static Vector3 BoxInertia(float mass, float sx, float sy, float sz) => new Vector3(
        mass * (sy * sy + sz * sz) / 12f,
        mass * (sx * sx + sz * sz) / 12f,
        mass * (sx * sx + sy * sy) / 12f);

    // ---- Quaternion orientation (drift-free SO(3) integration) ----
    // Phase 7 stores a spinning object's orientation as a unit quaternion (the source of truth) and
    // converts it to the engine's Euler LocalRotate each frame for rendering. This removes the Euler-
    // integration drift of phase 6: angular velocity integrates correctly in SO(3), then we project to
    // Euler in the ENGINE'S rotation order (Vector3.Rotate = Rx then Ry then Rz, i.e. R = Rz·Ry·Rx).
    public readonly struct Quat
    {
        public readonly float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);
    }

    // Hamilton product a⊗b.
    public static Quat QuatMul(Quat a, Quat b) => new Quat(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static Quat QuatNorm(Quat q)
    {
        float m = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return m > 1e-12f ? new Quat(q.X / m, q.Y / m, q.Z / m, q.W / m) : Quat.Identity;
    }

    // Euler (engine order: apply Rx, then Ry, then Rz → q = qz·qy·qx) → unit quaternion.
    public static Quat QuatFromEuler(Vector3 e)
    {
        float hx = e.X * 0.5f, hy = e.Y * 0.5f, hz = e.Z * 0.5f;
        Quat qx = new Quat(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
        Quat qy = new Quat(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
        Quat qz = new Quat(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));
        return QuatMul(qz, QuatMul(qy, qx));
    }

    // Quaternion → Euler (X,Y,Z) in the engine's order, extracted from R = Rz·Ry·Rx. Gimbal lock at
    // |Y|≈90° is a representation ambiguity only (the quaternion remains the source of truth).
    public static Vector3 EulerFromQuat(Quat q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float r20 = 2f * (x * z - w * y);
        float r21 = 2f * (y * z + w * x);
        float r22 = 1f - 2f * (x * x + y * y);
        float r10 = 2f * (x * y + w * z);
        float r00 = 1f - 2f * (y * y + z * z);
        return new Vector3(MathF.Atan2(r21, r22), MathF.Asin(Math.Clamp(-r20, -1f, 1f)), MathF.Atan2(r10, r00));
    }

    // Integrate a WORLD-frame angular velocity ω for dt: q' = normalize(q + ½·(ω⊗q)·dt). First-order
    // but renormalized, so the orientation stays a unit quaternion and never drifts/scales.
    public static Quat IntegrateQuat(Quat q, Vector3 omega, float dt)
    {
        Quat dq = QuatMul(new Quat(omega.X, omega.Y, omega.Z, 0f), q);
        float h = 0.5f * dt;
        return QuatNorm(new Quat(q.X + dq.X * h, q.Y + dq.Y * h, q.Z + dq.Z * h, q.W + dq.W * h));
    }

    // Physics tick (authority/solo only): the impulse-based rigid-body constraint solver. It SUBSTEPS a large
    // frame time internally (ImpulseWorld.Step: each substep <= 1/60 s, capped) so a slow render frame can't
    // make a fast fall tunnel and explode — frame-rate INDEPENDENT. This is the sole object-dynamics engine.
    private void StepPhysics(float dt)
    {
        if (!CanEdit || !_world.Physics.GravityEnabled) return;
        if (dt <= 0f) return;
        StepImpulse(dt);
    }

    // ---- impulse-solver runtime wiring (see PHYSICS.md). ----
    // Simulates dynamic SPHERES and BOXES (any collidable dynamic Object3d as its OBB) against STATIC
    // collidables (boxes + real triangle meshes) and each other; the platform is the primary support.
    // Legacy mode ("legacy", the default) never reaches this path and is unchanged.
    private ImpulseWorld? _impWorld;
    private readonly Dictionary<GameObject, RigidBody> _impBodies = new();   // persistent dynamic-sphere bodies (velocity/sleep/warm-start state survives frames)

    private void StepImpulse(float dt)
    {
        var w = _impWorld ??= new ImpulseWorld();
        w.Gravity = new Vector3(0f, -_world.Physics.GravityStrength, 0f);
        w.Restitution = _world.Physics.Restitution;
        w.Bodies.Clear();

        // Dynamic bodies (Gravity + Collides) -> persistent RigidBodies (the solver owns their transform +
        // velocity + sleep/warm-start state across frames). A sphere is a solver Sphere; any other collidable
        // Object3d is simulated as its OBB (Stage 2: dynamic meshes are boxed; real-triangle dynamic meshes
        // are Stage 5). Two-way dynamic-vs-dynamic is still inert (only dynamic-vs-static generates contacts).
        var live = new HashSet<GameObject>();
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!(inst.Gravity && inst.Collides)) continue;
            if (inst is Sphere sph)
            {
                live.Add(inst);
                if (!_impBodies.TryGetValue(inst, out var rb)) { rb = RigidBody.Sphere(sph.Position, sph.R, MathF.Max(1e-4f, inst.Mass)); _impBodies[inst] = rb; }
                rb.Radius = sph.R;
                rb.Restitution = inst.Restitution; rb.Friction = inst.Friction; rb.RollingFriction = inst.RollingFriction; rb.Tag = e;
                w.Bodies.Add(rb);
            }
            else if (inst is Object3d o)
            {
                live.Add(inst);
                Vector3 half = o.Size * (0.5f * o.Scale);
                float mass = MathF.Max(1e-4f, inst.Mass);
                if (!_impBodies.TryGetValue(inst, out var rb))
                {
                    Vector3 center = (o.LocalCenter * o.Scale).Rotate(o.LocalRotate) + o.Position;
                    rb = RigidBody.DynamicBox(center, half, QuatFromEuler(o.LocalRotate), mass);
                    _impBodies[inst] = rb;
                }
                else rb.SetBoxShape(half, mass);   // refresh shape/inertia (scale/mass may have been edited)
                rb.Restitution = inst.Restitution; rb.Friction = inst.Friction; rb.RollingFriction = inst.RollingFriction; rb.Tag = e;
                w.Bodies.Add(rb);
            }
        }
        // Prune bodies whose object was deleted / lost gravity.
        if (_impBodies.Count != live.Count)
            foreach (var k in _impBodies.Keys.Where(k => !live.Contains(k)).ToList()) _impBodies.Remove(k);

        // Static collidables -> fresh static bodies each frame (they carry no solver state). A gravity+collides
        // object is a DYNAMIC body (added above); everything else that collides is a static support.
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!inst.Collides || inst.Gravity) continue;
            if (inst is Sphere ss) w.Bodies.Add(RigidBody.StaticSphere(ss.Position, ss.R));
            else if (inst is Object3d o) w.Bodies.Add(BuildStaticMeshBody(o));
        }

        w.Step(dt);

        // Write solved transforms back to the engine instances (+ mark for network sync).
        foreach (var kv in _impBodies)
        {
            var go = kv.Key; var rb = kv.Value;
            bool moved = false;
            if (go is Sphere sph)
            {
                moved = (sph.Position - rb.Position).Length() > 1e-6f;
                sph.Position = rb.Position;
            }
            else if (go is Object3d o)
            {
                Vector3 newRot = EulerFromQuat(rb.Orientation);
                Vector3 newPos = rb.Position - (o.LocalCenter * o.Scale).Rotate(newRot);   // solver Position is the OBB centre; back out the anchor offset
                moved = (newPos - o.Position).Length() > 1e-6f || (newRot - o.LocalRotate).Length() > 1e-5f;
                o.Position = newPos;
                o.LocalRotate = newRot;
                o.UpdateGeometry();
            }
            if (moved && _online && _isServer && rb.Tag is EditEntry ee) _physMoved.Add(ee.Descriptor.Id);
        }
    }

    // Build a STATIC support body from an Object3d. It carries the world triangles (the sphere-vs-mesh contact
    // uses the REAL surface) AND a solid BOX VIEW (its world AABB, floored to a minimum downward thickness so a
    // zero-thickness platform quad is still a solid support) that the dynamic-box manifold rests on. A rotated
    // static mesh is boxed by its (loose) AABB — the real-triangle box-vs-mesh contact is Stage 5.
    private static RigidBody BuildStaticMeshBody(Object3d o)
    {
        var wv = o.WorldVertices;
        var verts = new Vector3[wv.Length];
        System.Array.Copy(wv, verts, wv.Length);
        var faces = o.Faces;
        var tris = new int[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++) { var f = faces[i]; tris[i * 3] = f.I0; tris[i * 3 + 1] = f.I1; tris[i * 3 + 2] = f.I2; }
        var body = RigidBody.StaticMesh(verts, tris);

        const float MinThick = 0.5f;
        Vector3 mn = o.WorldMin, mx = o.WorldMax;
        if (mx.Y - mn.Y < MinThick) mn.Y = mx.Y - MinThick;              // extend downward, keep the top face
        body.Position = (mn + mx) * 0.5f;
        body.HalfExtents = (mx - mn) * 0.5f;
        body.Orientation = Quat.Identity;
        body.BoxView = true;
        return body;
    }

    // Full 3D Separating-Axis-Theorem between two oriented boxes A and B (center, 3 orthonormal axes,
    // half-extents). Tests 15 axes — the 3 faces of each box + the 9 edge×edge cross products — and
    // returns overlap + the minimum-penetration UNIT normal (pointing A->B) + the penetration depth, or
    // hit=false at the first separating axis. Near-degenerate cross axes (parallel edges) are skipped;
    // the face axes already cover those. Pure + tested. The impulse box-box manifold uses this normal.
    public static (bool hit, Vector3 normal, float depth) SatBox3D(
        Vector3 cA, Vector3 ax0, Vector3 ax1, Vector3 ax2, Vector3 hA,
        Vector3 cB, Vector3 bx0, Vector3 bx1, Vector3 bx2, Vector3 hB)
    {
        Span<Vector3> axes = stackalloc Vector3[15];
        axes[0] = ax0; axes[1] = ax1; axes[2] = ax2;
        axes[3] = bx0; axes[4] = bx1; axes[5] = bx2;
        Span<Vector3> ea = stackalloc Vector3[3]; ea[0] = ax0; ea[1] = ax1; ea[2] = ax2;
        Span<Vector3> eb = stackalloc Vector3[3]; eb[0] = bx0; eb[1] = bx1; eb[2] = bx2;
        int k = 6;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                axes[k++] = Vector3.Cross(ea[i], eb[j]);

        Vector3 dC = cB - cA;
        float minOv = float.MaxValue; Vector3 best = new Vector3(0f, 1f, 0f);
        for (int a = 0; a < 15; a++)
        {
            Vector3 L = axes[a];
            float len2 = L * L;                                  // Vector3*Vector3 = dot
            if (len2 < 1e-9f) continue;                          // degenerate axis (parallel edges) — faces cover it
            Vector3 Ln = L * (1f / MathF.Sqrt(len2));
            float rA = MathF.Abs(ax0 * Ln) * hA.X + MathF.Abs(ax1 * Ln) * hA.Y + MathF.Abs(ax2 * Ln) * hA.Z;
            float rB = MathF.Abs(bx0 * Ln) * hB.X + MathF.Abs(bx1 * Ln) * hB.Y + MathF.Abs(bx2 * Ln) * hB.Z;
            float dist = dC * Ln;
            float ov = rA + rB - MathF.Abs(dist);
            if (ov <= 0f) return (false, new Vector3(0f, 1f, 0f), 0f);          // separating axis -> no contact
            if (ov < minOv) { minOv = ov; best = dist < 0f ? Ln * -1f : Ln; }   // normal points A->B
        }
        return (true, best, minOv);
    }

    // Server: every PhysicsSyncEvery frames, stream the positions of objects physics moved since the
    // last flush as one compact PhysicsSyncPacket, then clear the changed set. Only changed objects
    // travel; a resting object's final settling move is its last entry, after which it goes quiet.
    private void FlushPhysicsSync()
    {
        if (!_online || !_isServer || _netManager == null) return;
        if (++_physFrame < PhysicsSyncEvery) return;
        _physFrame = 0;
        if (_physMoved.Count == 0) return;

        var ids = new List<int>(_physMoved.Count);
        var pos = new List<Vector3>(_physMoved.Count);
        var vel = new List<Vector3>(_physMoved.Count);
        var rot = new List<Vector3>(_physMoved.Count);
        var ang = new List<Vector3>(_physMoved.Count);
        foreach (int id in _physMoved)
        {
            int idx = _editables.FindIndex(e => e.Descriptor.Id == id);
            if (idx < 0) continue;                               // deleted since it moved
            var inst = _editables[idx].Instance;
            // full velocity from the solver's RigidBody (every moved object is a solver body).
            Vector3 lin = Vector3.Zero, angv = Vector3.Zero;
            if (_impBodies.TryGetValue(inst, out var rb)) { lin = rb.LinVel; angv = rb.AngVel; }
            ids.Add(id);
            pos.Add(inst.Position);
            vel.Add(lin);
            rot.Add(inst.LocalRotate);
            ang.Add(angv);
        }
        _physMoved.Clear();
        if (ids.Count == 0) return;
        _netManager.SendPacket(new PhysicsSyncPacket
        {
            Ids = ids.ToArray(), Positions = pos.ToArray(), LinVel = vel.ToArray(), Rotations = rot.ToArray(), AngVel = ang.ToArray(),
        }, _myNetId);
    }

    // Client: a position batch arrived (main thread, via ProcessEvents). Store/refresh each object's
    // interpolation target; StepNetworkPhysics eases the live instance toward it every frame.
    private void OnPhysicsSyncReceived(PhysicsSyncPacket packet, int senderId)
    {
        for (int i = 0; i < packet.Ids.Length; i++)
        {
            int id = packet.Ids[i];
            if (!_physTargets.TryGetValue(id, out var t)) { t = new NetTarget(); _physTargets[id] = t; }
            t.Pos = packet.Positions[i];
            t.Lin = packet.LinVel[i];
            t.Rot = packet.Rotations[i];
            t.AngVel = packet.AngVel[i];
        }
    }

    // Client: ease each synced object toward its target (dead-reckoning the ongoing fall by velocity
    // between sparse batches), so falling/resting looks smooth even though the client never simulates.
    // View-only peers only; the authority moves objects directly in StepPhysics.
    private void StepNetworkPhysics(float dt)
    {
        if (CanEdit || _physTargets.Count == 0) return;
        DoNetworkInterp(dt);
    }

    private void DoNetworkInterp(float dt)
    {
        foreach (var kv in _physTargets)
        {
            int idx = _editables.FindIndex(e => e.Descriptor.Id == kv.Key);
            if (idx < 0) continue;
            var entry = _editables[idx];
            var (cur, tgt) = StepInterpolate(entry.Instance.Position, kv.Value.Pos, kv.Value.Lin, dt, PhysLerpRate);
            kv.Value.Pos = tgt;                                  // remember the dead-reckoned target

            // Dead-reckon the spin between sparse batches by the synced angular velocity (advance the target
            // pose by ω·dt — mirrors the position dead-reckon), then ease toward it (shortest-angle per axis,
            // so a ±π wrap never spins the long way). Lets peers see physics-driven spin, not just position.
            Vector3 advRot = kv.Value.Rot + kv.Value.AngVel * dt;
            kv.Value.Rot = advRot;                               // remember the dead-reckoned pose
            Vector3 lr = entry.Instance.LocalRotate;
            float f = Math.Clamp(PhysLerpRate * dt, 0f, 1f);
            Vector3 rot = new Vector3(LerpAngle(lr.X, advRot.X, f), LerpAngle(lr.Y, advRot.Y, f), LerpAngle(lr.Z, advRot.Z, f));

            bool moved = MathF.Abs(cur.X - entry.Instance.Position.X) > 1e-6f
                      || MathF.Abs(cur.Y - entry.Instance.Position.Y) > 1e-6f
                      || MathF.Abs(cur.Z - entry.Instance.Position.Z) > 1e-6f;
            bool turned = MathF.Abs(rot.X - lr.X) > 1e-6f || MathF.Abs(rot.Y - lr.Y) > 1e-6f || MathF.Abs(rot.Z - lr.Z) > 1e-6f;
            if (moved) entry.Instance.Position = cur;
            if (turned) entry.Instance.LocalRotate = rot;
            if (moved || turned)
            {
                if (entry.Instance is Object3d oo) oo.UpdateGeometry();
                SyncLightToMarker(entry);
            }
        }
    }

    // Interpolate an angle toward a target along the SHORTEST arc (wrapping ±π), fraction t in [0,1].
    public static float LerpAngle(float a, float b, float t)
    {
        float d = MathF.Atan2(MathF.Sin(b - a), MathF.Cos(b - a));   // shortest signed delta
        return a + d * t;
    }

    // Pure interpolation step for a network-synced position: first extrapolate the target forward by its
    // FULL linear velocity (dead-reckon the ongoing motion — fall/roll/tumble — between sparse batches; a
    // no-op once lin is 0, i.e. at rest), then exponentially ease the current position toward it. rate is
    // the ease speed (1/sec). Returns (new current, advanced target). Kept pure + static so physicstest covers it.
    public static (Vector3 cur, Vector3 tgt) StepInterpolate(Vector3 cur, Vector3 tgt, Vector3 lin, float dt, float rate)
    {
        tgt += lin * dt;
        float f = Math.Clamp(rate * dt, 0f, 1f);
        cur += (tgt - cur) * f;
        return (cur, tgt);
    }

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

        // Keep each spinning light's marker mesh aligned with its freshly-swept Direction: AdvanceLights
        // moved the engine Light above, so re-aim (or, for a beam fan, re-bake) the cone to match. Same
        // no-op condition as Light.Spin() (zero speed / point lights never sweep).
        foreach (var e in _editables)
            if (e.Light != null && e.Light.SpinSpeed != 0f && e.Light.Kind != LightKind.Point)
                OrientLightMarker(e);

        if (_isChatting)
        {
            HandleChatInput();
        }
        else if (_entryMode)
        {
            HandleFieldEntryInput();   // inline typed entry (name or numeric) captures all keystrokes until Enter/Esc
        }
        else
        {
            // Tab toggles the editor; camera fly stays active in edit mode.
            if (Pressed(ConsoleKey.Tab)) _editMode = !_editMode;

            float dt = GameTime.GetDeltaTime();
            HandleGameInput(dt);
            if (PlayerWalking)
            {
                StepPlayerPhysics(dt);              // gravity + ground + jump
            }
            else
            {
                if (!_flyMode) ResolveBodyCollision();      // no-gravity world: keep wall pushout; fly mode passes through
                _playerVelY = 0f; _onGround = false;       // reset so re-entering walk starts clean
            }
            StepPhysics(dt);
            SyncCameraToBody();   // camera = body + mode offset, every frame after the body settles

            if (_editMode) HandleEditorInput();

            if (Input.IsGetKey(ConsoleKey.T))
            {
                _isChatting = true;
                _currentInput = "";
                while (Console.KeyAvailable) Console.ReadKey(true);
            }
        }

        // Physics position sync: the server streams what moved (throttled), clients ease toward it.
        // Run regardless of chat state so a client keeps interpolating smoothly while typing.
        FlushPhysicsSync();                            // server: send the changed-object batch
        StepNetworkPhysics(GameTime.GetDeltaTime());   // client: dead-reckon + lerp toward targets

        // Server is the world authority: if an edit-action changed the selected object this frame,
        // stream ONE coalesced delta for it. A dirty PLATFORM pushes the whole settings packet (it has
        // no per-object delta); any other object streams a coalesced Modify.
        if (_online && _isServer && _editDirty && _selected >= 0 && _selected < _editables.Count)
        {
            var sel = _editables[_selected];
            if (string.Equals(sel.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase))
                BroadcastWorldSettings();                 // platform: push whole settings (no per-object delta)
            else { var o = FromInstance(sel.Descriptor, sel.Instance, sel.Light); _netManager?.SendPacket(new WorldEditPacket { Op = 0, Id = o.Id, ObjectJson = JsonSerializer.Serialize(o) }, _myNetId); }
        }
        _editDirty = false;

        if (!_isChatting && _netManager != null)
        {
            // Stream the BODY transform (in 1st person the body IS at the old camera position, so remote
            // avatars look exactly as before; in 3rd person peers still see your body, not your camera).
            var packet = new TransformPacket(_localBody.Position, _myCamera.LocalRotate);
            _netManager.SendPacket(packet, _myNetId);
        }

        // Drain a few paced mesh-chunk/spawn sends so a big live spawn doesn't block other packets.
        for (int i = 0; i < ChunksPerFrame && _outgoing.Count > 0; i++)
            _outgoing.Dequeue().Invoke();

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
        // Enter enters inline TYPED entry on the active EDITABLE field: the Name field takes text (as
        // before), a NUMERIC field takes a typed value (parsed + clamped on confirm). Enum/toggle fields
        // (Collider/Gravity/Collides/Kind/Shape/Texture/TexFace/TexFilter…) aren't typeable — Enter is
        // inert there (use N/M to cycle). Spawn moved off Enter to [B] so it always works. Checked first so
        // Enter takes the editable field before anything else reads it.
        if (CanEdit && _selected >= 0 && _selected < _editables.Count && Pressed(ConsoleKey.Enter))
        {
            var selFields = FieldsFor(_editables[_selected]);
            Field af = selFields[Math.Clamp(_fieldIndex, 0, selFields.Length - 1)];
            if (af == Field.Name || IsNumericField(af)) { BeginFieldEntry(_editables[_selected], af); return; }
        }

        // Cycle the spawn type.
        if (Pressed(ConsoleKey.G) && _spawnTypes.Count > 0)
            _spawnIndex = (_spawnIndex + 1) % _spawnTypes.Count;

        // Spawn the current type a couple of units in front of the camera, at ground level. Relocated off
        // Enter (now the field-edit trigger) to [B]. (A view-only client cannot mutate the synced world.)
        if (CanEdit && Pressed(ConsoleKey.B))
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

                // Delete the selected object (the platform deletes by disabling — see DeleteSelected).
                if (Pressed(ConsoleKey.Delete)) DeleteSelected();
            }
        }

        // Runtime graphics settings (shadows/BVH/camera-light/platform) — authority only, live-synced.
        if (CanEdit) HandleGraphicsToggles();

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
            case Field.ColorR: inst.Color = new Rgba32((byte)Math.Clamp(inst.Color.R + dir * ColorStep, 0, 255), inst.Color.G, inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorG: inst.Color = new Rgba32(inst.Color.R, (byte)Math.Clamp(inst.Color.G + dir * ColorStep, 0, 255), inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorB: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, (byte)Math.Clamp(inst.Color.B + dir * ColorStep, 0, 255), inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorA: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, inst.Color.B, (byte)Math.Clamp(inst.Color.A + dir * ColorStep, 0, 255)); SyncColorDerived(entry); break;
            case Field.Radius:
                if (inst is Sphere s) s.R = MathF.Max(0.01f, s.R + dir * RadiusStep);
                break;
            case Field.Collides:
                if (!_world.Physics.CollisionEnabled) break;                          // world collision off -> locked off
                inst.Collides = !inst.Collides;                                       // N or M toggles it
                if (entry.Platform != null) entry.Platform.Collides = inst.Collides;  // platform: persist on the live config
                break;
            case Field.Gravity:
                if (!_world.Physics.GravityEnabled) break;                            // world gravity off -> locked off
                inst.Gravity = !inst.Gravity;                                         // N or M toggles it
                if (!inst.Gravity) _impBodies.Remove(inst);                            // stop simulating it once it can't fall
                if (entry.Platform != null) entry.Platform.Gravity = inst.Gravity;    // platform: persist on the live config
                break;
            case Field.Collider:
                if (!_world.Physics.CollisionEnabled) break;                          // world collision off -> shape moot
                inst.Collider = inst.Collider == ColliderShape.Obb ? ColliderShape.Aabb : ColliderShape.Obb;  // N or M toggles AABB<->OBB
                break;
            case Field.Mass:
                inst.Mass = MathF.Max(MassMin, inst.Mass + dir * MassStep);
                break;
            case Field.Restitution:
                // Cycle inherit(<0) <-> explicit 0..1: from "inherit", M sets 0.0 (explicit); stepping an
                // explicit value below 0 returns to "inherit world", above 1 clamps. (Bouncy = closer to 1.)
                if (inst.Restitution < 0f) inst.Restitution = dir > 0 ? 0f : -1f;
                else { float nv = inst.Restitution + dir * RestStep; inst.Restitution = nv < 0f ? -1f : MathF.Min(1f, nv); }
                break;
            case Field.Friction:
                inst.Friction = Math.Clamp(inst.Friction + dir * FrictionStep, 0f, FrictionMax);
                break;
            case Field.RollingFriction:
                inst.RollingFriction = Math.Clamp(inst.RollingFriction + dir * RollFrictionStep, 0f, 1f);
                break;
            case Field.ColorFade:
                inst.ColorFade = Math.Clamp(inst.ColorFade + dir * ColorFadeStep, 0f, 1f);   // 0 = true colour, 1 = washed to white
                if (entry.Light != null) entry.Light.ColorFade = inst.ColorFade;             // pale the emitted colour too
                break;
            case Field.Texture:
            {
                // Cycle "none" + every PNG in textures/. Set via the decode-once cache (null = none). A live
                // swap re-uploads ONLY the texture pool (A3) — the static geometry + BVH stay cached.
                var opts = new List<string> { "" };
                opts.AddRange(WorldManager.ListAvailableTextures(_texturesFolder));
                string cur = inst.Texture?.Name ?? "";
                int idx = opts.FindIndex(t => string.Equals(t, cur, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = 0;
                int n = opts.Count;
                string next = opts[(((idx + dir) % n) + n) % n];
                inst.Texture = string.IsNullOrEmpty(next) ? null : TextureLoader.Get(_texturesFolder, next);
                InvalidateGpuTextures();
                break;
            }
            case Field.TextureScale:
                inst.TextureScale = Math.Clamp(inst.TextureScale + dir * TextureScaleStep, 0.1f, 10f);
                break;
            case Field.TextureFace:
            {
                // Cycle "All" (-1) then each face option (a cube: +X..-Z); other shapes have only "All".
                int total = TextureFaceOptions(entry.Descriptor.Type).Length + 1;
                int slot = (((inst.TextureFace + 1 + dir) % total) + total) % total;
                inst.TextureFace = slot - 1;
                break;
            }
            case Field.TextureFilter:
                // Toggle Nearest <-> Bilinear (only two modes). Rides in the per-frame SnapObject, so no
                // GPU geometry invalidation is needed (unlike a texture swap).
                inst.TextureFilter = inst.TextureFilter == TextureFilterMode.Bilinear ? TextureFilterMode.Nearest : TextureFilterMode.Bilinear;
                break;
            case Field.Power:
                if (entry.Light != null) entry.Light.LightPower = MathF.Max(0f, entry.Light.LightPower + dir * PowerStep);
                break;
            case Field.ClrInf:
                if (entry.Light != null)
                    entry.Light.ColorInfluence = Math.Clamp(entry.Light.ColorInfluence + dir * InfluenceStep, 0f, 1f);
                break;
            case Field.Kind:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<LightKind>().Length;
                    entry.Light.Kind = (LightKind)((((int)entry.Light.Kind + dir) % n + n) % n);
                    // Morph the marker mesh (cube <-> cone <-> shaped panel) to match the new kind.
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.DirX: AdjustDirection(entry, new Vector3(dir * DirStep, 0f, 0f)); break;
            case Field.DirY: AdjustDirection(entry, new Vector3(0f, dir * DirStep, 0f)); break;
            case Field.DirZ: AdjustDirection(entry, new Vector3(0f, 0f, dir * DirStep)); break;
            case Field.ConeAngle:
                if (entry.Light != null)
                {
                    entry.Light.ConeAngleDeg = Math.Clamp(entry.Light.ConeAngleDeg + dir * ConeStep, 1f, 89f);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.AreaSize:
                if (entry.Light != null)
                {
                    entry.Light.AreaSize = MathF.Max(0.05f, entry.Light.AreaSize + dir * AreaStep);
                    if (entry.Light.Kind == LightKind.Area && entry.Instance is Object3d am)
                    { am.Scale = entry.Light.AreaSize; am.UpdateGeometry(); }   // shaped marker tracks the area size
                }
                break;
            case Field.AreaShape:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<ConeShapeKind>().Length;
                    entry.Light.AreaShape = (ConeShapeKind)((((int)entry.Light.AreaShape + dir) % n + n) % n);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                        entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.Spin:
                if (entry.Light != null) entry.Light.SpinSpeed += dir * LightSpinStep;
                break;
            case Field.Beams:
                if (entry.Light != null)
                {
                    entry.Light.BeamCount = Math.Clamp(entry.Light.BeamCount + dir, 1, 8);
                    // Always rebuild: this swaps mesh TYPE both ways (1 -> baked fan, many -> single cone).
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                        entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.Shape:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<ConeShapeKind>().Length;
                    entry.Light.ConeShape = (ConeShapeKind)((((int)entry.Light.ConeShape + dir) % n + n) % n);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.PlatShape:
                if (entry.Platform != null)
                {
                    int idx = Array.IndexOf(PlatformShapes, entry.Platform.Shape?.Trim().ToLowerInvariant());
                    if (idx < 0) idx = 0;
                    int n = PlatformShapes.Length;
                    entry.Platform.Shape = PlatformShapes[(((idx + dir) % n) + n) % n];
                    RebuildFloor(entry);
                }
                break;
            case Field.PlatSize:
                if (entry.Platform != null) { entry.Platform.Size = MathF.Max(PlatformMin, entry.Platform.Size + dir * PlatformStep); RebuildFloor(entry); }
                break;
            case Field.PlatWidth:
                if (entry.Platform != null) { entry.Platform.Width = MathF.Max(PlatformMin, entry.Platform.Width + dir * PlatformStep); RebuildFloor(entry); }
                break;
            case Field.PlatDepth:
                if (entry.Platform != null) { entry.Platform.Depth = MathF.Max(PlatformMin, entry.Platform.Depth + dir * PlatformStep); RebuildFloor(entry); }
                break;
        }
    }

    // Rebuilds the live floor mesh from the (just-mutated) PlatformConfig: a shape/size change
    // swaps the geometry in place, keeping the entry selectable. Mirrors BuildPlatform's floor
    // build (a shadow caster, NOT a _models entry) — not ReplaceMarker (markers are visual-only).
    private void RebuildFloor(EditEntry entry)
    {
        if (entry.Platform == null) return;
        Object3d fresh = CreatePlatform(entry.Platform);
        fresh.Position = entry.Instance.Position;   // a shape/size change must keep the floor where it was moved to
        fresh.Color = ParseColor(entry.Platform.Color, new Rgba32(255, 255, 0));
        ApplyPhysicsFlags(fresh, entry.Platform.Collides, entry.Platform.Gravity);
        fresh.UpdateGeometry();
        if (entry.Instance is IDisplays oldDisp) RemoveDisplaysObject(oldDisp);
        AddDisplaysObject(fresh);          // default castsShadow:true, same as BuildPlatform
        entry.Instance = fresh;
        _floor = fresh;
    }

    // Nudges one light's Direction by delta and re-normalizes (kept a unit vector; falls back to
    // straight down if it collapses to zero), then re-aims the marker to match.
    private void AdjustDirection(EditEntry entry, Vector3 delta)
    {
        if (entry.Light == null) return;
        Vector3 d = entry.Light.Direction + delta;
        entry.Light.Direction = d.Length() > 1e-6f ? d.Norm() : new Vector3(0f, -1f, 0f);
        OrientLightMarker(entry);
    }

    // Re-aims a light's marker along its current Direction. A multi-beam spot bakes each beam's aim
    // into the mesh (no single LocalRotate), so a direction change must REBUILD it; every other marker
    // just updates LocalRotate. No-op for a point light (its cube stays axis-aligned).
    private void OrientLightMarker(EditEntry entry)
    {
        if (entry.Light == null || entry.Instance is not Object3d m) return;
        if (entry.Light.Kind == LightKind.Spot && entry.Light.BeamCount > 1)   // baked fan -> re-bake for new dir
        {
            ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
            return;
        }
        if (entry.Light.Kind == LightKind.Point) return;
        m.LocalRotate = DirToEuler(entry.Light.Direction); m.UpdateGeometry();
    }

    // Carries a freshly-edited instance color to its derivatives: a light emits its marker's color,
    // and a platform persists its color as hex (so save/sync pick it up without a floor rebuild).
    private static void SyncColorDerived(EditEntry entry)
    {
        if (entry.Light != null) entry.Light.Rgb = entry.Instance.Color.ToUnit();
        if (entry.Platform != null) entry.Platform.Color = ToHex(entry.Instance.Color);
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _editables.Count) return;

        // The platform "deletes" by disabling (so the removal persists on save/sync) + dropping the floor.
        var sel = _editables[_selected];
        if (string.Equals(sel.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase))
        {
            _world.Platform.Enabled = false;
            RemoveEntryAt(_selected);
            _floor = null;
            if (_online && _isServer) BroadcastWorldSettings();   // push the disable to connected clients
            return;
        }

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
        _impBodies.Remove(entry.Instance);                   // drop the solver RigidBody state
        _physMoved.Remove(entry.Descriptor.Id);              // and any pending/interp sync state
        _physTargets.Remove(entry.Descriptor.Id);
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
        Vector3 spawnPos = _localBody.Position + forward * 3f;   // in front of the BODY (same in 1st person; sensible in 3rd)
        spawnPos.Y = 0f; // ground level

        string label = _spawnTypes[_spawnIndex];

        // The platform is a SINGLE-INSTANCE object: spawn re-enables + rebuilds it (restoring its last
        // config Shape/Size/Color), or just selects the existing one. (No spawn broadcast — out of scope.)
        if (string.Equals(label, "platform", StringComparison.OrdinalIgnoreCase))
        {
            int existing = _editables.FindIndex(e =>
                string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { _selected = existing; _fieldIndex = 0; return; }   // already one — just select it
            _world.Platform.Enabled = true;
            BuildPlatform();                          // appends the platform entry + sets _floor
            if (_editables.Count > 0) { _selected = _editables.Count - 1; _fieldIndex = 0; }
            if (_online && _isServer) BroadcastWorldSettings();   // push the re-enable to connected clients
            return;
        }

        // The built-in types (primitives + light) keep their label as the Type; anything else is a
        // library mesh.
        bool isBuiltIn = label is "cube" or "sphere" or "cylinder" or "cone" or "pyramid" or "ramp" or "flatpicture" or "light";
        // A flat picture is a visual panel — non-colliding by default (a thin quad is a degenerate
        // collider); gravity already defaults off. Every other primitive keeps the default (collides).
        bool isFlatPic = string.Equals(label, "flatpicture", StringComparison.OrdinalIgnoreCase);
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
            Collides = !isFlatPic,
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

            string? objText = null;
            if (string.Equals(descriptor.Type, "mesh", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(descriptor.Mesh))
            {
                string path = Path.Combine(AppPaths.ModelsFolder, descriptor.Mesh + ".obj");
                try { objText = File.ReadAllText(path); }
                catch (Exception ex) { Logger.Error($"Spawn: failed reading mesh '{descriptor.Mesh}' at {path}", ex); }
            }

            // LARGE mesh: stream it as chunks (paced a few per frame in Update) BEFORE the spawn, so
            // real-time packets interleave; the trailing spawn carries MeshName but EMPTY text (the
            // mesh is already on disk by then). SMALL mesh (or none): inline exactly as today.
            if (objText != null && objText.Length > MeshChunkThreshold)
            {
                string name = descriptor.Mesh!;
                int total = (objText.Length + MeshChunkSize - 1) / MeshChunkSize;
                for (int i = 0; i < total; i++)
                {
                    int start = i * MeshChunkSize;
                    string data = objText.Substring(start, Math.Min(MeshChunkSize, objText.Length - start));
                    int index = i;   // capture per iteration
                    _outgoing.Enqueue(() => _netManager?.SendPacket(
                        new MeshChunkPacket { MeshName = name, Index = index, Total = total, Data = data }, _myNetId));
                }
                packet.MeshName = name;   // MeshObjText stays empty; chunks already delivered the mesh
                _outgoing.Enqueue(() => _netManager?.SendPacket(packet, _myNetId));
            }
            else
            {
                if (objText != null) { packet.MeshName = descriptor.Mesh!; packet.MeshObjText = objText; }
                _netManager?.SendPacket(packet, _myNetId);
            }
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
    private WorldConfig BuildLiveWorldConfig()
    {
        // The platform is excluded from FromInstance, so sync its live floor position here — this is
        // the SINGLE place the moved floor's position flows back into the config for save/sync.
        if (_floor != null) _world.Platform.Position = FromVec(_floor.Position);
        return new WorldConfig
        {
            Name = _world.Name,
            Graphics = _world.Graphics,
            Physics = _world.Physics,     // gravity + collision world switches persist on save/sync
            Platform = _world.Platform,   // the live platform (mutated in place) rides along for save/sync
            // EXCLUDE the platform entry: it is an extra selectable, not an Object — it must never leak
            // into the saved/synced object list (it carries through Platform above instead).
            Objects = _editables
                .Where(e => !string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase))
                .Select(e => FromInstance(e.Descriptor, e.Instance, e.Light)).ToList(),
        };
    }

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

        UI.AddText("[G] cycle type  [B] spawn  [ [ / ] ] select  [Enter] type value", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText("Move: J/L=X  I/K=Z  U/O=Y    [F5] save", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText($"[F1] {(_flyMode ? "Fly" : "Walk")}   [F7] View: {(_cameraMode == CameraMode.ThirdPerson ? "3rd-person" : "1st-person")}", new Vector2Int(x, y++), ConsoleColor.Gray);

        // Runtime graphics settings — only the authority can flip them, so only it sees the hints.
        if (CanEdit)
        {
            string g = $"[F2] Shadows:{OnOff(_world.Graphics.Shadows)}  [F3] BVH:{OnOff(_world.Graphics.Bvh)}  "
                     + $"[F4] CamLight:{OnOff(!_world.Graphics.DisableCameraLight)}  [F6] Floor:{OnOff(_world.Platform.Enabled)}";
            UI.AddText(g, new Vector2Int(x, y++), ConsoleColor.Gray);
        }

        if (_saveFlash > 0)
            UI.AddText(_saveMsg, new Vector2Int(x, y), ConsoleColor.Green);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

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

            // Read-only header: Type, stable Id, and the display Name (user name, or the system name).
            lines.Add(($"  Type: {type}", ConsoleColor.DarkGray));
            lines.Add(($"  Id: {entry.Descriptor.Id}", ConsoleColor.DarkGray));
            lines.Add(($"  Name: {DisplayName(entry)}", ConsoleColor.DarkGray));
            if (type == "mesh")
                lines.Add(($"  Mesh: {entry.Descriptor.Mesh}", ConsoleColor.DarkGray));

            // Editable fields with the active-field cursor.
            foreach (var f in fields)
            {
                bool on = f == active;
                string line = $"{(on ? ">" : " ")} {FieldLabel(f)}: {FieldValue(f, entry)}";
                lines.Add((line, on ? ConsoleColor.Cyan : ConsoleColor.Gray));
            }

            if (_entryMode)
                lines.Add(("[Enter] confirm  [Esc] cancel", ConsoleColor.Green));
            else if (active == Field.Name)
                lines.Add((",/. field   [Enter] type name   Del", ConsoleColor.DarkGray));
            else if (IsNumericField(active))
                lines.Add((",/. field   [Enter] type / N,M step   Del", ConsoleColor.DarkGray));
            else
                lines.Add((",/. field   N,M cycle   Del", ConsoleColor.DarkGray));
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
        Field.Scale => "Scale", Field.RotateSpeed => "Spin",
        Field.ColorR => "R", Field.ColorG => "G", Field.ColorB => "B", Field.ColorA => "A",
        Field.Radius => "Radius", Field.Power => "Power", Field.ClrInf => "Influence",
        Field.Kind => "Kind", Field.DirX => "Dir X", Field.DirY => "Dir Y", Field.DirZ => "Dir Z",
        Field.ConeAngle => "Cone", Field.AreaSize => "Size", Field.AreaShape => "Shape", Field.Spin => "Spin",
        Field.Beams => "Beams", Field.Shape => "Shape",
        Field.PlatShape => "Shape", Field.PlatSize => "Size",
        Field.PlatWidth => "Width", Field.PlatDepth => "Depth",
        Field.Collides => "Collide",
        Field.Gravity => "Gravity",
        Field.Collider => "Collider",
        Field.Mass => "Mass",
        Field.Restitution => "Bounce",
        Field.Friction => "Friction",
        Field.RollingFriction => "RollFric",
        Field.ColorFade => "Pale",
        Field.Texture => "Texture",
        Field.TextureScale => "TexScale",
        Field.TextureFace => "TexFace",
        Field.TextureFilter => "TexFilter",
        Field.Name => "Name",
        _ => f.ToString()
    };

    private string FieldValue(Field f, EditEntry entry)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        // While typing into THIS field on the selected entry, show the live buffer with a cursor (Name or
        // numeric alike) — the single display point for inline entry, mirroring the old rename display.
        if (_entryMode && _entryField == f && _selected >= 0 && _selected < _editables.Count && _editables[_selected] == entry)
            return $"{_entryBuffer}_";
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
            Field.ColorR => inst.Color.R.ToString(),
            Field.ColorG => inst.Color.G.ToString(),
            Field.ColorB => inst.Color.B.ToString(),
            Field.ColorA => inst.Color.A.ToString(),
            Field.Radius => (inst as Sphere)?.R.ToString("F2") ?? "-",
            Field.Power => (entry.Light?.LightPower ?? entry.Descriptor.Power).ToString("F0"),
            Field.ClrInf => (entry.Light?.ColorInfluence ?? 0f).ToString("F2"),
            Field.Kind => entry.Light != null ? LightKindToString(entry.Light.Kind) : "-",
            Field.DirX => (entry.Light?.Direction.X ?? 0f).ToString("F2"),
            Field.DirY => (entry.Light?.Direction.Y ?? 0f).ToString("F2"),
            Field.DirZ => (entry.Light?.Direction.Z ?? 0f).ToString("F2"),
            Field.ConeAngle => (entry.Light?.ConeAngleDeg ?? 0f).ToString("F0"),
            Field.AreaSize => (entry.Light?.AreaSize ?? 0f).ToString("F2"),
            Field.AreaShape => entry.Light != null ? ConeShapeToString(entry.Light.AreaShape) : "-",
            Field.Spin => (entry.Light?.SpinSpeed ?? 0f).ToString("F2"),
            Field.Beams => (entry.Light?.BeamCount ?? 1).ToString(),
            Field.Shape => entry.Light != null ? ConeShapeToString(entry.Light.ConeShape) : "-",
            Field.PlatShape => entry.Platform?.Shape ?? "-",
            Field.PlatSize => entry.Platform?.Size.ToString("F1") ?? "-",
            Field.PlatWidth => entry.Platform?.Width.ToString("F1") ?? "-",
            Field.PlatDepth => entry.Platform?.Depth.ToString("F1") ?? "-",
            // Collide/Gravity show their effective state; "locked" makes clear the world switch
            // forces it off (you can't enable it until the world's master switch is on).
            Field.Collides => !_world.Physics.CollisionEnabled ? "Off (locked)" : (inst.Collides ? "On" : "Off"),
            Field.Gravity  => !_world.Physics.GravityEnabled   ? "Off (locked)" : (inst.Gravity  ? "On" : "Off"),
            Field.Collider => !_world.Physics.CollisionEnabled ? "AABB (locked)" : (inst.Collider == ColliderShape.Obb ? "OBB" : "AABB"),
            Field.Mass => inst.Mass.ToString("F2"),
            // <0 means "inherit world default" — show it as world (effective value) so it's never blank.
            Field.Restitution => inst.Restitution < 0f ? $"world ({RestitutionOf(inst):F2})" : inst.Restitution.ToString("F2"),
            Field.Friction => inst.Friction.ToString("F2"),
            Field.RollingFriction => inst.RollingFriction.ToString("F2"),
            Field.ColorFade => inst.ColorFade.ToString("F2"),
            Field.Texture => string.IsNullOrEmpty(inst.Texture?.Name) ? "none" : inst.Texture!.Name,
            Field.TextureScale => inst.TextureScale.ToString("F2"),
            Field.TextureFace => TextureFaceLabel(entry.Descriptor.Type, inst.TextureFace),
            Field.TextureFilter => inst.TextureFilter == TextureFilterMode.Bilinear ? "Bilinear" : "Nearest",
            Field.Name => DisplayName(entry),   // live typing handled by the buffer check above
            _ => ""
        };
    }

    // Editor display for a TextureFace value: "All" for -1, else the type's face option name (a cube:
    // +X..-Z), or the raw index as a fallback for an out-of-range value from hand-edited JSON.
    private static string TextureFaceLabel(string? type, int face)
    {
        if (face < 0) return "All";
        string[] opts = TextureFaceOptions(type);
        return face < opts.Length ? opts[face] : face.ToString();
    }

    // Inspection/test accessor: the live editable entries (each pairs a descriptor with its
    // instance, plus a Light for "light" objects). Read-only.
    public IReadOnlyList<EditEntry> EditableEntries => _editables;

    // ---- B-direct-input test hooks (headless typed entry) ----
    // Drives the SAME BeginFieldEntry/TryAppendEntryChar/Confirm|Cancel path the keyboard uses. Types
    // `typed` into the field named `fieldName` (the Field enum member, e.g. "Scale"/"PosX"/"Mass"/"Name"/
    // "Collider") on editable entry `entryIndex`, then confirms (Enter) or cancels (Esc). Returns true if
    // the field is typeable (Name or numeric) and entry mode ran; false for an enum/toggle field or a bad
    // index/field/typo — so a test can assert enum fields reject typing.
    public bool TypeFieldForTest(int entryIndex, string fieldName, string typed, bool confirm)
    {
        if (entryIndex < 0 || entryIndex >= _editables.Count) return false;
        if (!Enum.TryParse<Field>(fieldName, out var f)) return false;
        var entry = _editables[entryIndex];
        int fi = Array.IndexOf(FieldsFor(entry), f);
        if (fi < 0) return false;                                   // field not on this entry's panel
        _selected = entryIndex; _fieldIndex = fi;
        if (f != Field.Name && !IsNumericField(f)) return false;    // enum/toggle: not typeable
        BeginFieldEntry(entry, f);
        foreach (char c in typed) TryAppendEntryChar(c);            // exercise the real per-key filter
        if (confirm) ConfirmFieldEntry(); else CancelFieldEntry();
        return true;
    }

    // N/M steps a field (dir -1/+1) headlessly — used to assert enum/toggle fields still cycle. No-op for a
    // bad index/field name.
    public void StepFieldForTest(int entryIndex, string fieldName, int dir)
    {
        if (entryIndex < 0 || entryIndex >= _editables.Count) return;
        if (!Enum.TryParse<Field>(fieldName, out var f)) return;
        AdjustField(f, _editables[entryIndex], dir);
    }

    // The string the panel shows for a field (so a test can read back the enum/toggle display). No-op-safe.
    public string FieldValueForTest(int entryIndex, string fieldName)
    {
        if (entryIndex < 0 || entryIndex >= _editables.Count) return "";
        if (!Enum.TryParse<Field>(fieldName, out var f)) return "";
        return FieldValue(f, _editables[entryIndex]);
    }

    // Spawns the current type headlessly — the action the relocated [B] key triggers. Returns the new
    // editable count so a test can assert the spawn worked.
    public int SpawnForTest() { SpawnCurrent(); return _editables.Count; }

    // Inspection/test accessors for the local player body (B-camera): its stable id + system name, whether
    // it leaked into the editables list (it must NOT — it's the player, not world content), and its current
    // world position + the derived camera position.
    public int LocalBodyId => _localBodyId;
    public string LocalBodyName => $"player #{_localBodyId}";
    public bool LocalBodyInEditables => _editables.Any(e => ReferenceEquals(e.Instance, _localBody));
    public Vector3 LocalBodyPosition => _localBody.Position;
    public Vector3 CameraPosition => _myCamera.Position;
    public CameraMode CurrentCameraMode => _cameraMode;

    // Test hook: advance the authority physics one step (so a headless test can verify stability
    // without the interactive render loop). Same call the Update loop makes.
    public void StepPhysicsForTest(float dt) => StepPhysics(dt);

    // ---- Stage-7a multiplayer-sync test hooks (headless) ----
    // Snapshot the CURRENT dynamic-body state (pos + Euler orientation + full linVel + angVel) into a
    // PhysicsSyncPacket exactly as FlushPhysicsSync gathers it — the authority side of the sync round-trip.
    public NetworkPackets.PhysicsSyncPacket SnapshotPhysicsSyncForTest()
    {
        var ids = new List<int>(); var pos = new List<Vector3>(); var lin = new List<Vector3>(); var rot = new List<Vector3>(); var ang = new List<Vector3>();
        foreach (var e in _editables)
        {
            var inst = e.Instance;
            if (!(inst.Gravity && inst.Collides)) continue;
            Vector3 lv = Vector3.Zero, av = Vector3.Zero;
            if (_impBodies.TryGetValue(inst, out var rb)) { lv = rb.LinVel; av = rb.AngVel; }
            ids.Add(e.Descriptor.Id); pos.Add(inst.Position); lin.Add(lv); rot.Add(inst.LocalRotate); ang.Add(av);
        }
        return new NetworkPackets.PhysicsSyncPacket { Ids = ids.ToArray(), Positions = pos.ToArray(), LinVel = lin.ToArray(), Rotations = rot.ToArray(), AngVel = ang.ToArray() };
    }
    // Client side: adopt a received batch as the interpolation target, then advance the dead-reckon/ease
    // (same code the real client runs, minus the CanEdit gate).
    public void ReceivePhysicsSyncForTest(NetworkPackets.PhysicsSyncPacket p) => OnPhysicsSyncReceived(p, 0);
    public void StepNetworkPhysicsForTest(float dt) => DoNetworkInterp(dt);

    // Inspection/test accessor: the world config built from the CURRENT live instances (the same
    // projection SaveWorld/OnWorldRequested use), WITHOUT writing to disk.
    public WorldConfig LiveWorldSnapshot() => BuildLiveWorldConfig();
    
    
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

        // WASD moves the BODY (the physical anchor); the camera derives from it in SyncCameraToBody.
        if (Input.IsGetKey(ConsoleKey.W)) _localBody.Position += forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.S)) _localBody.Position -= forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.D)) _localBody.Position += right * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.A)) _localBody.Position -= right * moveSpeed * dt;
        // F1 toggles free-fly / walk. Vertical Space/C only fly when NOT walking; in walk mode Space
        // jumps (handled in StepPlayerPhysics, which reads it edge-triggered).
        if (Pressed(ConsoleKey.F1)) _flyMode = !_flyMode;
        // F7 toggles the camera rig 1st <-> 3rd person (a purely local view choice — works on every peer).
        if (Pressed(ConsoleKey.F7))
        {
            _cameraMode = _cameraMode == CameraMode.ThirdPerson ? CameraMode.FirstPerson : CameraMode.ThirdPerson;
            ApplyCameraMode();
        }
        if (!PlayerWalking)
        {
            if (Input.IsGetKey(ConsoleKey.Spacebar)) _localBody.Position.Y += moveSpeed * dt;
            if (Input.IsGetKey(ConsoleKey.C))        _localBody.Position.Y -= moveSpeed * dt;
        }

        if (Input.IsGetKey(ConsoleKey.OemPlus)) _mainLight.LightPower += moveSpeed * 50 * dt;
        if (Input.IsGetKey(ConsoleKey.OemMinus)) _mainLight.LightPower -= moveSpeed* 50  * dt;

        // Detail level: tap P to cycle render resolution 1->2->3->4->1 (lower = fewer rays = faster).
        if (Pressed(ConsoleKey.P)) RenderScale = RenderScale % 4 + 1;
    }

    private void OnTransformReceived(TransformPacket packet, int senderId)
    {
        // Server is a hub: re-broadcast a peer's transform to all clients (the original sender ignores
        // its own echo via senderId==_myNetId), and remember which connection this netId came in on so
        // a disconnect can map it back.
        if (_isServer && _netManager != null)
        {
            _netManager.SendPacket(packet, senderId);
            _connToNet[_netManager.LastSenderConnId] = senderId;
        }

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
        Object3d newPlayerCube = CreatePlayerAvatar(netId);   // same avatar shape/colour as the local body
        _remotePlayers.Add(netId, newPlayerCube);
        AddDisplaysObject(newPlayerCube);
    }

    // Drops a peer's avatar (server on disconnect; client on a PlayerLeft notice). No-op if unknown.
    private void RemoveRemotePlayer(int netId)
    {
        if (!_remotePlayers.TryGetValue(netId, out var cube)) return;
        RemoveDisplaysObject(cube);
        _remotePlayers.Remove(netId);
    }

    // Server, main thread (via ProcessEvents): a connection dropped — remove its avatar and tell the
    // other clients to drop it too.
    private void OnClientDisconnectedServer(int connId)
    {
        if (!_connToNet.TryGetValue(connId, out int netId)) return;
        _connToNet.Remove(connId);
        RemoveRemotePlayer(netId);
        _netManager?.SendPacket(new PlayerLeftPacket { NetId = netId }, _myNetId);
    }

    // Client (and harmless on a server): a peer left — drop its avatar.
    private void OnPlayerLeft(PlayerLeftPacket packet, int senderId)
    {
        RemoveRemotePlayer(packet.NetId);
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
    
    // ---- Object identity: id + name (B-identity) ----

    // The user-given name for an entry: the platform's rides on its live PlatformConfig; every other
    // object's on its descriptor. "" means "no user name — show the derived system name".
    private static string UserName(EditEntry e) => (e.Platform != null ? e.Platform.Name : e.Descriptor.Name) ?? "";

    private static void SetUserName(EditEntry e, string name)
    {
        if (e.Platform != null) e.Platform.Name = name;   // platform saves via _world.Platform
        e.Descriptor.Name = name;                         // descriptor rides FromInstance for every other object
    }

    // Derived, never stored: "{type} #{id}" (e.g. "cube #3", "light #5", "platform #0").
    public static string SystemName(EditEntry e) => $"{e.Descriptor.Type} #{e.Descriptor.Id}";

    // What the panel shows: the user name, or the system name when the user hasn't named it.
    public static string DisplayName(EditEntry e)
    {
        string n = UserName(e);
        return string.IsNullOrWhiteSpace(n) ? SystemName(e) : n;
    }

    // Whether a field takes a TYPED numeric value (Enter → parse + clamp). Everything else is either the
    // Name (its own text path) or an enum/toggle that only makes sense to cycle with N/M.
    private static bool IsNumericField(Field f) => f switch
    {
        Field.PosX or Field.PosY or Field.PosZ or
        Field.RotX or Field.RotY or Field.RotZ or
        Field.Scale or Field.RotateSpeed or
        Field.ColorR or Field.ColorG or Field.ColorB or Field.ColorA or
        Field.Radius or Field.Power or Field.ClrInf or
        Field.DirX or Field.DirY or Field.DirZ or
        Field.ConeAngle or Field.AreaSize or Field.Spin or Field.Beams or
        Field.PlatSize or Field.PlatWidth or Field.PlatDepth or
        Field.Mass or Field.Restitution or Field.Friction or Field.RollingFriction or
        Field.ColorFade or Field.TextureScale => true,
        _ => false,   // Name (text), Kind/AreaShape/Shape/PlatShape/Texture/TexFace/TexFilter/Collides/Gravity/Collider (cycle/toggle)
    };

    // Enters inline typed-entry for field `f` on entry `e`: seed the buffer (the Name field with its
    // current name, so it can be edited; a numeric field EMPTY, for a clean exact value) and drain any
    // buffered keys (incl. the Enter that opened it) so they don't leak into the buffer.
    private void BeginFieldEntry(EditEntry e, Field f)
    {
        _entryMode = true;
        _entryField = f;
        _entryBuffer = f == Field.Name ? UserName(e) : "";
        // Drain the trigger key (and any buffered keys) so they don't leak into the buffer. Guard against a
        // redirected/absent console (headless self-tests) where Console.KeyAvailable throws.
        while (!Console.IsInputRedirected && Console.KeyAvailable) Console.ReadKey(true);
    }

    private void CancelFieldEntry() { _entryMode = false; _entryBuffer = ""; }

    // Appends one keystroke to the entry buffer under the active field's rules: the Name field takes
    // name-safe chars (letters/digits/space/-/_, capped 32); a numeric field takes digits plus a single
    // leading '-' and a single '.', capped 16. Returns whether the char was accepted.
    private bool TryAppendEntryChar(char c)
    {
        if (_entryField == Field.Name)
        {
            if ((char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_') && _entryBuffer.Length < 32)
            { _entryBuffer += c; return true; }
            return false;
        }
        bool numOk = char.IsDigit(c)
                     || (c == '-' && _entryBuffer.Length == 0)     // leading sign only
                     || (c == '.' && !_entryBuffer.Contains('.')); // single decimal point
        if (numOk && _entryBuffer.Length < 16) { _entryBuffer += c; return true; }
        return false;
    }

    // Confirms the inline entry (the Enter path): the Name writes as text; a numeric buffer is PARSED and
    // routed through SetNumericField (which clamps to the field's range). An empty/invalid/NaN buffer
    // leaves the value UNCHANGED (a safe cancel), never throwing.
    private void ConfirmFieldEntry()
    {
        if (_selected >= 0 && _selected < _editables.Count)
        {
            var entry = _editables[_selected];
            if (_entryField == Field.Name)
            {
                SetUserName(entry, _entryBuffer.Trim());
                _editDirty = true;   // stream/save the rename like any other edit
            }
            else if (float.TryParse(_entryBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                     && !float.IsNaN(v) && !float.IsInfinity(v))
            {
                SetNumericField(_entryField, entry, v);   // clamps to range + side effects
                _editDirty = true;
            }
            // else: empty/invalid -> leave the value unchanged (cancel), never crash
        }
        _entryMode = false; _entryBuffer = "";
    }

    // Text entry for the inline field buffer (mirrors HandleChatInput): Enter confirms, Esc cancels,
    // Backspace deletes, and the per-field filter (TryAppendEntryChar) gates accepted chars.
    private void HandleFieldEntryInput()
    {
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Enter) ConfirmFieldEntry();
            else if (keyInfo.Key == ConsoleKey.Escape) CancelFieldEntry();   // cancel: leave the value unchanged
            else if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (_entryBuffer.Length > 0) _entryBuffer = _entryBuffer.Substring(0, _entryBuffer.Length - 1);
            }
            else TryAppendEntryChar(keyInfo.KeyChar);   // the per-field filter accepts/rejects (Name allows space; numeric doesn't)
        }
    }

    // Sets a NUMERIC field to an absolute value, clamped to the SAME range N/M uses and with the SAME side
    // effects (geometry/marker/derived-colour updates) — keep this in lockstep with AdjustField's clamps.
    // Non-numeric fields (Name/enums/toggles) are never routed here.
    private void SetNumericField(Field f, EditEntry entry, float v)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        switch (f)
        {
            case Field.PosX: inst.Position.X = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosY: inst.Position.Y = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosZ: inst.Position.Z = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.RotX: inst.LocalRotate.X = v; o?.UpdateGeometry(); break;
            case Field.RotY: inst.LocalRotate.Y = v; o?.UpdateGeometry(); break;
            case Field.RotZ: inst.LocalRotate.Z = v; o?.UpdateGeometry(); break;
            case Field.Scale:
                if (o != null) { o.Scale = MathF.Max(0.01f, v); o.UpdateGeometry(); }
                break;
            case Field.RotateSpeed:
                if (o != null) o.RotateSpeed = v;
                break;
            case Field.ColorR: inst.Color = new Rgba32((byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.G, inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorG: inst.Color = new Rgba32(inst.Color.R, (byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorB: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, (byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorA: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, inst.Color.B, (byte)Math.Clamp((int)MathF.Round(v), 0, 255)); SyncColorDerived(entry); break;
            case Field.Radius:
                if (inst is Sphere s) s.R = MathF.Max(0.01f, v);
                break;
            case Field.Mass:
                inst.Mass = MathF.Max(MassMin, v);
                break;
            case Field.Restitution:
                // Same semantics as N/M: a negative typed value returns to "inherit world"; else clamp 0..1.
                inst.Restitution = v < 0f ? -1f : MathF.Min(1f, v);
                break;
            case Field.Friction:
                inst.Friction = Math.Clamp(v, 0f, FrictionMax);
                break;
            case Field.RollingFriction:
                inst.RollingFriction = Math.Clamp(v, 0f, 1f);
                break;
            case Field.ColorFade:
                inst.ColorFade = Math.Clamp(v, 0f, 1f);
                if (entry.Light != null) entry.Light.ColorFade = inst.ColorFade;   // pale the emitted colour too
                break;
            case Field.TextureScale:
                inst.TextureScale = Math.Clamp(v, 0.1f, 10f);
                break;
            case Field.Power:
                if (entry.Light != null) entry.Light.LightPower = MathF.Max(0f, v);
                break;
            case Field.ClrInf:
                if (entry.Light != null) entry.Light.ColorInfluence = Math.Clamp(v, 0f, 1f);
                break;
            case Field.DirX: SetDirectionComponent(entry, 0, v); break;
            case Field.DirY: SetDirectionComponent(entry, 1, v); break;
            case Field.DirZ: SetDirectionComponent(entry, 2, v); break;
            case Field.ConeAngle:
                if (entry.Light != null) { entry.Light.ConeAngleDeg = Math.Clamp(v, 1f, 89f); RebuildLightMarker(entry); }
                break;
            case Field.AreaSize:
                if (entry.Light != null)
                {
                    entry.Light.AreaSize = MathF.Max(0.05f, v);
                    if (entry.Light.Kind == LightKind.Area && entry.Instance is Object3d am)
                    { am.Scale = entry.Light.AreaSize; am.UpdateGeometry(); }   // shaped marker tracks the area size
                }
                break;
            case Field.Spin:
                if (entry.Light != null) entry.Light.SpinSpeed = v;
                break;
            case Field.Beams:
                if (entry.Light != null) { entry.Light.BeamCount = Math.Clamp((int)MathF.Round(v), 1, 8); RebuildLightMarker(entry); }
                break;
            case Field.PlatSize:
                if (entry.Platform != null) { entry.Platform.Size = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
            case Field.PlatWidth:
                if (entry.Platform != null) { entry.Platform.Width = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
            case Field.PlatDepth:
                if (entry.Platform != null) { entry.Platform.Depth = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
        }
    }

    // Sets one direction component (0=X,1=Y,2=Z) to an absolute value, reusing AdjustDirection's
    // renormalize + marker re-aim by feeding it the delta from the current component.
    private void SetDirectionComponent(EditEntry entry, int axis, float v)
    {
        if (entry.Light == null) return;
        Vector3 d = entry.Light.Direction;
        float cur = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
        Vector3 delta = axis == 0 ? new Vector3(v - cur, 0f, 0f)
                       : axis == 1 ? new Vector3(0f, v - cur, 0f)
                                   : new Vector3(0f, 0f, v - cur);
        AdjustDirection(entry, delta);
    }

    // Rebuilds a light's marker mesh from its current fields (cone shape/angle/beam-fan/area). Shared by
    // the typed setters that change cone geometry, mirroring AdjustField's inline ReplaceMarker calls.
    private void RebuildLightMarker(EditEntry entry)
    {
        if (entry.Light == null) return;
        ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
            entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
    }

    private void OnChatReceived(ChatPacket packet, int senderId)
    {
        // Server is a hub: re-broadcast to all clients (the original sender ignores its own echo below).
        if (_isServer && _netManager != null) _netManager.SendPacket(packet, senderId);
        if (senderId == _myNetId) return;   // our own relayed echo — already shown locally when sent
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
        var sync = WorldSync.Pack(live, AppPaths.ModelsFolder, AppPaths.TexturesFolder);
        int connId = _netManager?.LastSenderConnId ?? -1;

        // Stream every LARGE referenced texture (too big to inline) as chunks to THIS requester FIRST, so
        // that — TCP being reliable + ordered — the peer materializes them to disk before the world packet
        // below triggers its build. Small textures already ride inline in `sync`.
        int chunkTex = 0, chunkParts = 0;
        foreach (var kv in WorldSync.ReadLargeTextures(live, AppPaths.TexturesFolder))
        {
            byte[] bytes = kv.Value;
            int total = (bytes.Length + MeshChunkSize - 1) / MeshChunkSize;
            for (int i = 0; i < total; i++)
            {
                int start = i * MeshChunkSize;
                int len = Math.Min(MeshChunkSize, bytes.Length - start);
                byte[] slice = new byte[len];
                Array.Copy(bytes, start, slice, 0, len);
                _netManager?.SendPacketTo(connId,
                    new TextureChunkPacket { TextureName = kv.Key, Index = i, Total = total, Data = slice }, _myNetId);
                chunkParts++;
            }
            chunkTex++;
        }

        // Answer ONLY the requester — broadcasting would rebuild every already-connected client's world.
        _netManager?.SendPacketTo(connId, sync, _myNetId);
        Logger.Info($"Sent world '{live.Name}' to client {senderId} ({sync.MeshTexts.Count} mesh file(s), {sync.TextureData.Count} inline texture(s), {chunkTex} streamed texture(s) in {chunkParts} chunk(s)).");
    }

    // Client side: the world arrived. Runs on the main thread (via ProcessEvents in Update),
    // so it is safe to rebuild the scene here — Update fully precedes the render each frame.
    private void OnWorldSyncReceived(WorldSyncPacket packet, int senderId)
    {
        var (world, meshTexts, textureData) = WorldSync.Unpack(packet);
        ApplyReceivedWorld(world, meshTexts, textureData);
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
            ReplaceMarker(_editables[idx], BuildLightMarker(light.Kind, light.Direction, light.AreaSize, light.ConeShape, light.ConeAngleDeg, light.AreaShape, light.BeamCount));
        }
    }

    // Server: push the live PlatformConfig (+ GraphicsConfig) to connected clients. Called on every
    // platform change (move/shape/size/color via the dirty-broadcast block, plus delete/spawn).
    // Applies the world's GraphicsConfig to the live engine state (shadows, BVH, camera light).
    // Idempotent — safe to call after any change to _world.Graphics, whether from a local runtime
    // toggle or a received settings/world packet. (ExtraLight is a real object, reconciled elsewhere.)
    private void ApplyGraphicsSettings()
    {
        EnableShadows = _world.Graphics.Shadows;
        Object3d.UseBvh = _world.Graphics.Bvh;

        bool wantOwn = !_world.Graphics.DisableCameraLight;
        if (wantOwn && !_ownLightEnabled) AddLight(_mainLight);
        if (!wantOwn && _ownLightEnabled) RemoveLight(_mainLight);
        _ownLightEnabled = wantOwn;
    }

    // Authority-only runtime graphics toggles (edit mode): flip a GraphicsConfig field, apply it
    // live, and — on the server — push the whole settings delta so connected clients follow.
    private void HandleGraphicsToggles()
    {
        bool changed = false;
        if (Pressed(ConsoleKey.F2)) { _world.Graphics.Shadows = !_world.Graphics.Shadows; changed = true; }
        if (Pressed(ConsoleKey.F3)) { _world.Graphics.Bvh = !_world.Graphics.Bvh; changed = true; }
        if (Pressed(ConsoleKey.F4)) { _world.Graphics.DisableCameraLight = !_world.Graphics.DisableCameraLight; changed = true; }

        if (changed)
        {
            ApplyGraphicsSettings();
            if (_online && _isServer) BroadcastWorldSettings();   // push the delta to clients
        }

        // Platform on/off is a settings field too (mirrors the spawn/delete-on-platform paths).
        if (Pressed(ConsoleKey.F6)) TogglePlatform();
    }

    // Flips the floor between built and removed, persisting the choice on _world.Platform.Enabled.
    // Mirrors SpawnCurrent (re-enable + BuildPlatform) and DeleteSelected (disable + drop floor).
    private void TogglePlatform()
    {
        _world.Platform.Enabled = !_world.Platform.Enabled;
        if (_world.Platform.Enabled)
        {
            BuildPlatform();   // appends the platform entry + sets _floor
        }
        else
        {
            int idx = _editables.FindIndex(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) RemoveEntryAt(idx);
            _floor = null;
        }
        if (_online && _isServer) BroadcastWorldSettings();   // push the change to clients
    }

    private void BroadcastWorldSettings()
    {
        if (_floor != null) _world.Platform.Position = FromVec(_floor.Position);   // capture live floor pos (as BuildLiveWorldConfig does)
        _netManager?.SendPacket(new WorldSettingsPacket
        {
            PlatformJson = JsonSerializer.Serialize(_world.Platform),
            GraphicsJson = JsonSerializer.Serialize(_world.Graphics),
        }, _myNetId);
    }

    // Client: apply a server settings delta. Runs on the main thread (via ProcessEvents), so rebuilding
    // the platform here is safe.
    private void OnWorldSettingsReceived(WorldSettingsPacket packet, int senderId)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _world.Platform = JsonSerializer.Deserialize<PlatformConfig>(packet.PlatformJson, opts) ?? _world.Platform;
            _world.Graphics = JsonSerializer.Deserialize<GraphicsConfig>(packet.GraphicsJson, opts) ?? _world.Graphics;
        }
        catch (Exception ex) { Logger.Error("WorldSettings: bad JSON", ex); return; }

        ApplyGraphicsSettings();   // shadows + BVH + camera light, idempotent

        // rebuild the platform from the new config (drop the old floor+entry, rebuild if Enabled)
        int idx = _editables.FindIndex(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) RemoveEntryAt(idx);
        _floor = null;
        BuildPlatform();   // builds floor + entry if Enabled; no-op if disabled
    }

    // Client: buffer a streamed mesh chunk; once all Total parts are present, concat + write the mesh
    // to disk. The trailing spawn WorldEditPacket (Op 1, empty MeshObjText) then builds it from disk.
    private void OnMeshChunkReceived(MeshChunkPacket packet, int senderId)
    {
        if (!_meshChunks.TryGetValue(packet.MeshName, out var buf) || buf.parts.Length != packet.Total)
        {
            buf = (packet.Total, new string[packet.Total]);
            _meshChunks[packet.MeshName] = buf;
        }
        if (packet.Index < 0 || packet.Index >= buf.total) return;   // out-of-range guard
        buf.parts[packet.Index] = packet.Data;

        if (buf.parts.Any(p => p == null)) return;   // still waiting on parts
        MaterializeMesh(packet.MeshName, string.Concat(buf.parts));
        _meshChunks.Remove(packet.MeshName);
    }

    // Client: a slice of a streamed texture arrived — buffer by Index; once all parts are present,
    // concatenate the bytes and write the .png to received/textures/. Mirrors OnMeshChunkReceived, but
    // for a BINARY payload. Ordered before the WorldSyncPacket by TCP, so the file is on disk in time.
    private void OnTextureChunkReceived(TextureChunkPacket packet, int senderId)
    {
        if (!_textureChunks.TryGetValue(packet.TextureName, out var buf) || buf.parts.Length != packet.Total)
        {
            buf = (packet.Total, new byte[packet.Total][]);
            _textureChunks[packet.TextureName] = buf;
        }
        if (packet.Index < 0 || packet.Index >= buf.total) return;   // out-of-range guard
        buf.parts[packet.Index] = packet.Data;

        if (buf.parts.Any(p => p == null)) return;   // still waiting on parts
        int totalLen = buf.parts.Sum(p => p.Length);
        byte[] full = new byte[totalLen];
        int off = 0;
        foreach (var part in buf.parts) { Array.Copy(part, 0, full, off, part.Length); off += part.Length; }
        MaterializeTexture(packet.TextureName, full);
        _textureChunks.Remove(packet.TextureName);
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

    // Client: one reassembled/inline texture arrives — write its raw PNG bytes to received/textures/<name>
    // (idempotent overwrite), where the loader decodes it exactly like a local file. Mirrors MaterializeMesh.
    private static void MaterializeTexture(string name, byte[] pngBytes)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ReceivedTexturesFolder);
            File.WriteAllBytes(Path.Combine(AppPaths.ReceivedTexturesFolder, name), pngBytes);
        }
        catch (Exception ex) { Logger.Error($"Failed writing received texture '{name}'", ex); }
    }

    private void ApplyReceivedWorld(WorldConfig world, IReadOnlyDictionary<string, string> meshTexts,
                                    IReadOnlyDictionary<string, byte[]> textureData)
    {
        // 1) Materialize the received meshes + inline textures into dedicated folders (never the user's
        //    models/ or textures/). Large textures already streamed via chunks (materialized on arrival,
        //    which — TCP being ordered — precedes this packet, so they are on disk before the build below).
        foreach (var kv in meshTexts)
            MaterializeMesh(kv.Key, kv.Value);
        foreach (var kv in textureData)
            MaterializeTexture(kv.Key, kv.Value);
        _modelsFolder = AppPaths.ReceivedFolder;
        _texturesFolder = AppPaths.ReceivedTexturesFolder;

        // 2) Tear down the current world's objects + platform (camera stays; lights reconciled below).
        foreach (var e in _editables)
        {
            if (e.Instance is IDisplays disp) RemoveDisplaysObject(disp);
            if (e.Light != null) RemoveLight(e.Light);   // drop a light object's engine Light too
        }
        _editables.Clear();
        _models.Clear();
        _impBodies.Clear(); _physMoved.Clear(); _physTargets.Clear();   // drop stale physics/sync state
        _selected = -1;
        _floor = null;   // the floor is an editable entry now; the loop above already removed its display

        // 3) Build the received world (BuildWorldObject loads meshes from _modelsFolder == received/).
        _world = world;
        // Adopt the server's ids verbatim (do NOT reassign); future local spawns (5b) start past them.
        _nextObjectId = _world.Objects.Count == 0 ? PlatformId + 1 : _world.Objects.Max(o => o.Id) + 1;   // never hand out id 0 (platform)
        BuildPlatform();
        foreach (var o in _world.Objects)
            BuildWorldObject(o);

        // 4) Apply its graphics and reconcile the lights against the placeholder's.
        ApplyGraphicsSettings();   // shadows + BVH + camera light, idempotent

        // The settings extra light is now a normal "light" object inside the received config and was
        // already built in step 3 — no separate reconcile needed (the camera light above stays special).

        // 5) Done — drop the waiting overlay (replace on a repeat send).
        _awaitingWorld = false;
        _worldReceived = true;
        Logger.Info($"Applied received world '{_world.Name}': {_world.Objects.Count} objects, {meshTexts.Count} mesh file(s), {textureData.Count} inline texture(s) (+ any streamed to received/textures).");
    }

    public static Object3d CreateCube()
    {
        var verts = new Vector3[]
        {
            new Vector3(-1f, -1f, 1f),
            new Vector3(-1f, 1f, 1f),
            new Vector3(-1f, -1f, -1f),
            new Vector3(-1f, 1f, -1f),
            new Vector3(1f, -1f, 1f),
            new Vector3(1f, 1f, 1f),
            new Vector3(1f, -1f, -1f),
            new Vector3(1f, 1f, -1f)
        };
        var normals = new Vector3[]
        {
            new Vector3(-1f, 0, 0),
            new Vector3(0, 0, -1f),
            new Vector3(1f, 0, 0),
            new Vector3(0, 0, 1f),
            new Vector3(0, -1f, 0),
            new Vector3(0, 1f, 0)
        };
        var faces = new FacingInfo[]
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
        };
        return new Object3d(verts, normals, faces, GenerateBoxUvs(verts, normals, faces), GenerateBoxGroups(normals, faces));
    }

    // Face-group id per cube triangle = which of the 6 axis faces it belongs to, ordered to match the
    // editor's TextureFace options: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z. Two triangles share each group.
    private static int[] GenerateBoxGroups(Vector3[] normals, FacingInfo[] faces)
    {
        var g = new int[faces.Length];
        for (int f = 0; f < faces.Length; f++) g[f] = AxisGroup(normals[faces[f].Normal1 - 1]);
        return g;
    }

    // Maps an axis-aligned face normal to a group id in +X,-X,+Y,-Y,+Z,-Z order (0..5).
    private static int AxisGroup(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0f ? 0 : 1;
        if (ay >= az)             return n.Y >= 0f ? 2 : 3;
        return n.Z >= 0f ? 4 : 5;
    }

    // The texture-face options for a given object type (beyond "All"): the cube exposes its 6 sides; every
    // other shape is a single "whole" group, so it offers only "All". Used by the editor's Field.TextureFace.
    public static readonly string[] CubeFaceNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
    public static string[] TextureFaceOptions(string? type)
        => string.Equals(type?.Trim(), "cube", StringComparison.OrdinalIgnoreCase) ? CubeFaceNames : Array.Empty<string>();

    // Procedural UVs for an axis-aligned box in [-1,1]^3: each of the 6 faces maps the FULL texture
    // (0,0)–(1,1). For each face corner, drop the axis the face normal points along and remap the two
    // remaining (tangent) coordinates from [-1,1] to [0,1]. Returned in face/corner order — length
    // faces.Length*3 — matching the Object3d build loop (uvs[f*3 + 0..2] are face f's three corners).
    private static Vector2[] GenerateBoxUvs(Vector3[] verts, Vector3[] normals, FacingInfo[] faces)
    {
        var uvs = new Vector2[faces.Length * 3];
        for (int f = 0; f < faces.Length; f++)
        {
            var fi = faces[f];
            Vector3 n = normals[fi.Normal1 - 1];
            uvs[f * 3]     = BoxCornerUv(verts[fi.Vertex1 - 1], n);
            uvs[f * 3 + 1] = BoxCornerUv(verts[fi.Vertex2 - 1], n);
            uvs[f * 3 + 2] = BoxCornerUv(verts[fi.Vertex3 - 1], n);
        }
        return uvs;
    }

    private static Vector2 BoxCornerUv(Vector3 p, Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        float u, v;
        if (ax >= ay && ax >= az) { u = p.Z; v = p.Y; }        // ±X face: tangents Z (u), Y (v)
        else if (ay >= ax && ay >= az) { u = p.X; v = p.Z; }   // ±Y face: tangents X (u), Z (v)
        else { u = p.X; v = p.Y; }                             // ±Z face: tangents X (u), Y (v)
        return new Vector2((u + 1f) * 0.5f, (v + 1f) * 0.5f);
    }

    // Builds a flat-shaded Object3d from local vertices + 1-based triangle index triples: one
    // normal per face, taken from the winding (Cross(b-a, c-a)) so the supplied normal always
    // agrees with the renderer's winding-based back-face test. Mirrors CreateCube's flat style.
    // uvs (optional): per-face-corner texture coordinates in face order (length tris.Count*3), threaded
    // straight into the Object3d ctor; null = untextured (Zero UVs, ignored unless a Texture is set).
    private static Object3d BuildFlat(List<Vector3> verts, List<(int a, int b, int c)> tris, Vector2[]? uvs = null)
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
        return new Object3d(vertArr, normals, faces, uvs);
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

        // Procedural UVs built ALONGSIDE the tris (same order), so the seam segment reaches u=1 while the
        // first reaches u=0 — no shared-vertex conflict because UVs are stored PER FACE-CORNER, not per
        // vertex. Side: u = angle fraction, v = height (bottom row v=1, top v=0). Caps: the ring wrapped
        // onto a disc in the texture, centre (0.5,0.5). Linear interp → bit-exact CPU↔GPU parity.
        Vector2 Cap(int s) { float a = MathF.Tau * s / seg; return new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)); }
        var tris = new List<(int, int, int)>();
        var uvs = new List<Vector2>();
        for (int s = 0; s < seg; s++)
        {
            int b0 = B(s), b1 = B(s + 1), t0 = T(s), t1 = T(s + 1);
            float u0 = (float)s / seg, u1 = (float)(s + 1) / seg;
            tris.Add((b0, t1, b1)); uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2(u1, 0f)); uvs.Add(new Vector2(u1, 1f));   // side quad
            tris.Add((b0, t0, t1)); uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2(u0, 0f)); uvs.Add(new Vector2(u1, 0f));
            tris.Add((tc, t1, t0)); uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s + 1)); uvs.Add(Cap(s));                     // top cap (+Y)
            tris.Add((bc, b0, b1)); uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s)); uvs.Add(Cap(s + 1));                     // bottom cap (-Y)
        }
        return BuildFlat(verts, tris, uvs.ToArray());
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

        // UVs alongside the tris (see CreateCylinder): the side triangle spans u=[s,s+1]/seg at v=1 with
        // the apex at the segment's u-midpoint at v=0; the base cap wraps the ring onto a disc (centre 0.5,0.5).
        Vector2 Cap(int s) { float a = MathF.Tau * s / seg; return new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)); }
        var tris = new List<(int, int, int)>();
        var uvs = new List<Vector2>();
        for (int s = 0; s < seg; s++)
        {
            float u0 = (float)s / seg, u1 = (float)(s + 1) / seg;
            tris.Add((B(s), apex, B(s + 1)));   // side
            uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2((u0 + u1) * 0.5f, 0f)); uvs.Add(new Vector2(u1, 1f));
            tris.Add((bc, B(s), B(s + 1)));     // base cap (-Y)
            uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s)); uvs.Add(Cap(s + 1));
        }
        return BuildFlat(verts, tris, uvs.ToArray());
    }

    // Raw geometry for a spot-marker cone whose base CROSS-SECTION reflects the spot's ConeShape: apex
    // at (0,+1,0), base polygon at y=-1 sized by baseRadius, plus a base cap — same structure/winding
    // as CreateCone. Circle = 16-seg ring; Square = 4 corners; Triangle = equilateral, vertex up (+Z),
    // circumradius baseRadius (matching the engine's triangle cone). Exposed (verts+tris, 1-based) so
    // the beam-fan can bake multiple oriented copies into one mesh.
    private static void ConeMarkerGeometry(ConeShapeKind shape, float baseRadius,
                                           out List<Vector3> verts, out List<(int, int, int)> tris)
    {
        verts = new List<Vector3>();
        switch (shape)
        {
            case ConeShapeKind.Square:
                verts.Add(new Vector3(-baseRadius, -1f, -baseRadius));
                verts.Add(new Vector3( baseRadius, -1f, -baseRadius));
                verts.Add(new Vector3( baseRadius, -1f,  baseRadius));
                verts.Add(new Vector3(-baseRadius, -1f,  baseRadius));
                break;
            case ConeShapeKind.Triangle:
                for (int s = 0; s < 3; s++)   // first vertex at +Z (a = π/2), then +120°
                {
                    float a = MathF.PI / 2f + MathF.Tau * s / 3f;
                    verts.Add(new Vector3(baseRadius * MathF.Cos(a), -1f, baseRadius * MathF.Sin(a)));
                }
                break;
            default:   // Circle
                for (int s = 0; s < 16; s++)
                {
                    float a = MathF.Tau * s / 16;
                    verts.Add(new Vector3(baseRadius * MathF.Cos(a), -1f, baseRadius * MathF.Sin(a)));
                }
                break;
        }

        int n = verts.Count;
        int apex = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // base centre

        int B(int s) => (s % n) + 1;

        tris = new List<(int, int, int)>();
        for (int s = 0; s < n; s++)
        {
            tris.Add((B(s), apex, B(s + 1)));   // side
            tris.Add((bc, B(s), B(s + 1)));     // base cap (-Y)
        }
    }

    private static Object3d BuildConeMarker(ConeShapeKind shape, float baseRadius)
    {
        ConeMarkerGeometry(shape, baseRadius, out var v, out var t);
        return BuildFlat(v, t);
    }

    // A multi-beam spot marker: BeamCount cones fanned about the aim exactly like SpotTerm's lighting
    // fan. Each beam's orientation is BAKED into the verts (vert.Rotate uses the same X->Y->Z order as
    // Object3d's LocalRotate), so a single mesh shows beams pointing in different world directions;
    // LocalRotate stays Zero and uniform Scale commutes with the baked rotation.
    private static Object3d BuildSpotFanMarker(Vector3 dir, int beams, ConeShapeKind coneShape, float coneAngleDeg)
    {
        beams = Math.Max(2, beams);
        bool nearVertical = MathF.Abs(dir.Norm().Y) > 0.99f;
        float baseRadius = Math.Clamp(2f * MathF.Tan(coneAngleDeg * MathF.PI / 180f), 0.2f, 3f);
        ConeMarkerGeometry(coneShape, baseRadius, out var cone, out var coneTris);
        var verts = new List<Vector3>(); var tris = new List<(int, int, int)>();
        for (int k = 0; k < beams; k++)
        {
            float ang = k * (MathF.Tau / beams);
            Vector3 axis = (nearVertical ? dir.Rotate(new Vector3(ang, 0, 0)) : dir.Rotate(new Vector3(0, ang, 0))).Norm();
            Vector3 euler = DirToEuler(axis);
            int off = verts.Count;
            foreach (var v in cone) verts.Add(v.Rotate(euler));            // bake each beam's orientation
            foreach (var (a, b, c) in coneTris) tris.Add((a + off, b + off, c + off));   // 1-based, shift by vert offset
        }
        var m = BuildFlat(verts, tris);
        m.Scale = LightMarkerScale;       // uniform scale commutes with the baked rotation; LocalRotate stays Zero
        return m;
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
        return BuildFlat(verts, tris, GeneratePyramidUvs(verts, tris));
    }

    // Procedural UVs for the pyramid, matching the cube's per-face "full texture" style. The two BASE
    // tris (-Y) unwrap like the cube's -Y face: drop Y, remap X,Z from [-1,1]→[0,1]. Each triangular
    // SIDE (baseA, apex, baseB — the apex is always the middle corner) drapes the texture with the two
    // base corners at the bottom edge (0,0)/(1,0) and the apex at the top centre (0.5,1).
    private static Vector2[] GeneratePyramidUvs(List<Vector3> v, List<(int a, int b, int c)> tris)
    {
        Vector2 Xz(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Z + 1f) * 0.5f);
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            if (t < 2)   // base
            {
                uvs[t * 3] = Xz(v[a - 1]); uvs[t * 3 + 1] = Xz(v[b - 1]); uvs[t * 3 + 2] = Xz(v[c - 1]);
            }
            else         // side: (baseA, apex, baseB) → (0,0),(0.5,1),(1,0)
            {
                uvs[t * 3] = new Vector2(0f, 0f); uvs[t * 3 + 1] = new Vector2(0.5f, 1f); uvs[t * 3 + 2] = new Vector2(1f, 0f);
            }
        }
        return uvs;
    }

    // A wedge / RAMP: a triangular prism whose top is a 45° inclined plane rising in +X (the high edge is
    // the +X/+Y corner). Origin-centred, ±1 like the cube. Great for rolling a ball DOWN the slope (it
    // rolls toward -X). The sloped face's outward normal points up-and-toward -X; rotate the object to aim it.
    public static Object3d CreateRamp()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f, -1f,  1f),   // 1 A front (low)
            new Vector3( 1f, -1f,  1f),   // 2 B front (bottom of the high side)
            new Vector3( 1f,  1f,  1f),   // 3 C front (high)
            new Vector3(-1f, -1f, -1f),   // 4 A back
            new Vector3( 1f, -1f, -1f),   // 5 B back
            new Vector3( 1f,  1f, -1f),   // 6 C back
        };
        var tris = new List<(int, int, int)>
        {
            (1, 4, 5), (1, 5, 2),   // bottom (-Y)
            (1, 3, 6), (1, 6, 4),   // sloped top (the ramp surface)
            (2, 5, 6), (2, 6, 3),   // vertical back wall (+X)
            (1, 2, 3),              // front triangular cap (+Z)
            (4, 6, 5),              // back triangular cap (-Z)
        };
        return BuildFlat(verts, tris, GenerateRampUvs(verts, tris));
    }

    // Procedural UVs for the ramp, cube-style: each face maps the FULL texture (0,0)–(1,1). Axis-aligned
    // faces drop the face-normal axis and remap the two tangent coords [-1,1]→[0,1] (like the cube); the
    // sloped top (a diagonal-normal rectangle) is unwrapped along its own two edge directions — Z (depth,
    // u) and the low→high rise (x, v). One projection per tri, indexed to match CreateRamp's tris order.
    private static Vector2[] GenerateRampUvs(List<Vector3> v, List<(int a, int b, int c)> tris)
    {
        Vector2 Xz(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Z + 1f) * 0.5f);   // -Y (drop Y)
        Vector2 Zy(Vector3 p) => new Vector2((p.Z + 1f) * 0.5f, (p.Y + 1f) * 0.5f);   // +X wall (drop X)
        Vector2 Xy(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Y + 1f) * 0.5f);   // ±Z caps (drop Z)
        Vector2 Slope(Vector3 p) => new Vector2((p.Z + 1f) * 0.5f, (p.X + 1f) * 0.5f); // sloped top: u=depth, v=rise
        var proj = new Func<Vector3, Vector2>[] { Xz, Xz, Slope, Slope, Zy, Zy, Xy, Xy };
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            uvs[t * 3] = proj[t](v[a - 1]); uvs[t * 3 + 1] = proj[t](v[b - 1]); uvs[t * 3 + 2] = proj[t](v[c - 1]);
        }
        return uvs;
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

    // A "flat picture" / billboard: a VERTICAL unit quad in the XY plane (origin-centred, ±1), standing
    // up and facing ±Z. TWO-SIDED — the two triangles are duplicated with reversed winding so the
    // winding-based back-face cull shows the image from EITHER side (mirrored on the back). Per-corner
    // UVs map the FULL texture right-side-up (u=(x+1)/2 across; v=(1-y)/2 down, so the quad's top row is
    // the image's top). Linear/barycentric interp → BIT-EXACT CPU↔GPU parity, like the cube/ramp.
    public static Object3d CreateFlatPicture()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f,  1f, 0f),   // 1 top-left
            new Vector3( 1f,  1f, 0f),   // 2 top-right
            new Vector3(-1f, -1f, 0f),   // 3 bottom-left
            new Vector3( 1f, -1f, 0f),   // 4 bottom-right
        };
        var tris = new List<(int, int, int)>
        {
            (3, 4, 2), (3, 2, 1),   // front (+Z normal)
            (3, 2, 4), (3, 1, 2),   // back  (-Z normal — reversed winding, so it's visible from behind)
        };
        Vector2 Uv(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (1f - p.Y) * 0.5f);
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            uvs[t * 3] = Uv(verts[a - 1]); uvs[t * 3 + 1] = Uv(verts[b - 1]); uvs[t * 3 + 2] = Uv(verts[c - 1]);
        }
        return BuildFlat(verts, tris, uvs);
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