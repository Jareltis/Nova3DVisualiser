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

namespace SampleGame;

partial class Program
{

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
        if (args.Length > 0 && args[0] == "splashtest") { SplashSelfTest(); return; }
        if (args.Length > 0 && args[0] == "uitest") { WizardUi.UiSelfTest.Run(); return; }        // Variant B (engine-renderer wizard) toolkit tests
        if (args.Length > 0 && args[0] == "uidemo") { WizardUi.UiDemo.Run(); return; }            // Variant B stage-1 interactive demo (font-zoom check)

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

    // The full interactive app: engine-renderer setup wizard -> scene build -> render loop.
    static void RunApp()
    {
        Logger.Init(AppPaths.LogsFolder);
        Logger.Info("Application started");

        // Styled title splash (~2.5s, any key skips). Drawn with the plain Console before the wizard. The
        // self-test arg branches in Main return before RunApp, so no `*test` invocation ever renders it.
        ShowSplash();

        // ---- Setup choices ----
        bool online = false;
        bool isServer = false;
        string ip = "127.0.0.1";
        int port = 7777;
        WorldConfig? chosenWorld = null;
        bool quit = false;

        // There is always at least the default world to load.
        WorldManager.EnsureDefault();

        // ---- Setup wizard ----
        // The whole flow (Mode → Role → World → Create/Load → Network, with Back) runs on our own console UI
        // toolkit (SampleGame/WizardUi) — keyboard + mouse, and it reflows on a window resize / font zoom
        // because it polls the console size every frame. NewWizard.RunFlow returns the chosen session/world;
        // the launch code below consumes it unchanged.
        var wiz = WizardUi.NewWizard.RunFlow();
        online = wiz.Online;
        isServer = wiz.IsServer;
        ip = wiz.Ip;
        port = wiz.Port;
        chosenWorld = wiz.World;
        quit = wiz.Quit;

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

    // ================= Startup splash screen =================
    // A styled block-letter "NOVA 3D / VISUALISER" title, centered for the current console size,
    // shown for ~2.5s at startup and skippable with any key. Kept out of the self-tests (they return
    // in Main before RunApp). The pure helpers (BuildSplashBlock / SplashUsesArt / SplashLeft) are
    // exercised by `splashtest`; the render/timing below is visual-only.

    // Draws the centered splash, waits (skippable), then clears the screen for the wizard. Robust
    // to odd/narrow terminals (degrades to a one-line title) and never throws out to the caller.
    static void ShowSplash()
    {
        try
        {
            int w = SafeConsole(() => Console.WindowWidth, 80);
            int h = SafeConsole(() => Console.WindowHeight, 25);

            try { Console.CursorVisible = false; } catch { }
            DrawSplash(w, h);
            WaitForSplashDismiss(2500, w, h);
        }
        catch { /* the splash must never break startup on an odd terminal */ }
        finally
        {
            // Leave a clean screen for the setup wizard.
            try { Console.ResetColor(); Console.Clear(); Console.CursorVisible = true; } catch { }
        }
    }

    // Clears the screen and draws the centered splash for the CURRENT console size (w,h): the block art
    // (or a one-line fallback when the terminal is too small), a tagline, and the version. Re-callable so a
    // resize re-centres it. Never throws (each line is guarded).
    static void DrawSplash(int w, int h)
    {
        var block = BuildSplashBlock();
        int blockW = block.Length > 0 ? block[0].Length : 0;

        var content = new List<(string text, ConsoleColor color)>();
        if (SplashUsesArt(w, h, blockW))
            foreach (var line in block) content.Add((line, ConsoleColor.Cyan));
        else
            content.Add(("Nova 3D Visualiser", ConsoleColor.Cyan));

        content.Add(("", ConsoleColor.Gray));
        content.Add(("A S C I I   3 D   E N G I N E", ConsoleColor.Gray));
        string ver = SplashVersion();
        if (ver.Length > 0) content.Add((ver, ConsoleColor.DarkGray));

        try { Console.Clear(); } catch { }

        int top = Math.Max(0, (h - content.Count) / 2);
        for (int i = 0; i < content.Count && top + i < h; i++)
        {
            var (text, color) = content[i];
            if (text.Length == 0) continue;
            int left = SplashLeft(w, text.Length);
            int room = Math.Max(0, w - left);
            string shown = text.Length > room ? text.Substring(0, room) : text;
            try
            {
                Console.SetCursorPosition(left, top + i);
                Console.ForegroundColor = color;
                Console.Write(shown);
            }
            catch { /* skip a line that won't fit the current buffer */ }
        }
        Console.ResetColor();
    }

    // Waits up to `ms`, returning early on any keypress (so repeat launches aren't slowed). During the wait
    // it also polls for a CONSOLE SIZE CHANGE (window stretch / Ctrl+scroll font zoom) and RE-DRAWS the
    // splash centred for the new size — so it never "flies to the corner". `w`/`h` are the last-drawn size.
    // When input is redirected (no interactive console) it just sleeps out the duration.
    static void WaitForSplashDismiss(int ms, int w, int h)
    {
        if (Console.IsInputRedirected) { Thread.Sleep(ms); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try { if (Console.KeyAvailable) { Console.ReadKey(true); return; } }
            catch { Thread.Sleep(ms); return; }
            int nw = SafeConsole(() => Console.WindowWidth, w);
            int nh = SafeConsole(() => Console.WindowHeight, h);
            if (nw != w || nh != h) { w = nw; h = nh; DrawSplash(w, h); }   // re-centre for the new size
            Thread.Sleep(30);
        }
    }

    // The app version line for the splash (blank if it can't be resolved).
    static string SplashVersion()
    {
        try
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";
        }
        catch { return ""; }
    }

    // Reads a console property that can throw on odd terminals, falling back to a sane default.
    static T SafeConsole<T>(Func<T> get, T fallback)
    {
        try { return get(); } catch { return fallback; }
    }

    // ---- Pure splash helpers (testable via `splashtest`) ----

    // 5x5 block-letter font for the splash title. Unknown chars render as a blank glyph so the
    // banner text can be tweaked later without crashing.
    static string SplashGlyphRow(char c, int row)
    {
        string[] g = char.ToUpperInvariant(c) switch
        {
            'N' => new[] { "#   #", "##  #", "# # #", "#  ##", "#   #" },
            'O' => new[] { " ### ", "#   #", "#   #", "#   #", " ### " },
            'V' => new[] { "#   #", "#   #", "#   #", " # # ", "  #  " },
            'A' => new[] { " ### ", "#   #", "#####", "#   #", "#   #" },
            '3' => new[] { "#### ", "    #", " ### ", "    #", "#### " },
            'D' => new[] { "###  ", "#  # ", "#   #", "#  # ", "###  " },
            'I' => new[] { "#####", "  #  ", "  #  ", "  #  ", "#####" },
            'S' => new[] { " ####", "#    ", " ### ", "    #", "#### " },
            'U' => new[] { "#   #", "#   #", "#   #", "#   #", " ### " },
            'L' => new[] { "#    ", "#    ", "#    ", "#    ", "#####" },
            'E' => new[] { "#####", "#    ", "#### ", "#    ", "#####" },
            'R' => new[] { "#### ", "#   #", "#### ", "#  # ", "#   #" },
            _   => new[] { "     ", "     ", "     ", "     ", "     " },
        };
        return g[row];
    }

    // Builds one line of block-letter text as 5 equal-width rows (glyphs joined by a 1-col gap).
    static string[] BuildSplashLine(string text)
    {
        const int rows = 5;
        var lines = new string[rows];
        for (int r = 0; r < rows; r++)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(SplashGlyphRow(text[i], r));
            }
            lines[r] = sb.ToString();
        }
        return lines;
    }

    // Builds the stacked "NOVA 3D" / "VISUALISER" banner: two block-letter lines (each 5 rows), the
    // narrower one centered, separated by a blank row. All rows are padded to the wider line's width,
    // so the banner is a rectangular block of equal-width rows.
    // internal (not private) so the Variant-B engine-renderer wizard toolkit (SampleGame/WizardUi) can reuse
    // the exact same branded header art + centering, rather than duplicating the block-letter font.
    internal static string[] BuildSplashBlock()
    {
        var topLine = BuildSplashLine("NOVA 3D");
        var bottomLine = BuildSplashLine("VISUALISER");
        int w = Math.Max(topLine[0].Length, bottomLine[0].Length);

        var rows = new List<string>();
        foreach (var r in topLine) rows.Add(CenterPad(r, w));
        rows.Add(new string(' ', w));          // gap between the two block-letter lines
        foreach (var r in bottomLine) rows.Add(CenterPad(r, w));
        return rows.ToArray();
    }

    // Centers `s` within width `w` by padding both sides with spaces (returns `s` unchanged if it is
    // already at least `w` wide).
    static string CenterPad(string s, int w)
    {
        if (s.Length >= w) return s;
        int left = (w - s.Length) / 2;
        return new string(' ', left) + s + new string(' ', w - s.Length - left);
    }

    // Whether the terminal is roomy enough for the stacked block art (else the one-line title
    // fallback). The banner is ~11 rows tall plus the tagline/version, so it needs more height than
    // a single-line banner would; the width threshold rides the (now wider) passed block width.
    // internal so the Variant-B wizard toolkit reuses the same degrade + centering decisions.
    internal static bool SplashUsesArt(int width, int height, int blockWidth) =>
        blockWidth > 0 && width >= blockWidth + 2 && height >= 16;

    // Start column that centers `textLen` in `width` (never negative; oversized text clamps to 0).
    internal static int SplashLeft(int width, int textLen) => Math.Max(0, (width - textLen) / 2);

    // Headless check of the pure splash helpers (block dimensions + degrade/centering logic).
    static void SplashSelfTest()
    {
        bool ok = true;
        void Check(bool cond, string msg) { if (!cond) { ok = false; Console.WriteLine($"  FAIL: {msg}"); } }

        // Single-line builder: "NOVA 3D" is 7 cells (incl. the space) x 5 cols + 6 one-col gaps = 41.
        var line = BuildSplashLine("NOVA 3D");
        Check(line.Length == 5, "line has 5 rows");
        int lw = line.Length > 0 ? line[0].Length : 0;
        foreach (var row in line) Check(row.Length == lw, "line rows equal width");
        Check(lw == 41, $"expected line width 41, got {lw}");

        // Unknown chars degrade to a blank glyph — still 5 equal-width rows, no throw.
        var q = BuildSplashLine("?");
        Check(q.Length == 5 && q[0].Trim().Length == 0, "unknown char -> blank glyph");

        // Stacked banner: "NOVA 3D" over "VISUALISER" (10 cells x 5 + 9 gaps = 59 wide), separated by
        // a blank row: 5 + 1 + 5 = 11 rows, all padded to the wider (59) line.
        var block = BuildSplashBlock();
        Check(block.Length == 11, $"expected 11 stacked rows, got {block.Length}");
        int bw = block.Length > 0 ? block[0].Length : 0;
        foreach (var row in block) Check(row.Length == bw, "block rows equal width");
        Check(bw == 59, $"expected block width 59, got {bw}");

        // Roomy terminal shows the art; too-narrow or too-short degrades to the fallback line.
        Check(SplashUsesArt(bw + 2, 24, bw), "wide+tall uses art");
        Check(!SplashUsesArt(bw + 1, 24, bw), "too narrow -> fallback");
        Check(!SplashUsesArt(bw + 2, 8, bw), "too short -> fallback");

        // Centering clamps and never goes negative.
        Check(SplashLeft(80, 10) == 35, "centered left column");
        Check(SplashLeft(10, 40) == 0, "oversized text clamps to 0");

        // Terminal-resize plan (pure, ConsoleScreenAsync.ResizePlan) — the in-game resize path's decision.
        // A grow re-sizes buffers to newW*newH with the new aspect and flags a resize; an UNCHANGED size is
        // a no-op (so fixed-size renders / gputest are never perturbed); a tiny/zero/negative size clamps to
        // the minimum (no 0-length buffer / divide-by-zero) with a finite aspect.
        const float pa = 11f / 24f;
        var grow = ConsoleScreenAsync.ResizePlan(80, 25, 120, 40, pa);
        Check(grow.needsResize && grow.w == 120 && grow.h == 40, "resize: grow updates dims");
        Check(grow.w * grow.h == 120 * 40, "resize: buffer length == newW*newH");
        Check(Math.Abs(grow.aspect - ConsoleScreenAsync.RegionAspect(120, 40, pa)) < 1e-6f, "resize: aspect from new size");
        var same = ConsoleScreenAsync.ResizePlan(80, 25, 80, 25, pa);
        Check(!same.needsResize && same.w == 80 && same.h == 25, "resize: unchanged size is a no-op");
        var zero = ConsoleScreenAsync.ResizePlan(80, 25, 0, 0, pa);
        Check(zero.needsResize && zero.w == 1 && zero.h == 1 && zero.w * zero.h == 1, "resize: zero clamps to min");
        var neg = ConsoleScreenAsync.ResizePlan(80, 25, -5, -3, pa);
        Check(neg.w == 1 && neg.h == 1 && !float.IsNaN(neg.aspect) && !float.IsInfinity(neg.aspect), "resize: negative clamps to min, finite aspect");

        Console.WriteLine(ok ? "splashtest: PASS" : "splashtest: FAIL");
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

    // Brings the terminal back to a usable state (after a crash or a normal exit): reset colors and show the
    // cursor. The setup wizard now runs on the engine's own console renderer (SampleGame/WizardUi) and the
    // render loop is a plain Console app, so there is no UI framework to shut down here. Every step is
    // guarded so restoring the console never throws.
    static void RestoreConsole()
    {
        try { Console.ResetColor(); } catch { }
        try { Console.CursorVisible = true; } catch { }
    }

    // internal so the Variant-B toolkit wizard (SampleGame/WizardUi) reuses the EXACT same name/number parsing
    // as the live v2 Create dialog — guaranteeing the produced WorldConfig matches byte-for-byte.
    internal static string SanitizeWorldName(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
        }
        return sb.ToString();
    }

    // Parses a text field as a positive float (invariant), falling back to a default for blank/invalid/
    // non-positive input so the platform always gets a sane size.
    internal static float ParseFloatOr(string? text, float fallback)
    {
        if (float.TryParse((text ?? "").Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out float v) && v > 0f)
            return v;
        return fallback;
    }

}