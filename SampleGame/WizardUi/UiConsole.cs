using System;
using System.Text;
using Nova3DVisualiser;
using Nova3DVisualiser.Implementation;

namespace SampleGame.WizardUi;

// The wizard UI colour palette (truecolor), echoing the splash/HUD look: a cyan accent, muted grey labels,
// bright values, a dim tagline/help line, and a clear focus highlight. Plain data.
public sealed class UiTheme
{
    public Rgb24 Background;   // screen background
    public Rgb24 Accent;       // branding / section headers / selected option
    public Rgb24 Label;        // muted static text / unselected options
    public Rgb24 Value;        // bright values / button text
    public Rgb24 Dim;          // tagline / help line
    public Rgb24 FocusFg;      // focused widget text
    public Rgb24 FocusBg;      // focused widget background highlight
    public Rgb24 FieldBg;      // inset background for an unfocused interactive control
    public Rgb24 Warn;         // validation error text

    public static UiTheme Default() => new UiTheme
    {
        Background = new Rgb24(0, 0, 0),
        Accent     = new Rgb24(0, 220, 230),     // bright cyan
        Label      = new Rgb24(150, 150, 150),   // muted grey
        Value      = new Rgb24(235, 235, 235),   // bright
        Dim        = new Rgb24(110, 110, 110),   // dim grey
        FocusFg    = new Rgb24(0, 0, 0),         // black text ...
        FocusBg    = new Rgb24(0, 200, 215),     // ... on a cyan highlight
        FieldBg    = new Rgb24(28, 32, 36),      // subtle inset
        Warn       = new Rgb24(230, 90, 90),     // red validation message
    };
}

// A small truecolor text-mode console renderer for the wizard UI. Mirrors the engine renderer's approach — a
// per-cell char + fg + bg buffer, a diff-present that writes ONLY changed cells (ANSI cursor jumps + 24-bit
// SGR), and a per-frame AdaptToConsoleSize poll of Console.WindowWidth/Height that re-allocates + full-redraws
// on a window resize or font zoom (polling the size every frame is what makes the wizard reflow live, exactly
// like the in-game renderer). Unlike the engine screen it carries a BACKGROUND colour per cell (for
// field/focus highlights). Reuses the engine's EnableVirtualTerminal + the pure ResizePlan decision.
public sealed class UiConsole
{
    private const char Esc = (char)27;
    private const int MinDim = 1;

    public int Width { get; private set; }
    public int Height { get; private set; }

    private char[] _ch = Array.Empty<char>();
    private Rgb24[] _fg = Array.Empty<Rgb24>();
    private Rgb24[] _bg = Array.Empty<Rgb24>();
    private char[] _pch = Array.Empty<char>();
    private Rgb24[] _pfg = Array.Empty<Rgb24>();
    private Rgb24[] _pbg = Array.Empty<Rgb24>();
    private bool _firstPresent = true;
    private readonly StringBuilder _frame = new();

    public UiConsole()
    {
        ConsoleScreenAsync.EnableVirtualTerminal();          // 24-bit ANSI truecolor (no-op off-Windows)
        try { Console.CursorVisible = false; } catch { }
        Width = Math.Max(MinDim, SafeWidth());
        Height = Math.Max(MinDim, SafeHeight());
        Alloc(Width, Height);
    }

    private static int SafeWidth()  { try { return Console.WindowWidth;  } catch { return 80; } }
    private static int SafeHeight() { try { return Console.WindowHeight; } catch { return 25; } }

    private void Alloc(int w, int h)
    {
        int n = w * h;
        _ch = new char[n]; _fg = new Rgb24[n]; _bg = new Rgb24[n];
        _pch = new char[n]; _pfg = new Rgb24[n]; _pbg = new Rgb24[n];
        _firstPresent = true;
    }

    // Poll the console size; on a change (window resize or font zoom) re-allocate + force a full redraw.
    // Called EVERY UI-loop iteration (UiRunner), so a resize/zoom is caught within one ~20ms tick — exactly
    // like the in-game renderer. Reuses the engine's pure ResizePlan so the wizard's resize decision matches
    // the 3D renderer's. Returns true when it resized. Guarded — a transient read failure just retries next
    // frame.
    public bool AdaptToConsoleSize()
    {
        int cw, ch;
        try { cw = Console.WindowWidth; ch = Console.WindowHeight; } catch { return false; }
        var plan = ConsoleScreenAsync.ResizePlan(Width, Height, cw, ch, 0.5f);   // aspect unused by a text UI
        if (!plan.needsResize) return false;
        Width = plan.w; Height = plan.h;
        Alloc(Width, Height);
        try { Console.Clear(); } catch { }   // wipe stale glyphs from the old grid
        return true;
    }

    public void Clear(Rgb24 bg)
    {
        for (int i = 0; i < _ch.Length; i++) { _ch[i] = ' '; _fg[i] = bg; _bg[i] = bg; }
    }

    public void Put(int x, int y, char ch, Rgb24 fg, Rgb24 bg)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;   // clamp: never write out of bounds
        int i = y * Width + x;
        _ch[i] = ch; _fg[i] = fg; _bg[i] = bg;
    }

    public void Text(int x, int y, string s, Rgb24 fg, Rgb24 bg)
    {
        for (int k = 0; k < s.Length; k++) Put(x + k, y, s[k], fg, bg);
    }

    // Diff-present: emit only RUNS of cells that changed since last frame (ANSI cursor jump + 24-bit fg/bg on
    // change). A static screen writes (almost) nothing — the same trick the engine screen uses.
    public void Present()
    {
        int w = Width, h = Height;
        if (_ch.Length == 0) return;

        _frame.Clear();
        bool any = false;
        Rgb24 lastFg = default, lastBg = default; bool have = false;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int x = 0;
            while (x < w)
            {
                int idx = row + x;
                if (!_firstPresent && _ch[idx] == _pch[idx] && _fg[idx] == _pfg[idx] && _bg[idx] == _pbg[idx]) { x++; continue; }

                _frame.Append(Esc).Append('[').Append(y + 1).Append(';').Append(x + 1).Append('H');
                have = false;
                while (x < w)
                {
                    int ri = row + x;
                    if (!_firstPresent && _ch[ri] == _pch[ri] && _fg[ri] == _pfg[ri] && _bg[ri] == _pbg[ri]) break;
                    Rgb24 fg = _fg[ri], bg = _bg[ri];
                    if (!have || fg != lastFg || bg != lastBg) { AppendColor(_frame, fg, bg); lastFg = fg; lastBg = bg; have = true; }
                    _frame.Append(_ch[ri]);
                    _pch[ri] = _ch[ri]; _pfg[ri] = fg; _pbg[ri] = bg;
                    x++;
                }
                any = true;
            }
        }

        if (any) { _frame.Append(Esc).Append("[0m"); try { Console.Out.Write(_frame.ToString()); } catch { } }
        _firstPresent = false;
    }

    // 24-bit ANSI set foreground (ESC[38;2;r;g;bm) + background (ESC[48;2;r;g;bm).
    private static void AppendColor(StringBuilder sb, Rgb24 fg, Rgb24 bg)
    {
        sb.Append(Esc).Append("[38;2;").Append(fg.R).Append(';').Append(fg.G).Append(';').Append(fg.B).Append('m');
        sb.Append(Esc).Append("[48;2;").Append(bg.R).Append(';').Append(bg.G).Append(';').Append(bg.B).Append('m');
    }
}
