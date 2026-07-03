using System.Net;
using System.Runtime.InteropServices;
using NStack;
using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Scenes;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.IO.Compression;
using System.Text.Json;
using Terminal.Gui;

namespace SampleGame;

class Program
{
    // Setup wizard step and per-dialog outcome.
    enum Step { Mode, Role, World, Create, Load, Network }
    enum DlgResult { Ok, Back, Quit }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "impulsetest") { ImpulseSelfTest(); return; }
        if (args.Length > 0 && args[0] == "cutovertest") { CutoverSelfTest(); return; }
        if (args.Length > 0 && args[0] == "bvhtest") { BvhSelfTest(); return; }
        if (args.Length > 0 && args[0] == "worldtest") { WorldSelfTest(); return; }
        if (args.Length > 0 && args[0] == "editortest") { EditorSelfTest(); return; }
        if (args.Length > 0 && args[0] == "picktest") { PickSelfTest(); return; }
        if (args.Length > 0 && args[0] == "worldsynctest") { WorldSyncSelfTest(); return; }
        if (args.Length > 0 && args[0] == "colortest") { ColorSelfTest(); return; }
        if (args.Length > 0 && args[0] == "collisiontest") { CollisionSelfTest(); return; }
        if (args.Length > 0 && args[0] == "physicstest") { PhysicsSelfTest(); return; }
        if (args.Length > 0 && args[0] == "gputest") { GpuSelfTest(); return; }
        if (args.Length > 0 && args[0] == "texturetest") { TextureSelfTest(); return; }

        // Crash net: the render loop is async + parallel (Parallel.For), so a crash on a worker
        // thread or an unobserved task never reaches the try/catch below. Capture those globally
        // too, so any crash lands in logs/ with a FATAL marker and the console is restored.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            RunApp();
        }
        catch (Exception ex)
        {
            LogFatal("Main", ex);
        }
        finally
        {
            RestoreConsole();
        }
    }

    // The full interactive app: Terminal.Gui setup wizard -> scene build -> render loop.
    static void RunApp()
    {
        Logger.Init(AppPaths.LogsFolder);
        Logger.Info("Application started");

        // ---- Setup choices ----
        bool online = false;
        bool isServer = false;
        string ip = "127.0.0.1";
        int port = 7777;
        WorldConfig? chosenWorld = null;
        bool quit = false;

        // There is always at least the default world to load.
        WorldManager.EnsureDefault();

        // ---- Terminal.Gui modal setup wizard ----
        Application.Init();
        try
        {
            // Neutral grey-on-dark scheme with green accents (no default blue theme).
            var scheme = new ColorScheme
            {
                Normal    = Application.Driver.MakeAttribute(Color.Gray,        Color.Black),
                Focus     = Application.Driver.MakeAttribute(Color.Black,       Color.Green),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
                HotFocus  = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Green),
                Disabled  = Application.Driver.MakeAttribute(Color.DarkGray,    Color.Black),
            };
            Colors.Base = scheme;
            Colors.Dialog = scheme;
            Colors.Menu = scheme;

            // Each step's Dialog is run inside a full-screen host toplevel (see RunStepDialog), so
            // running a step overdraws the WHOLE screen and the previous step cannot linger behind
            // it — without any manual Clear/Refresh (those destabilized Terminal.Gui and crashed).

            var step = Step.Mode;
            bool done = false;
            while (!done)
            {
                switch (step)
                {
                    case Step.Mode:
                        if (ShowModeDialog(ref online) == DlgResult.Quit) { quit = true; done = true; }
                        else step = online ? Step.Role : Step.World;
                        break;

                    case Step.Role:
                        // Server picks a world; the Client gets its world from the server, so it
                        // skips the World menu and goes straight to the network dialog.
                        if (ShowRoleDialog(ref isServer) == DlgResult.Back) step = Step.Mode;
                        else step = isServer ? Step.World : Step.Network;
                        break;

                    case Step.World:
                        var menu = ShowWorldMenuDialog(out bool create);
                        if (menu == DlgResult.Back) step = online ? Step.Role : Step.Mode;
                        else step = create ? Step.Create : Step.Load;
                        break;

                    case Step.Create:
                        if (ShowCreateDialog(out WorldConfig? created) == DlgResult.Back) step = Step.World;
                        else { chosenWorld = created; step = online ? Step.Network : Step.Mode; if (!online) done = true; }
                        break;

                    case Step.Load:
                        if (ShowLoadDialog(out WorldConfig? loaded) == DlgResult.Back) step = Step.World;
                        else { chosenWorld = loaded; step = online ? Step.Network : Step.Mode; if (!online) done = true; }
                        break;

                    case Step.Network:
                        // Client's Network-dialog Back returns to Role; Server's returns to the World menu.
                        if (ShowNetworkDialog(isServer, ref ip, ref port) == DlgResult.Back)
                            step = isServer ? Step.World : Step.Role;
                        else done = true; // Start
                        break;
                }
            }
        }
        finally
        {
            Application.Shutdown();
        }

        // Reset the console to a clean state so the raw renderer starts cleanly.
        Console.ResetColor();
        Console.Clear();

        // The Client never chose a world — give it a platform-only placeholder to start with;
        // the real world arrives from the server over the network.
        if (!quit && online && !isServer && chosenWorld == null)
        {
            chosenWorld = new WorldConfig
            {
                Name = "(remote)",
                Graphics = new GraphicsConfig(),
                Platform = new PlatformConfig { Enabled = true, Size = 10f, Color = "Yellow" },
                Objects = new List<WorldObject>(),
            };
        }

        if (quit || chosenWorld == null)
        {
            Logger.Info("Setup cancelled by user");
            return;
        }

        Object3d.UseBvh = chosenWorld.Graphics.Bvh;
        Logger.Info($"World='{chosenWorld.Name}'; Mode={(online ? (isServer ? "Server" : "Client") : "Local")}; Network {ip}:{port}; platform={chosenWorld.Platform.Enabled}; objects={chosenWorld.Objects.Count}; extraLight={chosenWorld.Graphics.ExtraLight}; ownLight={!chosenWorld.Graphics.DisableCameraLight}; shadows={chosenWorld.Graphics.Shadows}; bvh={chosenWorld.Graphics.Bvh}");

        Console.WriteLine(online
            ? $"Starting {(isServer ? "Server" : "Client")} on {ip}:{port} [world: {chosenWorld.Name}]..."
            : $"Starting local (offline) session [world: {chosenWorld.Name}]...");
        Thread.Sleep(500);
        Console.Clear();

        var scene = new PriviewNetworkScene(new DisplayManagerAsync(), chosenWorld, isServer, ip, port, online);
        scene.EnableShadows = chosenWorld.Graphics.Shadows;

        // Pick the renderer the world asked for. "gpu" tries an NVIDIA/ILGPU screen and silently
        // falls back to the CPU screen if no usable GPU is present (logged + printed once).
        Screen screen;
        if (string.Equals(chosenWorld.Graphics.Renderer, "gpu", StringComparison.OrdinalIgnoreCase))
        {
            var gpu = Nova3DVisualiser.Gpu.GpuScreen.TryCreate(out string gpuStatus);
            if (gpu != null)
            {
                Logger.Info($"GPU renderer active: {gpuStatus}");
                Console.WriteLine($"GPU renderer: {gpuStatus}");
                screen = gpu;
            }
            else
            {
                Logger.Warning($"GPU renderer unavailable ({gpuStatus}); falling back to CPU.");
                Console.WriteLine($"GPU unavailable ({gpuStatus}) — using CPU renderer.");
                Thread.Sleep(900);
                screen = new ConsoleScreenAsync();
            }
        }
        else
        {
            screen = new ConsoleScreenAsync();
        }

        Logger.Info("Scene constructed, entering render loop");
        try
        {
            new Frame(scene, screen).MainLoop();
        }
        catch (Exception ex) { Logger.Error("Unhandled exception in main loop", ex); throw; }
        //new Frame(new PreviewScene(new DisplayManagerAsync()), new ConsoleScreenAsync()).MainLoop();
    }

    // Runs one wizard step's centered Dialog inside a full-screen host toplevel. Because the host
    // fills and overdraws the WHOLE screen when it draws, running a step fully covers the previous
    // step — fixing the lingering-frame bug with NO manual Clear/Refresh. The inner Dialog keeps
    // its centered, fixed-size box look (border, title, buttons); the host is borderless and fills
    // the screen with the wizard's grey-on-black scheme. The Dialog's buttons still call
    // Application.RequestStop(), which stops this host, so each ShowXxxDialog returns as before.
    static void RunStepDialog(Dialog dialog)
    {
        var host = new Window
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Colors.Base,
        };
        host.Border.BorderStyle = BorderStyle.None;
        host.Add(dialog);
        Application.Run(host);
    }

    // Writes the full exception (type, message, stack, and every InnerException) to the log with a
    // FATAL marker, restores the console, and echoes the text so it survives the raw renderer.
    static void LogFatal(string source, Exception? ex)
    {
        string text = $"FATAL [{source}] {DescribeException(ex)}";
        try { Logger.Error(text); } catch { /* logging must never crash the crash handler */ }
        RestoreConsole();
        try { Console.Error.WriteLine(text); } catch { }
    }

    // Flattens an exception chain (each level's type, message, and stack) for the log.
    static string DescribeException(Exception? ex)
    {
        if (ex == null) return "(no exception object)";
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
            sb.AppendLine(e.StackTrace);
            if (e.InnerException != null) sb.AppendLine("--- inner exception ---");
        }
        return sb.ToString();
    }

    // Brings the terminal back to a usable state (after a crash or a normal exit): shut Terminal.Gui
    // down if it is still up, reset colors, and show the cursor. Every step is guarded so restoring
    // the console never throws.
    static void RestoreConsole()
    {
        try { Application.Shutdown(); } catch { }
        try { Console.ResetColor(); } catch { }
        try { Console.CursorVisible = true; } catch { }
    }

    // ---- Dialog 1: session mode (Local / Online). Secondary button quits the app. ----
    static DlgResult ShowModeDialog(ref bool online)
    {
        var result = DlgResult.Quit;
        var radio = new RadioGroup(new ustring[] { "Local / Offline", "Online" }, online ? 1 : 0)
        {
            X = 1, Y = 1
        };

        var ok = new Button("Ok", is_default: true);
        var btnQuit = new Button("Quit");
        ok.Clicked += () => { result = DlgResult.Ok; Application.RequestStop(); };
        btnQuit.Clicked += () => { result = DlgResult.Quit; Application.RequestStop(); };

        var dialog = new Dialog("Session mode", 50, 9, ok, btnQuit);
        dialog.Add(new Label("Run a local solo session or go online?") { X = 1, Y = 0 }, radio);
        RunStepDialog(dialog);

        if (result == DlgResult.Ok) online = radio.SelectedItem == 1;
        return result;
    }

    // ---- Dialog 2: network role (Server / Client). ----
    static DlgResult ShowRoleDialog(ref bool isServer)
    {
        var result = DlgResult.Back;
        var radio = new RadioGroup(new ustring[] { "Server (host)", "Client (join)" }, isServer ? 0 : 1)
        {
            X = 1, Y = 1
        };

        var ok = new Button("Ok", is_default: true);
        var back = new Button("Back");
        ok.Clicked += () => { result = DlgResult.Ok; Application.RequestStop(); };
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };

        var dialog = new Dialog("Network role", 50, 9, ok, back);
        dialog.Add(new Label("Host a server or join one?") { X = 1, Y = 0 }, radio);
        RunStepDialog(dialog);

        if (result == DlgResult.Ok) isServer = radio.SelectedItem == 0;
        return result;
    }

    // ---- Dialog 3: host/connect config with validation. ----
    static DlgResult ShowNetworkDialog(bool isServer, ref string ip, ref int port)
    {
        var result = DlgResult.Back;
        string ipLocal = ip;
        int portLocal = port;

        var ipField = new TextField(ip) { X = 14, Y = 2, Width = Dim.Fill() - 2 };
        var portField = new TextField(port.ToString()) { X = 14, Y = (isServer ? 3 : 4), Width = 10 };

        var ok = new Button("Ok", is_default: true);
        var back = new Button("Back");
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };
        ok.Clicked += () =>
        {
            if (!int.TryParse(portField.Text.ToString()?.Trim(), out var p) || p < 1 || p > 65535)
            {
                MessageBox.ErrorQuery("Invalid port", "Port must be a number between 1 and 65535.", "Ok");
                return;
            }

            string ipVal = ipLocal;
            if (!isServer)
            {
                ipVal = ipField.Text.ToString()?.Trim() ?? "";
                if (!IPAddress.TryParse(ipVal, out _))
                {
                    MessageBox.ErrorQuery("Invalid IP", "Enter a valid IP address.", "Ok");
                    return;
                }
            }

            ipLocal = ipVal;
            portLocal = p;
            result = DlgResult.Ok;
            Application.RequestStop();
        };

        var dialog = new Dialog(isServer ? "Host server" : "Join server", 56, 11, ok, back);
        if (isServer)
        {
            string localIP = NetworkUtils.GetLocalIPAddress();
            dialog.Add(
                new Label($"Your local IP: {localIP}") { X = 1, Y = 0 },
                new Label("Give this IP to your friend!") { X = 1, Y = 1 },
                new Label("Listen port:") { X = 1, Y = 3 },
                portField);
        }
        else
        {
            dialog.Add(
                new Label("Connect to a server.") { X = 1, Y = 0 },
                new Label("Server IP:") { X = 1, Y = 2 },
                ipField,
                new Label("Port:") { X = 1, Y = 4 },
                portField);
        }

        RunStepDialog(dialog);

        if (result == DlgResult.Ok)
        {
            ip = ipLocal;
            port = portLocal;
        }
        return result;
    }

    // ---- Dialog 4: world menu (Create new / Load existing). ----
    static DlgResult ShowWorldMenuDialog(out bool create)
    {
        var result = DlgResult.Back;
        var radio = new RadioGroup(new ustring[] { "Create new world", "Load world" }, 0) { X = 1, Y = 1 };

        var ok = new Button("Ok", is_default: true);
        var back = new Button("Back");
        ok.Clicked += () => { result = DlgResult.Ok; Application.RequestStop(); };
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };

        var dialog = new Dialog("World", 50, 9, ok, back);
        dialog.Add(new Label("Create a new world or load a saved one?") { X = 1, Y = 0 }, radio);
        RunStepDialog(dialog);

        create = radio.SelectedItem == 0;
        return result;
    }

    // ---- Dialog 5: create a world (name + graphics + platform toggle). ----
    // A new world starts with no objects; scene objects are added by hand-editing
    // the world JSON for now (the in-scene editor is a later step).
    static DlgResult ShowCreateDialog(out WorldConfig? world)
    {
        world = null;
        var result = DlgResult.Back;
        WorldConfig? built = null;

        var nameField = new TextField("myworld") { X = 13, Y = 0, Width = Dim.Fill() - 2 };

        var cbShadows = new CheckBox("Shadows", true) { X = 1, Y = 3 };
        var cbBvh = new CheckBox("BVH acceleration", true) { X = 1, Y = 4 };
        var cbExtra = new CheckBox("Extra fixed light", false) { X = 1, Y = 5 };
        var cbDisableOwn = new CheckBox("Disable camera light", false) { X = 1, Y = 6 };
        var rgRenderer = new RadioGroup(new ustring[] { "CPU", "GPU (NVIDIA)" }, 0)
        {
            X = 11, Y = 7,
            DisplayMode = DisplayModeLayout.Horizontal,
            HorizontalSpace = 2,
        };
        var cbPlatform = new CheckBox("Include platform", true) { X = 1, Y = 8 };
        var rgShape = new RadioGroup(new ustring[] { "Square", "Rectangle", "Circle" }, 0)
        {
            X = 1, Y = 10,
            DisplayMode = DisplayModeLayout.Horizontal,
            HorizontalSpace = 2,
        };
        var sizeField = new TextField("10") { X = 22, Y = 11, Width = 8 };
        var widthField = new TextField("20") { X = 22, Y = 12, Width = 8 };
        var depthField = new TextField("20") { X = 33, Y = 12, Width = 8 };
        var cbGravity = new CheckBox("Gravity", false) { X = 1, Y = 14 };
        var cbCollision = new CheckBox("Collision", true) { X = 20, Y = 14 };
        var gravityField = new TextField("9.8") { X = 22, Y = 15, Width = 8 };
        var restitutionField = new TextField("0") { X = 39, Y = 15, Width = 6 };

        var create = new Button("Create", is_default: true);
        var back = new Button("Back");
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };
        create.Clicked += () =>
        {
            string safe = SanitizeWorldName(nameField.Text.ToString() ?? "");
            if (string.IsNullOrEmpty(safe))
            {
                MessageBox.ErrorQuery("Invalid name", "Enter a non-empty world name (letters, digits, - or _).", "Ok");
                return;
            }

            var w = new WorldConfig
            {
                Name = safe,
                Graphics = new GraphicsConfig
                {
                    Shadows = cbShadows.Checked,
                    Bvh = cbBvh.Checked,
                    ExtraLight = cbExtra.Checked,
                    DisableCameraLight = cbDisableOwn.Checked,
                    Renderer = rgRenderer.SelectedItem == 1 ? "gpu" : "cpu",
                },
                Platform = new PlatformConfig
                {
                    Enabled = cbPlatform.Checked,
                    Shape = rgShape.SelectedItem switch { 1 => "rectangle", 2 => "circle", _ => "square" },
                    Size = ParseFloatOr(sizeField.Text, 10f),
                    Width = ParseFloatOr(widthField.Text, 20f),
                    Depth = ParseFloatOr(depthField.Text, 20f),
                    Color = "Yellow",
                },
                Objects = new List<WorldObject>(),
                Physics = new PhysicsConfig
                {
                    GravityEnabled = cbGravity.Checked,
                    GravityStrength = float.TryParse(gravityField.Text?.ToString(), out var g) ? g : 9.8f,
                    CollisionEnabled = cbCollision.Checked,
                    Restitution = Math.Clamp(ParseFloatOr(restitutionField.Text, 0f), 0f, 1f),
                },
            };
            WorldManager.Save(w);
            built = w;
            result = DlgResult.Ok;
            Application.RequestStop();
        };

        var dialog = new Dialog("Create world", 56, 20, create, back);
        dialog.Add(
            new Label("World name:") { X = 1, Y = 0 }, nameField,
            new Label("Graphics (Space toggles):") { X = 1, Y = 2 },
            cbShadows, cbBvh, cbExtra, cbDisableOwn,
            new Label("Renderer:") { X = 1, Y = 7 }, rgRenderer,
            cbPlatform,
            new Label("Platform shape:") { X = 1, Y = 9 }, rgShape,
            new Label("Size (square/circle):") { X = 1, Y = 11 }, sizeField,
            new Label("Rect W x D:") { X = 1, Y = 12 }, widthField, depthField,
            new Label("Physics (Space toggles):") { X = 1, Y = 13 }, cbGravity, cbCollision,
            new Label("Gravity strength:") { X = 1, Y = 15 }, gravityField,
            new Label("Bounce:") { X = 31, Y = 15 }, restitutionField);
        RunStepDialog(dialog);

        world = built;
        return result;
    }

    // ---- Dialog 6: load a saved world. ----
    static DlgResult ShowLoadDialog(out WorldConfig? world)
    {
        world = null;
        var result = DlgResult.Back;
        WorldConfig? loaded = null;

        var names = WorldManager.ListWorlds();
        var labels = names.Select(n => (ustring)n).ToArray();
        var radio = new RadioGroup(labels, 0) { X = 1, Y = 1 };

        var load = new Button("Load", is_default: true);
        var back = new Button("Back");
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };
        load.Clicked += () =>
        {
            if (names.Count == 0) { result = DlgResult.Back; Application.RequestStop(); return; }

            string name = names[radio.SelectedItem];
            var w = WorldManager.Load(name);
            if (w == null)
            {
                MessageBox.ErrorQuery("Load failed", $"Could not load world '{name}' (see log).", "Ok");
                return;
            }
            loaded = w;
            result = DlgResult.Ok;
            Application.RequestStop();
        };

        int height = Math.Min(20, 7 + Math.Max(1, names.Count));
        var dialog = new Dialog("Load world", 50, height, load, back);
        dialog.Add(new Label("Select a saved world:") { X = 1, Y = 0 }, radio);
        RunStepDialog(dialog);

        world = loaded;
        return result;
    }

    static string SanitizeWorldName(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
        }
        return sb.ToString();
    }

    // Parses a dialog text field as a positive float (invariant), falling back to a default for
    // blank/invalid/non-positive input so the platform always gets a sane size.
    static float ParseFloatOr(ustring? text, float fallback)
    {
        if (float.TryParse((text?.ToString() ?? "").Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out float v) && v > 0f)
            return v;
        return fallback;
    }

    static void BvhSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== BVH SELF-TEST ===");

        // models/ is a pure mesh library now: load each .obj raw and build its acceleration here.
        var models = new List<Object3d>();
        if (Directory.Exists(AppPaths.ModelsFolder))
        {
            foreach (string objPath in Directory.GetFiles(AppPaths.ModelsFolder, "*.obj"))
            {
                var m = ModelLoader.LoadRawMesh(AppPaths.ModelsFolder, Path.GetFileNameWithoutExtension(objPath));
                if (m == null) continue;
                m.BuildAcceleration();
                models.Add(m);
            }
        }
        if (models.Count == 0) { Console.WriteLine("No models found - cannot run self-test."); return; }

        // Pick the highest-triangle model (the monkey/Suzanne; it has a BVH).
        Object3d model = models[0];
        foreach (var m in models) if (m.FaceCount > model.FaceCount) model = m;
        Console.WriteLine($"Selected model: {model.FaceCount} triangles, HasBvh={model.HasBvh}");

        // Non-trivial transform to exercise the inverse.
        model.LocalRotate = new Vector3(0.3f, 0.7f, -0.4f);
        model.Scale = 0.3f;
        model.Position = new Vector3(1f, 0.5f, 2f);
        model.UpdateGeometry();

        // Aim rays at the (approximate) world centre of the bottom-anchored mesh.
        Vector3 totalRot = model.LocalRotate + Vector3.Zero;
        Vector3 target = (new Vector3(0f, model.Size.Y * 0.5f, 0f) * model.Scale).Rotate(totalRot) + model.Position;
        float radius = model.Size.Length() * model.Scale * 0.5f;
        Vector3 origin = target + new Vector3(6f, 2.5f, 6f);

        var rng = new Random(12345);
        int rays = 0, hits = 0, mismatches = 0, reported = 0;

        for (int i = 0; i < 4000; i++)
        {
            Vector3 aim;
            if (i % 8 == 0)
                aim = target + new Vector3(50f, 0f, -50f);   // far to the side -> guaranteed miss
            else
                aim = target + new Vector3(
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f,
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f,
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f);

            Vector3 dir = (aim - origin).Norm();
            var ray = new Ray(origin, dir);
            rays++;

            Object3d.UseBvh = false; var a = model.GetRenderData(ray);
            Object3d.UseBvh = true;  var b = model.GetRenderData(ray);

            bool ah = a.Intersection > -1, bh = b.Intersection > -1;
            if (ah) hits++;

            bool mismatch = ah != bh;
            if (!mismatch && ah && bh)
            {
                if (Math.Abs(a.Intersection - b.Intersection) > 1e-3f) mismatch = true;
                if (Math.Abs(a.Normal.X - b.Normal.X) > 1e-3f ||
                    Math.Abs(a.Normal.Y - b.Normal.Y) > 1e-3f ||
                    Math.Abs(a.Normal.Z - b.Normal.Z) > 1e-3f) mismatch = true;
                if (Math.Abs(a.IntersectionPoint.X - b.IntersectionPoint.X) > 1e-3f ||
                    Math.Abs(a.IntersectionPoint.Y - b.IntersectionPoint.Y) > 1e-3f ||
                    Math.Abs(a.IntersectionPoint.Z - b.IntersectionPoint.Z) > 1e-3f) mismatch = true;
            }

            if (mismatch)
            {
                mismatches++;
                if (reported < 5)
                {
                    reported++;
                    Console.WriteLine($"MISMATCH ray#{i}: dir=({dir.X:F3},{dir.Y:F3},{dir.Z:F3})");
                    Console.WriteLine($"  linear: hit={ah} t={a.Intersection:F5} n=({a.Normal.X:F3},{a.Normal.Y:F3},{a.Normal.Z:F3}) p=({a.IntersectionPoint.X:F3},{a.IntersectionPoint.Y:F3},{a.IntersectionPoint.Z:F3})");
                    Console.WriteLine($"  bvh:    hit={bh} t={b.Intersection:F5} n=({b.Normal.X:F3},{b.Normal.Y:F3},{b.Normal.Z:F3}) p=({b.IntersectionPoint.X:F3},{b.IntersectionPoint.Y:F3},{b.IntersectionPoint.Z:F3})");
                }
            }
        }

        Console.WriteLine($"Rays: {rays}, Hits: {hits}, Mismatches: {mismatches}");
        Console.WriteLine(mismatches == 0 ? "BVH SELF-TEST PASSED" : "BVH SELF-TEST FAILED");
        Object3d.UseBvh = true;
    }

    // Non-interactive check that the worlds engine resolves the platform-only default
    // world and that the mesh/primitive build paths work.
    static void WorldSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== WORLD SELF-TEST ===");

        WorldManager.EnsureDefault();

        var worlds = WorldManager.ListWorlds();
        Console.WriteLine($"Worlds found: {string.Join(", ", worlds)}");

        var world = WorldManager.Load("default");
        if (world == null) { Console.WriteLine("WORLD TEST FAILED: could not load 'default'."); return; }

        Console.WriteLine($"World '{world.Name}': shadows={world.Graphics.Shadows}, bvh={world.Graphics.Bvh}, " +
                          $"extraLight={world.Graphics.ExtraLight}, disableCameraLight={world.Graphics.DisableCameraLight}");
        Console.WriteLine($"Platform: enabled={world.Platform.Enabled}, size={world.Platform.Size}, color={world.Platform.Color}");
        Console.WriteLine($"Objects: {world.Objects.Count}");

        if (!world.Platform.Enabled) { Console.WriteLine("WORLD TEST FAILED: default platform not enabled."); return; }
        if (world.Objects.Count != 0) { Console.WriteLine("WORLD TEST FAILED: default world should have 0 objects."); return; }

        // Verify the mesh build path: pick the highest-tri available mesh, load raw + build BVH.
        var meshes = WorldManager.ListAvailableMeshes();
        if (meshes.Count == 0) { Console.WriteLine("WORLD TEST FAILED: no meshes available to verify build path."); return; }

        string best = "";
        Object3d? bestMesh = null;
        foreach (var name in meshes)
        {
            var m = ModelLoader.LoadRawMesh(AppPaths.ModelsFolder, name);
            if (m == null) continue;
            m.BuildAcceleration();
            if (bestMesh == null || m.FaceCount > bestMesh.FaceCount) { best = name; bestMesh = m; }
        }
        if (bestMesh == null) { Console.WriteLine("WORLD TEST FAILED: no mesh resolved."); return; }
        Console.WriteLine($"Mesh build OK: '{best}' -> {bestMesh.FaceCount} triangles, bvh={bestMesh.HasBvh}");

        // Confirm a primitive builds.
        var sphere = new Sphere(new Vector3(0, 0, 0), Vector3.Zero, 1f);
        Console.WriteLine($"Sphere build OK: r={sphere.R}");

        // Backward-compat: the on-disk default world has no Shape, so it loads as "square".
        if (world.Platform.Shape != "square")
        { Console.WriteLine($"WORLD TEST FAILED: default platform shape '{world.Platform.Shape}' != 'square'."); return; }

        // Round-trip the new platform fields (Shape + Width/Depth) through the world JSON
        // (in-memory, same options WorldManager uses for save/load — no test artifact on disk).
        var shaped = new WorldConfig
        {
            Name = "platshapetest",
            Platform = new PlatformConfig { Enabled = true, Shape = "rectangle", Size = 9f, Width = 14f, Depth = 6f, Color = "Cyan",
                Position = new Vec3Config { X = 3f, Y = 1f, Z = -2f } },
        };
        string shapedJson = System.Text.Json.JsonSerializer.Serialize(shaped, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>(shapedJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (reloaded == null) { Console.WriteLine("WORLD TEST FAILED: could not deserialize round-tripped world."); return; }
        var rp = reloaded.Platform;
        if (rp.Shape != "rectangle" || Math.Abs(rp.Size - 9f) > 1e-4f ||
            Math.Abs(rp.Width - 14f) > 1e-4f || Math.Abs(rp.Depth - 6f) > 1e-4f ||
            Math.Abs(rp.Position.X - 3f) > 1e-4f || Math.Abs(rp.Position.Y - 1f) > 1e-4f || Math.Abs(rp.Position.Z + 2f) > 1e-4f)
        { Console.WriteLine($"WORLD TEST FAILED: platform fields did not round-trip (shape={rp.Shape}, size={rp.Size}, w={rp.Width}, d={rp.Depth}, pos=({rp.Position.X},{rp.Position.Y},{rp.Position.Z}))."); return; }
        Console.WriteLine($"Platform round-trip OK: shape={rp.Shape}, size={rp.Size}, w={rp.Width}, d={rp.Depth}, pos=({rp.Position.X},{rp.Position.Y},{rp.Position.Z})");

        // Build a rectangle and a circle platform: each must produce real geometry.
        var rect = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "rectangle", Width = 14f, Depth = 6f });
        var disc = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "circle", Size = 8f });
        var square = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "square", Size = 10f });
        Console.WriteLine($"Platform geometry: square faces={square.FaceCount}, rectangle faces={rect.FaceCount}, circle faces={disc.FaceCount}");
        if (rect.FaceCount <= 0 || disc.FaceCount <= 0 || square.FaceCount <= 0)
        { Console.WriteLine("WORLD TEST FAILED: a platform shape built with no faces."); return; }

        // Ramp (wedge) primitive: a triangular prism — bottom(2) + slope(2) + back wall(2) + 2 end caps = 8 faces.
        var ramp = PriviewNetworkScene.CreateRamp();
        Console.WriteLine($"Ramp geometry: faces={ramp.FaceCount} (want 8)");
        if (ramp.FaceCount != 8) { Console.WriteLine($"WORLD TEST FAILED: ramp should have 8 faces, got {ramp.FaceCount}."); return; }

        Console.WriteLine("WORLD TEST PASSED");
    }

    // Visual + headless display check for 24-bit truecolor. Enables VT the SAME way the renderer
    // does, reports the console mode + truecolor env hints, prints labeled swatches and a blue ramp
    // using the SAME ESC[38;2;r;g;bm emission, and finally writes a raw blue swatch so its bytes can
    // be hexdumped. The user runs this to confirm the terminal renders blue (ruling out a VT issue).
    static void ColorSelfTest()
    {
        char esc = (char)27;
        ConsoleScreenAsync.EnableVirtualTerminal();   // exact renderer VT-enable path

        Console.WriteLine("=== COLOR TEST (24-bit truecolor) ===");

        uint mode = 0; bool gotMode = false;
        if (OperatingSystem.IsWindows())
            try { gotMode = GetConsoleMode(GetStdHandle(StdOut), out mode); } catch { }
        Console.WriteLine(gotMode
            ? $"console mode = 0x{mode:X4}  (ENABLE_VIRTUAL_TERMINAL_PROCESSING 0x4 -> {((mode & 0x4u) != 0 ? "SET" : "NOT set")})"
            : "console mode = (unavailable — stdout redirected or non-Windows; run live in a terminal to see it)");
        Console.WriteLine($"WT_SESSION={Environment.GetEnvironmentVariable("WT_SESSION")}  " +
                          $"COLORTERM={Environment.GetEnvironmentVariable("COLORTERM")}  " +
                          $"TERM_PROGRAM={Environment.GetEnvironmentVariable("TERM_PROGRAM")}");
        Console.WriteLine();

        void Swatch(string label, int r, int g, int b)
            => Console.WriteLine($"{label,-9} {esc}[38;2;{r};{g};{b}m████████{esc}[0m  (escape: 38;2;{r};{g};{b})");

        Swatch("RED",      255, 0,   0);
        Swatch("GREEN",    0,   255, 0);
        Swatch("BLUE",     0,   0,   255);
        Swatch("WHITE",    255, 255, 255);
        Swatch("CYAN",     0,   255, 255);
        Swatch("MAGENTA",  255, 0,   255);
        Swatch("YELLOW",   255, 255, 0);
        Swatch("DARKBLUE", 0,   0,   128);
        Swatch("DARKCYAN", 0,   128, 128);
        Console.WriteLine();

        Console.Write("BLUE RAMP ");
        for (int v = 0; v <= 255; v += 16)
            Console.Write($"{esc}[38;2;0;0;{v}m█");
        Console.WriteLine($"{esc}[0m");
        Console.WriteLine();

        // Raw blue swatch escape, written plainly so its bytes can be hexdumped (must carry 38;2;0;0;255).
        Console.WriteLine("raw BLUE swatch escape (hexdump the line below; bytes must contain 38;2;0;0;255):");
        Console.Out.Write($"{esc}[38;2;0;0;255m████{esc}[0m");
        Console.WriteLine();

        Console.WriteLine("COLOR TEST DONE — expect: PURE BLUE shows blue (not black), ramp goes dark->blue, CYAN != GREEN.");
    }

    private const int StdOut = -11;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    // Non-interactive round-trip of the editor's save-back conversion (FromInstance):
    // build a cube object from a descriptor, mutate its live Position, convert back, and
    // assert the result reflects the move while preserving Type/Color/Scale.
    static void EditorSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== EDITOR SELF-TEST ===");

        var descriptor = new WorldObject
        {
            Type = "cube",
            Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
            Scale = 2f,
            Color = "Magenta",
        };

        // Build the live instance the same way the scene does.
        Object3d cube = PriviewNetworkScene.CreateCube();
        cube.Position = new Vector3(descriptor.Position.X, descriptor.Position.Y, descriptor.Position.Z);
        cube.Scale = descriptor.Scale;
        cube.Color = PriviewNetworkScene.ParseColor(descriptor.Color, Rgba32.White);

        // Mutate the instance across every editable property (as the panel would).
        cube.Position += new Vector3(5f, 0f, 0f);
        cube.LocalRotate = new Vector3(0.25f, 0.5f, -0.75f);
        cube.Scale = 3.5f;
        cube.RotateSpeed = 1.25f;
        cube.Color = PriviewNetworkScene.ParseColor("Green", Rgba32.White);

        WorldObject back = PriviewNetworkScene.FromInstance(descriptor, cube);

        Console.WriteLine($"Back: type={back.Type}, pos=({back.Position.X},{back.Position.Y},{back.Position.Z}), " +
                          $"rot=({back.Rotation.X},{back.Rotation.Y},{back.Rotation.Z}), scale={back.Scale}, spin={back.RotateSpeed}, color={back.Color}");

        bool ok =
            back.Type == "cube" &&
            back.Mesh == null &&
            Math.Abs(back.Position.X - 6f) < 1e-4f &&
            Math.Abs(back.Position.Y - 2f) < 1e-4f &&
            Math.Abs(back.Position.Z - 3f) < 1e-4f &&
            Math.Abs(back.Rotation.X - 0.25f) < 1e-4f &&
            Math.Abs(back.Rotation.Y - 0.5f) < 1e-4f &&
            Math.Abs(back.Rotation.Z + 0.75f) < 1e-4f &&
            Math.Abs(back.Scale - 3.5f) < 1e-4f &&
            Math.Abs(back.RotateSpeed - 1.25f) < 1e-4f &&
            PriviewNetworkScene.ParseColor(back.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Green", Rgba32.White);

        // Full-RGBA round-trip: a NON-palette color WITH non-opaque alpha must survive the
        // Rgba32 -> hex -> Rgba32 path (alpha via "#C8327B80"), proving arbitrary 24-bit color + alpha
        // persists (not just named presets). Opaque colors must still emit "#RRGGBB" (no alpha byte).
        {
            cube.Color = new Rgba32(200, 50, 123, 128);
            WorldObject rgbBack = PriviewNetworkScene.FromInstance(descriptor, cube);
            bool rgbOk = PriviewNetworkScene.ParseColor(rgbBack.Color, Rgba32.White) == new Rgba32(200, 50, 123, 128);

            cube.Color = new Rgba32(200, 50, 123);   // opaque (A=255) -> "#RRGGBB", no alpha byte
            WorldObject opqBack = PriviewNetworkScene.FromInstance(descriptor, cube);
            bool opqOk = opqBack.Color == "#C8327B" &&
                         PriviewNetworkScene.ParseColor(opqBack.Color, Rgba32.White) == new Rgba32(200, 50, 123);

            Console.WriteLine($"  full-rgba: (200,50,123,128) -> hex={rgbBack.Color}; opaque -> {opqBack.Color} -> {(rgbOk && opqOk ? "ok" : "BAD")}");
            ok &= rgbOk && opqOk;
        }

        // Cover the generated primitives: each builds with geometry, and FromInstance round-trips
        // its Type/transform/color exactly like the cube (they ride the same editor/save/sync path).
        foreach (var (type, prim) in new (string, Object3d)[]
        {
            ("cylinder", PriviewNetworkScene.CreateCylinder()),
            ("cone",     PriviewNetworkScene.CreateCone()),
            ("pyramid",  PriviewNetworkScene.CreatePyramid()),
        })
        {
            var desc = new WorldObject { Type = type, Color = "White" };
            prim.Position = new Vector3(4f, 5f, 6f);
            prim.LocalRotate = new Vector3(0.1f, 0.2f, 0.3f);
            prim.Scale = 1.5f;
            prim.RotateSpeed = 0.7f;
            prim.Color = PriviewNetworkScene.ParseColor("Red", Rgba32.White);

            WorldObject b = PriviewNetworkScene.FromInstance(desc, prim);
            bool pok =
                prim.FaceCount > 0 &&
                b.Type == type && b.Mesh == null &&
                Math.Abs(b.Position.X - 4f) < 1e-4f && Math.Abs(b.Position.Y - 5f) < 1e-4f && Math.Abs(b.Position.Z - 6f) < 1e-4f &&
                Math.Abs(b.Rotation.X - 0.1f) < 1e-4f && Math.Abs(b.Rotation.Y - 0.2f) < 1e-4f && Math.Abs(b.Rotation.Z - 0.3f) < 1e-4f &&
                Math.Abs(b.Scale - 1.5f) < 1e-4f && Math.Abs(b.RotateSpeed - 0.7f) < 1e-4f &&
                PriviewNetworkScene.ParseColor(b.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Red", Rgba32.White);

            Console.WriteLine($"  {type}: faces={prim.FaceCount}, back type={b.Type}, scale={b.Scale}, color={b.Color} -> {(pok ? "ok" : "BAD")}");
            ok &= pok;
        }

        // Cover the new "light" object end-to-end through a real (offline) scene, one entry per
        // Kind: Start() builds a visible marker (FaceCount > 0) AND an engine Light of the right
        // Kind, paired in one EditEntry; FromInstance round-trips every light field (kind, direction,
        // cone, area size, power, position). (Platform off so the lights are the only entries.)
        {
            var lightWorld = new WorldConfig
            {
                Name = "lighttest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "point", Color = "White",
                        Position = new Vec3Config { X = 2f, Y = 3f, Z = -1f }, Power = 750f },
                    new WorldObject { Id = 1, Type = "light", LightKind = "directional", Color = "Cyan",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSpin = 0.5f },
                    new WorldObject { Id = 2, Type = "light", LightKind = "spot", Color = "Red",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 900f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 18f,
                        BeamCount = 4, ConeShape = "square" },
                    new WorldObject { Id = 3, Type = "light", LightKind = "area", Color = "Green",
                        Position = new Vec3Config { X = 1f, Y = 3f, Z = 2f }, Power = 600f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 2f },
                },
            };

            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), lightWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();

            var lightEntries = scene.EditableEntries.Where(e => e.Light != null).ToList();
            bool lok = lightEntries.Count == 4;
            if (!lok) Console.WriteLine($"  light: expected 4 light entries, got {lightEntries.Count} -> BAD");

            var expectKinds = new[] { LightKind.Point, LightKind.Directional, LightKind.Spot, LightKind.Area };
            for (int i = 0; lok && i < lightEntries.Count; i++)
            {
                var e = lightEntries[i];
                var orig = lightWorld.Objects[i];                 // the descriptor we fed in (round-trip target)
                var marker = e.Instance as Object3d;
                float wantInfluence = 0.05f + 0.2f * i;           // mutate per-light ColorInfluence; must round-trip
                e.Light!.ColorInfluence = wantInfluence;
                WorldObject lb = PriviewNetworkScene.FromInstance(e.Descriptor, e.Instance, e.Light);
                bool one =
                    marker != null && marker.FaceCount > 0 &&                       // marker is visible
                    e.Light!.Kind == expectKinds[i] &&                              // scene gained a Light of this kind
                    lb.Type == "light" && lb.Mesh == null &&
                    lb.LightKind == orig.LightKind &&                              // kind round-trips
                    PriviewNetworkScene.ParseColor(lb.Color, Rgba32.White) == PriviewNetworkScene.ParseColor(orig.Color, Rgba32.White) &&   // color round-trips
                    Math.Abs(lb.Direction.X - orig.Direction.X) < 1e-4f &&         // direction round-trips (already unit)
                    Math.Abs(lb.Direction.Y - orig.Direction.Y) < 1e-4f &&
                    Math.Abs(lb.Direction.Z - orig.Direction.Z) < 1e-4f &&
                    Math.Abs(lb.ConeAngle - orig.ConeAngle) < 1e-4f &&             // cone round-trips
                    Math.Abs(lb.LightSize - orig.LightSize) < 1e-4f &&            // area size round-trips
                    lb.BeamCount == orig.BeamCount &&                              // beam count round-trips
                    e.Light!.BeamCount == orig.BeamCount &&                        // ...and reached the engine Light
                    lb.ConeShape == orig.ConeShape &&                             // cone shape round-trips
                    Math.Abs(lb.Power - orig.Power) < 1e-4f &&                    // power round-trips
                    Math.Abs(lb.ColorInfluence - wantInfluence) < 1e-4f &&         // color-influence round-trips
                    Math.Abs(lb.Position.X - orig.Position.X) < 1e-4f &&          // marker position round-trips
                    Math.Abs(lb.Position.Y - orig.Position.Y) < 1e-4f &&
                    Math.Abs(lb.Position.Z - orig.Position.Z) < 1e-4f;
                Console.WriteLine($"  light[{i}] kind={e.Light!.Kind}: marker faces={marker?.FaceCount}, back kind={lb.LightKind}, cone={lb.ConeAngle}, size={lb.LightSize}, power={lb.Power} -> {(one ? "ok" : "BAD")}");
                lok &= one;
            }
            ok &= lok;
        }

        // Area emitter shapes — one Area light per AreaShape (circle/square/triangle): each builds a
        // visible flat marker (FaceCount > 0) and its AreaShape round-trips through FromInstance.
        {
            var areaWorld = new WorldConfig
            {
                Name = "areashapetest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "circle" },
                    new WorldObject { Id = 1, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "square" },
                    new WorldObject { Id = 2, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = 2f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "triangle" },
                },
            };

            var areaScene = new PriviewNetworkScene(new DisplayManagerAsync(), areaWorld, isServer: false, "127.0.0.1", 0, online: false);
            areaScene.Start();
            var areaEntries = areaScene.EditableEntries.Where(e => e.Light != null).ToList();
            bool aok = areaEntries.Count == 3;
            if (!aok) Console.WriteLine($"  area-shape: expected 3 area entries, got {areaEntries.Count} -> BAD");
            var expectShapes = new[] { "circle", "square", "triangle" };
            for (int i = 0; aok && i < areaEntries.Count; i++)
            {
                var e = areaEntries[i];
                var marker = e.Instance as Object3d;
                WorldObject ab = PriviewNetworkScene.FromInstance(e.Descriptor, e.Instance, e.Light);
                bool one = marker != null && marker.FaceCount > 0 &&
                           e.Light!.AreaShape == areaWorld.Objects[i].AreaShape switch { "circle" => ConeShapeKind.Circle, "triangle" => ConeShapeKind.Triangle, _ => ConeShapeKind.Square } &&
                           ab.AreaShape == expectShapes[i];   // shape round-trips
                Console.WriteLine($"  area-shape[{i}] {expectShapes[i]}: marker faces={marker?.FaceCount}, back AreaShape={ab.AreaShape} -> {(one ? "ok" : "BAD")}");
                aok &= one;
            }
            ok &= aok;
        }

        // Marker reflects BeamCount + Directional shaped cone — a 4-beam spot bakes 4 cones into its
        // marker (≈4x the faces of a 1-beam spot, same ConeShape); a Directional builds a shaped cone
        // marker per ConeShape. All markers must be non-empty.
        {
            var markerWorld = new WorldConfig
            {
                Name = "markertest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "spot", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = -4f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 20f, ConeShape = "circle", BeamCount = 1 },
                    new WorldObject { Id = 1, Type = "light", LightKind = "spot", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 20f, ConeShape = "circle", BeamCount = 4 },
                    new WorldObject { Id = 2, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = -4f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "circle" },
                    new WorldObject { Id = 3, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = 0f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "square" },
                    new WorldObject { Id = 4, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = 4f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "triangle" },
                },
            };

            var mScene = new PriviewNetworkScene(new DisplayManagerAsync(), markerWorld, isServer: false, "127.0.0.1", 0, online: false);
            mScene.Start();
            var mEntries = mScene.EditableEntries.Where(e => e.Light != null).ToList();
            bool mok = mEntries.Count == 5;
            if (!mok) Console.WriteLine($"  marker: expected 5 light entries, got {mEntries.Count} -> BAD");
            if (mok)
            {
                int spot1 = (mEntries[0].Instance as Object3d)!.FaceCount;
                int spot4 = (mEntries[1].Instance as Object3d)!.FaceCount;
                bool beamOk = spot1 > 0 && spot4 > 0 && spot4 > spot1;
                Console.WriteLine($"  marker: spot Beams=1 faces={spot1}, Beams=4 faces={spot4} (want 4>1, both>0) -> {(beamOk ? "ok" : "BAD")}");
                mok &= beamOk;

                foreach (var (idx, name) in new[] { (2, "circle"), (3, "square"), (4, "triangle") })
                {
                    int faces = (mEntries[idx].Instance as Object3d)!.FaceCount;
                    bool dok = faces > 0;
                    Console.WriteLine($"  marker: directional {name} faces={faces} (want >0) -> {(dok ? "ok" : "BAD")}");
                    mok &= dok;
                }
            }
            ok &= mok;
        }

        // Headless multi-beam check — a spot with BeamCount=4 fans four cones about the aim, so a
        // point sitting squarely in a FANNED (non-primary) beam is lit with Beams=4 but dark with
        // Beams=1 (the lone primary cone misses it). Aim +X (horizontal -> fans about world Y); the
        // k=1 beam points -Z, so a point straight down -Z from the light lands dead-center in it.
        {
            var mgr = new DisplayManagerAsync();
            var none = new List<IDisplays>();
            var P = new Vector3(0f, 0f, -3f);                                   // on the k=1 (-Z) fanned beam axis
            var rd = new RenderData(0f, (new Vector3(0f, 0f, 0f) - P).Norm(), P, Rgba32.White);
            var spot = new Light(new Vector3(0f, 0f, 0f), 500f)
            { Kind = LightKind.Spot, Direction = new Vector3(1f, 0f, 0f), ConeAngleDeg = 30f };

            spot.BeamCount = 1; float b1 = spot.Contribution(rd, none, mgr, shadows: false);
            spot.BeamCount = 4; float b4 = spot.Contribution(rd, none, mgr, shadows: false);
            bool bok = b1 == 0f && b4 > 0f;
            Console.WriteLine($"  beams: fanned point Beams=1 -> {b1:F3} (want 0), Beams=4 -> {b4:F3} (want >0) -> {(bok ? "ok" : "BAD")}");
            ok &= bok;
        }

        // Headless cone-shape check — a SQUARE cross-section lights its corners; the inscribed CIRCLE
        // does not. Aim +X, half-angle 30deg (t=tan30); a point at normalized cross-section offset
        // (0.8t, 0.8t) sits inside the square but past the circle's edge (sqrt(2)*0.8t > t).
        {
            var mgr = new DisplayManagerAsync();
            var none = new List<IDisplays>();
            float t = MathF.Tan(30f * MathF.PI / 180f);
            var P = new Vector3(1f, -0.8f * t, 0.8f * t);                       // u=+Z, v=-Y at axial 1
            var rd = new RenderData(0f, (new Vector3(0f, 0f, 0f) - P).Norm(), P, Rgba32.White);
            var spot = new Light(new Vector3(0f, 0f, 0f), 500f)
            { Kind = LightKind.Spot, Direction = new Vector3(1f, 0f, 0f), ConeAngleDeg = 30f, BeamCount = 1 };

            spot.ConeShape = ConeShapeKind.Circle; float circ = spot.Contribution(rd, none, mgr, shadows: false);
            spot.ConeShape = ConeShapeKind.Square; float sq = spot.Contribution(rd, none, mgr, shadows: false);
            bool sok = circ == 0f && sq > 0f;
            Console.WriteLine($"  shape: corner point Circle -> {circ:F3} (want 0), Square -> {sq:F3} (want >0) -> {(sok ? "ok" : "BAD")}");
            ok &= sok;
        }

        // Headless dominance/shadow check — the core multi-light fix. A surface point blocked from
        // light A but reached by an unblocked light B on the OTHER side must still be lit by B: each
        // light is shadow-tested independently and contributes additively (no global/min/multiplied
        // shadow factor). A occluded -> 0; B clear -> > 0; their sum stays > 0.
        {
            var mgr = new DisplayManagerAsync();
            var occluder = new Sphere(new Vector3(-2f, 2f, 0f), Vector3.Zero, 1f);  // sits between P and light A
            var objs = new List<IDisplays> { occluder };

            var rd = new RenderData(0f, new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 0f), Rgba32.White);  // up-facing point at origin
            var lightA = new Light(new Vector3(-4f, 4f, 0f), 500f);  // line of sight blocked by the sphere
            var lightB = new Light(new Vector3( 4f, 4f, 0f), 500f);  // clear line of sight

            float a = lightA.Contribution(rd, objs, mgr, shadows: true);
            float b = lightB.Contribution(rd, objs, mgr, shadows: true);

            bool dok = a == 0f && b > 0f && (a + b) > 0f;
            Console.WriteLine($"  dominance: blocked A={a:F3} (want 0), reached B={b:F3} (want >0), sum={(a + b):F3} -> {(dok ? "ok" : "BAD")}");
            ok &= dok;
        }

        // FindSortedIntersections — two spheres at different depths on one +X ray, passed in REVERSE
        // (far first); the manager must return them front-to-back (nearest Intersection first).
        {
            var mgr = new DisplayManagerAsync();
            var near = new Sphere(new Vector3(5f, 0f, 0f), Vector3.Zero, 1f);   // hit at ~4
            var far  = new Sphere(new Vector3(10f, 0f, 0f), Vector3.Zero, 1f);  // hit at ~9
            var objs = new List<IDisplays> { far, near };                       // reversed on purpose
            var ray = new Ray(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f).Norm());

            var hits = mgr.FindSortedIntersections(ray, objs);
            bool sok = hits.Count == 2 && hits[0].Intersection < hits[1].Intersection;
            Console.WriteLine($"  sorted: {hits.Count} hits, depths {(hits.Count > 0 ? hits[0].Intersection.ToString("F2") : "-")} < {(hits.Count > 1 ? hits[1].Intersection.ToString("F2") : "-")} (front-to-back) -> {(sok ? "ok" : "BAD")}");
            ok &= sok;
        }

        // Color round-trip — the BLUE/Z channel must survive ConsoleColor->RGB->Rgb24 (regression guard).
        {
            Vector3 blue = ColorRgb.ToRgb(ConsoleColor.Blue);
            Rgb24 unitBlue = Rgb24.FromUnit(new Vector3(0f, 0f, 1f));
            Rgb24 white = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.White));
            Rgb24 cyan = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.Cyan));
            Rgb24 magenta = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.Magenta));
            Console.WriteLine($"  color: ToRgb(Blue)=({blue.X:F2},{blue.Y:F2},{blue.Z:F2}); unitBlue=({unitBlue.R},{unitBlue.G},{unitBlue.B}); white=({white.R},{white.G},{white.B}); cyan=({cyan.R},{cyan.G},{cyan.B}); magenta=({magenta.R},{magenta.G},{magenta.B})");
            bool cok =
                blue.Z > 0.99f && blue.X < 0.01f && blue.Y < 0.01f &&
                unitBlue.R == 0 && unitBlue.G == 0 && unitBlue.B == 255 &&
                white.R == 255 && white.G == 255 && white.B == 255 &&
                cyan.R == 0 && cyan.G == 255 && cyan.B == 255 &&
                magenta.R == 255 && magenta.G == 0 && magenta.B == 255;
            Console.WriteLine($"  color: blue/Z survives RGB->Rgb24 -> {(cok ? "ok" : "BAD")}");
            ok &= cok;
        }

        // SurfaceTint combine — a colored light shows on a BLUE-LESS surface. Replicates Scene's new
        // hybrid combine (defaults SurfaceTint=0.4, Ambient=0.1, Exposure=0.05) on a yellow surface
        // (albedo (1,1,0), blue=0) lit by a pure-blue light: old pure-multiplicative blue was 0; the
        // hybrid lets blue through (> 0).
        {
            Vector3 albedo = ColorRgb.ToRgb(ConsoleColor.Yellow);   // (1,1,0): blue channel = 0
            Vector3 accum = new Vector3(0f, 0f, 10f);               // pure-blue light contribution
            const float tint = 0.4f, amb = 0.1f, exp = 0.05f;       // Scene defaults
            float oldBlue = albedo.Z * (amb + accum.Z * exp);       // pure multiplicative -> 0
            float tB = 1f - tint * (1f - albedo.Z);
            float newBlue = albedo.Z * amb + tB * accum.Z * exp;    // hybrid -> > 0
            bool stok = oldBlue == 0f && newBlue > 0f;
            Console.WriteLine($"  surfacetint: yellow surface + blue light -> oldBlue={oldBlue:F3} (was 0), newBlue={newBlue:F3} (>0) -> {(stok ? "ok" : "BAD")}");
            ok &= stok;
        }

        // Part B — the settings "extra light" injects as exactly ONE real editable light object on the
        // authority/solo, and is idempotent (a world that already has a light is not doubled).
        {
            var injWorld = new WorldConfig
            {
                Name = "extralight-inject",
                Graphics = new GraphicsConfig { ExtraLight = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>(),   // no lights yet -> one should be injected
            };
            var s1 = new PriviewNetworkScene(new DisplayManagerAsync(), injWorld, isServer: false, "127.0.0.1", 0, online: false);
            s1.Start();
            var le = s1.EditableEntries.FirstOrDefault(e => e.Light != null);
            int lights1 = s1.EditableEntries.Count(e => e.Light != null);
            bool injOk = lights1 == 1 && le != null && (le.Instance as Object3d)?.FaceCount > 0;
            Console.WriteLine($"  extralight-inject: light entries={lights1} (want 1), marker faces={(le?.Instance as Object3d)?.FaceCount} -> {(injOk ? "ok" : "BAD")}");
            ok &= injOk;

            // Idempotent: ExtraLight on, but the world already contains a light -> no second injection.
            var dupWorld = new WorldConfig
            {
                Name = "extralight-idempotent",
                Graphics = new GraphicsConfig { ExtraLight = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Type = "light", LightKind = "point", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 500f },
                },
            };
            var s2 = new PriviewNetworkScene(new DisplayManagerAsync(), dupWorld, isServer: false, "127.0.0.1", 0, online: false);
            s2.Start();
            int lights2 = s2.EditableEntries.Count(e => e.Light != null);
            bool dupOk = lights2 == 1;
            Console.WriteLine($"  extralight-idempotent: light entries={lights2} (want 1, no double) -> {(dupOk ? "ok" : "BAD")}");
            ok &= dupOk;
        }

        // Part C — the PLATFORM is an editable entry (selectable like any object), backed by the live
        // PlatformConfig, but never leaks into the saved/synced Objects list. An offline scene with a
        // known platform + 2 ordinary objects: exactly one "platform" entry (a real Object3d floor);
        // the non-platform entries match world.Objects; the live snapshot carries the platform via
        // Platform (NOT Objects); and a live Shape edit propagates to the snapshot (save/sync path).
        {
            var platWorld = new WorldConfig
            {
                Name = "platedit",
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 7f, Color = "Cyan" },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "cube", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                    new WorldObject { Id = 1, Type = "sphere", Color = "Red", Radius = 1f,
                        Position = new Vec3Config { X = 3f, Y = 0f, Z = 0f } },
                },
            };

            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), platWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();

            var platEntries = scene.EditableEntries.Where(e => e.Platform != null).ToList();
            int platTyped = scene.EditableEntries.Count(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            var platEntry = platEntries.FirstOrDefault();
            var platFloor = platEntry?.Instance as Object3d;
            bool pCount = platEntries.Count == 1 && platTyped == 1 && platEntry != null && platFloor != null && platFloor.FaceCount > 0;
            Console.WriteLine($"  platform: editable entries={platEntries.Count} (want 1), typed=platform={platTyped}, floor faces={platFloor?.FaceCount} -> {(pCount ? "ok" : "BAD")}");
            ok &= pCount;

            int nonPlat = scene.EditableEntries.Count(e => !string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool pNon = nonPlat == platWorld.Objects.Count;
            Console.WriteLine($"  platform: non-platform entries={nonPlat} (want {platWorld.Objects.Count}) -> {(pNon ? "ok" : "BAD")}");
            ok &= pNon;

            var snap = scene.LiveWorldSnapshot();
            bool snapLeak = snap.Objects.Any(o => string.Equals(o.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool pSnap =
                snap.Objects.Count == platWorld.Objects.Count &&            // platform absent from Objects
                !snapLeak &&                                                // ...and nothing typed platform leaked
                snap.Platform.Shape == "square" &&                          // platform rides through Platform
                Math.Abs(snap.Platform.Size - 7f) < 1e-4f &&
                PriviewNetworkScene.ParseColor(snap.Platform.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Cyan", Rgba32.White);
            Console.WriteLine($"  platform: snapshot Objects={snap.Objects.Count} (want {platWorld.Objects.Count}), leak={snapLeak}, plat shape={snap.Platform.Shape}, size={snap.Platform.Size}, color={snap.Platform.Color} -> {(pSnap ? "ok" : "BAD")}");
            ok &= pSnap;

            // A live platform edit (Shape) must propagate to the save/sync snapshot.
            if (platEntry?.Platform != null) platEntry.Platform.Shape = "circle";
            string snapShape = scene.LiveWorldSnapshot().Platform.Shape;
            bool pEdit = snapShape == "circle";
            Console.WriteLine($"  platform: live Shape edit -> snapshot Platform.Shape={snapShape} (want circle) -> {(pEdit ? "ok" : "BAD")}");
            ok &= pEdit;

            // MOVE: moving the floor instance propagates to the snapshot (synced at snapshot time).
            if (platEntry != null) platEntry.Instance.Position = new Vector3(2f, 1f, -3f);
            var movedPos = scene.LiveWorldSnapshot().Platform.Position;
            bool pMove = Math.Abs(movedPos.X - 2f) < 1e-4f && Math.Abs(movedPos.Y - 1f) < 1e-4f && Math.Abs(movedPos.Z + 3f) < 1e-4f;
            Console.WriteLine($"  platform: move -> snapshot Platform.Position=({movedPos.X},{movedPos.Y},{movedPos.Z}) (want 2,1,-3) -> {(pMove ? "ok" : "BAD")}");
            ok &= pMove;

            // B-identity: every editable — INCLUDING the platform — has a stable id >= 0, ids are unique,
            // and the platform is no longer the -1 sentinel.
            var idsAll = scene.EditableEntries.Select(e => e.Descriptor.Id).ToList();
            bool nonNeg = idsAll.All(id => id >= 0);
            bool unique = idsAll.Distinct().Count() == idsAll.Count;
            var platB = scene.EditableEntries.First(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool idOk = nonNeg && unique && platB.Descriptor.Id >= 0;
            Console.WriteLine($"  identity: ids=[{string.Join(",", idsAll)}] nonNeg={nonNeg}, unique={unique}, platformId={platB.Descriptor.Id} (was -1) -> {(idOk ? "ok" : "BAD")}");
            ok &= idOk;

            // Derived system name "{type} #{id}" for an unnamed object (and the platform).
            var cubeB = scene.EditableEntries.First(e => string.Equals(e.Descriptor.Type, "cube", StringComparison.OrdinalIgnoreCase));
            string cubeSys = PriviewNetworkScene.DisplayName(cubeB);
            string platSys = PriviewNetworkScene.DisplayName(platB);
            bool sysOk = cubeSys == $"cube #{cubeB.Descriptor.Id}" && platSys == "platform #0";
            Console.WriteLine($"  identity: system names cube='{cubeSys}', platform='{platSys}' -> {(sysOk ? "ok" : "BAD")}");
            ok &= sysOk;

            // A user Name overrides the system name and rides the live snapshot (save/sync path) for both
            // an ordinary object (via FromInstance/descriptor) and the platform (via PlatformConfig).
            cubeB.Descriptor.Name = "hero box";
            if (platB.Platform != null) platB.Platform.Name = "the stage";
            var liveW = scene.LiveWorldSnapshot();
            var cubeObj = liveW.Objects.First(o => o.Id == cubeB.Descriptor.Id);
            bool nameRt = cubeObj.Name == "hero box" && liveW.Platform.Name == "the stage"
                       && PriviewNetworkScene.DisplayName(cubeB) == "hero box";
            Console.WriteLine($"  identity: name round-trip cube='{cubeObj.Name}', platform='{liveW.Platform.Name}' -> {(nameRt ? "ok" : "BAD")}");
            ok &= nameRt;
        }

        // Part C2 — a DISABLED (i.e. deleted) platform reloads as ABSENT: no "platform" entry, and the
        // live snapshot keeps Platform.Enabled == false. This is the persistence contract behind delete.
        {
            var offWorld = new WorldConfig
            {
                Name = "platoff",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>(),
            };
            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), offWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();
            int platTyped = scene.EditableEntries.Count(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool snapEnabled = scene.LiveWorldSnapshot().Platform.Enabled;
            bool pOff = platTyped == 0 && !snapEnabled;
            Console.WriteLine($"  platform-off: platform entries={platTyped} (want 0), snapshot Enabled={snapEnabled} (want False) -> {(pOff ? "ok" : "BAD")}");
            ok &= pOff;
        }

        // Part D — a SPOT light's marker reflects its cone cross-section: each ConeShape (circle/square/
        // triangle) builds a real cone marker (FaceCount > 0). Visual correctness is verified live.
        {
            var spotWorld = new WorldConfig
            {
                Name = "spotshapes",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "spot", Color = "White", ConeShape = "circle",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 25f },
                    new WorldObject { Id = 1, Type = "light", LightKind = "spot", Color = "Red", ConeShape = "square",
                        Position = new Vec3Config { X = 0f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 35f },
                    new WorldObject { Id = 2, Type = "light", LightKind = "spot", Color = "Cyan", ConeShape = "triangle",
                        Position = new Vec3Config { X = 2f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 45f },
                },
            };
            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), spotWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();
            var spots = scene.EditableEntries.Where(e => e.Light?.Kind == LightKind.Spot).ToList();
            bool sok = spots.Count == 3;
            foreach (var e in spots)
            {
                var marker = e.Instance as Object3d;
                bool one = marker != null && marker.FaceCount > 0;
                Console.WriteLine($"  spot-marker shape={e.Light!.ConeShape}: marker faces={marker?.FaceCount} -> {(one ? "ok" : "BAD")}");
                sok &= one;
            }
            ok &= sok;
        }

        // B-camera — the local player is BODY + CAMERA. (1) The rig math: FIRSTPERSON camera == body;
        // THIRDPERSON is behind + above along the look; toggling moves the camera while the body input is
        // unchanged. (2) The body is a real object with a valid id + system name "player #<id>", NOT in
        // editables (it's the player, not world content), and the default 1st-person camera sits at the body.
        {
            var body = new Vector3(3f, 1.5f, -2f);
            var look = new Vector3(0f, 0.7f, 0f);   // yaw 0.7 rad
            var camFp = PriviewNetworkScene.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.FirstPerson);
            var camTp = PriviewNetworkScene.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.ThirdPerson);
            Vector3 fwd = new Vector3(1, 0, 0).Rotate(new Vector3(0, look.Y, 0));
            Vector3 wantTp = body + fwd * (-4f) + new Vector3(0, 1.5f, 0);   // ThirdPersonBack=4, Up=1.5
            bool fpOk = (camFp - body).Length() < 1e-4f;                     // 1st person: camera AT the body
            bool tpOk = (camTp - wantTp).Length() < 1e-4f;                   // 3rd person: exact behind + above
            bool behindAbove = camTp.Y > body.Y + 0.5f && (camTp - body).Length() > 1f;
            bool moves = (camTp - camFp).Length() > 1f;                      // toggling modes moves the camera
            bool rigOk = fpOk && tpOk && behindAbove && moves;
            Console.WriteLine($"  camera-rig: 1p@body={fpOk}, 3p behind+above exact={tpOk} (Y up={behindAbove}), toggle moves cam={moves} -> {(rigOk ? "ok" : "BAD")}");
            ok &= rigOk;

            var camWorld = new WorldConfig { Name = "camtest", Platform = new PlatformConfig { Enabled = true }, Objects = new List<WorldObject> { new WorldObject { Id = 0, Type = "cube" } } };
            var camScene = new PriviewNetworkScene(new DisplayManagerAsync(), camWorld, isServer: false, "127.0.0.1", 0, online: false);
            camScene.Start();
            bool bodyIdOk = camScene.LocalBodyId >= 0 && camScene.LocalBodyName == $"player #{camScene.LocalBodyId}";
            bool excluded = !camScene.LocalBodyInEditables;
            bool camAtBody = (camScene.CameraPosition - camScene.LocalBodyPosition).Length() < 1e-4f
                          && camScene.CurrentCameraMode == PriviewNetworkScene.CameraMode.FirstPerson;
            bool bodyOk = bodyIdOk && excluded && camAtBody;
            Console.WriteLine($"  camera-body: id={camScene.LocalBodyId} name='{camScene.LocalBodyName}', excluded-from-editables={excluded}, default 1p cam@body={camAtBody} -> {(bodyOk ? "ok" : "BAD")}");
            ok &= bodyOk;
        }

        // B-direct-input — TYPED direct entry for numeric fields (Enter → type → parse + CLAMP). Drives the
        // real BeginFieldEntry/TryAppendEntryChar/Confirm|Cancel path via the headless test hooks: a valid
        // value sets exactly, an out-of-range value CLAMPS to the field's N/M range, an invalid/empty entry
        // and Esc both leave the value UNCHANGED; enum/toggle fields reject typing but still cycle on N/M;
        // and the relocated spawn action still works.
        {
            var typeWorld = new WorldConfig
            {
                Name = "directinput",
                Graphics = new GraphicsConfig { },
                Physics = new PhysicsConfig { CollisionEnabled = true },   // so the Collider enum can cycle
                Platform = new PlatformConfig { Enabled = false },         // cube is the only editable -> index 0
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 2f },
                },
            };
            var s = new PriviewNetworkScene(new DisplayManagerAsync(), typeWorld, isServer: false, "127.0.0.1", 0, online: false);
            s.Start();
            var cubeI = s.EditableEntries[0].Instance as Object3d;
            var inst = s.EditableEntries[0].Instance;
            bool dvin = cubeI != null && Math.Abs(cubeI!.Scale - 2f) < 1e-4f;   // baseline
            if (!dvin) Console.WriteLine($"  direct-input: baseline cube scale={cubeI?.Scale} (want 2) -> BAD");

            // (1) Valid typed value sets exactly.
            bool r1 = s.TypeFieldForTest(0, "Scale", "3.5", confirm: true);
            bool set1 = r1 && Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: type Scale='3.5' -> {cubeI!.Scale:F2} (want 3.50) -> {(set1 ? "ok" : "BAD")}");
            dvin &= set1;

            // (2) Invalid entry (letters are filtered out -> empty buffer) leaves the value UNCHANGED.
            s.TypeFieldForTest(0, "Scale", "abc", confirm: true);
            bool inv = Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            // ...and a lone '-' (parses to nothing) is also ignored.
            s.TypeFieldForTest(0, "Scale", "-", confirm: true);
            inv &= Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: invalid 'abc'/'-' -> scale stays {cubeI!.Scale:F2} (want 3.50) -> {(inv ? "ok" : "BAD")}");
            dvin &= inv;

            // (3) Esc (confirm:false) cancels — value unchanged.
            s.TypeFieldForTest(0, "Scale", "99", confirm: false);
            bool esc = Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: type '99' + Esc -> scale stays {cubeI!.Scale:F2} (want 3.50) -> {(esc ? "ok" : "BAD")}");
            dvin &= esc;

            // (4) Out-of-range CLAMPS to the field's N/M range (Scale >= 0.01; Mass >= 0.1; Friction <= 2;
            //     ColorR <= 255).
            s.TypeFieldForTest(0, "Scale", "-5", confirm: true);
            bool clScale = Math.Abs(cubeI!.Scale - 0.01f) < 1e-4f;
            s.TypeFieldForTest(0, "Mass", "-3", confirm: true);
            bool clMass = Math.Abs(inst.Mass - 0.1f) < 1e-4f;
            s.TypeFieldForTest(0, "Friction", "9", confirm: true);
            bool clFric = Math.Abs(inst.Friction - 2f) < 1e-4f;
            s.TypeFieldForTest(0, "ColorR", "300", confirm: true);
            bool clR = inst.Color.R == 255;
            bool clamp = clScale && clMass && clFric && clR;
            Console.WriteLine($"  direct-input: clamp Scale(-5)->{cubeI!.Scale:F2}(0.01) Mass(-3)->{inst.Mass:F2}(0.1) Friction(9)->{inst.Friction:F2}(2) ColorR(300)->{inst.Color.R}(255) -> {(clamp ? "ok" : "BAD")}");
            dvin &= clamp;

            // (5) A precise decimal + a free (unclamped) field both set exactly.
            s.TypeFieldForTest(0, "PosX", "12.25", confirm: true);
            s.TypeFieldForTest(0, "ColorFade", "0.5", confirm: true);
            bool prec = Math.Abs(inst.Position.X - 12.25f) < 1e-4f && Math.Abs(inst.ColorFade - 0.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: PosX='12.25'->{inst.Position.X:F2}, ColorFade='0.5'->{inst.ColorFade:F2} -> {(prec ? "ok" : "BAD")}");
            dvin &= prec;

            // (6) Enum/toggle field: N/M still cycles it, but typing is rejected (returns false) and leaves
            //     it unchanged. Collider AABB -> OBB via N/M; typing "1" into it does nothing.
            var before = inst.Collider;
            s.StepFieldForTest(0, "Collider", +1);
            var afterStep = inst.Collider;
            bool cycled = before == ColliderShape.Aabb && afterStep == ColliderShape.Obb;
            bool typedEnum = s.TypeFieldForTest(0, "Collider", "1", confirm: true);   // must return false
            bool enumUnchanged = inst.Collider == afterStep;
            bool enumOk = cycled && !typedEnum && enumUnchanged;
            Console.WriteLine($"  direct-input: enum Collider N/M {before}->{afterStep} (cycled={cycled}), type rejected={!typedEnum}, unchanged={enumUnchanged} -> {(enumOk ? "ok" : "BAD")}");
            dvin &= enumOk;

            // (7) The relocated spawn action still works (the [B] key calls SpawnCurrent).
            int countBefore = s.EditableEntries.Count;
            int countAfter = s.SpawnForTest();
            bool spawnOk = countAfter == countBefore + 1;
            Console.WriteLine($"  direct-input: spawn -> editables {countBefore}->{countAfter} (want +1) -> {(spawnOk ? "ok" : "BAD")}");
            dvin &= spawnOk;

            ok &= dvin;
        }

        Console.WriteLine(ok ? "EDITOR TEST PASSED" : "EDITOR TEST FAILED");
    }

    // Non-interactive check of the raycast pick: two spheres along +X, a ray down +X picks
    // the nearer one; a ray pointing away picks nothing. Reuses PriviewNetworkScene.PickNearest.
    static void PickSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== PICK SELF-TEST ===");

        var near = new PriviewNetworkScene.EditEntry
        {
            Descriptor = new WorldObject { Type = "sphere", Radius = 1f },
            Instance = new Sphere(new Vector3(5f, 0f, 0f), Vector3.Zero, 1f),
        };
        var far = new PriviewNetworkScene.EditEntry
        {
            Descriptor = new WorldObject { Type = "sphere", Radius = 1f },
            Instance = new Sphere(new Vector3(10f, 0f, 0f), Vector3.Zero, 1f),
        };
        var editables = new List<PriviewNetworkScene.EditEntry> { far, near }; // far first on purpose

        var towards = new Ray(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f).Norm());
        var away = new Ray(new Vector3(0f, 0f, 0f), new Vector3(-1f, 0f, 0f).Norm());

        int hit = PriviewNetworkScene.PickNearest(towards, editables);
        int miss = PriviewNetworkScene.PickNearest(away, editables);

        Console.WriteLine($"Aiming +X -> index {hit} (expect 1, the nearer sphere at X=5)");
        Console.WriteLine($"Aiming -X -> index {miss} (expect -1, no hit)");

        bool ok = hit == 1 && miss == -1;
        Console.WriteLine(ok ? "PICK TEST PASSED" : "PICK TEST FAILED");
    }

    // Non-interactive round-trip of the world-transfer format: build a world (mesh + cube +
    // sphere), Pack it, push the packet through its own serialize -> deserialize (bytes),
    // Unpack, then assert the config and the monkey .obj text are recovered faithfully.
    static void WorldSyncSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== WORLD SYNC SELF-TEST ===");

        void Fail(string reason) => Console.WriteLine($"WORLD SYNC TEST FAILED: {reason}");

        var world = new WorldConfig
        {
            Name = "synctest",
            Graphics = new GraphicsConfig { Shadows = true, Bvh = false, ExtraLight = true, DisableCameraLight = true, Renderer = "gpu" },
            Platform = new PlatformConfig { Enabled = true, Name = "the floor", Size = 12f, Color = "Cyan", Collides = false, Gravity = true, Position = new Vec3Config { X = 1.5f, Y = -0.5f, Z = 2.5f } },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 12.5f, CollisionEnabled = false, Restitution = 0.4f },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 10, Type = "mesh", Mesh = "monkey",
                    Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
                    Rotation = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                    Scale = 1.5f, Color = "Red", Anchor = "Center", RotateSpeed = 0.5f, Radius = 1f, Collider = "obb" },
                new WorldObject { Id = 11, Type = "cube", Name = "my crate",
                    Position = new Vec3Config { X = -1f, Y = 0f, Z = 2f },
                    Scale = 2f, Color = "Green", Collides = false, Gravity = true, Mass = 3.5f, Restitution = 0.8f, Friction = 0.35f, RollingFriction = 0.12f, ColorFade = 0.5f, Texture = "brick.png", TextureScale = 2f, TextureFace = 4, TextureFilter = 1 },
                new WorldObject { Id = 12, Type = "sphere",
                    Position = new Vec3Config { X = 4f, Y = 1f, Z = -2f },
                    Radius = 2.5f, Color = "Blue" },
                new WorldObject { Id = 13, Type = "light",
                    Position = new Vec3Config { X = 5f, Y = 4f, Z = 1f },
                    Power = 800f, Color = "Magenta", LightKind = "spot",
                    Direction = new Vec3Config { X = 0.5f, Y = -1f, Z = 0.25f },
                    ConeAngle = 22f, LightSize = 1.5f, LightSpin = 0.75f,
                    BeamCount = 3, ConeShape = "triangle", ColorInfluence = 0.85f, ColorFade = 0.4f },
                new WorldObject { Id = 14, Type = "light",
                    Position = new Vec3Config { X = -3f, Y = 5f, Z = -1f },
                    Power = 650f, Color = "Cyan", LightKind = "area",
                    Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f },
                    LightSize = 2f, AreaShape = "triangle" },
                new WorldObject { Id = 15, Type = "flatpicture",
                    Position = new Vec3Config { X = 2f, Y = 1.5f, Z = -3f },
                    Rotation = new Vec3Config { X = 0f, Y = 1.57f, Z = 0f },
                    Scale = 1.8f, Color = "Yellow", ColorFade = 0.2f, Collides = false, Texture = "poster.png" },
            }
        };

        // Pack, then round-trip the packet through its own byte (de)serialization. (This world references
        // brick.png/poster.png which aren't on disk, so TextureData stays empty here — the texture-BYTES
        // streaming is exercised in its own blocks below with real on-disk PNGs.)
        WorldSyncPacket packet = WorldSync.Pack(world, AppPaths.ModelsFolder, AppPaths.TexturesFolder);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var w = new BinaryWriter(ms)) packet.Serialize(w);
            bytes = ms.ToArray();
        }
        Console.WriteLine($"Packet size: {bytes.Length} bytes ({world.Objects.Count} objects, {packet.MeshTexts.Count} mesh file(s), {packet.TextureData.Count} inline texture(s)).");

        var received = new WorldSyncPacket();
        using (var r = new BinaryReader(new MemoryStream(bytes))) received.Deserialize(r);

        var (back, meshTexts, _) = WorldSync.Unpack(received);

        // Compare the recovered config field-by-field.
        string? reason = CompareWorlds(world, back);
        if (reason != null) { Fail(reason); return; }

        // Mesh text must round-trip and match the on-disk file exactly.
        string monkeyPath = Path.Combine(AppPaths.ModelsFolder, "monkey.obj");
        if (!meshTexts.TryGetValue("monkey", out var recoveredObj) || string.IsNullOrEmpty(recoveredObj))
        { Fail("recovered mesh text for 'monkey' is missing/empty."); return; }
        if (recoveredObj != File.ReadAllText(monkeyPath))
        { Fail("recovered 'monkey' .obj text does not match the on-disk file."); return; }

        Console.WriteLine($"Recovered: name={back.Name}, objects={back.Objects.Count}, monkey .obj chars={recoveredObj.Length}");

        // WorldSettings delta: round-trip the packet through its OWN serialize/deserialize, then
        // re-parse the JSON payloads and assert the PlatformConfig + GraphicsConfig survive intact.
        {
            var settings = new WorldSettingsPacket
            {
                PlatformJson = JsonSerializer.Serialize(world.Platform),
                GraphicsJson = JsonSerializer.Serialize(world.Graphics),
            };
            byte[] sbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) settings.Serialize(w);
                sbytes = ms.ToArray();
            }
            var recvSettings = new WorldSettingsPacket();
            using (var r = new BinaryReader(new MemoryStream(sbytes))) recvSettings.Deserialize(r);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var pb = JsonSerializer.Deserialize<PlatformConfig>(recvSettings.PlatformJson, opts);
            var gb = JsonSerializer.Deserialize<GraphicsConfig>(recvSettings.GraphicsJson, opts);
            bool settingsOk =
                pb != null && gb != null &&
                pb.Enabled == world.Platform.Enabled && pb.Shape == world.Platform.Shape &&
                Math.Abs(pb.Size - world.Platform.Size) < 1e-4f && pb.Color == world.Platform.Color &&
                Math.Abs(pb.Position.X - world.Platform.Position.X) < 1e-4f &&
                Math.Abs(pb.Position.Y - world.Platform.Position.Y) < 1e-4f &&
                Math.Abs(pb.Position.Z - world.Platform.Position.Z) < 1e-4f &&
                gb.Shadows == world.Graphics.Shadows && gb.Bvh == world.Graphics.Bvh &&
                gb.ExtraLight == world.Graphics.ExtraLight && gb.DisableCameraLight == world.Graphics.DisableCameraLight &&
                gb.Renderer == world.Graphics.Renderer;
            Console.WriteLine($"WorldSettings round-trip: {sbytes.Length} bytes, platform shape={pb?.Shape} size={pb?.Size} color={pb?.Color}, graphics shadows={gb?.Shadows} bvh={gb?.Bvh} -> {(settingsOk ? "ok" : "BAD")}");
            if (!settingsOk) { Fail("WorldSettings packet round-trip lost platform/graphics fields."); return; }
        }

        // PlayerLeft delta: round-trip the packet through its OWN serialize/deserialize; NetId survives.
        {
            var left = new PlayerLeftPacket { NetId = 4242 };
            byte[] lbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) left.Serialize(w);
                lbytes = ms.ToArray();
            }
            var recvLeft = new PlayerLeftPacket();
            using (var r = new BinaryReader(new MemoryStream(lbytes))) recvLeft.Deserialize(r);
            bool leftOk = recvLeft.NetId == 4242;
            Console.WriteLine($"PlayerLeft round-trip: {lbytes.Length} bytes, NetId={recvLeft.NetId} -> {(leftOk ? "ok" : "BAD")}");
            if (!leftOk) { Fail("PlayerLeft packet round-trip lost NetId."); return; }
        }

        // MeshChunk delta: round-trip the packet through its OWN serialize/deserialize; fields survive.
        {
            var chunk = new MeshChunkPacket { MeshName = "bigmesh", Index = 3, Total = 7, Data = "v 1.0 2.0 3.0\n" };
            byte[] cbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) chunk.Serialize(w);
                cbytes = ms.ToArray();
            }
            var recvChunk = new MeshChunkPacket();
            using (var r = new BinaryReader(new MemoryStream(cbytes))) recvChunk.Deserialize(r);
            bool chunkOk = recvChunk.MeshName == "bigmesh" && recvChunk.Index == 3 && recvChunk.Total == 7 && recvChunk.Data == "v 1.0 2.0 3.0\n";
            Console.WriteLine($"MeshChunk round-trip: {cbytes.Length} bytes, name={recvChunk.MeshName} idx={recvChunk.Index}/{recvChunk.Total} -> {(chunkOk ? "ok" : "BAD")}");
            if (!chunkOk) { Fail("MeshChunk packet round-trip lost fields."); return; }
        }

        // PhysicsSync delta: round-trip the compact state batch (id + pos + FULL linVel + rot + angVel per entry).
        {
            var ps = new PhysicsSyncPacket
            {
                Ids = new[] { 7, 42 },
                Positions = new[] { new Vector3(1f, 2f, 3f), new Vector3(-4f, 5.5f, 6f) },
                LinVel = new[] { new Vector3(1.2f, -9.8f, 0.3f), Vector3.Zero },
                Rotations = new[] { new Vector3(0.1f, 0.2f, 0.3f), new Vector3(-0.4f, 0.5f, -0.6f) },
                AngVel = new[] { new Vector3(1.5f, -2.5f, 0.5f), new Vector3(0f, 3f, -1f) },
            };
            byte[] pbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) ps.Serialize(w);
                pbytes = ms.ToArray();
            }
            var recv = new PhysicsSyncPacket();
            using (var r = new BinaryReader(new MemoryStream(pbytes))) recv.Deserialize(r);
            bool psOk = recv.Ids.Length == 2 && recv.Ids[0] == 7 && recv.Ids[1] == 42
                && Math.Abs(recv.Positions[1].Y - 5.5f) < 1e-5f
                && Math.Abs(recv.LinVel[0].X - 1.2f) < 1e-5f && Math.Abs(recv.LinVel[0].Y - (-9.8f)) < 1e-5f && Math.Abs(recv.LinVel[0].Z - 0.3f) < 1e-5f
                && recv.LinVel[1].Length() == 0f
                && Math.Abs(recv.Rotations[0].Y - 0.2f) < 1e-5f && Math.Abs(recv.Rotations[1].Z - (-0.6f)) < 1e-5f
                && Math.Abs(recv.AngVel[0].X - 1.5f) < 1e-5f && Math.Abs(recv.AngVel[1].Y - 3f) < 1e-5f;
            Console.WriteLine($"PhysicsSync round-trip: {pbytes.Length} bytes, n={recv.Ids.Length}, pos1.Y={recv.Positions[1].Y:F2}, linVel0=({recv.LinVel[0].X:F1},{recv.LinVel[0].Y:F1},{recv.LinVel[0].Z:F1}), rot0.Y={recv.Rotations[0].Y:F2}, angvel0.X={recv.AngVel[0].X:F2} -> {(psOk ? "ok" : "BAD")}");
            if (!psOk) { Fail("PhysicsSync packet round-trip lost fields."); return; }
        }

        // Split/reassemble: cut a long string into MeshChunkSize pieces, reassemble by Index, compare.
        {
            const int chunkSize = 16384;   // mirrors PriviewNetworkScene.MeshChunkSize
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40000; i++) sb.Append((char)('a' + (i % 26)));
            string original = sb.ToString();

            int total = (original.Length + chunkSize - 1) / chunkSize;
            var parts = new string[total];
            for (int i = 0; i < total; i++)
            {
                int start = i * chunkSize;
                parts[i] = original.Substring(start, Math.Min(chunkSize, original.Length - start));
            }
            string reassembled = string.Concat(parts);
            bool splitOk = total == 3 && reassembled == original;   // 40000 -> ceil(40000/16384) = 3 chunks
            Console.WriteLine($"MeshChunk split: len={original.Length} -> {total} chunks, reassembled match={reassembled == original} -> {(splitOk ? "ok" : "BAD")}");
            if (!splitOk) { Fail("MeshChunk split/reassemble mismatch."); return; }
        }

        // ---- Network TEXTURE sync (A1): stream PNG bytes so a peer without the file still sees it ----

        // A distinct 2x2 RGBA image (all channels used) shared by the texture blocks below.
        var texPixels = new[]
        {
            new Rgba32(10, 20, 30, 255), new Rgba32(200, 40, 60, 128),
            new Rgba32(70, 180, 90, 200), new Rgba32(15, 25, 240, 90),
        };

        // (1) Texture round-trip: a world referencing an on-disk PNG -> Pack reads its BYTES inline ->
        // Serialize -> Deserialize -> Unpack yields the identical bytes, which re-decode to the texture.
        {
            Directory.CreateDirectory(AppPaths.TexturesFolder);
            string texName = "__sync_tex_roundtrip__.png";
            string texPath = Path.Combine(AppPaths.TexturesFolder, texName);
            byte[] png = PngEncode(2, 2, texPixels, 6, 0);
            try
            {
                File.WriteAllBytes(texPath, png);
                var texWorld = new WorldConfig
                {
                    Name = "textest",
                    Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Texture = texName } },
                };
                var tp = WorldSync.Pack(texWorld, AppPaths.ModelsFolder, AppPaths.TexturesFolder);
                bool packedInline = tp.TextureData.TryGetValue(texName, out var packedBytes) && packedBytes!.SequenceEqual(png);

                byte[] tb;
                using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) tp.Serialize(w); tb = ms.ToArray(); }
                var tr = new WorldSyncPacket();
                using (var r = new BinaryReader(new MemoryStream(tb))) tr.Deserialize(r);
                var (_, _, texData) = WorldSync.Unpack(tr);

                bool survived = texData.TryGetValue(texName, out var recv) && recv!.SequenceEqual(png);
                var decoded = PngDecoder.Decode(recv!);
                bool decodeOk = decoded.Width == 2 && decoded.Height == 2;
                bool ok = packedInline && survived && decodeOk;
                Console.WriteLine($"Texture round-trip: packed inline={packedInline}, {png.Length} bytes survive={survived}, decode={decoded.Width}x{decoded.Height} -> {(ok ? "ok" : "BAD")}");
                if (!ok) { Fail("Texture round-trip lost/altered the PNG bytes."); return; }
            }
            finally { try { File.Delete(texPath); } catch { } }
        }

        // (2) TextureChunk round-trip: the binary chunk packet survives its own serialize/deserialize.
        {
            var tc = new TextureChunkPacket { TextureName = "big.png", Index = 2, Total = 5, Data = new byte[] { 0, 255, 1, 254, 128, 64, 200 } };
            byte[] cbytes;
            using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) tc.Serialize(w); cbytes = ms.ToArray(); }
            var recv = new TextureChunkPacket();
            using (var r = new BinaryReader(new MemoryStream(cbytes))) recv.Deserialize(r);
            bool tcOk = recv.TextureName == "big.png" && recv.Index == 2 && recv.Total == 5 && recv.Data.SequenceEqual(tc.Data);
            Console.WriteLine($"TextureChunk round-trip: {cbytes.Length} bytes, name={recv.TextureName} idx={recv.Index}/{recv.Total} data[{recv.Data.Length}] -> {(tcOk ? "ok" : "BAD")}");
            if (!tcOk) { Fail("TextureChunk packet round-trip lost fields."); return; }
        }

        // (3) TextureChunk split/reassemble: cut a > threshold byte[] at the chunk size, reassemble by
        // Index, assert byte-identical (PNGs are usually > 49 KB, so this is the common path).
        {
            const int chunkSize = 16384;      // mirrors PriviewNetworkScene.MeshChunkSize
            const int threshold = 49152;      // mirrors the inline threshold
            byte[] original = new byte[60000]; // deterministic > threshold payload (4 chunks)
            for (int i = 0; i < original.Length; i++) original[i] = (byte)((i * 7 + 3) & 0xFF);

            int total = (original.Length + chunkSize - 1) / chunkSize;
            var parts = new byte[total][];
            for (int i = 0; i < total; i++)
            {
                int start = i * chunkSize;
                int len = Math.Min(chunkSize, original.Length - start);
                parts[i] = new byte[len];
                Array.Copy(original, start, parts[i], 0, len);
            }
            byte[] reassembled = new byte[original.Length];
            int off = 0;
            foreach (var p in parts) { Array.Copy(p, 0, reassembled, off, p.Length); off += p.Length; }
            bool splitOk = original.Length > threshold && total == 4 && reassembled.SequenceEqual(original);
            Console.WriteLine($"TextureChunk split: len={original.Length} (> {threshold}) -> {total} chunks, reassembled match={reassembled.SequenceEqual(original)} -> {(splitOk ? "ok" : "BAD")}");
            if (!splitOk) { Fail("TextureChunk split/reassemble mismatch."); return; }
        }

        // (4) Peer-without-file: the peer's OWN textures/ lacks the file, but the streamed bytes land in
        // received/textures/ -> loading from there attaches the texture (non-null, correct WxH), while the
        // default folder still returns null (proving it truly wasn't local).
        {
            Directory.CreateDirectory(AppPaths.ReceivedTexturesFolder);
            string name = "__peer_recv_tex__.png";
            string absentPath = Path.Combine(AppPaths.TexturesFolder, name);
            string recvPath = Path.Combine(AppPaths.ReceivedTexturesFolder, name);
            byte[] png = PngEncode(2, 2, texPixels, 6, 0);
            try
            {
                try { if (File.Exists(absentPath)) File.Delete(absentPath); } catch { }   // ensure the peer lacks it locally
                File.WriteAllBytes(recvPath, png);                                          // the streamed bytes arrive here

                var fromReceived = TextureLoader.Get(AppPaths.ReceivedTexturesFolder, name);
                var fromDefault = TextureLoader.Get(AppPaths.TexturesFolder, name);
                bool peerOk = fromReceived != null && fromReceived.Width == 2 && fromReceived.Height == 2 && fromDefault == null;
                Console.WriteLine($"Peer-without-file: received-folder load={(fromReceived != null ? $"{fromReceived.Width}x{fromReceived.Height}" : "NULL")}, default-folder load={(fromDefault == null ? "null (absent)" : "present")} -> {(peerOk ? "ok" : "BAD")}");
                if (!peerOk) { Fail("Peer-without-file: streamed texture did not attach from received/textures."); return; }
            }
            finally { try { File.Delete(recvPath); } catch { } }
        }

        Console.WriteLine("WORLD SYNC TEST PASSED");
    }

    // Returns null if the two worlds match (within float epsilon), else a reason string.
    // Headless end-to-end check of the Stage-1 texture pipeline: decode a KNOWN PNG (encoded in-test
    // with the BCL's ZLibStream so we feed the decoder real, valid PNGs), sample a Texture (nearest +
    // wrap), interpolate UVs on a triangle, and shade a textured BOX — asserting exact texels throughout.
    static void TextureSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== TEXTURE SELF-TEST ===");
        bool ok = true;
        static bool Approx(float a, float b) => MathF.Abs(a - b) < 1e-4f;

        // 1) DECODE — a known 2x2 RGB image (TL red, TR green, BL blue, BR yellow) round-trips exactly.
        Rgba32 red = new(255, 0, 0), green = new(0, 255, 0), blue = new(0, 0, 255), yellow = new(255, 255, 0);
        Rgba32[] rgb = { red, green, blue, yellow };
        var d = PngDecoder.Decode(PngEncode(2, 2, rgb, 2, 0));
        bool decRgb = d.Width == 2 && d.Height == 2 &&
            d.Pixels[0] == red && d.Pixels[1] == green && d.Pixels[2] == blue && d.Pixels[3] == yellow;
        Console.WriteLine($"  decode RGB 2x2: {d.Width}x{d.Height}, pixels match -> {(decRgb ? "ok" : "BAD")}");
        ok &= decRgb;

        // RGBA (colour type 6) with distinct alphas round-trips including the alpha channel.
        Rgba32[] rgba = { new(10, 20, 30, 40), new(50, 60, 70, 80), new(90, 100, 110, 120), new(130, 140, 150, 160) };
        var d2 = PngDecoder.Decode(PngEncode(2, 2, rgba, 6, 0));
        bool decRgba = d2.Width == 2 && d2.Height == 2;
        for (int i = 0; i < 4; i++) decRgba &= d2.Pixels[i] == rgba[i];
        Console.WriteLine($"  decode RGBA 2x2 (with alpha): pixels match -> {(decRgba ? "ok" : "BAD")}");
        ok &= decRgba;

        // Every PNG filter (1 Sub, 2 Up, 3 Average, 4 Paeth) un-filters back to the same pixels.
        bool filters = true;
        for (int ft = 1; ft <= 4; ft++)
        {
            var df = PngDecoder.Decode(PngEncode(2, 2, rgba, 6, ft));
            for (int i = 0; i < 4; i++) filters &= df.Pixels[i] == rgba[i];
        }
        Console.WriteLine($"  un-filter Sub/Up/Average/Paeth -> {(filters ? "ok" : "BAD")}");
        ok &= filters;

        // Unsupported variants are rejected with a CLEAR error (not silently corrupted).
        bool rejCt = false, rejBd = false;
        try { PngDecoder.Decode(PngBad(8, 3)); } catch (NotSupportedException ex) { rejCt = ex.Message.Contains("colour type"); }
        try { PngDecoder.Decode(PngBad(16, 2)); } catch (NotSupportedException ex) { rejBd = ex.Message.Contains("bit depth"); }
        Console.WriteLine($"  reject colour-type-3 -> {(rejCt ? "ok" : "BAD")}; reject 16-bit -> {(rejBd ? "ok" : "BAD")}");
        ok &= rejCt && rejBd;

        // 2) SAMPLE — nearest-neighbour at corners/centre, and WRAP outside [0,1).
        var tex = new Texture(2, 2, rgb);
        bool corners = tex.Sample(0f, 0f) == red && tex.Sample(0.6f, 0f) == green &&
                       tex.Sample(0f, 0.6f) == blue && tex.Sample(0.6f, 0.6f) == yellow;
        bool wrap = tex.Sample(1.0f, 0f) == red && tex.Sample(1.6f, 0.6f) == yellow && tex.Sample(-0.4f, 0f) == green;
        Console.WriteLine($"  sample corners -> {(corners ? "ok" : "BAD")}; wrap (repeat) -> {(wrap ? "ok" : "BAD")}");
        ok &= corners && wrap;

        // 2b) BILINEAR SAMPLE (A2) — the 2x2 image is TL red, TR green, BL blue, BR yellow; alphas equal.
        //   - at a texel edge (fu=fv=0) it equals nearest;  - halfway between two texels = their average;
        //   - at the centre of the 4 texels = their mean;  - WRAP: the last row blends with the first.
        byte al = red.A;
        bool blCentre = tex.SampleBilinear(0f, 0f) == tex.Sample(0f, 0f) && tex.SampleBilinear(0f, 0f) == red;
        bool blBetween = tex.SampleBilinear(0.25f, 0f) == new Rgba32(128, 128, 0, al);       // avg(red, green)
        bool blQuad = tex.SampleBilinear(0.25f, 0.25f) == new Rgba32(128, 128, 64, al);       // mean(red, green, blue, yellow)
        bool blWrap = tex.SampleBilinear(0f, 0.75f) == new Rgba32(128, 0, 128, al);           // avg(blue, wrapped red)
        Console.WriteLine($"  bilinear: centre=nearest -> {(blCentre ? "ok" : "BAD")}; between=avg -> {(blBetween ? "ok" : "BAD")}; " +
                          $"4-centre=mean -> {(blQuad ? "ok" : "BAD")}; wrap edge -> {(blWrap ? "ok" : "BAD")}");
        ok &= blCentre && blBetween && blQuad && blWrap;

        // 3) UV-INTERP — a triangle with corner UVs (0,0)/(1,0)/(0,1), ray hitting bary (0.8,0.1).
        var triV = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        var tri = new Triangle(new int[] { 0, 1, 2 },
            new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
        var triRd = tri.GetRenderData(new Ray(new Vector3(0.8f, 0.1f, 5f), new Vector3(0, 0, -1f)), triV, Vector3.Zero);
        bool uvInterp = triRd.Intersection > -1f && Approx(triRd.Uv.X, 0.8f) && Approx(triRd.Uv.Y, 0.1f);
        Console.WriteLine($"  uv-interp: hit uv=({triRd.Uv.X:F3},{triRd.Uv.Y:F3}) want (0.800,0.100) -> {(uvInterp ? "ok" : "BAD")}");
        ok &= uvInterp;

        // 4) END-TO-END — a textured cube; a ray hits the +Z face at (0.5,-0.2,1) => uv (0.75,0.40).
        const int W = 8, H = 8;
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = new Rgba32((byte)(x * 32), (byte)(y * 32), 128, 255);   // every texel distinct
        var tex4 = new Texture(W, H, px, "unit.png");
        var box = PriviewNetworkScene.CreateCube();
        box.Texture = tex4;   // Color defaults White, ColorFade 0 -> the shaded colour is the raw texel
        var boxRd = box.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        float eu = (0.5f + 1f) * 0.5f, ev = (-0.2f + 1f) * 0.5f;     // +Z face maps (x,y) -> ((x+1)/2,(y+1)/2)
        Rgba32 texel = tex4.Sample(eu, ev);
        Rgba32 expected = box.ShadeTexel(texel);
        bool e2e = boxRd.Intersection > -1f && Approx(boxRd.Uv.X, eu) && Approx(boxRd.Uv.Y, ev) &&
                   boxRd.Color == expected && expected == texel;
        Console.WriteLine($"  end-to-end box: uv=({boxRd.Uv.X:F3},{boxRd.Uv.Y:F3}) want ({eu:F3},{ev:F3}); " +
                          $"colour=({boxRd.Color.R},{boxRd.Color.G},{boxRd.Color.B}) want texel ({texel.R},{texel.G},{texel.B}) -> {(e2e ? "ok" : "BAD")}");
        ok &= e2e;

        // 5) PRIMITIVE UVs (Stage 3) — ramp + pyramid generate procedural per-corner UVs. They interpolate
        // linearly/barycentrically like the cube, so their generated corner UVs must match the hand-derived
        // per-face unwrap exactly (and they get BIT-EXACT GPU parity — proven by gputest).
        static bool UvEq(Vector2 uv, float u, float v) => Approx(uv.X, u) && Approx(uv.Y, v);

        var ramp = PriviewNetworkScene.CreateRamp();
        var rf = ramp.Faces;
        bool rampUv =
            UvEq(rf[0].Uv0, 0, 1) && UvEq(rf[0].Uv1, 0, 0) && UvEq(rf[0].Uv2, 1, 0) &&       // bottom (-Y): drop Y
            UvEq(rf[2].Uv0, 1, 0) && UvEq(rf[2].Uv1, 1, 1) && UvEq(rf[2].Uv2, 0, 1) &&       // sloped top: depth×rise
            UvEq(rf[6].Uv0, 0, 0) && UvEq(rf[6].Uv1, 1, 0) && UvEq(rf[6].Uv2, 1, 1);         // front cap (+Z): drop Z
        Console.WriteLine($"  ramp UVs (bottom/slope/front-cap) -> {(rampUv ? "ok" : "BAD")}");
        ok &= rampUv;

        var pyr = PriviewNetworkScene.CreatePyramid();
        var pf = pyr.Faces;
        bool pyrUv =
            UvEq(pf[0].Uv0, 0, 0) && UvEq(pf[0].Uv1, 1, 0) && UvEq(pf[0].Uv2, 1, 1) &&       // base (-Y): drop Y
            UvEq(pf[2].Uv0, 0, 0) && UvEq(pf[2].Uv1, 0.5f, 1) && UvEq(pf[2].Uv2, 1, 0);      // a side: draped triangle
        Console.WriteLine($"  pyramid UVs (base/side) -> {(pyrUv ? "ok" : "BAD")}");
        ok &= pyrUv;

        // 6) SPHERE UV (Stage 3) — equirectangular map at the cardinal directions + the -X seam (u wraps
        // 0↔1) + both poles, and that LocalRotate rotates the mapping (a +X world normal on a Y-90° sphere
        // reads the texel that +Z reads unrotated). Matches the GPU kernel's SphereUv but for the atan2/asin band.
        bool sphereUv =
            UvEq(Sphere.EquirectangularUv(new Vector3(1, 0, 0)), 0.5f, 0.5f) &&    // +X equator
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 0, 1)), 0.75f, 0.5f) &&   // +Z
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 0, -1)), 0.25f, 0.5f) &&  // -Z
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 1, 0)), 0.5f, 0f) &&      // +Y north pole
            UvEq(Sphere.EquirectangularUv(new Vector3(0, -1, 0)), 0.5f, 1f) &&     // -Y south pole
            UvEq(Sphere.EquirectangularUv(new Vector3(-1, 0, 0)), 1f, 0.5f);       // -X seam
        bool sphereRot = UvEq(
            Sphere.EquirectangularUv(new Vector3(1, 0, 0).RotateInverse(new Vector3(0, MathF.PI / 2f, 0))), 0.75f, 0.5f);
        Console.WriteLine($"  sphere UV cardinals+seam+poles -> {(sphereUv ? "ok" : "BAD")}; rotates with object -> {(sphereRot ? "ok" : "BAD")}");
        ok &= sphereUv && sphereRot;

        // 7) FLAT PICTURE (Stage 3b) — a two-sided vertical quad whose four corners map the FULL texture
        // (0,0)/(1,0)/(0,1)/(1,1), right-side-up. Front AND back faces (reversed winding) both carry UVs
        // (mirrored on the back). Linear interp → BIT-EXACT GPU parity (proven by gputest).
        var pic = PriviewNetworkScene.CreateFlatPicture();
        var cf = pic.Faces;
        bool picUv =
            cf.Count == 4 &&                                                              // 2 front + 2 back tris (two-sided)
            UvEq(cf[0].Uv0, 0, 1) && UvEq(cf[0].Uv1, 1, 1) && UvEq(cf[0].Uv2, 1, 0) &&    // front: BL,BR,TR
            UvEq(cf[1].Uv0, 0, 1) && UvEq(cf[1].Uv1, 1, 0) && UvEq(cf[1].Uv2, 0, 0) &&    // front: BL,TR,TL
            UvEq(cf[2].Uv0, 0, 1) && UvEq(cf[2].Uv1, 1, 0) && UvEq(cf[2].Uv2, 1, 1);      // back (reversed): BL,TR,BR
        Console.WriteLine($"  flat-picture UVs (front+back corners, {cf.Count} tris) -> {(picUv ? "ok" : "BAD")}");
        ok &= picUv;

        // 8) CYLINDER + CONE (Stage-3 tail) — procedural UVs added because the engine's PER-FACE-CORNER
        // UV model needs NO seam-vertex duplication: side u = angle fraction (v: top 0, bottom 1), caps
        // wrap the ring onto a disc (centre 0.5,0.5). Linear interp → bit-exact parity like the others.
        var cyl = PriviewNetworkScene.CreateCylinder();
        var yf = cyl.Faces;
        bool cylUv =
            UvEq(yf[0].Uv0, 0f, 1f) && UvEq(yf[0].Uv1, 1f / 16f, 0f) && UvEq(yf[0].Uv2, 1f / 16f, 1f) &&   // first side-quad tri
            UvEq(yf[2].Uv0, 0.5f, 0.5f) && UvEq(yf[2].Uv2, 1f, 0.5f);                                       // top-cap centre + ring @angle0
        var cone = PriviewNetworkScene.CreateCone();
        var nf = cone.Faces;
        bool coneUv =
            UvEq(nf[0].Uv0, 0f, 1f) && UvEq(nf[0].Uv1, 1f / 32f, 0f) && UvEq(nf[0].Uv2, 1f / 16f, 1f) &&    // side: base→apex-midpoint→base
            UvEq(nf[1].Uv0, 0.5f, 0.5f) && UvEq(nf[1].Uv1, 1f, 0.5f);                                        // base-cap centre + ring @angle0
        Console.WriteLine($"  cylinder/cone UVs (side+cap) -> {((cylUv && coneUv) ? "ok" : "BAD")}");
        ok &= cylUv && coneUv;

        // 9) TEXTURE PARAMS (Stage 4) — face-group tagging, UV scale/tiling, per-face gating, options list.
        // (a) The cube's 12 triangles tag into 6 groups (0..5 = +X,-X,+Y,-Y,+Z,-Z), two triangles each.
        var pcube = PriviewNetworkScene.CreateCube();
        var counts = new int[6];
        bool groupsInRange = true;
        foreach (var t in pcube.Faces) { if (t.Group >= 0 && t.Group < 6) counts[t.Group]++; else groupsInRange = false; }
        bool cubeGroups = groupsInRange;
        for (int gi = 0; gi < 6; gi++) if (counts[gi] != 2) cubeGroups = false;
        Console.WriteLine($"  cube face-groups: [{string.Join(",", counts)}] want all 2 -> {(cubeGroups ? "ok" : "BAD")}");
        ok &= cubeGroups;

        // (b) Scale tiling: the +Z hit at (0.5,-0.2,1) → uv (0.75,0.40); with TextureScale=2 the sampled
        // texel is tex4.Sample(0.75*2, 0.40*2) — wrap tiles it. (tex4/eu/ev are from section 4.)
        var scube = PriviewNetworkScene.CreateCube();
        scube.Texture = tex4; scube.TextureScale = 2f;
        var scubeRd = scube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        Rgba32 scaledTexel = scube.ShadeTexel(tex4.Sample(eu * 2f, ev * 2f));
        bool scaleTiling = scubeRd.Intersection > -1f && scubeRd.Color == scaledTexel && scaledTexel != tex4.Sample(eu, ev);
        Console.WriteLine($"  scale tiling (x2): colour=({scubeRd.Color.R},{scubeRd.Color.G},{scubeRd.Color.B}) want ({scaledTexel.R},{scaledTexel.G},{scaledTexel.B}) -> {(scaleTiling ? "ok" : "BAD")}");
        ok &= scaleTiling;

        // (c) Per-face gate: with TextureFace=+Z (group 4) the +Z hit is textured; set to +X (group 0) and
        // the SAME +Z hit shows flat colour instead.
        var fcube = PriviewNetworkScene.CreateCube();
        fcube.Texture = tex4; fcube.Color = new Rgba32(30, 60, 90);
        fcube.TextureFace = 4;
        var fOn = fcube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        bool faceOn = fOn.Intersection > -1f && fOn.Group == 4 && fOn.Color == fcube.ShadeTexel(tex4.Sample(eu, ev));
        fcube.TextureFace = 0;
        var fOff = fcube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        bool faceOff = fOff.Intersection > -1f && fOff.Color == fcube.EffectiveColor;
        Console.WriteLine($"  texture-face gate: +Z textured -> {(faceOn ? "ok" : "BAD")}; +Z flat when face=+X -> {(faceOff ? "ok" : "BAD")}");
        ok &= faceOn && faceOff;

        // (d) Face options: a cube exposes 6 sides; a sphere (analytic, whole) exposes none.
        int cubeOpts = PriviewNetworkScene.TextureFaceOptions("cube").Length;
        int sphOpts = PriviewNetworkScene.TextureFaceOptions("sphere").Length;
        bool faceOpts = cubeOpts == 6 && sphOpts == 0;
        Console.WriteLine($"  face options: cube={cubeOpts} (want 6), sphere={sphOpts} (want 0) -> {(faceOpts ? "ok" : "BAD")}");
        ok &= faceOpts;

        // 10) IMPORTED .OBJ UVs (Stage 5) — ObjLoader now parses `vt` and feeds per-corner UVs with the
        // OBJ→image v-flip (v_tex = 1 - v_obj). A 2-tri quad with known vt: verts 1..4 at vt (0,0)(1,0)(1,1)(0,1)
        // → after the flip the corners read (0,1)(1,1)(1,0)(0,0); the fan tris are (1,2,3) and (1,3,4).
        const string quadObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nv 0 1 -1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn -1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n";
        var quadMesh = ObjLoader.Load(WriteObjFixture("uvquad", quadObj));
        var qf = quadMesh.Faces;
        bool objUv = qf.Count == 2 &&
            UvEq(qf[0].Uv0, 0, 1) && UvEq(qf[0].Uv1, 1, 1) && UvEq(qf[0].Uv2, 1, 0) &&   // tri (v1,v2,v3)
            UvEq(qf[1].Uv0, 0, 1) && UvEq(qf[1].Uv1, 1, 0) && UvEq(qf[1].Uv2, 0, 0);     // tri (v1,v3,v4)
        Console.WriteLine($"  imported .obj UVs (v-flipped, {qf.Count} tris) -> {(objUv ? "ok" : "BAD")}");
        ok &= objUv;

        // A vt-less .obj yields Zero UVs (untextured meshes stay byte-identical).
        const string plainObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nvn -1 0 0\nf 1//1 2//1 3//1\n";
        var plainMesh = ObjLoader.Load(WriteObjFixture("nouvquad", plainObj));
        bool objNoUv = plainMesh.Faces.Count == 1 &&
            UvEq(plainMesh.Faces[0].Uv0, 0, 0) && UvEq(plainMesh.Faces[0].Uv1, 0, 0) && UvEq(plainMesh.Faces[0].Uv2, 0, 0);
        Console.WriteLine($"  vt-less .obj -> Zero UVs -> {(objNoUv ? "ok" : "BAD")}");
        ok &= objNoUv;

        // 11) REAL DISK PATH (diagnostics) — exercise the actual disk→decode→attach chain in the REAL
        // AppPaths.TexturesFolder. A known-good 8-bit RGBA PNG must load; a JPEG-magic file renamed .png
        // must fail with a message that NAMES it as a JPEG. Uses unique names + cleans up after itself.
        Console.WriteLine($"  resolved textures folder: {AppPaths.TexturesFolder}");
        Directory.CreateDirectory(AppPaths.TexturesFolder);
        string goodName = "__texload_selftest__.png";
        string jpgName = "__texload_jpeg__.png";
        string goodPath = Path.Combine(AppPaths.TexturesFolder, goodName);
        string jpgPath = Path.Combine(AppPaths.TexturesFolder, jpgName);
        try
        {
            File.WriteAllBytes(goodPath, PngEncode(2, 2, rgba, 6, 0));                          // real 8-bit RGBA PNG
            File.WriteAllBytes(jpgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1 });  // JPEG/JFIF magic

            var loaded = TextureLoader.Get(goodName);
            bool diskLoad = loaded != null && loaded.Width == 2 && loaded.Height == 2;
            Console.WriteLine($"  disk load (real loader): '{goodName}' -> {(loaded != null ? $"{loaded.Width}x{loaded.Height}" : "NULL")} -> {(diskLoad ? "ok" : "BAD")}");
            ok &= diskLoad;

            string jpgReason = "";
            try { PngDecoder.Decode(File.ReadAllBytes(jpgPath)); }
            catch (Exception ex) { jpgReason = ex.Message; }
            bool jpgClear = jpgReason.Contains("JPEG", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  jpeg-as-png names the format: \"{jpgReason}\" -> {(jpgClear ? "ok" : "BAD")}");
            ok &= jpgClear;
        }
        finally
        {
            try { File.Delete(goodPath); } catch { }
            try { File.Delete(jpgPath); } catch { }
        }

        Console.WriteLine(ok ? "TEXTURE TEST PASSED" : "TEXTURE TEST FAILED");
    }

    // Writes a fixture .obj into a temp folder and returns its full path so ObjLoader.Load can read it.
    // Used by the texture self-tests to exercise the .obj `vt` parsing without polluting the models/ library.
    static string WriteObjFixture(string name, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nova_tex_fixtures");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".obj");
        File.WriteAllText(path, content);
        return path;
    }

    // --- tiny PNG ENCODER (test-only): produce a real, valid PNG so the decoder is exercised end-to-end.
    // Applies one filter type to every scanline; compresses via ZLibStream (zlib header + Adler-32); writes
    // IHDR/IDAT/IEND with correct CRC-32s. colorType 2 = RGB, 6 = RGBA; bit depth fixed at 8.
    static byte[] PngEncode(int w, int h, Rgba32[] pixels, int colorType, int filterType)
    {
        int channels = colorType == 6 ? 4 : 3;
        int stride = w * channels;
        byte[] raw = new byte[h * (stride + 1)];
        byte[] prev = new byte[stride];
        int rp = 0;
        for (int y = 0; y < h; y++)
        {
            byte[] cur = new byte[stride];
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = pixels[y * w + x];
                int o = x * channels;
                cur[o] = p.R; cur[o + 1] = p.G; cur[o + 2] = p.B;
                if (channels == 4) cur[o + 3] = p.A;
            }
            raw[rp++] = (byte)filterType;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? cur[x - channels] : 0;
                int b = prev[x];
                int c = x >= channels ? prev[x - channels] : 0;
                int fv = filterType switch
                {
                    1 => cur[x] - a,
                    2 => cur[x] - b,
                    3 => cur[x] - (a + b) / 2,
                    4 => cur[x] - PngPaeth(a, b, c),
                    _ => cur[x],
                };
                raw[rp++] = (byte)(fv & 0xFF);
            }
            prev = cur;
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        byte[] ihdr = new byte[13];
        PngPutBE32(ihdr, 0, w); PngPutBE32(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = (byte)colorType;   // [10] compression, [11] filter, [12] interlace all 0

        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        PngWriteChunk(outMs, "IHDR", ihdr);
        PngWriteChunk(outMs, "IDAT", idat);
        PngWriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    // A structurally valid PNG whose IHDR declares an UNSUPPORTED format (the decoder must reject it at
    // the IHDR check, before inflating — so the IDAT contents are irrelevant).
    static byte[] PngBad(int bitDepth, int colorType)
    {
        byte[] ihdr = new byte[13];
        PngPutBE32(ihdr, 0, 1); PngPutBE32(ihdr, 4, 1);
        ihdr[8] = (byte)bitDepth; ihdr[9] = (byte)colorType;
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[] { 0, 0 }, 0, 2);
            idat = ms.ToArray();
        }
        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        PngWriteChunk(outMs, "IHDR", ihdr);
        PngWriteChunk(outMs, "IDAT", idat);
        PngWriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    static void PngWriteChunk(MemoryStream s, string type, byte[] data)
    {
        byte[] lenb = new byte[4]; PngPutBE32(lenb, 0, data.Length); s.Write(lenb, 0, 4);
        byte[] typeb = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeb, 0, typeb.Length);
        s.Write(data, 0, data.Length);
        byte[] crcIn = new byte[typeb.Length + data.Length];
        Buffer.BlockCopy(typeb, 0, crcIn, 0, typeb.Length);
        Buffer.BlockCopy(data, 0, crcIn, typeb.Length, data.Length);
        byte[] crcb = new byte[4]; PngPutBE32(crcb, 0, unchecked((int)PngCrc32(crcIn))); s.Write(crcb, 0, 4);
    }

    static uint PngCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte bb in data)
        {
            crc ^= bb;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    static int PngPaeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    static void PngPutBE32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)((v >> 24) & 0xFF);
        buf[off + 1] = (byte)((v >> 16) & 0xFF);
        buf[off + 2] = (byte)((v >> 8) & 0xFF);
        buf[off + 3] = (byte)(v & 0xFF);
    }

    static string? CompareWorlds(WorldConfig a, WorldConfig b)
    {
        const float eps = 1e-4f;
        bool Eq(float x, float y) => Math.Abs(x - y) < eps;

        if (a.Name != b.Name) return $"name '{a.Name}' != '{b.Name}'";
        if (a.Graphics.Shadows != b.Graphics.Shadows || a.Graphics.Bvh != b.Graphics.Bvh ||
            a.Graphics.ExtraLight != b.Graphics.ExtraLight || a.Graphics.DisableCameraLight != b.Graphics.DisableCameraLight ||
            a.Graphics.Renderer != b.Graphics.Renderer)
            return "graphics flags differ";
        if (a.Platform.Name != b.Platform.Name ||
            a.Platform.Enabled != b.Platform.Enabled || !Eq(a.Platform.Size, b.Platform.Size) ||
            PriviewNetworkScene.ParseColor(a.Platform.Color, Rgba32.White) != PriviewNetworkScene.ParseColor(b.Platform.Color, Rgba32.White) ||
            a.Platform.Shape != b.Platform.Shape || !Eq(a.Platform.Width, b.Platform.Width) || !Eq(a.Platform.Depth, b.Platform.Depth) ||
            a.Platform.Collides != b.Platform.Collides || a.Platform.Gravity != b.Platform.Gravity ||
            !Eq(a.Platform.Position.X, b.Platform.Position.X) || !Eq(a.Platform.Position.Y, b.Platform.Position.Y) || !Eq(a.Platform.Position.Z, b.Platform.Position.Z))
            return "platform differs";
        if (a.Physics.GravityEnabled != b.Physics.GravityEnabled) return "physics gravity-enabled differs";
        if (!Eq(a.Physics.GravityStrength, b.Physics.GravityStrength)) return "physics gravity-strength differs";
        if (a.Physics.CollisionEnabled != b.Physics.CollisionEnabled) return "physics collision-enabled differs";
        if (!Eq(a.Physics.Restitution, b.Physics.Restitution)) return "physics restitution differs";
        if (a.Objects.Count != b.Objects.Count) return $"object count {a.Objects.Count} != {b.Objects.Count}";

        for (int i = 0; i < a.Objects.Count; i++)
        {
            var x = a.Objects[i];
            var y = b.Objects[i];
            if (x.Id != y.Id) return $"object[{i}].Id {x.Id} != {y.Id}";
            if (x.Name != y.Name) return $"object[{i}].Name '{x.Name}' != '{y.Name}'";
            if (x.Type != y.Type) return $"object[{i}].Type '{x.Type}' != '{y.Type}'";
            if (x.Mesh != y.Mesh) return $"object[{i}].Mesh '{x.Mesh}' != '{y.Mesh}'";
            if (!Eq(x.Position.X, y.Position.X) || !Eq(x.Position.Y, y.Position.Y) || !Eq(x.Position.Z, y.Position.Z))
                return $"object[{i}].Position differs";
            if (!Eq(x.Rotation.X, y.Rotation.X) || !Eq(x.Rotation.Y, y.Rotation.Y) || !Eq(x.Rotation.Z, y.Rotation.Z))
                return $"object[{i}].Rotation differs";
            if (!Eq(x.Scale, y.Scale)) return $"object[{i}].Scale differs";
            if (PriviewNetworkScene.ParseColor(x.Color, Rgba32.White) != PriviewNetworkScene.ParseColor(y.Color, Rgba32.White))
                return $"object[{i}].Color '{x.Color}' != '{y.Color}'";
            if (x.Anchor != y.Anchor) return $"object[{i}].Anchor '{x.Anchor}' != '{y.Anchor}'";
            if (!Eq(x.RotateSpeed, y.RotateSpeed)) return $"object[{i}].RotateSpeed differs";
            if (!Eq(x.Radius, y.Radius)) return $"object[{i}].Radius differs";
            if (x.Collides != y.Collides) return $"object[{i}].Collides {x.Collides} != {y.Collides}";
            if (x.Gravity != y.Gravity) return $"object[{i}].Gravity {x.Gravity} != {y.Gravity}";
            if (x.Collider != y.Collider) return $"object[{i}].Collider '{x.Collider}' != '{y.Collider}'";
            if (!Eq(x.Mass, y.Mass)) return $"object[{i}].Mass {x.Mass} != {y.Mass}";
            if (!Eq(x.Restitution, y.Restitution)) return $"object[{i}].Restitution {x.Restitution} != {y.Restitution}";
            if (!Eq(x.Friction, y.Friction)) return $"object[{i}].Friction {x.Friction} != {y.Friction}";
            if (!Eq(x.RollingFriction, y.RollingFriction)) return $"object[{i}].RollingFriction {x.RollingFriction} != {y.RollingFriction}";
            if (!Eq(x.ColorFade, y.ColorFade)) return $"object[{i}].ColorFade {x.ColorFade} != {y.ColorFade}";
            if (x.Texture != y.Texture) return $"object[{i}].Texture '{x.Texture}' != '{y.Texture}'";
            if (!Eq(x.TextureScale, y.TextureScale)) return $"object[{i}].TextureScale {x.TextureScale} != {y.TextureScale}";
            if (x.TextureFace != y.TextureFace) return $"object[{i}].TextureFace {x.TextureFace} != {y.TextureFace}";
            if (x.TextureFilter != y.TextureFilter) return $"object[{i}].TextureFilter {x.TextureFilter} != {y.TextureFilter}";
            if (!Eq(x.Power, y.Power)) return $"object[{i}].Power differs";
            // ---- light-only rich fields ----
            if (x.LightKind != y.LightKind) return $"object[{i}].LightKind '{x.LightKind}' != '{y.LightKind}'";
            if (!Eq(x.Direction.X, y.Direction.X) || !Eq(x.Direction.Y, y.Direction.Y) || !Eq(x.Direction.Z, y.Direction.Z))
                return $"object[{i}].Direction differs";
            if (!Eq(x.ConeAngle, y.ConeAngle)) return $"object[{i}].ConeAngle differs";
            if (!Eq(x.LightSize, y.LightSize)) return $"object[{i}].LightSize differs";
            if (!Eq(x.LightSpin, y.LightSpin)) return $"object[{i}].LightSpin differs";
            if (x.BeamCount != y.BeamCount) return $"object[{i}].BeamCount {x.BeamCount} != {y.BeamCount}";
            if (x.ConeShape != y.ConeShape) return $"object[{i}].ConeShape '{x.ConeShape}' != '{y.ConeShape}'";
            if (x.AreaShape != y.AreaShape) return $"object[{i}].AreaShape differs";
            if (!Eq(x.ColorInfluence, y.ColorInfluence)) return $"object[{i}].ColorInfluence differs";
        }
        return null;
    }

    // Headless check of the pure collision resolvers (the camera-bubble math): a sphere is ejected
    // out of an AABB / another sphere when penetrating, kept exactly tangent at the surface, and
    // returned UNCHANGED when clear. Mirrors what ResolveCameraCollision does each frame.
    static void CollisionSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== COLLISION SELF-TEST ===");

        const float eps = 1e-3f;
        bool ok = true;

        // Distance from a point to an AABB (0 when inside) — the penetration check.
        static float DistToAabb(Vector3 c, Vector3 min, Vector3 max)
        {
            Vector3 cl = new(Math.Clamp(c.X, min.X, max.X), Math.Clamp(c.Y, min.Y, max.Y), Math.Clamp(c.Z, min.Z, max.Z));
            return (c - cl).Length();
        }

        Vector3 bmin = new(-1f, -1f, -1f), bmax = new(1f, 1f, 1f);
        const float r = 0.35f;

        // 1) Centre INSIDE the box -> ejected until it no longer penetrates (dist from box >= r).
        {
            Vector3 outc = PriviewNetworkScene.ResolveSphereVsAabb(new Vector3(0f, 0f, 0f), r, bmin, bmax);
            float dist = DistToAabb(outc, bmin, bmax);
            bool t = dist >= r - eps;
            Console.WriteLine($"  aabb-centre: ejected to ({outc.X:F3},{outc.Y:F3},{outc.Z:F3}), dist-from-box={dist:F3} (want >= {r}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 2) Beside the +X face -> pushed EXACTLY to the surface; tangential (Y,Z) kept.
        {
            Vector3 inc = new(1.2f, 0.5f, -0.3f);
            Vector3 outc = PriviewNetworkScene.ResolveSphereVsAabb(inc, r, bmin, bmax);
            float dist = DistToAabb(outc, bmin, bmax);
            bool t = Math.Abs(dist - r) < eps && Math.Abs(outc.Y - inc.Y) < eps && Math.Abs(outc.Z - inc.Z) < eps && outc.X > inc.X;
            Console.WriteLine($"  aabb-face: ({inc.X},{inc.Y},{inc.Z}) -> ({outc.X:F3},{outc.Y:F3},{outc.Z:F3}), dist-from-box={dist:F3} (want {r}), tangential kept -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 3) Sphere-vs-sphere overlap -> centres end up exactly r+sr apart.
        {
            Vector3 center = new(0.5f, 0f, 0f); float sr = 0.5f; float rr = r + sr;
            Vector3 outc = PriviewNetworkScene.ResolveSphereVsSphere(new Vector3(0f, 0f, 0f), r, center, sr);
            float d = (outc - center).Length();
            bool t = Math.Abs(d - rr) < eps;
            Console.WriteLine($"  sphere-sphere: resolved centre-dist={d:F3} (want {rr}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 4) Non-penetrating inputs are returned UNCHANGED (both AABB + sphere resolvers).
        {
            Vector3 farc = new(5f, 0f, 0f);
            Vector3 a = PriviewNetworkScene.ResolveSphereVsAabb(farc, r, bmin, bmax);
            Vector3 b = PriviewNetworkScene.ResolveSphereVsSphere(farc, r, new Vector3(0f, 0f, 0f), 0.5f);
            bool t = a == farc && b == farc;
            Console.WriteLine($"  no-penetration: aabb->({a.X},{a.Y},{a.Z}), sphere->({b.X},{b.Y},{b.Z}) (want unchanged 5,0,0) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 5) OBB with identity axes == AABB (a non-rotated box must resolve identically to ResolveSphereVsAabb).
        {
            Vector3 ax = new(1f, 0f, 0f), ay = new(0f, 1f, 0f), az = new(0f, 0f, 1f), half = new(1f, 1f, 1f), center = new(0f, 0f, 0f);
            Vector3 inc = new(1.2f, 0.5f, -0.3f);
            Vector3 aabb = PriviewNetworkScene.ResolveSphereVsAabb(inc, r, bmin, bmax);
            Vector3 obb = PriviewNetworkScene.ResolveSphereVsObb(inc, r, center, ax, ay, az, half);
            bool t = (obb - aabb).Length() < eps;
            Console.WriteLine($"  obb-identity: obb=({obb.X:F3},{obb.Y:F3},{obb.Z:F3}) vs aabb=({aabb.X:F3},{aabb.Y:F3},{aabb.Z:F3}) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 6) OBB in a ROTATED frame (unit box turned 45° in the XZ plane): a point just past its +X
        // local face is pushed out ALONG that local axis to face+r; a far point is returned unchanged.
        {
            const float s = 0.70710678f;                       // cos/sin 45°
            Vector3 ax = new(s, 0f, s), ay = new(0f, 1f, 0f), az = new(-s, 0f, s), half = new(1f, 1f, 1f), center = new(0f, 0f, 0f);
            Vector3 inc = ax * 1.1f;                            // local ex=1.1 (just past the face at 1.0), within r
            Vector3 outc = PriviewNetworkScene.ResolveSphereVsObb(inc, r, center, ax, ay, az, half);
            Vector3 want = ax * (1f + r);                       // pushed to face + r along the local X axis
            bool faceOk = (outc - want).Length() < eps;
            Vector3 farc = ax * (1f + r + 0.5f);                // clear of the face -> unchanged
            Vector3 outf = PriviewNetworkScene.ResolveSphereVsObb(farc, r, center, ax, ay, az, half);
            bool freeOk = (outf - farc).Length() < eps;
            Console.WriteLine($"  obb-rotated: face-push dist-from-want={(outc - want).Length():F4}, free-unchanged={freeOk} -> {(faceOk && freeOk ? "ok" : "BAD")}");
            ok &= faceOk && freeOk;
        }

        // 8) SatBox3D (OBB-OBB contact, full 3D — the manifold normal the impulse box-box solver uses): the
        // axis-aligned MTV (normal +X, depth 0.2); a vertical offset separates with a +Y normal; a 45°-about-Z
        // box still overlaps with a unit normal; a far pair reports no hit.
        {
            Vector3 hU = new(0.5f, 0.5f, 0.5f);
            Vector3 AX = new(1f, 0f, 0f), AY = new(0f, 1f, 0f), AZ = new(0f, 0f, 1f);
            var (b3hit, b3n, b3d) = PriviewNetworkScene.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0.8f, 0f, 0f), AX, AY, AZ, hU);
            bool b3Axis = b3hit && Math.Abs(b3n.X - 1f) < eps && Math.Abs(b3n.Y) < eps && Math.Abs(b3n.Z) < eps && Math.Abs(b3d - 0.2f) < eps;
            var (b3vh, b3vn, b3vd) = PriviewNetworkScene.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0f, 0.8f, 0f), AX, AY, AZ, hU);
            bool b3Vert = b3vh && Math.Abs(b3vn.Y - 1f) < eps && Math.Abs(b3vd - 0.2f) < eps;
            var (b3fh, _, _) = PriviewNetworkScene.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(3f, 0f, 0f), AX, AY, AZ, hU);
            const float q = 0.70710678f;
            Vector3 rAX = new(q, q, 0f), rAY = new(-q, q, 0f);     // axes rotated 45° about Z
            var (b3rh, b3rn, b3rd) = PriviewNetworkScene.SatBox3D(Vector3.Zero, AX, AY, AZ, hU, new Vector3(0.9f, 0f, 0f), rAX, rAY, AZ, hU);
            bool b3Rot = b3rh && b3rd > 0f && Math.Abs(b3rn.Length() - 1f) < eps;
            bool satOk = b3Axis && b3Vert && !b3fh && b3Rot;
            Console.WriteLine($"  sat-box3d: axis(n={b3n.X:F2} d={b3d:F2}), vert={b3Vert}, far={!b3fh}, rot={b3Rot} -> {(satOk ? "ok" : "BAD")}");
            ok &= satOk;
        }

        Console.WriteLine(ok ? "COLLISION TEST PASSED" : "COLLISION TEST FAILED");
    }

    // Headless check of the shared pure math the physics + networking still use: drift-free quaternion
    // orientation (Euler<->Quat round-trip, ω integration, unit-norm), shortest-arc LerpAngle + the
    // StepInterpolate dead-reckon/ease (network sync), CombineRestitution, and ClosestPointOnTriangle.
    // (The legacy decoupled-pass helpers this used to exercise were retired in Stage 7b; the object-
    // dynamics behaviour is now covered end-to-end by impulsetest.)
    static void PhysicsSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== PHYSICS SELF-TEST ===");

        const float eps = 1e-2f;
        bool ok = true;

        // 3f) Quaternion orientation: Euler -> Quat -> Euler round-trips (away from gimbal lock); a unit
        // angular velocity integrated for time T rotates by exactly ω·T (drift-free) and the quaternion
        // stays unit-norm even integrating about two axes (where naive Euler integration would drift).
        {
            Vector3 e = new(0.3f, 0.5f, -0.2f);
            var rt = PriviewNetworkScene.EulerFromQuat(PriviewNetworkScene.QuatFromEuler(e));
            bool rtOk = Math.Abs(rt.X - e.X) < 1e-3f && Math.Abs(rt.Y - e.Y) < 1e-3f && Math.Abs(rt.Z - e.Z) < 1e-3f;

            // integrate ω=(0,2,0) for T=0.5 -> yaw 1.0 rad.
            var q = PriviewNetworkScene.Quat.Identity; float dt = 1f / 600f;
            for (int i = 0; i < 300; i++) q = PriviewNetworkScene.IntegrateQuat(q, new Vector3(0f, 2f, 0f), dt);
            var eY = PriviewNetworkScene.EulerFromQuat(q);
            bool spinOk = Math.Abs(eY.Y - 1.0f) < 1e-2f && Math.Abs(eY.X) < 1e-2f && Math.Abs(eY.Z) < 1e-2f;

            // two-axis integration must keep the quaternion unit-norm (drift-free).
            var q2 = PriviewNetworkScene.Quat.Identity;
            for (int i = 0; i < 600; i++) q2 = PriviewNetworkScene.IntegrateQuat(q2, new Vector3(1.5f, -1f, 0.7f), dt);
            float norm2 = MathF.Sqrt(q2.X * q2.X + q2.Y * q2.Y + q2.Z * q2.Z + q2.W * q2.W);
            bool normOk = Math.Abs(norm2 - 1f) < 1e-4f;

            bool quatOk = rtOk && spinOk && normOk;
            Console.WriteLine($"  quaternion: roundtrip={rtOk}, spinY={eY.Y:F3}(1.000), unit-norm={norm2:F5} -> {(quatOk ? "ok" : "BAD")}");
            ok &= quatOk;
        }

        // 3g) LerpAngle (rotation sync): eases along the SHORTEST arc — lerping from +3.0 toward -3.0
        // (≈ across the ±π seam) moves the SHORT way (magnitude grows past π / wraps), not back through 0.
        {
            float half = PriviewNetworkScene.LerpAngle(3.0f, -3.0f, 0.5f);   // shortest delta ≈ +0.283, half -> ~3.14
            float midNoWrap = 3.0f + (-3.0f - 3.0f) * 0.5f;                  // naive lerp would give 0 (the long way)
            bool shortArc = MathF.Abs(half) > 3.0f && Math.Abs(midNoWrap) < eps;   // short arc leaves |angle|>3; naive collapses to 0
            float plain = PriviewNetworkScene.LerpAngle(0.2f, 0.8f, 0.5f);   // no wrap -> simple midpoint 0.5
            bool lerpOk = shortArc && Math.Abs(plain - 0.5f) < eps;
            Console.WriteLine($"  lerpangle: seam={half:F3}(|>3|), plain={plain:F3}(0.5) -> {(lerpOk ? "ok" : "BAD")}");
            ok &= lerpOk;
        }

        // 4) StepInterpolate (client position sync): with velY=0 the current position eases toward a
        // fixed target and converges (monotonic shrinking error); with velY<0 it dead-reckons the
        // ongoing fall, so the target keeps descending and the eased current tracks it downward.
        {
            float dt = 1f / 60f, rate = 12f;
            // converge: cur=(0,5,0) toward tgt=(0,0,0), no velocity.
            Vector3 cur = new(0f, 5f, 0f), tgt = new(0f, 0f, 0f);
            float prevErr = MathF.Abs(cur.Y - tgt.Y); bool monotonic = true;
            for (int i = 0; i < 240; i++)
            {
                (cur, tgt) = PriviewNetworkScene.StepInterpolate(cur, tgt, Vector3.Zero, dt, rate);
                float err = MathF.Abs(cur.Y - tgt.Y);
                if (err > prevErr + 1e-6f) monotonic = false;
                prevErr = err;
            }
            bool conv = monotonic && MathF.Abs(cur.Y) < 1e-2f;
            Console.WriteLine($"  interp-converge: cur.Y={cur.Y:F4} (want ~0), monotonic={monotonic} -> {(conv ? "ok" : "BAD")}");
            ok &= conv;

            // dead-reckon: velY=-6, target starts at 0 and must descend ~velY*elapsed; cur tracks near it.
            Vector3 c2 = new(0f, 0f, 0f), t2 = new(0f, 0f, 0f);
            int steps = 120; float velY = -6f;
            for (int i = 0; i < steps; i++) (c2, t2) = PriviewNetworkScene.StepInterpolate(c2, t2, new Vector3(0f, velY, 0f), dt, rate);
            float expected = velY * steps * dt;                 // target's extrapolated Y
            bool fell = t2.Y < -1e-2f && MathF.Abs(t2.Y - expected) < 1e-2f && MathF.Abs(c2.Y - t2.Y) < 0.5f;
            Console.WriteLine($"  interp-deadreckon: tgt.Y={t2.Y:F3} (want {expected:F3}), cur.Y={c2.Y:F3} (tracks) -> {(fell ? "ok" : "BAD")}");
            ok &= fell;
        }

        // 4b) COMBINE-RESTITUTION: a contact's bounciness is the geometric mean of the two bodies'
        // restitutions, so two elastic surfaces stay elastic, a dead one (0) kills the rebound, and a
        // negative ("inherit world") input clamps to 0 in the raw combine (callers resolve it first).
        {
            float c11 = PriviewNetworkScene.CombineRestitution(1f, 1f);
            float c10 = PriviewNetworkScene.CombineRestitution(1f, 0f);
            float c55 = PriviewNetworkScene.CombineRestitution(0.5f, 0.5f);
            float cNeg = PriviewNetworkScene.CombineRestitution(-1f, 0.5f);
            bool cok = MathF.Abs(c11 - 1f) < 1e-4f && c10 < 1e-4f && MathF.Abs(c55 - 0.5f) < 1e-4f && cNeg < 1e-4f;
            Console.WriteLine($"  combine-restitution: (1,1)={c11:F2} (1,0)={c10:F2} (.5,.5)={c55:F2} (-1,.5)={cNeg:F2} -> {(cok ? "ok" : "BAD")}");
            ok &= cok;
        }

        // 4c) CLOSEST-POINT-ON-TRIANGLE (the heart of real sphere-vs-mesh contact): a point above the face
        // projects straight down onto it; a point beyond a vertex clamps to that vertex; a point past an edge
        // clamps onto the edge. Triangle in the y=0 plane: A(0,0,0) B(1,0,0) C(0,0,1).
        {
            Vector3 a = new(0f, 0f, 0f), b = new(1f, 0f, 0f), cc = new(0f, 0f, 1f);
            Vector3 above = PriviewNetworkScene.ClosestPointOnTriangle(new Vector3(0.25f, 5f, 0.25f), a, b, cc);   // inside -> projects to (0.25,0,0.25)
            Vector3 vert = PriviewNetworkScene.ClosestPointOnTriangle(new Vector3(-2f, 0f, -2f), a, b, cc);        // beyond A -> A
            Vector3 edge = PriviewNetworkScene.ClosestPointOnTriangle(new Vector3(0.5f, 0f, -1f), a, b, cc);       // past AB -> (0.5,0,0)
            bool cptOk = MathF.Abs(above.X - 0.25f) < eps && MathF.Abs(above.Y) < eps && MathF.Abs(above.Z - 0.25f) < eps
                      && vert.Length() < eps
                      && MathF.Abs(edge.X - 0.5f) < eps && MathF.Abs(edge.Y) < eps && MathF.Abs(edge.Z) < eps;
            Console.WriteLine($"  closest-point: above=({above.X:F2},{above.Y:F2},{above.Z:F2}), vert=({vert.X:F2},{vert.Y:F2},{vert.Z:F2}), edge=({edge.X:F2},{edge.Y:F2},{edge.Z:F2}) -> {(cptOk ? "ok" : "BAD")}");
            ok &= cptOk;
        }

        Console.WriteLine(ok ? "PHYSICS TEST PASSED" : "PHYSICS TEST FAILED");
    }

    // Headless check of the STAGE-1 impulse solver (SampleGame/Physics/, see PHYSICS.md): drop a dynamic
    // sphere onto a STATIC ground box across a dt sweep and assert the acceptance criteria — settles at rest
    // WITHOUT sinking, bounces with restitution whose peaks strictly DECAY (no energy gain) and never exceed
    // the drop height, stays bounded + frame-rate independent, and SLEEPS. (No CCD — a fast impact at COARSE
    // dt may transiently penetrate for one substep before the solver pushes it out; it must still not sink
    // THROUGH and must rest within slop. Strict sub-slop penetration is asserted at fine dt.)
    static void ImpulseSelfTest()
    {
        Console.WriteLine("=== IMPULSE SELF-TEST (Stage 1: sphere/static; Stage 2: friction+box/static; Stage 3: box-box stacks; Stage 4: sphere-box + sphere-sphere) ===");
        bool ok = true;
        const float R = 0.5f, restY = 0.5f, dropY = 5f, g = 9.8f, slop = 0.02f;

        // Tilt (rad) of a body's local up-axis away from world vertical — 0 = perfectly level.
        static float BoxTilt(RigidBody b) { Vector3 up = ImpulseMath.Rotate(b.Orientation, new Vector3(0f, 1f, 0f)); return MathF.Acos(Math.Clamp(up.Y, -1f, 1f)); }

        // 1) REST + NO SINK-THROUGH + FRAME-RATE INDEPENDENCE (restitution 0). Across a dt sweep the sphere
        //    settles at ~restY, never sinks THROUGH the ground, comes to rest, stays put, and sleeps.
        foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 15f, 1f / 8f, 1f / 4f, 1f / 2f, 1f / 1f })
        {
            var w = MakeImpulseWorld(0f, g, R, dropY, out var ball);
            float maxPen = 0f;
            int steps = (int)(8f / dt);
            for (int i = 0; i < steps; i++)
            {
                w.Step(dt);
                float pen = restY - ball.Position.Y;              // > 0 = below rest (sunk)
                if (pen > maxPen) maxPen = pen;
            }
            // last-window stability: a few more steps must not move it (it's asleep).
            float y0 = ball.Position.Y, maxJit = 0f;
            for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, MathF.Abs(ball.Position.Y - y0)); }

            bool rested = MathF.Abs(ball.Position.Y - restY) < slop;      // frame-rate INDEPENDENT rest height
            bool noFallThrough = ball.Position.Y > -slop;                 // ended ON the ground, never sank below / out the bottom
            bool subSlop = dt > 1f / 8f + 1e-4f || maxPen < slop;         // STRICT sub-slop penetration at fine dt (no CCD at coarse dt)
            bool recovered = maxPen < 1.5f;                               // a coarse-dt impact may transiently penetrate, but bounded + it recovered to rest
            bool still = maxJit < 1e-4f;                                  // frozen (asleep)
            bool bounded = MathF.Abs(ball.Position.X) < 1f && MathF.Abs(ball.Position.Z) < 1f && ball.Position.Y < dropY + 1f;
            bool good = rested && noFallThrough && subSlop && recovered && still && ball.Sleeping && bounded;
            Console.WriteLine($"  rest dt=1/{1f / dt:F0}: restY={ball.Position.Y:F4} (want {restY}), maxPen={maxPen:F4}{(maxPen >= slop ? " (coarse-dt transient, no CCD)" : "")}, jitter={maxJit:F6}, sleep={ball.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) RESTITUTION (e = 0.5) — NO ENERGY GAIN: the sphere bounces; successive APEX heights strictly
        //    decrease and never exceed the drop height; then it settles and sleeps.
        {
            float dt = 1f / 120f;                                  // fine dt so apex sampling is clean
            var w = MakeImpulseWorld(0.5f, g, R, dropY, out var ball);
            var apex = new List<float>();
            int steps = (int)(12f / dt);
            for (int i = 0; i < steps; i++)
            {
                float vyBefore = ball.LinVel.Y;
                w.Step(dt);
                if (vyBefore > 0f && ball.LinVel.Y <= 0f && ball.Position.Y > restY + 0.02f) apex.Add(ball.Position.Y);   // rising -> apex
            }
            bool anyBounce = apex.Count >= 2;
            bool underDrop = apex.Count > 0 && apex[0] < dropY;
            bool decaying = true;
            for (int k = 1; k < apex.Count; k++) if (apex[k] >= apex[k - 1] - 1e-4f) decaying = false;
            bool settled = MathF.Abs(ball.Position.Y - restY) < slop && ball.Sleeping;
            bool good = anyBounce && underDrop && decaying && settled;
            Console.WriteLine($"  restitution e=0.5: apexes=[{string.Join(",", apex.ConvertAll(a => a.ToString("F2")))}] (decaying + < {dropY}, then rest+sleep) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 3) BOX REST (dt sweep): a box dropped flat onto static ground SETTLES FLAT (tilt ~0), no jitter/drift,
        //    penetration <= slop (at fine dt), rests at ~half above the ground, and SLEEPS. Frame-rate indep.
        {
            const float bh = 0.5f, boxDrop = 1f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 15f, 1f / 8f, 1f / 4f, 1f / 2f, 1f / 1f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var box = RigidBody.DynamicBox(new Vector3(0f, boxDrop, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f);
                w.Bodies.Add(box);
                float maxPen = 0f;
                int steps = (int)(8f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); float pen = bh - box.Position.Y; if (pen > maxPen) maxPen = pen; }
                Vector3 p0 = box.Position; float maxJit = 0f, maxTilt = 0f;
                for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, (box.Position - p0).Length()); maxTilt = MathF.Max(maxTilt, BoxTilt(box)); }

                bool rested = MathF.Abs(box.Position.Y - bh) < slop;
                bool flat = maxTilt < 0.02f;                              // stayed level (tilt < ~1.1°)
                bool subSlop = dt > 1f / 8f + 1e-4f || maxPen < slop;     // strict sub-slop penetration at fine dt (no CCD at coarse dt)
                bool recovered = maxPen < 1.5f;
                bool still = maxJit < 1e-4f;                              // frozen (asleep)
                bool bounded = MathF.Abs(box.Position.X) < 1f && MathF.Abs(box.Position.Z) < 1f;
                bool good = rested && flat && subSlop && recovered && still && box.Sleeping && bounded;
                Console.WriteLine($"  box-rest dt=1/{1f / dt:F0}: restY={box.Position.Y:F4} (want {bh}), tilt={maxTilt:F5}, maxPen={maxPen:F4}{(maxPen >= slop ? " (coarse-dt transient)" : "")}, jitter={maxJit:F6}, sleep={box.Sleeping} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 4) BOX FRICTION ON AN INCLINE (two μ): a box resting on a static OBB tilted ~11.5° (NOT the legacy
        //    ramp — a plain tilted static box). HIGH μ -> the box STICKS (no slide); LOW μ -> it slides downhill
        //    with a BOUNDED speed (friction caps the acceleration; it never runs away). Swept over frame times.
        {
            float theta = 0.2f;                                          // incline angle (rad); tan θ ≈ 0.203
            PriviewNetworkScene.Quat tilt = PriviewNetworkScene.QuatFromEuler(new Vector3(0f, 0f, theta));
            Vector3 topN = ImpulseMath.Rotate(tilt, new Vector3(0f, 1f, 0f));   // (-sinθ, cosθ, 0): downhill is -X
            Vector3 groundHalf = new Vector3(10f, 0.5f, 10f);
            Vector3 topFaceCenter = topN * groundHalf.Y;
            foreach (var (mu, shouldStick) in new[] { (0.8f, true), (0.05f, false) })
            {
                bool allDt = true; float lastDisp = 0f, lastSpeed = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    var ground = RigidBody.StaticBox(new Vector3(0f, 0f, 0f), groundHalf, tilt); ground.Friction = mu; w.Bodies.Add(ground);
                    Vector3 start = topFaceCenter + topN * (0.5f + 0.005f);
                    var box = RigidBody.DynamicBox(start, new Vector3(0.5f, 0.5f, 0.5f), tilt, 1f); box.Friction = mu; w.Bodies.Add(box);
                    int steps = (int)(3f / dt); float maxSpeed = 0f;
                    for (int i = 0; i < steps; i++) { w.Step(dt); float sp = box.LinVel.Length(); if (sp > maxSpeed) maxSpeed = sp; }
                    float disp = (box.Position - start).Length();
                    bool downhill = box.Position.X < start.X - 1e-3f;    // slid toward -X (downhill)
                    bool bounded = box.Position.Y > -5f && MathF.Abs(box.Position.X) < 12f && maxSpeed < 15f;
                    bool good = shouldStick ? (disp < 0.1f && bounded) : (disp > 0.3f && downhill && bounded);
                    allDt &= good; lastDisp = disp; lastSpeed = maxSpeed;
                    if (!good) Console.WriteLine($"    [detail] mu={mu} dt=1/{1f / dt:F0}: disp={disp:F3}, maxSpeed={maxSpeed:F2}, endX={box.Position.X:F2}");
                }
                Console.WriteLine($"  box-incline mu={mu} ({(shouldStick ? "stick" : "slide")}): disp={lastDisp:F3}, maxSpeed={lastSpeed:F2} -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 5) FRICTION NO CREEP: a box AND a sphere placed at rest on level ground (μ>0, zero initial velocity)
        //    must NOT drift horizontally over many steps (friction cancels any numerical tangential velocity).
        {
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
            var box = RigidBody.DynamicBox(new Vector3(2f, 0.5f, -1f), new Vector3(0.5f, 0.5f, 0.5f), PriviewNetworkScene.Quat.Identity, 1f);
            w.Bodies.Add(box);
            var sph = RigidBody.Sphere(new Vector3(-2f, 0.5f, 1f), 0.5f, 1f);
            w.Bodies.Add(sph);
            Vector3 bx0 = box.Position, sp0 = sph.Position;
            for (int i = 0; i < 600; i++) w.Step(1f / 60f);
            float boxDrift = MathF.Sqrt((box.Position.X - bx0.X) * (box.Position.X - bx0.X) + (box.Position.Z - bx0.Z) * (box.Position.Z - bx0.Z));
            float sphDrift = MathF.Sqrt((sph.Position.X - sp0.X) * (sph.Position.X - sp0.X) + (sph.Position.Z - sp0.Z) * (sph.Position.Z - sp0.Z));
            bool good = boxDrift < 1e-3f && sphDrift < 1e-3f;
            Console.WriteLine($"  friction-no-creep: box drift={boxDrift:F6}, sphere drift={sphDrift:F6} (want ~0) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // Largest penetration across a vertical stack of boxes (each half bh) on ground top y=0: the box0↔ground
        // interface (0 − (Y−bh) = bh − Y) plus every box(k-1)↔box(k) interface. > 0 means overlapping.
        static float StackMaxPen(List<RigidBody> st, float bh)
        {
            float m = bh - st[0].Position.Y;                                              // ground (top y=0) vs box0 bottom
            for (int k = 1; k < st.Count; k++) m = MathF.Max(m, (st[k - 1].Position.Y + bh) - (st[k].Position.Y - bh));
            return m;
        }

        // 6) STACK STABILITY (the headline): 4 aligned dynamic boxes stacked on static ground must stay UPRIGHT
        //    (tilt ~0), NOT sink (settled inter-box penetration <= slop), NOT drift, SETTLE and SLEEP, and stay
        //    bounded — across a realistic dt sweep. Coarse 1/1 asserts only bounded + recovered (no CCD).
        {
            const float bh = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f, 1f / 1f })
            {
                bool coarse = dt > 1f / 20f + 1e-4f;
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var st = new List<RigidBody>();
                for (int k = 0; k < 4; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * (2f * bh), 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f); st.Add(bx); w.Bodies.Add(bx); }

                float maxTilt = 0f, maxDrift = 0f, maxPen = 0f;
                int steps = (int)(10f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    for (int k = 0; k < 4; k++) maxTilt = MathF.Max(maxTilt, BoxTilt(st[k]));
                    maxDrift = MathF.Max(maxDrift, MathF.Max(MathF.Abs(st[3].Position.X), MathF.Abs(st[3].Position.Z)));   // top box XZ deviation from centre
                    maxPen = MathF.Max(maxPen, StackMaxPen(st, bh));
                }
                float finalPen = StackMaxPen(st, bh);
                bool allSleep = st.All(b => b.Sleeping);
                bool upright = maxTilt < 0.02f;
                bool noSink = finalPen < slop;                                            // settled interfaces within slop
                bool noDrift = maxDrift < 0.05f;
                bool boundedFine = st.All(b => MathF.Abs(b.Position.X) < 2f && MathF.Abs(b.Position.Z) < 2f && b.Position.Y > 0f && b.Position.Y < 5f) && maxPen < 0.6f;
                bool boundedCoarse = st.All(b => MathF.Abs(b.Position.X) < 15f && MathF.Abs(b.Position.Z) < 15f && b.Position.Y > -1f && b.Position.Y < 10f);   // no fling (a 1-FPS stack may collapse into a heap, per the no-CCD caveat)
                bool good = coarse ? boundedCoarse : (upright && noSink && noDrift && allSleep && boundedFine);
                Console.WriteLine($"  stack-stability dt=1/{1f / dt:F0}: maxTilt={maxTilt:F5}, topDrift={maxDrift:F4}, finalPen={finalPen:F4} (transient {maxPen:F3}), sleep={allSleep} -> {(good ? "ok" : "BAD")}{(coarse ? " (coarse: bounded-only)" : "")}");
                ok &= good;
            }
        }

        // 7) BOX-ON-BOX REST: one dynamic box resting on another (bottom on static ground) rests FLAT and STILL
        //    — no jitter/drift, penetration <= slop, both SLEEP.
        {
            const float bh = 0.5f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
            var lo = RigidBody.DynamicBox(new Vector3(0f, bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f);
            var hi = RigidBody.DynamicBox(new Vector3(0f, 3f * bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f);
            var st = new List<RigidBody> { lo, hi }; w.Bodies.Add(lo); w.Bodies.Add(hi);
            for (int i = 0; i < 400; i++) w.Step(1f / 60f);
            Vector3 pLo = lo.Position, pHi = hi.Position; float maxJit = 0f, maxTilt = 0f;
            for (int i = 0; i < 120; i++) { w.Step(1f / 60f); maxJit = MathF.Max(maxJit, MathF.Max((lo.Position - pLo).Length(), (hi.Position - pHi).Length())); maxTilt = MathF.Max(maxTilt, MathF.Max(BoxTilt(lo), BoxTilt(hi))); }
            float pen = StackMaxPen(st, bh);
            bool good = pen < slop && maxJit < 1e-4f && maxTilt < 0.02f && lo.Sleeping && hi.Sleeping;
            Console.WriteLine($"  box-on-box-rest: pen={pen:F4}, jitter={maxJit:F6}, tilt={maxTilt:F5}, sleep={lo.Sleeping && hi.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 8) BOX-BOX MOMENTUM (mass-weighted, no energy gain): a box moving at +X strikes a resting box on
        //    frictionless level ground. Inelastic (restitution 0) -> they move together at v·mL/(mL+mR); a LIGHT
        //    box hitting a HEAVY one moves it LESS than an equal-mass one would. Momentum conserved, KE not gained.
        {
            const float bh = 0.5f, v0 = 3f;
            static (float rVel, float momA, float momB, float keA, float keB) HitTest(float mR, float g, float bh, float v0)
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                var ground = RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity); ground.Friction = 0f; w.Bodies.Add(ground);
                var L = RigidBody.DynamicBox(new Vector3(-1f, bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f); L.Friction = 0f; L.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(L);
                var Rb = RigidBody.DynamicBox(new Vector3(0.5f, bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, mR); Rb.Friction = 0f; w.Bodies.Add(Rb);
                float momA = 1f * v0;                                                     // initial X-momentum (only L moves)
                float keA = 0.5f * 1f * v0 * v0;
                for (int i = 0; i < 90; i++) w.Step(1f / 120f);                            // ~0.75 s: collide, then coast together
                float momB = 1f * L.LinVel.X + mR * Rb.LinVel.X;
                float keB = 0.5f * 1f * L.LinVel.X * L.LinVel.X + 0.5f * mR * Rb.LinVel.X * Rb.LinVel.X;
                return (Rb.LinVel.X, momA, momB, keA, keB);
            }
            var heavy = HitTest(5f, g, bh, v0);                                            // light L (m=1) hits heavy R (m=5)
            var equal = HitTest(1f, g, bh, v0);                                            // equal masses
            bool heavyMovesLess = heavy.rVel < equal.rVel - 0.05f && heavy.rVel > 0.01f;   // heavy R gains less speed, but does move
            bool momOk = MathF.Abs(heavy.momB - heavy.momA) < 0.15f && MathF.Abs(equal.momB - equal.momA) < 0.15f;
            bool noGain = heavy.keB <= heavy.keA + 1e-3f && equal.keB <= equal.keA + 1e-3f;
            bool good = heavyMovesLess && momOk && noGain;
            Console.WriteLine($"  box-box-momentum: heavyR vel={heavy.rVel:F3} < equalR vel={equal.rVel:F3} (mass-weighted), momentum {heavy.momA:F2}->{heavy.momB:F2}/{equal.momB:F2}, KE {heavy.keA:F2}->{heavy.keB:F2}/{equal.keB:F2} (no gain) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 9) SPHERE-ON-BOX (dynamic-vs-dynamic): a dynamic sphere dropped onto a dynamic box (box on static
        //    ground) rests on the box (mass-weighted), the pair SETTLES with penetration <= slop and SLEEPS,
        //    no energy gain — across the realistic sweep. (1/1 is omitted for this delicate 3-body VERTICAL
        //    stack: a ball on a thin box at 1 FPS can tunnel with no CCD — the RunawayBound backstop keeps it
        //    bounded, but a meaningful "rests" assertion needs a realistic frame time.)
        {
            const float bh = 0.5f, rad = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var box = RigidBody.DynamicBox(new Vector3(0f, bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f);
                w.Bodies.Add(box);
                var ball = RigidBody.Sphere(new Vector3(0f, box.Position.Y + bh + rad + 0.5f, 0f), rad, 1f);   // small centred drop onto the box top
                w.Bodies.Add(ball);
                float PenOf() { Vector3 cp = ContactGen.ClosestPointOnObb(box.Position, box.HalfExtents, box.Orientation, ball.Position); return MathF.Max(rad - (ball.Position - cp).Length(), bh - box.Position.Y); }
                float maxPen = 0f;
                int steps = (int)(10f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); maxPen = MathF.Max(maxPen, PenOf()); }
                Vector3 pBall = ball.Position, pBox = box.Position; float maxJit = 0f;
                for (int i = 0; i < 120; i++) { w.Step(dt); maxJit = MathF.Max(maxJit, MathF.Max((ball.Position - pBall).Length(), (box.Position - pBox).Length())); }
                float finalPen = PenOf();
                bool rested = MathF.Abs(ball.Position.Y - (box.Position.Y + bh + rad)) < 2f * slop;   // ball sits box-top + rad
                bool noSink = finalPen < slop;
                bool still = maxJit < 1e-4f;
                bool asleep = ball.Sleeping && box.Sleeping;
                bool bounded = MathF.Abs(ball.Position.X) < 1f && MathF.Abs(ball.Position.Z) < 1f && ball.Position.Y > 0f && ball.Position.Y < 4f && maxPen < 0.6f;
                bool good = rested && noSink && still && asleep && bounded;
                Console.WriteLine($"  sphere-on-box dt=1/{1f / dt:F0}: ballY={ball.Position.Y:F4} (want {box.Position.Y + bh + rad:F3}), finalPen={finalPen:F4} (transient {maxPen:F3}), jitter={maxJit:F6}, sleep={asleep} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 10) SPHERE-SPHERE MOMENTUM (mass-weighted, no energy gain): a sphere moving at +X strikes a resting
        //     sphere on frictionless level ground. Inelastic (restitution 0) -> a LIGHT sphere moves a HEAVY one
        //     less than an equal-mass one would; momentum conserved, KE not gained.
        {
            const float rad = 0.5f, v0 = 3f;
            static (float rVel, float momA, float momB, float keA, float keB) HitSphere(float mR, float g, float rad, float v0)
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                var ground = RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity); ground.Friction = 0f; w.Bodies.Add(ground);
                var L = RigidBody.Sphere(new Vector3(-1f, rad, 0f), rad, 1f); L.Friction = 0f; L.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(L);
                var Rb = RigidBody.Sphere(new Vector3(0.5f, rad, 0f), rad, mR); Rb.Friction = 0f; w.Bodies.Add(Rb);
                for (int i = 0; i < 90; i++) w.Step(1f / 120f);
                return (Rb.LinVel.X, 1f * v0, 1f * L.LinVel.X + mR * Rb.LinVel.X, 0.5f * 1f * v0 * v0, 0.5f * 1f * L.LinVel.X * L.LinVel.X + 0.5f * mR * Rb.LinVel.X * Rb.LinVel.X);
            }
            var heavy = HitSphere(5f, g, rad, v0);
            var equal = HitSphere(1f, g, rad, v0);
            bool heavyMovesLess = heavy.rVel < equal.rVel - 0.05f && heavy.rVel > 0.01f;
            bool momOk = MathF.Abs(heavy.momB - heavy.momA) < 0.15f && MathF.Abs(equal.momB - equal.momA) < 0.15f;
            bool noGain = heavy.keB <= heavy.keA + 1e-3f && equal.keB <= equal.keA + 1e-3f;
            bool good = heavyMovesLess && momOk && noGain;
            Console.WriteLine($"  sphere-sphere-momentum: heavyR vel={heavy.rVel:F3} < equalR vel={equal.rVel:F3} (mass-weighted), momentum {heavy.momA:F2}->{heavy.momB:F2}/{equal.momB:F2}, KE {heavy.keA:F2}->{heavy.keB:F2}/{equal.keB:F2} (no gain) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 11) BALL SCATTERS STACK (the whole-system test): a fast dynamic sphere fired horizontally into a
        //     3-box stack. ASSERT the stack is DISTURBED, no horizontal momentum is INJECTED (friction/ground
        //     only remove it, so peak Σm·vx <= the ball's initial), no ENERGY is injected (peak Σ½m|v|² <=
        //     initial KE + convertible gravity PE), everything stays BOUNDED (no fling), and it SETTLES/sleeps.
        {
            const float bh = 0.5f, rad = 0.5f, v0 = 8f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f, 1f / 1f })
            {
                bool coarse = dt > 1f / 20f + 1e-4f;
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var boxes = new List<RigidBody>();
                for (int k = 0; k < 3; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * 2f * bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f); boxes.Add(bx); w.Bodies.Add(bx); }
                var ball = RigidBody.Sphere(new Vector3(-2.5f, 1.5f, 0f), rad, 1f); ball.LinVel = new Vector3(v0, 0f, 0f); w.Bodies.Add(ball);
                var all = new List<RigidBody>(boxes) { ball };
                Vector3[] start = boxes.Select(b => b.Position).ToArray();
                float initMomX = 1f * v0, initKE = 0.5f * 1f * v0 * v0;
                float gravPE = 1f * g * ball.Position.Y; foreach (var bx in boxes) gravPE += 1f * g * bx.Position.Y;
                float peakMomX = 0f, peakKE = 0f, maxDisp = 0f, maxCoord = 0f;
                int steps = (int)(15f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    float momX = 0f, ke = 0f;
                    foreach (var b in all) { momX += b.LinVel.X; ke += 0.5f * (b.LinVel * b.LinVel); maxCoord = MathF.Max(maxCoord, MathF.Max(MathF.Abs(b.Position.X), MathF.Abs(b.Position.Z))); }
                    peakMomX = MathF.Max(peakMomX, momX); peakKE = MathF.Max(peakKE, ke);
                    for (int k = 0; k < 3; k++) maxDisp = MathF.Max(maxDisp, (boxes[k].Position - start[k]).Length());
                }
                bool disturbed = maxDisp > 0.3f;
                bool momOk = peakMomX <= initMomX + 0.5f;                       // no horizontal momentum injection
                bool energyOk = peakKE <= initKE + gravPE + 5f;                 // no energy injection
                bool bounded = maxCoord < 20f && all.All(b => b.Position.Y > -1f && b.Position.Y < 15f);
                // The STACK settles to rest; the ball may keep rolling (rolling friction is Stage 6), so it's
                // not required to sleep — only to stay bounded (above).
                bool settled = boxes.All(b => b.LinVel.Length() < 0.15f && b.AngVel.Length() < 0.4f);
                bool good = coarse ? bounded : (disturbed && momOk && energyOk && bounded && settled);
                Console.WriteLine($"  ball-scatters-stack dt=1/{1f / dt:F0}: disturbed={maxDisp:F2}, peakMomX={peakMomX:F2}/<={initMomX:F1}, peakKE={peakKE:F1}/<={initKE + gravPE:F1}, maxCoord={maxCoord:F1}, stackAtRest={settled} -> {(good ? "ok" : "BAD")}{(coarse ? " (coarse: bounded-only)" : "")}");
                ok &= good;
            }
        }

        // ---- Stage 5 fixtures: real triangle meshes (winding-independent — BoxVsMesh orients the face normal
        //      toward the box, SphereVsMesh uses centre−closest). ----
        // Ramp: HIGH (y=H) at x=-Xr, LOW (y=0) at x=+Xr, spanning z∈[-Zr,Zr]; downhill is +X, slope = H/(2·Xr).
        static RigidBody Ramp(float H, float Xr, float Zr)
            => RigidBody.StaticMesh(new[] { new Vector3(-Xr, H, -Zr), new Vector3(-Xr, H, Zr), new Vector3(Xr, 0f, Zr), new Vector3(Xr, 0f, -Zr) }, new[] { 0, 1, 2, 0, 2, 3 });
        // Pyramid: apex (0,H,0), square base (±B,0,±B); 4 side faces.
        static RigidBody Pyramid(float H, float B)
            => RigidBody.StaticMesh(new[] { new Vector3(0f, H, 0f), new Vector3(-B, 0f, -B), new Vector3(B, 0f, -B), new Vector3(B, 0f, B), new Vector3(-B, 0f, B) }, new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 });

        // 12) SPHERE-ON-RAMP: a sphere dropped on the REAL ramp face rolls DOWN the face (not its bounding box),
        //     with NO teleport (no large single-substep horizontal jump — the original-bug guard), bounded.
        {
            const float rad = 0.5f, H = 4f, Xr = 6f, Zr = 4f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                w.Bodies.Add(Ramp(H, Xr, Zr));
                float sx = -Xr + 1.5f, sy = H * (Xr - sx) / (2f * Xr);
                var ball = RigidBody.Sphere(new Vector3(sx, sy + rad + 0.3f, 0f), rad, 1f); w.Bodies.Add(ball);
                float startX = ball.Position.X, maxStep = 0f; Vector3 prev = ball.Position;
                int steps = (int)(6f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((ball.Position.X - prev.X) * (ball.Position.X - prev.X) + (ball.Position.Z - prev.Z) * (ball.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = ball.Position; }
                bool rolled = ball.Position.X > startX + 1f;                     // moved downhill (+X) on the real face
                bool noTeleport = maxStep < 0.5f;                                // no large single-substep horizontal jump
                bool bounded = MathF.Abs(ball.Position.X) < 40f && MathF.Abs(ball.Position.Z) < 5f && ball.Position.Y > -1f && ball.Position.Y < H + 2f;
                bool good = rolled && noTeleport && bounded;
                Console.WriteLine($"  sphere-on-ramp dt=1/{1f / dt:F0}: rolled {startX:F2}->{ball.Position.X:F2} (downhill), maxStep={maxStep:F3} (no teleport), Y={ball.Position.Y:F2} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 13) BOX-ON-RAMP REST: a box on a SHALLOW ramp settles FLAT ALIGNED to the face (bottom face ≈ parallel
        //     to the ramp — the "non-perpendicular" fix). HIGH friction -> STICKS; LOW friction -> SLIDES with a
        //     bounded speed (still aligned). No teleport.
        {
            const float bh = 0.5f, H = 2f, Xr = 6f, Zr = 4f;                    // slope 0.167, ~9.5°
            float theta = MathF.Atan(H / (2f * Xr));
            Vector3 nRamp = new Vector3(H / (2f * Xr), 1f, 0f); nRamp *= 1f / nRamp.Length();
            var alignedQ = PriviewNetworkScene.QuatFromEuler(new Vector3(0f, 0f, -theta));
            foreach (var (mu, shouldStick) in new[] { (0.8f, true), (0.03f, false) })
            {
                bool allDt = true; float lastTilt = 0f, lastDisp = 0f, lastSpeed = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                    var ramp = Ramp(H, Xr, Zr); ramp.Friction = mu; w.Bodies.Add(ramp);
                    float sx0 = -1f, sy0 = H * (Xr - sx0) / (2f * Xr);
                    Vector3 start = new Vector3(sx0, sy0, 0f) + nRamp * (bh + 0.02f);
                    var box = RigidBody.DynamicBox(start, new Vector3(bh, bh, bh), alignedQ, 1f); box.Friction = mu; w.Bodies.Add(box);
                    Vector3 startPos = box.Position, prev = box.Position; float maxStep = 0f;
                    int steps = (int)(2f / dt);   // measured while still ON the ramp (a low-μ box slides several units; keep it on the slope)
                    for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((box.Position.X - prev.X) * (box.Position.X - prev.X) + (box.Position.Z - prev.Z) * (box.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = box.Position; }
                    Vector3 boxUp = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                    float tiltVsFace = MathF.Acos(Math.Clamp(boxUp * nRamp, -1f, 1f));
                    float disp = MathF.Sqrt((box.Position.X - startPos.X) * (box.Position.X - startPos.X) + (box.Position.Z - startPos.Z) * (box.Position.Z - startPos.Z));
                    bool aligned = tiltVsFace < 0.06f;                          // bottom face parallel to the ramp (not perpendicular)
                    bool noTeleport = maxStep < 0.5f;
                    bool bounded = MathF.Abs(box.Position.X) < 20f && box.Position.Y > -1f;
                    bool motion = shouldStick ? disp < 0.15f : (disp > 0.3f && box.LinVel.Length() < 15f);
                    allDt &= aligned && noTeleport && bounded && motion; lastTilt = tiltVsFace; lastDisp = disp; lastSpeed = box.LinVel.Length();
                }
                Console.WriteLine($"  box-on-ramp-rest mu={mu} ({(shouldStick ? "stick" : "slide")}): tiltVsFace={lastTilt:F4} (aligned), disp={lastDisp:F3}, endSpeed={lastSpeed:F2} -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 14) BOX-TUMBLES-RAMP (emergent-tumble proof): a box placed OFF-BALANCE on a STEEP ramp TUMBLES over
        //     its edge down the slope (orientation rotates well past its start), moves downhill, stays BOUNDED,
        //     NO teleport, and settles — no legacy tip, no fling.
        {
            const float bh = 0.5f, H = 8f, Xr = 5f, Zr = 4f;                    // slope 0.8, ~38.7°
            float theta = MathF.Atan(H / (2f * Xr));
            var q = PriviewNetworkScene.QuatFromEuler(new Vector3(0f, 0f, -theta - 0.5f));   // aligned + extra downhill lean (past its edge)
            foreach (float dt in new[] { 1f / 60f, 1f / 30f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0.1f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var ramp = Ramp(H, Xr, Zr); ramp.Friction = 0.8f; w.Bodies.Add(ramp);
                float sx0 = -Xr + 2f, sy0 = H * (Xr - sx0) / (2f * Xr);
                var box = RigidBody.DynamicBox(new Vector3(sx0, sy0 + bh + 0.3f, 0f), new Vector3(bh, bh, bh), q, 1f); box.Friction = 0.8f; w.Bodies.Add(box);
                Vector3 startPos = box.Position; float startTilt = BoxTilt(box), maxTilt = startTilt, maxStep = 0f, lastWindow = 0f;
                Vector3 prev = box.Position, prevUp = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                int steps = (int)(12f / dt);
                for (int i = 0; i < steps; i++)
                {
                    w.Step(dt);
                    float t = BoxTilt(box); if (t > maxTilt) maxTilt = t;
                    float s = MathF.Sqrt((box.Position.X - prev.X) * (box.Position.X - prev.X) + (box.Position.Z - prev.Z) * (box.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = box.Position;
                    Vector3 up = ImpulseMath.Rotate(box.Orientation, new Vector3(0f, 1f, 0f));
                    if (i >= steps - 90) { float step = MathF.Acos(Math.Clamp(up * prevUp, -1f, 1f)); if (step > lastWindow) lastWindow = step; }
                    prevUp = up;
                }
                bool tumbled = maxTilt > startTilt + 0.6f;                       // rotated well past its start (a real tumble)
                bool movedDownhill = box.Position.X > startPos.X + 1.5f;
                bool noTeleport = maxStep < 0.5f;
                bool settled = lastWindow < 0.02f;                              // stopped rotating by the end
                bool bounded = MathF.Abs(box.Position.X) < 20f && box.Position.Y > -1f && box.Position.Y < H + 2f;
                bool good = tumbled && movedDownhill && noTeleport && settled && bounded;
                Console.WriteLine($"  box-tumbles-ramp dt=1/{1f / dt:F0}: tilt {startTilt:F2}->max {maxTilt:F2} (tumbled), movedX {startPos.X:F2}->{box.Position.X:F2}, maxStep={maxStep:F3}, settled(Δ={lastWindow:F4}) -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 15) BOX / SPHERE ON PYRAMID: dropped onto a pyramid FACE they rest/slide/roll on the REAL face — their
        //     final height is well BELOW the bbox top (y=H+size), proving it's the real triangle, not the box —
        //     with NO teleport, bounded.
        {
            const float H = 5f, B = 6f, bh = 0.5f, rad = 0.5f;
            foreach (bool isBox in new[] { true, false })
            {
                bool allDt = true; float lastY = 0f, lastStep = 0f;
                foreach (float dt in new[] { 1f / 60f, 1f / 20f })
                {
                    var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                    w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                    var pyr = Pyramid(H, B); pyr.Friction = 0.6f; w.Bodies.Add(pyr);
                    Vector3 dropAbove = new Vector3(2f * B / 3f, H / 3f + 1.2f, 0f);     // above the +X face's centroid
                    RigidBody obj = isBox ? RigidBody.DynamicBox(dropAbove, new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f) : RigidBody.Sphere(dropAbove, rad, 1f);
                    obj.Friction = 0.6f; w.Bodies.Add(obj);
                    Vector3 prev = obj.Position; float maxStep = 0f, maxY = obj.Position.Y;
                    int steps = (int)(6f / dt);
                    for (int i = 0; i < steps; i++) { w.Step(dt); float s = MathF.Sqrt((obj.Position.X - prev.X) * (obj.Position.X - prev.X) + (obj.Position.Z - prev.Z) * (obj.Position.Z - prev.Z)); if (s > maxStep) maxStep = s; prev = obj.Position; if (obj.Position.Y > maxY) maxY = obj.Position.Y; }
                    bool onRealFace = maxY < H + 0.9f;                          // never floated up to the bbox top (H + halfSize)
                    bool noTeleport = maxStep < 0.5f;
                    bool bounded = MathF.Abs(obj.Position.X) < 30f && MathF.Abs(obj.Position.Z) < 10f && obj.Position.Y > -1f;
                    allDt &= onRealFace && noTeleport && bounded; lastY = obj.Position.Y; lastStep = maxStep;
                }
                Console.WriteLine($"  {(isBox ? "box" : "sphere")}-on-pyramid: endY={lastY:F2} (< bbox top {H + (isBox ? bh : rad):F1} = real face), maxStep={lastStep:F3} (no teleport) -> {(allDt ? "ok" : "BAD")}");
                ok &= allDt;
            }
        }

        // 16) BOX-ON-FLAT-MESH (guards the live platform, which is a flat 2-triangle MESH — not a StaticBox):
        //     a box dropped on a flat mesh quad rests FLAT and STILL (tilt ~0, no jitter, pen ≤ slop) and SLEEPS.
        {
            const float bh = 0.5f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticMesh(new[] { new Vector3(-20f, 0f, -20f), new Vector3(-20f, 0f, 20f), new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, -20f) }, new[] { 0, 1, 2, 0, 2, 3 }));
            var box = RigidBody.DynamicBox(new Vector3(0.7f, 1f, -0.3f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f); w.Bodies.Add(box);
            for (int i = 0; i < 400; i++) w.Step(1f / 60f);
            Vector3 p0 = box.Position; float maxJit = 0f, maxTilt = 0f;
            for (int i = 0; i < 120; i++) { w.Step(1f / 60f); maxJit = MathF.Max(maxJit, (box.Position - p0).Length()); maxTilt = MathF.Max(maxTilt, BoxTilt(box)); }
            bool rested = MathF.Abs(box.Position.Y - bh) < slop;
            bool good = rested && maxTilt < 0.02f && maxJit < 1e-4f && box.Sleeping;
            Console.WriteLine($"  box-on-flat-mesh: restY={box.Position.Y:F4} (want {bh}), tilt={maxTilt:F5}, jitter={maxJit:F6}, sleep={box.Sleeping} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 17) BALL-ROLLING-STOPS (Stage 6 rolling friction): a sphere given horizontal velocity on flat ground
        //     rolls, DECELERATES, and comes to REST + SLEEPS within a bounded distance — NOT perpetual. Rolling
        //     friction never reverses the ball or injects energy.
        {
            const float rad = 0.5f;
            foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 20f })
            {
                var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
                w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
                var ball = RigidBody.Sphere(new Vector3(0f, rad, 0f), rad, 1f); ball.LinVel = new Vector3(4f, 0f, 0f); w.Bodies.Add(ball);
                float startX = ball.Position.X; bool reversed = false;
                int steps = (int)(40f / dt);
                for (int i = 0; i < steps; i++) { w.Step(dt); if (ball.LinVel.X < -0.05f) reversed = true; if (ball.Sleeping) break; }
                float stopDist = ball.Position.X - startX;
                bool stopped = ball.Sleeping && ball.LinVel.Length() < 0.05f && ball.AngVel.Length() < 0.1f;
                bool bounded = stopDist > 0.5f && stopDist < 40f;               // rolled forward a FINITE distance (not perpetual)
                bool good = stopped && bounded && !reversed;
                Console.WriteLine($"  ball-rolling-stops dt=1/{1f / dt:F0}: stopDist={stopDist:F2}, |v|={ball.LinVel.Length():F4}, |w|={ball.AngVel.Length():F4}, sleep={ball.Sleeping}, reversed={reversed} -> {(good ? "ok" : "BAD")}");
                ok &= good;
            }
        }

        // 18) BALL-SCATTERS-STACK-SLEEPS (fixes Stage-4's "doesn't fully sleep"): re-run the scatter; now the
        //     ball STOPS (rolling friction) so the WHOLE scene — ball + all boxes — eventually SLEEPS as one
        //     island, staying bounded.
        {
            const float bh = 0.5f, rad = 0.5f, v0 = 8f;
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
            var all = new List<RigidBody>();
            for (int k = 0; k < 3; k++) { var bx = RigidBody.DynamicBox(new Vector3(0f, bh + k * 2f * bh, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.Quat.Identity, 1f); all.Add(bx); w.Bodies.Add(bx); }
            var ball = RigidBody.Sphere(new Vector3(-2.5f, 1.5f, 0f), rad, 1f); ball.LinVel = new Vector3(v0, 0f, 0f); all.Add(ball); w.Bodies.Add(ball);
            bool allSleep = false; int sleptAt = 0;
            int steps = (int)(30f / (1f / 60f));
            for (int i = 0; i < steps; i++) { w.Step(1f / 60f); if (all.TrueForAll(b => b.Sleeping)) { allSleep = true; sleptAt = i; break; } }
            bool bounded = all.TrueForAll(b => MathF.Abs(b.Position.X) < 20f && MathF.Abs(b.Position.Z) < 10f && b.Position.Y > -1f);
            bool good = allSleep && bounded;
            Console.WriteLine($"  ball-scatters-stack-sleeps: wholeSceneSleeps={allSleep} (at ~{sleptAt / 60f:F1}s), bounded={bounded} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 19) REGRESSION — rolling friction only DAMPS, it does NOT freeze legitimate motion: a ball on a GENTLE
        //     slope STILL rolls down (gravity beats the small rolling resistance), and a box on a STEEP ramp
        //     STILL tumbles (Stage 5 preserved).
        {
            // (a) gentle slope: ball still rolls down
            const float rad = 0.5f, Hg = 2f, Xr = 8f, Zr = 4f;                  // slope 0.125, ~7.1°
            var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0f };
            w.Bodies.Add(Ramp(Hg, Xr, Zr));
            float sx = -Xr + 1.5f, sy = Hg * (Xr - sx) / (2f * Xr);
            var ball = RigidBody.Sphere(new Vector3(sx, sy + rad + 0.2f, 0f), rad, 1f); w.Bodies.Add(ball);
            for (int i = 0; i < (int)(6f / (1f / 60f)); i++) w.Step(1f / 60f);
            bool stillRolls = ball.Position.X > sx + 1.5f;                       // gravity overcame rolling friction on the gentle slope
            // (b) steep ramp: box still tumbles
            const float bh = 0.5f, Hs = 8f;
            float thetaS = MathF.Atan(Hs / (2f * 5f));
            var w2 = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = 0.1f };
            w2.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
            var ramp2 = Ramp(Hs, 5f, 4f); ramp2.Friction = 0.8f; w2.Bodies.Add(ramp2);
            float s2 = -5f + 2f, sy2 = Hs * (5f - s2) / (2f * 5f);
            var box = RigidBody.DynamicBox(new Vector3(s2, sy2 + bh + 0.3f, 0f), new Vector3(bh, bh, bh), PriviewNetworkScene.QuatFromEuler(new Vector3(0f, 0f, -thetaS - 0.5f)), 1f); box.Friction = 0.8f; w2.Bodies.Add(box);
            float startTilt = BoxTilt(box), maxTilt = startTilt;
            for (int i = 0; i < (int)(6f / (1f / 60f)); i++) { w2.Step(1f / 60f); float t = BoxTilt(box); if (t > maxTilt) maxTilt = t; }
            bool stillTumbles = maxTilt > startTilt + 0.6f && box.Position.X > s2 + 1f;
            bool good = stillRolls && stillTumbles;
            Console.WriteLine($"  slope-still-rolls/steep-still-tumbles: ball rolled to X={ball.Position.X:F2} (>{sx + 1.5f:F2})={stillRolls}, box tumbled tilt {startTilt:F2}->{maxTilt:F2} + movedX={stillTumbles} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        Console.WriteLine(ok ? "IMPULSE TEST PASSED" : "IMPULSE TEST FAILED");
    }

    // Stage-7a CUTOVER: the impulse solver is now the DEFAULT, and its RigidBody state streams to peers.
    // Asserts (1) a world with no explicit engine resolves to "impulse" (legacy still selectable), and
    // (2) the PhysicsSync round-trip: an authority steps the impulse solver; each batch its state is
    // serialized -> deserialized -> applied on a CLIENT that dead-reckons + eases, and the client's
    // reconstructed Position AND Orientation track the authority within tolerance across batch rates.
    static void CutoverSelfTest()
    {
        Console.WriteLine("=== CUTOVER SELF-TEST (Stage 7a: impulse default + multiplayer sync) ===");
        bool ok = true;
        // Tilt (rad) of a body's local up-axis away from world vertical (0 = upright).
        static float TiltOfV(Vector3 lr) { Vector3 up = new Vector3(0f, 1f, 0f).Rotate(lr); return MathF.Acos(Math.Clamp(up.Y, -1f, 1f)); }

        // 1) SINGLE ENGINE: the "engine" switch was retired (Stage 7b) — there is one solver. A world JSON that
        //    still carries a STALE "engine" key (from an older save) must LOAD GRACEFULLY (the obsolete key is
        //    ignored, not a parse error), keeping the rest of the physics block intact.
        {
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            WorldConfig? back = null; bool loaded = true;
            try { back = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>("{\"Name\":\"x\",\"Physics\":{\"GravityEnabled\":true,\"GravityStrength\":7.5,\"Engine\":\"legacy\"}}", opts); }
            catch { loaded = false; }
            bool fieldsKept = loaded && back != null && back.Physics.GravityEnabled && Math.Abs(back.Physics.GravityStrength - 7.5f) < 1e-4f;
            bool good = loaded && fieldsKept;
            Console.WriteLine($"  stale-engine-key-loads: parsed={loaded}, gravity-kept={fieldsKept} (obsolete \"engine\":\"legacy\" ignored) -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        // 2) PHYSICS-SYNC ROUND-TRIP: authority (impulse) vs a client that only dead-reckons + eases. The box
        //    tumbles down a ramp (sustained translation X/Y + rotation), then settles — so the sync is exercised
        //    while the body is genuinely FALLING / SLIDING / TUMBLING, not just at rest.
        static WorldConfig SyncWorld() => new WorldConfig
        {
            Name = "synctest", Graphics = new GraphicsConfig { Shadows = false },
            Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 60f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0.1f },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 0, Type = "ramp", Color = "White",
                    Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 4f, Collides = true, Gravity = false },
                new WorldObject { Id = 1, Type = "cube", Color = "Red",
                    Position = new Vec3Config { X = 2f, Y = 7f, Z = 0f },
                    Rotation = new Vec3Config { X = 0f, Y = 0f, Z = 1.05f },   // off-balance -> falls, tumbles down the ramp, settles
                    Scale = 1f, Collides = true, Gravity = true },
            },
        };
        static PhysicsSyncPacket RoundTrip(PhysicsSyncPacket p)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) p.Serialize(w);
            ms.Position = 0;
            var recv = new PhysicsSyncPacket();
            using (var r = new BinaryReader(ms)) recv.Deserialize(r);
            return recv;
        }

        foreach (int batchEvery in new[] { 3, 6 })                 // ~20 Hz and ~10 Hz batches @ 60 fps
        {
            var authority = new PriviewNetworkScene(new DisplayManagerAsync(), SyncWorld(), isServer: false, "127.0.0.1", 0, online: false);
            authority.Start();
            var client = new PriviewNetworkScene(new DisplayManagerAsync(), SyncWorld(), isServer: false, "127.0.0.1", 0, online: false);
            client.Start();
            // give the authority box a little horizontal + spin so it translates in X/Z AND rotates (exercises full LinVel + AngVel).
            var aEntry = authority.EditableEntries.First(e => e.Instance.Gravity);
            var aBox = aEntry.Instance; int id = aEntry.Descriptor.Id;
            Vector3 startPos = aBox.Position; float startTilt = TiltOfV(aBox.LocalRotate);
            float dt = 1f / 60f, maxPosErr = 0f, maxRotErr = 0f, aMoved = 0f, aRotated = 0f;
            for (int frame = 0; frame < 480; frame++)
            {
                authority.StepPhysicsForTest(dt);
                if (frame % batchEvery == 0) client.ReceivePhysicsSyncForTest(RoundTrip(authority.SnapshotPhysicsSyncForTest()));
                client.StepNetworkPhysicsForTest(dt);
                aMoved = MathF.Max(aMoved, (aBox.Position - startPos).Length());
                aRotated = MathF.Max(aRotated, MathF.Abs(TiltOfV(aBox.LocalRotate) - startTilt));
                if (frame > 15)                                    // after the first couple of batches, measure tracking THROUGHOUT the motion
                {
                    var cBox = client.EditableEntries.First(e => e.Descriptor.Id == id).Instance;
                    float posErr = (cBox.Position - aBox.Position).Length();
                    Vector3 aUp = new Vector3(0f, 1f, 0f).Rotate(aBox.LocalRotate), cUp = new Vector3(0f, 1f, 0f).Rotate(cBox.LocalRotate);
                    float rotErr = MathF.Acos(Math.Clamp(aUp * cUp, -1f, 1f));
                    if (posErr > maxPosErr) maxPosErr = posErr;
                    if (rotErr > maxRotErr) maxRotErr = rotErr;
                }
            }
            var cFinal = client.EditableEntries.First(e => e.Descriptor.Id == id).Instance;
            float finalPos = (cFinal.Position - aBox.Position).Length();
            Vector3 aU = new Vector3(0f, 1f, 0f).Rotate(aBox.LocalRotate), cU = new Vector3(0f, 1f, 0f).Rotate(cFinal.LocalRotate);
            float finalRot = MathF.Acos(Math.Clamp(aU * cU, -1f, 1f));
            bool actuallyMoved = aMoved > 2f && aRotated > 0.5f;   // the authority genuinely fell + tumbled (not a trivial pass)
            bool tracks = maxPosErr < 0.6f && maxRotErr < 0.6f;    // client stayed close THROUGHOUT the motion (dead-reckon + ease)
            bool converged = finalPos < 0.05f && finalRot < 0.05f; // and matches exactly once at rest
            bool good = actuallyMoved && tracks && converged;
            Console.WriteLine($"  physics-sync-roundtrip batch=1/{batchEvery}: authorityMoved={aMoved:F2}/rotated={aRotated:F2}, maxPosErr={maxPosErr:F3}, maxRotErr={maxRotErr:F3}, finalPosErr={finalPos:F4}, finalRotErr={finalRot:F4} -> {(good ? "ok" : "BAD")}");
            ok &= good;
        }

        Console.WriteLine(ok ? "CUTOVER TEST PASSED" : "CUTOVER TEST FAILED");
    }

    // A static ground box (top face at y=0) + a dynamic sphere dropped from dropY, for the impulse test.
    static ImpulseWorld MakeImpulseWorld(float e, float g, float radius, float dropY, out RigidBody ball)
    {
        var w = new ImpulseWorld { Gravity = new Vector3(0f, -g, 0f), Restitution = e };
        w.Bodies.Add(RigidBody.StaticBox(new Vector3(0f, -1f, 0f), new Vector3(50f, 1f, 50f), PriviewNetworkScene.Quat.Identity));
        ball = RigidBody.Sphere(new Vector3(0f, dropY, 0f), radius, 1f);
        w.Bodies.Add(ball);
        return w;
    }

    // A deterministic smooth-shaded UV sphere mesh (lat×lon grid), used by gputest to give the GPU
    // BVH a real, deep tree to traverse (>64 tris). Vertex normals = unit position (smooth shading).
    static Object3d BuildUvSphere(float radius, int lat, int lon)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        int stride = lon + 1;
        for (int i = 0; i <= lat; i++)
        {
            float theta = MathF.PI * i / lat;
            for (int j = 0; j <= lon; j++)
            {
                float phi = MathF.Tau * j / lon;
                float x = MathF.Sin(theta) * MathF.Cos(phi);
                float y = MathF.Cos(theta);
                float z = MathF.Sin(theta) * MathF.Sin(phi);
                verts.Add(new Vector3(x, y, z) * radius);
                norms.Add(new Vector3(x, y, z));
            }
        }
        var faces = new List<FacingInfo>();
        for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                int a = i * stride + j + 1, b = i * stride + j + 2;            // 1-based
                int c = (i + 1) * stride + j + 1, d = (i + 1) * stride + j + 2;
                faces.Add(new FacingInfo(new[] { a, d, b }, new[] { a, d, b }));
                faces.Add(new FacingInfo(new[] { a, c, d }, new[] { a, c, d }));
            }
        return new Object3d(verts.ToArray(), norms.ToArray(), faces.ToArray());
    }

    // A tiny deterministic scene (one cube + one sphere + one point light) used only by gputest.
    sealed class GpuTestScene : Scene
    {
        public GpuTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = true;

            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(10f, 0f, 0f);
            cube.Scale = 1.6f;
            cube.Color = new Rgba32(220, 50, 50);
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            var sphere = new Sphere(new Vector3(8f, -1f, 2.6f), Vector3.Zero, 1.3f) { Color = new Rgba32(60, 90, 230) };
            AddDisplaysObject(sphere);

            // A TRANSPARENT sphere directly in front of the cube — exercises front-to-back compositing
            // (a semi-transparent layer over the opaque cube behind it).
            var glass = new Sphere(new Vector3(4f, 0f, 0f), Vector3.Zero, 1.2f) { Color = new Rgba32(70, 230, 120, 128) };
            AddDisplaysObject(glass);

            // A high-poly, rotated mesh (1536 tris) — gives the GPU two-level BVH a real, deep tree to
            // traverse in the object's local space (with a non-trivial rotation/scale transform).
            var hi = BuildUvSphere(1f, 24, 32);
            hi.Position = new Vector3(11f, 2f, 1f);
            hi.Scale = 1.8f;
            hi.LocalRotate = new Vector3(0.4f, 0.7f, 0.2f);
            hi.Color = new Rgba32(200, 180, 60);
            hi.ColorFade = 0.5f;   // exercise colour-paleness: baked into both the CPU shade and the GPU snapshot
            hi.UpdateGeometry();
            hi.BuildAcceleration();
            AddDisplaysObject(hi);

            // One of every LightKind, with the rich extras: a point, a multi-beam SQUARE spot, and a
            // TRIANGLE area light — so the GPU/CPU parity test covers beams, cone shapes and area sampling.
            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.9f, 0.8f) });

            AddLight(new Light(new Vector3(6f, 6f, 0f), 800f)
            {
                Kind = LightKind.Spot, Direction = new Vector3(0.3f, -1f, 0f).Norm(),
                ConeAngleDeg = 35f, BeamCount = 2, ConeShape = ConeShapeKind.Square,
                Rgb = new Vector3(0.7f, 0.8f, 1f), ColorFade = 0.4f,   // pale the emission (parity check)
            });

            AddLight(new Light(new Vector3(6f, 6f, -4f), 400f)
            {
                Kind = LightKind.Area, Direction = new Vector3(0f, -1f, 0.3f).Norm(),
                AreaSize = 1.5f, AreaShape = ConeShapeKind.Triangle,
                Rgb = new Vector3(1f, 0.85f, 0.7f),
            });

            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-2 texture-parity scene: a TEXTURED cube (a known unique-texel image) plus an UNtextured cube,
    // so the GPU texture path and the flat-colour path are compared side by side against the CPU. Shadows
    // are off (this scene exists to prove texel-fetch parity is EXACT, independent of the shadow band).
    sealed class GpuTextureTestScene : Scene
    {
        public GpuTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            // A small texture with a UNIQUE texel per cell, so any wrong fetch (bad UV / wrap / channel
            // order / offset) shows up as a colour mismatch rather than blending away.
            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gputex");

            // Textured cube, tilted so several faces show and the UV interpolation is genuinely 3D.
            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(8f, 0f, 0f);
            cube.Scale = 2.2f;
            cube.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            cube.Color = Rgba32.White;   // unused when textured (both renderers sample the texel), but a bug would show white
            cube.Texture = tex;
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            // A second, UNtextured cube alongside — textured + flat objects must coexist at parity.
            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(11f, 1.6f, 2.2f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(80, 200, 120);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            // Textured RAMP + PYRAMID (Stage 3) — Object3d meshes whose procedural per-corner UVs
            // interpolate barycentrically exactly like the cube, so they must hit the SAME texel-exact
            // parity (Δ=0 interior). Tilted so several faces + the UV interpolation are genuinely 3D.
            var ramp = PriviewNetworkScene.CreateRamp();
            ramp.Position = new Vector3(9f, -1.9f, -2.2f);
            ramp.Scale = 1.3f;
            ramp.LocalRotate = new Vector3(0.2f, 0.6f, 0.05f);
            ramp.Texture = tex;
            ramp.UpdateGeometry();
            AddDisplaysObject(ramp);

            var pyr = PriviewNetworkScene.CreatePyramid();
            pyr.Position = new Vector3(10f, 1.9f, -2.4f);
            pyr.Scale = 1.3f;
            pyr.LocalRotate = new Vector3(0.1f, 0.9f, 0.15f);
            pyr.Texture = tex;
            pyr.UpdateGeometry();
            AddDisplaysObject(pyr);

            // Textured FLAT PICTURE (Stage 3b) — a two-sided vertical quad, rotated to face the camera and
            // placed CLOSE + in front so it's unoccluded. Its linear quad UVs must be texel-EXACT (Δ=0).
            var pic = PriviewNetworkScene.CreateFlatPicture();
            pic.Position = new Vector3(5.5f, 0f, 1.3f);
            pic.Scale = 1.4f;
            pic.LocalRotate = new Vector3(0.1f, 1.3f, 0.05f);
            pic.Texture = tex;
            pic.UpdateGeometry();
            AddDisplaysObject(pic);

            // Textured CYLINDER + CONE (Stage-3 tail) — their per-face-corner UVs interpolate linearly like
            // every other mesh primitive, so they too must be texel-EXACT (Δ=0). Placed clear of the others.
            var cyl = PriviewNetworkScene.CreateCylinder();
            cyl.Position = new Vector3(7f, 2.2f, -1.5f);
            cyl.Scale = 1.2f;
            cyl.LocalRotate = new Vector3(0.25f, 0.4f, 0.1f);
            cyl.Texture = tex;
            cyl.UpdateGeometry();
            AddDisplaysObject(cyl);

            var cone = PriviewNetworkScene.CreateCone();
            cone.Position = new Vector3(7f, -2.2f, 1.8f);
            cone.Scale = 1.2f;
            cone.LocalRotate = new Vector3(0.15f, 0.7f, 0.2f);
            cone.Texture = tex;
            cone.UpdateGeometry();
            AddDisplaysObject(cone);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });

            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-3 sphere-texture parity: a TEXTURED sphere (analytic equirectangular UV via atan2/asin) beside
    // an UNtextured cube. Because atan2/asin round slightly differently on the GPU, a THIN seam/pole band
    // may differ CPU↔GPU (tolerated like the shadow band); the untextured cube stays exact. Shadows off.
    sealed class GpuSphereTextureTestScene : Scene
    {
        public GpuSphereTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            // Unique-texel image (G held at 100 so every hit stays above the brightness threshold — a dark
            // texel at a seam pixel must not read as a background "miss" and inflate the silhouette count).
            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(40 + x * 24), 100, (byte)(40 + y * 24), 255);
            var tex = new Texture(TW, TH, px, "gpusphere");

            // Zero LocalRotate: the analytic intersection is then identical on CPU and GPU, so the ONLY
            // divergence is the transcendental UV — a clean measurement of the seam/pole band.
            var ball = new Sphere(new Vector3(7f, 0f, 0f), Vector3.Zero, 1.7f) { Color = Rgba32.White, Texture = tex };
            AddDisplaysObject(ball);

            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(11f, 1.4f, 2.6f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(90, 160, 210);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-4 texture-PARAMS parity: a TILED cube (TextureScale=2 → 2×2) and a SINGLE-FACE cube (only its
    // +Z group textured, the other 5 faces flat colour). Both the scale and the per-face gate are exact
    // (integer group compare + linear UV), so CPU↔GPU must be Δ=0. Shadows off.
    sealed class GpuTextureParamsTestScene : Scene
    {
        public GpuTextureParamsTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpuparams");

            // TILED cube — TextureScale=2 tiles the image 2×2 on every face.
            var tiled = PriviewNetworkScene.CreateCube();
            tiled.Position = new Vector3(8f, 0f, 0f);
            tiled.Scale = 2.2f;
            tiled.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            tiled.Texture = tex;
            tiled.TextureScale = 2f;
            tiled.UpdateGeometry();
            AddDisplaysObject(tiled);

            // SINGLE-FACE cube — only the +Z group (4) is textured; the other faces show flat colour.
            var oneFace = PriviewNetworkScene.CreateCube();
            oneFace.Position = new Vector3(11f, 1.6f, 2.4f);
            oneFace.Scale = 1.6f;
            oneFace.LocalRotate = new Vector3(0.2f, 0.9f, 0.1f);
            oneFace.Color = new Rgba32(70, 160, 90);
            oneFace.Texture = tex;
            oneFace.TextureFace = 4;
            oneFace.UpdateGeometry();
            AddDisplaysObject(oneFace);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // A2 BILINEAR parity: a BILINEAR-filtered textured cube. Bilinear blends 4 texels with float lerps that
    // round slightly differently on GPU (XMath) vs CPU (MathF), so — UNLIKE nearest — a THIN band of interior
    // pixels may differ by ~1; we require only that the band stay thin (like the sphere seam), NOT Δ=0. A
    // small 8×8 texture magnified over the cube makes most pixels land BETWEEN texels, genuinely blending.
    sealed class GpuBilinearTextureTestScene : Scene
    {
        public GpuBilinearTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpubilinear");

            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(8f, 0f, 0f);
            cube.Scale = 2.4f;
            cube.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            cube.Texture = tex;
            cube.TextureFilter = TextureFilterMode.Bilinear;   // the opt-in smoothing under test
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-5 imported-mesh parity: a TEXTURED .obj mesh (loaded via ObjLoader, so its per-corner UVs came
    // from the file's `vt` with the v-flip) beside an untextured cube. Imported UVs interpolate linearly
    // like the cube, so CPU↔GPU must be Δ=0. Shadows off. The mesh is supplied by the caller (a fixture).
    sealed class GpuImportedMeshTestScene : Scene
    {
        private readonly Object3d _mesh;
        public GpuImportedMeshTestScene(Object3d mesh) : base(new DisplayManagerAsync()) { _mesh = mesh; }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpuimport");

            // The fixture quad's normal faces -X (toward the camera at the origin); a small tilt makes the
            // UV interpolation genuinely 3D. Placed close + in front so it's unoccluded.
            _mesh.Position = new Vector3(6f, 0f, 0f);
            _mesh.Scale = 1.6f;
            _mesh.LocalRotate = new Vector3(0.1f, 0.15f, 0.05f);
            _mesh.Texture = tex;
            _mesh.UpdateGeometry();
            AddDisplaysObject(_mesh);

            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(10f, 1.4f, 2f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(90, 160, 210);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // gputest: render a fixed scene with the GPU kernel (on whatever accelerator ILGPU finds — CUDA on
    // an NVIDIA box, else the managed CPU accelerator in CI) and compare it to the engine's own CPU
    // raytracer pixel-by-pixel. They share the same intersection + shading + tone-map math for the
    // supported "fast path", so an opaque scene with a point light must match within float epsilon
    // (a few silhouette-edge pixels may differ on rounding — that is the small allowed budget).
    static void GpuSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== GPU SELF-TEST ===");

        try
        {
            var scene = new GpuTestScene();
            scene.Start();

            const int W = 64, H = 32;
            const float aspect = 1.6f;

            using var rt = new Nova3DVisualiser.Gpu.GpuRaytracer(requireGpu: false);
            Console.WriteLine($"  accelerator: {rt.AcceleratorName} (hardware GPU = {rt.IsHardwareGpu})");

            var brightness = new float[W * H];
            var color = new Rgb24[W * H];

            // Compares the GPU image to the engine's CPU image for the current shadow setting, counting
            // a pixel as a mismatch only when it exceeds (bTol, cTol). Mismatches are split into "edge"
            // (one renderer hit geometry, the other saw background — a silhouette float flip) and
            // "interior" (both hit the surface but the shaded/shadowed value differs). With shadows off
            // every feature is deterministic, so interior MUST be 0; with shadows on, soft/hard shadow
            // boundaries add a benign band of interior flips.
            (int nonBlack, int edge, int interior, float worstB, int worstC) Compare(Scene s, float bTol, int cTol)
            {
                SceneSnapshot snap = s.BuildSnapshot();
                rt.Render(snap, W, H, aspect, brightness, color);

                int nb = 0, edge = 0, interior = 0; float wb = 0f; int wc = 0;
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++)
                    {
                        float uvx = ((float)i / (W - 1) * 2f - 1f) * aspect;
                        float uvy = -((float)j / (H - 1) * 2f - 1f);
                        var cpu = s.GetPixelData(new Vector2(uvx, uvy));
                        int idx = j * W + i;
                        if (cpu.Brightness > 0.01f) nb++;

                        float db = Math.Abs(cpu.Brightness - brightness[idx]);
                        int dc = Math.Max(Math.Abs(cpu.Color.R - color[idx].R),
                                 Math.Max(Math.Abs(cpu.Color.G - color[idx].G), Math.Abs(cpu.Color.B - color[idx].B)));
                        if (db <= bTol && dc <= cTol) continue;

                        bool cpuHit = cpu.Brightness > 0.01f, gpuHit = brightness[idx] > 0.01f;
                        if (cpuHit != gpuHit) edge++;                 // silhouette hit/miss flip
                        else { interior++; wb = Math.Max(wb, db); wc = Math.Max(wc, dc); }
                    }
                return (nb, edge, interior, wb, wc);
            }

            int total = W * H;

            // Pass A — shadows OFF: intersection + transparency compositing + every light kind (incl.
            // beams, cone shapes, area sampling) + tone-map are all deterministic, so the GPU image must
            // match the CPU EXACTLY. ANY interior mismatch here (Δ>0) is a real kernel bug.
            scene.EnableShadows = false;
            var a = Compare(scene, 0.004f, 1);   // tiny: absorbs ULP/rounding noise, far below any feature-level diff
            Console.WriteLine($"  [no shadows] nonBlack={a.nonBlack}, edge={a.edge}, interior={a.interior} (worstΔb={a.worstB:F4}, worstΔc={a.worstC})");

            // Pass B — shadows ON: shadow rays are float-sensitive at boundaries (and the area light's
            // 4 occlusion samples form a soft penumbra), so a band of pixels differs by a SMALL amount.
            // We tolerate sub-penumbra noise per pixel and only require the disagreement to stay bounded
            // in magnitude and not become pervasive (which a systematic shadow bug would).
            scene.EnableShadows = true;
            var b = Compare(scene, 0.1f, 28);
            Console.WriteLine($"  [shadows]    nonBlack={b.nonBlack}, edge={b.edge}, interior={b.interior} (worstΔb={b.worstB:F4}, worstΔc={b.worstC})");

            // Pass C — TEXTURED parity (Stage 2), shadows OFF: a textured cube must fetch the SAME texel on
            // GPU and CPU. Interior must be EXACT (Δ=0). A texel-BOUNDARY band can arise where CPU (the box
            // takes the world-space non-BVH path) and GPU (local-space BVH) barycentric noise straddles a
            // texel edge — tolerated as a thin band (the analog of the shadow band) and reported explicitly.
            var texScene = new GpuTextureTestScene();
            texScene.Start();
            rt.ResetGeometryCache();   // reuse one raytracer across scenes: force a geometry re-upload (versions can collide)
            var c = Compare(texScene, 0.004f, 1);
            int texBand = c.interior;                                    // texel-edge float flips, if any
            Console.WriteLine($"  [textured]   nonBlack={c.nonBlack}, edge={c.edge}, interior={c.interior} (worstΔb={c.worstB:F4}, worstΔc={c.worstC}), texel-boundary band={texBand}");

            // Pass D — TEXTURED SPHERE (Stage 3), shadows OFF: the equirectangular UV uses atan2/asin, which
            // round slightly differently on the GPU, so a THIN seam/pole band differs (the analog of the
            // shadow band). The mesh primitives (Pass C) stay EXACT; here we only require the band to be thin.
            var sphScene = new GpuSphereTextureTestScene();
            sphScene.Start();
            rt.ResetGeometryCache();   // new scene → force a geometry re-upload
            var e = Compare(sphScene, 0.004f, 1);
            Console.WriteLine($"  [tex-sphere] nonBlack={e.nonBlack}, edge={e.edge}, seam/pole band={e.interior} (worstΔb={e.worstB:F4}, worstΔc={e.worstC})");

            // Pass F — TEXTURE PARAMS (Stage 4), shadows OFF: a TILED cube (TextureScale=2) + a SINGLE-FACE
            // cube (only +Z textured, the rest flat). Both the tiling and the per-face gate are exact
            // (integer group compare + linear UV), so CPU↔GPU must be EXACT (Δ=0, band 0).
            var paramScene = new GpuTextureParamsTestScene();
            paramScene.Start();
            rt.ResetGeometryCache();
            var pf = Compare(paramScene, 0.004f, 1);
            Console.WriteLine($"  [tex-params] nonBlack={pf.nonBlack}, edge={pf.edge}, interior={pf.interior} (worstΔb={pf.worstB:F4}, worstΔc={pf.worstC})");

            // Pass G — IMPORTED-MESH texture (Stage 5), shadows OFF: a textured .obj (UVs parsed from `vt`
            // via ObjLoader, v-flipped) must be texel-EXACT CPU↔GPU (Δ=0) — its per-corner UVs interpolate
            // linearly like the cube. Loaded from an in-test fixture .obj so no models/ file is needed.
            const string gpuQuadObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nv 0 1 -1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn -1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n";
            var importMesh = ObjLoader.Load(WriteObjFixture("uvquad", gpuQuadObj));
            var importScene = new GpuImportedMeshTestScene(importMesh);
            importScene.Start();
            rt.ResetGeometryCache();
            var pg = Compare(importScene, 0.004f, 1);
            Console.WriteLine($"  [tex-import] nonBlack={pg.nonBlack}, edge={pg.edge}, interior={pg.interior} (worstΔb={pg.worstB:F4}, worstΔc={pg.worstC})");

            // Pass H — BILINEAR (A2), shadows OFF: an opt-in bilinear-filtered cube. The 4-texel float blend
            // rounds slightly differently on GPU (XMath) vs CPU (MathF), so we tolerate ±1 per pixel and only
            // require the BAND beyond that (Δc>=2) to stay THIN — NOT Δ=0. The nearest passes above prove the
            // default filter is untouched (still exact). Report the band + worstΔ.
            var bilScene = new GpuBilinearTextureTestScene();
            bilScene.Start();
            rt.ResetGeometryCache();
            var ph = Compare(bilScene, 0.004f, 1);
            Console.WriteLine($"  [tex-bilinear] nonBlack={ph.nonBlack}, edge={ph.edge}, band={ph.interior} (worstΔb={ph.worstB:F4}, worstΔc={ph.worstC})");

            // A3 — TARGETED TEXTURE RE-UPLOAD. (correctness) a texture-version-only bump re-renders the same
            // image BYTE-IDENTICALLY via the pool-only upload path; (behaviour) it re-uploads the texture pool
            // but NOT the geometry, while a geometry-version bump does re-upload geometry — proving the swap is
            // targeted, not a full geometry re-upload, with the output unchanged.
            bool reuploadOk;
            {
                var reScene = new GpuTextureTestScene();
                reScene.Start();
                rt.ResetGeometryCache();
                var snap = reScene.BuildSnapshot();

                var bright1 = new float[W * H]; var col1 = new Rgb24[W * H];
                int g0 = rt.GeometryUploads, t0 = rt.TextureUploads;
                rt.Render(snap, W, H, aspect, bright1, col1);                 // first frame: uploads both
                int gFirst = rt.GeometryUploads - g0, tFirst = rt.TextureUploads - t0;

                var bright2 = new float[W * H]; var col2 = new Rgb24[W * H];
                int g1 = rt.GeometryUploads, t1 = rt.TextureUploads;
                snap.TextureVersion++;                                        // simulate a live texture swap
                rt.Render(snap, W, H, aspect, bright2, col2);
                int gTex = rt.GeometryUploads - g1, tTex = rt.TextureUploads - t1;

                bool identical = true;                                        // byte-identical across the two paths
                for (int i = 0; i < W * H; i++)
                    if (col1[i].R != col2[i].R || col1[i].G != col2[i].G || col1[i].B != col2[i].B || bright1[i] != bright2[i])
                    { identical = false; break; }

                int g2 = rt.GeometryUploads;
                var bright3 = new float[W * H]; var col3 = new Rgb24[W * H];
                snap.GeometryVersion++;                                       // a genuine geometry change
                rt.Render(snap, W, H, aspect, bright3, col3);
                int gGeom = rt.GeometryUploads - g2;

                reuploadOk = gFirst == 1 && tFirst == 1                       // first frame uploads geometry + pool
                          && gTex == 0 && tTex == 1                           // texture-only: pool re-uploaded, geometry NOT
                          && identical                                        // ...and the image is byte-identical
                          && gGeom == 1;                                      // geometry change re-uploads geometry
                Console.WriteLine($"  [tex-reupload] first(g={gFirst},t={tFirst}), texSwap(g={gTex},t={tTex},identical={identical}), geomChange(g={gGeom}) -> {(reuploadOk ? "ok" : "BAD")}");
            }

            bool ok = a.nonBlack > 50
                      && a.interior == 0 && a.edge == 0                                          // untextured shading exact (Δ=0)
                      && (float)b.interior / total < 0.06f && (float)b.edge / total < 0.06f      // shadow boundary thin
                      && c.nonBlack > 50 && c.edge == 0
                      && (float)texBand / total < 0.02f                                          // textured mesh interior exact but for a thin texel-edge band
                      && e.nonBlack > 50 && (float)e.edge / total < 0.02f
                      && (float)e.interior / total < 0.08f                                       // sphere seam/pole band thin
                      && pf.nonBlack > 50 && pf.edge == 0 && pf.interior == 0                    // tiled + single-face cubes exact (Δ=0)
                      && pg.nonBlack > 50 && pg.edge == 0 && pg.interior == 0                    // imported textured mesh exact (Δ=0)
                      && ph.nonBlack > 50 && (float)ph.edge / total < 0.02f
                      && (float)ph.interior / total < 0.05f                                      // bilinear band thin (NOT Δ=0 — float rounding)
                      && reuploadOk;                                                             // A3: texture swap is targeted + output unchanged
            Console.WriteLine(ok ? "GPU TEST PASSED" : "GPU TEST FAILED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("GPU TEST FAILED");
        }
    }
}