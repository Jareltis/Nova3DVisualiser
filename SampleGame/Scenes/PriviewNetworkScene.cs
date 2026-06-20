using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Worlds;

namespace SampleGame.Scenes;

public class PriviewNetworkScene : Scene
{
    private readonly bool _online;
    private readonly NetworkManager? _netManager;
    private readonly int _myNetId;
    private readonly Dictionary<int, Object3d> _remotePlayers = new();
    
    readonly Camera _myCamera;
    readonly Light _mainLight;

    private readonly WorldConfig _world;
    private readonly List<Object3d> _models = new();   // spinnable Object3d objects (meshes + cubes)

    private readonly bool _ownLightEnabled;
    private readonly Light? _extraLight;

    private bool _isChatting = false;
    private string _currentInput = "";
    private readonly List<string> _chatHistory = new();
    private const int MaxHistory = 5;

    // ---- In-scene editor (3b-i: toggle, spawn, select, move, save) ----
    // Pairs a saved WorldObject descriptor (Type/Mesh/Anchor/Radius metadata) with its
    // live scene instance (Object3d for mesh/cube, Sphere for sphere — both GameObjects).
    private sealed class EditEntry
    {
        public required WorldObject Descriptor;
        public required GameObject Instance;
    }

    private bool _editMode = false;
    private readonly List<EditEntry> _editables = new();
    private int _selected = -1;
    private readonly List<string> _spawnTypes = new();   // "cube", "sphere", then each library mesh
    private int _spawnIndex = 0;
    private const float MoveStep = 0.5f;
    private readonly HashSet<ConsoleKey> _prevDown = new();
    private int _saveFlash = 0;
    private string _saveMsg = "";


    public PriviewNetworkScene(IDisplaysManagerAsync manager, WorldConfig world, bool isServer, string targetIp, int port, bool online = true) : base(manager)
    {
        _online = online;
        Exposure = 0.05f;
        Ambient = 0.1f;

        _world = world;
        _ownLightEnabled = !world.Graphics.DisableCameraLight;
        if (world.Graphics.ExtraLight)
            _extraLight = new Light(new Vector3(2, 6, 0), 600f);

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

        PacketManager.Subscribe<TransformPacket>(OnTransformReceived);
        PacketManager.Subscribe<ChatPacket>(OnChatReceived);

        if (isServer)
        {
            _netManager.StartServer(port);
            Logger.Info($"Server listening on port {port}");
            Console.Title = $"SERVER (Port: {port}) | ID: {_myNetId}";
        }
        else
        {
            _netManager.Connect(targetIp, port);
            Logger.Info($"Connecting to {targetIp}:{port}");
            Console.Title = $"CLIENT (ID: {_myNetId}) -> {targetIp}:{port}";
        }


    }

    public override void Start()
    {
        if (_world.Platform.Enabled)
        {
            Object3d floor = CreatePlane(_world.Platform.Size);
            floor.Position = new Vector3(0, 0, 0);
            floor.Color = ParseColor(_world.Platform.Color, ConsoleColor.Yellow);
            AddDisplaysObject(floor);
        }

        if (_ownLightEnabled) AddLight(_mainLight);
        if (_extraLight != null) AddLight(_extraLight);

        SetMainCamera(_myCamera);

        // Spawn palette for the editor: the two primitives, then every library mesh.
        _spawnTypes.Add("cube");
        _spawnTypes.Add("sphere");
        _spawnTypes.AddRange(WorldManager.ListAvailableMeshes());

        foreach (var obj in _world.Objects)
            BuildWorldObject(obj);
    }

    // Builds one world object (mesh / cube / sphere), adds it to the display, and records
    // an editable entry pairing the descriptor with its live instance. Returns the entry
    // (null if it did not resolve). The platform, lights and remote players are NOT recorded.
    private EditEntry? BuildWorldObject(WorldObject o)
    {
        GameObject? instance = null;

        switch (o.Type?.Trim().ToLowerInvariant())
        {
            case "mesh":
            {
                if (string.IsNullOrWhiteSpace(o.Mesh))
                {
                    Logger.Warning("World mesh object has no Mesh name; skipping.");
                    return null;
                }

                Object3d? mesh = ModelLoader.LoadRawMesh(AppPaths.ModelsFolder, o.Mesh);
                if (mesh == null)
                {
                    Logger.Warning($"World mesh '{o.Mesh}' did not resolve; skipping.");
                    return null;
                }

                // Same order the old per-model loader used.
                mesh.Position = ToVec(o.Position);
                mesh.LocalRotate = ToVec(o.Rotation);
                mesh.Scale = o.Scale;
                mesh.Color = ParseColor(o.Color, ConsoleColor.White);
                mesh.RotateSpeed = o.RotateSpeed;
                mesh.ApplyAnchor(ParseAnchor(o.Anchor));
                mesh.BuildAcceleration();
                mesh.UpdateGeometry();

                _models.Add(mesh);
                AddDisplaysObject(mesh);
                instance = mesh;
                break;
            }

            case "cube":
            {
                Object3d cube = CreateCube();
                cube.Position = ToVec(o.Position);
                cube.LocalRotate = ToVec(o.Rotation);
                cube.Scale = o.Scale;
                cube.Color = ParseColor(o.Color, ConsoleColor.White);
                cube.RotateSpeed = o.RotateSpeed;
                cube.UpdateGeometry();

                _models.Add(cube);
                AddDisplaysObject(cube);
                instance = cube;
                break;
            }

            case "sphere":
            {
                var sphere = new Sphere(ToVec(o.Position), ToVec(o.Rotation), o.Radius)
                {
                    Color = ParseColor(o.Color, ConsoleColor.White)
                };
                AddDisplaysObject(sphere);
                instance = sphere;
                break;
            }

            default:
                Logger.Warning($"Unknown world object type '{o.Type}'; skipping.");
                return null;
        }

        var entry = new EditEntry { Descriptor = o, Instance = instance };
        _editables.Add(entry);
        return entry;
    }

    private static Vec3Config FromVec(Vector3 v) => new Vec3Config { X = v.X, Y = v.Y, Z = v.Z };

    /// <summary>
    /// Save-back conversion: reads the live instance's Position/Rotation/Scale/Color into a
    /// fresh WorldObject, keeping Type/Mesh/Anchor/Radius from the descriptor. Takes a
    /// GameObject so it covers both Object3d (mesh/cube) and Sphere instances.
    /// </summary>
    public static WorldObject FromInstance(WorldObject descriptor, GameObject instance)
    {
        var o3d = instance as Object3d;
        return new WorldObject
        {
            Type = descriptor.Type,
            Mesh = descriptor.Mesh,
            Anchor = descriptor.Anchor,
            Radius = descriptor.Radius,
            Position = FromVec(instance.Position),
            Rotation = FromVec(instance.LocalRotate),
            Scale = o3d?.Scale ?? descriptor.Scale,
            Color = instance.Color.ToString(),
            RotateSpeed = o3d?.RotateSpeed ?? descriptor.RotateSpeed,
        };
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
        _netManager?.ProcessEvents();

        foreach (var m in _models)
            if (m.RotateSpeed != 0f)
            {
                m.LocalRotate.Y += m.RotateSpeed * GameTime.GetDeltaTime();
                m.UpdateGeometry();
            }

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

        if (!_isChatting && _netManager != null)
        {
            var packet = new TransformPacket(_myCamera.Position, _myCamera.LocalRotate);
            _netManager.SendPacket(packet, _myNetId);
        }
        if (_ownLightEnabled) _mainLight.Position = _myCamera.Position;
        if (_saveFlash > 0) _saveFlash--;
        DrawChatInterface();
        DrawEditorOverlay();
    }

    // ---- Editor input (only called in edit mode, never while chatting) ----
    private void HandleEditorInput()
    {
        // Cycle the spawn type.
        if (Pressed(ConsoleKey.G) && _spawnTypes.Count > 0)
            _spawnIndex = (_spawnIndex + 1) % _spawnTypes.Count;

        // Spawn the current type a couple of units in front of the camera, at ground level.
        if (Pressed(ConsoleKey.Enter))
            SpawnCurrent();

        // Cycle the selection (wrap around).
        if (_editables.Count > 0)
        {
            if (Pressed(ConsoleKey.Oem4)) // '['
                _selected = (_selected <= 0 ? _editables.Count : _selected) - 1;
            if (Pressed(ConsoleKey.Oem6)) // ']'
                _selected = (_selected + 1) % _editables.Count;
        }

        // Move the selected object by a fixed step per press (world axes).
        if (_selected >= 0 && _selected < _editables.Count)
        {
            var delta = Vector3.Zero;
            if (Pressed(ConsoleKey.L)) delta.X += MoveStep;
            if (Pressed(ConsoleKey.J)) delta.X -= MoveStep;
            if (Pressed(ConsoleKey.I)) delta.Z += MoveStep;
            if (Pressed(ConsoleKey.K)) delta.Z -= MoveStep;
            if (Pressed(ConsoleKey.U)) delta.Y += MoveStep;
            if (Pressed(ConsoleKey.O)) delta.Y -= MoveStep;

            if (delta.X != 0f || delta.Y != 0f || delta.Z != 0f)
            {
                var inst = _editables[_selected].Instance;
                inst.Position += delta;
                if (inst is Object3d o) o.UpdateGeometry();
            }
        }

        // Save the arrangement back into the world JSON.
        if (Pressed(ConsoleKey.F5))
            SaveWorld();
    }

    private void SpawnCurrent()
    {
        if (_spawnTypes.Count == 0) return;

        Vector3 yaw = new Vector3(0, _myCamera.LocalRotate.Y, 0);
        Vector3 forward = new Vector3(1, 0, 0).Rotate(yaw);
        Vector3 spawnPos = _myCamera.Position + forward * 3f;
        spawnPos.Y = 0f; // ground level

        string label = _spawnTypes[_spawnIndex];
        var descriptor = new WorldObject
        {
            Type = label is "cube" or "sphere" ? label : "mesh",
            Mesh = label is "cube" or "sphere" ? null : label,
            Position = FromVec(spawnPos),
            Scale = 1f,
            Color = "White",
            Anchor = "Bottom",
            Radius = 1f,
        };

        var entry = BuildWorldObject(descriptor);
        if (entry != null) _selected = _editables.Count - 1;
    }

    private void SaveWorld()
    {
        _world.Objects = _editables.Select(e => FromInstance(e.Descriptor, e.Instance)).ToList();
        WorldManager.Save(_world);
        _saveMsg = $"Saved to {_world.Name} ({_world.Objects.Count} objects)";
        _saveFlash = 120;
        Logger.Info(_saveMsg);
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

        UI.AddText("[G] cycle type  [Enter] spawn  [ [ / ] ] select", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText("Move: J/L=X  I/K=Z  U/O=Y    [F5] save", new Vector2Int(x, y++), ConsoleColor.Gray);

        if (_saveFlash > 0)
            UI.AddText(_saveMsg, new Vector2Int(x, y), ConsoleColor.Green);
    }

    private static string DescribeEntry(EditEntry e) =>
        e.Descriptor.Type == "mesh" ? $"mesh:{e.Descriptor.Mesh}" : e.Descriptor.Type;
    
    
    private void HandleGameInput(float dt)
    {
        float rotateSpeed = 1.5f;
        float moveSpeed = 5.0f;

        if (Input.IsGetKey(ConsoleKey.LeftArrow)) _myCamera.LocalRotate.Y += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.RightArrow)) _myCamera.LocalRotate.Y -= rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.UpArrow)) _myCamera.LocalRotate.Z += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.DownArrow)) _myCamera.LocalRotate.Z -= rotateSpeed * dt;
        _myCamera.LocalRotate.Z = Math.Clamp(_myCamera.LocalRotate.Z, -1.5f, 1.5f);
        _myCamera.LocalRotate.X = 0;

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

    private Object3d CreatePlane(float size)
    {
        return new Object3d(
            new Vector3[]
            {
                new Vector3(-size, 0f, size),
                new Vector3(size, 0f, size),
                new Vector3(-size, 0f, -size),
                new Vector3(size, 0f, -size)
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
}