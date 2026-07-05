using System;
using System.Collections.Generic;
using Nova3DVisualiser;

namespace SampleGame.WizardUi;

// A full-screen wizard screen: a branded header (reusing the splash "NOVA 3D / VISUALISER" art), a set of
// vertically-stacked, horizontally-centred widgets, and a bottom help line. Owns focus (Tab / Up / Down) and
// dispatches each key to the focused widget FIRST (so an OptionGroup eats arrows before they move focus).
// Layout + focus navigation are pure/deterministic so `uitest` can assert them without a console.
public sealed class UiScreen
{
    public string HelpText;
    public Action? OnCancel;                 // Esc
    public bool CompactHeader;               // force the one-line title (for form-heavy screens with many widgets)
    public int FormW, FormH;                 // >0 => FORM layout (explicit FormCol/FormRow in a centred box)
    public string? ErrorText;                // a validation message, drawn in the warning colour above the help line
    private readonly List<UiWidget> _widgets = new();
    public int FocusIndex { get; private set; } = -1;

    // Header geometry, set by Layout and exposed for the tests.
    public bool HeaderUsesArt { get; private set; }
    public int HeaderRows { get; private set; }
    public int ContentTop { get; private set; }
    public int HelpRow { get; private set; }

    public UiScreen(string helpText) { HelpText = helpText; }

    public IReadOnlyList<UiWidget> Widgets => _widgets;

    public void Add(params UiWidget[] ws)
    {
        _widgets.AddRange(ws);
        if (FocusIndex < 0) FocusIndex = FirstFocusable(_widgets);
    }

    public UiWidget? Focused => (FocusIndex >= 0 && FocusIndex < _widgets.Count) ? _widgets[FocusIndex] : null;

    // The branded header art is a fixed deterministic value — build it once.
    private static string[]? _block;
    private static string[] Block => _block ??= Program.BuildSplashBlock();

    // ---- pure focus navigation (testable) ----
    public static int FirstFocusable(IReadOnlyList<UiWidget> ws)
    {
        for (int i = 0; i < ws.Count; i++) if (ws[i].Focusable) return i;
        return -1;
    }
    public static int NextFocusable(IReadOnlyList<UiWidget> ws, int from)
    {
        if (ws.Count == 0) return -1;
        for (int step = 1; step <= ws.Count; step++)
        {
            int i = ((from + step) % ws.Count + ws.Count) % ws.Count;
            if (ws[i].Focusable) return i;
        }
        return from;
    }
    public static int PrevFocusable(IReadOnlyList<UiWidget> ws, int from)
    {
        if (ws.Count == 0) return -1;
        for (int step = 1; step <= ws.Count; step++)
        {
            int i = ((from - step) % ws.Count + ws.Count) % ws.Count;
            if (ws[i].Focusable) return i;
        }
        return from;
    }

    public void FocusNext() => FocusIndex = NextFocusable(_widgets, FocusIndex);
    public void FocusPrev() => FocusIndex = PrevFocusable(_widgets, FocusIndex);

    // ---- pure hit-testing (testable) ----
    // The index of the focusable/clickable widget whose laid-out rectangle contains (x,y), or -1 for empty
    // space or a non-clickable widget (a label). Uses each widget's CURRENT Bounds, so it tracks the layout
    // through resizes / font zoom automatically.
    public static int HitTest(IReadOnlyList<UiWidget> ws, int x, int y)
    {
        for (int i = 0; i < ws.Count; i++)
        {
            if (!ws[i].Focusable) continue;   // labels / static text aren't click targets
            var b = ws[i].Bounds;
            if (x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H) return i;
        }
        return -1;
    }

    // ---- layout (deterministic; testable) ----
    // Positions the branded header + the vertically-stacked, horizontally-centred widgets + the help line for
    // a given console size. Reuses the splash art (or its one-line fallback on a small terminal). Every
    // position is clamped non-negative so a tiny size never throws or writes off-screen (Draw also clamps).
    public void Layout(int w, int h)
    {
        int blockW = Block.Length > 0 ? Block[0].Length : 0;
        HeaderUsesArt = !CompactHeader && Program.SplashUsesArt(w, h, blockW);
        HeaderRows = HeaderUsesArt ? Block.Length : 1;           // 11 rows of art, or a 1-line title
        int tagRow = HeaderRows;                                 // tagline directly under the header
        ContentTop = Math.Min(Math.Max(0, h - 1), tagRow + 2);   // a blank gap under the tagline
        HelpRow = Math.Max(0, h - 1);
        int contentBottom = Math.Max(ContentTop, HelpRow - 1);
        int contentH = Math.Max(0, contentBottom - ContentTop);

        if (FormW > 0)
        {
            // FORM layout: centre a FormW x FormH box below the header and place each widget at its explicit
            // (FormCol, FormRow) within it (multi-column, like the v2 wizard's Create/Network forms).
            int boxX = Math.Max(0, (w - FormW) / 2);
            int boxY = ContentTop + Math.Max(0, (contentH - FormH) / 2);
            foreach (var wd in _widgets)
            {
                int col = wd.FormCol < 0 ? 0 : wd.FormCol;
                int row = wd.FormRow < 0 ? 0 : wd.FormRow;
                wd.Bounds = new UiRect(Math.Max(0, boxX + col), Math.Max(0, boxY + row), wd.PreferredW, wd.PreferredH);
            }
            return;
        }

        // AUTO layout: vertically-stacked, horizontally-centred widgets.
        int gap = 1;
        int stackH = 0;
        foreach (var wd in _widgets) stackH += wd.PreferredH;
        if (_widgets.Count > 1) stackH += gap * (_widgets.Count - 1);

        int y = ContentTop + Math.Max(0, (contentH - stackH) / 2);
        foreach (var wd in _widgets)
        {
            int ww = wd.PreferredW, wh = wd.PreferredH;
            int x = Math.Max(0, (w - ww) / 2);
            wd.Bounds = new UiRect(x, y, ww, wh);
            y += wh + gap;
        }
    }

    public void Draw(UiConsole c, UiTheme t)
    {
        DrawHeader(c, t);
        for (int i = 0; i < _widgets.Count; i++)
            _widgets[i].Draw(c, i == FocusIndex, t);

        // A validation error, one row above the help line, in the warning colour.
        if (!string.IsNullOrEmpty(ErrorText) && HelpRow - 1 >= 0 && HelpRow - 1 < c.Height)
        {
            int x = Math.Max(0, (c.Width - ErrorText.Length) / 2);
            c.Text(x, HelpRow - 1, ErrorText, t.Warn, t.Background);
        }

        if (HelpRow >= 0 && HelpRow < c.Height && HelpText.Length > 0)
        {
            int x = Math.Max(0, (c.Width - HelpText.Length) / 2);
            c.Text(x, HelpRow, HelpText, t.Dim, t.Background);
        }
    }

    private void DrawHeader(UiConsole c, UiTheme t)
    {
        if (HeaderUsesArt)
        {
            for (int i = 0; i < Block.Length; i++)
                c.Text(Program.SplashLeft(c.Width, Block[i].Length), i, Block[i], t.Accent, t.Background);
            const string tag = "A S C I I   3 D   E N G I N E";
            c.Text(Program.SplashLeft(c.Width, tag.Length), HeaderRows, tag, t.Dim, t.Background);
        }
        else
        {
            const string title = "Nova 3D Visualiser";
            c.Text(Program.SplashLeft(c.Width, title.Length), 0, title, t.Accent, t.Background);
        }
    }

    // ---- input ----
    public void HandleKey(ConsoleKeyInfo key)
    {
        var fw = Focused;
        if (fw != null && fw.HandleKey(key)) return;   // widget consumed it (option arrows / button Enter)

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift)) FocusPrev(); else FocusNext();
                break;
            case ConsoleKey.DownArrow: FocusNext(); break;
            case ConsoleKey.UpArrow:   FocusPrev(); break;
            case ConsoleKey.Escape:    OnCancel?.Invoke(); break;
        }
    }

    // A left-click at (x,y): if it lands on a clickable widget, focus it and activate it (press a button /
    // select an option row). Clicks on empty space or labels do nothing.
    public void HandleClick(int x, int y)
    {
        int hit = HitTest(_widgets, x, y);
        if (hit < 0) return;
        FocusIndex = hit;
        _widgets[hit].OnClick(x, y);
    }
}
