using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.Globalization;
using System.Text.Json;

namespace SampleGame.Scenes;

public partial class PriviewNetworkScene
{
    // Editor HUD: the overlay/properties-panel drawing plus the field label/value formatting helpers.

    // Shared overlay palette — both the EDIT MODE and PROPERTIES boxes use it so the HUD reads as
    // one consistent scheme: green section headers/titles, cyan for interactive keys/values, gray
    // for control hints, dark-gray for read-only meta, and yellow for the live selection status.
    private const ConsoleColor HudHeader = ConsoleColor.Green;
    private const ConsoleColor HudAccent = ConsoleColor.Cyan;
    private const ConsoleColor HudHint   = ConsoleColor.Gray;
    private const ConsoleColor HudMeta   = ConsoleColor.DarkGray;
    private const ConsoleColor HudStatus = ConsoleColor.Yellow;

    // Docked HUD colour roles (stage 3) — distinct, consistent tones that read on the dark 3D background.
    // The OVERLAY editor keeps the Hud* palette above unchanged; these style ONLY the docked panels + play HUD.
    private const ConsoleColor DockTitle   = ConsoleColor.Cyan;      // panel titles + the toolbar/status mode label
    private const ConsoleColor DockSection = ConsoleColor.Green;     // Inspector section headers (expanded)
    private const ConsoleColor DockLabel   = ConsoleColor.Gray;      // field LABELS (muted, still readable)
    private const ConsoleColor DockValue   = ConsoleColor.White;     // field VALUES (bright / primary data)
    private const ConsoleColor DockSel     = ConsoleColor.Yellow;    // the SELECTED object + the active cursor row
    private const ConsoleColor DockDim     = ConsoleColor.DarkGray;  // hints, separators, off-states
    private const ConsoleColor DockOn      = ConsoleColor.Green;     // a toolbar toggle that is ON
    private const ConsoleColor DockBorder  = ConsoleColor.DarkGray;  // panel frames (dimmer than the content)

    // Top row of the top-left EDIT MODE box — clear of the Detail/Waiting indicators (rows 10–11).
    private const int EditBoxTop = 12;
    // Top row of the F-key panel, stacked BELOW the EDIT MODE box (whose fixed content — spawn/selected/
    // spacer/two hint lines, + an optional transient save flash — spans at most rows 13–19, bottom border
    // at row 19), so row 21 always clears it with a one-row gap.
    private const int FKeyBoxTop = 21;

    private void DrawEditorOverlay()
    {
        if (!EditActive)   // (only reached if called outside an edit mode — the dispatch calls it in OVERLAY-EDIT)
        {
            int x = 2, y = 12;
            UI.AddText("[Tab] Edit mode", new Vector2Int(x, y++), HudMeta);
            UI.AddText($"[F7]/[F8] View: {ViewLabel()}", new Vector2Int(x, y), HudMeta);
            return;
        }

        // Edit-mode HUD drawn as a styled box (matching the PROPERTIES panel), anchored top-left. The
        // F-key controls live in their OWN vertical panel below (DrawFKeyPanel); this box keeps the spawn/
        // selection status and the non-F movement/spawn/select hints.
        var lines = new List<(string text, ConsoleColor color)>
        {
            ($"Spawn: {_spawnTypes[_spawnIndex]}", HudAccent),
        };

        string sel = _selected >= 0 && _selected < _editables.Count
            ? $"Selected: [{_selected + 1}/{_editables.Count}] {DescribeEntry(_editables[_selected])}"
            : $"Selected: (none) — {_editables.Count} object(s)";
        lines.Add((sel, HudStatus));

        lines.Add(("", HudHint));   // spacer between status and the control hints
        lines.Add(("[G] cycle type  [B] spawn  [ [ / ] ] select  [Enter] type value", HudHint));
        lines.Add(("Move: J/L=X  I/K=Z  U/O=Y", HudHint));

        if (_saveFlash > 0)
            lines.Add((_saveMsg, HudHeader));

        DrawOverlayBox("EDIT MODE", lines, anchorRight: false, top: EditBoxTop);
        DrawFKeyPanel();
    }

    // The F1–F8 controls as a dedicated VERTICAL, rectangular panel on the left — one key per line with its
    // live state, in numeric order. F1/F5/F7/F8 (fly-walk / save / views) show for everyone; the runtime
    // graphics toggles F2/F3/F4/F6 are authority-only, so a client sees only the keys it can use (matching
    // the previous overlay's visibility). Uses the same DrawOverlayBox styled panel + HUD palette.
    private void DrawFKeyPanel()
    {
        var keys = new List<(string text, ConsoleColor color)>();
        void Row(string key, string label) => keys.Add(($"{key}  {label}", HudHint));

        Row("F1", _flyMode ? "Fly" : "Walk");
        if (CanEdit)
        {
            Row("F2", $"Shadows  {OnOff(_world.Graphics.Shadows)}");
            Row("F3", $"BVH      {OnOff(_world.Graphics.Bvh)}");
            Row("F4", $"CamLight {OnOff(!_world.Graphics.DisableCameraLight)}");
        }
        Row("F5", "Save");
        if (CanEdit)
            Row("F6", $"Floor    {OnOff(_world.Platform.Enabled)}");
        Row("F7", $"View     {ViewLabel()}");
        Row("F8", "Camera (cycle view)");
        Row("F9", $"Split    {(_splitScreen ? "2-way" : "off")}");

        DrawOverlayBox("KEYS", keys, anchorRight: false, top: FKeyBoxTop);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string DescribeEntry(EditEntry e) =>
        e.Descriptor.Type == "mesh" ? $"mesh:{e.Descriptor.Mesh}" : e.Descriptor.Type;

    // Visual-Studio-style properties panel for the selected object — a titled, bordered box drawn
    // via the in-scene UI overlay. The box
    // auto-sizes to the current content each frame and sits in the top-right corner.
    private void DrawPropertiesPanel()
    {
        var lines = new List<(string text, ConsoleColor color)>();

        if (_selected < 0 || _selected >= _editables.Count)
        {
            lines.Add(("(no selection)", HudMeta));
        }
        else
        {
            var entry = _editables[_selected];
            string type = entry.Descriptor.Type ?? "";
            var fields = FieldsFor(entry);
            Field active = fields[Math.Clamp(_fieldIndex, 0, fields.Length - 1)];

            // Read-only header: Type, stable Id, and the display Name (user name, or the system name).
            lines.Add(($"  Type: {type}", HudMeta));
            lines.Add(($"  Id: {entry.Descriptor.Id}", HudMeta));
            lines.Add(($"  Name: {DisplayName(entry)}", HudMeta));
            if (type == "mesh")
                lines.Add(($"  Mesh: {entry.Descriptor.Mesh}", HudMeta));

            // Editable fields with the active-field cursor. Labels are padded to a common width so
            // the values line up in a column.
            int labelW = 0;
            foreach (var f in fields) labelW = Math.Max(labelW, FieldLabel(f).Length);
            foreach (var f in fields)
            {
                bool on = f == active;
                string line = $"{(on ? ">" : " ")} {FieldLabel(f).PadRight(labelW)} : {FieldValue(f, entry)}";
                lines.Add((line, on ? HudAccent : HudHint));
            }

            // Camera rotation maps to the view's ray convention: X = roll (spins the image), Y = yaw,
            // Z = pitch. Noting it here avoids "RotX doesn't turn the camera" confusion (RotX only rolls).
            if (type == "camera")
                lines.Add(("Rot: X=roll  Y=yaw  Z=pitch", HudMeta));

            if (_entryMode)
                lines.Add(("[Enter] confirm  [Esc] cancel", HudHeader));
            else if (active == Field.Name)
                lines.Add((",/. field   [Enter] type name   Del", HudMeta));
            else if (IsNumericField(active))
                lines.Add((",/. field   [Enter] type / N,M step   Del", HudMeta));
            else
                lines.Add((",/. field   N,M cycle   Del", HudMeta));
        }

        DrawOverlayBox("PROPERTIES", lines);
    }

    // True when the console's output encoding can represent the box-drawing glyphs; if not, the
    // overlay box falls back to ASCII (+ - |). Probed once via an encoding round-trip.
    private static readonly bool BoxCharsSupported = DetectBoxChars();
    private static bool DetectBoxChars()
    {
        try
        {
            const string probe = "┌─┐│└┘";
            var enc = Console.OutputEncoding;
            return enc.GetString(enc.GetBytes(probe)) == probe;
        }
        catch { return false; }
    }

    // Draws a bordered box (grey frame, inset green title) via the UI
    // overlay: a top border with the title seated in it, blank framed side rows with the content
    // overlaid, and a bottom border. Auto-sizes to the widest content line. Anchors to the top-right
    // corner by default (the PROPERTIES panel); pass anchorRight:false for a top-left box (the EDIT
    // MODE panel) and `top` for its first row — so both HUD panels share one styled look.
    private void DrawOverlayBox(string title, IReadOnlyList<(string text, ConsoleColor color)> lines,
                                bool anchorRight = true, int top = 0)
    {
        bool uni = BoxCharsSupported;
        char tl = uni ? '┌' : '+', tr = uni ? '┐' : '+', bl = uni ? '└' : '+', br = uni ? '┘' : '+';
        char hz = uni ? '─' : '-', vt = uni ? '│' : '|';
        const ConsoleColor border = ConsoleColor.Gray;
        const ConsoleColor titleColor = HudHeader;

        // Interior width: the widest content line, but always enough to seat the title in the top
        // border, plus one space of padding on each side.
        int innerW = title.Length + 1;
        foreach (var (text, _) in lines) innerW = Math.Max(innerW, text.Length);
        innerW += 2;

        int boxW = innerW + 2;   // + the two side borders
        int left = anchorRight ? Math.Max(0, Console.WindowWidth - boxW) : 2;

        // Top border with the inset title:  ┌─ PROPERTIES ───┐
        UI.AddText(tl + new string(hz, innerW) + tr, new Vector2Int(left, top), border);
        UI.AddText($" {title} ", new Vector2Int(left + 2, top), titleColor);

        // Content rows: a blank framed row, then the line overlaid one space in from the border.
        int row = top + 1;
        foreach (var (text, color) in lines)
        {
            UI.AddText(vt + new string(' ', innerW) + vt, new Vector2Int(left, row), border);
            UI.AddText(text, new Vector2Int(left + 2, row), color);
            row++;
        }

        UI.AddText(bl + new string(hz, innerW) + br, new Vector2Int(left, row), border);
    }

    private static string FieldLabel(Field f) => f switch
    {
        Field.PosX => "Pos X", Field.PosY => "Pos Y", Field.PosZ => "Pos Z",
        Field.RotX => "Rot X", Field.RotY => "Rot Y", Field.RotZ => "Rot Z",
        Field.Scale => "Scale", Field.RotateSpeed => "Spin",
        Field.ColorR => "R", Field.ColorG => "G", Field.ColorB => "B", Field.ColorA => "A",
        Field.Radius => "Radius", Field.Power => "Power", Field.ClrInf => "Influence",
        Field.Kind => "Kind", Field.DirX => "Dir X", Field.DirY => "Dir Y", Field.DirZ => "Dir Z",
        Field.ConeAngle => "Cone", Field.AreaSize => "Size", Field.AreaShape => "Shape", Field.Spin => "Spin",
        Field.Beams => "Beams", Field.Shape => "Shape",
        Field.PlatShape => "Shape", Field.PlatSize => "Size",
        Field.PlatWidth => "Width", Field.PlatDepth => "Depth",
        Field.Collides => "Collide",
        Field.Gravity => "Gravity",
        Field.Collider => "Collider",
        Field.Mass => "Mass",
        Field.Restitution => "Bounce",
        Field.Friction => "Friction",
        Field.RollingFriction => "RollFric",
        Field.ColorFade => "Pale",
        Field.Texture => "Texture",
        Field.TextureScale => "TexScale",
        Field.TextureFace => "TexFace",
        Field.TextureFilter => "TexFilter",
        Field.CamKind => "Kind",
        Field.FollowTargetId => "Target",
        Field.Name => "Name",
        _ => f.ToString()
    };

    private string FieldValue(Field f, EditEntry entry)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        // While typing into THIS field on the selected entry, show the live buffer with a cursor (Name or
        // numeric alike) — the single display point for inline entry, mirroring the old rename display.
        if (_entryMode && _entryField == f && _selected >= 0 && _selected < _editables.Count && _editables[_selected] == entry)
            return $"{_entryBuffer}_";
        return f switch
        {
            Field.PosX => inst.Position.X.ToString("F2"),
            Field.PosY => inst.Position.Y.ToString("F2"),
            Field.PosZ => inst.Position.Z.ToString("F2"),
            Field.RotX => inst.LocalRotate.X.ToString("F2"),
            Field.RotY => inst.LocalRotate.Y.ToString("F2"),
            Field.RotZ => inst.LocalRotate.Z.ToString("F2"),
            Field.Scale => (o?.Scale ?? 1f).ToString("F2"),
            Field.RotateSpeed => (o?.RotateSpeed ?? 0f).ToString("F2"),
            Field.ColorR => inst.Color.R.ToString(),
            Field.ColorG => inst.Color.G.ToString(),
            Field.ColorB => inst.Color.B.ToString(),
            Field.ColorA => inst.Color.A.ToString(),
            Field.Radius => (inst as Sphere)?.R.ToString("F2") ?? "-",
            Field.Power => (entry.Light?.LightPower ?? entry.Descriptor.Power).ToString("F0"),
            Field.ClrInf => (entry.Light?.ColorInfluence ?? 0f).ToString("F2"),
            Field.Kind => entry.Light != null ? LightKindToString(entry.Light.Kind) : "-",
            Field.DirX => (entry.Light?.Direction.X ?? 0f).ToString("F2"),
            Field.DirY => (entry.Light?.Direction.Y ?? 0f).ToString("F2"),
            Field.DirZ => (entry.Light?.Direction.Z ?? 0f).ToString("F2"),
            Field.ConeAngle => (entry.Light?.ConeAngleDeg ?? 0f).ToString("F0"),
            Field.AreaSize => (entry.Light?.AreaSize ?? 0f).ToString("F2"),
            Field.AreaShape => entry.Light != null ? ConeShapeToString(entry.Light.AreaShape) : "-",
            Field.Spin => (entry.Light?.SpinSpeed ?? 0f).ToString("F2"),
            Field.Beams => (entry.Light?.BeamCount ?? 1).ToString(),
            Field.Shape => entry.Light != null ? ConeShapeToString(entry.Light.ConeShape) : "-",
            Field.PlatShape => entry.Platform?.Shape ?? "-",
            Field.PlatSize => entry.Platform?.Size.ToString("F1") ?? "-",
            Field.PlatWidth => entry.Platform?.Width.ToString("F1") ?? "-",
            Field.PlatDepth => entry.Platform?.Depth.ToString("F1") ?? "-",
            // Collide/Gravity show their effective state; "locked" makes clear the world switch
            // forces it off (you can't enable it until the world's master switch is on).
            Field.Collides => !_world.Physics.CollisionEnabled ? "Off (locked)" : (inst.Collides ? "On" : "Off"),
            Field.Gravity  => !_world.Physics.GravityEnabled   ? "Off (locked)" : (inst.Gravity  ? "On" : "Off"),
            Field.Collider => !_world.Physics.CollisionEnabled ? "AABB (locked)" : (inst.Collider == ColliderShape.Obb ? "OBB" : "AABB"),
            Field.Mass => inst.Mass.ToString("F2"),
            // <0 means "inherit world default" — show it as world (effective value) so it's never blank.
            Field.Restitution => inst.Restitution < 0f ? $"world ({RestitutionOf(inst):F2})" : inst.Restitution.ToString("F2"),
            Field.Friction => inst.Friction.ToString("F2"),
            Field.RollingFriction => inst.RollingFriction.ToString("F2"),
            Field.ColorFade => inst.ColorFade.ToString("F2"),
            Field.Texture => string.IsNullOrEmpty(inst.Texture?.Name) ? "none" : inst.Texture!.Name,
            Field.TextureScale => inst.TextureScale.ToString("F2"),
            Field.TextureFace => TextureFaceLabel(entry.Descriptor.Type, inst.TextureFace),
            Field.TextureFilter => inst.TextureFilter switch
            {
                TextureFilterMode.Mipmapped => "Mipmapped",
                TextureFilterMode.Bilinear => "Bilinear",
                _ => "Nearest",
            },
            Field.CamKind => ParseCameraKind(entry.Descriptor.CameraKind) == CameraMode.Follow ? "Follow" : "Fixed",
            Field.FollowTargetId => entry.Descriptor.FollowTargetId < 0 ? "player" : $"#{entry.Descriptor.FollowTargetId}",
            Field.Name => DisplayName(entry),   // live typing handled by the buffer check above
            _ => ""
        };
    }

    // Editor display for a TextureFace value: "All" for -1, else the type's face option name (a cube:
    // +X..-Z), or the raw index as a fallback for an out-of-range value from hand-edited JSON.
    private static string TextureFaceLabel(string? type, int face)
    {
        if (face < 0) return "All";
        string[] opts = TextureFaceOptions(type);
        return face < opts.Length ? opts[face] : face.ToString();
    }

    // ================= PLAY HUD =================

    // Minimal HUD over full-screen 3D: one subtle mode/chat hint + the aim crosshair. No panels. (fps is the
    // render loop's own top-left readout in PLAY/OVERLAY — suppressed only in docked.)
    private void DrawPlayHud()
    {
        UI.AddText("[Tab] edit   [`] docked   ·   [T] chat", new Vector2Int(2, 1), DockDim);
        DrawCrosshair();
    }

    // ================= DOCKED EDITOR (Unity/Blender-style) =================
    // A centre 3D viewport with four docked panels around it. The 3D renders into the centre rect (via
    // GetViewports); the panels FILL their rects (so no stale 3D shows through). The five rects TILE the
    // screen exactly (DockLayout, pure + tested). Colours/collapsible sections are later stages.

    // Layout tunables: the side panels are a fraction of the width, clamped to a column range, then shrunk
    // so the centre viewport keeps a minimum. Top/bottom 1-row bars drop out on a very short terminal.
    private const int MinViewportW = 16;
    private const int MinViewportH = 3;
    private const int HierMin = 18, HierMax = 22;
    private const int InspMin = 28, InspMax = 32;
    private const float HierFrac = 0.18f, InspFrac = 0.24f;

    // A screen rectangle in cells. Public so the pure DockLayout can be unit-tested.
    public readonly struct DockRect
    {
        public readonly int X, Y, W, H;
        public DockRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int CenterX => X + W / 2;
        public int CenterY => Y + H / 2;
    }

    public readonly struct DockLayoutRects
    {
        public readonly DockRect Toolbar, Status, Hierarchy, Inspector, Viewport;
        public DockLayoutRects(DockRect toolbar, DockRect status, DockRect hierarchy, DockRect inspector, DockRect viewport)
        { Toolbar = toolbar; Status = status; Hierarchy = hierarchy; Inspector = inspector; Viewport = viewport; }
    }

    // PURE docked layout (unit-tested): splits a W×H terminal into a top toolbar, bottom status bar, left
    // hierarchy, right inspector, and the remaining centre viewport. The five rectangles TILE the screen
    // EXACTLY (cover every cell, no overlap). On a tiny terminal the bars/panels shrink or drop so the
    // viewport keeps a usable minimum — never negative dims, never a crash.
    public static DockLayoutRects DockLayout(int width, int height)
    {
        int W = Math.Max(1, width), H = Math.Max(1, height);

        // Top + bottom 1-row bars only when there's room for them plus a minimum-height viewport.
        bool bars = H >= MinViewportH + 2;
        int top = bars ? 1 : 0, bot = bars ? 1 : 0;
        int midY = top, midH = H - top - bot;   // >= 1

        // Side panel widths: a fraction of the screen, clamped to the design range, then shrunk (inspector
        // first, then hierarchy) so the viewport keeps MinViewportW. On a very narrow screen both collapse.
        int hierW = Math.Clamp((int)MathF.Round(W * HierFrac), HierMin, HierMax);
        int inspW = Math.Clamp((int)MathF.Round(W * InspFrac), InspMin, InspMax);
        int overflow = hierW + inspW + MinViewportW - W;
        if (overflow > 0) { int c = Math.Min(overflow, inspW); inspW -= c; overflow -= c; }
        if (overflow > 0) { int c = Math.Min(overflow, hierW); hierW -= c; overflow -= c; }
        int vpW = Math.Max(1, W - hierW - inspW);

        return new DockLayoutRects(
            new DockRect(0, 0, W, top),                 // toolbar (h=0 when no bars)
            new DockRect(0, H - bot, W, bot),           // status  (h=0 when no bars)
            new DockRect(0, midY, hierW, midH),         // hierarchy (w=0 when collapsed)
            new DockRect(W - inspW, midY, inspW, midH), // inspector (w=0 when collapsed)
            new DockRect(hierW, midY, vpW, midH));      // centre viewport (the remainder)
    }

    private void DrawDockedHud()
    {
        DockLayoutRects L = DockLayout(Console.WindowWidth, Console.WindowHeight);
        DrawDockToolbar(L.Toolbar);
        DrawDockStatus(L.Status);
        DrawDockHierarchy(L.Hierarchy);
        DrawDockInspector(L.Inspector);
        DrawCrosshair();   // at the centre viewport rect (DrawCrosshair's docked branch)
    }

    // Top toolbar: the mode indicator + the F-toggle states, coloured so on/off reads at a glance. No
    // leading gap needed now that the docked standalone fps is suppressed (Fix 1).
    private void DrawDockToolbar(DockRect r)
    {
        var segs = new List<Seg>
        {
            new Seg("EDIT — DOCKED", DockTitle),
            new Seg("   F1 ", DockLabel), new Seg(_flyMode ? "Fly" : "Walk", DockValue),
        };
        void Toggle(string key, string label, bool on)
        {
            segs.Add(new Seg($"   {key} {label} ", DockLabel));
            segs.Add(new Seg(on ? "on" : "off", on ? DockOn : DockDim));
        }
        Toggle("F2", "Shadows", _world.Graphics.Shadows);
        Toggle("F3", "BVH", _world.Graphics.Bvh);
        Toggle("F4", "Light", !_world.Graphics.DisableCameraLight);
        Toggle("F6", "Floor", _world.Platform.Enabled);
        segs.Add(new Seg("   F7 ", DockLabel));       segs.Add(new Seg(ViewLabel(), DockValue));
        segs.Add(new Seg("   F9 Split ", DockLabel)); segs.Add(new Seg(_splitScreen ? "2-way" : "off", _splitScreen ? DockOn : DockDim));
        DrawBarRow(r, segs);
    }

    // Bottom status bar: mode · fps · network · selection · key hints (fps lives HERE in docked, Fix 1).
    private void DrawDockStatus(DockRect r)
    {
        string net = !_online ? "OFFLINE" : (_isServer ? $"SERVER · {_remotePlayers.Count} peer(s)" : "CLIENT");
        bool hasSel = _selected >= 0 && _selected < _editables.Count;
        string sel = hasSel ? $"{DescribeEntry(_editables[_selected])} #{_editables[_selected].Descriptor.Id}" : "(none)";
        int fps = (int)Math.Round(GameTime.GetFps());
        string detail = RenderScale > 1 ? $" · Detail {RenderScale}/4" : "";
        string wait = _awaitingWorld ? " · waiting for world…" : "";
        var segs = new List<Seg>
        {
            new Seg(" DOCKED", DockTitle),
            new Seg($"   {fps} fps", DockValue),
            new Seg($"   {net}{detail}{wait}", DockDim),
            new Seg("   Sel: ", DockLabel), new Seg(sel, hasSel ? DockSel : DockDim),
            new Seg("    [ / ] sel  [B] spawn  [Enter] type/toggle  [T] chat", DockDim),
        };
        DrawBarRow(r, segs);
    }

    // Left hierarchy: the spawn type at top, then the world objects by id + type — an interactive, navigable
    // list. [ / ] move the SELECTION (the Inspector + highlight follow); the SELECTED one is marked "*" + the
    // accent. If the list is taller than the panel it SCROLLS (HierarchyWindow) to keep the selection visible.
    private void DrawDockHierarchy(DockRect r)
    {
        var rows = new List<Seg[]>
        {
            new[] { new Seg("Spawn: ", DockLabel), new Seg(_spawnTypes.Count > 0 ? _spawnTypes[_spawnIndex] : "-", DockValue) },
            new[] { new Seg("", DockDim) },
        };

        // The object list gets the interior rows below the 2 header rows; it scrolls to keep _selected in view.
        int listRows = Math.Max(1, (r.H - 2) - rows.Count);
        var (start, count) = HierarchyWindow(_editables.Count, listRows, _selected);
        for (int i = start; i < start + count; i++)
        {
            bool sel = i == _selected;
            string body = $"#{_editables[i].Descriptor.Id} {DescribeEntry(_editables[i])}";
            rows.Add(sel
                ? new[] { new Seg($"{MarkerSelected} ", DockSel), new Seg(body, DockSel) }
                : new[] { new Seg("  ", DockDim),                  new Seg(body, DockLabel) });
        }

        // Title shows the selection position so it's clear there's more above/below when the list scrolls.
        string title = _editables.Count > 0 ? $"HIERARCHY  {Math.Clamp(_selected, 0, _editables.Count - 1) + 1}/{_editables.Count}" : "HIERARCHY";
        DrawPanel(r, title, DockTitle, rows);
    }

    // PURE (unit-tested): the visible window [start, start+count) of a `listCount`-item list in a `rows`-row
    // panel that KEEPS the `selected` item in view (auto-scroll on selection change), clamped to the ends.
    // When the list fits, shows all from 0; otherwise centres the selection and clamps to [0, listCount-rows].
    // Mirrors ChatVisibleSlice. Navigation past the ends is handled by the [ / ] keys (they wrap _selected).
    public static (int start, int count) HierarchyWindow(int listCount, int rows, int selected)
    {
        if (rows < 1) rows = 1;
        if (listCount <= rows) return (0, Math.Max(0, listCount));   // fits — show all
        selected = Math.Clamp(selected, 0, listCount - 1);
        int start = Math.Clamp(selected - rows / 2, 0, listCount - rows);   // centre the selection, clamp at both ends
        return (start, rows);
    }

    // Right inspector: the selected object's fields grouped under COLLAPSIBLE Transform/Appearance/Physics/
    // Texture sub-headers ([-] expanded / [+] collapsed — ASCII, Fix 2). The field cursor navigates the
    // headers + the currently-VISIBLE fields (BuildInspectorRows); the ">" cursor + N,M/type-value editing
    // work on a field row, Enter on a header toggles it. Colours (stage 3): muted labels + bright values,
    // the active row + selected object accented, collapsed headers dimmed. Collapsed fields are skipped.
    private void DrawDockInspector(DockRect r)
    {
        var rows = new List<Seg[]>();
        if (_selected < 0 || _selected >= _editables.Count)
        {
            rows.Add(new[] { new Seg("(no selection)", DockDim) });
            rows.Add(new[] { new Seg("", DockDim) });
            rows.Add(new[] { new Seg("select an object", DockDim) });
        }
        else
        {
            var entry = _editables[_selected];
            var fields = FieldsFor(entry);
            var irows = BuildInspectorRows(SectionsOf(fields), _collapsedSections);
            int cursor = Math.Clamp(_fieldIndex, 0, irows.Count - 1);

            rows.Add(new[] { new Seg($"{DescribeEntry(entry)} #{entry.Descriptor.Id}", DockSel) });
            if ((entry.Descriptor.Type ?? "") == "mesh")
                rows.Add(new[] { new Seg($"mesh: {entry.Descriptor.Mesh}", DockDim) });

            int labelW = 0;
            foreach (var f in fields) labelW = Math.Max(labelW, FieldLabel(f).Length);
            for (int i = 0; i < irows.Count; i++)
            {
                var row = irows[i];
                bool on = i == cursor;
                string cur = on ? ">" : " ";
                if (row.IsHeader)
                {
                    bool collapsed = _collapsedSections.Contains(row.Section);
                    string marker = collapsed ? MarkerCollapsed : MarkerExpanded;
                    ConsoleColor hc = on ? DockSel : (collapsed ? DockDim : DockSection);
                    rows.Add(new[] { new Seg($"{cur} {marker} {row.Section}", hc) });
                }
                else
                {
                    Field f = fields[row.FieldIndex];
                    rows.Add(new[]
                    {
                        new Seg($"{cur}   {FieldLabel(f).PadRight(labelW)} ", on ? DockSel : DockLabel),
                        new Seg(FieldValue(f, entry), DockValue),
                    });
                }
            }

            rows.Add(new[] { new Seg("", DockDim) });
            var (af, onHeader, _) = ActiveInspectorTarget(entry);
            string hint = _entryMode ? "[Enter] confirm  [Esc] cancel"
                : onHeader ? ",/. row  [Enter] collapse/expand"
                : af == Field.Name ? ",/. row  [Enter] type name  Del"
                : (af is Field nf && IsNumericField(nf)) ? ",/. row  [Enter]/N,M step  Del"
                : ",/. row  N,M cycle  Del";
            rows.Add(new[] { new Seg(hint, _entryMode ? DockSel : DockDim) });
        }
        DrawPanel(r, "INSPECTOR", DockTitle, rows);
    }

    // ---- Collapsible-section model (Part B) ----
    // A docked-Inspector navigation row: either a section header, or a field (carried by its index into the
    // source field list). Plain data (string/int/bool) so the pure BuildInspectorRows is unit-testable
    // WITHOUT exposing the private Field enum.
    public readonly struct InspectorRow
    {
        public readonly bool IsHeader;
        public readonly string Section;
        public readonly int FieldIndex;   // index into the source fields array; -1 for a header
        public InspectorRow(bool isHeader, string section, int fieldIndex) { IsHeader = isHeader; Section = section; FieldIndex = fieldIndex; }
    }

    // PURE (unit-tested): given each field's SECTION name (in field order) + the set of collapsed sections,
    // build the visible Inspector rows — when the section changes emit a Header row, then a Field row ONLY if
    // that section is expanded (collapsed sections show the header alone). The field cursor navigates these
    // rows, so it can never land on a hidden field. Default (empty collapsed set) = every field visible.
    public static List<InspectorRow> BuildInspectorRows(IReadOnlyList<string> fieldSections, ISet<string> collapsed)
    {
        var rows = new List<InspectorRow>();
        string? cur = null;
        for (int i = 0; i < fieldSections.Count; i++)
        {
            string g = fieldSections[i];
            if (g != cur) { cur = g; rows.Add(new InspectorRow(true, g, -1)); }
            if (!collapsed.Contains(g)) rows.Add(new InspectorRow(false, g, i));
        }
        return rows;
    }

    // The section name of each field, in order (the DOCKED Inspector row source).
    private static string[] SectionsOf(Field[] fields)
    {
        var s = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++) s[i] = FieldGroup(fields[i]);
        return s;
    }

    // What the edit actions target for the current selection + HUD mode: a FIELD (overlay: the flat cursor;
    // docked: the field of the cursor's row), or a section HEADER when the DOCKED cursor is on one (Enter
    // toggles it). Keeps `_fieldIndex` as the single cursor — interpreted as a flat field index in OVERLAY,
    // and as a row index (into BuildInspectorRows) in DOCKED.
    private (Field? field, bool onHeader, string section) ActiveInspectorTarget(EditEntry entry)
    {
        var fields = FieldsFor(entry);
        if (_hudMode != HudMode.DockedEdit)
            return (fields[Math.Clamp(_fieldIndex, 0, fields.Length - 1)], false, "");
        var rows = BuildInspectorRows(SectionsOf(fields), _collapsedSections);
        var row = rows[Math.Clamp(_fieldIndex, 0, rows.Count - 1)];
        return row.IsHeader ? ((Field?)null, true, row.Section) : (fields[row.FieldIndex], false, "");
    }

    // Toggle a docked Inspector section's collapsed state (local view action — clients too).
    private void ToggleSection(string section)
    {
        if (!_collapsedSections.Remove(section)) _collapsedSections.Add(section);
    }

    // Which Inspector section a field belongs under (the collapsible grouping headers).
    private static string FieldGroup(Field f) => f switch
    {
        Field.PosX or Field.PosY or Field.PosZ or Field.RotX or Field.RotY or Field.RotZ or Field.Scale
            or Field.RotateSpeed or Field.Radius or Field.DirX or Field.DirY or Field.DirZ or Field.Spin
            or Field.CamKind or Field.FollowTargetId
            or Field.PlatShape or Field.PlatSize or Field.PlatWidth or Field.PlatDepth => "Transform",
        Field.ColorR or Field.ColorG or Field.ColorB or Field.ColorA or Field.ColorFade
            or Field.Power or Field.ClrInf or Field.Kind or Field.ConeAngle or Field.AreaSize
            or Field.AreaShape or Field.Beams or Field.Shape => "Appearance",
        Field.Collides or Field.Gravity or Field.Collider or Field.Mass or Field.Restitution
            or Field.Friction or Field.RollingFriction => "Physics",
        Field.Texture or Field.TextureScale or Field.TextureFace or Field.TextureFilter => "Texture",
        _ => "Object",
    };

    // A single full-width HUD bar row (toolbar/status), padded/clipped to the rect width so it fully covers
    // its cells (no stale 3D leaks under the docked layout).
    private void DrawBarRow(DockRect r, IReadOnlyList<Seg> segs)
    {
        if (r.W <= 0 || r.H <= 0) return;
        UI.AddText(new string(' ', r.W), new Vector2Int(r.X, r.Y), DockDim);   // blank-fill (covers stale 3D)
        PaintSegments(r.X, r.Y, r.W, segs);
    }

    // A docked panel that FILLS its whole rectangle: a bordered frame with an inset title (titleColor), then
    // the content rows drawn from coloured SEGMENTS over each row's blank fill (so a field row can show a
    // muted label + a bright value). Filling every cell keeps the docked layout free of stale 3D. Content
    // beyond the interior height is clipped (scrolling is a later stage).
    private void DrawPanel(DockRect r, string title, ConsoleColor titleColor, IReadOnlyList<Seg[]> rows)
    {
        if (r.W <= 0 || r.H <= 0) return;
        if (r.W < 3 || r.H < 3)   // too small to frame — blank-fill so it never leaks the 3D beneath
        {
            for (int y = 0; y < r.H; y++) UI.AddText(new string(' ', r.W), new Vector2Int(r.X, r.Y + y), DockDim);
            return;
        }
        bool uni = BoxCharsSupported;
        char tl = uni ? '┌' : '+', tr = uni ? '┐' : '+', bl = uni ? '└' : '+', br = uni ? '┘' : '+';
        char hz = uni ? '─' : '-', vt = uni ? '│' : '|';
        int innerW = r.W - 2;

        UI.AddText(tl + new string(hz, innerW) + tr, new Vector2Int(r.X, r.Y), DockBorder);
        if (innerW >= title.Length + 2) UI.AddText($" {title} ", new Vector2Int(r.X + 2, r.Y), titleColor);

        for (int i = 0; i < r.H - 2; i++)
        {
            int row = r.Y + 1 + i;
            UI.AddText(vt + new string(' ', innerW) + vt, new Vector2Int(r.X, row), DockBorder);   // fill the row
            if (i < rows.Count) PaintSegments(r.X + 1, row, innerW, rows[i]);
        }

        UI.AddText(bl + new string(hz, innerW) + br, new Vector2Int(r.X, r.Y + r.H - 1), DockBorder);
    }

    // Draws coloured segments left-to-right from (x,y), clipping to maxW cells (later segments dropped when
    // the width runs out). Shared by the bar rows and the panel content rows.
    private void PaintSegments(int x, int y, int maxW, IReadOnlyList<Seg> segs)
    {
        int col = x, remaining = maxW;
        for (int s = 0; s < segs.Count; s++)
        {
            if (remaining <= 0) break;
            string t = segs[s].Text;
            string shown = t.Length > remaining ? t.Substring(0, remaining) : t;
            if (shown.Length > 0) UI.AddText(shown, new Vector2Int(col, y), segs[s].Color);
            col += shown.Length; remaining -= shown.Length;
        }
    }

    // A coloured text segment of a docked HUD row.
    private readonly struct Seg
    {
        public readonly string Text; public readonly ConsoleColor Color;
        public Seg(string text, ConsoleColor color) { Text = text; Color = color; }
    }

    // ================= CONTAINED CHAT PANEL (chat rework) =================
    // The chat is now a bounded, styled box placed CLEAR of the HUD (ChatBoxRect), with word-WRAPPED history
    // + input (WrapText) and a SCROLLABLE window (ChatVisibleSlice; PgUp/PgDn). It fills every cell of its
    // rect (via DrawPanel), so it never bleeds over the panels. The pure helpers below are unit-tested.

    private const int ChatMaxW = 60;          // chat box width cap (border + text)
    private const int ChatMaxH = 12;          // chat box height cap (border + rows)
    private const int ChatMinH = 5;           // minimum usable height (borders + a couple rows + hint)
    private const int ChatInputCap = 3;       // max wrapped input rows shown (a very long input shows its tail)
    private const int ChatShowFrames = 360;   // frames a message keeps the box shown (~6s @ 60fps) before it collapses
    public  const int OverlayHudBottom = 34;  // OVERLAY HUD boxes (EDIT MODE/KEYS top-left, PROPERTIES top-right) reserve the top rows; chat sits below
    public  const int PlayHudBottom = 3;      // PLAY HUD is just a top hint line; chat sits below it

    // PURE word-wrap (unit-tested): greedy-wrap `text` to `width` columns; a token longer than `width` is
    // HARD-SPLIT across lines so no line ever exceeds `width`. Empty text → a single empty line.
    public static List<string> WrapText(string text, int width)
    {
        if (width < 1) width = 1;
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

        string cur = "";
        foreach (string word in text.Split(' '))
        {
            string w = word;
            while (w.Length > width)               // hard-split an over-long token
            {
                if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                lines.Add(w.Substring(0, width));
                w = w.Substring(width);
            }
            if (w.Length == 0) continue;           // fully consumed by the split, or an empty token (double space)
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= width) cur += " " + w;
            else { lines.Add(cur); cur = w; }
        }
        lines.Add(cur);
        return lines;
    }

    // PURE scroll window (unit-tested): the visible slice [start, start+count) of `totalLines` wrapped history
    // lines for a `boxRows`-row box at scroll `offset` (0 = newest/bottom, higher = older). The offset clamps
    // to [0, max]; at offset 0 the NEWEST lines show (auto-bottom), so a new message stays visible when at the
    // bottom but scrolling up (offset>0) keeps showing older lines instead of jumping down.
    public static (int start, int count, int clampedOffset) ChatVisibleSlice(int totalLines, int boxRows, int offset)
    {
        if (boxRows < 1) boxRows = 1;
        if (totalLines < 0) totalLines = 0;
        int maxOffset = Math.Max(0, totalLines - boxRows);
        int off = Math.Clamp(offset, 0, maxOffset);
        int start = Math.Max(0, totalLines - boxRows - off);
        int count = Math.Max(0, Math.Min(boxRows, totalLines - start));
        return (start, count, off);
    }

    // PURE placement (unit-tested): the chat box rectangle for the HUD `mode` (0 Play, 1 OverlayEdit,
    // 2 DockedEdit), computed so it NEVER overlaps the HUD. DOCKED: anchored to the bottom of the layout's
    // VIEWPORT rect (so it can't touch the toolbar/status/hierarchy/inspector). PLAY/OVERLAY: bottom-left,
    // below the reserved top-HUD rows (PlayHudBottom / OverlayHudBottom) and narrow enough to clear the
    // top-right PROPERTIES box. Returns a zero-size rect when there's no room (the caller then skips it).
    public static DockRect ChatBoxRect(int mode, int width, int height, DockLayoutRects dock)
    {
        int W = Math.Max(1, width), H = Math.Max(1, height);
        if (mode == 2)   // DockedEdit — inside the viewport, anchored to its bottom-left
        {
            DockRect vp = dock.Viewport;
            int cw = Math.Min(ChatMaxW, vp.W - 2);
            int ch = Math.Min(ChatMaxH, vp.H - 1);
            if (cw < 3 || ch < ChatMinH) return default;
            return new DockRect(vp.X + 1, vp.Y + vp.H - ch, cw, ch);
        }
        int reservedTop = mode == 1 ? OverlayHudBottom : PlayHudBottom;
        int boxH = Math.Min(ChatMaxH, H - reservedTop);
        int boxW = Math.Min(ChatMaxW, W - 4);
        if (boxW < 3 || boxH < ChatMinH) return default;
        return new DockRect(2, H - boxH, boxW, boxH);   // anchored to the bottom-left
    }

    // The CONTAINED chat panel: a bordered box (ChatBoxRect) with word-wrapped, scrollable history + the
    // input line + the [ENTER]/[ESC] hint INSIDE it. Shown while chatting or briefly after a message; else a
    // subtle "[T] chat" hint. Composited LAST (Update) so it sits on top — but always within its own rect.
    private void DrawChatInterface()
    {
        int W = Console.WindowWidth, H = Console.WindowHeight;
        DockRect box = ChatBoxRect((int)_hudMode, W, H, DockLayout(W, H));
        if (box.W < 3 || box.H < 3) return;   // no room for a contained box

        bool docked = _hudMode == HudMode.DockedEdit;
        if (!_isChatting && _chatShowTimer <= 0)
        {
            // Idle with no recent messages — collapse to a subtle hint (skip in docked; the Status bar has it).
            if (!docked) UI.AddText("[T] chat", new Vector2Int(box.X, box.Y + box.H - 1), DockDim);
            return;
        }

        int innerW = box.W - 2, innerH = box.H - 2;

        // Reserve rows for the (wrapped) input + hint while typing; the rest is scrollable history.
        var inputLines = new List<string>();
        int hintRows = 0;
        if (_isChatting)
        {
            inputLines = WrapText("> " + _currentInput + "_", innerW);
            if (inputLines.Count > ChatInputCap) inputLines = inputLines.GetRange(inputLines.Count - ChatInputCap, ChatInputCap);
            hintRows = 1;
        }
        int historyRows = Math.Max(1, innerH - inputLines.Count - hintRows);

        // Wrap every history message, take the visible (scrolled) slice, and keep the offset clamped.
        var histLines = new List<string>();
        foreach (var m in _chatHistory) histLines.AddRange(WrapText(m, innerW));
        var (start, count, clamped) = ChatVisibleSlice(histLines.Count, historyRows, _chatScroll);
        _chatScroll = clamped;

        // Rows: blank top padding, then history (newest just above the input), then the input + hint.
        var rows = new List<Seg[]>();
        for (int i = count; i < historyRows; i++) rows.Add(new[] { new Seg("", DockDim) });
        for (int i = 0; i < count; i++)          rows.Add(new[] { new Seg(histLines[start + i], DockValue) });
        foreach (var il in inputLines)           rows.Add(new[] { new Seg(il, DockSel) });
        if (hintRows > 0)                        rows.Add(new[] { new Seg("[ENTER] Send  [ESC] Cancel  [PgUp/PgDn] scroll", DockDim) });

        string title = clamped > 0 ? "CHAT  ^ older (PgDn)" : "CHAT";   // subtle scrolled-up indicator
        DrawPanel(box, title, DockTitle, rows);
    }

}
