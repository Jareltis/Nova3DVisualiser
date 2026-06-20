using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;

namespace SampleGame.Scenes;

public class PriviewNetworkScene : Scene
{
    private readonly bool _online;
    private readonly NetworkManager? _netManager;
    private readonly int _myNetId;
    private readonly Dictionary<int, Object3d> _remotePlayers = new();
    
    readonly Camera _myCamera;
    readonly Light _mainLight;
    readonly Object3d _floorPlane;

    readonly Sphere _demoSphere;
    readonly Object3d _demoCube;
    private List<Object3d> _models = new();

    private readonly bool _ownLightEnabled;
    private readonly Light? _extraLight;

    private bool _isChatting = false;
    private string _currentInput = "";
    private readonly List<string> _chatHistory = new();
    private const int MaxHistory = 5;


    public PriviewNetworkScene(IDisplaysManagerAsync manager, bool isServer, string targetIp, int port, bool addExtraLight, bool disableOwnLight, bool online = true) : base(manager)
    {
        _online = online;
        Exposure = 0.05f;
        Ambient = 0.1f;

        _ownLightEnabled = !disableOwnLight;
        if (addExtraLight)
            _extraLight = new Light(new Vector3(2, 6, 0), 600f);

        // Tunable starting framing: lower and nearly level so models sit near vertical center.
        _myCamera = new Camera(new Vector3(-5.5f, 1.5f, 0), new Vector3(0, 0, -0.05f));
        _mainLight = new Light(new Vector3(0, 2, -2), 500);

        _floorPlane = CreatePlane();
        _floorPlane.Position = new Vector3(0, 0, 0);
        _floorPlane.Color = ConsoleColor.Yellow;

        _demoSphere = new Sphere(new Vector3(1, 1, -2), Vector3.Zero, r: 1f) { Color = ConsoleColor.Red };

        _demoCube = CreateCube();
        _demoCube.Position = new Vector3(2, 1, 2);
        _demoCube.Color = ConsoleColor.Cyan;

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
        AddDisplaysObject(_floorPlane);
        AddDisplaysObject(_demoSphere);

        _demoCube.UpdateGeometry();
        AddDisplaysObject(_demoCube);

        if (_ownLightEnabled) AddLight(_mainLight);
        if (_extraLight != null) AddLight(_extraLight);

        SetMainCamera(_myCamera);

        _models = ModelLoader.LoadFolder(AppPaths.ModelsFolder);
        foreach (var m in _models) AddDisplaysObject(m);
    }

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
            HandleGameInput(GameTime.GetDeltaTime());
            
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
        DrawChatInterface();
    }
    
    
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
    
    private Object3d CreateCube()
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

    private Object3d CreatePlane()
    {
        return new Object3d(
            new Vector3[]
            {
                new Vector3(-10f, 0f, 10f),
                new Vector3(10f, 0f, 10f),
                new Vector3(-10f, 0f, -10f),
                new Vector3(10f, 0f, -10f)
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