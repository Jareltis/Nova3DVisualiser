using System;
using System.Collections.Generic;
using Nova3DVisualiser.Logging;
using SampleGame.Worlds;

namespace SampleGame;

/// <summary>
/// F3 (Part B): a generated "TestLab" world — a reference showroom of correctly-authored joints (Zone J), a
/// physics playground (Zone P), and a capsule course (Zone C), so the owner can verify behaviour against
/// known-good setups and hunt bugs. Run <c>dotnet run --project SampleGame -- maketestworld</c>, then load the
/// "TestLab" world from the setup wizard.
///
/// Numbers use the REAL primitive sizes: a default cube spans ±1 → half-extent 1.0·Scale; a sphere's radius is
/// its Radius field; the player capsule is ~1.8 tall (CapsuleHalf 0.55 + CameraRadius 0.35, ×2). Joint anchors
/// are in WORLD units relative to a body's OBB centre (edge anchors use ±half-extent). BuildTestLab() is pure
/// (no I/O) so a test can validate + round-trip it; Generate() saves it and prints the zone legend.
/// </summary>
public static class TestLab
{
    public const string WorldName = "TestLab";

    private static Vec3Config V(float x, float y, float z) => new Vec3Config { X = x, Y = y, Z = z };

    /// <summary>Builds the TestLab WorldConfig programmatically (pure — no disk I/O).</summary>
    public static WorldConfig BuildTestLab()
    {
        var objects = new List<WorldObject>();
        var joints = new List<JointConfig>();
        int nextId = 1;         // object ids MUST equal list-index+1 (the world authority re-stamps them on load)
        int nextJointId = 1000; // joints live in the same id space, seeded well past the objects

        // Append an object, assigning it the next id (== its 1-based list position), and return that id.
        int Add(WorldObject o) { o.Id = nextId++; objects.Add(o); return o.Id; }
        void Joint(JointConfig j) { j.Id = nextJointId++; joints.Add(j); }

        // ===================== ZONE J — joints showroom (lane Z = -8) =====================
        // Static ANCHOR cubes (yellow) up high; dynamic bodies below; correctly-authored anchors. This is the
        // REFERENCE the owner compares their own joints against.

        // J1 PENDULUM (ballsocket): anchor block, bob hangs BELOW the pin on a visible 1.7 arm (offset anchors).
        int j1a = Add(new WorldObject { Type = "cube", Position = V(0f, 7f, -8f), Scale = 0.5f, Color = "Yellow", Gravity = false });
        int j1b = Add(new WorldObject { Type = "cube", Position = V(0f, 3.8f, -8f), Scale = 0.5f, Color = "Blue", Gravity = true });
        Joint(new JointConfig { Kind = "ballsocket", BodyA = j1a, BodyB = j1b, AnchorA = V(0f, -0.5f, 0f), AnchorB = V(0f, 1.7f, 0f) });

        // J2 ROD (distance, rigid): bob hangs at a fixed RestLength 3 below the anchor (centre anchors).
        int j2a = Add(new WorldObject { Type = "cube", Position = V(5f, 7f, -8f), Scale = 0.5f, Color = "Yellow", Gravity = false });
        int j2b = Add(new WorldObject { Type = "cube", Position = V(5f, 3f, -8f), Scale = 0.5f, Color = "Green", Gravity = true });
        Joint(new JointConfig { Kind = "distance", BodyA = j2a, BodyB = j2b, RestLength = 3f });

        // J3 SPRING (distance, soft): bob springs about RestLength 2.5 (placed exactly there — no assembly snap).
        int j3a = Add(new WorldObject { Type = "cube", Position = V(10f, 7f, -8f), Scale = 0.5f, Color = "Yellow", Gravity = false });
        int j3b = Add(new WorldObject { Type = "cube", Position = V(10f, 4.5f, -8f), Scale = 0.5f, Color = "Cyan", Gravity = true });
        Joint(new JointConfig { Kind = "distance", BodyA = j3a, BodyB = j3b, RestLength = 2.5f, SpringEnabled = true, Frequency = 2f, DampingRatio = 0.3f });

        // J4 DOOR (hinge, ±90° limit about world +Y): a post + a door pinned by their EDGES (edge anchors).
        // F4 Collide=true (door vs post): evaluated headlessly — at rest the door touches the post with zero
        // jitter and sleeps, and it still swings to its ±90° stops (the swing sweeps AWAY from the post), so the
        // door can't pass through its own post.
        int j4a = Add(new WorldObject { Type = "cube", Position = V(15f, 1.5f, -8f), Scale = 0.3f, Color = "Gray", Gravity = false });
        int j4b = Add(new WorldObject { Type = "cube", Position = V(16.2f, 1.5f, -8f), Scale = 0.6f, Color = "#FF9628", Gravity = true });
        Joint(new JointConfig { Kind = "hinge", BodyA = j4a, BodyB = j4b, Axis = V(0f, 1f, 0f),
            AnchorA = V(0.3f, 0f, 0f), AnchorB = V(-0.6f, 0f, 0f),
            LimitEnabled = true, LowerLimit = -1.57f, UpperLimit = 1.57f, Collide = true });

        // J5 WINDMILL (hinge, motor about world +Z, no limit): spins on load — an "is physics alive" beacon.
        // F5: the HUB sticks OUT of the post — the post's anchor is an out-of-plane point 1.2 in +Z in front of
        // its face (a joint anchor is just a body-frame point; it may lie outside the geometry). The blade's
        // rotation plane (about +Z, through its centre) is therefore offset 1.2 in Z from the post — clearing
        // their combined Z half-extents (0.3 + 0.8 = 1.1) by ~0.1 (>2×Slop) — so the sweep NEVER intersects the
        // post's volume (before F5 the blade swept THROUGH the post every half-turn).
        int j5a = Add(new WorldObject { Type = "cube", Position = V(20f, 5f, -8f),   Scale = 0.3f, Color = "Gray", Gravity = false });
        int j5b = Add(new WorldObject { Type = "cube", Position = V(20f, 5f, -6.8f), Scale = 0.8f, Color = "White", Gravity = true });
        Joint(new JointConfig { Kind = "hinge", BodyA = j5a, BodyB = j5b, Axis = V(0f, 0f, 1f),
            AnchorA = V(0f, 0f, 1.2f), AnchorB = V(0f, 0f, 0f),
            MotorEnabled = true, MotorTargetSpeed = 1.2f, MaxMotorTorque = 60f });

        // J6 CHAIN (three ballsockets, F4 Collide=true): links now BUMP into each other instead of folding
        // THROUGH — a real chain. The anchor offsets reach 0.01 past each link face (a ~2×Slop gap) so resting
        // links sit just CLEAR of one another (no perma-jitter from a marginal touch) but can't interpenetrate.
        int j6a  = Add(new WorldObject { Type = "cube", Position = V(27f, 8f,   -8f), Scale = 0.4f, Color = "Yellow", Gravity = false });
        int j6l1 = Add(new WorldObject { Type = "cube", Position = V(27f, 6.5f, -8f), Scale = 0.8f, Color = "Magenta", Gravity = true });
        int j6l2 = Add(new WorldObject { Type = "cube", Position = V(27f, 4.9f, -8f), Scale = 0.8f, Color = "Magenta", Gravity = true });
        int j6l3 = Add(new WorldObject { Type = "cube", Position = V(27f, 3.3f, -8f), Scale = 0.8f, Color = "Magenta", Gravity = true });
        Joint(new JointConfig { Kind = "ballsocket", BodyA = j6a,  BodyB = j6l1, AnchorA = V(0f, -0.41f, 0f), AnchorB = V(0f, 0.81f, 0f), Collide = true });
        Joint(new JointConfig { Kind = "ballsocket", BodyA = j6l1, BodyB = j6l2, AnchorA = V(0f, -0.81f, 0f), AnchorB = V(0f, 0.81f, 0f), Collide = true });
        Joint(new JointConfig { Kind = "ballsocket", BodyA = j6l2, BodyB = j6l3, AnchorA = V(0f, -0.81f, 0f), AnchorB = V(0f, 0.81f, 0f), Collide = true });

        // J7 SWING (two rigid distance joints on ONE seat): a seat cube hung from two separated static anchor
        // blocks — demonstrates MULTIPLE joints on one body + planar swinging. Both rods reach RestLength 2.5 at
        // rest (anchors 3 apart, seat 2 below the anchor line → each rod = sqrt(1.5²+2²) = 2.5).
        int j7a = Add(new WorldObject { Type = "cube", Position = V(31f, 7f, -8f), Scale = 0.3f, Color = "Gray", Gravity = false });
        int j7b = Add(new WorldObject { Type = "cube", Position = V(34f, 7f, -8f), Scale = 0.3f, Color = "Gray", Gravity = false });
        int j7s = Add(new WorldObject { Type = "cube", Position = V(32.5f, 5f, -8f), Scale = 0.6f, Color = "#66AAFF", Gravity = true });
        Joint(new JointConfig { Kind = "distance", BodyA = j7a, BodyB = j7s, RestLength = 2.5f });
        Joint(new JointConfig { Kind = "distance", BodyA = j7b, BodyB = j7s, RestLength = 2.5f });

        // J8 TRAPDOOR (люк): a HORIZONTAL-axis hinge that demonstrates limits WITHOUT any user input. A static
        // frame block + a dynamic lid hinged along their shared edge (edge anchors, ~2×Slop clearance), axis X,
        // authored CLOSED (θ = 0 = a horizontal lid extending +Z). LimitEnabled Lower 0 / Upper 1.2 (≈69°): the
        // lid's free gravitational hang is straight down (90°), so the Upper stop — set BELOW 90° — catches it
        // BEFORE vertical and gravity firmly holds it there. On load gravity flops the lid open and it comes to
        // REST at the stop (~69°, propped short of vertical) — the hanging angle IS the limit doing its job.
        // (Frame at z −8; the lid clears the frame by the ~0.02 anchor gap so a swing never jams it.)
        int j8a = Add(new WorldObject { Type = "cube", Position = V(-5f, 3f, -8f),    Scale = 0.5f, Color = "Gray", Gravity = false });
        int j8b = Add(new WorldObject { Type = "cube", Position = V(-5f, 3f, -6.98f), Scale = 0.5f, Color = "#CC7722", Gravity = true });
        Joint(new JointConfig { Kind = "hinge", BodyA = j8a, BodyB = j8b, Axis = V(1f, 0f, 0f),
            AnchorA = V(0f, 0f, 0.51f), AnchorB = V(0f, 0f, -0.51f),
            LimitEnabled = true, LowerLimit = 0f, UpperLimit = 1.2f });

        // ===================== ZONE P — physics playground (lane Z = 0) =====================
        // A 3-cube stack (rests solid); a bouncy sphere (bounces on load); a tilted ramp + a sphere that rolls
        // off; a heavy vs light pair for shove tests.
        Add(new WorldObject { Type = "cube", Position = V(0f, 1f, 0f), Scale = 1f, Color = "White",    Gravity = true });
        Add(new WorldObject { Type = "cube", Position = V(0f, 3f, 0f), Scale = 1f, Color = "Gray",     Gravity = true });
        Add(new WorldObject { Type = "cube", Position = V(0f, 5f, 0f), Scale = 1f, Color = "DarkGray", Gravity = true });
        Add(new WorldObject { Type = "sphere", Position = V(4f, 5f, 0f), Radius = 0.8f, Color = "Red", Gravity = true, Restitution = 0.8f });
        Add(new WorldObject { Type = "cube", Position = V(-4f, 1f, 0f), Rotation = V(0f, 0f, 0.4f), Scale = 1.5f, Color = "DarkCyan", Gravity = false, Collider = "obb" });
        Add(new WorldObject { Type = "sphere", Position = V(-4.6f, 4.5f, 0f), Radius = 0.6f, Color = "Yellow", Gravity = true });
        Add(new WorldObject { Type = "cube", Position = V(8f,  1f, 0f), Scale = 1f, Color = "DarkBlue", Gravity = true, Mass = 10f });   // heavy (dark)
        Add(new WorldObject { Type = "cube", Position = V(11f, 1f, 0f), Scale = 1f, Color = "#EEEEEE",  Gravity = true, Mass = 0.5f });  // light (pale)

        // ===================== ZONE C — capsule course (lane Z = 8) =====================
        // A head-height bar the ~1.8 capsule can't fit under (bottom at 1.5, below the 1.8 head); a shoulder-
        // width slalom of static cubes; a low step (a partially floor-sunk cube) it can walk onto.
        Add(new WorldObject { Type = "cube", Position = V(0f, 2f, 8f), Scale = 0.5f, Color = "DarkRed", Gravity = false });     // floating bar (bottom 1.5)
        Add(new WorldObject { Type = "cube", Position = V(-1.5f, 1f, 6f),  Scale = 0.8f, Color = "Green", Gravity = false });   // slalom
        Add(new WorldObject { Type = "cube", Position = V(1.5f,  1f, 8f),  Scale = 0.8f, Color = "Green", Gravity = false });
        Add(new WorldObject { Type = "cube", Position = V(-1.5f, 1f, 10f), Scale = 0.8f, Color = "Green", Gravity = false });
        Add(new WorldObject { Type = "cube", Position = V(0f, -0.2f, 12f), Scale = 0.8f, Color = "Blue", Gravity = false });    // low step (top ~0.6)

        // One cheap DIRECTIONAL light over the whole map.
        Add(new WorldObject { Type = "light", Position = V(11f, 20f, 2f), Color = "White", Power = 500f,
            LightKind = "directional", Direction = V(0.3f, -1f, 0.2f), ColorInfluence = 0.6f });

        return new WorldConfig
        {
            Name = WorldName,
            Graphics = new GraphicsConfig { Shadows = true, Bvh = true, ExtraLight = false, DisableCameraLight = false, Renderer = "cpu" },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0f },
            // A big square floor centred to cover all three lanes (X −4..27, Z −8..12).
            Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 24f, Color = "DarkGray", Position = V(11f, 0f, 2f) },
            Objects = objects,
            Joints = joints,
        };
    }

    /// <summary>Builds the TestLab world, saves it via the normal WorldManager path (overwriting any existing
    /// "TestLab"), and prints the zone legend. This is the impure entry point for the `maketestworld` tool.</summary>
    public static void Generate()
    {
        Logger.Init(AppPaths.LogsFolder);
        var world = BuildTestLab();
        WorldManager.Save(world);

        Console.WriteLine($"Generated world \"{WorldName}\" ({world.Objects.Count} objects, {world.Joints.Count} joints).");
        Console.WriteLine("Saved to the worlds folder — start the game and LOAD the \"TestLab\" world.");
        Console.WriteLine("Zones (walk toward -Z / 0 / +Z):");
        Console.WriteLine("  J  (Z=-8)  Joints showroom — J1 ballsocket pendulum, J2 rigid rod, J3 spring,");
        Console.WriteLine("             J4 hinge door (±90° limits), J5 hinge windmill (motor — SPINS in front of its post),");
        Console.WriteLine("             J6 3-link chain (COLLIDES — links bump instead of folding through), J7 two-rod swing,");
        Console.WriteLine("             J8 trapdoor (flops open on load + hangs at its limit — limits with NO input; left at X=-5).");
        Console.WriteLine("  P  (Z= 0)  Physics playground — a 3-cube stack, a bouncy sphere, a ramp + a sphere that rolls off,");
        Console.WriteLine("             and a heavy(dark)/light(pale) pair for shove tests.");
        Console.WriteLine("  C  (Z=+8)  Capsule course — a head-height bar you CAN'T fit under, a slalom, and a low step to walk onto.");
        Console.WriteLine("Tip: pick the J2 rod bob, toggle Gravity OFF, drag it away, toggle ON — it is PULLED back to rod length (no teleport).");
        Console.WriteLine("     Pick a J6 chain joint → the JCollide field reads On; flip it Off to see the links fold through again.");
    }
}
