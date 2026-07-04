namespace Nova3DVisualiser;

/// <summary>
/// Per-object texture minification/magnification filter, opt-in from Nearest so all existing content keeps
/// its exact parity. Nearest (default) picks the single covering texel — bit-exact on CPU and GPU (the Δ=0
/// parity guarantee). Bilinear blends the 4 nearest texels with float lerps, so it smooths blocky
/// magnification but rounds slightly differently on CPU vs GPU (a THIN tolerated band, like the sphere seam
/// / shadow band). Mipmapped adds TRILINEAR minification: it picks a mip level from a ray-cone footprint
/// estimate (see <see cref="MipLod"/>) and blends the two nearest levels (bilinear within each) — also a
/// thin tolerated band (float level-selection + blend). The sphere's analytic equirect UV is NOT mipmapped
/// this stage (its footprint is awkward); a Mipmapped sphere samples bilinear on the base level.
/// </summary>
public enum TextureFilterMode { Nearest = 0, Bilinear = 1, Mipmapped = 2 }

/// <summary>
/// A decoded RGBA image the raytracer samples for surface colour. PLAIN DATA + arithmetic only — no
/// third-party deps and no file I/O (PNG decoding lives in the app layer, <c>SampleGame</c>). This
/// mirrors the ColorFade split: the colour is produced at the source (here, the sampled texel) so both
/// the CPU raytracer and the GPU kernel can stay in lockstep. Nearest-neighbour + optional bilinear
/// magnification and optional trilinear-mipmapped minification, all with WRAP (repeat) addressing. The
/// box-filter mip chain (<see cref="Mips"/>) is generated on the CPU and uploaded to the GPU as bytes, so
/// only level SELECTION + blending are float math (a tolerated band) — the pixels themselves are identical.
/// </summary>
public sealed class Texture
{
    public readonly int Width;
    public readonly int Height;
    public readonly Rgba32[] Pixels;   // row-major, row 0 first; length == Width*Height
    public readonly string Name;       // source key (the world's texture file name) — kept for save-back round-trip

    // Grazing-incidence clamp for the mip footprint: below this the footprint would blow up, so it is
    // capped (limits the maximum stretch to 1/MipMinCos). MUST match the GPU kernel's MipMinCos constant.
    public const float MipMinCos = 0.25f;

    private Rgba32[][]? _mips;   // lazily-built box-filter chain; Mips[0] aliases Pixels

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
    public Rgba32 SampleBilinear(float u, float v) => SampleBilinearLevel(0, u, v);

    /// <summary>
    /// Bilinear sample of a specific mip LEVEL, using that level's own width/height and pixel block. Level 0
    /// is <see cref="Pixels"/>, so <c>SampleBilinearLevel(0, …)</c> equals <see cref="SampleBilinear"/>. The
    /// GPU kernel's SampleBilinearLevel replicates this exactly (per-level offset + the identical BLerp).
    /// </summary>
    public Rgba32 SampleBilinearLevel(int level, float u, float v)
    {
        Rgba32[] px = Mips[level];
        int w = MipDim(Width, level), h = MipDim(Height, level);

        float fx = u * w, fy = v * h;
        float flx = MathF.Floor(fx), fly = MathF.Floor(fy);
        float fu = fx - flx, fv = fy - fly;

        int x0 = (int)flx % w;  if (x0 < 0) x0 += w;
        int y0 = (int)fly % h;  if (y0 < 0) y0 += h;
        int x1 = x0 + 1; if (x1 >= w) x1 -= w;
        int y1 = y0 + 1; if (y1 >= h) y1 -= h;

        Rgba32 t00 = px[y0 * w + x0], t10 = px[y0 * w + x1];
        Rgba32 t01 = px[y1 * w + x0], t11 = px[y1 * w + x1];

        return new Rgba32(
            BLerp(t00.R, t10.R, t01.R, t11.R, fu, fv),
            BLerp(t00.G, t10.G, t01.G, t11.G, fu, fv),
            BLerp(t00.B, t10.B, t01.B, t11.B, fu, fv),
            BLerp(t00.A, t10.A, t01.A, t11.A, fu, fv));
    }

    /// <summary>
    /// Trilinear-filtered sample at fractional mip level <paramref name="lod"/>: bilinear within the two
    /// nearest levels (floor/ceil of lod), blended by the fractional part. At lod ≤ 0 this equals
    /// <see cref="SampleBilinear"/> (base level); at/above the coarsest level it is that level's bilinear.
    /// The GPU kernel's SampleTexelTrilinear replicates this exactly (thin tolerated band from float
    /// level-selection + blend rounding, XMath vs MathF).
    /// </summary>
    public Rgba32 SampleTrilinear(float u, float v, float lod)
    {
        int maxL = LevelCount - 1;
        if (lod < 0f) lod = 0f;
        if (lod > maxL) lod = maxL;
        int l0 = (int)MathF.Floor(lod);
        if (l0 >= maxL) return SampleBilinearLevel(maxL, u, v);

        int l1 = l0 + 1;
        float f = lod - l0;
        Rgba32 c0 = SampleBilinearLevel(l0, u, v);
        Rgba32 c1 = SampleBilinearLevel(l1, u, v);
        byte L(byte a, byte b) => (byte)(a + (b - a) * f + 0.5f);
        return new Rgba32(L(c0.R, c1.R), L(c0.G, c1.G), L(c0.B, c1.B), L(c0.A, c1.A));
    }

    /// <summary>
    /// Number of mip levels: floor(log2(max(W,H))) + 1, i.e. level 0 (full) down to a 1×1 top level.
    /// </summary>
    public int LevelCount
    {
        get
        {
            int m = Math.Max(Width, Height), n = 1;
            while (m > 1) { m >>= 1; n++; }
            return n;
        }
    }

    /// <summary>
    /// One mip level's dimension along an axis: <c>max(1, baseDim >> level)</c>. Matches the chain's
    /// iterative halving (floor, min 1) and the GPU kernel's MipDim — so a level's block dims agree exactly.
    /// </summary>
    public static int MipDim(int baseDim, int level)
    {
        int d = baseDim >> level;
        return d < 1 ? 1 : d;
    }

    /// <summary>
    /// The lazily-built box-filter mip chain: <c>Mips[0]</c> aliases <see cref="Pixels"/>; each subsequent
    /// level halves both dimensions (floor, min 1) by averaging the covered 2×2 source block (edge blocks
    /// clamp on odd dims). Built once on first access. The GPU uploads this SAME chain as bytes, so the
    /// pixels are bit-identical on both renderers (only level selection + blending are float — a thin band).
    /// </summary>
    public Rgba32[][] Mips => _mips ??= BuildMips();

    private Rgba32[][] BuildMips()
    {
        int levels = LevelCount;
        var chain = new Rgba32[levels][];
        chain[0] = Pixels;
        for (int l = 1; l < levels; l++)
        {
            int w = MipDim(Width, l), h = MipDim(Height, l);
            int pw = MipDim(Width, l - 1), ph = MipDim(Height, l - 1);
            Rgba32[] src = chain[l - 1];
            var dst = new Rgba32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2, y0 = y * 2;
                    int x1 = x0 + 1 < pw ? x0 + 1 : pw - 1;   // clamp for odd source dims
                    int y1 = y0 + 1 < ph ? y0 + 1 : ph - 1;
                    Rgba32 a = src[y0 * pw + x0], b = src[y0 * pw + x1];
                    Rgba32 c = src[y1 * pw + x0], d = src[y1 * pw + x1];
                    dst[y * w + x] = new Rgba32(
                        Avg4(a.R, b.R, c.R, d.R), Avg4(a.G, b.G, c.G, d.G),
                        Avg4(a.B, b.B, c.B, d.B), Avg4(a.A, b.A, c.A, d.A));
                }
            chain[l] = dst;
        }
        return chain;
    }

    // Rounded average of a 2×2 texel block (box filter). Integer math, so the CPU-built chain is
    // byte-identical everywhere it is used (the GPU reads these exact bytes — no filter runs on device).
    private static byte Avg4(byte a, byte b, byte c, byte d) => (byte)((a + b + c + d + 2) / 4);

    /// <summary>
    /// Fractional mip LOD (level) for a textured hit, from a ray-cone footprint estimate. DELIBERATELY
    /// SIMPLE + fully documented so the GPU kernel computes the IDENTICAL value (a thin float-rounding band
    /// tolerated, exactly like bilinear / the sphere seam). There are no screen-space UV derivatives in a
    /// raytracer, so the footprint is a scalar ray-cone estimate:
    /// <code>
    ///   footprintWorld = distance * cone / max(|incidenceCos|, MipMinCos)   // cone = per-pixel angular size
    ///   texels         = footprintWorld * texScale * width                  // assume 1 world unit ≈ 1 UV span
    ///   lod            = clamp(log2(max(texels, 1)), 0, levelCount - 1)
    /// </code>
    /// A near / head-on hit → small footprint → level 0 (crisp); a far / grazing hit → higher level
    /// (smooth). The "1 world unit ≈ 1 UV" assumption (true for the primitives' full-face UV mapping) is a
    /// stated simplification — the point is a monotone, CPU/GPU-identical level, not an exact derivative.
    /// </summary>
    public static float MipLod(int width, int levelCount, float distance, float cone, float incidenceCos, float texScale)
    {
        float cos = MathF.Abs(incidenceCos);
        if (cos < MipMinCos) cos = MipMinCos;
        float footprint = distance * cone / cos;
        float texels = footprint * texScale * width;
        if (texels < 1f) texels = 1f;
        float lod = MathF.Log2(texels);
        if (lod < 0f) lod = 0f;
        float maxLod = levelCount - 1;
        if (lod > maxLod) lod = maxLod;
        return lod;
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
