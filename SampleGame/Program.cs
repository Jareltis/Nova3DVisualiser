using Nova3DVisualiser;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.Scenes;

namespace SampleGame;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "bvhtest") { BvhSelfTest(); return; }

        Logger.Init(AppPaths.LogsFolder);
        Logger.Info("Application started");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=== 3D ENGINE ONLINE SETUP ===");
        Console.ResetColor();
        
        Console.Write("Select Mode: [S]erver or [C]lient: ");
        var key = Console.ReadKey(true).Key;
        Console.WriteLine(key);
        
        bool isServer = (key == ConsoleKey.S);
        Logger.Info($"Mode: {(isServer ? "Server" : "Client")}");
        string ip = "127.0.0.1";
        int port = 7777;
        
        if (isServer)
        {
            string localIP = NetworkUtils.GetLocalIPAddress();
            Console.WriteLine($"\nYour Local IP: {localIP}");
            Console.WriteLine("Give this IP to your friend!\n");
        
            Console.Write("Enter Port to listen (default 7777): ");
            string? portInput = Console.ReadLine();
            if (!int.TryParse(portInput, out port)) port = 7777;
        }
        else
        {
            Console.Write("\nEnter Server IP (default 127.0.0.1): ");
            string? ipInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(ipInput)) ip = ipInput;
        
            Console.Write("Enter Server Port (default 7777): ");
            string? portInput = Console.ReadLine();
            if (!int.TryParse(portInput, out port)) port = 7777;
        }
        
        bool addExtraLight  = AskYesNo("Add an extra fixed light to the world?");
        bool disableOwnLight = AskYesNo("Disable your own (camera) light?");
        bool enableShadows = AskYesNo("Enable shadows?", defaultYes: true);
        bool useBvh = AskYesNo("Use BVH acceleration?", defaultYes: true);
        Object3d.UseBvh = useBvh;
        Logger.Info($"Network {ip}:{port}; extraLight={addExtraLight}; ownLight={!disableOwnLight}; shadows={enableShadows}; bvh={useBvh}");

        Console.WriteLine($"\nStarting {(isServer ? "Server" : "Client")} on {ip}:{port}...");
        Thread.Sleep(500);
        Console.Clear();

        var scene = new PriviewNetworkScene(new DisplayManagerAsync(), isServer, ip, port, addExtraLight, disableOwnLight);
        scene.EnableShadows = enableShadows;

        Logger.Info("Scene constructed, entering render loop");
        try
        {
            new Frame(scene, new ConsoleScreenAsync()).MainLoop();
        }
        catch (Exception ex) { Logger.Error("Unhandled exception in main loop", ex); throw; }
        //new Frame(new PreviewScene(new DisplayManagerAsync()), new ConsoleScreenAsync()).MainLoop();
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

    static bool AskYesNo(string prompt, bool defaultYes = false)
    {
        Console.Write(prompt + (defaultYes ? " [Y/n]: " : " [y/N]: "));
        string? input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(input)) return defaultYes;
        return input.StartsWith("y");
    }
}