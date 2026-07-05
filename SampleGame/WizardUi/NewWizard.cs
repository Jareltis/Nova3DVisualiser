using System;
using System.Collections.Generic;
using System.Net;
using Nova3DVisualiser.Network;
using SampleGame.Worlds;

namespace SampleGame.WizardUi;

// The FULL setup wizard, built on the engine's own console UI toolkit (SampleGame/WizardUi) — no external UI
// library. Program.RunApp calls RunFlow() before the render loop; it walks Mode -> Role -> World ->
// Create/Load -> Network (with Back) and RETURNS the chosen session/world. The flow state machine (Next), the
// config assembly (BuildCreateConfig) and the validation (ValidatePort/ValidateIp) are pure/testable; the
// screens are the interactive layer. Each screen's widget state is persisted in a WizardState, so navigating
// Back and returning restores the user's previous choices instead of resetting to defaults.
public static class NewWizard
{
    public enum WStep { Mode, Role, World, Create, Load, Network, Launch, Cancelled }
    public enum WOutcome { Ok, Back, Quit }

    // ---- pure flow state machine (mirrors RunApp's switch; testable) ----
    public static WStep Next(WStep step, WOutcome outcome, bool online, bool isServer, bool create) => step switch
    {
        WStep.Mode    => outcome == WOutcome.Quit ? WStep.Cancelled : (online ? WStep.Role : WStep.World),
        WStep.Role    => outcome == WOutcome.Back ? WStep.Mode : (isServer ? WStep.World : WStep.Network),
        WStep.World   => outcome == WOutcome.Back ? (online ? WStep.Role : WStep.Mode) : (create ? WStep.Create : WStep.Load),
        WStep.Create  => outcome == WOutcome.Back ? WStep.World : (online ? WStep.Network : WStep.Launch),
        WStep.Load    => outcome == WOutcome.Back ? WStep.World : (online ? WStep.Network : WStep.Launch),
        WStep.Network => outcome == WOutcome.Back ? (isServer ? WStep.World : WStep.Role) : WStep.Launch,
        _             => step,
    };

    // ---- pure config assembly (identical logic/helpers to ShowCreateDialog → byte-for-byte parity) ----
    public static (bool valid, WorldConfig? config) BuildCreateConfig(
        string? nameText, bool shadows, bool bvh, bool extra, bool disableOwn, bool platform,
        int rendererIdx, int shapeIdx, string? sizeText, string? widthText, string? depthText,
        bool gravity, bool collision, string? gravityText, string? restitutionText)
    {
        string safe = Program.SanitizeWorldName(nameText ?? "");
        if (string.IsNullOrEmpty(safe)) return (false, null);

        var w = new WorldConfig
        {
            Name = safe,
            Graphics = new GraphicsConfig
            {
                Shadows = shadows,
                Bvh = bvh,
                ExtraLight = extra,
                DisableCameraLight = disableOwn,
                Renderer = rendererIdx == 1 ? "gpu" : "cpu",
            },
            Platform = new PlatformConfig
            {
                Enabled = platform,
                Shape = shapeIdx switch { 1 => "rectangle", 2 => "circle", _ => "square" },
                Size = Program.ParseFloatOr(sizeText, 10f),
                Width = Program.ParseFloatOr(widthText, 20f),
                Depth = Program.ParseFloatOr(depthText, 20f),
                Color = "Yellow",
            },
            Objects = new List<WorldObject>(),
            Physics = new PhysicsConfig
            {
                GravityEnabled = gravity,
                GravityStrength = float.TryParse(gravityText, out var g) ? g : 9.8f,
                CollisionEnabled = collision,
                Restitution = Math.Clamp(Program.ParseFloatOr(restitutionText, 0f), 0f, 1f),
            },
        };
        return (true, w);
    }

    // ---- pure network validation (same rules as ShowNetworkDialog) ----
    public static bool ValidatePort(string? text, out int port)
        => int.TryParse(text?.Trim(), out port) && port >= 1 && port <= 65535;
    public static bool ValidateIp(string? text)
        => IPAddress.TryParse((text ?? "").Trim(), out _);

    // ================= interactive layer =================
    // The chosen session + world produced by the wizard flow — fed to the game launch by RunApp.
    public sealed class WizardResult
    {
        public bool Quit;
        public bool Online;
        public bool IsServer;
        public string Ip = "127.0.0.1";
        public int Port = 7777;
        public WorldConfig? World;
    }

    // Persisted widget state for EVERY screen, so navigating Back and returning restores the user's previous
    // choices (an OptionGroup index, a TextInput's text, CheckBox states, the Load-list selection) instead of
    // resetting to defaults. Each screen SEEDS its widgets from here and WRITES them back on exit (Ok OR Back).
    // The flow booleans and the final result are DERIVED from it, so the resulting WorldConfig/session is
    // byte-identical to before (config parity holds).
    public sealed class WizardState
    {
        public int ModeSel;                 // 0 = Local, 1 = Online
        public int RoleSel = 1;             // 0 = Server, 1 = Client (default Client, as before)
        public int WorldSel;                // 0 = Create, 1 = Load
        public int LoadSel;                 // Load-list selection

        // Create form
        public string Name = "myworld";
        public bool Shadows = true, Bvh = true, Extra = false, DisableOwn = false, Platform = true;
        public int RendererSel;             // 0 = CPU, 1 = GPU
        public int ShapeSel;                // 0 = Square, 1 = Rectangle, 2 = Circle
        public string SizeText = "10", WidthText = "20", DepthText = "20";
        public bool Gravity = false, Collision = true;
        public string GravityText = "9.8", RestitutionText = "0";

        // Network
        public string Ip = "127.0.0.1";
        public string PortText = "7777";

        // derived for the flow state machine + the result
        public bool Online => ModeSel == 1;
        public bool IsServer => RoleSel == 0;
        public bool Create => WorldSel == 0;
        public int Port => int.TryParse(PortText?.Trim(), out var p) ? p : 7777;
    }

    // Runs the full toolkit wizard (Mode → Role → World → Create/Load → Network) and RETURNS the chosen
    // session/config. No printing, no game launch — RunApp calls this and drives the launch with the result.
    // The client-with-no-world (remote) placeholder is left to the caller (RunApp applies it, as it always
    // has). One WizardState persists every screen's widget state across Back/forward navigation.
    public static WizardResult RunFlow()
    {
        WorldManager.EnsureDefault();
        var theme = UiTheme.Default();
        var runner = new UiRunner(theme);
        var st = new WizardState();

        WorldConfig? chosenWorld = null;
        var step = WStep.Mode;
        while (step != WStep.Launch && step != WStep.Cancelled)
        {
            WOutcome outcome;
            switch (step)
            {
                case WStep.Mode:    outcome = RunModeScreen(runner, st); break;
                case WStep.Role:    outcome = RunRoleScreen(runner, st); break;
                case WStep.World:   outcome = RunWorldMenuScreen(runner, st); break;
                case WStep.Create:  outcome = RunCreateScreen(runner, st, out var created); if (outcome == WOutcome.Ok) chosenWorld = created; break;
                case WStep.Load:    outcome = RunLoadScreen(runner, st, out var loaded);   if (outcome == WOutcome.Ok) chosenWorld = loaded;  break;
                case WStep.Network: outcome = RunNetworkScreen(runner, st); break;
                default:            outcome = WOutcome.Back; break;
            }
            step = Next(step, outcome, st.Online, st.IsServer, st.Create);
        }

        return new WizardResult
        {
            Quit = step == WStep.Cancelled,
            Online = st.Online, IsServer = st.IsServer, Ip = st.Ip, Port = st.Port, World = chosenWorld,
        };
    }

    // ================= the six screens =================

    private static UiButton Btn(string text) => new UiButton(text);

    private static WOutcome RunModeScreen(UiRunner runner, WizardState st)
    {
        var screen = new UiScreen("Click or Tab/arrows to navigate  |  Enter confirm  |  Esc quit");
        var modes = new UiOptionGroup(new[] { "Local / Offline", "Online" }, st.ModeSel);
        var ok = Btn("Ok"); var quit = Btn("Quit");
        WOutcome outcome = WOutcome.Quit;
        ok.OnPressed   = () => { outcome = WOutcome.Ok;   runner.Stop(); };
        quit.OnPressed = () => { outcome = WOutcome.Quit; runner.Stop(); };
        screen.OnCancel = () => { outcome = WOutcome.Quit; runner.Stop(); };
        screen.Add(new UiLabel("Session mode", title: true), new UiLabel("Run a local solo session or go online?"), modes, ok, quit);
        runner.Run(screen);
        st.ModeSel = modes.Selected;   // persist across navigation
        return outcome;
    }

    private static WOutcome RunRoleScreen(UiRunner runner, WizardState st)
    {
        var screen = new UiScreen("Click or Tab/arrows to navigate  |  Enter confirm  |  Esc back");
        var roles = new UiOptionGroup(new[] { "Server (host)", "Client (join)" }, st.RoleSel);
        var ok = Btn("Ok"); var back = Btn("Back");
        WOutcome outcome = WOutcome.Back;
        ok.OnPressed   = () => { outcome = WOutcome.Ok;   runner.Stop(); };
        back.OnPressed = () => { outcome = WOutcome.Back; runner.Stop(); };
        screen.OnCancel = () => { outcome = WOutcome.Back; runner.Stop(); };
        screen.Add(new UiLabel("Network role", title: true), new UiLabel("Host a server or join one?"), roles, ok, back);
        runner.Run(screen);
        st.RoleSel = roles.Selected;   // persist across navigation
        return outcome;
    }

    private static WOutcome RunWorldMenuScreen(UiRunner runner, WizardState st)
    {
        var screen = new UiScreen("Click or Tab/arrows to navigate  |  Enter confirm  |  Esc back");
        var menu = new UiOptionGroup(new[] { "Create new world", "Load world" }, st.WorldSel);
        var ok = Btn("Ok"); var back = Btn("Back");
        WOutcome outcome = WOutcome.Back;
        ok.OnPressed   = () => { outcome = WOutcome.Ok;   runner.Stop(); };
        back.OnPressed = () => { outcome = WOutcome.Back; runner.Stop(); };
        screen.OnCancel = () => { outcome = WOutcome.Back; runner.Stop(); };
        screen.Add(new UiLabel("World", title: true), new UiLabel("Create a new world or load a saved one?"), menu, ok, back);
        runner.Run(screen);
        st.WorldSel = menu.Selected;   // remember Create/Load across Back (the reported bug)
        return outcome;
    }

    private static WOutcome RunCreateScreen(UiRunner runner, WizardState st, out WorldConfig? world)
    {
        world = null;
        WorldConfig? built = null;
        var screen = new UiScreen("Tab/click move  |  Space toggles  |  type in fields  |  Enter=Create  |  Esc back")
        {
            CompactHeader = true, FormW = 52, FormH = 18,
        };

        var name = new UiTextInput(st.Name, fieldWidth: 30);
        var cbShadows = new UiCheckBox("Shadows", st.Shadows);
        var cbBvh = new UiCheckBox("BVH acceleration", st.Bvh);
        var cbExtra = new UiCheckBox("Extra fixed light", st.Extra);
        var cbDisableOwn = new UiCheckBox("Disable camera light", st.DisableOwn);
        var rgRenderer = new UiOptionGroup(new[] { "CPU", "GPU (NVIDIA)" }, st.RendererSel, horizontal: true);
        var cbPlatform = new UiCheckBox("Include platform", st.Platform);
        var rgShape = new UiOptionGroup(new[] { "Square", "Rectangle", "Circle" }, st.ShapeSel, horizontal: true);
        var sizeField = new UiTextInput(st.SizeText, fieldWidth: 8, numeric: true);
        var widthField = new UiTextInput(st.WidthText, fieldWidth: 8, numeric: true);
        var depthField = new UiTextInput(st.DepthText, fieldWidth: 8, numeric: true);
        var cbGravity = new UiCheckBox("Gravity", st.Gravity);
        var cbCollision = new UiCheckBox("Collision", st.Collision);
        var gravityField = new UiTextInput(st.GravityText, fieldWidth: 8, numeric: true);
        var restitutionField = new UiTextInput(st.RestitutionText, fieldWidth: 6, numeric: true);
        var create = Btn("Create");
        var back = Btn("Back");

        WOutcome outcome = WOutcome.Back;
        back.OnPressed = () => { outcome = WOutcome.Back; runner.Stop(); };
        create.OnPressed = () =>
        {
            var (valid, cfg) = BuildCreateConfig(
                name.Text, cbShadows.Checked, cbBvh.Checked, cbExtra.Checked, cbDisableOwn.Checked, cbPlatform.Checked,
                rgRenderer.Selected, rgShape.Selected, sizeField.Text, widthField.Text, depthField.Text,
                cbGravity.Checked, cbCollision.Checked, gravityField.Text, restitutionField.Text);
            if (!valid || cfg == null) { screen.ErrorText = "Enter a non-empty world name (letters, digits, - or _)."; return; }
            WorldManager.Save(cfg);
            built = cfg; outcome = WOutcome.Ok; runner.Stop();
        };
        screen.OnCancel = () => { outcome = WOutcome.Back; runner.Stop(); };

        // A multi-column form matching the v2 Create dialog's field positions (col,row within the 52-wide box).
        screen.Add(
            new UiLabel("World name:").At(1, 0), name.At(13, 0),
            new UiLabel("Graphics (Space toggles):", title: true).At(1, 2),
            cbShadows.At(1, 3), cbBvh.At(1, 4), cbExtra.At(1, 5), cbDisableOwn.At(1, 6),
            new UiLabel("Renderer:").At(1, 7), rgRenderer.At(11, 7),
            cbPlatform.At(1, 8),
            new UiLabel("Platform shape:", title: true).At(1, 9), rgShape.At(1, 10),
            new UiLabel("Size (square/circle):").At(1, 11), sizeField.At(22, 11),
            new UiLabel("Rect W x D:").At(1, 12), widthField.At(22, 12), depthField.At(33, 12),
            new UiLabel("Physics (Space toggles):", title: true).At(1, 13), cbGravity.At(1, 14), cbCollision.At(20, 14),
            new UiLabel("Gravity strength:").At(1, 15), gravityField.At(22, 15),
            new UiLabel("Bounce:").At(31, 15), restitutionField.At(39, 15),
            create.At(14, 17), back.At(28, 17));

        runner.Run(screen);

        // Persist every field so the whole form survives a Back and return.
        st.Name = name.Text;
        st.Shadows = cbShadows.Checked; st.Bvh = cbBvh.Checked; st.Extra = cbExtra.Checked;
        st.DisableOwn = cbDisableOwn.Checked; st.Platform = cbPlatform.Checked;
        st.RendererSel = rgRenderer.Selected; st.ShapeSel = rgShape.Selected;
        st.SizeText = sizeField.Text; st.WidthText = widthField.Text; st.DepthText = depthField.Text;
        st.Gravity = cbGravity.Checked; st.Collision = cbCollision.Checked;
        st.GravityText = gravityField.Text; st.RestitutionText = restitutionField.Text;

        world = built;
        return outcome;
    }

    private static WOutcome RunLoadScreen(UiRunner runner, WizardState st, out WorldConfig? world)
    {
        world = null;
        WorldConfig? loaded = null;
        var names = WorldManager.ListWorlds();
        var screen = new UiScreen("Up/Down or click to pick  |  Enter/Load  |  Esc back");

        var items = names.Count > 0 ? names : new List<string> { "(no saved worlds)" };
        int initSel = names.Count > 0 ? Math.Clamp(st.LoadSel, 0, names.Count - 1) : 0;
        var list = new UiListView(items, visibleRows: 6, selected: initSel);
        var load = Btn("Load"); var back = Btn("Back");
        WOutcome outcome = WOutcome.Back;
        back.OnPressed = () => { outcome = WOutcome.Back; runner.Stop(); };
        void DoLoad()
        {
            if (names.Count == 0) { outcome = WOutcome.Back; runner.Stop(); return; }
            string chosen = names[Math.Clamp(list.Selected, 0, names.Count - 1)];
            var w = WorldManager.Load(chosen);
            if (w == null) { screen.ErrorText = $"Could not load world '{chosen}' (see log)."; return; }
            loaded = w; outcome = WOutcome.Ok; runner.Stop();
        }
        load.OnPressed = DoLoad;
        list.OnActivate = _ => DoLoad();          // Enter on the list = Load
        screen.OnCancel = () => { outcome = WOutcome.Back; runner.Stop(); };

        screen.Add(new UiLabel("Load world", title: true), new UiLabel("Select a saved world:"), list, load, back);
        runner.Run(screen);
        if (names.Count > 0) st.LoadSel = list.Selected;   // remember which world was highlighted
        world = loaded;
        return outcome;
    }

    private static WOutcome RunNetworkScreen(UiRunner runner, WizardState st)
    {
        bool isServer = st.IsServer;
        var screen = new UiScreen("Type IP/port  |  Tab/click move  |  Enter=Ok  |  Esc back")
        {
            CompactHeader = true, FormW = 46, FormH = 8,
        };

        var portField = new UiTextInput(st.PortText, fieldWidth: 10, numeric: true);
        var ipField = new UiTextInput(st.Ip, fieldWidth: 24);   // text (an IP has multiple dots)
        var ok = Btn("Ok"); var back = Btn("Back");

        WOutcome outcome = WOutcome.Back;
        back.OnPressed = () => { outcome = WOutcome.Back; runner.Stop(); };
        ok.OnPressed = () =>
        {
            screen.ErrorText = null;
            if (!ValidatePort(portField.Text, out int _)) { screen.ErrorText = "Port must be a number between 1 and 65535."; return; }
            if (!isServer && !ValidateIp(ipField.Text)) { screen.ErrorText = "Enter a valid IP address."; return; }
            outcome = WOutcome.Ok; runner.Stop();
        };
        screen.OnCancel = () => { outcome = WOutcome.Back; runner.Stop(); };

        if (isServer)
        {
            string localIP = NetworkUtils.GetLocalIPAddress();
            screen.Add(
                new UiLabel($"Host server — your local IP: {localIP}", title: true).At(0, 0),
                new UiLabel("Give this IP to your friend!").At(0, 1),
                new UiLabel("Listen port:").At(0, 3), portField.At(14, 3),
                ok.At(12, 6), back.At(26, 6));
        }
        else
        {
            screen.Add(
                new UiLabel("Join server — connect to a host", title: true).At(0, 0),
                new UiLabel("Server IP:").At(0, 2), ipField.At(14, 2),
                new UiLabel("Port:").At(0, 4), portField.At(14, 4),
                ok.At(12, 6), back.At(26, 6));
        }

        runner.Run(screen);
        // Persist the typed IP/port so they survive a Back (the port is re-validated on the next Ok; the flow
        // only reaches Launch after a valid Ok, so the result always carries a validated value).
        st.PortText = portField.Text;
        if (!isServer) st.Ip = (ipField.Text ?? "").Trim();
        return outcome;
    }
}
