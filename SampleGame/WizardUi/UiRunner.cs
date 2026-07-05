using System;
using System.Threading;

namespace SampleGame.WizardUi;

// The wizard UI loop. Each iteration: poll the console size (reflow on stretch OR Ctrl+scroll font zoom),
// re-lay-out the screen, draw it, and diff-present; then drain any pending keys NON-BLOCKING. Blocking on
// ReadKey would freeze the reflow between keystrokes, so instead it sleeps briefly when idle and re-polls the
// size — exactly how the engine's render loop catches font zoom. Stop() ends the loop.
public sealed class UiRunner
{
    private readonly UiConsole _con = new();
    private readonly UiTheme _theme;
    private bool _running;

    public UiRunner(UiTheme theme) { _theme = theme; }
    public void Stop() => _running = false;

    public void Run(UiScreen screen)
    {
        UiWinConsole.EnableMouse();                    // console mouse (Windows); restored in the finally
        try
        {
            _running = true;
            while (_running)
            {
                _con.AdaptToConsoleSize();                 // reflow on stretch / Ctrl+scroll font zoom
                screen.Layout(_con.Width, _con.Height);
                _con.Clear(_theme.Background);
                screen.Draw(_con, _theme);
                _con.Present();

                // Drain pending console input NON-BLOCKING (keys AND left-clicks), so a font zoom during an
                // idle wait still reflows within a frame. None-events (key-up / mouse move) are just consumed.
                bool acted = false;
                while (_running && UiWinConsole.TryReadEvent(out var ev))
                {
                    if (ev.Kind == UiInputKind.Key) { screen.HandleKey(ev.Key); acted = true; }
                    else if (ev.Kind == UiInputKind.MouseClick) { screen.HandleClick(ev.MouseX, ev.MouseY); acted = true; }
                }
                if (_running && !acted) Thread.Sleep(20);   // idle: re-poll the size ~50x/s (live font-zoom)
            }
        }
        finally
        {
            UiWinConsole.RestoreMouse();                // put QuickEdit etc. back so the console isn't left odd
        }
    }
}
