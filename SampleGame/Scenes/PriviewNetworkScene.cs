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

public partial class PriviewNetworkScene : Scene
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
    
    
}