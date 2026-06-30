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
using SampleGame.Scenes;
using SampleGame.Worlds;
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
        if (args.Length > 0 && args[0] == "bvhtest") { BvhSelfTest(); return; }
        if (args.Length > 0 && args[0] == "worldtest") { WorldSelfTest(); return; }
        if (args.Length > 0 && args[0] == "editortest") { EditorSelfTest(); return; }
        if (args.Length > 0 && args[0] == "picktest") { PickSelfTest(); return; }
        if (args.Length > 0 && args[0] == "worldsynctest") { WorldSyncSelfTest(); return; }
        if (args.Length > 0 && args[0] == "colortest") { ColorSelfTest(); return; }
        if (args.Length > 0 && args[0] == "collisiontest") { CollisionSelfTest(); return; }
        if (args.Length > 0 && args[0] == "physicstest") { PhysicsSelfTest(); return; }
        if (args.Length > 0 && args[0] == "gputest") { GpuSelfTest(); return; }

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
            Platform = new PlatformConfig { Enabled = true, Size = 12f, Color = "Cyan", Collides = false, Gravity = true, Position = new Vec3Config { X = 1.5f, Y = -0.5f, Z = 2.5f } },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 12.5f, CollisionEnabled = false, Restitution = 0.4f },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 10, Type = "mesh", Mesh = "monkey",
                    Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
                    Rotation = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                    Scale = 1.5f, Color = "Red", Anchor = "Center", RotateSpeed = 0.5f, Radius = 1f, Collider = "obb" },
                new WorldObject { Id = 11, Type = "cube",
                    Position = new Vec3Config { X = -1f, Y = 0f, Z = 2f },
                    Scale = 2f, Color = "Green", Collides = false, Gravity = true, Mass = 3.5f, Restitution = 0.8f, ColorFade = 0.5f },
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
            }
        };

        // Pack, then round-trip the packet through its own byte (de)serialization.
        WorldSyncPacket packet = WorldSync.Pack(world, AppPaths.ModelsFolder);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var w = new BinaryWriter(ms)) packet.Serialize(w);
            bytes = ms.ToArray();
        }
        Console.WriteLine($"Packet size: {bytes.Length} bytes ({world.Objects.Count} objects, {packet.MeshTexts.Count} mesh file(s)).");

        var received = new WorldSyncPacket();
        using (var r = new BinaryReader(new MemoryStream(bytes))) received.Deserialize(r);

        var (back, meshTexts) = WorldSync.Unpack(received);

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

        // PhysicsSync delta: round-trip the compact position batch (id + pos + velY per entry).
        {
            var ps = new PhysicsSyncPacket
            {
                Ids = new[] { 7, 42 },
                Positions = new[] { new Vector3(1f, 2f, 3f), new Vector3(-4f, 5.5f, 6f) },
                VelY = new[] { -9.8f, 0f },
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
                && Math.Abs(recv.Positions[1].Y - 5.5f) < 1e-5f && Math.Abs(recv.VelY[0] - (-9.8f)) < 1e-5f
                && recv.VelY[1] == 0f
                && Math.Abs(recv.Rotations[0].Y - 0.2f) < 1e-5f && Math.Abs(recv.Rotations[1].Z - (-0.6f)) < 1e-5f
                && Math.Abs(recv.AngVel[0].X - 1.5f) < 1e-5f && Math.Abs(recv.AngVel[1].Y - 3f) < 1e-5f;
            Console.WriteLine($"PhysicsSync round-trip: {pbytes.Length} bytes, n={recv.Ids.Length}, pos1.Y={recv.Positions[1].Y:F2}, vel0={recv.VelY[0]:F2}, rot0.Y={recv.Rotations[0].Y:F2}, angvel0.X={recv.AngVel[0].X:F2} -> {(psOk ? "ok" : "BAD")}");
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

        Console.WriteLine("WORLD SYNC TEST PASSED");
    }

    // Returns null if the two worlds match (within float epsilon), else a reason string.
    static string? CompareWorlds(WorldConfig a, WorldConfig b)
    {
        const float eps = 1e-4f;
        bool Eq(float x, float y) => Math.Abs(x - y) < eps;

        if (a.Name != b.Name) return $"name '{a.Name}' != '{b.Name}'";
        if (a.Graphics.Shadows != b.Graphics.Shadows || a.Graphics.Bvh != b.Graphics.Bvh ||
            a.Graphics.ExtraLight != b.Graphics.ExtraLight || a.Graphics.DisableCameraLight != b.Graphics.DisableCameraLight ||
            a.Graphics.Renderer != b.Graphics.Renderer)
            return "graphics flags differ";
        if (a.Platform.Enabled != b.Platform.Enabled || !Eq(a.Platform.Size, b.Platform.Size) ||
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
            if (!Eq(x.ColorFade, y.ColorFade)) return $"object[{i}].ColorFade {x.ColorFade} != {y.ColorFade}";
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

        // 7) ResolveAabbHorizontal (phase-1 object separation): an overlapping pair is pushed along the
        // least-penetration horizontal axis and the push clears the overlap; pairs apart on X/Z OR on Y
        // resolve to zero (Y apart = not touching, so the horizontal solver leaves them alone).
        {
            Vector3 aMin = new(-1f, -1f, -1f), aMax = new(1f, 1f, 1f);
            // b overlaps a, more on X (0.5) than on Z (0.2) -> push along Z by 0.2 (the smaller axis).
            Vector3 bMin = new(0.5f, -1f, 0.8f), bMax = new(2.5f, 1f, 2.8f);
            Vector3 push = PriviewNetworkScene.ResolveAabbHorizontal(aMin, aMax, bMin, bMax);
            bool axisOk = push.X == 0f && Math.Abs(push.Z - (-0.2f)) < eps;
            float aMaxZ2 = aMax.Z + push.Z;                    // a's +Z edge after the push
            bool cleared = aMaxZ2 <= bMin.Z + eps;             // now just touching, no longer penetrating
            // Apart on Y only (overlap XZ) -> not touching -> zero.
            Vector3 yMin = new(0.5f, 5f, 0.5f), yMax = new(1.5f, 7f, 1.5f);
            Vector3 pY = PriviewNetworkScene.ResolveAabbHorizontal(aMin, aMax, yMin, yMax);
            // Fully separated -> zero.
            Vector3 pFar = PriviewNetworkScene.ResolveAabbHorizontal(aMin, aMax, new Vector3(5f, -1f, 5f), new Vector3(7f, 1f, 7f));
            bool zeros = pY.X == 0f && pY.Z == 0f && pFar.X == 0f && pFar.Z == 0f;
            // RESTING on a wide floor: a small box dips 0.01 into a huge thin slab -> the vertical overlap is
            // the SMALLEST axis -> a stacking contact -> NO horizontal push (else it's ejected sideways off
            // the floor, the playground-pyramid runaway). A genuine side hit (Y not least) still pushes.
            Vector3 fMin = new(-30f, -0.5f, -30f), fMax = new(30f, 0f, 30f);
            Vector3 pRest = PriviewNetworkScene.ResolveAabbHorizontal(new Vector3(5f, -0.01f, 5f), new Vector3(7f, 2f, 7f), fMin, fMax);
            bool restNoPush = pRest.X == 0f && pRest.Z == 0f;
            Console.WriteLine($"  aabb-horiz: push=({push.X:F2},{push.Z:F2}) (want 0,-0.20), cleared={cleared}, y/far-zero={zeros}, rest-no-push={restNoPush} -> {(axisOk && cleared && zeros && restNoPush ? "ok" : "BAD")}");
            ok &= axisOk && cleared && zeros && restNoPush;
        }

        // 8) SatRect2D (OBB-OBB contact, XZ): two axis-aligned unit squares offset 0.8 on X give the
        // same axis-aligned MTV as the AABB path (normal +X, depth 0.2); a fully separated pair reports
        // no hit; a 45°-rotated square overlapping an axis-aligned one is detected with a unit normal.
        {
            Vector3 eAx = new(0.5f, 0f, 0f), eAz = new(0f, 0f, 0.5f);   // axis-aligned unit square (half 0.5)
            var (hit1, nx1, nz1, d1) = PriviewNetworkScene.SatRect2D(
                new Vector3(0f, 0f, 0f), eAx, eAz, new Vector3(0.8f, 0f, 0f), eAx, eAz);
            bool axisOk = hit1 && Math.Abs(nx1 - 1f) < eps && Math.Abs(nz1) < eps && Math.Abs(d1 - 0.2f) < eps;

            var (hit2, _, _, _) = PriviewNetworkScene.SatRect2D(
                new Vector3(0f, 0f, 0f), eAx, eAz, new Vector3(2f, 0f, 0f), eAx, eAz);   // far apart -> no hit

            const float h = 0.35355339f;                                 // 0.5·cos45
            Vector3 rBx = new(h, 0f, h), rBz = new(-h, 0f, h);           // square rotated 45°
            var (hit3, nx3, nz3, d3) = PriviewNetworkScene.SatRect2D(
                new Vector3(0f, 0f, 0f), eAx, eAz, new Vector3(0.7f, 0f, 0f), rBx, rBz);
            float nlen3 = MathF.Sqrt(nx3 * nx3 + nz3 * nz3);
            bool rotOk = hit3 && d3 > 0f && Math.Abs(nlen3 - 1f) < eps;

            // Full 3D SatBox3D: same axis-aligned MTV (normal +X, depth 0.2); a vertical offset separates
            // with a +Y normal (which the 2D XZ test could never see); a 45°-about-Z box still overlaps with
            // a unit normal; a far pair reports no hit.
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
            bool box3dOk = b3Axis && b3Vert && !b3fh && b3Rot;

            bool satOk = axisOk && !hit2 && rotOk && box3dOk;
            Console.WriteLine($"  sat-obb: axis2d(n={nx1:F2},{nz1:F2} d={d1:F2}), rot2d|n|={nlen3:F2}, 3d(axis={b3Axis} vert={b3Vert} far={!b3fh} rot={b3Rot}) -> {(satOk ? "ok" : "BAD")}");
            ok &= satOk;
        }

        Console.WriteLine(ok ? "COLLISION TEST PASSED" : "COLLISION TEST FAILED");
    }

    // Headless check of the pure gravity helpers (StepFallY + XZOverlap): a falling object converges
    // to rest on its support without sinking through; with no support it free-falls forever; and the
    // X/Z overlap test classifies AABB pairs correctly. Mirrors what StepPhysics integrates per frame.
    static void PhysicsSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== PHYSICS SELF-TEST ===");

        const float eps = 1e-2f;
        bool ok = true;

        // 1) Drop from bottom=10 onto supportTop=0; after ~300 steps it rests exactly on the support.
        {
            float bottom = 10f, vel = 0f, g = 9.8f, dt = 1f / 60f, support = 0f;
            for (int i = 0; i < 300; i++) (bottom, vel) = PriviewNetworkScene.StepFallY(bottom, vel, dt, g, support);
            bool t = Math.Abs(bottom - support) < eps && Math.Abs(vel) < eps && bottom >= support - eps;
            Console.WriteLine($"  rest: after 300 steps bottom={bottom:F4} (want ~{support}), vel={vel:F4} (want ~0) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 2) No support below (supportTop = -1000): keeps falling, velocity stays negative (free fall).
        {
            float bottom = 10f, vel = 0f, g = 9.8f, dt = 1f / 60f, support = -1000f;
            float prev = bottom;
            bool everRose = false, everNonNeg = false;
            for (int i = 0; i < 120; i++)
            {
                (bottom, vel) = PriviewNetworkScene.StepFallY(bottom, vel, dt, g, support);
                if (bottom >= prev) everRose = true;     // bottom must strictly decrease
                if (vel >= 0f) everNonNeg = true;        // velocity must stay negative once falling
                prev = bottom;
            }
            bool t = !everRose && !everNonNeg && bottom < 0f;   // fell well past the start, never rested
            Console.WriteLine($"  freefall: after 120 steps bottom={bottom:F3} (falling, <0), vel={vel:F3} (<0), monotonic-down={!everRose} -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 3) XZOverlap: an overlapping pair is true; a pair separated in X (or Z) is false (Y ignored).
        {
            Vector3 aMin = new(-1f, -5f, -1f), aMax = new(1f, 5f, 1f);
            bool overlap = PriviewNetworkScene.XZOverlap(aMin, aMax, new Vector3(0.5f, 100f, 0.5f), new Vector3(2f, 200f, 2f));   // X/Z overlap, Y far apart -> true
            bool apartX  = PriviewNetworkScene.XZOverlap(aMin, aMax, new Vector3(3f, -5f, 0f), new Vector3(5f, 5f, 1f));          // separated in X -> false
            bool apartZ  = PriviewNetworkScene.XZOverlap(aMin, aMax, new Vector3(0f, -5f, 3f), new Vector3(1f, 5f, 5f));          // separated in Z -> false
            bool t = overlap && !apartX && !apartZ;
            Console.WriteLine($"  xzoverlap: overlap={overlap} (want True), apartX={apartX} (want False), apartZ={apartZ} (want False) -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 3b) Restitution: a fall with restitution>0 rebounds (downward velocity flips positive on
        // impact) and then settles to rest as the bounces decay below BounceMin; restitution 0 never bounces.
        {
            float g = 9.8f, dt = 1f / 60f, support = 0f, rest = 0.6f;
            float bottom = 5f, vel = 0f; bool bounced = false;
            for (int i = 0; i < 700; i++)   // long enough to fall (~60 steps), bounce several times, then settle
            {
                float before = vel;
                (bottom, vel) = PriviewNetworkScene.StepFallY(bottom, vel, dt, g, support, rest);
                if (before < 0f && vel > 0f) bounced = true;     // impact reflected the velocity upward
            }
            bool settled = Math.Abs(bottom - support) < eps && Math.Abs(vel) < eps;
            // control: restitution 0 must NEVER produce an upward rebound.
            float b2 = 5f, v2 = 0f; bool bounced0 = false;
            for (int i = 0; i < 200; i++) { float bv = v2; (b2, v2) = PriviewNetworkScene.StepFallY(b2, v2, dt, g, support, 0f); if (bv < 0f && v2 > 0f) bounced0 = true; }
            bool t = bounced && settled && !bounced0;
            Console.WriteLine($"  restitution: bounced={bounced}, settled(bottom={bottom:F4},vel={vel:F4})={settled}, no-bounce@0={!bounced0} -> {(t ? "ok" : "BAD")}");
            ok &= t;
        }

        // 3c) StepFriction: a horizontal velocity decays monotonically toward 0 without flipping sign,
        // and Y is left untouched. 3d) NormalImpulse: equal-mass elastic swaps the normal components;
        // a wall reflects with restitution; a separating pair is unchanged.
        {
            float dt = 1f / 60f;
            Vector3 v = new(3f, -5f, -2f); float prevSpeed = MathF.Sqrt(v.X * v.X + v.Z * v.Z); bool mono = true, signOk = true;
            for (int i = 0; i < 600; i++)
            {
                v = PriviewNetworkScene.StepFriction(v, dt, 4f);
                float sp = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
                if (sp > prevSpeed + 1e-6f) mono = false;
                if (v.X < 0f || v.Z > 0f) signOk = false;        // started +X / -Z; must not overshoot past 0
                prevSpeed = sp;
            }
            bool yKept = Math.Abs(v.Y - (-5f)) < 1e-6f;
            bool frOk = mono && signOk && yKept && Math.Abs(v.X) < eps && Math.Abs(v.Z) < eps;
            Console.WriteLine($"  friction: settled vX={v.X:F4} vZ={v.Z:F4} (->0), monotonic={mono}, no-overshoot={signOk}, Y-kept={yKept} -> {(frOk ? "ok" : "BAD")}");
            ok &= frOk;

            // elastic equal-mass: A=+4 toward B=0 -> they swap (A=0, B=+4); momentum conserved.
            var (a1, b1) = PriviewNetworkScene.NormalImpulse(4f, 0f, 1f, 1f, 1f);
            // inelastic (e=0): both end at the common velocity (+2).
            var (a0, b0) = PriviewNetworkScene.NormalImpulse(4f, 0f, 1f, 1f, 0f);
            // wall (B mass infinite, e=0.5): A reflects to -2, wall unchanged.
            var (aw, bw) = PriviewNetworkScene.NormalImpulse(4f, 0f, 1f, float.PositiveInfinity, 0.5f);
            // separating pair (A moving away, pA<pB): unchanged.
            var (as_, bs) = PriviewNetworkScene.NormalImpulse(-1f, 2f, 1f, 1f, 1f);
            // mass-asymmetric elastic: light A (m=1, +4) hits heavy B (m=3, 0) -> A=-2, B=+2; momentum 1*4 = 1*-2 + 3*2.
            var (am, bm) = PriviewNetworkScene.NormalImpulse(4f, 0f, 1f, 3f, 1f);
            float momBefore = 1f * 4f, momAfter = 1f * am + 3f * bm;
            bool impOk = Math.Abs(a1) < eps && Math.Abs(b1 - 4f) < eps
                       && Math.Abs(a0 - 2f) < eps && Math.Abs(b0 - 2f) < eps
                       && Math.Abs(aw - (-2f)) < eps && bw == 0f
                       && as_ == -1f && bs == 2f
                       && Math.Abs(am - (-2f)) < eps && Math.Abs(bm - 2f) < eps && Math.Abs(momAfter - momBefore) < eps;
            Console.WriteLine($"  impulse: elastic=({a1:F2},{b1:F2}) inelastic=({a0:F2},{b0:F2}) wall=({aw:F2}) sep=({as_:F2},{bs:F2}) mass=({am:F2},{bm:F2}) mom={momAfter:F2} -> {(impOk ? "ok" : "BAD")}");
            ok &= impOk;
        }

        // 3e) BoxInertia = diagonal m(·)/12 tensor; AngularImpulse = (r × J) ÷ I (component-wise): a
        // centered hit gives no spin; a horizontal impulse at a horizontal lever yaws (about Y); the same
        // impulse at a VERTICAL lever pitches/rolls (about a horizontal axis); zero inertia => no spin.
        {
            Vector3 I = PriviewNetworkScene.BoxInertia(2f, 2f, 3f, 4f);       // (m(9+16),m(4+16),m(4+9))/12 = (4.166,3.333,2.166)
            bool inertiaOk = Math.Abs(I.X - 2f * 25f / 12f) < 1e-3f && Math.Abs(I.Y - 2f * 20f / 12f) < 1e-3f && Math.Abs(I.Z - 2f * 13f / 12f) < 1e-3f;
            Vector3 centered = PriviewNetworkScene.AngularImpulse(Vector3.Zero, new Vector3(0f, 0f, 6f), new Vector3(3f, 3f, 3f));
            Vector3 yaw = PriviewNetworkScene.AngularImpulse(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 6f), new Vector3(3f, 3f, 3f));   // (r×J)=(0,-6,0)/3 -> Y=-2
            Vector3 pitch = PriviewNetworkScene.AngularImpulse(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 6f), new Vector3(3f, 3f, 3f)); // (r×J)=(6,0,0)/3 -> X=+2
            Vector3 zeroI = PriviewNetworkScene.AngularImpulse(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 6f), Vector3.Zero);
            bool angOk = inertiaOk
                && centered.X == 0f && centered.Y == 0f && centered.Z == 0f
                && Math.Abs(yaw.Y - (-2f)) < eps && Math.Abs(yaw.X) < eps && Math.Abs(yaw.Z) < eps
                && Math.Abs(pitch.X - 2f) < eps && Math.Abs(pitch.Y) < eps && Math.Abs(pitch.Z) < eps
                && zeroI.X == 0f && zeroI.Y == 0f && zeroI.Z == 0f;
            Console.WriteLine($"  rotation: I=({I.X:F2},{I.Y:F2},{I.Z:F2}), yaw.Y={yaw.Y:F2}(-2), pitch.X={pitch.X:F2}(+2), centered/zeroI=0 -> {(angOk ? "ok" : "BAD")}");
            ok &= angOk;
        }

        // 3e2) Off-diagonal inertia tensor (AngularImpulseT): the SAME torque spins a body fast when it
        // hits its LOW-inertia axis and slow when the body is rotated to present a HIGH-inertia axis.
        // Anisotropic I=(1,5,5): a torque about world +X gives ω=6 when body-X is world-aligned, but only
        // ω=1.2 when the body is turned 90° about Z (so its high-inertia Y axis now faces +X). World-aligned
        // axes must reproduce AngularImpulse exactly (the tensor reduces to the diagonal case).
        {
            Vector3 Ia = new(1f, 5f, 5f);
            Vector3 tA = PriviewNetworkScene.AngularImpulseT(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 6f),
                new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), Ia);          // body axes = world
            Vector3 tR = PriviewNetworkScene.AngularImpulseT(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 6f),
                new Vector3(0f, 1f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), Ia);          // body turned 90° about Z
            bool tensorOk = Math.Abs(tA.X - 6f) < eps && Math.Abs(tA.Y) < eps && Math.Abs(tA.Z) < eps
                         && Math.Abs(tR.X - 1.2f) < 1e-3f && Math.Abs(tR.Y) < eps && Math.Abs(tR.Z) < eps;
            Console.WriteLine($"  inertia-tensor: aligned ωx={tA.X:F2}(6), rotated ωx={tR.X:F2}(1.2) -> {(tensorOk ? "ok" : "BAD")}");
            ok &= tensorOk;
        }

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

        // 3h) ReflectVelocity (sphere bounce off a surface normal): a flat (up) normal gives the usual
        // vertical bounce (horizontal kept); a 45° slope deflects a straight-down drop SIDEWAYS; a velocity
        // moving away from the surface is unchanged.
        {
            Vector3 flat = PriviewNetworkScene.ReflectVelocity(new Vector3(1f, -5f, 0f), new Vector3(0f, 1f, 0f), 0.5f);
            bool flatOk = Math.Abs(flat.X - 1f) < eps && Math.Abs(flat.Y - 2.5f) < eps && Math.Abs(flat.Z) < eps;   // velY -> -e·velY=+2.5, X kept
            const float s = 0.70710678f;
            Vector3 slope = PriviewNetworkScene.ReflectVelocity(new Vector3(0f, -5f, 0f), new Vector3(s, s, 0f), 0.5f);
            bool slopeOk = slope.X > 0.5f && slope.Y > -5f;                                                          // gains sideways +X, vertical eased
            Vector3 away = PriviewNetworkScene.ReflectVelocity(new Vector3(0f, 5f, 0f), new Vector3(0f, 1f, 0f), 0.5f);
            bool awayOk = away.X == 0f && away.Y == 5f && away.Z == 0f;                                              // moving away -> unchanged
            bool refOk = flatOk && slopeOk && awayOk;
            Console.WriteLine($"  reflect: flat=({flat.X:F2},{flat.Y:F2}), slope.X={slope.X:F2}(>0 sideways), away-unchanged={awayOk} -> {(refOk ? "ok" : "BAD")}");
            ok &= refOk;
        }

        // 3i) SphereContactResponse (the stability fix): a REAL impact bounces; a GENTLE contact only
        // slides (its speed must NOT grow — this is what stops the slope runaway); a separating velocity
        // is unchanged. The no-energy-gain property is checked even on a slope with restitution=1.
        {
            const float bm = 0.6f, s = 0.70710678f;
            Vector3 up = new(0f, 1f, 0f), slopeN = new(s, s, 0f);
            // real impact (fast into a flat floor) -> bounces up.
            var impact = PriviewNetworkScene.SphereContactResponse(new Vector3(0f, -5f, 0f), up, 0.5f, bm);
            bool impactOk = impact.Y > 0f;
            // gentle contact (slow into floor) -> vertical removed, no bounce.
            var graze = PriviewNetworkScene.SphereContactResponse(new Vector3(1f, -0.2f, 0f), up, 1f, bm);
            bool grazeOk = Math.Abs(graze.Y) < eps && Math.Abs(graze.X - 1f) < eps;
            // CRITICAL: a gentle contact on a slope with restitution=1 must NOT increase speed (no runaway).
            Vector3 vIn = new(0.3f, -0.3f, 0f);
            var slopeGraze = PriviewNetworkScene.SphereContactResponse(vIn, slopeN, 1f, bm);
            float inMag = MathF.Sqrt(vIn.X * vIn.X + vIn.Y * vIn.Y + vIn.Z * vIn.Z);
            float outMag = MathF.Sqrt(slopeGraze.X * slopeGraze.X + slopeGraze.Y * slopeGraze.Y + slopeGraze.Z * slopeGraze.Z);
            bool noGain = outMag <= inMag + 1e-4f;
            // separating velocity -> unchanged.
            var sep = PriviewNetworkScene.SphereContactResponse(new Vector3(0f, 5f, 0f), up, 1f, bm);
            bool sepOk = sep.Y == 5f;
            bool scrOk = impactOk && grazeOk && noGain && sepOk;
            Console.WriteLine($"  sphere-contact: impact.Y={impact.Y:F2}(>0), graze=({graze.X:F2},{graze.Y:F2}), slope no-gain={outMag:F3}<={inMag:F3}, sep={sep.Y:F1} -> {(scrOk ? "ok" : "BAD")}");
            ok &= scrOk;
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
                (cur, tgt) = PriviewNetworkScene.StepInterpolate(cur, tgt, 0f, dt, rate);
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
            for (int i = 0; i < steps; i++) (c2, t2) = PriviewNetworkScene.StepInterpolate(c2, t2, velY, dt, rate);
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

        // 5) STABILITY SIM (the runaway regression): drop a sphere onto a STATIC pyramid (gravity on,
        // restitution 0.5) and step the real authority physics for ~10 s. The sphere must come to rest on
        // the pyramid/floor and STAY at sane coordinates — never fly off to hundreds of units or sink away.
        {
            var simWorld = new WorldConfig
            {
                Name = "stability-sim",
                Graphics = new GraphicsConfig { Shadows = false },
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 40f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0.5f },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "pyramid", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 1f, Collides = true, Gravity = false },
                    new WorldObject { Id = 1, Type = "sphere", Color = "Red", Radius = 1f,
                        Position = new Vec3Config { X = 0.2f, Y = 8f, Z = 0.1f }, Collides = true, Gravity = true },
                },
            };
            var sim = new PriviewNetworkScene(new DisplayManagerAsync(), simWorld, isServer: false, "127.0.0.1", 0, online: false);
            sim.Start();
            float dt = 1f / 60f;
            for (int i = 0; i < 600; i++) sim.StepPhysicsForTest(dt);

            var ballEntry = sim.EditableEntries.FirstOrDefault(en => en.Instance is Sphere);
            var p = ballEntry?.Instance.Position ?? new Vector3(9999f, 9999f, 9999f);
            // Stayed near the drop column horizontally, settled above the floor, never escaped or sank.
            bool boundedXZ = MathF.Abs(p.X) < 15f && MathF.Abs(p.Z) < 15f;   // a real elastic bounce off a slope legitimately travels a few units; a runaway goes to hundreds
            bool boundedY = p.Y > -5f && p.Y < 20f;
            // ROLLED downhill: tangential gravity slid the ball off the apex (proves sideways motion works)
            // while staying bounded (the runaway it replaces would have flung it to hundreds of units).
            float rollDist = MathF.Sqrt((p.X - 0.2f) * (p.X - 0.2f) + (p.Z - 0.1f) * (p.Z - 0.1f));
            bool rolled = rollDist > 0.5f;
            bool simOk = ballEntry != null && boundedXZ && boundedY && rolled;
            Console.WriteLine($"  stability-sim: ball after 600 steps at ({p.X:F2},{p.Y:F2},{p.Z:F2}) -> rolled={rolled} (dist {rollDist:F2}), boundedXZ={boundedXZ}, boundedY={boundedY} -> {(simOk ? "ok" : "BAD")}");
            ok &= simOk;
        }

        // 6) MESH SLIDE SIM: a falling CUBE must rest on the pyramid's REAL face (ray-traced footprint,
        // StepMeshGravity), slide downhill via tangential gravity, and settle — bounded, never floating on
        // its bounding box at the apex or running away. Mirrors stability-sim but for a mesh.
        {
            var simWorld = new WorldConfig
            {
                Name = "mesh-slide-sim",
                Graphics = new GraphicsConfig { Shadows = false },
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 40f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0.3f },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "pyramid", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 2f, Collides = true, Gravity = false },
                    new WorldObject { Id = 1, Type = "cube", Color = "Red",
                        Position = new Vec3Config { X = 0.6f, Y = 8f, Z = 0.2f }, Scale = 1f, Collides = true, Gravity = true },
                },
            };
            var sim = new PriviewNetworkScene(new DisplayManagerAsync(), simWorld, isServer: false, "127.0.0.1", 0, online: false);
            sim.Start();
            float dt = 1f / 60f;
            for (int i = 0; i < 600; i++) sim.StepPhysicsForTest(dt);

            var cubeEntry = sim.EditableEntries.FirstOrDefault(en => en.Descriptor.Id == 1);
            var p = cubeEntry?.Instance.Position ?? new Vector3(9999f, 9999f, 9999f);
            bool boundedXZ = MathF.Abs(p.X) < 12f && MathF.Abs(p.Z) < 12f;
            bool boundedY = p.Y > -5f && p.Y < 20f;
            float slid = MathF.Sqrt((p.X - 0.6f) * (p.X - 0.6f) + (p.Z - 0.2f) * (p.Z - 0.2f));   // moved off the drop column
            bool moved = slid > 0.3f;
            bool meshOk = cubeEntry != null && boundedXZ && boundedY && moved;
            Console.WriteLine($"  mesh-slide-sim: cube after 600 steps at ({p.X:F2},{p.Y:F2},{p.Z:F2}) -> slid={moved} (dist {slid:F2}), boundedXZ={boundedXZ}, boundedY={boundedY} -> {(meshOk ? "ok" : "BAD")}");
            ok &= meshOk;
        }

        // 7) MYWORLD REPRO (the reported runaway): a falling sphere (restitution 0) dropped ONTO a DYNAMIC,
        // 45°/45°-rotated cube over a big floor — exactly the user's myworld, for BOTH collider shapes. The
        // ball must NOT be flung to huge coordinates. Tracks the max horizontal radius reached by ANY object.
        foreach (string cubeShape in new[] { "aabb", "obb" })
        {
            var w = new WorldConfig
            {
                Name = "myworld-repro",
                Graphics = new GraphicsConfig { Shadows = false },
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 200f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 10f, CollisionEnabled = true, Restitution = 0.5f },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 2, Type = "cube", Color = "#7777FF",
                        Position = new Vec3Config { X = -0.048719168f, Y = 1.4142137f, Z = -3.2081192f },
                        Rotation = new Vec3Config { X = 0.7853982f, Y = 0.7853982f, Z = 0f },
                        Scale = 1f, Collides = true, Gravity = true, Collider = cubeShape, Restitution = -1f },
                    new WorldObject { Id = 4, Type = "sphere", Color = "White", Radius = 1f,
                        Position = new Vec3Config { X = 0.5f, Y = 10f, Z = -3.2081192f },   // OFF-CENTRE onto the tilted cube so it rolls off (not the unstable dead-centre balance)
                        Scale = 1f, Collides = true, Gravity = true, Restitution = 0f },
                },
            };
            // Sweep frame times (an ASCII raytracer steps by the REAL frame time). For EACH: the ball must
            // stay bounded (no fling) AND move SMOOTHLY — the per-frame step at 60 FPS must be small (a
            // teleport off the bounding box would be a big jump). Now the ball hits the cube's REAL faces,
            // so it rolls off instead of being ejected from the box.
            bool boundedAll = true;
            foreach (float dt in new[] { 1f / 60f, 1f / 15f, 1f / 8f, 1f / 4f, 1f / 2f, 1f })
            {
                var sim = new PriviewNetworkScene(new DisplayManagerAsync(), w, isServer: false, "127.0.0.1", 0, online: false);
                sim.Start();
                Vector3 prev = sim.EditableEntries.FirstOrDefault(en => en.Instance is Sphere)?.Instance.Position ?? Vector3.Zero;
                float maxR = 0f, maxStep = 0f;
                int steps = (int)(10f / dt);                    // ~10 s of sim regardless of dt
                for (int i = 0; i < steps; i++)
                {
                    sim.StepPhysicsForTest(dt);
                    var cur = sim.EditableEntries.FirstOrDefault(en => en.Instance is Sphere)?.Instance.Position ?? prev;
                    float step = (cur - prev).Length(); if (step > maxStep) maxStep = step;
                    prev = cur;
                    foreach (var en in sim.EditableEntries)
                    {
                        var pp = en.Instance.Position;
                        float rr = MathF.Sqrt(pp.X * pp.X + pp.Z * pp.Z);
                        if (rr > maxR) maxR = rr;
                    }
                }
                var ball = sim.EditableEntries.FirstOrDefault(en => en.Instance is Sphere);
                var bp = ball?.Instance.Position ?? new Vector3(9999f, 9999f, 9999f);
                bool bounded = MathF.Abs(bp.X) < 20f && MathF.Abs(bp.Z) < 20f && bp.Y > -5f && bp.Y < 30f && maxR < 50f;
                bool smooth = dt > 1f / 60f + 1e-4f || maxStep < 0.5f;   // no teleport: assert the smooth-step only at 60 FPS (substep makes a big-dt call coarser)
                bool good = bounded && smooth;
                Console.WriteLine($"  myworld-repro [{cubeShape}] dt=1/{1f / dt:F0}: ball end ({bp.X:F2},{bp.Y:F2},{bp.Z:F2}), maxR={maxR:F1}, maxStep={maxStep:F2} -> {(good ? "ok" : "BAD")}");
                boundedAll &= good;
            }
            ok &= boundedAll;
        }

        Console.WriteLine(ok ? "PHYSICS TEST PASSED" : "PHYSICS TEST FAILED");
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
            (int nonBlack, int edge, int interior, float worstB, int worstC) Compare(float bTol, int cTol)
            {
                SceneSnapshot snap = scene.BuildSnapshot();
                rt.Render(snap, W, H, aspect, brightness, color);

                int nb = 0, edge = 0, interior = 0; float wb = 0f; int wc = 0;
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++)
                    {
                        float uvx = ((float)i / (W - 1) * 2f - 1f) * aspect;
                        float uvy = -((float)j / (H - 1) * 2f - 1f);
                        var cpu = scene.GetPixelData(new Vector2(uvx, uvy));
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
            var a = Compare(0.004f, 1);   // tiny: absorbs ULP/rounding noise, far below any feature-level diff
            Console.WriteLine($"  [no shadows] nonBlack={a.nonBlack}, edge={a.edge}, interior={a.interior} (worstΔb={a.worstB:F4}, worstΔc={a.worstC})");

            // Pass B — shadows ON: shadow rays are float-sensitive at boundaries (and the area light's
            // 4 occlusion samples form a soft penumbra), so a band of pixels differs by a SMALL amount.
            // We tolerate sub-penumbra noise per pixel and only require the disagreement to stay bounded
            // in magnitude and not become pervasive (which a systematic shadow bug would).
            scene.EnableShadows = true;
            var b = Compare(0.1f, 28);
            Console.WriteLine($"  [shadows]    nonBlack={b.nonBlack}, edge={b.edge}, interior={b.interior} (worstΔb={b.worstB:F4}, worstΔc={b.worstC})");

            bool ok = a.nonBlack > 50
                      && a.interior == 0 && a.edge == 0                                          // shading exact (Δ=0)
                      && (float)b.interior / total < 0.06f && (float)b.edge / total < 0.06f;      // shadow boundary thin
            Console.WriteLine(ok ? "GPU TEST PASSED" : "GPU TEST FAILED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("GPU TEST FAILED");
        }
    }
}