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

    // Previous frame's on-screen content, so Present can write ONLY the cells that changed
    // (diff render). For a still camera almost nothing is written — far less console I/O, which
    // removes the top-to-bottom tearing and lifts the frame rate.
    private readonly char[] _prevChars;
    private readonly Rgb24[] _prevColors;
    private bool _firstPresent = true;   // first frame writes everything to seed the prev buffers

    // Aspect correction applied in CalculateUV. Protected so a subclass renderer (e.g. the GPU
    // screen) can feed the exact same uv mapping to its kernel.
    protected readonly float _aspectRatio;

    public ConsoleScreenAsync() : base(Console.WindowWidth, Console.WindowHeight)
    {
        EnableVirtualTerminal();   // so 24-bit ANSI truecolor escapes are honored (no-op off-Windows)
        _charBuffer = new char[Width * Height];
        _prevChars = new char[Width * Height];
        _prevColors = new Rgb24[Width * Height];
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
        // Diff render: walk the buffer row by row and emit only RUNS of cells that differ from
        // last frame. Each run is jumped to with an ANSI cursor-position escape (ESC[row;colH),
        // then its cells are written with a 24-bit color escape emitted only on a color change.
        // A static frame writes (almost) nothing; a fully-changed frame costs about the same as
        // the old full write but with one cursor jump per row. Everything goes out in one write.
        int w = Width, h = Height;
        if (_charBuffer.Length == 0) return;

        _frame.Clear();
        bool any = false;
        Rgb24 lastColor = default;
        bool haveColor = false;

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * w;
            int x = 0;
            while (x < w)
            {
                int idx = rowStart + x;
                if (!_firstPresent && _charBuffer[idx] == _prevChars[idx] && ColorBuffer[idx] == _prevColors[idx])
                { x++; continue; }

                // Start of a changed run: position the cursor (1-based row;col), then stream the run.
                _frame.Append(Esc).Append('[').Append(y + 1).Append(';').Append(x + 1).Append('H');
                haveColor = false;
                while (x < w)
                {
                    int ri = rowStart + x;
                    char ch = _charBuffer[ri];
                    Rgb24 col = ColorBuffer[ri];
                    if (!_firstPresent && ch == _prevChars[ri] && col == _prevColors[ri]) break;   // run ends
                    if (!haveColor || col != lastColor) { AppendColor(_frame, col); lastColor = col; haveColor = true; }
                    _frame.Append(ch);
                    _prevChars[ri] = ch;
                    _prevColors[ri] = col;
                    x++;
                }
                any = true;
            }
        }

        if (any)
        {
            _frame.Append(Esc).Append("[0m");   // reset so later direct writes (FPS) start from a clean color
            Console.Out.Write(_frame.ToString());
        }
        _firstPresent = false;
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
        // Fill the brightness/color buffers (CPU raytrace by default; a subclass may compute them on
        // the GPU), then the shared compositing below maps them to glyphs, draws UI, and presents.
        FillBuffers(scene);

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

    // Computes BrightnessBuffer + ColorBuffer for the frame. Default: the CPU raytracer (one ray per
    // anchor cell, block-filled by RenderScale stride). A GPU screen overrides this to fill the same
    // two buffers on the device; the rest of RenderFrame (glyph mapping, UI, diff present) is shared.
    protected virtual void FillBuffers(Scene scene)
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
