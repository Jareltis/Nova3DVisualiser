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

    public void MainLoop()
    {
        _activeScene.Start();

        var pacing = Stopwatch.StartNew();

        while (true)
        {
            pacing.Restart();

            GameTime.Tick();

            if (Input.IsGetKey(ConsoleKey.Escape)) break;

            _activeScene.Update();

            _screen.RenderFrame(_activeScene);

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
