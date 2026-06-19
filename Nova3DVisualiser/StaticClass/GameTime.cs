using System.Diagnostics;

namespace Nova3DVisualiser.StaticClass;

public static class GameTime
{
    // High-resolution monotonic clock, started once.
    private static readonly Stopwatch _clock = Stopwatch.StartNew();

    private static double _lastTickSeconds = 0;
    private static bool _started = false;

    private static double _deltaTime = 0;
    private static double _fps = 0;

    // Clamp so a stall/breakpoint can't cause a movement jump.
    private const double MaxDeltaTime = 0.1;

    // Call ONCE per frame. Computes the real frame-to-frame dt (including any pacing sleep
    // from the previous frame) and stores it so every GetDeltaTime() this frame is identical.
    public static void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;

        if (!_started)
        {
            _started = true;
            _lastTickSeconds = now;
            _deltaTime = 0;
            _fps = 0;
            return;
        }

        double dt = now - _lastTickSeconds;
        _lastTickSeconds = now;

        if (dt < 0) dt = 0;
        if (dt > MaxDeltaTime) dt = MaxDeltaTime;

        _deltaTime = dt;
        _fps = dt > 0 ? 1.0 / dt : 0;
    }

    public static double GetFps()
    { return _fps; }
    public static float GetDeltaTime()
    { return (float)_deltaTime; }
}
