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

    private void DrawEditorOverlay()
    {
        int x = 2, y = 12;
        if (!_editMode)
        {
            UI.AddText("[Tab] Edit mode", new Vector2Int(x, y), ConsoleColor.DarkGray);
            return;
        }

        UI.AddText("== EDIT MODE ==", new Vector2Int(x, y++), ConsoleColor.Green);
        UI.AddText($"Spawn: {_spawnTypes[_spawnIndex]}", new Vector2Int(x, y++), ConsoleColor.Cyan);

        string sel = _selected >= 0 && _selected < _editables.Count
            ? $"Selected: [{_selected + 1}/{_editables.Count}] {DescribeEntry(_editables[_selected])}"
            : $"Selected: (none) — {_editables.Count} object(s)";
        UI.AddText(sel, new Vector2Int(x, y++), ConsoleColor.Yellow);

        UI.AddText("[G] cycle type  [B] spawn  [ [ / ] ] select  [Enter] type value", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText("Move: J/L=X  I/K=Z  U/O=Y    [F5] save", new Vector2Int(x, y++), ConsoleColor.Gray);
        UI.AddText($"[F1] {(_flyMode ? "Fly" : "Walk")}   [F7] View: {(_cameraMode == CameraMode.ThirdPerson ? "3rd-person" : "1st-person")}", new Vector2Int(x, y++), ConsoleColor.Gray);

        // Runtime graphics settings — only the authority can flip them, so only it sees the hints.
        if (CanEdit)
        {
            string g = $"[F2] Shadows:{OnOff(_world.Graphics.Shadows)}  [F3] BVH:{OnOff(_world.Graphics.Bvh)}  "
                     + $"[F4] CamLight:{OnOff(!_world.Graphics.DisableCameraLight)}  [F6] Floor:{OnOff(_world.Platform.Enabled)}";
            UI.AddText(g, new Vector2Int(x, y++), ConsoleColor.Gray);
        }

        if (_saveFlash > 0)
            UI.AddText(_saveMsg, new Vector2Int(x, y), ConsoleColor.Green);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string DescribeEntry(EditEntry e) =>
        e.Descriptor.Type == "mesh" ? $"mesh:{e.Descriptor.Mesh}" : e.Descriptor.Type;

    // Visual-Studio-style properties panel for the selected object, framed like the Terminal.Gui
    // setup dialogs (a titled, bordered box) but drawn via the in-scene UI overlay. The box
    // auto-sizes to the current content each frame and sits in the top-right corner.
    private void DrawPropertiesPanel()
    {
        var lines = new List<(string text, ConsoleColor color)>();

        if (_selected < 0 || _selected >= _editables.Count)
        {
            lines.Add(("(no selection)", ConsoleColor.DarkGray));
        }
        else
        {
            var entry = _editables[_selected];
            string type = entry.Descriptor.Type ?? "";
            var fields = FieldsFor(entry);
            Field active = fields[Math.Clamp(_fieldIndex, 0, fields.Length - 1)];

            // Read-only header: Type, stable Id, and the display Name (user name, or the system name).
            lines.Add(($"  Type: {type}", ConsoleColor.DarkGray));
            lines.Add(($"  Id: {entry.Descriptor.Id}", ConsoleColor.DarkGray));
            lines.Add(($"  Name: {DisplayName(entry)}", ConsoleColor.DarkGray));
            if (type == "mesh")
                lines.Add(($"  Mesh: {entry.Descriptor.Mesh}", ConsoleColor.DarkGray));

            // Editable fields with the active-field cursor.
            foreach (var f in fields)
            {
                bool on = f == active;
                string line = $"{(on ? ">" : " ")} {FieldLabel(f)}: {FieldValue(f, entry)}";
                lines.Add((line, on ? ConsoleColor.Cyan : ConsoleColor.Gray));
            }

            if (_entryMode)
                lines.Add(("[Enter] confirm  [Esc] cancel", ConsoleColor.Green));
            else if (active == Field.Name)
                lines.Add((",/. field   [Enter] type name   Del", ConsoleColor.DarkGray));
            else if (IsNumericField(active))
                lines.Add((",/. field   [Enter] type / N,M step   Del", ConsoleColor.DarkGray));
            else
                lines.Add((",/. field   N,M cycle   Del", ConsoleColor.DarkGray));
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

    // Draws a bordered box (Terminal.Gui-dialog feel: grey frame, inset green title) via the UI
    // overlay: a top border with the title seated in it, blank framed side rows with the content
    // overlaid, and a bottom border. Auto-sizes to the widest content line and is anchored to the
    // top-right corner, clear of the centre crosshair and the bottom chat.
    private void DrawOverlayBox(string title, IReadOnlyList<(string text, ConsoleColor color)> lines)
    {
        bool uni = BoxCharsSupported;
        char tl = uni ? '┌' : '+', tr = uni ? '┐' : '+', bl = uni ? '└' : '+', br = uni ? '┘' : '+';
        char hz = uni ? '─' : '-', vt = uni ? '│' : '|';
        const ConsoleColor border = ConsoleColor.Gray;
        const ConsoleColor titleColor = ConsoleColor.Green;

        // Interior width: the widest content line, but always enough to seat the title in the top
        // border, plus one space of padding on each side.
        int innerW = title.Length + 1;
        foreach (var (text, _) in lines) innerW = Math.Max(innerW, text.Length);
        innerW += 2;

        int boxW = innerW + 2;   // + the two side borders
        int left = Math.Max(0, Console.WindowWidth - boxW);
        int top = 0;

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
            Field.TextureFilter => inst.TextureFilter == TextureFilterMode.Bilinear ? "Bilinear" : "Nearest",
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

}
