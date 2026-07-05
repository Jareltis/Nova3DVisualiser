using System;

namespace SampleGame.WizardUi;

// The `uidemo` entry (dispatched in Program.Main before RunApp, like the self-tests): runnable screens on the
// engine-renderer UI toolkit, to eyeball keyboard + mouse nav, the visual style, and live resize/font-zoom
// reflow. Two screens: a "Session mode" screen, then (on Ok) a WIDGET GALLERY (checkbox + text + numeric +
// list). Returns to the shell on Ok / Back / Esc; no game launch.
public static class UiDemo
{
    public static void Run()
    {
        var theme = UiTheme.Default();
        var runner = new UiRunner(theme);

        string result = ShowModeScreen(runner, theme);   // returns "gallery" to advance, else a final string
        if (result == "gallery") result = ShowGalleryScreen(runner, theme);

        // Leave a clean console for the shell.
        try { Console.ResetColor(); Console.CursorVisible = true; Console.Clear(); } catch { }
        Console.WriteLine($"uidemo result: {result}");
    }

    // Screen 1 — the stage-1 Session-mode screen. Ok advances to the gallery; Back/Esc exit.
    private static string ShowModeScreen(UiRunner runner, UiTheme theme)
    {
        var screen = new UiScreen("Click or Tab/arrows to navigate  |  Enter confirm  |  Esc back");
        var title = new UiLabel("Session mode", title: true);
        var prompt = new UiLabel("Run a local solo session or go online?");
        var modes = new UiOptionGroup(new[] { "Local / Offline", "Online" }, 0);
        var next = new UiButton("Ok");
        var back = new UiButton("Back");

        string result = "Cancel (Esc)";
        next.OnPressed = () => { result = "gallery"; runner.Stop(); };
        back.OnPressed = () => { result = $"Back  (mode = {modes.Labels[modes.Selected]})"; runner.Stop(); };
        screen.OnCancel = () => { result = "Cancel (Esc)"; runner.Stop(); };

        screen.Add(title, prompt, modes, next, back);
        runner.Run(screen);
        return result;
    }

    // Screen 2 — the stage-3 widget gallery: a checkbox, a text field, a numeric field, and a small list,
    // all keyboard + mouse driven. CompactHeader keeps it fitting under the branded header on normal terminals.
    private static string ShowGalleryScreen(UiRunner runner, UiTheme theme)
    {
        var screen = new UiScreen("Space toggle  |  type in fields  |  arrows in list  |  Tab/click move  |  Esc back")
        {
            CompactHeader = true,
        };

        var title = new UiLabel("Widget gallery", title: true);
        var shadows = new UiCheckBox("Shadows", true);
        var nameLabel = new UiLabel("Name (text):");
        var name = new UiTextInput("world1", fieldWidth: 24);
        var sizeLabel = new UiLabel("Size (numeric):");
        var size = new UiTextInput("10", fieldWidth: 10, numeric: true);
        var worldsLabel = new UiLabel("Worlds:");
        var worlds = new UiListView(new[] { "default", "arena", "test-01", "sky", "void", "pyramid" }, visibleRows: 4);
        var ok = new UiButton("Ok");
        var back = new UiButton("Back");

        string result = "gallery: Esc";
        ok.OnPressed = () =>
        {
            result = $"gallery Ok  (shadows={shadows.Checked}, name='{name.Text}', size={size.AsFloat(0f)}, world='{worlds.Items[worlds.Selected]}')";
            runner.Stop();
        };
        back.OnPressed = () => { result = "gallery: Back"; runner.Stop(); };
        screen.OnCancel = () => { result = "gallery: Esc"; runner.Stop(); };

        screen.Add(title, shadows, nameLabel, name, sizeLabel, size, worldsLabel, worlds, ok, back);
        runner.Run(screen);
        return result;
    }
}
