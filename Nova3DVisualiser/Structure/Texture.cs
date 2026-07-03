namespace Nova3DVisualiser;

/// <summary>
/// Per-object texture magnification filter. Nearest (default) picks the single covering texel — bit-exact
/// on CPU and GPU (the Δ=0 parity guarantee). Bilinear blends the 4 nearest texels with float lerps, so it
/// smooths blocky magnification but rounds slightly differently on CPU vs GPU (a THIN tolerated band, like
/// the sphere seam / shadow band). It is OPT-IN so all existing (nearest) content keeps its exact parity.
/// </summary>
public enum TextureFilterMode { Nearest = 0, Bilinear = 1 }

/// <summary>
/// A decoded RGBA image the raytracer samples for surface colour. PLAIN DATA + arithmetic only — no
/// third-party deps and no file I/O (PNG decoding lives in the app layer, <c>SampleGame</c>). This
/// mirrors the ColorFade split: the colour is produced at the source (here, the sampled texel) so both
/// the CPU raytracer and the GPU kernel can stay in lockstep. Nearest-neighbour + optional bilinear
/// magnification, both with WRAP (repeat) addressing — no mip-mapping (minification anti-aliasing is a
/// separate future stage).
/// </summary>
public sealed class Texture
{
    public readonly int Width;
    public readonly int Height;
    public readonly Rgba32[] Pixels;   // row-major, row 0 first; length == Width*Height
    public readonly string Name;       // source key (the world's texture file name) — kept for save-back round-trip

    public Texture(int width, int height, Rgba32[] pixels, string name = "")
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Texture dimensions must be positive.");
        if (pixels is null || pixels.Length != width * height)
            throw new ArgumentException("Texture pixel buffer length must equal width*height.");
        Width = width;
        Height = height;
        Pixels = pixels;
        Name = name ?? "";
    }

    /// <summary>
    /// Nearest-neighbour sample with WRAP (repeat) addressing: <paramref name="u"/> maps across (X),
    /// <paramref name="v"/> down (Y). Coordinates outside [0,1) wrap (…, -0.25, 0.75, 1.75, … all map to
    /// the same texel). v increases downward — row 0 is the first row of <see cref="Pixels"/>. That
    /// orientation is a fixed convention this stage; per-face UV orientation controls are a later stage.
    /// </summary>
    public Rgba32 Sample(float u, float v)
    {
        int x = WrapIndex(u, Width);
        int y = WrapIndex(v, Height);
        return Pixels[y * Width + x];
    }

    /// <summary>
    /// Bilinear-filtered sample with the SAME WRAP addressing as <see cref="Sample"/>: fetch the 4 texels
    /// at (floor(u*w), +1) × (floor(v*h), +1) and lerp by the fractional parts fu = u*w - floor(u*w),
    /// fv = v*h - floor(v*h). At a texel edge (fu=fv=0) this returns the same texel as Sample. The GPU
    /// kernel's SampleTexelBilinear replicates this math exactly (only float hardware rounding may differ →
    /// the thin tolerated band). All 4 channels (incl. alpha) interpolate so it equals Sample at fu=fv=0.
    /// </summary>
    public Rgba32 SampleBilinear(float u, float v)
    {
        float fx = u * Width, fy = v * Height;
        float flx = MathF.Floor(fx), fly = MathF.Floor(fy);
        float fu = fx - flx, fv = fy - fly;

        int x0 = (int)flx % Width;  if (x0 < 0) x0 += Width;
        int y0 = (int)fly % Height; if (y0 < 0) y0 += Height;
        int x1 = x0 + 1; if (x1 >= Width)  x1 -= Width;    // == (x0+1) wrapped, since x0 in [0,Width)
        int y1 = y0 + 1; if (y1 >= Height) y1 -= Height;

        Rgba32 t00 = Pixels[y0 * Width + x0], t10 = Pixels[y0 * Width + x1];
        Rgba32 t01 = Pixels[y1 * Width + x0], t11 = Pixels[y1 * Width + x1];

        return new Rgba32(
            BLerp(t00.R, t10.R, t01.R, t11.R, fu, fv),
            BLerp(t00.G, t10.G, t01.G, t11.G, fu, fv),
            BLerp(t00.B, t10.B, t01.B, t11.B, fu, fv),
            BLerp(t00.A, t10.A, t01.A, t11.A, fu, fv));
    }

    // Bilinear blend of 4 texel channel bytes: lerp the top pair and the bottom pair by fu, then those by
    // fv, rounding to 8-bit. The GPU kernel uses the identical expression (the parity rule).
    private static byte BLerp(byte c00, byte c10, byte c01, byte c11, float fu, float fv)
    {
        float top = c00 + (c10 - c00) * fu;
        float bot = c01 + (c11 - c01) * fu;
        float val = top + (bot - top) * fv;
        return (byte)(val + 0.5f);
    }

    // floor(t*n) reduced into [0,n) with a true modulo (handles negative t).
    private static int WrapIndex(float t, int n)
    {
        int i = (int)MathF.Floor(t * n);
        i %= n;
        if (i < 0) i += n;
        return i;
    }
}
