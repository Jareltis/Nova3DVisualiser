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
    private const int MinDim = 1;                                  // clamp a transient tiny/zero console size so we never allocate 0-length / throw
    private char[] _charBuffer;                                    // re-allocated on a terminal resize (see AdaptToConsoleSize)
    private readonly StringBuilder _frame = new StringBuilder();   // reused per frame for the ANSI output

    // Previous frame's on-screen content, so Present can write ONLY the cells that changed
    // (diff render). For a still camera almost nothing is written — far less console I/O, which
    // removes the top-to-bottom tearing and lifts the frame rate. Re-allocated on a resize.
    private char[] _prevChars;
    private Rgb24[] _prevColors;
    private bool _firstPresent = true;   // first frame (and the frame after a resize) writes everything to seed the prev buffers

    // Aspect correction (full-screen). Refreshed on a resize. Protected so a subclass renderer (e.g. the
    // GPU screen) can feed the exact same uv mapping to its kernel.
    protected float _aspectRatio;
    // The character-cell aspect (a console cell is taller than wide). A region's aspect is its own
    // width/height times this, so a half-width viewport uses half the horizontal spread. Stored so the
    // region-relative mapping (RegionUV / the GPU viewport aspect) can reuse it.
    protected readonly float _pixelAspect = 11.0f / 24.0f;

    public ConsoleScreenAsync() : base(Console.WindowWidth, Console.WindowHeight)
    {
        EnableVirtualTerminal();   // so 24-bit ANSI truecolor escapes are honored (no-op off-Windows)
        _charBuffer = new char[Width * Height];
        _prevChars = new char[Width * Height];
        _prevColors = new Rgb24[Width * Height];
        Console.CursorVisible = false;

        _aspectRatio = RegionAspect(Width, Height, _pixelAspect);   // full-screen aspect
    }

    // The aspect a REGION uses: its own width/height times the cell aspect. Pure + static so both the CPU
    // mapping (RegionUV) and the GPU viewport aspect derive from the same formula (kept in lockstep).
    public static float RegionAspect(int w, int h, float pixelAspect) => (float)w / h * pixelAspect;

    // Region-relative uv mapping (pure, testable): maps pixel (i,j) within region [x0,y0,w,h] to the
    // camera's FULL view uv in [-1,1] using the REGION's aspect. The full-screen region (x0=y0=0, w=Width,
    // h=Height) reproduces the old CalculateUV EXACTLY; a half-width region halves the horizontal spread.
    // The GPU kernel's region→uv math must stay in LOCKSTEP with this (the CPU↔GPU parity rule).
    public static Vector2 RegionUV(int i, int j, int x0, int y0, int w, int h, float pixelAspect)
    {
        Vector2 uv = new Vector2((float)(i - x0) / (w - 1), (float)(j - y0) / (h - 1)) * 2 - 1;
        uv.X *= RegionAspect(w, h, pixelAspect);
        uv.Y = -uv.Y;
        return uv;
    }

    protected override Vector2 CalculateUV(int i, int j) => RegionUV(i, j, 0, 0, Width, Height, _pixelAspect);

    // Pure resize decision (testable): given the CURRENT (curW,curH) and a freshly-read console size
    // (newW,newH), clamp the new size to a positive minimum (so a transient 0/negative during a window
    // drag never allocates a 0-length buffer or divides by zero), and report the clamped dims, the region
    // aspect for the new size, and whether it actually differs from the current size (needsResize). When the
    // size is unchanged this is a no-op (needsResize == false), so fixed-size renders are never perturbed.
    public static (int w, int h, float aspect, bool needsResize) ResizePlan(int curW, int curH, int newW, int newH, float pixelAspect)
    {
        int w = newW < MinDim ? MinDim : newW;
        int h = newH < MinDim ? MinDim : newH;
        bool needsResize = w != curW || h != curH;
        return (w, h, RegionAspect(w, h, pixelAspect), needsResize);
    }

    // Per-frame resize adaptation: if the console cell grid changed (window stretch OR Ctrl+scroll font
    // zoom), re-size EVERY size-dependent buffer to the new dimensions, refresh the aspect, and force a full
    // redraw next Present (the diff buffers are invalid at the new size). A no-op when the size is unchanged.
    // The GPU screen inherits RenderFrame, so this runs for both renderers; GpuRaytracer re-allocs its own
    // device output buffers by pixel count. Reading the console size is guarded (a drag can momentarily make
    // it throw) — on failure we skip the resize this frame and try again next frame.
    private void AdaptToConsoleSize()
    {
        int cw, ch;
        try { cw = Console.WindowWidth; ch = Console.WindowHeight; }
        catch { return; }   // can't read the size right now — leave the buffers as-is, re-check next frame

        var plan = ResizePlan(Width, Height, cw, ch, _pixelAspect);
        if (!plan.needsResize) return;

        ResizeBuffers(plan.w, plan.h);                 // base: Width/Height + BrightnessBuffer/ColorBuffer
        _charBuffer = new char[plan.w * plan.h];
        _prevChars = new char[plan.w * plan.h];
        _prevColors = new Rgb24[plan.w * plan.h];
        _aspectRatio = plan.aspect;
        _firstPresent = true;                          // the old prev buffers are invalid → Present writes every cell
        try { Console.Clear(); } catch { }             // wipe stale glyphs from the old (differently-sized) grid
    }

    // The per-pixel ray-cone spread (world-space footprint growth per unit ray distance) used for texture
    // mip-level selection: the average per-pixel uv step (the horizontal step carries the region aspect,
    // the vertical does not) divided by the focal length — the small-angle size of one pixel. ONE pure
    // definition so the CPU sampler (FillViewport) and the GPU kernel (via GpuGlobals.PixelCone) select the
    // SAME mip level (kept in lockstep like the texel samplers). A degenerate 1-pixel region → 0 (base level).
    public static float PixelConeSpread(int w, int h, float aspect, float focal)
    {
        if (w <= 1 || h <= 1 || focal <= 0f) return 0f;
        float duX = 2f / (w - 1) * aspect;   // horizontal uv step (aspect-scaled, matching RegionUV)
        float duY = 2f / (h - 1);            // vertical uv step
        return 0.5f * (duX + duY) / focal;
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
        // Adapt to a terminal resize (window stretch / font zoom) BEFORE filling: re-size the buffers +
        // aspect and force a full redraw so the frame fills the new grid cleanly (CPU + GPU, single + split).
        AdaptToConsoleSize();

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

        // Render each viewport (its own camera) into its screen region. The default single full-screen
        // viewport is byte-identical to the old whole-screen fill; a 2-way split tiles the width.
        foreach (var vp in scene.GetViewports(Width, Height))
            FillViewport(scene, vp, stride);
    }

    // Raytraces one viewport: the region's pixels map to the viewport camera's FULL view (region-relative
    // uv with the region's own aspect) and are written to the full-buffer index (by*Width+bx). The stride
    // block-fill and parallel-over-anchor-rows structure match FillBuffers' original single-view path.
    private void FillViewport(Scene scene, Viewport vp, int stride)
    {
        int x0 = vp.X0, y0 = vp.Y0, rw = vp.W, rh = vp.H;
        int anchorRows = (rh + stride - 1) / stride;

        // Per-pixel ray-cone spread for texture mip selection — one scalar for the whole viewport (uniform
        // uv grid), from the region dims/aspect + this camera's focal. Passed to GetPixelData → the sampler.
        // Same formula the GPU kernel uses (PixelConeSpread) so mip levels agree CPU↔GPU.
        float focal = 1f / MathF.Tan(vp.Camera.Fov * (MathF.PI / 360f));
        float cone = PixelConeSpread(rw, rh, RegionAspect(rw, rh, _pixelAspect), focal);

        // Parallelize over anchor ROWS only; each anchor casts one ray and fills its own stride×stride
        // block (clamped to the region), so every parallel iteration writes a disjoint area (no race).
        Parallel.For(0, anchorRows, jr =>
        {
            int j = y0 + jr * stride;
            int jMax = Math.Min(j + stride, y0 + rh);   // clamp the region's bottom edge (ragged blocks)

            for (int i = x0; i < x0 + rw; i += stride)
            {
                Vector2 uv = RegionUV(i, j, x0, y0, rw, rh, _pixelAspect);
                var pixelData = scene.GetPixelData(uv, vp.Camera, cone);

                int iMax = Math.Min(i + stride, x0 + rw); // clamp the region's right edge
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
