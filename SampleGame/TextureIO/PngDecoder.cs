using System;
using System.IO;
using System.IO.Compression;
using Nova3DVisualiser;

namespace SampleGame.Textures;

/// <summary>
/// A minimal, self-contained PNG decoder. Keeps the engine dependency-free: inflate is the BCL's
/// <see cref="ZLibStream"/> (part of .NET, not a third-party package), everything else is hand-rolled.
/// (This file lives in <c>TextureIO/</c> rather than a <c>Textures/</c> folder because the asset folder
/// is <c>textures/</c> and Windows' case-insensitive filesystem would merge the two; the namespace stays
/// <c>SampleGame.Textures</c>, mirroring the existing WorldSystem/ → SampleGame.Worlds split.)
///
/// Supports 8-bit, NON-interlaced truecolour: colour type 2 (RGB) and colour type 6 (RGBA) — the common
/// export from image editors. Any other bit depth / colour type (palette, grayscale) or interlacing is
/// rejected with a CLEAR <see cref="NotSupportedException"/>, so the caller can log + fall back to a flat
/// colour rather than silently corrupt. Chunk CRCs are not verified (ZLibStream's Adler-32 already
/// guards the pixel stream).
/// </summary>
public static class PngDecoder
{
    /// <summary>Decoded image: dimensions + a row-major RGBA pixel buffer (row 0 first).</summary>
    public readonly record struct Image(int Width, int Height, Rgba32[] Pixels);

    // Bomb / giant-allocation guard: width*height drives every downstream allocation (`expected`, the
    // pixel array), and a tiny IDAT can inflate to gigabytes. Textures in this engine are small; these
    // caps are generous. Tunable.
    public const int MaxDimension = 8192;
    public const int MaxPixels = 16_000_000;   // ~16 MP → RGBA buffer ≤ ~64 MB; bounds `expected` and the pixel array

    public static bool IsSizeValid(int width, int height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension && (long)width * height <= MaxPixels;

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static Image Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 8)
            throw new InvalidDataException("PNG: file too short.");
        for (int i = 0; i < 8; i++)
            if (bytes[i] != Signature[i])
                throw new InvalidDataException($"PNG: bad signature — {DescribeActualFormat(bytes)}");

        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        bool haveIhdr = false;
        using var idat = new MemoryStream();

        while (pos + 8 <= bytes.Length)
        {
            int len = ReadBE32(bytes, pos); pos += 4;
            if (len < 0) throw new InvalidDataException("PNG: negative chunk length.");
            string type = System.Text.Encoding.ASCII.GetString(bytes, pos, 4); pos += 4;
            if ((long)pos + len + 4 > bytes.Length)
                throw new InvalidDataException($"PNG: chunk '{type}' overruns file.");
            int dataStart = pos;

            switch (type)
            {
                case "IHDR":
                    width = ReadBE32(bytes, dataStart);
                    height = ReadBE32(bytes, dataStart + 4);
                    bitDepth = bytes[dataStart + 8];
                    colorType = bytes[dataStart + 9];
                    // bytes[+10] compression, bytes[+11] filter method (both always 0), bytes[+12] interlace
                    interlace = bytes[dataStart + 12];
                    haveIhdr = true;

                    // Reject a giant image BEFORE any large allocation or inflate (rejects non-positive too).
                    // With this cap, stride = width*channels and expected = height*(stride+1) stay well within
                    // int (expected ≤ ~64 M), so no overflow guard is needed downstream.
                    if (!IsSizeValid(width, height))
                        throw new InvalidDataException($"PNG: dimensions {width}x{height} exceed limits (max {MaxDimension}px per side, {MaxPixels} pixels total).");
                    if (bitDepth != 8)
                        throw new NotSupportedException($"PNG: unsupported bit depth {bitDepth} (only 8-bit is supported).");
                    if (colorType != 2 && colorType != 6)
                        throw new NotSupportedException($"PNG: unsupported colour type {colorType} (only 2=RGB and 6=RGBA are supported).");
                    if (interlace != 0)
                        throw new NotSupportedException("PNG: interlaced images are not supported.");
                    break;

                case "IDAT":
                    idat.Write(bytes, dataStart, len);
                    break;

                case "IEND":
                    pos = bytes.Length;   // stop scanning
                    break;
            }

            if (pos == bytes.Length) break;
            pos = dataStart + len + 4;    // advance past data + CRC (CRC not verified)
        }

        if (!haveIhdr) throw new InvalidDataException("PNG: missing IHDR.");
        if (idat.Length == 0) throw new InvalidDataException("PNG: missing IDAT.");

        int channels = colorType == 6 ? 4 : 3;   // bytes per pixel at 8-bit
        int stride = width * channels;
        int expected = height * (stride + 1);     // +1 filter byte per scanline

        byte[] raw = Inflate(idat.ToArray(), expected);
        if (raw.Length < expected)
            throw new InvalidDataException("PNG: decompressed data shorter than expected.");

        byte[] flat = Unfilter(raw, height, channels, stride);

        var pixels = new Rgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            int s = i * channels;
            byte r = flat[s], g = flat[s + 1], b = flat[s + 2];
            byte a = channels == 4 ? flat[s + 3] : (byte)255;
            pixels[i] = new Rgba32(r, g, b, a);
        }
        return new Image(width, height, pixels);
    }

    private static byte[] Inflate(byte[] zlib, int expectedLen)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        if (expectedLen <= 0) return Array.Empty<byte>();
        // Bounded: Unfilter needs exactly expectedLen bytes; read AT MOST that many and STOP — never
        // drain a decompression bomb (a tiny IDAT inflating to gigabytes). expectedLen is IHDR-capped.
        byte[] outp = new byte[expectedLen];
        int total = 0;
        while (total < expectedLen)
        {
            int n = z.Read(outp, total, expectedLen - total);
            if (n == 0) break;   // stream ended early → return short buffer; caller throws "shorter than expected"
            total += n;
        }
        if (total == expectedLen) return outp;
        byte[] shorter = new byte[total];
        Array.Copy(outp, shorter, total);
        return shorter;
    }

    // Reverse the PNG per-scanline filters (0 None, 1 Sub, 2 Up, 3 Average, 4 Paeth) into a tightly
    // packed pixel buffer with the per-line filter bytes removed. a=left, b=up, c=up-left (all 0 at edges).
    private static byte[] Unfilter(byte[] raw, int height, int bpp, int stride)
    {
        byte[] outp = new byte[height * stride];
        int inPos = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[inPos++];
            int rowStart = y * stride;
            int prevRowStart = rowStart - stride;
            for (int x = 0; x < stride; x++)
            {
                int rawVal = raw[inPos++];
                int a = x >= bpp ? outp[rowStart + x - bpp] : 0;
                int b = y > 0 ? outp[prevRowStart + x] : 0;
                int c = (y > 0 && x >= bpp) ? outp[prevRowStart + x - bpp] : 0;
                int val = filter switch
                {
                    0 => rawVal,
                    1 => rawVal + a,
                    2 => rawVal + b,
                    3 => rawVal + (a + b) / 2,
                    4 => rawVal + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG: unknown filter type {filter}."),
                };
                outp[rowStart + x] = (byte)(val & 0xFF);
            }
        }
        return outp;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static int ReadBE32(byte[] b, int i)
        => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    // Identifies a mis-named file from its magic bytes so the "bad signature" error is ACTIONABLE — the
    // usual mistake is a JPEG (or other format) renamed to .png. Only a real PNG (8-bit RGB/RGBA,
    // non-interlaced) decodes; everything else must be re-exported/converted.
    private static string DescribeActualFormat(byte[] b)
    {
        const string fix = "Re-export/convert it to a real PNG (8-bit RGB or RGBA, non-interlaced).";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return $"this file is actually a JPEG renamed to .png. {fix}";
        if (b.Length >= 3 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            return $"this file is actually a GIF renamed to .png. {fix}";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M')
            return $"this file is actually a BMP renamed to .png. {fix}";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return $"this file is actually a WebP renamed to .png. {fix}";
        return $"not a PNG file (its first bytes are not the PNG magic 89 50 4E 47). {fix}";
    }
}
