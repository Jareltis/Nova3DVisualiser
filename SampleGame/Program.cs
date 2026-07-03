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

partial class Program
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

}