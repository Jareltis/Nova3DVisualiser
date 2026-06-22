namespace Nova3DVisualiser.StaticClass;

/// <summary>
/// Tiny ConsoleColor &lt;-&gt; RGB bridge for the colored-lighting path: a surface's ConsoleColor
/// becomes RGB albedo, and a lit RGB is quantized back to the nearest of the 16 console colors for
/// display. World-agnostic; no third-party deps. Palette order matches System.ConsoleColor's value.
/// </summary>
public static class ColorRgb
{
    // Standard Windows console palette (0..1 per channel), indexed by (int)ConsoleColor.
    private static readonly Vector3[] Palette =
    {
        new Vector3(0f,    0f,    0f   ),  // 0  Black
        new Vector3(0f,    0f,    0.5f ),  // 1  DarkBlue
        new Vector3(0f,    0.5f,  0f   ),  // 2  DarkGreen
        new Vector3(0f,    0.5f,  0.5f ),  // 3  DarkCyan
        new Vector3(0.5f,  0f,    0f   ),  // 4  DarkRed
        new Vector3(0.5f,  0f,    0.5f ),  // 5  DarkMagenta
        new Vector3(0.5f,  0.5f,  0f   ),  // 6  DarkYellow
        new Vector3(0.75f, 0.75f, 0.75f),  // 7  Gray
        new Vector3(0.5f,  0.5f,  0.5f ),  // 8  DarkGray
        new Vector3(0f,    0f,    1f   ),  // 9  Blue
        new Vector3(0f,    1f,    0f   ),  // 10 Green
        new Vector3(0f,    1f,    1f   ),  // 11 Cyan
        new Vector3(1f,    0f,    0f   ),  // 12 Red
        new Vector3(1f,    0f,    1f   ),  // 13 Magenta
        new Vector3(1f,    1f,    0f   ),  // 14 Yellow
        new Vector3(1f,    1f,    1f   ),  // 15 White
    };

    /// <summary>RGB albedo (0..1) for a ConsoleColor; white for anything out of range.</summary>
    public static Vector3 ToRgb(ConsoleColor c)
    {
        int i = (int)c;
        return (i >= 0 && i < Palette.Length) ? Palette[i] : new Vector3(1f, 1f, 1f);
    }

    /// <summary>The console color whose palette RGB is nearest (squared distance) to the given RGB.</summary>
    public static ConsoleColor Nearest(Vector3 rgb)
    {
        int best = 15;                 // default White
        float bestDist = float.MaxValue;
        for (int i = 0; i < Palette.Length; i++)
        {
            float dx = rgb.X - Palette[i].X, dy = rgb.Y - Palette[i].Y, dz = rgb.Z - Palette[i].Z;
            float dist = dx * dx + dy * dy + dz * dz;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return (ConsoleColor)best;
    }
}
