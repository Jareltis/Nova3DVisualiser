namespace Nova3DVisualiser;

/// <summary>
/// A 32-bit RGBA color (0..255 per channel) for the transparency path: surfaces carry an alpha and
/// the primary ray composites lit colors front-to-back. The terminal cell has no alpha, so the final
/// output is flattened to opaque <see cref="Rgb24"/>. World-agnostic, no third-party deps.
/// </summary>
public struct Rgba32(byte r, byte g, byte b, byte a = 255)
{
    public byte R = r; public byte G = g; public byte B = b; public byte A = a;

    public static readonly Rgba32 White = new Rgba32(255, 255, 255, 255);

    public Rgb24 ToRgb24() => new Rgb24(R, G, B);
    public Vector3 ToUnit() => new Vector3(R / 255f, G / 255f, B / 255f);   // RGB only
    public float AUnit => A / 255f;

    public static Rgba32 FromUnit(Vector3 rgb, byte a = 255)
        => new Rgba32(ToByte(rgb.X), ToByte(rgb.Y), ToByte(rgb.Z), a);

    private static byte ToByte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

    public bool Equals(Rgba32 o) => R == o.R && G == o.G && B == o.B && A == o.A;
    public override bool Equals(object? o) => o is Rgba32 x && Equals(x);
    public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
    public static bool operator ==(Rgba32 a, Rgba32 b) => a.Equals(b);
    public static bool operator !=(Rgba32 a, Rgba32 b) => !a.Equals(b);
}
