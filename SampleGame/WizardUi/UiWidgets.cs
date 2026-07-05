using System;
using System.Collections.Generic;
using System.Globalization;
using Nova3DVisualiser;

namespace SampleGame.WizardUi;

// A laid-out rectangle (cell coordinates), assigned to each widget by UiScreen.Layout.
public struct UiRect
{
    public int X, Y, W, H;
    public UiRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
}

// Base widget: a rectangle that reports a preferred size, draws itself (knowing whether it has focus), and —
// when focused — gets first crack at a key (so an OptionGroup can eat arrows before the screen treats them as
// focus navigation).
public abstract class UiWidget
{
    public UiRect Bounds;
    public virtual bool Focusable => false;
    public abstract int PreferredW { get; }
    public virtual int PreferredH => 1;
    public abstract void Draw(UiConsole c, bool focused, UiTheme t);
    // Returns true if the widget consumed the key.
    public virtual bool HandleKey(ConsoleKeyInfo key) => false;
    // A left-click landed inside this widget's bounds at (x,y) — activate it (press a button, select an
    // option row). Only called for Focusable widgets (hit-testing skips labels).
    public virtual void OnClick(int x, int y) { }

    // Explicit position within a UiScreen FORM box (col,row from the box origin); -1 = unset (auto-stack).
    // Used by form screens (Create/Network) to lay widgets out in multiple columns like the v2 wizard.
    public int FormCol = -1;
    public int FormRow = -1;
    public UiWidget At(int col, int row) { FormCol = col; FormRow = row; return this; }
}

// Static text. A `title` label renders in the accent colour (a sub-header); otherwise muted.
public sealed class UiLabel : UiWidget
{
    public string Text;
    public bool Title;
    public UiLabel(string text, bool title = false) { Text = text; Title = title; }
    public override int PreferredW => Text.Length;
    public override void Draw(UiConsole c, bool focused, UiTheme t)
        => c.Text(Bounds.X, Bounds.Y, Text, Title ? t.Accent : t.Label, t.Background);
}

// A push button ("[ Text ]"). Enter/Space activates it (fires OnPressed). Highlighted when focused.
public sealed class UiButton : UiWidget
{
    public string Text;
    public Action? OnPressed;
    public UiButton(string text) { Text = text; }
    public override bool Focusable => true;
    public override int PreferredW => Text.Length + 4;   // "[ " + Text + " ]"
    public override void Draw(UiConsole c, bool focused, UiTheme t)
    {
        string s = "[ " + Text + " ]";
        Rgb24 fg = focused ? t.FocusFg : t.Value;
        Rgb24 bg = focused ? t.FocusBg : t.FieldBg;
        c.Text(Bounds.X, Bounds.Y, s, fg, bg);
    }
    public override bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar) { OnPressed?.Invoke(); return true; }
        return false;
    }
    public override void OnClick(int x, int y) => OnPressed?.Invoke();   // clicking a button presses it
}

// A radio-style option list (one selected). Up/Left and Down/Right move the selection (wrapping); the screen's
// Tab moves focus OFF it. The selected option shows a filled marker + accent; a focused group highlights the
// selected row.
public sealed class UiOptionGroup : UiWidget
{
    public string[] Labels;
    public int Selected;
    public bool Horizontal;               // false = one option per row (default); true = a single row
    private const int HSpace = 2;         // gap between options in horizontal mode

    public UiOptionGroup(string[] labels, int selected = 0, bool horizontal = false)
    {
        Labels = labels;
        Horizontal = horizontal;
        Selected = Math.Clamp(selected, 0, Math.Max(0, labels.Length - 1));
    }
    public override bool Focusable => true;
    public override int PreferredH => Horizontal ? 1 : Math.Max(1, Labels.Length);
    public override int PreferredW
    {
        get
        {
            if (Horizontal)
            {
                int w = 0;
                for (int k = 0; k < Labels.Length; k++) w += 4 + Labels[k].Length + (k > 0 ? HSpace : 0);
                return Math.Max(4, w);
            }
            int m = 0; foreach (var l in Labels) m = Math.Max(m, l.Length); return m + 4;   // "(*) "
        }
    }

    // Wrap an index into [0,n) (so arrows wrap around the ends).
    public static int Wrap(int i, int n) => n <= 0 ? 0 : ((i % n) + n) % n;

    // The starting column (relative to Bounds.X) of each option in horizontal mode.
    private int OffsetOf(int k)
    {
        int x = 0;
        for (int i = 0; i < k; i++) x += 4 + Labels[i].Length + HSpace;
        return x;
    }

    public override void Draw(UiConsole c, bool focused, UiTheme t)
    {
        for (int k = 0; k < Labels.Length; k++)
        {
            bool sel = k == Selected;
            string s = (sel ? "(*) " : "( ) ") + Labels[k];
            Rgb24 fg, bg;
            if (focused && sel) { fg = t.FocusFg; bg = t.FocusBg; }     // the active choice
            else if (sel)       { fg = t.Accent;  bg = t.Background; }  // selected but group not focused
            else                { fg = t.Label;   bg = t.Background; }  // unselected (muted)
            if (Horizontal) c.Text(Bounds.X + OffsetOf(k), Bounds.Y, s, fg, bg);
            else            c.Text(Bounds.X, Bounds.Y + k, s, fg, bg);
        }
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.LeftArrow:
                Selected = Wrap(Selected - 1, Labels.Length); return true;
            case ConsoleKey.DownArrow:
            case ConsoleKey.RightArrow:
                Selected = Wrap(Selected + 1, Labels.Length); return true;
        }
        return false;
    }

    // Click selects the option under the cursor (a row in vertical mode, a segment in horizontal mode).
    public override void OnClick(int x, int y)
    {
        if (Labels.Length == 0) return;
        if (!Horizontal) { Selected = Math.Clamp(y - Bounds.Y, 0, Labels.Length - 1); return; }
        int rx = x - Bounds.X;
        for (int k = 0; k < Labels.Length; k++)
        {
            int start = OffsetOf(k), end = start + 4 + Labels[k].Length;
            if (rx >= start && rx < end) { Selected = k; return; }
        }
    }
}

// A labeled boolean toggle ("[x] Label" / "[ ] Label"). Space / Enter / click toggles it. Checked shows the
// marker + accent; unchecked is muted; focus highlights the whole row.
public sealed class UiCheckBox : UiWidget
{
    public string Text;
    public bool Checked;
    public Action<bool>? OnChanged;
    public UiCheckBox(string text, bool @checked = false) { Text = text; Checked = @checked; }
    public override bool Focusable => true;
    public override int PreferredW => Text.Length + 4;   // "[x] " + Text

    public void Toggle() { Checked = !Checked; OnChanged?.Invoke(Checked); }

    public override void Draw(UiConsole c, bool focused, UiTheme t)
    {
        string s = (Checked ? "[x] " : "[ ] ") + Text;
        Rgb24 fg, bg;
        if (focused)      { fg = t.FocusFg; bg = t.FocusBg; }      // active
        else if (Checked) { fg = t.Accent;  bg = t.Background; }   // on (accent)
        else              { fg = t.Label;   bg = t.Background; }   // off (muted)
        c.Text(Bounds.X, Bounds.Y, s, fg, bg);
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Spacebar || key.Key == ConsoleKey.Enter) { Toggle(); return true; }
        return false;
    }
    public override void OnClick(int x, int y) => Toggle();
}

// A single-line editable text field with a visible caret + an inset field style. Type printable characters,
// Backspace/Delete, move the caret with Left/Right/Home/End. An optional NUMERIC mode accepts only digits, a
// single '.', and a leading '-'. Text longer than the field scrolls horizontally to keep the caret visible.
public sealed class UiTextInput : UiWidget
{
    public string Text;
    public int Caret;              // 0..Text.Length
    public bool Numeric;
    public int FieldWidth;

    public UiTextInput(string text = "", int fieldWidth = 20, bool numeric = false)
    {
        Text = text ?? "";
        Numeric = numeric;
        FieldWidth = Math.Max(1, fieldWidth);
        Caret = Text.Length;
    }

    public override bool Focusable => true;
    public override int PreferredW => FieldWidth;

    // The left offset of the visible window so the caret stays in view (pure, testable).
    public static int ScrollOffset(int caret, int fieldWidth) => Math.Max(0, caret - (fieldWidth - 1));

    // Whether `ch` may be inserted at `caret` in numeric mode given the current text (pure, testable).
    public static bool NumericAccepts(string text, int caret, char ch)
    {
        if (ch >= '0' && ch <= '9') return true;
        if (ch == '.') return !text.Contains('.');
        if (ch == '-') return caret == 0 && !text.Contains('-');
        return false;
    }

    // Parse the field as a number with a fallback (pure, testable; invariant culture).
    public static float ParseFloat(string? text, float fallback)
        => float.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public float AsFloat(float fallback) => ParseFloat(Text, fallback);
    public int AsInt(int fallback) => ParseInt(Text, fallback);

    public override void Draw(UiConsole c, bool focused, UiTheme t)
    {
        int off = ScrollOffset(Caret, FieldWidth);
        Rgb24 fieldFg = t.Value;
        Rgb24 fieldBg = focused ? t.FieldBg : t.FieldBg;   // inset either way; the caret marks focus
        for (int i = 0; i < FieldWidth; i++)
        {
            int ti = off + i;
            char ch = ti < Text.Length ? Text[ti] : ' ';
            bool caretHere = focused && ti == Caret;
            Rgb24 fg = caretHere ? t.FocusFg : fieldFg;
            Rgb24 bg = caretHere ? t.FocusBg : fieldBg;    // block caret = an accent cell
            c.Put(Bounds.X + i, Bounds.Y, ch, fg, bg);
        }
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:  Caret = Math.Max(0, Caret - 1); return true;
            case ConsoleKey.RightArrow: Caret = Math.Min(Text.Length, Caret + 1); return true;
            case ConsoleKey.Home:       Caret = 0; return true;
            case ConsoleKey.End:        Caret = Text.Length; return true;
            case ConsoleKey.Backspace:
                if (Caret > 0) { Text = Text.Remove(Caret - 1, 1); Caret--; }
                return true;
            case ConsoleKey.Delete:
                if (Caret < Text.Length) Text = Text.Remove(Caret, 1);
                return true;
        }

        char ch = key.KeyChar;
        if (ch >= ' ' && !char.IsControl(ch))
        {
            // Consume any printable key; insert it only if allowed (numeric mode filters).
            if (!Numeric || NumericAccepts(Text, Caret, ch))
            {
                Text = Text.Insert(Caret, ch.ToString());
                Caret++;
            }
            return true;
        }
        return false;
    }

    // Click focuses the field and drops the caret at the end (good enough this stage).
    public override void OnClick(int x, int y) => Caret = Text.Length;
}

// A scrollable, selectable list. Up/Down move the selection (clamped); the visible window of VisibleRows rows
// scrolls to keep the selection in view. Enter (or a click) selects/activates; a click selects the clicked
// row. Selected row shows a marker + accent; focus highlights it.
public sealed class UiListView : UiWidget
{
    public List<string> Items;
    public int Selected;
    public int VisibleRows;
    public Action<int>? OnActivate;

    public UiListView(IEnumerable<string> items, int visibleRows = 5, int selected = 0)
    {
        Items = new List<string>(items);
        VisibleRows = Math.Max(1, visibleRows);
        Selected = Items.Count == 0 ? 0 : Math.Clamp(selected, 0, Items.Count - 1);
    }

    public override bool Focusable => true;
    public override int PreferredH => Math.Min(VisibleRows, Math.Max(1, Items.Count));
    public override int PreferredW
    {
        get { int m = 0; foreach (var it in Items) m = Math.Max(m, it.Length); return Math.Max(4, m) + 2; }  // "> "
    }

    // Minimal-scroll visible window start: keep `selected` in [start, start+rows), clamped to the list ends.
    // Pure + testable (mirrors the HUD chat/Hierarchy visible-window helpers).
    public static int Window(int count, int rows, int selected)
    {
        if (count <= rows) return 0;
        int start = 0;
        if (selected >= rows) start = selected - rows + 1;                 // scrolled so selection is last visible
        if (selected < start) start = selected;                            // (never needed here, kept for clarity)
        return Math.Clamp(start, 0, Math.Max(0, count - rows));
    }

    public override void Draw(UiConsole c, bool focused, UiTheme t)
    {
        int rows = PreferredH;
        int start = Window(Items.Count, rows, Selected);
        for (int r = 0; r < rows; r++)
        {
            int idx = start + r;
            if (idx >= Items.Count) break;
            bool sel = idx == Selected;
            string s = (sel ? "> " : "  ") + Items[idx];
            Rgb24 fg, bg;
            if (focused && sel) { fg = t.FocusFg; bg = t.FocusBg; }
            else if (sel)       { fg = t.Accent;  bg = t.Background; }
            else                { fg = t.Label;   bg = t.Background; }
            c.Text(Bounds.X, Bounds.Y + r, s, fg, bg);
        }
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        if (Items.Count == 0) return false;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:   Selected = Math.Max(0, Selected - 1); return true;
            case ConsoleKey.DownArrow: Selected = Math.Min(Items.Count - 1, Selected + 1); return true;
            case ConsoleKey.Enter:     OnActivate?.Invoke(Selected); return true;
        }
        return false;
    }

    // Click a visible row to select it (maps the click row through the current scroll window).
    public override void OnClick(int x, int y)
    {
        if (Items.Count == 0) return;
        int rows = PreferredH;
        int start = Window(Items.Count, rows, Selected);
        int idx = start + (y - Bounds.Y);
        if (idx >= 0 && idx < Items.Count) Selected = idx;
    }
}
