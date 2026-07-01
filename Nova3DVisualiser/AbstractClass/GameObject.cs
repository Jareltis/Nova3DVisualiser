namespace Nova3DVisualiser.AbstractClass;

// Collider shape for an Object3d in the scene's collision pass: Aabb is the world-axis-aligned box
// (cheap, but loose for rotated/elongated meshes); Obb is the oriented box that rotates with the
// object, hugging its true silhouette. Spheres always collide as spheres regardless of this.
public enum ColliderShape { Aabb, Obb }

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
    // Collider shape used by the scene's collision pass (Object3d only; spheres always use a sphere).
    // Default Aabb keeps the historical world-AABB behavior for every existing object.
    public ColliderShape Collider = ColliderShape.Aabb;
    // Mass for the impulse solver: a heavier object is shoved less by a lighter one in a collision.
    // Default 1 keeps the old equal-mass behavior; a static/immovable obstacle is treated as infinite.
    public float Mass = 1f;
    // Per-object coefficient of restitution (bounciness, 0..1) for the landing + impulse solvers. A
    // NEGATIVE value means "inherit the world default" (PhysicsConfig.Restitution) — the spawn default,
    // so an object bounces exactly as before until given its own value. On contact the two bodies'
    // restitutions combine, so a bouncy ball vs a dead wall (or a springy one) behaves realistically.
    public float Restitution = -1f;
    // Per-object Coulomb friction coefficient μ (>=0) for the impulse solver. On contact the two bodies'
    // frictions combine (geometric mean), so a slick object slides where a grippy one sticks. Default 0.5.
    public float Friction = 0.5f;
    // Per-object rolling-friction coefficient (>=0): a bounded resistance to SPIN while in contact, so a
    // rolling ball slows and stops (and a tumble damps) instead of rolling forever. Small default (0.05) so
    // it never freezes legitimate motion; combined per contact (geometric mean).
    public float RollingFriction = 0.05f;

    // "Colour transparency" / PALENESS, 0..1 — independent of the alpha channel. 0 = the object's true
    // colour; 1 = washed out to white. It fades ONLY the RGB shown in shading; the alpha (which is the
    // OBJECT transparency: see-through compositing + lighter shadow) is left untouched, so a pale object
    // stays fully solid and casts a full shadow. EffectiveColor bakes it in identically on the CPU and in
    // the GPU snapshot, so the two renderers stay in lockstep (no kernel change, gputest parity holds).
    public float ColorFade = 0f;

    // The surface colour after paling: RGB lerped toward white by ColorFade, alpha preserved.
    public Rgba32 EffectiveColor
    {
        get
        {
            if (ColorFade <= 0f) return Color;
            float f = ColorFade >= 1f ? 1f : ColorFade;
            byte L(byte ch) => (byte)(ch + (255 - ch) * f + 0.5f);
            return new Rgba32(L(Color.R), L(Color.G), L(Color.B), Color.A);
        }
    }
}