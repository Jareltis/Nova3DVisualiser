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
            new Label("Gravity strength:") { X = 1, Y = 15 }, gravityField);
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
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 12.5f, CollisionEnabled = false },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 10, Type = "mesh", Mesh = "monkey",
                    Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
                    Rotation = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                    Scale = 1.5f, Color = "Red", Anchor = "Center", RotateSpeed = 0.5f, Radius = 1f },
                new WorldObject { Id = 11, Type = "cube",
                    Position = new Vec3Config { X = -1f, Y = 0f, Z = 2f },
                    Scale = 2f, Color = "Green", Collides = false, Gravity = true },
                new WorldObject { Id = 12, Type = "sphere",
                    Position = new Vec3Config { X = 4f, Y = 1f, Z = -2f },
                    Radius = 2.5f, Color = "Blue" },
                new WorldObject { Id = 13, Type = "light",
                    Position = new Vec3Config { X = 5f, Y = 4f, Z = 1f },
                    Power = 800f, Color = "Magenta", LightKind = "spot",
                    Direction = new Vec3Config { X = 0.5f, Y = -1f, Z = 0.25f },
                    ConeAngle = 22f, LightSize = 1.5f, LightSpin = 0.75f,
                    BeamCount = 3, ConeShape = "triangle", ColorInfluence = 0.85f },
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
                Rgb = new Vector3(0.7f, 0.8f, 1f),
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