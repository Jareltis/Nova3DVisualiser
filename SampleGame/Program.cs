using System.Net;
using NStack;
using Nova3DVisualiser;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.Scenes;
using Terminal.Gui;

namespace SampleGame;

class Program
{
    // Outcome of a single setup dialog.
    enum Step { Mode, Role, Network, Graphics }
    enum DlgResult { Ok, Back, Quit }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "bvhtest") { BvhSelfTest(); return; }

        Logger.Init(AppPaths.LogsFolder);
        Logger.Info("Application started");

        // ---- Setup choices (defaults match the previous flow) ----
        bool online = false;
        bool isServer = false;
        string ip = "127.0.0.1";
        int port = 7777;
        bool enableShadows = true;
        bool useBvh = true;
        bool addExtraLight = false;
        bool disableOwnLight = false;
        bool quit = false;

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

            var step = Step.Mode;
            bool done = false;
            while (!done)
            {
                switch (step)
                {
                    case Step.Mode:
                        if (ShowModeDialog(ref online) == DlgResult.Quit) { quit = true; done = true; }
                        else step = online ? Step.Role : Step.Graphics;
                        break;

                    case Step.Role:
                        step = ShowRoleDialog(ref isServer) == DlgResult.Back ? Step.Mode : Step.Network;
                        break;

                    case Step.Network:
                        step = ShowNetworkDialog(isServer, ref ip, ref port) == DlgResult.Back ? Step.Role : Step.Graphics;
                        break;

                    case Step.Graphics:
                        if (ShowGraphicsDialog(ref enableShadows, ref useBvh, ref addExtraLight, ref disableOwnLight) == DlgResult.Back)
                            step = online ? Step.Network : Step.Mode;
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

        if (quit)
        {
            Logger.Info("Setup cancelled by user");
            return;
        }

        Object3d.UseBvh = useBvh;
        Logger.Info($"Mode={(online ? (isServer ? "Server" : "Client") : "Local")}; Network {ip}:{port}; extraLight={addExtraLight}; ownLight={!disableOwnLight}; shadows={enableShadows}; bvh={useBvh}");

        Console.WriteLine(online
            ? $"Starting {(isServer ? "Server" : "Client")} on {ip}:{port}..."
            : "Starting local (offline) session...");
        Thread.Sleep(500);
        Console.Clear();

        var scene = new PriviewNetworkScene(new DisplayManagerAsync(), isServer, ip, port, addExtraLight, disableOwnLight, online);
        scene.EnableShadows = enableShadows;

        Logger.Info("Scene constructed, entering render loop");
        try
        {
            new Frame(scene, new ConsoleScreenAsync()).MainLoop();
        }
        catch (Exception ex) { Logger.Error("Unhandled exception in main loop", ex); throw; }
        //new Frame(new PreviewScene(new DisplayManagerAsync()), new ConsoleScreenAsync()).MainLoop();
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
        Application.Run(dialog);

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
        Application.Run(dialog);

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

        Application.Run(dialog);

        if (result == DlgResult.Ok)
        {
            ip = ipLocal;
            port = portLocal;
        }
        return result;
    }

    // ---- Dialog 4: graphics options as checkboxes. ----
    static DlgResult ShowGraphicsDialog(ref bool shadows, ref bool bvh, ref bool extraLight, ref bool disableOwnLight)
    {
        var result = DlgResult.Back;
        var cbShadows = new CheckBox("Shadows", shadows) { X = 1, Y = 1 };
        var cbBvh = new CheckBox("BVH acceleration", bvh) { X = 1, Y = 2 };
        var cbExtra = new CheckBox("Extra fixed light", extraLight) { X = 1, Y = 3 };
        var cbDisableOwn = new CheckBox("Disable camera light", disableOwnLight) { X = 1, Y = 4 };

        var start = new Button("Start", is_default: true);
        var back = new Button("Back");
        start.Clicked += () => { result = DlgResult.Ok; Application.RequestStop(); };
        back.Clicked += () => { result = DlgResult.Back; Application.RequestStop(); };

        var dialog = new Dialog("Graphics options", 50, 11, start, back);
        dialog.Add(new Label("Toggle with Space; Tab to the buttons.") { X = 1, Y = 0 },
            cbShadows, cbBvh, cbExtra, cbDisableOwn);
        Application.Run(dialog);

        if (result == DlgResult.Ok)
        {
            shadows = cbShadows.Checked;
            bvh = cbBvh.Checked;
            extraLight = cbExtra.Checked;
            disableOwnLight = cbDisableOwn.Checked;
        }
        return result;
    }

    static void BvhSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== BVH SELF-TEST ===");

        var models = ModelLoader.LoadFolder(AppPaths.ModelsFolder);
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
}