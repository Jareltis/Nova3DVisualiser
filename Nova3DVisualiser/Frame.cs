using System.Diagnostics;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.StaticClass;

namespace Nova3DVisualiser;
public class Frame(Scene activeScene, Screen screen)
{
    private readonly Screen _screen = screen;
    private readonly Scene _activeScene = activeScene;

    private const int TargetFps = 60;   // tunable frame-rate cap
    private bool _escWasDown = false;   // ESC quit is EDGE-triggered (see below)

    public void MainLoop()
    {
        _activeScene.Start();

        var pacing = Stopwatch.StartNew();

        while (true)
        {
            pacing.Restart();

            GameTime.Tick();

            // Quit on a FRESH ESC press, but only when the scene allows it — a scene that captures ESC (the
            // chat / field-entry cancel) returns AllowQuit=false while doing so. Edge-triggering means a held
            // ESC that just cancelled the chat can't quit the following frame once AllowQuit flips back true.
            bool escDown = Input.IsGetKey(ConsoleKey.Escape);
            if (escDown && !_escWasDown && _activeScene.AllowQuit) break;
            _escWasDown = escDown;

            _activeScene.Update();

            _screen.RenderFrame(_activeScene);

            // Standalone top-left fps readout — suppressed when the scene shows fps in its own HUD (the
            // docked editor's Status bar) so they don't collide (Fix 1).
            if (_activeScene.ShowFrameFps)
                _screen.PrintText("Fps: " + Double.Round(GameTime.GetFps(), 1) + "       ", Vector2Int.Zero);

            // Frame pacing: sleep the remainder of this frame's budget so light scenes
            // don't busy-spin. The sleep is included in next frame's dt (measured by Tick).
            double workMs = pacing.Elapsed.TotalMilliseconds;
            int sleepMs = (int)(1000.0 / TargetFps - workMs);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }

        Console.CursorVisible = true;
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("Exited.");
        Logger.Info("User requested quit");
        Environment.Exit(0);
    }
}
