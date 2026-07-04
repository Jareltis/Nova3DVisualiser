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
        if (args.Length > 0 && args[0] == "splashtest") { SplashSelfTest(); return; }

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

        // Styled title splash (~2.5s, any key skips). Drawn with the plain Console BEFORE
        // Application.Init so it never fights Terminal.Gui. The self-test arg branches in Main
        // return before RunApp, so no `*test` invocation ever renders it.
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

        // ---- Terminal.Gui modal setup wizard ----
        Application.Init();
        try
        {
            // Cohesive dark scheme that echoes the splash/HUD palette: readable grey labels on black, a CYAN
            // accent for focus (matching the splash title + docked panel titles), cyan hotkeys. Replaces the
            // old green accents so the setup feels part of the same product.
            var scheme = new ColorScheme
            {
                Normal    = Application.Driver.MakeAttribute(Color.Gray,       Color.Black),      // labels / text / borders
                Focus     = Application.Driver.MakeAttribute(Color.Black,      Color.BrightCyan), // focused button / field — cyan accent
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),      // hotkey letters
                HotFocus  = Application.Driver.MakeAttribute(Color.Black,      Color.BrightCyan),
                Disabled  = Application.Driver.MakeAttribute(Color.DarkGray,   Color.Black),
            };
            Colors.Base = scheme;
            Colors.Dialog = scheme;
            Colors.Menu = scheme;

            // Branding schemes (splash-consistent): a bright cyan title + a dim grey tagline, drawn on the
            // wizard host by RunStepDialog so every step feels part of the same product.
            _brandScheme = SolidScheme(Color.BrightCyan, Color.Black);
            _tagScheme   = SolidScheme(Color.DarkGray,   Color.Black);

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
            // Leave a clean screen for the Terminal.Gui wizard.
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
    static string[] BuildSplashBlock()
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
    static bool SplashUsesArt(int width, int height, int blockWidth) =>
        blockWidth > 0 && width >= blockWidth + 2 && height >= 16;

    // Start column that centers `textLen` in `width` (never negative; oversized text clamps to 0).
    static int SplashLeft(int width, int textLen) => Math.Max(0, (width - textLen) / 2);

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

    // Runs one wizard step's centered Dialog inside a full-screen host toplevel. Because the host
    // fills and overdraws the WHOLE screen when it draws, running a step fully covers the previous
    // step — fixing the lingering-frame bug with NO manual Clear/Refresh. The inner Dialog keeps
    // its centered, fixed-size box look (border, title, buttons); the host is borderless and fills
    // the screen with the wizard's grey-on-black scheme. The Dialog's buttons still call
    // Application.RequestStop(), which stops this host, so each ShowXxxDialog returns as before.
    // Branding schemes for the wizard host header (set in RunApp after Application.Init).
    private static ColorScheme? _brandScheme;
    private static ColorScheme? _tagScheme;

    // A one-attribute scheme (same colour for every state) — for the non-interactive branding labels.
    static ColorScheme SolidScheme(Color fg, Color bg)
    {
        var a = Application.Driver.MakeAttribute(fg, bg);
        return new ColorScheme { Normal = a, Focus = a, HotNormal = a, HotFocus = a, Disabled = a };
    }

    // The branded header occupies the top HeaderRows; the step Dialog is centred below it. A dialog never
    // shrinks below these (it just overflows a truly tiny terminal — unavoidable).
    private const int HeaderRows = 3;
    private const int MinDialogW = 24;
    private const int MinDialogH = 7;

    // desiredW/H are the step's natural size; the dialog is clamped to the CURRENT console so a font zoom
    // (Ctrl+scroll → fewer/more cells) never clips it or sticks it in a corner.
    static void RunStepDialog(Dialog dialog, int desiredW, int desiredH)
    {
        var host = new Window
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            ColorScheme = Colors.Base,
        };
        host.Border.BorderStyle = BorderStyle.None;

        // Splash-consistent branding across the top — Pos.Center() re-centres it on resize automatically.
        host.Add(new Label("Nova 3D Visualiser") { X = Pos.Center(), Y = 1, ColorScheme = _brandScheme ?? Colors.Base });
        host.Add(new Label("A S C I I   3 D   E N G I N E") { X = Pos.Center(), Y = 2, ColorScheme = _tagScheme ?? Colors.Base });

        host.Add(dialog);

        // Clamp the fixed-size Dialog to the CURRENT console and centre it in the area below the header — this
        // is re-applied whenever Terminal.Gui reports a resize (a window STRETCH) so the dialog reflows and
        // never clips. NOTE: a Ctrl+scroll FONT ZOOM raises no resize event in Terminal.Gui v1's Windows/Net
        // driver, so the wizard does not live-reflow on a font zoom (a v1 limitation — the in-game 3D does).
        void Fit()
        {
            int cols = Application.Driver?.Cols ?? desiredW;
            int rows = Application.Driver?.Rows ?? desiredH;
            int w = Math.Min(desiredW, Math.Max(MinDialogW, cols - 2));
            int h = Math.Min(desiredH, Math.Max(MinDialogH, rows - HeaderRows - 1));
            dialog.Width  = w;
            dialog.Height = h;
            dialog.X = Pos.Center();
            dialog.Y = HeaderRows + Math.Max(0, (rows - HeaderRows - h) / 2);   // centred below the header
            host.SetNeedsDisplay();
        }
        Fit();

        // WINDOW STRETCH: Terminal.Gui's driver detects it and fires Resized → re-fit immediately.
        void OnResized(Application.ResizedEventArgs _) { Fit(); Application.Refresh(); }
        Application.Resized += OnResized;

        try { Application.Run(host); }
        finally { Application.Resized -= OnResized; }
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
        RunStepDialog(dialog, 50, 9);

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
        RunStepDialog(dialog, 50, 9);

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

        RunStepDialog(dialog, 56, 11);

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
        RunStepDialog(dialog, 50, 9);

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
        RunStepDialog(dialog, 56, 20);

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
        RunStepDialog(dialog, 50, height);

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