using System;
using WStep = SampleGame.WizardUi.NewWizard.WStep;
using WOutcome = SampleGame.WizardUi.NewWizard.WOutcome;

namespace SampleGame.WizardUi;

// Headless tests for the pure toolkit logic behind the interactive screen: LAYOUT (positions for a size),
// RE-LAYOUT on resize (re-centres, no overflow, clamps on tiny sizes), FOCUS navigation (Tab/Up/Down cycle
// the focusable widgets in order, wrapping), and OptionGroup SELECTION (arrows wrap the index). The
// interactive font-zoom itself is verified live via `uidemo`. Dispatched as the `uitest` arg in Program.Main.
public static class UiSelfTest
{
    public static void Run()
    {
        bool ok = true;
        void Check(bool c, string m) { if (!c) { ok = false; Console.WriteLine($"  FAIL: {m}"); } }

        // A representative screen: a title label (not focusable) + an option group + two buttons.
        UiScreen NewScreen()
        {
            var s = new UiScreen("help");
            s.Add(new UiLabel("Session mode", title: true),
                  new UiOptionGroup(new[] { "Local / Offline", "Online" }, 0),
                  new UiButton("Ok"),
                  new UiButton("Back"));
            return s;
        }

        // ---- LAYOUT (roomy: art header, widgets below it, centred, in bounds, above the help line) ----
        var s1 = NewScreen();
        s1.Layout(100, 40);
        Check(s1.HeaderUsesArt, "roomy: header uses art");
        Check(s1.HeaderRows == 11, "art header is 11 rows");
        Check(s1.HelpRow == 39, "help row at the bottom");
        int prevY = -1;
        foreach (var wd in s1.Widgets)
        {
            Check(wd.Bounds.Y > s1.HeaderRows, "widget below the header");
            Check(wd.Bounds.Y > prevY, "widgets stacked top-to-bottom");
            Check(wd.Bounds.X >= 0 && wd.Bounds.X + wd.Bounds.W <= 100, "widget within width");
            Check(wd.Bounds.Y + wd.Bounds.H <= s1.HelpRow, "widget above the help line");
            prevY = wd.Bounds.Y;
        }
        Check(Program.SplashLeft(100, 59) == (100 - 59) / 2, "header centred (SplashLeft)");

        // ---- RE-LAYOUT on resize: re-centres for a new size, no overflow ----
        s1.Layout(60, 30);
        foreach (var wd in s1.Widgets)
            Check(wd.Bounds.X >= 0 && wd.Bounds.X + wd.Bounds.W <= 60, "relayout: within the new width");
        // A widening re-centres a widget further right than the narrower layout did.
        var g60 = s1.Widgets[1]; int x60 = g60.Bounds.X;
        s1.Layout(120, 30);
        Check(g60.Bounds.X > x60, "relayout: wider screen re-centres further right");

        // ---- tiny size: degrades + clamps, never negative / throws ----
        s1.Layout(10, 4);
        Check(!s1.HeaderUsesArt, "tiny: degrades to a one-line header");
        foreach (var wd in s1.Widgets)
            Check(wd.Bounds.X >= 0 && wd.Bounds.Y >= 0, "tiny: non-negative positions");
        s1.Layout(0, 0);   // pathological — must not throw
        foreach (var wd in s1.Widgets)
            Check(wd.Bounds.X >= 0 && wd.Bounds.Y >= 0, "zero size: non-negative positions");

        // ---- FOCUS navigation (label 0 skipped; group 1, ok 2, back 3; wraps) ----
        var s2 = NewScreen();
        var ws = s2.Widgets;
        Check(UiScreen.FirstFocusable(ws) == 1, "first focusable = option group");
        Check(UiScreen.NextFocusable(ws, 1) == 2, "next: group -> ok");
        Check(UiScreen.NextFocusable(ws, 2) == 3, "next: ok -> back");
        Check(UiScreen.NextFocusable(ws, 3) == 1, "next wraps: back -> group");
        Check(UiScreen.PrevFocusable(ws, 1) == 3, "prev wraps: group -> back");
        Check(UiScreen.PrevFocusable(ws, 2) == 1, "prev: ok -> group");
        Check(s2.FocusIndex == 1, "initial focus = first focusable");
        s2.FocusNext(); Check(s2.FocusIndex == 2, "FocusNext -> ok");
        s2.FocusNext(); Check(s2.FocusIndex == 3, "FocusNext -> back");
        s2.FocusNext(); Check(s2.FocusIndex == 1, "FocusNext wraps -> group");
        s2.FocusPrev(); Check(s2.FocusIndex == 3, "FocusPrev wraps -> back");

        // A screen with NO focusable widgets stays at -1 and never moves.
        var s3 = new UiScreen("h");
        s3.Add(new UiLabel("just text"));
        Check(s3.FocusIndex == -1, "no focusable -> index -1");

        // ---- OptionGroup selection (arrows wrap) ----
        var g = new UiOptionGroup(new[] { "A", "B", "C" }, 0);
        ConsoleKeyInfo K(ConsoleKey k) => new ConsoleKeyInfo('\0', k, false, false, false);
        Check(g.HandleKey(K(ConsoleKey.DownArrow)) && g.Selected == 1, "down -> 1");
        g.HandleKey(K(ConsoleKey.DownArrow));
        Check(g.HandleKey(K(ConsoleKey.DownArrow)) && g.Selected == 0, "down wraps 2 -> 0");
        Check(g.HandleKey(K(ConsoleKey.UpArrow)) && g.Selected == 2, "up wraps 0 -> 2");
        Check(g.HandleKey(K(ConsoleKey.RightArrow)) && g.Selected == 0, "right wraps 2 -> 0");
        Check(g.HandleKey(K(ConsoleKey.LeftArrow)) && g.Selected == 2, "left wraps 0 -> 2");
        Check(!g.HandleKey(K(ConsoleKey.Tab)), "group does not consume Tab");
        Check(UiOptionGroup.Wrap(-1, 3) == 2 && UiOptionGroup.Wrap(3, 3) == 0 && UiOptionGroup.Wrap(0, 0) == 0, "wrap helper");

        // A button consumes Enter (firing OnPressed) but not arrows.
        bool pressed = false;
        var b = new UiButton("Go") { OnPressed = () => pressed = true };
        Check(b.HandleKey(K(ConsoleKey.Enter)) && pressed, "button: Enter fires OnPressed");
        Check(!b.HandleKey(K(ConsoleKey.DownArrow)), "button does not consume arrows");

        // ---- HIT-TEST (Stage 2 mouse): a click point -> the widget under it, or -1 ----
        var s4 = NewScreen();          // [0]=title label, [1]=group(2 rows), [2]=Ok, [3]=Back
        s4.Layout(100, 40);
        var w4 = s4.Widgets;
        var grp = w4[1]; var okB = w4[2]; var backB = w4[3]; var lbl = w4[0];
        // a cell inside each widget hits it
        Check(UiScreen.HitTest(w4, okB.Bounds.X, okB.Bounds.Y) == 2, "hit: Ok button");
        Check(UiScreen.HitTest(w4, backB.Bounds.X + 1, backB.Bounds.Y) == 3, "hit: Back button");
        Check(UiScreen.HitTest(w4, grp.Bounds.X, grp.Bounds.Y) == 1, "hit: option group row 0");
        Check(UiScreen.HitTest(w4, grp.Bounds.X, grp.Bounds.Y + 1) == 1, "hit: option group row 1");
        // a label is NOT a click target
        Check(UiScreen.HitTest(w4, lbl.Bounds.X, lbl.Bounds.Y) == -1, "hit: label is not clickable");
        // empty space misses
        Check(UiScreen.HitTest(w4, 0, 0) == -1, "hit: empty space (header) misses");
        Check(UiScreen.HitTest(w4, okB.Bounds.X, okB.Bounds.Y + 5) == -1, "hit: below a button misses");
        // boundary cells: last cell inside hits, one past the right edge misses
        Check(UiScreen.HitTest(w4, okB.Bounds.X + okB.Bounds.W - 1, okB.Bounds.Y) == 2, "hit: right-edge cell inside");
        Check(UiScreen.HitTest(w4, okB.Bounds.X + okB.Bounds.W, okB.Bounds.Y) == -1, "hit: one past right edge misses");

        // HandleClick focuses + activates: click Back -> focus moves to it + its OnPressed fires
        bool backPressed = false;
        ((UiButton)backB).OnPressed = () => backPressed = true;
        s4.HandleClick(backB.Bounds.X + 1, backB.Bounds.Y);
        Check(s4.FocusIndex == 3 && backPressed, "click Back: focus + activate");
        // click option row 1 selects option 1 (and focuses the group)
        s4.HandleClick(grp.Bounds.X, grp.Bounds.Y + 1);
        Check(s4.FocusIndex == 1 && ((UiOptionGroup)grp).Selected == 1, "click option row 1: select + focus");
        // click empty space: no change to focus
        int before = s4.FocusIndex;
        s4.HandleClick(0, 0);
        Check(s4.FocusIndex == before, "click empty: focus unchanged");

        // ---- MOUSE PARSE (pure): left down-transition is a click; up / move / right / held are not ----
        const uint LEFT = 0x0001, RIGHT = 0x0002, MOVED = 0x0001, WHEEL = 0x0004;
        Check(UiWinConsole.IsLeftClick(LEFT, 0, false, out bool d1) && d1, "parse: left down = click");
        Check(!UiWinConsole.IsLeftClick(0, 0, true, out bool d2) && !d2, "parse: left up = no click");
        Check(!UiWinConsole.IsLeftClick(LEFT, 0, true, out _), "parse: held left = no click (no transition)");
        Check(!UiWinConsole.IsLeftClick(LEFT, MOVED, false, out _), "parse: move (even with left) = no click");
        Check(!UiWinConsole.IsLeftClick(LEFT, WHEEL, false, out _), "parse: wheel = no click");
        Check(!UiWinConsole.IsLeftClick(RIGHT, 0, false, out _), "parse: right button = no click");

        // ---- STAGE 3 — UiCheckBox: Space/Enter/click toggles ----
        int changes = 0;
        var cb = new UiCheckBox("Shadows", false) { OnChanged = _ => changes++ };
        Check(!cb.Checked, "checkbox: starts unchecked");
        Check(cb.HandleKey(K(ConsoleKey.Spacebar)) && cb.Checked, "checkbox: Space checks");
        Check(cb.HandleKey(K(ConsoleKey.Enter)) && !cb.Checked, "checkbox: Enter unchecks");
        cb.OnClick(0, 0);
        Check(cb.Checked && changes == 3, "checkbox: click toggles + OnChanged fired each time");
        Check(!cb.HandleKey(K(ConsoleKey.DownArrow)), "checkbox: does not consume arrows");

        // ---- STAGE 3 — UiTextInput: typing at the caret, Backspace, caret tracking ----
        var ti = new UiTextInput("ab", fieldWidth: 10);
        Check(ti.Caret == 2, "textinput: caret starts at end");
        // A synthetic "typed character": KeyChar set, Key = 0 (not any handled special key).
        void Type(UiTextInput f, char c) => f.HandleKey(new ConsoleKeyInfo(c, (ConsoleKey)0, false, false, false));
        Type(ti, 'c');
        Check(ti.Text == "abc" && ti.Caret == 3, "textinput: typing appends at caret");
        ti.HandleKey(K(ConsoleKey.LeftArrow)); ti.HandleKey(K(ConsoleKey.LeftArrow));   // caret -> 1 (between a|bc)
        Type(ti, 'X');
        Check(ti.Text == "aXbc" && ti.Caret == 2, "textinput: insert at mid-caret");
        ti.HandleKey(K(ConsoleKey.Backspace));
        Check(ti.Text == "abc" && ti.Caret == 1, "textinput: Backspace removes before caret");
        ti.HandleKey(K(ConsoleKey.Delete));
        Check(ti.Text == "ac" && ti.Caret == 1, "textinput: Delete removes at caret");
        ti.HandleKey(K(ConsoleKey.Home)); Check(ti.Caret == 0, "textinput: Home");
        ti.HandleKey(K(ConsoleKey.End)); Check(ti.Caret == 2, "textinput: End");

        // NUMERIC mode: rejects letters, allows digits + one '.' + a leading '-'; parse helper + fallback
        var num = new UiTextInput("", fieldWidth: 8, numeric: true);
        Type(num, '-'); Type(num, '1'); Type(num, '2'); Type(num, 'a'); Type(num, '.'); Type(num, '5'); Type(num, '.');
        Check(num.Text == "-12.5", "numeric: accepts -digits.one-dot, rejects letter + 2nd dot");
        Check(Math.Abs(num.AsFloat(0f) - (-12.5f)) < 1e-6f, "numeric: AsFloat parses");
        Check(new UiTextInput("", numeric: true).AsFloat(9.8f) == 9.8f, "numeric: empty -> fallback");
        Check(UiTextInput.ParseInt("42", 0) == 42 && UiTextInput.ParseInt("x", 7) == 7, "numeric: ParseInt + fallback");
        Check(UiTextInput.NumericAccepts("1.2", 3, '.') == false, "numeric: second dot rejected");
        Check(UiTextInput.NumericAccepts("12", 0, '-') == true && UiTextInput.NumericAccepts("12", 1, '-') == false, "numeric: '-' only leading");
        Check(UiTextInput.ScrollOffset(9, 10) == 0 && UiTextInput.ScrollOffset(15, 10) == 6, "textinput: caret scroll window");

        // ---- STAGE 3 — UiListView: Up/Down clamp, visible window, click select ----
        var lv = new UiListView(new[] { "a", "b", "c", "d", "e", "f" }, visibleRows: 3, selected: 0);
        Check(lv.Selected == 0, "list: starts at 0");
        lv.HandleKey(K(ConsoleKey.UpArrow)); Check(lv.Selected == 0, "list: Up clamps at top");
        lv.HandleKey(K(ConsoleKey.DownArrow)); lv.HandleKey(K(ConsoleKey.DownArrow));
        Check(lv.Selected == 2, "list: Down moves");
        for (int i = 0; i < 10; i++) lv.HandleKey(K(ConsoleKey.DownArrow));
        Check(lv.Selected == 5, "list: Down clamps at bottom");
        // visible window keeps selection in view + clamps at ends
        Check(UiListView.Window(6, 3, 0) == 0, "list window: top");
        Check(UiListView.Window(6, 3, 5) == 3, "list window: bottom clamps to count-rows");
        Check(UiListView.Window(6, 3, 2) == 0 && UiListView.Window(6, 3, 3) == 1, "list window: scrolls to keep selection visible");
        Check(UiListView.Window(2, 5, 1) == 0, "list window: shorter-than-view stays 0");
        int activated = -1;
        lv.OnActivate = i => activated = i;
        lv.HandleKey(K(ConsoleKey.Enter)); Check(activated == 5, "list: Enter activates selected");
        // click a visible row selects it (window at selected=5 -> start=3, row0=idx3)
        lv.Bounds = new UiRect(0, 10, 12, 3);
        lv.OnClick(0, 10);  // top visible row -> idx 3
        Check(lv.Selected == 3, "list: click selects the clicked visible row");

        // ---- STAGE 3 — HitTest returns checkbox / text / list rows as hittable; labels/empty still miss ----
        var s5 = new UiScreen("h");
        s5.Add(new UiLabel("t", true), new UiCheckBox("cb"), new UiTextInput("x", 12), new UiListView(new[] { "1", "2", "3" }, 3));
        s5.Layout(100, 40);
        var w5 = s5.Widgets;
        Check(UiScreen.HitTest(w5, w5[1].Bounds.X, w5[1].Bounds.Y) == 1, "hit: checkbox clickable");
        Check(UiScreen.HitTest(w5, w5[2].Bounds.X, w5[2].Bounds.Y) == 2, "hit: text field clickable");
        Check(UiScreen.HitTest(w5, w5[3].Bounds.X, w5[3].Bounds.Y + 2) == 3, "hit: list bottom row clickable");
        Check(UiScreen.HitTest(w5, w5[0].Bounds.X, w5[0].Bounds.Y) == -1, "hit: title label still not clickable");

        // ---- STAGE 4 — FLOW state machine (mirrors RunApp; N(step,outcome,online,isServer,create)) ----
        WStep N(WStep s, WOutcome o, bool online, bool isServer, bool create) => NewWizard.Next(s, o, online, isServer, create);
        // Local branch
        Check(N(WStep.Mode, WOutcome.Ok, false, false, true) == WStep.World, "flow: local Mode->World");
        Check(N(WStep.World, WOutcome.Ok, false, false, true) == WStep.Create, "flow: World(create)->Create");
        Check(N(WStep.World, WOutcome.Ok, false, false, false) == WStep.Load, "flow: World(load)->Load");
        Check(N(WStep.Create, WOutcome.Ok, false, false, true) == WStep.Launch, "flow: local Create->Launch");
        Check(N(WStep.Load, WOutcome.Ok, false, false, false) == WStep.Launch, "flow: local Load->Launch");
        // Online / server / client
        Check(N(WStep.Mode, WOutcome.Ok, true, false, true) == WStep.Role, "flow: online Mode->Role");
        Check(N(WStep.Role, WOutcome.Ok, true, true, true) == WStep.World, "flow: server Role->World");
        Check(N(WStep.Role, WOutcome.Ok, true, false, true) == WStep.Network, "flow: client Role->Network");
        Check(N(WStep.Create, WOutcome.Ok, true, true, true) == WStep.Network, "flow: online Create->Network");
        Check(N(WStep.Network, WOutcome.Ok, true, true, true) == WStep.Launch, "flow: Network->Launch");
        // Quit + Back transitions
        Check(N(WStep.Mode, WOutcome.Quit, false, false, true) == WStep.Cancelled, "flow: Mode Quit->Cancelled");
        Check(N(WStep.Role, WOutcome.Back, true, false, true) == WStep.Mode, "flow: Role Back->Mode");
        Check(N(WStep.World, WOutcome.Back, false, false, true) == WStep.Mode, "flow: local World Back->Mode");
        Check(N(WStep.World, WOutcome.Back, true, true, true) == WStep.Role, "flow: online World Back->Role");
        Check(N(WStep.Create, WOutcome.Back, false, false, true) == WStep.World, "flow: Create Back->World");
        Check(N(WStep.Load, WOutcome.Back, false, false, false) == WStep.World, "flow: Load Back->World");
        Check(N(WStep.Network, WOutcome.Back, true, true, true) == WStep.World, "flow: server Network Back->World");
        Check(N(WStep.Network, WOutcome.Back, true, false, true) == WStep.Role, "flow: client Network Back->Role");

        // ---- STAGE 4 — CONFIG assembly parity with ShowCreateDialog (defaults + mappings) ----
        var (v1, c1) = NewWizard.BuildCreateConfig("myworld", true, true, false, false, true, 0, 0, "10", "20", "20", false, true, "5", "0");
        Check(v1 && c1 != null, "config: valid with defaults");
        Check(c1!.Name == "myworld", "config: name");
        Check(c1.Graphics.Shadows && c1.Graphics.Bvh && !c1.Graphics.ExtraLight && !c1.Graphics.DisableCameraLight, "config: graphics default toggles");
        Check(c1.Graphics.Renderer == "cpu", "config: renderer idx0 -> cpu");
        Check(c1.Platform.Enabled && c1.Platform.Shape == "square" && c1.Platform.Size == 10f && c1.Platform.Width == 20f && c1.Platform.Depth == 20f && c1.Platform.Color == "Yellow", "config: platform defaults");
        Check(!c1.Physics!.GravityEnabled && c1.Physics.GravityStrength == 5f && c1.Physics.CollisionEnabled && c1.Physics.Restitution == 0f, "config: physics defaults");
        Check(c1.Objects.Count == 0, "config: no objects");

        var (v2, c2) = NewWizard.BuildCreateConfig("my crate!", false, false, true, true, false, 1, 2, "15", "30", "40", true, false, "9", "0.5");
        Check(v2 && c2!.Name == "mycrate", "config: name sanitized (drops space + '!')");
        Check(c2!.Graphics.Renderer == "gpu" && c2.Graphics.ExtraLight && c2.Graphics.DisableCameraLight && !c2.Graphics.Shadows && !c2.Graphics.Bvh, "config: gpu + custom graphics");
        Check(c2.Platform.Shape == "circle" && c2.Platform.Size == 15f && !c2.Platform.Enabled, "config: circle + size + no platform");
        Check(c2.Physics!.GravityEnabled && c2.Physics.GravityStrength == 9f && !c2.Physics.CollisionEnabled && Math.Abs(c2.Physics.Restitution - 0.5f) < 1e-6f, "config: custom physics");

        var (_, cR) = NewWizard.BuildCreateConfig("w", true, true, false, false, true, 0, 1, "10", "20", "20", false, true, "5", "0");
        Check(cR!.Platform.Shape == "rectangle", "config: shape idx1 -> rectangle");
        var (vBad, cBad) = NewWizard.BuildCreateConfig("!!!", true, true, false, false, true, 0, 0, "10", "20", "20", false, true, "5", "0");
        Check(!vBad && cBad == null, "config: empty-after-sanitize name -> invalid");
        var (_, cClamp) = NewWizard.BuildCreateConfig("w", true, true, false, false, true, 0, 0, "10", "20", "20", false, true, "5", "5");
        Check(cClamp!.Physics!.Restitution == 1f, "config: restitution clamped to 1");
        var (_, cFb) = NewWizard.BuildCreateConfig("w", true, true, false, false, true, 0, 0, "abc", "", "-3", false, true, "5", "0");
        Check(cFb!.Platform.Size == 10f && cFb.Platform.Width == 20f && cFb.Platform.Depth == 20f, "config: invalid/empty/negative sizes -> fallbacks");
        var (_, cGr) = NewWizard.BuildCreateConfig("w", true, true, false, false, true, 0, 0, "10", "20", "20", true, true, "notnum", "0");
        Check(Math.Abs(cGr!.Physics!.GravityStrength - 9.8f) < 1e-6f, "config: invalid gravity -> 9.8 fallback");

        // ---- STAGE 4 — NETWORK validation parity with ShowNetworkDialog ----
        Check(NewWizard.ValidatePort("7777", out int pp) && pp == 7777, "net: valid port");
        Check(NewWizard.ValidatePort(" 22 ", out int pp2) && pp2 == 22, "net: port trims whitespace");
        Check(!NewWizard.ValidatePort("0", out _), "net: port 0 invalid");
        Check(!NewWizard.ValidatePort("70000", out _), "net: port > 65535 invalid");
        Check(!NewWizard.ValidatePort("abc", out _), "net: non-numeric port invalid");
        Check(NewWizard.ValidateIp("127.0.0.1"), "net: valid IPv4");
        Check(NewWizard.ValidateIp("::1"), "net: valid IPv6");
        Check(!NewWizard.ValidateIp("999.1.1.1"), "net: out-of-range IP invalid");
        Check(!NewWizard.ValidateIp(""), "net: empty IP invalid");

        // ---- STAGE 6 — the wizard remembers selections across Back (WizardState + seed/write-back) ----
        // Derived flow properties map from the persisted indices.
        var wst = new NewWizard.WizardState();
        wst.ModeSel = 1; Check(wst.Online, "state: ModeSel 1 -> Online");
        wst.RoleSel = 0; Check(wst.IsServer, "state: RoleSel 0 -> IsServer");
        wst.WorldSel = 1; Check(!wst.Create, "state: WorldSel 1 -> Load (not Create)");
        wst.PortText = "1234"; Check(wst.Port == 1234, "state: PortText -> Port");
        wst.PortText = "bad"; Check(wst.Port == 7777, "state: bad PortText -> default Port");

        // OptionGroup round-trip (the reported World-menu bug): seed from state, user picks Load, write back,
        // re-enter -> still Load. Uses the exact ctor + field the screen uses.
        var sOg = new NewWizard.WizardState();                       // WorldSel defaults to 0 (Create)
        var og1 = new UiOptionGroup(new[] { "Create new world", "Load world" }, sOg.WorldSel);
        Check(og1.Selected == 0, "persist(optiongroup): starts at Create");
        og1.HandleKey(K(ConsoleKey.DownArrow));                      // user picks "Load world"
        sOg.WorldSel = og1.Selected;                                 // screen writes back on exit
        var og2 = new UiOptionGroup(new[] { "Create new world", "Load world" }, sOg.WorldSel);   // re-enter
        Check(og2.Selected == 1, "persist(optiongroup): 'Load' survives Back/return");

        // TextInput round-trip (a typed world name survives Back).
        var sTx = new NewWizard.WizardState();                       // Name defaults to "myworld"
        var tx1 = new UiTextInput(sTx.Name, fieldWidth: 30);
        Type(tx1, 'X'); Type(tx1, 'Y');                             // append -> "myworldXY"
        sTx.Name = tx1.Text;
        var tx2 = new UiTextInput(sTx.Name, fieldWidth: 30);
        Check(tx2.Text == "myworldXY", "persist(textinput): typed name survives Back/return");

        // CheckBox round-trip.
        var sCb = new NewWizard.WizardState();                       // Shadows defaults to true
        var cbP = new UiCheckBox("Shadows", sCb.Shadows);
        cbP.Toggle();                                                // user unchecks
        sCb.Shadows = cbP.Checked;
        var cbP2 = new UiCheckBox("Shadows", sCb.Shadows);
        Check(!cbP2.Checked, "persist(checkbox): unchecked state survives Back/return");

        Console.WriteLine(ok ? "uitest: PASS" : "uitest: FAIL");
    }
}
