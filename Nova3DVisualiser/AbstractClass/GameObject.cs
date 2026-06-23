namespace Nova3DVisualiser.AbstractClass;

public abstract class GameObject(Vector3 position, Vector3 localRotate)
{
    public Vector3 Position = position;
    public Vector3 GlobalRotate = Vector3.Zero;
    public Vector3 LocalRotate = localRotate;
    public Rgba32 Color { get; set; } = Rgba32.White;
    // Whether this object participates in collision (camera bubble + as a support surface). Visual-only
    // objects (light markers, remote-player avatars) set this false so nothing is blocked by them.
    public bool Collides = true;
    // Whether this object is pulled down by gravity. Set from the world+per-object flags at build time
    // (false unless both the world's Gravity switch and the object's own gravity flag are on).
    public bool Gravity = false;
}