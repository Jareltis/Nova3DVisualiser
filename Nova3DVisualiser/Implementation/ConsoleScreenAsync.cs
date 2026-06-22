using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.StaticClass;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Nova3DVisualiser.Implementation;
public class ConsoleScreenAsync : Screen
{
    private const string Gradient = " .:!/r(l1Z4H9W8$@";
    private const char Esc = (char)27;                             // ANSI escape (ESC); kept as a char so no raw control byte lives in source
    private readonly char[] _charBuffer;
    private readonly StringBuilder _frame = new StringBuilder();   // reused per frame for the ANSI output

    private readonly float _aspectRatio;

    public ConsoleScreenAsync() : base(Console.WindowWidth, Console.WindowHeight)
    {
        EnableVirtualTerminal();   // so 24-bit ANSI truecolor escapes are honored (no-op off-Windows)
        _charBuffer = new char[Width * Height];
        Console.CursorVisible = false;

        float windowAspect = (float)Width / Height;
        float pixelAspect = 11.0f / 24.0f;
        _aspectRatio = windowAspect * pixelAspect;
    }

    protected override Vector2 CalculateUV(int i, int j)
    {
        Vector2 uv = new Vector2((float)i / (Width - 1), (float)j / (Height - 1)) * 2 - 1;
        uv.X *= _aspectRatio;
        uv.Y = -uv.Y;
        return uv;
    }

    protected override void Present()
    {
        // Assemble the whole frame as one string and write it once. A 24-bit ANSI foreground escape
        // is emitted only when the cell color changes from the previous cell (run-length), so the
        // string stays bounded for runs of same-colored cells. Cursor home + reset bracket the frame.
        int len = _charBuffer.Length;
        if (len == 0) return;

        _frame.Clear();

        Rgb24 current = ColorBuffer[0];
        AppendColor(_frame, current);
        for (int i = 0; i < len; i++)
        {
            Rgb24 c = ColorBuffer[i];
            if (c != current) { AppendColor(_frame, c); current = c; }
            _frame.Append(_charBuffer[i]);
        }
        _frame.Append(Esc).Append("[0m");   // reset so later direct writes (FPS) start from a clean color

        Console.SetCursorPosition(0, 0);
        Console.Out.Write(_frame.ToString());
    }

    // Appends a 24-bit ANSI set-foreground escape: ESC[38;2;r;g;bm
    private static void AppendColor(StringBuilder sb, Rgb24 c)
    {
        sb.Append(Esc).Append("[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
    }

    public override void PrintText(string text, Vector2Int position)
    {
        try
        {
            // Pure ANSI (white) so it composites cleanly over the truecolor frame, rather than
            // mixing Win32 console attributes with VT.
            Console.SetCursorPosition(position.X, position.Y);
            Console.Out.Write($"{Esc}[38;2;255;255;255m{text}{Esc}[0m");
        }
        catch { }
    }
    
    public override void RenderFrame(Scene scene)
    {
        // Detail level as a cell stride: 1 = a ray per cell (full res, identical to before);
        // 2-4 cast a ray only on every stride-th cell and block-fill the rest.
        int stride = Math.Clamp(scene.RenderScale, 1, 4);
        int anchorRows = (Height + stride - 1) / stride;

        // Parallelize over anchor ROWS only; each anchor casts one ray and fills its own
        // stride×stride block, so every parallel iteration writes a disjoint region (no race).
        Parallel.For(0, anchorRows, jr =>
        {
            int j = jr * stride;
            int jMax = Math.Min(j + stride, Height);   // clamp the bottom edge (ragged blocks)

            for (int i = 0; i < Width; i += stride)
            {
                Vector2 uv = CalculateUV(i, j);
                var pixelData = scene.GetPixelData(uv);

                int iMax = Math.Min(i + stride, Width); // clamp the right edge
                for (int by = j; by < jMax; by++)
                    for (int bx = i; bx < iMax; bx++)
                    {
                        int index = by * Width + bx;
                        BrightnessBuffer[index] = pixelData.Brightness;
                        ColorBuffer[index] = pixelData.Color;
                    }
            }
        });

        Parallel.For(0, BrightnessBuffer.Length, i =>
        {
            int idx = (int)MathF.Round(BrightnessBuffer[i] * (Gradient.Length - 1));
            idx = Math.Clamp(idx, 0, Gradient.Length - 1);
            _charBuffer[i] = Gradient[idx];
        });

        var uiElements = scene.UI.GetElements();
        foreach (var element in uiElements)
        {
            DrawTextToBuffer(element.Text, element.Position, element.Color);
        }

        Present();
    }
    
    private void DrawTextToBuffer(string text, Vector2Int pos, ConsoleColor color)
    {
        Rgb24 rgb = Rgb24.FromUnit(ColorRgb.ToRgb(color));   // UI keeps using ConsoleColor; map it to truecolor
        for (int i = 0; i < text.Length; i++)
        {
            int x = pos.X + i;
            int y = pos.Y;

            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                int index = y * Width + x;
                _charBuffer[index] = text[i];
                ColorBuffer[index] = rgb;
            }
        }
    }

    // Enables ANSI/VT processing on the console so 24-bit truecolor escapes render (instead of
    // printing literally). Windows-only via kernel32; a no-op (and harmless) on other platforms.
    // Public so a headless color diagnostic (colortest) can enable VT the exact same way.
    public static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { /* best-effort: a terminal that already supports VT still works */ }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
