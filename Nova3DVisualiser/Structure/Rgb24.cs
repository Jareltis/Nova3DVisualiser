namespace Nova3DVisualiser;

/// <summary>
/// A 24-bit RGB color (0..255 per channel) for the truecolor output path. The screen holds these
/// per cell and emits them as 24-bit ANSI; world-agnostic, no third-party deps.
/// </summary>
public struct Rgb24(byte r, byte g, byte b)
{
    public byte R = r;
    public byte G = g;
    public byte B = b;

    public static readonly Rgb24 Black = new Rgb24(0, 0, 0);
    public static readonly Rgb24 White = new Rgb24(255, 255, 255);

    /// <summary>From a 0..1 RGB vector (clamped, rounded).</summary>
    public static Rgb24 FromUnit(Vector3 v) => new Rgb24(ToByte(v.X), ToByte(v.Y), ToByte(v.Z));

    /// <summary>To a 0..1 RGB vector.</summary>
    public Vector3 ToUnit() => new Vector3(R / 255f, G / 255f, B / 255f);

    private static byte ToByte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

    public bool Equals(Rgb24 o) => R == o.R && G == o.G && B == o.B;
    public override bool Equals(object? obj) => obj is Rgb24 o && Equals(o);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public static bool operator ==(Rgb24 a, Rgb24 b) => a.Equals(b);
    public static bool operator !=(Rgb24 a, Rgb24 b) => !a.Equals(b);
}
