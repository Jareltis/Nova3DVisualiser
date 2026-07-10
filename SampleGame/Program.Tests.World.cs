using System.Net;
using System.Runtime.InteropServices;
using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Scenes;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.IO.Compression;
using System.Text.Json;

namespace SampleGame;

partial class Program
{
    // World/editor self-tests: world round-trip, editor save-back, pick, world-sync, plus the CompareWorlds diff helper.

    // Non-interactive check that the worlds engine resolves the platform-only default
    // world and that the mesh/primitive build paths work.
    static void WorldSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== WORLD SELF-TEST ===");

        WorldManager.EnsureDefault();

        var worlds = WorldManager.ListWorlds();
        Console.WriteLine($"Worlds found: {string.Join(", ", worlds)}");

        var world = WorldManager.Load("default");
        if (world == null) { Console.WriteLine("WORLD TEST FAILED: could not load 'default'."); return; }

        Console.WriteLine($"World '{world.Name}': shadows={world.Graphics.Shadows}, bvh={world.Graphics.Bvh}, " +
                          $"extraLight={world.Graphics.ExtraLight}, disableCameraLight={world.Graphics.DisableCameraLight}");
        Console.WriteLine($"Platform: enabled={world.Platform.Enabled}, size={world.Platform.Size}, color={world.Platform.Color}");
        Console.WriteLine($"Objects: {world.Objects.Count}");

        if (!world.Platform.Enabled) { Console.WriteLine("WORLD TEST FAILED: default platform not enabled."); return; }
        if (world.Objects.Count != 0) { Console.WriteLine("WORLD TEST FAILED: default world should have 0 objects."); return; }

        // Verify the mesh build path: pick the highest-tri available mesh, load raw + build BVH.
        var meshes = WorldManager.ListAvailableMeshes();
        if (meshes.Count == 0) { Console.WriteLine("WORLD TEST FAILED: no meshes available to verify build path."); return; }

        string best = "";
        Object3d? bestMesh = null;
        foreach (var name in meshes)
        {
            var m = ModelLoader.LoadRawMesh(AppPaths.ModelsFolder, name);
            if (m == null) continue;
            m.BuildAcceleration();
            if (bestMesh == null || m.FaceCount > bestMesh.FaceCount) { best = name; bestMesh = m; }
        }
        if (bestMesh == null) { Console.WriteLine("WORLD TEST FAILED: no mesh resolved."); return; }
        Console.WriteLine($"Mesh build OK: '{best}' -> {bestMesh.FaceCount} triangles, bvh={bestMesh.HasBvh}");

        // Confirm a primitive builds.
        var sphere = new Sphere(new Vector3(0, 0, 0), Vector3.Zero, 1f);
        Console.WriteLine($"Sphere build OK: r={sphere.R}");

        // Backward-compat: the on-disk default world has no Shape, so it loads as "square".
        if (world.Platform.Shape != "square")
        { Console.WriteLine($"WORLD TEST FAILED: default platform shape '{world.Platform.Shape}' != 'square'."); return; }

        // Round-trip the new platform fields (Shape + Width/Depth) through the world JSON
        // (in-memory, same options WorldManager uses for save/load — no test artifact on disk).
        var shaped = new WorldConfig
        {
            Name = "platshapetest",
            Platform = new PlatformConfig { Enabled = true, Shape = "rectangle", Size = 9f, Width = 14f, Depth = 6f, Color = "Cyan",
                Position = new Vec3Config { X = 3f, Y = 1f, Z = -2f } },
        };
        string shapedJson = System.Text.Json.JsonSerializer.Serialize(shaped, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>(shapedJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (reloaded == null) { Console.WriteLine("WORLD TEST FAILED: could not deserialize round-tripped world."); return; }
        var rp = reloaded.Platform;
        if (rp.Shape != "rectangle" || Math.Abs(rp.Size - 9f) > 1e-4f ||
            Math.Abs(rp.Width - 14f) > 1e-4f || Math.Abs(rp.Depth - 6f) > 1e-4f ||
            Math.Abs(rp.Position.X - 3f) > 1e-4f || Math.Abs(rp.Position.Y - 1f) > 1e-4f || Math.Abs(rp.Position.Z + 2f) > 1e-4f)
        { Console.WriteLine($"WORLD TEST FAILED: platform fields did not round-trip (shape={rp.Shape}, size={rp.Size}, w={rp.Width}, d={rp.Depth}, pos=({rp.Position.X},{rp.Position.Y},{rp.Position.Z}))."); return; }
        Console.WriteLine($"Platform round-trip OK: shape={rp.Shape}, size={rp.Size}, w={rp.Width}, d={rp.Depth}, pos=({rp.Position.X},{rp.Position.Y},{rp.Position.Z})");

        // Build a rectangle and a circle platform: each must produce real geometry.
        var rect = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "rectangle", Width = 14f, Depth = 6f });
        var disc = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "circle", Size = 8f });
        var square = PriviewNetworkScene.CreatePlatform(new PlatformConfig { Shape = "square", Size = 10f });
        Console.WriteLine($"Platform geometry: square faces={square.FaceCount}, rectangle faces={rect.FaceCount}, circle faces={disc.FaceCount}");
        if (rect.FaceCount <= 0 || disc.FaceCount <= 0 || square.FaceCount <= 0)
        { Console.WriteLine("WORLD TEST FAILED: a platform shape built with no faces."); return; }

        // Ramp (wedge) primitive: a triangular prism — bottom(2) + slope(2) + back wall(2) + 2 end caps = 8 faces.
        var ramp = PriviewNetworkScene.CreateRamp();
        Console.WriteLine($"Ramp geometry: faces={ramp.FaceCount} (want 8)");
        if (ramp.FaceCount != 8) { Console.WriteLine($"WORLD TEST FAILED: ramp should have 8 faces, got {ramp.FaceCount}."); return; }

        // C1-4a — the world's Joints list round-trips through the same JSON options WorldManager uses (in-memory,
        // no disk artifact): one of each kind (ballsocket dyn↔dyn, hinge dyn↔world with limit+motor, distance
        // dyn↔world spring). Assert every field of each joint survives; and an OLD world with NO "joints" key
        // loads as an EMPTY list (backward compatible — old worlds unchanged).
        {
            var jw = new WorldConfig
            {
                Name = "jointrt",
                Joints = new List<JointConfig>
                {
                    new JointConfig { Kind = "ballsocket", BodyA = 0, BodyB = 1,
                        AnchorA = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                        AnchorB = new Vec3Config { X = -0.4f, Y = 0.5f, Z = -0.6f } },
                    new JointConfig { Kind = "hinge", BodyA = 2, BodyB = -1,
                        AnchorA = new Vec3Config { X = 1f, Y = 0f, Z = 0f }, AnchorB = new Vec3Config { X = 0f, Y = 3f, Z = 0f },
                        Axis = new Vec3Config { X = 0f, Y = 0f, Z = 1f },
                        LimitEnabled = true, LowerLimit = -1.2f, UpperLimit = 0.8f,
                        MotorEnabled = true, MotorTargetSpeed = 2.5f, MaxMotorTorque = 15f },
                    new JointConfig { Kind = "distance", BodyA = 3, BodyB = -1,
                        AnchorB = new Vec3Config { X = 0f, Y = 5f, Z = 0f },
                        RestLength = 2.5f, SpringEnabled = true, Frequency = 3f, DampingRatio = 0.4f },
                },
            };
            string jjson = System.Text.Json.JsonSerializer.Serialize(jw, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var jrt = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>(jjson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (jrt == null || jrt.Joints.Count != 3)
            { Console.WriteLine($"WORLD TEST FAILED: joints round-trip count={jrt?.Joints.Count} (want 3)."); return; }

            var ja = jrt.Joints[0]; var jb = jrt.Joints[1]; var jc = jrt.Joints[2];
            bool jaOk = ja.Kind == "ballsocket" && ja.BodyA == 0 && ja.BodyB == 1
                && Math.Abs(ja.AnchorA.X - 0.1f) < 1e-4f && Math.Abs(ja.AnchorA.Y - 0.2f) < 1e-4f && Math.Abs(ja.AnchorA.Z - 0.3f) < 1e-4f
                && Math.Abs(ja.AnchorB.X + 0.4f) < 1e-4f && Math.Abs(ja.AnchorB.Y - 0.5f) < 1e-4f && Math.Abs(ja.AnchorB.Z + 0.6f) < 1e-4f;
            bool jbOk = jb.Kind == "hinge" && jb.BodyA == 2 && jb.BodyB == -1
                && Math.Abs(jb.AnchorA.X - 1f) < 1e-4f && Math.Abs(jb.AnchorB.Y - 3f) < 1e-4f && Math.Abs(jb.Axis.Z - 1f) < 1e-4f
                && jb.LimitEnabled && Math.Abs(jb.LowerLimit + 1.2f) < 1e-4f && Math.Abs(jb.UpperLimit - 0.8f) < 1e-4f
                && jb.MotorEnabled && Math.Abs(jb.MotorTargetSpeed - 2.5f) < 1e-4f && Math.Abs(jb.MaxMotorTorque - 15f) < 1e-4f;
            bool jcOk = jc.Kind == "distance" && jc.BodyA == 3 && jc.BodyB == -1
                && Math.Abs(jc.AnchorB.Y - 5f) < 1e-4f && Math.Abs(jc.RestLength - 2.5f) < 1e-4f
                && jc.SpringEnabled && Math.Abs(jc.Frequency - 3f) < 1e-4f && Math.Abs(jc.DampingRatio - 0.4f) < 1e-4f;
            Console.WriteLine($"  joints round-trip: ballsocket={jaOk}, hinge={jbOk}, distance={jcOk}");
            if (!(jaOk && jbOk && jcOk)) { Console.WriteLine("WORLD TEST FAILED: a joint field did not round-trip."); return; }

            var oldw = System.Text.Json.JsonSerializer.Deserialize<WorldConfig>("{\"Name\":\"old\"}",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            bool emptyOk = oldw != null && oldw.Joints != null && oldw.Joints.Count == 0;
            Console.WriteLine($"  joints backward-compat: no-joints-key -> empty list = {emptyOk}");
            if (!emptyOk) { Console.WriteLine("WORLD TEST FAILED: old world without a joints key did not load empty."); return; }
        }

        Console.WriteLine("WORLD TEST PASSED");
    }

    // Non-interactive round-trip of the editor's save-back conversion (FromInstance):
    // build a cube object from a descriptor, mutate its live Position, convert back, and
    // assert the result reflects the move while preserving Type/Color/Scale.
    static void EditorSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== EDITOR SELF-TEST ===");

        var descriptor = new WorldObject
        {
            Type = "cube",
            Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
            Scale = 2f,
            Color = "Magenta",
        };

        // Build the live instance the same way the scene does.
        Object3d cube = PriviewNetworkScene.CreateCube();
        cube.Position = new Vector3(descriptor.Position.X, descriptor.Position.Y, descriptor.Position.Z);
        cube.Scale = descriptor.Scale;
        cube.Color = PriviewNetworkScene.ParseColor(descriptor.Color, Rgba32.White);

        // Mutate the instance across every editable property (as the panel would).
        cube.Position += new Vector3(5f, 0f, 0f);
        cube.LocalRotate = new Vector3(0.25f, 0.5f, -0.75f);
        cube.Scale = 3.5f;
        cube.RotateSpeed = 1.25f;
        cube.Color = PriviewNetworkScene.ParseColor("Green", Rgba32.White);

        WorldObject back = PriviewNetworkScene.FromInstance(descriptor, cube);

        Console.WriteLine($"Back: type={back.Type}, pos=({back.Position.X},{back.Position.Y},{back.Position.Z}), " +
                          $"rot=({back.Rotation.X},{back.Rotation.Y},{back.Rotation.Z}), scale={back.Scale}, spin={back.RotateSpeed}, color={back.Color}");

        bool ok =
            back.Type == "cube" &&
            back.Mesh == null &&
            Math.Abs(back.Position.X - 6f) < 1e-4f &&
            Math.Abs(back.Position.Y - 2f) < 1e-4f &&
            Math.Abs(back.Position.Z - 3f) < 1e-4f &&
            Math.Abs(back.Rotation.X - 0.25f) < 1e-4f &&
            Math.Abs(back.Rotation.Y - 0.5f) < 1e-4f &&
            Math.Abs(back.Rotation.Z + 0.75f) < 1e-4f &&
            Math.Abs(back.Scale - 3.5f) < 1e-4f &&
            Math.Abs(back.RotateSpeed - 1.25f) < 1e-4f &&
            PriviewNetworkScene.ParseColor(back.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Green", Rgba32.White);

        // Full-RGBA round-trip: a NON-palette color WITH non-opaque alpha must survive the
        // Rgba32 -> hex -> Rgba32 path (alpha via "#C8327B80"), proving arbitrary 24-bit color + alpha
        // persists (not just named presets). Opaque colors must still emit "#RRGGBB" (no alpha byte).
        {
            cube.Color = new Rgba32(200, 50, 123, 128);
            WorldObject rgbBack = PriviewNetworkScene.FromInstance(descriptor, cube);
            bool rgbOk = PriviewNetworkScene.ParseColor(rgbBack.Color, Rgba32.White) == new Rgba32(200, 50, 123, 128);

            cube.Color = new Rgba32(200, 50, 123);   // opaque (A=255) -> "#RRGGBB", no alpha byte
            WorldObject opqBack = PriviewNetworkScene.FromInstance(descriptor, cube);
            bool opqOk = opqBack.Color == "#C8327B" &&
                         PriviewNetworkScene.ParseColor(opqBack.Color, Rgba32.White) == new Rgba32(200, 50, 123);

            Console.WriteLine($"  full-rgba: (200,50,123,128) -> hex={rgbBack.Color}; opaque -> {opqBack.Color} -> {(rgbOk && opqOk ? "ok" : "BAD")}");
            ok &= rgbOk && opqOk;
        }

        // Cover the generated primitives: each builds with geometry, and FromInstance round-trips
        // its Type/transform/color exactly like the cube (they ride the same editor/save/sync path).
        foreach (var (type, prim) in new (string, Object3d)[]
        {
            ("cylinder", PriviewNetworkScene.CreateCylinder()),
            ("cone",     PriviewNetworkScene.CreateCone()),
            ("pyramid",  PriviewNetworkScene.CreatePyramid()),
        })
        {
            var desc = new WorldObject { Type = type, Color = "White" };
            prim.Position = new Vector3(4f, 5f, 6f);
            prim.LocalRotate = new Vector3(0.1f, 0.2f, 0.3f);
            prim.Scale = 1.5f;
            prim.RotateSpeed = 0.7f;
            prim.Color = PriviewNetworkScene.ParseColor("Red", Rgba32.White);

            WorldObject b = PriviewNetworkScene.FromInstance(desc, prim);
            bool pok =
                prim.FaceCount > 0 &&
                b.Type == type && b.Mesh == null &&
                Math.Abs(b.Position.X - 4f) < 1e-4f && Math.Abs(b.Position.Y - 5f) < 1e-4f && Math.Abs(b.Position.Z - 6f) < 1e-4f &&
                Math.Abs(b.Rotation.X - 0.1f) < 1e-4f && Math.Abs(b.Rotation.Y - 0.2f) < 1e-4f && Math.Abs(b.Rotation.Z - 0.3f) < 1e-4f &&
                Math.Abs(b.Scale - 1.5f) < 1e-4f && Math.Abs(b.RotateSpeed - 0.7f) < 1e-4f &&
                PriviewNetworkScene.ParseColor(b.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Red", Rgba32.White);

            Console.WriteLine($"  {type}: faces={prim.FaceCount}, back type={b.Type}, scale={b.Scale}, color={b.Color} -> {(pok ? "ok" : "BAD")}");
            ok &= pok;
        }

        // Cover the new "light" object end-to-end through a real (offline) scene, one entry per
        // Kind: Start() builds a visible marker (FaceCount > 0) AND an engine Light of the right
        // Kind, paired in one EditEntry; FromInstance round-trips every light field (kind, direction,
        // cone, area size, power, position). (Platform off so the lights are the only entries.)
        {
            var lightWorld = new WorldConfig
            {
                Name = "lighttest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "point", Color = "White",
                        Position = new Vec3Config { X = 2f, Y = 3f, Z = -1f }, Power = 750f },
                    new WorldObject { Id = 1, Type = "light", LightKind = "directional", Color = "Cyan",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSpin = 0.5f },
                    new WorldObject { Id = 2, Type = "light", LightKind = "spot", Color = "Red",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 900f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 18f,
                        BeamCount = 4, ConeShape = "square" },
                    new WorldObject { Id = 3, Type = "light", LightKind = "area", Color = "Green",
                        Position = new Vec3Config { X = 1f, Y = 3f, Z = 2f }, Power = 600f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 2f },
                },
            };

            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), lightWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();

            var lightEntries = scene.EditableEntries.Where(e => e.Light != null).ToList();
            bool lok = lightEntries.Count == 4;
            if (!lok) Console.WriteLine($"  light: expected 4 light entries, got {lightEntries.Count} -> BAD");

            var expectKinds = new[] { LightKind.Point, LightKind.Directional, LightKind.Spot, LightKind.Area };
            for (int i = 0; lok && i < lightEntries.Count; i++)
            {
                var e = lightEntries[i];
                var orig = lightWorld.Objects[i];                 // the descriptor we fed in (round-trip target)
                var marker = e.Instance as Object3d;
                float wantInfluence = 0.05f + 0.2f * i;           // mutate per-light ColorInfluence; must round-trip
                e.Light!.ColorInfluence = wantInfluence;
                WorldObject lb = PriviewNetworkScene.FromInstance(e.Descriptor, e.Instance, e.Light);
                bool one =
                    marker != null && marker.FaceCount > 0 &&                       // marker is visible
                    e.Light!.Kind == expectKinds[i] &&                              // scene gained a Light of this kind
                    lb.Type == "light" && lb.Mesh == null &&
                    lb.LightKind == orig.LightKind &&                              // kind round-trips
                    PriviewNetworkScene.ParseColor(lb.Color, Rgba32.White) == PriviewNetworkScene.ParseColor(orig.Color, Rgba32.White) &&   // color round-trips
                    Math.Abs(lb.Direction.X - orig.Direction.X) < 1e-4f &&         // direction round-trips (already unit)
                    Math.Abs(lb.Direction.Y - orig.Direction.Y) < 1e-4f &&
                    Math.Abs(lb.Direction.Z - orig.Direction.Z) < 1e-4f &&
                    Math.Abs(lb.ConeAngle - orig.ConeAngle) < 1e-4f &&             // cone round-trips
                    Math.Abs(lb.LightSize - orig.LightSize) < 1e-4f &&            // area size round-trips
                    lb.BeamCount == orig.BeamCount &&                              // beam count round-trips
                    e.Light!.BeamCount == orig.BeamCount &&                        // ...and reached the engine Light
                    lb.ConeShape == orig.ConeShape &&                             // cone shape round-trips
                    Math.Abs(lb.Power - orig.Power) < 1e-4f &&                    // power round-trips
                    Math.Abs(lb.ColorInfluence - wantInfluence) < 1e-4f &&         // color-influence round-trips
                    Math.Abs(lb.Position.X - orig.Position.X) < 1e-4f &&          // marker position round-trips
                    Math.Abs(lb.Position.Y - orig.Position.Y) < 1e-4f &&
                    Math.Abs(lb.Position.Z - orig.Position.Z) < 1e-4f;
                Console.WriteLine($"  light[{i}] kind={e.Light!.Kind}: marker faces={marker?.FaceCount}, back kind={lb.LightKind}, cone={lb.ConeAngle}, size={lb.LightSize}, power={lb.Power} -> {(one ? "ok" : "BAD")}");
                lok &= one;
            }
            ok &= lok;
        }

        // Area emitter shapes — one Area light per AreaShape (circle/square/triangle): each builds a
        // visible flat marker (FaceCount > 0) and its AreaShape round-trips through FromInstance.
        {
            var areaWorld = new WorldConfig
            {
                Name = "areashapetest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "circle" },
                    new WorldObject { Id = 1, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "square" },
                    new WorldObject { Id = 2, Type = "light", LightKind = "area", Color = "White",
                        Position = new Vec3Config { X = 2f, Y = 4f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, LightSize = 1.5f, AreaShape = "triangle" },
                },
            };

            var areaScene = new PriviewNetworkScene(new DisplayManagerAsync(), areaWorld, isServer: false, "127.0.0.1", 0, online: false);
            areaScene.Start();
            var areaEntries = areaScene.EditableEntries.Where(e => e.Light != null).ToList();
            bool aok = areaEntries.Count == 3;
            if (!aok) Console.WriteLine($"  area-shape: expected 3 area entries, got {areaEntries.Count} -> BAD");
            var expectShapes = new[] { "circle", "square", "triangle" };
            for (int i = 0; aok && i < areaEntries.Count; i++)
            {
                var e = areaEntries[i];
                var marker = e.Instance as Object3d;
                WorldObject ab = PriviewNetworkScene.FromInstance(e.Descriptor, e.Instance, e.Light);
                bool one = marker != null && marker.FaceCount > 0 &&
                           e.Light!.AreaShape == areaWorld.Objects[i].AreaShape switch { "circle" => ConeShapeKind.Circle, "triangle" => ConeShapeKind.Triangle, _ => ConeShapeKind.Square } &&
                           ab.AreaShape == expectShapes[i];   // shape round-trips
                Console.WriteLine($"  area-shape[{i}] {expectShapes[i]}: marker faces={marker?.FaceCount}, back AreaShape={ab.AreaShape} -> {(one ? "ok" : "BAD")}");
                aok &= one;
            }
            ok &= aok;
        }

        // Marker reflects BeamCount + Directional shaped cone — a 4-beam spot bakes 4 cones into its
        // marker (≈4x the faces of a 1-beam spot, same ConeShape); a Directional builds a shaped cone
        // marker per ConeShape. All markers must be non-empty.
        {
            var markerWorld = new WorldConfig
            {
                Name = "markertest",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "spot", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = -4f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 20f, ConeShape = "circle", BeamCount = 1 },
                    new WorldObject { Id = 1, Type = "light", LightKind = "spot", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 500f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeAngle = 20f, ConeShape = "circle", BeamCount = 4 },
                    new WorldObject { Id = 2, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = -4f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "circle" },
                    new WorldObject { Id = 3, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = 0f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "square" },
                    new WorldObject { Id = 4, Type = "light", LightKind = "directional", Color = "White",
                        Position = new Vec3Config { X = 4f, Y = 5f, Z = 4f }, Power = 400f,
                        Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f }, ConeShape = "triangle" },
                },
            };

            var mScene = new PriviewNetworkScene(new DisplayManagerAsync(), markerWorld, isServer: false, "127.0.0.1", 0, online: false);
            mScene.Start();
            var mEntries = mScene.EditableEntries.Where(e => e.Light != null).ToList();
            bool mok = mEntries.Count == 5;
            if (!mok) Console.WriteLine($"  marker: expected 5 light entries, got {mEntries.Count} -> BAD");
            if (mok)
            {
                int spot1 = (mEntries[0].Instance as Object3d)!.FaceCount;
                int spot4 = (mEntries[1].Instance as Object3d)!.FaceCount;
                bool beamOk = spot1 > 0 && spot4 > 0 && spot4 > spot1;
                Console.WriteLine($"  marker: spot Beams=1 faces={spot1}, Beams=4 faces={spot4} (want 4>1, both>0) -> {(beamOk ? "ok" : "BAD")}");
                mok &= beamOk;

                foreach (var (idx, name) in new[] { (2, "circle"), (3, "square"), (4, "triangle") })
                {
                    int faces = (mEntries[idx].Instance as Object3d)!.FaceCount;
                    bool dok = faces > 0;
                    Console.WriteLine($"  marker: directional {name} faces={faces} (want >0) -> {(dok ? "ok" : "BAD")}");
                    mok &= dok;
                }
            }
            ok &= mok;
        }

        // Headless multi-beam check — a spot with BeamCount=4 fans four cones about the aim, so a
        // point sitting squarely in a FANNED (non-primary) beam is lit with Beams=4 but dark with
        // Beams=1 (the lone primary cone misses it). Aim +X (horizontal -> fans about world Y); the
        // k=1 beam points -Z, so a point straight down -Z from the light lands dead-center in it.
        {
            var mgr = new DisplayManagerAsync();
            var none = new List<IDisplays>();
            var P = new Vector3(0f, 0f, -3f);                                   // on the k=1 (-Z) fanned beam axis
            var rd = new RenderData(0f, (new Vector3(0f, 0f, 0f) - P).Norm(), P, Rgba32.White);
            var spot = new Light(new Vector3(0f, 0f, 0f), 500f)
            { Kind = LightKind.Spot, Direction = new Vector3(1f, 0f, 0f), ConeAngleDeg = 30f };

            spot.BeamCount = 1; float b1 = spot.Contribution(rd, none, mgr, shadows: false);
            spot.BeamCount = 4; float b4 = spot.Contribution(rd, none, mgr, shadows: false);
            bool bok = b1 == 0f && b4 > 0f;
            Console.WriteLine($"  beams: fanned point Beams=1 -> {b1:F3} (want 0), Beams=4 -> {b4:F3} (want >0) -> {(bok ? "ok" : "BAD")}");
            ok &= bok;
        }

        // Headless cone-shape check — a SQUARE cross-section lights its corners; the inscribed CIRCLE
        // does not. Aim +X, half-angle 30deg (t=tan30); a point at normalized cross-section offset
        // (0.8t, 0.8t) sits inside the square but past the circle's edge (sqrt(2)*0.8t > t).
        {
            var mgr = new DisplayManagerAsync();
            var none = new List<IDisplays>();
            float t = MathF.Tan(30f * MathF.PI / 180f);
            var P = new Vector3(1f, -0.8f * t, 0.8f * t);                       // u=+Z, v=-Y at axial 1
            var rd = new RenderData(0f, (new Vector3(0f, 0f, 0f) - P).Norm(), P, Rgba32.White);
            var spot = new Light(new Vector3(0f, 0f, 0f), 500f)
            { Kind = LightKind.Spot, Direction = new Vector3(1f, 0f, 0f), ConeAngleDeg = 30f, BeamCount = 1 };

            spot.ConeShape = ConeShapeKind.Circle; float circ = spot.Contribution(rd, none, mgr, shadows: false);
            spot.ConeShape = ConeShapeKind.Square; float sq = spot.Contribution(rd, none, mgr, shadows: false);
            bool sok = circ == 0f && sq > 0f;
            Console.WriteLine($"  shape: corner point Circle -> {circ:F3} (want 0), Square -> {sq:F3} (want >0) -> {(sok ? "ok" : "BAD")}");
            ok &= sok;
        }

        // Headless dominance/shadow check — the core multi-light fix. A surface point blocked from
        // light A but reached by an unblocked light B on the OTHER side must still be lit by B: each
        // light is shadow-tested independently and contributes additively (no global/min/multiplied
        // shadow factor). A occluded -> 0; B clear -> > 0; their sum stays > 0.
        {
            var mgr = new DisplayManagerAsync();
            var occluder = new Sphere(new Vector3(-2f, 2f, 0f), Vector3.Zero, 1f);  // sits between P and light A
            var objs = new List<IDisplays> { occluder };

            var rd = new RenderData(0f, new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 0f), Rgba32.White);  // up-facing point at origin
            var lightA = new Light(new Vector3(-4f, 4f, 0f), 500f);  // line of sight blocked by the sphere
            var lightB = new Light(new Vector3( 4f, 4f, 0f), 500f);  // clear line of sight

            float a = lightA.Contribution(rd, objs, mgr, shadows: true);
            float b = lightB.Contribution(rd, objs, mgr, shadows: true);

            bool dok = a == 0f && b > 0f && (a + b) > 0f;
            Console.WriteLine($"  dominance: blocked A={a:F3} (want 0), reached B={b:F3} (want >0), sum={(a + b):F3} -> {(dok ? "ok" : "BAD")}");
            ok &= dok;
        }

        // FindSortedIntersections — two spheres at different depths on one +X ray, passed in REVERSE
        // (far first); the manager must return them front-to-back (nearest Intersection first).
        {
            var mgr = new DisplayManagerAsync();
            var near = new Sphere(new Vector3(5f, 0f, 0f), Vector3.Zero, 1f);   // hit at ~4
            var far  = new Sphere(new Vector3(10f, 0f, 0f), Vector3.Zero, 1f);  // hit at ~9
            var objs = new List<IDisplays> { far, near };                       // reversed on purpose
            var ray = new Ray(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f).Norm());

            var hits = mgr.FindSortedIntersections(ray, objs);
            bool sok = hits.Count == 2 && hits[0].Intersection < hits[1].Intersection;
            Console.WriteLine($"  sorted: {hits.Count} hits, depths {(hits.Count > 0 ? hits[0].Intersection.ToString("F2") : "-")} < {(hits.Count > 1 ? hits[1].Intersection.ToString("F2") : "-")} (front-to-back) -> {(sok ? "ok" : "BAD")}");
            ok &= sok;
        }

        // Color round-trip — the BLUE/Z channel must survive ConsoleColor->RGB->Rgb24 (regression guard).
        {
            Vector3 blue = ColorRgb.ToRgb(ConsoleColor.Blue);
            Rgb24 unitBlue = Rgb24.FromUnit(new Vector3(0f, 0f, 1f));
            Rgb24 white = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.White));
            Rgb24 cyan = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.Cyan));
            Rgb24 magenta = Rgb24.FromUnit(ColorRgb.ToRgb(ConsoleColor.Magenta));
            Console.WriteLine($"  color: ToRgb(Blue)=({blue.X:F2},{blue.Y:F2},{blue.Z:F2}); unitBlue=({unitBlue.R},{unitBlue.G},{unitBlue.B}); white=({white.R},{white.G},{white.B}); cyan=({cyan.R},{cyan.G},{cyan.B}); magenta=({magenta.R},{magenta.G},{magenta.B})");
            bool cok =
                blue.Z > 0.99f && blue.X < 0.01f && blue.Y < 0.01f &&
                unitBlue.R == 0 && unitBlue.G == 0 && unitBlue.B == 255 &&
                white.R == 255 && white.G == 255 && white.B == 255 &&
                cyan.R == 0 && cyan.G == 255 && cyan.B == 255 &&
                magenta.R == 255 && magenta.G == 0 && magenta.B == 255;
            Console.WriteLine($"  color: blue/Z survives RGB->Rgb24 -> {(cok ? "ok" : "BAD")}");
            ok &= cok;
        }

        // SurfaceTint combine — a colored light shows on a BLUE-LESS surface. Replicates Scene's new
        // hybrid combine (defaults SurfaceTint=0.4, Ambient=0.1, Exposure=0.05) on a yellow surface
        // (albedo (1,1,0), blue=0) lit by a pure-blue light: old pure-multiplicative blue was 0; the
        // hybrid lets blue through (> 0).
        {
            Vector3 albedo = ColorRgb.ToRgb(ConsoleColor.Yellow);   // (1,1,0): blue channel = 0
            Vector3 accum = new Vector3(0f, 0f, 10f);               // pure-blue light contribution
            const float tint = 0.4f, amb = 0.1f, exp = 0.05f;       // Scene defaults
            float oldBlue = albedo.Z * (amb + accum.Z * exp);       // pure multiplicative -> 0
            float tB = 1f - tint * (1f - albedo.Z);
            float newBlue = albedo.Z * amb + tB * accum.Z * exp;    // hybrid -> > 0
            bool stok = oldBlue == 0f && newBlue > 0f;
            Console.WriteLine($"  surfacetint: yellow surface + blue light -> oldBlue={oldBlue:F3} (was 0), newBlue={newBlue:F3} (>0) -> {(stok ? "ok" : "BAD")}");
            ok &= stok;
        }

        // Part B — the settings "extra light" injects as exactly ONE real editable light object on the
        // authority/solo, and is idempotent (a world that already has a light is not doubled).
        {
            var injWorld = new WorldConfig
            {
                Name = "extralight-inject",
                Graphics = new GraphicsConfig { ExtraLight = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>(),   // no lights yet -> one should be injected
            };
            var s1 = new PriviewNetworkScene(new DisplayManagerAsync(), injWorld, isServer: false, "127.0.0.1", 0, online: false);
            s1.Start();
            var le = s1.EditableEntries.FirstOrDefault(e => e.Light != null);
            int lights1 = s1.EditableEntries.Count(e => e.Light != null);
            bool injOk = lights1 == 1 && le != null && (le.Instance as Object3d)?.FaceCount > 0;
            Console.WriteLine($"  extralight-inject: light entries={lights1} (want 1), marker faces={(le?.Instance as Object3d)?.FaceCount} -> {(injOk ? "ok" : "BAD")}");
            ok &= injOk;

            // Idempotent: ExtraLight on, but the world already contains a light -> no second injection.
            var dupWorld = new WorldConfig
            {
                Name = "extralight-idempotent",
                Graphics = new GraphicsConfig { ExtraLight = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Type = "light", LightKind = "point", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f }, Power = 500f },
                },
            };
            var s2 = new PriviewNetworkScene(new DisplayManagerAsync(), dupWorld, isServer: false, "127.0.0.1", 0, online: false);
            s2.Start();
            int lights2 = s2.EditableEntries.Count(e => e.Light != null);
            bool dupOk = lights2 == 1;
            Console.WriteLine($"  extralight-idempotent: light entries={lights2} (want 1, no double) -> {(dupOk ? "ok" : "BAD")}");
            ok &= dupOk;
        }

        // Part C — the PLATFORM is an editable entry (selectable like any object), backed by the live
        // PlatformConfig, but never leaks into the saved/synced Objects list. An offline scene with a
        // known platform + 2 ordinary objects: exactly one "platform" entry (a real Object3d floor);
        // the non-platform entries match world.Objects; the live snapshot carries the platform via
        // Platform (NOT Objects); and a live Shape edit propagates to the snapshot (save/sync path).
        {
            var platWorld = new WorldConfig
            {
                Name = "platedit",
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 7f, Color = "Cyan" },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "cube", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                    new WorldObject { Id = 1, Type = "sphere", Color = "Red", Radius = 1f,
                        Position = new Vec3Config { X = 3f, Y = 0f, Z = 0f } },
                },
            };

            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), platWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();

            var platEntries = scene.EditableEntries.Where(e => e.Platform != null).ToList();
            int platTyped = scene.EditableEntries.Count(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            var platEntry = platEntries.FirstOrDefault();
            var platFloor = platEntry?.Instance as Object3d;
            bool pCount = platEntries.Count == 1 && platTyped == 1 && platEntry != null && platFloor != null && platFloor.FaceCount > 0;
            Console.WriteLine($"  platform: editable entries={platEntries.Count} (want 1), typed=platform={platTyped}, floor faces={platFloor?.FaceCount} -> {(pCount ? "ok" : "BAD")}");
            ok &= pCount;

            int nonPlat = scene.EditableEntries.Count(e => !string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool pNon = nonPlat == platWorld.Objects.Count;
            Console.WriteLine($"  platform: non-platform entries={nonPlat} (want {platWorld.Objects.Count}) -> {(pNon ? "ok" : "BAD")}");
            ok &= pNon;

            var snap = scene.LiveWorldSnapshot();
            bool snapLeak = snap.Objects.Any(o => string.Equals(o.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool pSnap =
                snap.Objects.Count == platWorld.Objects.Count &&            // platform absent from Objects
                !snapLeak &&                                                // ...and nothing typed platform leaked
                snap.Platform.Shape == "square" &&                          // platform rides through Platform
                Math.Abs(snap.Platform.Size - 7f) < 1e-4f &&
                PriviewNetworkScene.ParseColor(snap.Platform.Color, Rgba32.White) == PriviewNetworkScene.ParseColor("Cyan", Rgba32.White);
            Console.WriteLine($"  platform: snapshot Objects={snap.Objects.Count} (want {platWorld.Objects.Count}), leak={snapLeak}, plat shape={snap.Platform.Shape}, size={snap.Platform.Size}, color={snap.Platform.Color} -> {(pSnap ? "ok" : "BAD")}");
            ok &= pSnap;

            // A live platform edit (Shape) must propagate to the save/sync snapshot.
            if (platEntry?.Platform != null) platEntry.Platform.Shape = "circle";
            string snapShape = scene.LiveWorldSnapshot().Platform.Shape;
            bool pEdit = snapShape == "circle";
            Console.WriteLine($"  platform: live Shape edit -> snapshot Platform.Shape={snapShape} (want circle) -> {(pEdit ? "ok" : "BAD")}");
            ok &= pEdit;

            // MOVE: moving the floor instance propagates to the snapshot (synced at snapshot time).
            if (platEntry != null) platEntry.Instance.Position = new Vector3(2f, 1f, -3f);
            var movedPos = scene.LiveWorldSnapshot().Platform.Position;
            bool pMove = Math.Abs(movedPos.X - 2f) < 1e-4f && Math.Abs(movedPos.Y - 1f) < 1e-4f && Math.Abs(movedPos.Z + 3f) < 1e-4f;
            Console.WriteLine($"  platform: move -> snapshot Platform.Position=({movedPos.X},{movedPos.Y},{movedPos.Z}) (want 2,1,-3) -> {(pMove ? "ok" : "BAD")}");
            ok &= pMove;

            // B-identity: every editable — INCLUDING the platform — has a stable id >= 0, ids are unique,
            // and the platform is no longer the -1 sentinel.
            var idsAll = scene.EditableEntries.Select(e => e.Descriptor.Id).ToList();
            bool nonNeg = idsAll.All(id => id >= 0);
            bool unique = idsAll.Distinct().Count() == idsAll.Count;
            var platB = scene.EditableEntries.First(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool idOk = nonNeg && unique && platB.Descriptor.Id >= 0;
            Console.WriteLine($"  identity: ids=[{string.Join(",", idsAll)}] nonNeg={nonNeg}, unique={unique}, platformId={platB.Descriptor.Id} (was -1) -> {(idOk ? "ok" : "BAD")}");
            ok &= idOk;

            // Derived system name "{type} #{id}" for an unnamed object (and the platform).
            var cubeB = scene.EditableEntries.First(e => string.Equals(e.Descriptor.Type, "cube", StringComparison.OrdinalIgnoreCase));
            string cubeSys = PriviewNetworkScene.DisplayName(cubeB);
            string platSys = PriviewNetworkScene.DisplayName(platB);
            bool sysOk = cubeSys == $"cube #{cubeB.Descriptor.Id}" && platSys == "platform #0";
            Console.WriteLine($"  identity: system names cube='{cubeSys}', platform='{platSys}' -> {(sysOk ? "ok" : "BAD")}");
            ok &= sysOk;

            // A user Name overrides the system name and rides the live snapshot (save/sync path) for both
            // an ordinary object (via FromInstance/descriptor) and the platform (via PlatformConfig).
            cubeB.Descriptor.Name = "hero box";
            if (platB.Platform != null) platB.Platform.Name = "the stage";
            var liveW = scene.LiveWorldSnapshot();
            var cubeObj = liveW.Objects.First(o => o.Id == cubeB.Descriptor.Id);
            bool nameRt = cubeObj.Name == "hero box" && liveW.Platform.Name == "the stage"
                       && PriviewNetworkScene.DisplayName(cubeB) == "hero box";
            Console.WriteLine($"  identity: name round-trip cube='{cubeObj.Name}', platform='{liveW.Platform.Name}' -> {(nameRt ? "ok" : "BAD")}");
            ok &= nameRt;
        }

        // Part C2 — a DISABLED (i.e. deleted) platform reloads as ABSENT: no "platform" entry, and the
        // live snapshot keeps Platform.Enabled == false. This is the persistence contract behind delete.
        {
            var offWorld = new WorldConfig
            {
                Name = "platoff",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>(),
            };
            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), offWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();
            int platTyped = scene.EditableEntries.Count(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            bool snapEnabled = scene.LiveWorldSnapshot().Platform.Enabled;
            bool pOff = platTyped == 0 && !snapEnabled;
            Console.WriteLine($"  platform-off: platform entries={platTyped} (want 0), snapshot Enabled={snapEnabled} (want False) -> {(pOff ? "ok" : "BAD")}");
            ok &= pOff;
        }

        // Part D — a SPOT light's marker reflects its cone cross-section: each ConeShape (circle/square/
        // triangle) builds a real cone marker (FaceCount > 0). Visual correctness is verified live.
        {
            var spotWorld = new WorldConfig
            {
                Name = "spotshapes",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 0, Type = "light", LightKind = "spot", Color = "White", ConeShape = "circle",
                        Position = new Vec3Config { X = -2f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 25f },
                    new WorldObject { Id = 1, Type = "light", LightKind = "spot", Color = "Red", ConeShape = "square",
                        Position = new Vec3Config { X = 0f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 35f },
                    new WorldObject { Id = 2, Type = "light", LightKind = "spot", Color = "Cyan", ConeShape = "triangle",
                        Position = new Vec3Config { X = 2f, Y = 4f, Z = 0f }, Power = 500f, ConeAngle = 45f },
                },
            };
            var scene = new PriviewNetworkScene(new DisplayManagerAsync(), spotWorld, isServer: false, "127.0.0.1", 0, online: false);
            scene.Start();
            var spots = scene.EditableEntries.Where(e => e.Light?.Kind == LightKind.Spot).ToList();
            bool sok = spots.Count == 3;
            foreach (var e in spots)
            {
                var marker = e.Instance as Object3d;
                bool one = marker != null && marker.FaceCount > 0;
                Console.WriteLine($"  spot-marker shape={e.Light!.ConeShape}: marker faces={marker?.FaceCount} -> {(one ? "ok" : "BAD")}");
                sok &= one;
            }
            ok &= sok;
        }

        // B-camera — the local player is BODY + CAMERA. (1) The rig math: FIRSTPERSON camera == body;
        // THIRDPERSON is behind + above along the look; toggling moves the camera while the body input is
        // unchanged. (2) The body is a real object with a valid id + system name "player #<id>", NOT in
        // editables (it's the player, not world content), and the default 1st-person camera sits at the body.
        {
            var body = new Vector3(3f, 1.5f, -2f);
            var look = new Vector3(0f, 0.7f, 0f);   // yaw 0.7 rad
            var camFp = CameraMath.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.FirstPerson);
            var camTp = CameraMath.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.ThirdPerson);
            Vector3 fwd = new Vector3(1, 0, 0).Rotate(new Vector3(0, look.Y, 0));
            Vector3 wantTp = body + fwd * (-4f) + new Vector3(0, 1.5f, 0);   // ThirdPersonBack=4, Up=1.5
            bool fpOk = (camFp - body).Length() < 1e-4f;                     // 1st person: camera AT the body
            bool tpOk = (camTp - wantTp).Length() < 1e-4f;                   // 3rd person: exact behind + above
            bool behindAbove = camTp.Y > body.Y + 0.5f && (camTp - body).Length() > 1f;
            bool moves = (camTp - camFp).Length() > 1f;                      // toggling modes moves the camera
            bool rigOk = fpOk && tpOk && behindAbove && moves;
            Console.WriteLine($"  camera-rig: 1p@body={fpOk}, 3p behind+above exact={tpOk} (Y up={behindAbove}), toggle moves cam={moves} -> {(rigOk ? "ok" : "BAD")}");
            ok &= rigOk;

            var camWorld = new WorldConfig { Name = "camtest", Platform = new PlatformConfig { Enabled = true }, Objects = new List<WorldObject> { new WorldObject { Id = 0, Type = "cube" } } };
            var camScene = new PriviewNetworkScene(new DisplayManagerAsync(), camWorld, isServer: false, "127.0.0.1", 0, online: false);
            camScene.Start();
            bool bodyIdOk = camScene.LocalBodyId >= 0 && camScene.LocalBodyName == $"player #{camScene.LocalBodyId}";
            bool excluded = !camScene.LocalBodyInEditables;
            bool camAtBody = (camScene.CameraPosition - camScene.LocalBodyPosition).Length() < 1e-4f
                          && camScene.CurrentCameraMode == PriviewNetworkScene.CameraMode.FirstPerson;
            bool bodyOk = bodyIdOk && excluded && camAtBody;
            Console.WriteLine($"  camera-body: id={camScene.LocalBodyId} name='{camScene.LocalBodyName}', excluded-from-editables={excluded}, default 1p cam@body={camAtBody} -> {(bodyOk ? "ok" : "BAD")}");
            ok &= bodyOk;
        }

        // B-direct-input — TYPED direct entry for numeric fields (Enter → type → parse + CLAMP). Drives the
        // real BeginFieldEntry/TryAppendEntryChar/Confirm|Cancel path via the headless test hooks: a valid
        // value sets exactly, an out-of-range value CLAMPS to the field's N/M range, an invalid/empty entry
        // and Esc both leave the value UNCHANGED; enum/toggle fields reject typing but still cycle on N/M;
        // and the relocated spawn action still works.
        {
            var typeWorld = new WorldConfig
            {
                Name = "directinput",
                Graphics = new GraphicsConfig { },
                Physics = new PhysicsConfig { CollisionEnabled = true },   // so the Collider enum can cycle
                Platform = new PlatformConfig { Enabled = false },         // cube is the only editable -> index 0
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f }, Scale = 2f },
                },
            };
            var s = new PriviewNetworkScene(new DisplayManagerAsync(), typeWorld, isServer: false, "127.0.0.1", 0, online: false);
            s.Start();
            var cubeI = s.EditableEntries[0].Instance as Object3d;
            var inst = s.EditableEntries[0].Instance;
            bool dvin = cubeI != null && Math.Abs(cubeI!.Scale - 2f) < 1e-4f;   // baseline
            if (!dvin) Console.WriteLine($"  direct-input: baseline cube scale={cubeI?.Scale} (want 2) -> BAD");

            // (1) Valid typed value sets exactly.
            bool r1 = s.TypeFieldForTest(0, "Scale", "3.5", confirm: true);
            bool set1 = r1 && Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: type Scale='3.5' -> {cubeI!.Scale:F2} (want 3.50) -> {(set1 ? "ok" : "BAD")}");
            dvin &= set1;

            // (2) Invalid entry (letters are filtered out -> empty buffer) leaves the value UNCHANGED.
            s.TypeFieldForTest(0, "Scale", "abc", confirm: true);
            bool inv = Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            // ...and a lone '-' (parses to nothing) is also ignored.
            s.TypeFieldForTest(0, "Scale", "-", confirm: true);
            inv &= Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: invalid 'abc'/'-' -> scale stays {cubeI!.Scale:F2} (want 3.50) -> {(inv ? "ok" : "BAD")}");
            dvin &= inv;

            // (3) Esc (confirm:false) cancels — value unchanged.
            s.TypeFieldForTest(0, "Scale", "99", confirm: false);
            bool esc = Math.Abs(cubeI!.Scale - 3.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: type '99' + Esc -> scale stays {cubeI!.Scale:F2} (want 3.50) -> {(esc ? "ok" : "BAD")}");
            dvin &= esc;

            // (4) Out-of-range CLAMPS to the field's N/M range (Scale >= 0.01; Mass >= 0.1; Friction <= 2;
            //     ColorR <= 255).
            s.TypeFieldForTest(0, "Scale", "-5", confirm: true);
            bool clScale = Math.Abs(cubeI!.Scale - 0.01f) < 1e-4f;
            s.TypeFieldForTest(0, "Mass", "-3", confirm: true);
            bool clMass = Math.Abs(inst.Mass - 0.1f) < 1e-4f;
            s.TypeFieldForTest(0, "Friction", "9", confirm: true);
            bool clFric = Math.Abs(inst.Friction - 2f) < 1e-4f;
            s.TypeFieldForTest(0, "ColorR", "300", confirm: true);
            bool clR = inst.Color.R == 255;
            bool clamp = clScale && clMass && clFric && clR;
            Console.WriteLine($"  direct-input: clamp Scale(-5)->{cubeI!.Scale:F2}(0.01) Mass(-3)->{inst.Mass:F2}(0.1) Friction(9)->{inst.Friction:F2}(2) ColorR(300)->{inst.Color.R}(255) -> {(clamp ? "ok" : "BAD")}");
            dvin &= clamp;

            // (5) A precise decimal + a free (unclamped) field both set exactly.
            s.TypeFieldForTest(0, "PosX", "12.25", confirm: true);
            s.TypeFieldForTest(0, "ColorFade", "0.5", confirm: true);
            bool prec = Math.Abs(inst.Position.X - 12.25f) < 1e-4f && Math.Abs(inst.ColorFade - 0.5f) < 1e-4f;
            Console.WriteLine($"  direct-input: PosX='12.25'->{inst.Position.X:F2}, ColorFade='0.5'->{inst.ColorFade:F2} -> {(prec ? "ok" : "BAD")}");
            dvin &= prec;

            // (6) Enum/toggle field: N/M still cycles it, but typing is rejected (returns false) and leaves
            //     it unchanged. Collider AABB -> OBB via N/M; typing "1" into it does nothing.
            var before = inst.Collider;
            s.StepFieldForTest(0, "Collider", +1);
            var afterStep = inst.Collider;
            bool cycled = before == ColliderShape.Aabb && afterStep == ColliderShape.Obb;
            bool typedEnum = s.TypeFieldForTest(0, "Collider", "1", confirm: true);   // must return false
            bool enumUnchanged = inst.Collider == afterStep;
            bool enumOk = cycled && !typedEnum && enumUnchanged;
            Console.WriteLine($"  direct-input: enum Collider N/M {before}->{afterStep} (cycled={cycled}), type rejected={!typedEnum}, unchanged={enumUnchanged} -> {(enumOk ? "ok" : "BAD")}");
            dvin &= enumOk;

            // (7) The relocated spawn action still works (the [B] key calls SpawnCurrent).
            int countBefore = s.EditableEntries.Count;
            int countAfter = s.SpawnForTest();
            bool spawnOk = countAfter == countBefore + 1;
            Console.WriteLine($"  direct-input: spawn -> editables {countBefore}->{countAfter} (want +1) -> {(spawnOk ? "ok" : "BAD")}");
            dvin &= spawnOk;

            ok &= dvin;
        }

        // Plan B — spawnable Fixed/Follow cameras + active-view switching. (1) A "camera" object builds as a
        // non-colliding VIEWPOINT (from a world + via the editor spawn path) and its CamKind cycles
        // Fixed<->Follow on N/M. (2) The view-transform math: a FIXED camera renders from its placed
        // transform; a FOLLOW camera aims its forward AT the body (verified through Camera.GetRayForUv, the
        // real center ray). (3) Cycling the active view steps body(-1) -> camera id -> back to body(-1).
        {
            var camViewWorld = new WorldConfig
            {
                Name = "camview",
                Platform = new PlatformConfig { Enabled = false },   // the world camera is editable index 0
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "camera", Collides = false, CameraKind = "fixed",
                        Position = new Vec3Config { X = 2f, Y = 5f, Z = 3f },
                        Rotation = new Vec3Config { X = 0f, Y = 0.4f, Z = -0.3f } },
                },
            };
            var cs = new PriviewNetworkScene(new DisplayManagerAsync(), camViewWorld, isServer: false, "127.0.0.1", 0, online: false);
            cs.Start();

            var camEntry = cs.EditableEntries.FirstOrDefault(e => e.Descriptor.Type == "camera");
            bool built = camEntry != null && !camEntry.Instance.Collides;
            Console.WriteLine($"  camera-obj: built={camEntry != null}, non-colliding={(camEntry != null && !camEntry.Instance.Collides)} -> {(built ? "ok" : "BAD")}");
            bool camOk = built;

            // Spawn a camera through the editor spawn path -> Type "camera", non-colliding.
            int before = cs.EditableEntries.Count;
            int after = cs.SpawnTypeForTest("camera");
            var spawned = cs.EditableEntries.Last();
            bool spawnCam = after == before + 1 && spawned.Descriptor.Type == "camera" && !spawned.Instance.Collides;
            Console.WriteLine($"  camera-spawn: editables {before}->{after}, type={spawned.Descriptor.Type}, non-colliding={!spawned.Instance.Collides} -> {(spawnCam ? "ok" : "BAD")}");
            camOk &= spawnCam;

            // CamKind cycles Fixed <-> Follow on N/M (editable 0 = the world camera).
            string k0 = cs.FieldValueForTest(0, "CamKind");
            cs.StepFieldForTest(0, "CamKind", +1); string k1 = cs.FieldValueForTest(0, "CamKind");
            cs.StepFieldForTest(0, "CamKind", +1); string k2 = cs.FieldValueForTest(0, "CamKind");
            bool kindCycles = k0 == "Fixed" && k1 == "Follow" && k2 == "Fixed";
            Console.WriteLine($"  camera-kind: {k0}->{k1}->{k2} (Fixed/Follow/Fixed) -> {(kindCycles ? "ok" : "BAD")}");
            camOk &= kindCycles;

            // View-transform math (pure): FIXED = placed transform verbatim; FOLLOW forward reaches the body.
            var camPos = new Vector3(2f, 5f, 3f);
            var placedLook = new Vector3(0f, 0.4f, -0.3f);
            var body = new Vector3(-1f, 1f, 4f);
            var (fp, fl) = PriviewNetworkScene.PlacedCameraView(PriviewNetworkScene.CameraMode.Fixed, camPos, placedLook, body);
            bool fixedOk = (fp - camPos).Length() < 1e-5f && (fl - placedLook).Length() < 1e-5f;
            var (op, ol) = PriviewNetworkScene.PlacedCameraView(PriviewNetworkScene.CameraMode.Follow, camPos, placedLook, body);
            Vector3 fwd = new Camera(op, ol).GetRayForUv(Vector2.Zero).RayDirection;   // the ACTUAL rendered center ray
            Vector3 want = (body - camPos).Norm();
            bool followOk = (op - camPos).Length() < 1e-5f && (fwd - want).Length() < 1e-3f;
            Console.WriteLine($"  camera-view: Fixed=placed({fixedOk}); Follow forward·toBody={(fwd * want):F4} (want ~1) -> {((fixedOk && followOk) ? "ok" : "BAD")}");
            camOk &= fixedOk && followOk;

            // Active-view cycling: body(-1) -> the placed camera id -> back to body(-1). (One camera; the
            // authority re-stamps ids on Start, so read the camera's live id rather than assuming it.)
            var vs = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "vc",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Type = "camera", Collides = false, CameraKind = "fixed" } } },
                isServer: false, "127.0.0.1", 0, online: false);
            vs.Start();
            int camId = vs.EditableEntries.First(e => e.Descriptor.Type == "camera").Descriptor.Id;
            int v0 = vs.ActiveViewCameraId;
            vs.CycleActiveViewForTest(); int v1 = vs.ActiveViewCameraId;
            vs.CycleActiveViewForTest(); int v2 = vs.ActiveViewCameraId;
            bool cycleOk = v0 == -1 && v1 == camId && v2 == -1;
            Console.WriteLine($"  camera-cycle: view {v0}->{v1}->{v2} (body/-1 -> cam#{camId} -> body/-1) -> {(cycleOk ? "ok" : "BAD")}");
            camOk &= cycleOk;

            // Fix 1a — pure helpers. BodyViewRegion picks which region shows the interactive body view from
            // the two regions' view ids (left=active, right=next); CrosshairCell places the reticle at that
            // region's centre (single → screen centre; none → suppressed).
            const int CW = 200, CH = 50; int lw = CW / 2;   // left region [0,100), right [100,200)
            bool regOk =
                PriviewNetworkScene.BodyViewRegion(false, -1, 0) == 0 &&          // single: whole screen
                PriviewNetworkScene.BodyViewRegion(false, 7, 9) == 0 &&
                PriviewNetworkScene.BodyViewRegion(true, -1, 3) == 0 &&           // split: body LEFT
                PriviewNetworkScene.BodyViewRegion(true, 3, -1) == 1 &&           // split: body RIGHT
                PriviewNetworkScene.BodyViewRegion(true, 3, 4) == -1;             // split: neither (two cameras)
            var xS = PriviewNetworkScene.CrosshairCell(false, CW, CH, 0);
            var xL = PriviewNetworkScene.CrosshairCell(true, CW, CH, 0);
            var xR = PriviewNetworkScene.CrosshairCell(true, CW, CH, 1);
            var xN = PriviewNetworkScene.CrosshairCell(true, CW, CH, -1);
            bool cellOk =
                xS.show && xS.x == CW / 2 && xS.y == CH / 2 &&                    // single → screen centre
                xL.show && xL.x == lw / 2 && xL.y == CH / 2 &&                    // split body-left → left centre (50)
                xR.show && xR.x == lw + (CW - lw) / 2 && xR.y == CH / 2 &&        // split body-right → right centre (150)
                !xN.show;                                                          // no body view → suppressed
            Console.WriteLine($"  crosshair-helpers: region(single/L/R/none)={regOk}, cell single={xS.x},L={xL.x},R={xR.x},none.show={xN.show} -> {((regOk && cellOk) ? "ok" : "BAD")}");
            camOk &= regOk && cellOk;

            // Fix 1b — the editor aim tracks the BODY view in split, never the placed camera shown in the
            // other region. Camera at (5,2,3), body elsewhere (1st-person body view ≈ body position).
            var aimWorld = new WorldConfig { Name = "aim", Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Type = "camera", Collides = false, CameraKind = "fixed",
                    Position = new Vec3Config { X = 5f, Y = 2f, Z = 3f } } } };
            var asc = new PriviewNetworkScene(new DisplayManagerAsync(), aimWorld, isServer: false, "127.0.0.1", 0, online: false);
            asc.Start();
            var camInst0 = asc.EditableEntries.First(e => e.Descriptor.Type == "camera");
            int aimCamId = camInst0.Descriptor.Id;
            Vector3 markerPos = camInst0.Instance.Position;
            asc.SetSplitForTest(true);
            asc.SetActiveViewForTest(-1); asc.ApplyActiveViewForTest();
            var aimL = asc.BodyAimForTest();                                   // active=body → body view is the LEFT region
            asc.SetActiveViewForTest(aimCamId); asc.ApplyActiveViewForTest();  // now viewing THROUGH the camera (left region)
            var aimR = asc.BodyAimForTest();                                   // body view is the RIGHT region — aim must be it
            bool aimOk = aimL.bodyVisible && aimR.bodyVisible
                && (aimL.pos - markerPos).Length() > 0.1f                      // left-region aim is the body, not the marker
                && (aimR.pos - markerPos).Length() > 0.1f;                     // right-region aim is the body, NOT the placed camera
            Console.WriteLine($"  crosshair-aim: bodyL={aimL.bodyVisible}, bodyR={aimR.bodyVisible}, aim≠camera(|R−cam|={(aimR.pos - markerPos).Length():F2}) -> {(aimOk ? "ok" : "BAD")}");
            camOk &= aimOk;

            // Fix 1c — two placed cameras, viewing through the first: NEITHER split region shows the body, so
            // the aim (and crosshair) is suppressed.
            var nbWorld = new WorldConfig { Name = "nb", Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> {
                    new WorldObject { Type = "camera", Collides = false, CameraKind = "fixed", Position = new Vec3Config { X = 5f } },
                    new WorldObject { Type = "camera", Collides = false, CameraKind = "fixed", Position = new Vec3Config { X = -5f } } } };
            var nbs = new PriviewNetworkScene(new DisplayManagerAsync(), nbWorld, isServer: false, "127.0.0.1", 0, online: false);
            nbs.Start();
            int firstCam = nbs.EditableEntries.Where(e => e.Descriptor.Type == "camera").Min(e => e.Descriptor.Id);
            nbs.SetSplitForTest(true);
            nbs.SetActiveViewForTest(firstCam); nbs.ApplyActiveViewForTest();
            var aimNone = nbs.BodyAimForTest();
            bool noBodyOk = !aimNone.bodyVisible;
            Console.WriteLine($"  crosshair-none: both regions cameras → aim suppressed (bodyVisible={aimNone.bodyVisible}) -> {(noBodyOk ? "ok" : "BAD")}");
            camOk &= noBodyOk;

            // Fix 2 — a placed camera's marker is NOT globally removed while viewed through: it self-hides
            // from its OWN camera (rays start at the marker centre → every face is back-face-culled) yet is a
            // visible object from elsewhere (so it renders in the OTHER split region). Geometry-level proof.
            var camMesh = camInst0.Instance as Object3d;
            Vector3 mc = camMesh!.Position;
            var fromCentre = camMesh.GetRenderData(new Ray(mc, new Vector3(0f, 0f, -1f)));           // its own view: invisible
            var fromAfar = camMesh.GetRenderData(new Ray(mc + new Vector3(0f, 0f, 6f), new Vector3(0f, 0f, -1f)));  // elsewhere: visible
            bool markerOk = fromCentre.Intersection < 0f && fromAfar.Intersection > 0f;
            Console.WriteLine($"  marker-per-region: self-view hit={fromCentre.Intersection:F2} (want <0), other-view hit={fromAfar.Intersection:F2} (want >0) -> {(markerOk ? "ok" : "BAD")}");
            camOk &= markerOk;

            ok &= camOk;
        }

        // HUD stage 1 — the DOCKED-editor layout (pure DockLayout): given a screen W×H it splits into a
        // toolbar/status/hierarchy/inspector/viewport that TILE the screen EXACTLY (every cell covered once →
        // cover + no overlap), keep the viewport ≥ a minimum with no negative dims, clamp on tiny sizes, and
        // the viewport centre == the docked crosshair cell (so the region→uv aim matches the reticle).
        {
            bool dockOk = true;

            // Exact-tiling check: mark each cell by how many of the 5 rects cover it — must be exactly 1.
            bool Tiles(int W, int H)
            {
                var L = PriviewNetworkScene.DockLayout(W, H);
                var rects = new[] { L.Toolbar, L.Status, L.Hierarchy, L.Inspector, L.Viewport };
                foreach (var r in rects) if (r.W < 0 || r.H < 0) return false;   // never negative dims
                var cover = new int[W * H];
                foreach (var r in rects)
                    for (int y = r.Y; y < r.Y + r.H; y++)
                        for (int x = r.X; x < r.X + r.W; x++)
                        {
                            if (x < 0 || x >= W || y < 0 || y >= H) return false;   // in bounds
                            cover[y * W + x]++;
                        }
                foreach (var c in cover) if (c != 1) return false;   // exactly once = cover + non-overlap
                return true;
            }

            (int, int)[] sizes = { (200, 50), (120, 40), (100, 30), (80, 25), (40, 12), (20, 6), (10, 4), (5, 3), (1, 1) };
            foreach (var (W, H) in sizes)
            {
                bool t = Tiles(W, H);
                if (!t) { dockOk = false; Console.WriteLine($"  dock-tiles {W}x{H} -> BAD"); }
            }
            Console.WriteLine($"  dock-tiling (9 sizes, exact cover + no overlap, no negative) -> {(dockOk ? "ok" : "BAD")}");

            // Roomy screen: the viewport keeps a sensible minimum and sits between the two side panels.
            var big = PriviewNetworkScene.DockLayout(200, 50);
            bool vpOk = big.Viewport.W >= 16 && big.Viewport.H >= 3
                && big.Hierarchy.W >= 18 && big.Inspector.W >= 28
                && big.Viewport.X == big.Hierarchy.W && big.Toolbar.H == 1 && big.Status.H == 1;
            Console.WriteLine($"  dock-viewport 200x50: vp={big.Viewport.W}x{big.Viewport.H}, hier={big.Hierarchy.W}, insp={big.Inspector.W} -> {(vpOk ? "ok" : "BAD")}");
            dockOk &= vpOk;

            // The docked crosshair (CrosshairCell over the viewport rect, single view) == the viewport centre,
            // so what the reticle marks is exactly what the region→uv pick aims at.
            var (cshow, ccx, ccy) = PriviewNetworkScene.CrosshairCell(false, big.Viewport.X, big.Viewport.Y, big.Viewport.W, big.Viewport.H, 0);
            bool crossOk = cshow && ccx == big.Viewport.CenterX && ccy == big.Viewport.CenterY;
            Console.WriteLine($"  dock-crosshair: cell=({ccx},{ccy}) viewport-centre=({big.Viewport.CenterX},{big.Viewport.CenterY}) -> {(crossOk ? "ok" : "BAD")}");
            dockOk &= crossOk;

            // HUD stage 2 — COLLAPSIBLE docked Inspector (pure BuildInspectorRows). Given each field's section
            // (in order) + the collapsed set, the visible rows = a header per section + its fields ONLY when
            // expanded; the field cursor navigates these rows so it can NEVER land on a hidden field.
            var secs = new[] { "Transform", "Transform", "Transform", "Appearance", "Appearance", "Physics" };

            // (a) all expanded → header + all fields, in order (headers at 0/4/7; fields carry their source index).
            var rAll = PriviewNetworkScene.BuildInspectorRows(secs, new HashSet<string>());
            bool allOk = rAll.Count == 9
                && rAll[0].IsHeader && rAll[0].Section == "Transform"
                && !rAll[1].IsHeader && rAll[1].FieldIndex == 0 && !rAll[3].IsHeader && rAll[3].FieldIndex == 2
                && rAll[4].IsHeader && rAll[4].Section == "Appearance"
                && rAll[7].IsHeader && rAll[7].Section == "Physics" && !rAll[8].IsHeader && rAll[8].FieldIndex == 5;
            Console.WriteLine($"  inspector-rows all-expanded: {rAll.Count} rows (want 9) -> {(allOk ? "ok" : "BAD")}");
            dockOk &= allOk;

            // (b) one collapsed (Appearance) → its header shows, its 2 fields HIDDEN (no cursorable field row).
            var rOne = PriviewNetworkScene.BuildInspectorRows(secs, new HashSet<string> { "Appearance" });
            bool noHiddenField = !rOne.Any(r => !r.IsHeader && r.Section == "Appearance");
            bool headerKept = rOne.Any(r => r.IsHeader && r.Section == "Appearance");
            bool oneOk = rOne.Count == 7 && noHiddenField && headerKept;
            Console.WriteLine($"  inspector-rows one-collapsed: {rOne.Count} rows (want 7), hidden fields skipped={noHiddenField}, header kept={headerKept} -> {(oneOk ? "ok" : "BAD")}");
            dockOk &= oneOk;

            // (c) all collapsed → headers only, no field rows.
            var rNone = PriviewNetworkScene.BuildInspectorRows(secs, new HashSet<string> { "Transform", "Appearance", "Physics" });
            bool allHeaders = rNone.Count == 3 && rNone.All(r => r.IsHeader);
            Console.WriteLine($"  inspector-rows all-collapsed: {rNone.Count} rows (want 3, all headers={rNone.All(r => r.IsHeader)}) -> {(allHeaders ? "ok" : "BAD")}");
            dockOk &= allHeaders;

            // (d) toggling flips the state + the visible-row count: collapse Transform (→6) then re-expand (→9).
            var st = new HashSet<string> { "Transform" };
            int collapsedCount = PriviewNetworkScene.BuildInspectorRows(secs, st).Count;
            st.Remove("Transform");
            int expandedCount = PriviewNetworkScene.BuildInspectorRows(secs, st).Count;
            bool toggleOk = collapsedCount == 6 && expandedCount == 9;
            Console.WriteLine($"  inspector-rows toggle: collapsed={collapsedCount} (want 6), re-expanded={expandedCount} (want 9) -> {(toggleOk ? "ok" : "BAD")}");
            dockOk &= toggleOk;

            // HUD stage 3 — Fix 2: the collapse/selection markers are pure ASCII (the old Unicode ▾/▸ rendered
            // as "?" on terminals lacking those glyphs).
            bool asciiMarkers = PriviewNetworkScene.MarkerExpanded.All(c => c <= 127)
                && PriviewNetworkScene.MarkerCollapsed.All(c => c <= 127)
                && PriviewNetworkScene.MarkerSelected.All(c => c <= 127);
            Console.WriteLine($"  hud-markers ASCII: exp='{PriviewNetworkScene.MarkerExpanded}' col='{PriviewNetworkScene.MarkerCollapsed}' sel='{PriviewNetworkScene.MarkerSelected}' -> {(asciiMarkers ? "ok" : "BAD")}");
            dockOk &= asciiMarkers;

            // HUD stage 3 — Fix 1: the render loop's standalone top-left fps is drawn in PLAY, SUPPRESSED in
            // DOCKED (the docked Status bar shows fps instead).
            var fpsScene = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "fps",
                Platform = new PlatformConfig { Enabled = false } }, isServer: false, "127.0.0.1", 0, online: false);
            fpsScene.Start();
            bool fpsPlay = fpsScene.ShowFrameFps;                 // PLAY (default) -> true
            fpsScene.SetDockedForTest(true);
            bool fpsDocked = fpsScene.ShowFrameFps;               // DOCKED -> false
            fpsScene.SetDockedForTest(false);
            bool fpsBack = fpsScene.ShowFrameFps;                 // back to PLAY -> true
            bool fpsOk = fpsPlay && !fpsDocked && fpsBack;
            Console.WriteLine($"  hud-fps gate: play={fpsPlay} docked={fpsDocked} back={fpsBack} (want T/F/T) -> {(fpsOk ? "ok" : "BAD")}");
            dockOk &= fpsOk;

            // CHAT rework — pure helpers.
            // (1) WrapText: no line exceeds the width; a long unbroken token hard-splits; short text passes.
            var wA = PriviewNetworkScene.WrapText("hello world foo", 5);
            bool wrapShort = wA.All(l => l.Length <= 5) && wA.Count == 3 && wA[0] == "hello" && wA[2] == "foo";
            var wB = PriviewNetworkScene.WrapText("abcdefghijk", 4);           // -> abcd/efgh/ijk
            bool wrapSplit = wB.All(l => l.Length <= 4) && wB.Count == 3 && wB[0] == "abcd" && wB[2] == "ijk";
            var wC = PriviewNetworkScene.WrapText("hi", 10);
            bool wrapPass = wC.Count == 1 && wC[0] == "hi";
            Console.WriteLine($"  chat-wrap: short[{string.Join('/', wA)}]={wrapShort}, split[{string.Join('/', wB)}]={wrapSplit}, pass={wrapPass} -> {((wrapShort && wrapSplit && wrapPass) ? "ok" : "BAD")}");
            dockOk &= wrapShort && wrapSplit && wrapPass;

            // (2) ChatVisibleSlice: correct window; clamps [0,max]; at-bottom auto-shows newest; scrolled-up does NOT.
            var vAll = PriviewNetworkScene.ChatVisibleSlice(3, 8, 0);          // few lines: all shown
            var vBot = PriviewNetworkScene.ChatVisibleSlice(20, 8, 0);         // newest 8
            var vUp  = PriviewNetworkScene.ChatVisibleSlice(20, 8, 5);         // scrolled up
            var vTop = PriviewNetworkScene.ChatVisibleSlice(20, 8, 999);       // clamp to top
            var vNeg = PriviewNetworkScene.ChatVisibleSlice(20, 8, -4);        // clamp to bottom
            bool sliceOk = vAll == (0, 3, 0) && vBot == (12, 8, 0) && vUp == (7, 8, 5)
                        && vTop == (0, 8, 12) && vNeg == (12, 8, 0);
            // auto-bottom vs scrolled-up when a message is appended (8 -> 9 wrapped lines, box 4 rows):
            var after0 = PriviewNetworkScene.ChatVisibleSlice(9, 4, 0);        // at bottom -> newest shown
            var afterU = PriviewNetworkScene.ChatVisibleSlice(9, 4, 3);        // scrolled up -> NOT jumped to bottom
            bool autoBottom = after0.start + after0.count == 9;                // includes the newest line
            bool notJump = afterU.start + afterU.count < 9;                    // still above the bottom
            Console.WriteLine($"  chat-scroll: slices={sliceOk}, auto-bottom={autoBottom}, scrolled-up-stays={notJump} -> {((sliceOk && autoBottom && notJump) ? "ok" : "BAD")}");
            dockOk &= sliceOk && autoBottom && notJump;

            // N2 — FormatChatLine (client-self-signed): a non-empty nick prefixes "{nick}: "; an empty nick
            // falls back to the SAME "player #<id>:" wording as NameForNetId / LocalBodyName.
            bool chatNick  = PriviewNetworkScene.FormatChatLine("Nick", 5, "hi") == "Nick: hi";
            bool chatEmpty = PriviewNetworkScene.FormatChatLine("", 7, "hi") == "player #7: hi";
            bool fmtOk = chatNick && chatEmpty;
            Console.WriteLine($"  chat-format: nick='{PriviewNetworkScene.FormatChatLine("Nick", 5, "hi")}', empty='{PriviewNetworkScene.FormatChatLine("", 7, "hi")}' -> {(fmtOk ? "ok" : "BAD")}");
            dockOk &= fmtOk;

            // (3) Placement: the chat box never intersects the HUD panels (per mode).
            bool Hit(PriviewNetworkScene.DockRect a, PriviewNetworkScene.DockRect b)
                => a.W > 0 && a.H > 0 && b.W > 0 && b.H > 0
                && a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
            int CW = 200, CH = 50;
            var CL = PriviewNetworkScene.DockLayout(CW, CH);
            // DOCKED: inside the viewport -> clear of toolbar/status/hierarchy/inspector.
            var chatD = PriviewNetworkScene.ChatBoxRect(2, CW, CH, CL);
            var vp = CL.Viewport;
            bool dInside = chatD.W > 0 && chatD.X >= vp.X && chatD.X + chatD.W <= vp.X + vp.W && chatD.Y >= vp.Y && chatD.Y + chatD.H <= vp.Y + vp.H;
            bool dClear = !Hit(chatD, CL.Toolbar) && !Hit(chatD, CL.Status) && !Hit(chatD, CL.Hierarchy) && !Hit(chatD, CL.Inspector);
            // OVERLAY: below the reserved top-HUD rows -> clear of EDIT MODE/KEYS (top-left) + PROPERTIES (top-right).
            var chatO = PriviewNetworkScene.ChatBoxRect(1, 120, CH, PriviewNetworkScene.DockLayout(120, CH));
            var editBox = new PriviewNetworkScene.DockRect(2, 12, 55, 8);
            var keysBox = new PriviewNetworkScene.DockRect(2, 21, 40, 11);
            var propBox = new PriviewNetworkScene.DockRect(120 - 40, 0, 40, 30);
            bool oClear = chatO.W > 0 && chatO.Y >= PriviewNetworkScene.OverlayHudBottom
                       && !Hit(chatO, editBox) && !Hit(chatO, keysBox) && !Hit(chatO, propBox);
            // PLAY: below the top hint line.
            var chatP = PriviewNetworkScene.ChatBoxRect(0, 120, CH, PriviewNetworkScene.DockLayout(120, CH));
            bool pClear = chatP.W > 0 && chatP.Y >= PriviewNetworkScene.PlayHudBottom;
            bool placeOk = dInside && dClear && oClear && pClear;
            Console.WriteLine($"  chat-place: docked inside-vp={dInside} clear={dClear}, overlay clear={oClear}, play clear={pClear} -> {(placeOk ? "ok" : "BAD")}");
            dockOk &= placeOk;

            // PART A — the editor input is SUPPRESSED while chatting (chat/field-entry has exclusive input),
            // so a send-Enter can't also begin Inspector field-entry. (fpsScene is reused from the fps test.)
            bool editWhenIdle = fpsScene.EditorProcessesInputForTest;   // not chatting -> editor runs
            fpsScene.SetChattingForTest(true);
            bool editWhenChatting = fpsScene.EditorProcessesInputForTest;   // chatting -> editor suppressed
            fpsScene.SetChattingForTest(false);
            bool gateOk = editWhenIdle && !editWhenChatting;
            Console.WriteLine($"  chat-enter-gate: editor idle={editWhenIdle}, chatting={editWhenChatting} (want T/F) -> {(gateOk ? "ok" : "BAD")}");
            dockOk &= gateOk;

            // PART B — HierarchyWindow keeps the SELECTED item visible + clamps at the ends (Unity-hierarchy scroll).
            bool InWin(int listCount, int rows, int sel)
            {
                var (s, c) = PriviewNetworkScene.HierarchyWindow(listCount, rows, sel);
                return s >= 0 && c >= 0 && s + c <= listCount && sel >= s && sel < s + c;   // selection is inside the window
            }
            var wFit = PriviewNetworkScene.HierarchyWindow(3, 8, 1);          // fits -> all shown from 0
            var wTop = PriviewNetworkScene.HierarchyWindow(20, 6, 0);         // selected at top
            var wMid = PriviewNetworkScene.HierarchyWindow(20, 6, 10);        // selected mid -> centred, in view
            var wBot = PriviewNetworkScene.HierarchyWindow(20, 6, 19);        // selected last -> clamped to the bottom
            bool hierOk = wFit == (0, 3)
                       && wTop.start == 0 && wTop.count == 6
                       && wBot.start == 14 && wBot.count == 6                  // last 6 of 20 -> start 14
                       && InWin(20, 6, 0) && InWin(20, 6, 10) && InWin(20, 6, 19) && InWin(50, 7, 25) && InWin(3, 8, 2);
            Console.WriteLine($"  hierarchy-window: fit={wFit}, top={wTop}, mid={wMid}, bot={wBot}, selection-always-visible={hierOk} -> {(hierOk ? "ok" : "BAD")}");
            dockOk &= hierOk;

            // FIX A — the render loop's ESC→quit is SUPPRESSED while chatting (ESC cancels the chat instead),
            // and allowed otherwise. (fpsScene reused; SetChattingForTest drives _isChatting.)
            bool quitIdle = fpsScene.AllowQuit;             // not chatting -> quit allowed
            fpsScene.SetChattingForTest(true);
            bool quitChatting = fpsScene.AllowQuit;         // chatting -> quit suppressed
            fpsScene.SetChattingForTest(false);
            bool quitOk = quitIdle && !quitChatting;
            Console.WriteLine($"  chat-esc-gate: quit idle={quitIdle}, chatting={quitChatting} (want T/F) -> {(quitOk ? "ok" : "BAD")}");
            dockOk &= quitOk;

            // FIX B — the chat scroll responds to the arrow/PgUp-PgDn inputs (same ScrollChat path): up = older
            // (offset++), down = newer (offset--), clamped at the bottom (0); ChatVisibleSlice reflects it.
            var scScene = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "sc",
                Platform = new PlatformConfig { Enabled = false } }, isServer: false, "127.0.0.1", 0, online: false);
            scScene.Start();
            int s0 = scScene.ChatScrollForTest;             // starts at 0 (bottom)
            scScene.ScrollChatForTest(+1); scScene.ScrollChatForTest(+1);   // two "up" (arrow/PgUp)
            int sUp = scScene.ChatScrollForTest;            // -> 2
            var slcUp = PriviewNetworkScene.ChatVisibleSlice(20, 8, sUp);   // scrolled up: not at the bottom
            scScene.ScrollChatForTest(-1);                  // one "down" (arrow/PgDn)
            int sDn = scScene.ChatScrollForTest;            // -> 1
            scScene.ScrollChatForTest(-5);                  // over-scroll down -> clamps at 0
            int sClamp = scScene.ChatScrollForTest;         // -> 0
            bool scrollOk = s0 == 0 && sUp == 2 && sDn == 1 && sClamp == 0 && slcUp.clampedOffset == 2 && slcUp.start == 10;
            Console.WriteLine($"  chat-scroll-keys: start={s0}, up={sUp}, down={sDn}, clamp={sClamp}, sliceScrolled={slcUp.clampedOffset} -> {(scrollOk ? "ok" : "BAD")}");
            dockOk &= scrollOk;

            ok &= dockOk;
        }

        // 3rd-person camera clip-avoidance — the body camera pulls IN past a wall so it never sees through
        // it, and GLIDES (eased) rather than snapping. (1) the pure clamp math; (2) the REAL raycast: a wall
        // between the body and the 3rd-person camera pulls the target boom in, nothing-in-the-way keeps the
        // full boom; (3) the eased boom converges toward the target and never overshoots past the wall.
        {
            var off = CameraMath.CameraOffsetFor(Vector3.Zero, PriviewNetworkScene.CameraMode.ThirdPerson);
            float full = off.Length();

            // (1) Pure clamp: a hit closer than the boom -> (hit - margin), floored at minDist; a hit at/
            //     beyond the boom (or none) -> the full boom.
            bool pNear  = Math.Abs(PriviewNetworkScene.ResolveCameraBackDistance(off, 2.0f, 0.6f, 0.3f) - 1.7f) < 1e-4f;   // 2.0<full -> 2.0-0.3
            bool pFloor = Math.Abs(PriviewNetworkScene.ResolveCameraBackDistance(off, 0.5f, 0.6f, 0.3f) - 0.6f) < 1e-4f;   // 0.5-0.3=0.2 -> floored to 0.6
            bool pMax   = Math.Abs(PriviewNetworkScene.ResolveCameraBackDistance(off, float.MaxValue, 0.6f, 0.3f) - full) < 1e-4f;
            bool pFar   = Math.Abs(PriviewNetworkScene.ResolveCameraBackDistance(off, full + 5f, 0.6f, 0.3f) - full) < 1e-4f;
            bool pureOk = pNear && pFloor && pMax && pFar;
            Console.WriteLine($"  clip-pure: near={pNear}, floor={pFloor}, none(max)={pMax}, none(far)={pFar} (full={full:F2}) -> {(pureOk ? "ok" : "BAD")}");
            bool clipOk = pureOk;

            // (2) Raycast: a wall cube ON the body->camera ray (body at origin, level look -> boom goes
            //     back+up). Place a unit cube centered ~2.2 along the boom direction, well inside the boom.
            Vector3 body = Vector3.Zero, look = Vector3.Zero;
            Vector3 dir = off.Norm();
            Vector3 wallPos = dir * 2.2f;
            var wallWorld = new WorldConfig { Name = "clip", Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Scale = 1f,
                    Position = new Vec3Config { X = wallPos.X, Y = wallPos.Y, Z = wallPos.Z } } } };
            var clipScene = new PriviewNetworkScene(new DisplayManagerAsync(), wallWorld, isServer: false, "127.0.0.1", 0, online: false);
            clipScene.Start();

            var withWall = clipScene.ThirdPersonClipForTest(body, look);
            bool pulledIn = withWall.hit > 0f && withWall.hit < withWall.full && withWall.target < withWall.full && withWall.target >= 0.6f - 1e-4f;
            var noWall = clipScene.ThirdPersonClipForTest(body, new Vector3(0f, MathF.PI, 0f));   // look 180° -> boom aims away from the wall
            bool fullWhenClear = noWall.hit >= noWall.full && Math.Abs(noWall.target - noWall.full) < 1e-4f;
            bool rayOk = pulledIn && fullWhenClear;
            Console.WriteLine($"  clip-ray: wall hit={withWall.hit:F2}<full={withWall.full:F2} -> target={withWall.target:F2}(pulled={pulledIn}); clear hit={(noWall.hit > 1e6f ? "none" : noWall.hit.ToString("F2"))} target={noWall.target:F2}==full({fullWhenClear}) -> {(rayOk ? "ok" : "BAD")}");
            clipOk &= rayOk;

            // (3) Smoothing: from full extension the eased boom glides DOWN toward the wall target — monotone
            //     and never below the target (no overshoot past the wall) — converging within a few steps.
            float tgt = withWall.target;
            float prev = float.MaxValue; bool monotone = true, noOver = true;
            for (int i = 0; i < 60; i++)
            {
                clipScene.StepThirdPersonCameraForTest(body, look, 1f / 60f);
                float boom = clipScene.CameraBoomLengthForTest;
                if (boom > prev + 1e-4f) monotone = false;   // never grows while pulling in
                if (boom < tgt - 1e-4f) noOver = false;      // never past the wall
                prev = boom;
            }
            bool converged = Math.Abs(clipScene.CameraBoomLengthForTest - tgt) < 1e-2f;
            bool smoothOk = monotone && noOver && converged && tgt < withWall.full;
            Console.WriteLine($"  clip-smooth: boom -> {clipScene.CameraBoomLengthForTest:F3} (target {tgt:F3}), monotone={monotone}, no-overshoot={noOver}, converged={converged} -> {(smoothOk ? "ok" : "BAD")}");
            clipOk &= smoothOk;

            ok &= clipOk;
        }

        // Plan B — 2nd-person body view + Follow-camera arbitrary targets.
        {
            bool camOk = true;

            // (1) 2nd-person rig: the camera sits IN FRONT of the body (offset sign OPPOSITE 3rd person
            //     along the look) and its orientation faces BACK at the body (forward·toBody ≈ 1 via the
            //     REAL center ray).
            var body2 = new Vector3(1f, 1f, -2f);
            var look2 = new Vector3(0f, 0.6f, 0f);   // yaw
            Vector3 fwdYaw = new Vector3(1, 0, 0).Rotate(new Vector3(0, look2.Y, 0));
            float behind3 = CameraMath.CameraOffsetFor(look2, PriviewNetworkScene.CameraMode.ThirdPerson) * fwdYaw;
            float front2  = CameraMath.CameraOffsetFor(look2, PriviewNetworkScene.CameraMode.SecondPerson) * fwdYaw;
            bool oppositeSide = front2 > 0f && behind3 < 0f;
            var cam2 = CameraMath.CameraPositionFor(body2, look2, PriviewNetworkScene.CameraMode.SecondPerson);
            var look2back = PriviewNetworkScene.LookRotationTo(body2 - cam2);
            Vector3 fwd2 = new Camera(cam2, look2back).GetRayForUv(Vector2.Zero).RayDirection;
            Vector3 want2 = (body2 - cam2).Norm();
            bool looksBack = (fwd2 * want2) > 0.999f;
            bool rig2Ok = oppositeSide && looksBack;
            Console.WriteLine($"  2nd-person: front2={front2:F2}>0 & behind3={behind3:F2}<0 ({oppositeSide}); look·toBody={(fwd2 * want2):F4} (want ~1) -> {(rig2Ok ? "ok" : "BAD")}");
            camOk &= rig2Ok;

            // (2) F7 body-view cycle: 1st -> 3rd -> 2nd -> 1st person.
            var f7 = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "f7",
                Platform = new PlatformConfig { Enabled = false }, Objects = new List<WorldObject>() },
                isServer: false, "127.0.0.1", 0, online: false);
            f7.Start();
            var m0 = f7.CurrentCameraMode;
            f7.CycleBodyViewForTest(); var m1 = f7.CurrentCameraMode;
            f7.CycleBodyViewForTest(); var m2 = f7.CurrentCameraMode;
            f7.CycleBodyViewForTest(); var m3 = f7.CurrentCameraMode;
            bool cycleOk = m0 == PriviewNetworkScene.CameraMode.FirstPerson && m1 == PriviewNetworkScene.CameraMode.ThirdPerson
                        && m2 == PriviewNetworkScene.CameraMode.SecondPerson && m3 == PriviewNetworkScene.CameraMode.FirstPerson;
            Console.WriteLine($"  2nd-cycle: {m0}->{m1}->{m2}->{m3} (1st/3rd/2nd/1st) -> {(cycleOk ? "ok" : "BAD")}");
            camOk &= cycleOk;

            // (3) Clip-avoidance for 2nd person: a wall on the FRONT boom pulls the camera in (same clamp as
            //     3rd person, just the front-facing boom direction).
            Vector3 body = Vector3.Zero, look = Vector3.Zero;
            Vector3 dir2 = CameraMath.CameraOffsetFor(look, PriviewNetworkScene.CameraMode.SecondPerson).Norm();
            Vector3 wallPos2 = dir2 * 2.2f;
            var clip2 = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "clip2",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Scale = 1f,
                    Position = new Vec3Config { X = wallPos2.X, Y = wallPos2.Y, Z = wallPos2.Z } } } },
                isServer: false, "127.0.0.1", 0, online: false);
            clip2.Start();
            var withWall2 = clip2.SecondPersonClipForTest(body, look);
            bool pulled2 = withWall2.hit > 0f && withWall2.hit < withWall2.full && withWall2.target < withWall2.full && withWall2.target >= 0.6f - 1e-4f;
            Console.WriteLine($"  2nd-clip: wall hit={withWall2.hit:F2}<full={withWall2.full:F2} -> target={withWall2.target:F2} (pulled={pulled2}) -> {(pulled2 ? "ok" : "BAD")}");
            camOk &= pulled2;

            // (4) Follow-camera arbitrary targets: a follow camera resolves its aim to the TRACKED object's
            //     live position; the -1 sentinel resolves to the player body; an unresolved id FALLS BACK to
            //     the player body (never breaks). Read the object's LIVE id (the authority may re-stamp ids).
            var ft = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "follow",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 5, Type = "cube", Position = new Vec3Config { X = 7f, Y = 2f, Z = -3f } },
                    new WorldObject { Id = 6, Type = "camera", Collides = false, CameraKind = "follow" },
                } },
                isServer: false, "127.0.0.1", 0, online: false);
            ft.Start();
            var cubeEntry = ft.EditableEntries.First(e => e.Descriptor.Type == "cube");
            int cubeId = cubeEntry.Descriptor.Id;
            Vector3 cubePos = cubeEntry.Instance.Position;
            bool tracksObj = (ft.FollowTargetPositionForTest(cubeId) - cubePos).Length() < 1e-5f;
            bool tracksBody = (ft.FollowTargetPositionForTest(-1) - ft.LocalBodyPosition).Length() < 1e-5f;
            bool fallsBack = (ft.FollowTargetPositionForTest(999999) - ft.LocalBodyPosition).Length() < 1e-5f;
            bool followOk = tracksObj && tracksBody && fallsBack;
            Console.WriteLine($"  follow-target: obj#{cubeId}@{cubePos.X:F1} tracked={tracksObj}, sentinel->body={tracksBody}, unresolved->body={fallsBack} -> {(followOk ? "ok" : "BAD")}");
            camOk &= followOk;

            ok &= camOk;
        }

        // Fix 1 — a placed FIXED camera's ACTIVE VIEW always reflects its CURRENT rotation, before AND
        // after a position edit (PlacedCameraView(Fixed) reads the marker's LIVE LocalRotate each frame;
        // moving calls UpdateGeometry, which never touches LocalRotate). Both edit orders must end with the
        // resolved view orientation == the object's rotation and position == the object's position; and the
        // set yaw/pitch must actually steer the rendered center ray.
        {
            bool fixOk = true;

            PriviewNetworkScene NewFixedCamActive()
            {
                var w = new WorldConfig { Name = "fixcam", Platform = new PlatformConfig { Enabled = false },
                    Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "camera", Collides = false, CameraKind = "fixed",
                        Position = new Vec3Config { X = 2f, Y = 3f, Z = 1f } } } };
                var sc = new PriviewNetworkScene(new DisplayManagerAsync(), w, isServer: false, "127.0.0.1", 0, online: false);
                sc.Start();
                sc.SetActiveViewForTest(sc.EditableEntries.First(e => e.Descriptor.Type == "camera").Descriptor.Id);
                return sc;   // the camera is editable index 0
            }
            bool ViewMatches(PriviewNetworkScene sc)
            {
                sc.ApplyActiveViewForTest();
                var m = sc.EditableEntries[0].Instance;
                return (sc.CameraLook - m.LocalRotate).Length() < 1e-4f && (sc.CameraPosition - m.Position).Length() < 1e-4f;
            }

            // Order A: position THEN rotation. After moving, set all three rotation fields -> the view must match.
            var a = NewFixedCamActive();
            a.TypeFieldForTest(0, "PosX", "5", true); a.TypeFieldForTest(0, "PosZ", "-4", true);         // move first
            a.TypeFieldForTest(0, "RotX", "0.3", true); a.TypeFieldForTest(0, "RotY", "0.5", true); a.TypeFieldForTest(0, "RotZ", "0.2", true);
            bool aMatch = ViewMatches(a);
            var aRay = new Camera(a.CameraPosition, a.CameraLook).GetRayForUv(Vector2.Zero).RayDirection;
            bool aSteers = (aRay - new Vector3(1f, 0f, 0f)).Length() > 0.1f;                             // yaw/pitch actually steer the look
            Console.WriteLine($"  fixcam-pos-then-rot: view==transform={aMatch}, ray steered={aSteers} -> {((aMatch && aSteers) ? "ok" : "BAD")}");
            fixOk &= aMatch && aSteers;

            // Order B: rotation THEN position. A later move must NOT disturb the orientation.
            var b = NewFixedCamActive();
            b.TypeFieldForTest(0, "RotX", "0.3", true); b.TypeFieldForTest(0, "RotY", "0.5", true); b.TypeFieldForTest(0, "RotZ", "0.2", true);
            b.ApplyActiveViewForTest(); var rotBeforeMove = b.CameraLook;
            b.TypeFieldForTest(0, "PosX", "5", true); b.TypeFieldForTest(0, "PosY", "-2", true);         // move after
            bool bMatch = ViewMatches(b);
            bool bStable = (b.CameraLook - rotBeforeMove).Length() < 1e-4f;                              // move didn't rotate the view
            Console.WriteLine($"  fixcam-rot-then-pos: view==transform={bMatch}, rot stable across move={bStable} -> {((bMatch && bStable) ? "ok" : "BAD")}");
            fixOk &= bMatch && bStable;

            ok &= fixOk;
        }

        // E2 — conn-map fix: RecordConnMappingIfTcp records a real TCP connId, but a UDP-delivered packet
        // (LastSenderConnId == -1) must NOT create a [-1] entry or clobber an existing mapping — otherwise
        // disconnect avatar cleanup regresses once transforms ride UDP.
        {
            var cm = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "connmap",
                Platform = new PlatformConfig { Enabled = false }, Objects = new List<WorldObject>() },
                isServer: false, "127.0.0.1", 0, online: false);
            cm.ConnMappingForTest(connId: 5, senderId: 42);
            bool tcpRecorded = cm.ConnMapTryGetForTest(5, out int n) && n == 42;   // TCP connId is recorded
            cm.ConnMappingForTest(connId: -1, senderId: 99);
            bool udpNoEntry = !cm.ConnMapTryGetForTest(-1, out _);                 // UDP (-1) creates no entry
            bool tcpIntact = cm.ConnMapTryGetForTest(5, out int n2) && n2 == 42;   // earlier mapping not clobbered
            bool connOk = tcpRecorded && udpNoEntry && tcpIntact;
            Console.WriteLine($"  connmap-tcp-vs-udp: tcpRecorded={tcpRecorded}, udpNoEntry={udpNoEntry}, tcpIntact={tcpIntact} -> {(connOk ? "ok" : "BAD")}");
            ok &= connOk;
        }

        // N2 — nickname roster (netId -> nick), the RECORD/read/prune logic (headless; the socket-level hub +
        // back-fill are glue, verified live). A received PlayerInfoPacket records a SANITIZED nick keyed by
        // the packet's NetId; NameForNetId reads it back and falls back to "player #<id>" for an unknown id;
        // and the leave path drops the entry.
        {
            var rs = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "roster",
                Platform = new PlatformConfig { Enabled = false }, Objects = new List<WorldObject>() },
                isServer: false, "127.0.0.1", 0, online: false);
            rs.Start();

            // A junky nick sanitizes on record (space + punctuation dropped, kept chars preserved).
            rs.ReceivePlayerInfoForTest(555, "Bo b!@#", senderId: 42);
            string recordedName = rs.NameForNetId(555);
            bool recorded = recordedName == "Bob";
            // An unknown net id (never seen; outside the 2..9999 self-id range) falls back to the system name.
            bool fallback = rs.NameForNetId(99999) == "player #99999";
            // Leaving prunes the entry -> back to the fallback.
            rs.PlayerLeftForTest(555);
            bool pruned = rs.NameForNetId(555) == "player #555";
            bool rosterOk = recorded && fallback && pruned;
            Console.WriteLine($"  roster: record='{recordedName}' (want 'Bob'), fallback={fallback}, pruned='{rs.NameForNetId(555)}'({pruned}) -> {(rosterOk ? "ok" : "BAD")}");
            ok &= rosterOk;
        }

        // C1-4b — in-editor JOINT authoring: spawn a joint with a body selected, edit ALL its params through
        // the field inspector (kind swaps the field set; body ids, anchors, limit/motor, spring), and delete
        // it — keeping _world.Joints (the C1-4a bridge source) in sync. Plus a load round-trip.
        {
            var jointWorld = new WorldConfig
            {
                Name = "jointedit",
                Physics = new PhysicsConfig { GravityEnabled = true },
                Platform = new PlatformConfig { Enabled = false },   // objects are editables 0,1
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Color = "White", Gravity = true,
                        Position = new Vec3Config { X = 0f, Y = 3f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Color = "Red",
                        Position = new Vec3Config { X = 4f, Y = 3f, Z = 0f } },
                },
            };
            var js = new PriviewNetworkScene(new DisplayManagerAsync(), jointWorld, isServer: false, "127.0.0.1", 0, online: false);
            js.Start();
            bool jok = true;

            // SPAWN: select editable 0 (the first cube) as BodyA, then spawn a "joint".
            int cubeAId = js.EditableEntries[0].Descriptor.Id;
            js.SelectForTest(0);
            int beforeJoints = js.WorldJointsForTest.Count;
            js.SpawnTypeForTest("joint");
            var jEntry = js.EditableEntries.LastOrDefault(e => e.Descriptor.Type == "joint");
            bool spawned = js.WorldJointsForTest.Count == beforeJoints + 1
                && jEntry != null && jEntry.Joint != null
                && jEntry.Joint.Kind == "ballsocket" && jEntry.Joint.BodyA == cubeAId && jEntry.Joint.BodyB == -1
                && !jEntry.Instance.Collides;
            Console.WriteLine($"  joint-spawn: joints {beforeJoints}->{js.WorldJointsForTest.Count}, kind={jEntry?.Joint?.Kind}, A={jEntry?.Joint?.BodyA}(want {cubeAId}), B={jEntry?.Joint?.BodyB}(want -1), marker non-colliding={jEntry != null && !jEntry.Instance.Collides} -> {(spawned ? "ok" : "BAD")}");
            jok &= spawned;

            int jIndex = js.EditableEntries.ToList().FindIndex(e => ReferenceEquals(e, jEntry));

            // A hinge-only field is NOT in the set while the joint is a ball-socket (TypeFieldForTest returns
            // false for a field absent from the entry's FieldsFor set — the kind-dependent set proof).
            bool hingeFieldAbsent = !js.TypeFieldForTest(jIndex, "JLower", "0", confirm: false);

            // EDIT (kind swap → hinge): the hinge fields now appear in the set (typing into them succeeds).
            js.StepFieldForTest(jIndex, "JointKind", +1);   // ballsocket -> hinge
            string kAfter = js.FieldValueForTest(jIndex, "JointKind");
            bool lowerNowInSet = js.TypeFieldForTest(jIndex, "JLower", "-1", confirm: true);
            js.TypeFieldForTest(jIndex, "JUpper", "0.75", confirm: true);
            js.StepFieldForTest(jIndex, "JLimitEnabled", +1);   // toggle the limit on
            bool limit = jEntry!.Joint!.LimitEnabled
                && Math.Abs(jEntry.Joint.LowerLimit + 1f) < 1e-4f && Math.Abs(jEntry.Joint.UpperLimit - 0.75f) < 1e-4f;
            // retarget Body B to the second cube's id (typed exact via the numeric path).
            int cubeBId = js.EditableEntries[1].Descriptor.Id;
            js.TypeFieldForTest(jIndex, "JBodyB", cubeBId.ToString(), confirm: true);
            bool bodyB = jEntry.Joint.BodyB == cubeBId;
            // enable a motor + set its params.
            js.StepFieldForTest(jIndex, "JMotorEnabled", +1);
            js.TypeFieldForTest(jIndex, "JMotorSpeed", "2", confirm: true);
            js.TypeFieldForTest(jIndex, "JMaxTorque", "12", confirm: true);
            bool motor = jEntry.Joint.MotorEnabled
                && Math.Abs(jEntry.Joint.MotorTargetSpeed - 2f) < 1e-4f && Math.Abs(jEntry.Joint.MaxMotorTorque - 12f) < 1e-4f;
            bool hingeOk = kAfter == "hinge" && hingeFieldAbsent && lowerNowInSet && limit && bodyB && motor;
            Console.WriteLine($"  joint-hinge: kind={kAfter}, ballsocket-had-no-hinge-field={hingeFieldAbsent}, hinge-field-now-in-set={lowerNowInSet}, BodyB={jEntry.Joint.BodyB}(want {cubeBId}), limit={limit}, motor={motor} -> {(hingeOk ? "ok" : "BAD")}");
            jok &= hingeOk;

            // EDIT (kind swap → distance): its fields appear; set rest/freq/damping + toggle the spring.
            js.StepFieldForTest(jIndex, "JointKind", +1);   // hinge -> distance
            string kDist = js.FieldValueForTest(jIndex, "JointKind");
            js.TypeFieldForTest(jIndex, "JRestLength", "2.5", confirm: true);
            js.StepFieldForTest(jIndex, "JSpringEnabled", +1);
            js.TypeFieldForTest(jIndex, "JFrequency", "3", confirm: true);
            js.TypeFieldForTest(jIndex, "JDamping", "0.4", confirm: true);
            bool dist = kDist == "distance" && jEntry.Joint.SpringEnabled
                && Math.Abs(jEntry.Joint.RestLength - 2.5f) < 1e-4f
                && Math.Abs(jEntry.Joint.Frequency - 3f) < 1e-4f
                && Math.Abs(jEntry.Joint.DampingRatio - 0.4f) < 1e-4f;
            Console.WriteLine($"  joint-distance: kind={kDist}, rest={jEntry.Joint.RestLength:F2}, spring={jEntry.Joint.SpringEnabled}, freq={jEntry.Joint.Frequency:F2}, damp={jEntry.Joint.DampingRatio:F2} -> {(dist ? "ok" : "BAD")}");
            jok &= dist;

            // The edits mutated the LIVE cfg that lives in _world.Joints (same reference — rides save + bridge).
            bool liveRef = js.WorldJointsForTest.Count == 1 && ReferenceEquals(js.WorldJointsForTest[0], jEntry.Joint);
            Console.WriteLine($"  joint-live: cfg is the live _world.Joints[0] = {liveRef} -> {(liveRef ? "ok" : "BAD")}");
            jok &= liveRef;

            // DELETE: removing the joint entry drops the config from _world.Joints AND the entry from editables.
            int jIndexNow = js.EditableEntries.ToList().FindIndex(e => e.Descriptor.Type == "joint");
            js.DeleteSelectedForTest(jIndexNow);
            bool deleted = js.WorldJointsForTest.Count == 0 && !js.EditableEntries.Any(e => e.Descriptor.Type == "joint");
            Console.WriteLine($"  joint-delete: joints={js.WorldJointsForTest.Count} (want 0), entry gone={!js.EditableEntries.Any(e => e.Descriptor.Type == "joint")} -> {(deleted ? "ok" : "BAD")}");
            jok &= deleted;

            // LOAD ROUND-TRIP: a world that already carries a JointConfig builds a joint entry on Start,
            // backed by the live _world.Joints[0].
            var loadWorld = new WorldConfig
            {
                Name = "jointload",
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Color = "White" } },
                Joints = new List<JointConfig> { new JointConfig { Kind = "hinge", BodyA = 1, BodyB = -1,
                    Axis = new Vec3Config { X = 0f, Y = 0f, Z = 1f } } },
            };
            var ls = new PriviewNetworkScene(new DisplayManagerAsync(), loadWorld, isServer: false, "127.0.0.1", 0, online: false);
            ls.Start();
            var loadedJoint = ls.EditableEntries.FirstOrDefault(e => e.Descriptor.Type == "joint");
            bool loaded = loadedJoint != null && loadedJoint.Joint != null && loadedJoint.Joint.Kind == "hinge"
                && ls.WorldJointsForTest.Count == 1 && ReferenceEquals(loadedJoint.Joint, ls.WorldJointsForTest[0]);
            Console.WriteLine($"  joint-load: entry built={loadedJoint != null}, kind={loadedJoint?.Joint?.Kind}, live-ref={(loadedJoint != null && ls.WorldJointsForTest.Count == 1 && ReferenceEquals(loadedJoint.Joint, ls.WorldJointsForTest[0]))} -> {(loaded ? "ok" : "BAD")}");
            jok &= loaded;

            ok &= jok;
        }

        // F1 — joint scene-bridge fix: STATIC/KINEMATIC anchors + visible status (the v1.0.1 "joints unusable
        // from the editor" bug — a joint side that wasn't a live DYNAMIC body used to resolve to null, so the
        // WHOLE joint was silently inert; a light/static prop/the platform could never be an anchor). World
        // gravity is ON for the bridge cases (the physics step runs).
        {
            bool f1 = true;

            // --- PURE JointStatusReason: priority order (gravity off beats deleted beats both-static; else null) ---
            bool prG    = PriviewNetworkScene.JointStatusReason(false, true, true, false, false) == "world gravity is off";
            bool prGwin = PriviewNetworkScene.JointStatusReason(false, false, false, true, true) == "world gravity is off";   // beats deleted + both-static
            bool prDel  = PriviewNetworkScene.JointStatusReason(true, false, true, false, false) == "a referenced body was deleted";
            bool prStat = PriviewNetworkScene.JointStatusReason(true, true, true, true, true) == "both sides are static — nothing to move";
            bool prAct  = PriviewNetworkScene.JointStatusReason(true, true, true, false, true) == null;   // one dynamic side -> active
            bool prAct2 = PriviewNetworkScene.JointStatusReason(true, true, true, true, false) == null;
            bool pureOk = prG && prGwin && prDel && prStat && prAct && prAct2;
            Console.WriteLine($"  joint-status-pure: gravity={prG}&{prGwin}, deleted={prDel}, both-static={prStat}, active={prAct}&{prAct2} -> {(pureOk ? "ok" : "BAD")}");
            f1 &= pureOk;

            // Scene helper: gravity ON, platform enabled (id 0 = a STATIC collidable), a dynamic cube (id 1) +
            // a LIGHT (id 2, a non-colliding => STATIC side). Caller supplies the joint list.
            PriviewNetworkScene MakeScene(List<JointConfig> joints, bool light = true)
            {
                var objs = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Color = "White", Gravity = true, Position = new Vec3Config { X = 0f, Y = 8.5f, Z = 0f } } };
                if (light) objs.Add(new WorldObject { Id = 2, Type = "light", Color = "White", Position = new Vec3Config { X = 0f, Y = 9f, Z = 0f } });
                var s = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig
                {
                    Name = "jointbridge",
                    Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 10f },
                    Platform = new PlatformConfig { Enabled = true, Size = 40f, Position = new Vec3Config { Y = -2f } },
                    Objects = objs,
                    Joints = joints,
                }, isServer: false, "127.0.0.1", 0, online: false);
                s.Start();
                return s;
            }
            void Step(PriviewNetworkScene s, int n) { for (int i = 0; i < n; i++) s.StepPhysicsForTest(1f / 60f); }
            bool Finite(Vector3 v) => !(float.IsNaN(v.X) || float.IsInfinity(v.X) || float.IsNaN(v.Y) || float.IsInfinity(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.Z));

            // (1) HOLD: a ballsocket cube<->light pins the cube's origin to the light's. The cube (below the
            // light, under gravity) is pulled up and HELD there — the two world anchors coincide + status is
            // active. If the joint were inert (the bug), the cube would fall ~11 units to the platform.
            var hs = MakeScene(new List<JointConfig> { new JointConfig { Id = 50, Kind = "ballsocket", BodyA = 1, BodyB = 2,
                AnchorA = new Vec3Config(), AnchorB = new Vec3Config() } });
            Step(hs, 400);
            var (ha, hb) = hs.JointAnchorsForTest(50);
            bool holds = (ha - hb).Length() < 0.1f && hs.JointStatusForTest(50) == null;
            Console.WriteLine($"  joint-hold: |anchorA-anchorB|={(ha - hb).Length():F3} (<0.1), status={hs.JointStatusForTest(50) ?? "active"} -> {(holds ? "ok" : "BAD")}");
            f1 &= holds;

            // (2) KINEMATIC DRAG: teleport the LIGHT anchor (+1 in X). The moving anchor WAKES + drags the
            // (settled) pinned cube, so after settling the two anchors coincide again at the new spot.
            float beforeX = hs.JointAnchorsForTest(50).a.X;
            int li = hs.EditableEntries.ToList().FindIndex(e => e.Descriptor.Id == 2);
            var lightInst = hs.EditableEntries[li].Instance;
            lightInst.Position += new Vector3(1f, 0f, 0f);
            if (lightInst is Object3d lo) lo.UpdateGeometry();
            Step(hs, 150);
            var (da, db) = hs.JointAnchorsForTest(50);
            bool follows = (da - db).Length() < 0.12f && (da.X - beforeX) > 0.7f;   // coincident again + moved with the light
            Console.WriteLine($"  joint-drag: cubeAnchorX {beforeX:F2}->{da.X:F2} (followed the light), |a-b|={(da - db).Length():F3} -> {(follows ? "ok" : "BAD")}");
            f1 &= follows;

            // (3) BOTH-STATIC (platform<->world) is INACTIVE with the both-static reason (no NaN, keeps
            // stepping); re-pointing BodyA at the dynamic cube flips the status to ACTIVE.
            var bs = MakeScene(new List<JointConfig> { new JointConfig { Id = 60, Kind = "ballsocket", BodyA = 0, BodyB = -1,
                AnchorA = new Vec3Config(), AnchorB = new Vec3Config { Y = 3f } } });
            Step(bs, 30);
            bool bothStatic = bs.JointStatusForTest(60) == "both sides are static — nothing to move";
            bs.WorldJointsForTest[0].BodyA = 1;   // flip BodyA to the dynamic cube (live cfg ref)
            Step(bs, 30);
            bool flipped = bs.JointStatusForTest(60) == null && Finite(bs.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position);
            bool bsOk = bothStatic && flipped;
            Console.WriteLine($"  joint-both-static: platform<->world inactive={bothStatic}, after-flip-to-cube active={flipped} -> {(bsOk ? "ok" : "BAD")}");
            f1 &= bsOk;

            // (4) DELETED anchor: delete the light the cube hangs from -> the joint reports the deleted-body
            // reason and the sim stays finite (never NaNs; the cube just falls free).
            var ds = MakeScene(new List<JointConfig> { new JointConfig { Id = 70, Kind = "ballsocket", BodyA = 1, BodyB = 2,
                AnchorA = new Vec3Config(), AnchorB = new Vec3Config() } });
            Step(ds, 60);
            int li2 = ds.EditableEntries.ToList().FindIndex(e => e.Descriptor.Id == 2);
            ds.DeleteSelectedForTest(li2);   // remove the light (the anchor side)
            Step(ds, 60);
            bool deletedReason = ds.JointStatusForTest(70) == "a referenced body was deleted"
                && Finite(ds.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position);
            Console.WriteLine($"  joint-deleted-anchor: status='{ds.JointStatusForTest(70)}', cube finite -> {(deletedReason ? "ok" : "BAD")}");
            f1 &= deletedReason;

            // (5) GRAVITY OFF: the master switch is off so the physics step never runs — the status must still be
            // meaningful (computed at read time), not a stale/blank state.
            var gs = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig
            {
                Name = "jointgravoff",
                Physics = new PhysicsConfig { GravityEnabled = false },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { Y = 4f } } },
                Joints = new List<JointConfig> { new JointConfig { Id = 80, Kind = "ballsocket", BodyA = 1, BodyB = -1 } },
            }, isServer: false, "127.0.0.1", 0, online: false);
            gs.Start();
            gs.StepPhysicsForTest(1f / 60f);   // no-op (gravity off)
            bool gravOff = gs.JointStatusForTest(80) == "world gravity is off";
            Console.WriteLine($"  joint-gravity-off: status='{gs.JointStatusForTest(80)}' -> {(gravOff ? "ok" : "BAD")}");
            f1 &= gravOff;

            ok &= f1;
        }

        // F2 — joint × solver INTEGRATION defects (bridge-level): assembly snap, retarget anchor remap,
        // collideConnected. World gravity ON, collision ON.
        {
            bool f2 = true;
            void Step(PriviewNetworkScene s, int n) { for (int i = 0; i < n; i++) s.StepPhysicsForTest(1f / 60f); }
            bool Finite(Vector3 v) => !(float.IsNaN(v.X) || float.IsInfinity(v.X) || float.IsNaN(v.Y) || float.IsInfinity(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.Z));
            PriviewNetworkScene Scene(List<WorldObject> objs, List<JointConfig> joints, bool platform = false) =>
                new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig
                {
                    Name = "f2",
                    Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 10f, CollisionEnabled = true },
                    Platform = new PlatformConfig { Enabled = platform, Size = 40f, Position = new Vec3Config { Y = -3f } },
                    Objects = objs,
                    Joints = joints,
                }, isServer: false, "127.0.0.1", 0, online: false);

            // (A1) ASSEMBLY SNAP — ballsocket to a FAR world point coincides in ONE physics step (no creep).
            var sa = Scene(
                new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 8f, Z = 0f } } },
                new List<JointConfig> { new JointConfig { Id = 90, Kind = "ballsocket", BodyA = 1, BodyB = -1,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config { X = 5f, Y = 8f, Z = 0f } } });
            sa.Start();
            sa.StepPhysicsForTest(1f / 60f);   // ONE step
            var (ba0, ba1) = sa.JointAnchorsForTest(90);
            Vector3 saCube = sa.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position;
            bool snapBall = (ba0 - ba1).Length() <= 0.06f && Finite(saCube);
            Console.WriteLine($"  joint-snap-ball: |a-b| after 1 step={(ba0 - ba1).Length():F4} (<=0.06), finite={Finite(saCube)} -> {(snapBall ? "ok" : "BAD")}");
            f2 &= snapBall;

            // (A2) ASSEMBLY SNAP — distance snaps to RestLength in ONE step.
            var sd = Scene(
                new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 8f, Z = 0f } } },
                new List<JointConfig> { new JointConfig { Id = 91, Kind = "distance", BodyA = 1, BodyB = -1, RestLength = 3f,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config { X = 5f, Y = 8f, Z = 0f } } });
            sd.Start();
            sd.StepPhysicsForTest(1f / 60f);
            var (da0, da1) = sd.JointAnchorsForTest(91);
            float sdDist = (da0 - da1).Length();
            bool snapDist = MathF.Abs(sdDist - 3f) <= 0.1f;
            Console.WriteLine($"  joint-snap-dist: sep after 1 step={sdDist:F4} (RestLength 3 ±0.1) -> {(snapDist ? "ok" : "BAD")}");
            f2 &= snapDist;

            // (B) RETARGET REMAP — a joint anchored to a WORLD point, retargeted onto a real cube via the field
            // path, keeps its anchor's WORLD position (no sideways lunge).
            var rm = Scene(
                new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Position = new Vec3Config { X = 3f, Y = 2f, Z = 1f } },
                },
                new List<JointConfig> { new JointConfig { Id = 92, Kind = "ballsocket", BodyA = 1, BodyB = -1,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config { X = 4f, Y = 6f, Z = 0f } } });
            rm.Start();
            Vector3 worldBefore = rm.JointAnchorsForTest(92).b;   // AnchorB world point before retarget
            int jIdx = rm.EditableEntries.ToList().FindIndex(e => e.Joint != null && e.Joint.Id == 92);
            rm.TypeFieldForTest(jIdx, "JBodyB", "2", confirm: true);   // retarget onto cube #2 via the numeric field
            Vector3 worldAfter = rm.JointAnchorsForTest(92).b;
            int newBodyB = rm.WorldJointsForTest.First(j => j.Id == 92).BodyB;
            bool remapOk = (worldAfter - worldBefore).Length() < 1e-3f && newBodyB == 2;
            Console.WriteLine($"  joint-retarget: worldAnchorB ({worldBefore.X:F2},{worldBefore.Y:F2},{worldBefore.Z:F2})->({worldAfter.X:F2},{worldAfter.Y:F2},{worldAfter.Z:F2}), BodyB={newBodyB}(want 2) -> {(remapOk ? "ok" : "BAD")}");
            f2 &= remapOk;

            // (C) collideConnected — a dynamic cube #1 dropped from just above a static collidable cube #2, but
            // ball-socketed centre-to-centre: the pin pulls it DOWN into full overlap and the excluded contact
            // never pushes it back out — the two centres END coincident (distance ~0), no NaN.
            Func<PriviewNetworkScene, float> pairDist = s =>
                (s.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position
                 - s.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.Position).Length();
            var cc = Scene(
                new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 6.5f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Gravity = false, Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f } },
                },
                new List<JointConfig> { new JointConfig { Id = 93, Kind = "ballsocket", BodyA = 1, BodyB = 2,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config() } });
            cc.Start();
            Step(cc, 120);
            Vector3 ccCube = cc.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position;
            bool jointedOverlap = pairDist(cc) < 0.1f && Finite(ccCube);   // pinned into overlap, NOT ejected by the (excluded) contact
            Console.WriteLine($"  joint-collideconn: jointed centre distance={pairDist(cc):F4} (<0.1, not pushed out), finite={Finite(ccCube)} -> {(jointedOverlap ? "ok" : "BAD")}");
            f2 &= jointedOverlap;

            // control: the SAME drop WITHOUT a joint RESTS ON TOP (contacts intact — it does NOT fall through the
            // static cube), ending well separated (~1 unit, resting).
            var nj = Scene(
                new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 6.5f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Gravity = false, Position = new Vec3Config { X = 0f, Y = 5f, Z = 0f } },
                },
                new List<JointConfig>());
            nj.Start();
            Step(nj, 180);
            Vector3 njCube = nj.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position;
            bool njResolved = pairDist(nj) > 0.7f && Finite(njCube);   // rested on top (sep ~1.0), did NOT fall through
            Console.WriteLine($"  joint-collideconn-control: non-jointed centre distance={pairDist(nj):F3} (>0.7 rested on top, contacts intact) -> {(njResolved ? "ok" : "BAD")}");
            f2 &= njResolved;

            // (F3-A) GRAVITY RE-TOGGLE must NOT teleport a jointed body into its anchor (the F2 snap-on-every-
            // rebuild bug). Satisfied distance joint (static anchor #1 + dynamic bob #2, RestLength 2). Toggle #2
            // gravity OFF, move it +4 in X, toggle ON: it is pulled back DYNAMICALLY — no single-frame teleport
            // (per-frame step bounded), never sleeps while violated, converges within ~2 s.
            var gt = Scene(
                new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = false, Position = new Vec3Config { X = 0f, Y = 8f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Gravity = true,  Position = new Vec3Config { X = 0f, Y = 6f, Z = 0f } },
                },
                new List<JointConfig> { new JointConfig { Id = 95, Kind = "distance", BodyA = 1, BodyB = 2, RestLength = 2f,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config() } });
            gt.Start();
            Step(gt, 60);   // settle the authored (already-snapped) joint
            int bi = gt.EditableEntries.ToList().FindIndex(e => e.Descriptor.Id == 2);
            gt.StepFieldForTest(bi, "Gravity", +1);   // #2 gravity OFF (field path — the real editor toggle)
            Step(gt, 5);
            var binst = gt.EditableEntries[bi].Instance;
            binst.Position += new Vector3(4f, 0f, 0f);            // drag it a few units away while inactive
            if (binst is Object3d bo) bo.UpdateGeometry();
            gt.StepFieldForTest(bi, "Gravity", +1);   // #2 gravity ON again -> rebuild, but must NOT re-snap
            const float rtTol = 0.05f, rtRest = 2f;
            float maxPerFrame = 0f; bool sleptWhileViolated = false;
            Vector3 prevPos = gt.EditableEntries[bi].Instance.Position;
            for (int i = 0; i < 120; i++)   // 2 s
            {
                gt.StepPhysicsForTest(1f / 60f);
                Vector3 nowPos = gt.EditableEntries[bi].Instance.Position;
                maxPerFrame = MathF.Max(maxPerFrame, (nowPos - prevPos).Length());
                prevPos = nowPos;
                var (wa, wb) = gt.JointAnchorsForTest(95);
                float viol = MathF.Abs((wa - wb).Length() - rtRest);
                if (viol > rtTol && gt.BodySleepingForTest(2) == true) sleptWhileViolated = true;
            }
            var (fa, fb) = gt.JointAnchorsForTest(95);
            float finalViol = MathF.Abs((fa - fb).Length() - rtRest);
            // A teleport would move ~4 units in one frame; the dynamic pull is bounded by MaxBiasSpeed·dt (~0.067)
            // plus gravity/swing slack — 0.35 comfortably separates the two.
            bool noTeleport = maxPerFrame < 0.35f;
            bool converged = finalViol <= rtTol;
            bool retoggleOk = noTeleport && !sleptWhileViolated && converged;
            Console.WriteLine($"  joint-gravity-retoggle: maxPerFrame={maxPerFrame:F4} (<0.35 no teleport), sleptWhileViolated={sleptWhileViolated}, finalViol={finalViol:F4} (<={rtTol}) -> {(retoggleOk ? "ok" : "BAD")}");
            f2 &= retoggleOk;

            // (F3-A) a RUNTIME joint-param EDIT rebuilds the joint (joint-edit liveness) but does NOT re-snap
            // (cfg already snap-evaluated) — it converges DYNAMICALLY. Author a satisfied distance joint (rod
            // length 2), then edit RestLength -> 4 via the field path: the bob must extend to the new length with
            // NO single-frame teleport, and the edit must actually take effect (rod length becomes 4).
            var re = Scene(
                new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = false, Position = new Vec3Config { X = 0f, Y = 8f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Gravity = true,  Position = new Vec3Config { X = 0f, Y = 6f, Z = 0f } },
                },
                new List<JointConfig> { new JointConfig { Id = 96, Kind = "distance", BodyA = 1, BodyB = 2, RestLength = 2f,
                    AnchorA = new Vec3Config(), AnchorB = new Vec3Config() } });
            re.Start();
            Step(re, 60);   // settle at rod length 2
            int rjIdx = re.EditableEntries.ToList().FindIndex(e => e.Joint != null && e.Joint.Id == 96);
            int rbi = re.EditableEntries.ToList().FindIndex(e => e.Descriptor.Id == 2);
            re.TypeFieldForTest(rjIdx, "JRestLength", "4", confirm: true);   // edit rod length 2 -> 4 (invalidates + rebuilds, no re-snap)
            float reMaxPerFrame = 0f;
            Vector3 rePrev = re.EditableEntries[rbi].Instance.Position;
            for (int i = 0; i < 180; i++)   // 3 s to extend + settle
            {
                re.StepPhysicsForTest(1f / 60f);
                Vector3 reNow = re.EditableEntries[rbi].Instance.Position;
                reMaxPerFrame = MathF.Max(reMaxPerFrame, (reNow - rePrev).Length());
                rePrev = reNow;
            }
            var (rea, reb) = re.JointAnchorsForTest(96);
            float reFinalLen = (rea - reb).Length();
            bool editNoSnap = reMaxPerFrame < 0.35f && MathF.Abs(reFinalLen - 4f) <= 0.05f;   // no teleport, converged to the NEW length
            Console.WriteLine($"  joint-edit-nosnap: maxPerFrame={reMaxPerFrame:F4} (<0.35 no teleport), finalRodLen={reFinalLen:F4} (edit 2->4 applied dynamically) -> {(editNoSnap ? "ok" : "BAD")}");
            f2 &= editNoSnap;

            // (F4) per-joint COLLIDE (Box2D collideConnected). A SURFACE-anchored ballsocket between two cubes;
            // cube 1 is world-pinned in place, cube 2 hangs from it via the tested joint. Gravity STRENGTH 0 (the
            // master switch stays on so the solver steps) so overlap is the natural rest with the flag off, and a
            // fold (180° about Z) into overlap tests whether the pair collides.
            PriviewNetworkScene CollideScene(bool collide) => new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig
            {
                Name = "collide",
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 0f, CollisionEnabled = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Gravity = true, Position = new Vec3Config { X = 0f, Y = 6f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Gravity = true, Position = new Vec3Config { X = 2f, Y = 6f, Z = 0f } },
                },
                Joints = new List<JointConfig>
                {
                    new JointConfig { Id = 96, Kind = "ballsocket", BodyA = -1, BodyB = 1,   // pin cube 1's centre to a WORLD point (stable; -1 side -> no pair)
                        AnchorA = new Vec3Config { X = 0f, Y = 6f, Z = 0f }, AnchorB = new Vec3Config() },
                    new JointConfig { Id = 97, Kind = "ballsocket", BodyA = 1, BodyB = 2,   // the TESTED pair: surface anchors (cube1 +X face <-> cube2 -X face)
                        AnchorA = new Vec3Config { X = 1f, Y = 0f, Z = 0f }, AnchorB = new Vec3Config { X = -1f, Y = 0f, Z = 0f }, Collide = collide },
                },
            }, isServer: false, "127.0.0.1", 0, online: false);
            float CPairDist(PriviewNetworkScene s) =>
                (s.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position
                 - s.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.Position).Length();

            // (F4-on) flag ON: fold cube 2 into overlap with cube 1 -> the contact opens them back to non-overlap
            // (penetration resolves), the pin still holds, finite.
            var con = CollideScene(true);
            con.Start();
            con.StepPhysicsForTest(1f / 60f);   // create solver bodies
            Vector3 a1 = con.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position;
            con.SetBodyPoseForTest(2, a1 + new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0f, MathF.PI));   // fold cube 2 onto cube 1
            for (int i = 0; i < 240; i++) con.StepPhysicsForTest(1f / 60f);
            var (cga, cgb) = con.JointAnchorsForTest(97);
            float conDist = CPairDist(con);
            Vector3 conP = con.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.Position;
            bool onSep = conDist > 1.5f && (cga - cgb).Length() < 0.15f && Finite(conP);   // separated to contact + pin holds
            Console.WriteLine($"  joint-collide-on: after fold pairDist={conDist:F3} (>1.5 separated), pinGap={(cga - cgb).Length():F4} (<0.15 holds), finite={Finite(conP)} -> {(onSep ? "ok" : "BAD")}");
            f2 &= onSep;

            // (F4-off control) flag OFF: the SAME fold stays free to overlap (the pair is excluded from contacts).
            var coff = CollideScene(false);
            coff.Start();
            coff.StepPhysicsForTest(1f / 60f);
            Vector3 a1b = coff.EditableEntries.First(e => e.Descriptor.Id == 1).Instance.Position;
            coff.SetBodyPoseForTest(2, a1b + new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0f, MathF.PI));
            for (int i = 0; i < 240; i++) coff.StepPhysicsForTest(1f / 60f);
            float offDist = CPairDist(coff);
            bool offOverlap = offDist < 0.5f;   // stayed overlapping (contact excluded)
            Console.WriteLine($"  joint-collide-off: after fold pairDist={offDist:F3} (<0.5 still overlapping, excluded) -> {(offOverlap ? "ok" : "BAD")}");
            f2 &= offOverlap;

            // (F4 live-toggle) the NoCollide refill reflects a live Collide field toggle on the next step.
            var clt = CollideScene(false);
            clt.Start();
            clt.StepPhysicsForTest(1f / 60f);
            bool excludedBefore = clt.NoCollidePairForTest(1, 2);   // Collide=false -> excluded
            int cjidx = clt.EditableEntries.ToList().FindIndex(e => e.Joint != null && e.Joint.Id == 97);
            clt.StepFieldForTest(cjidx, "JCollide", +1);   // toggle Collide -> true via the field path
            clt.StepPhysicsForTest(1f / 60f);
            bool excludedAfter = clt.NoCollidePairForTest(1, 2);   // Collide=true -> NOT excluded
            bool toggleOk = excludedBefore && !excludedAfter;
            Console.WriteLine($"  joint-collide-toggle: excluded before={excludedBefore}, after JCollide on={excludedAfter} (want T/F) -> {(toggleOk ? "ok" : "BAD")}");
            f2 &= toggleOk;

            // (F5-B) hinge limit angles render degrees alongside radians (DISPLAY only — storage/sync/editing stay
            // radians). The overlay panel + docked inspector both format via this helper.
            bool fmtLo = PriviewNetworkScene.FormatLimitAngle(-1.57f) == "-1.57 (-90°)";
            bool fmtHi = PriviewNetworkScene.FormatLimitAngle(1.2f) == "1.20 (69°)";
            bool fmtZero = PriviewNetworkScene.FormatLimitAngle(0f) == "0.00 (0°)";
            bool fmtOk = fmtLo && fmtHi && fmtZero;
            Console.WriteLine($"  joint-limit-degrees: -1.57->'{PriviewNetworkScene.FormatLimitAngle(-1.57f)}', 1.2->'{PriviewNetworkScene.FormatLimitAngle(1.2f)}', 0->'{PriviewNetworkScene.FormatLimitAngle(0f)}' -> {(fmtOk ? "ok" : "BAD")}");
            f2 &= fmtOk;

            ok &= f2;
        }

        // F3 (Part B) — the TestLab generator produces a VALID, round-tripping world with the right joint
        // structure (all three kinds + a motor + a limit). BuildTestLab() is pure — this never touches worlds/.
        {
            bool tl = true;
            var lab = TestLab.BuildTestLab();

            // (1) VALIDATION via the load path: build the scene from it + Start (the same build a world load runs).
            // It must materialize every object + joint without crashing.
            var labScene = new PriviewNetworkScene(new DisplayManagerAsync(), lab, isServer: false, "127.0.0.1", 0, online: false);
            labScene.Start();
            bool built = labScene.WorldJointsForTest.Count == lab.Joints.Count && labScene.EditableEntries.Count >= lab.Objects.Count;
            Console.WriteLine($"  testlab-build: objects={lab.Objects.Count}, joints={labScene.WorldJointsForTest.Count}/{lab.Joints.Count}, editables={labScene.EditableEntries.Count} -> {(built ? "ok" : "BAD")}");
            tl &= built;

            // (2) ROUND-TRIP through save+load in a TEMP folder (never the real worlds/), then CompareWorlds.
            string tmp = Path.Combine(Path.GetTempPath(), $"nova3d_testlab_{Guid.NewGuid():N}.json");
            var wopts = new JsonSerializerOptions { WriteIndented = true };                    // mirrors WorldManager.WriteOptions
            var ropts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };      // mirrors WorldManager.ReadOptions
            string rtReason = "write failed";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(lab, wopts));
                var reloaded = JsonSerializer.Deserialize<WorldConfig>(File.ReadAllText(tmp), ropts);
                rtReason = reloaded == null ? "deserialized null" : (CompareWorlds(lab, reloaded) ?? "");
            }
            finally { try { File.Delete(tmp); } catch { } }
            bool roundtrip = rtReason == "";
            Console.WriteLine($"  testlab-roundtrip: temp save+load CompareWorlds -> {(roundtrip ? "ok" : "BAD: " + rtReason)}");
            tl &= roundtrip;

            // (3) JOINT STRUCTURE (v3): >=11 joints covering all three kinds, with >=1 motor, >=1 limit, and
            // >=3 Collide=true (the real-chain links).
            var kinds = new HashSet<string>(lab.Joints.Select(j => j.Kind));
            bool allKinds = kinds.Contains("ballsocket") && kinds.Contains("hinge") && kinds.Contains("distance");
            bool hasMotor = lab.Joints.Any(j => j.MotorEnabled);
            bool hasLimit = lab.Joints.Any(j => j.LimitEnabled);
            int collideCount = lab.Joints.Count(j => j.Collide);
            bool structure = lab.Joints.Count >= 11 && allKinds && hasMotor && hasLimit && collideCount >= 3;
            Console.WriteLine($"  testlab-joints: count={lab.Joints.Count}(>=11), kinds=[{string.Join(",", kinds)}] all3={allKinds}, motor={hasMotor}, limit={hasLimit}, collide={collideCount}(>=3) -> {(structure ? "ok" : "BAD")}");
            tl &= structure;

            // (4) the Collide=true CHAIN rests without perma-jitter: a stand-alone 3-link chain (same gap +
            // Collide as TestLab's J6) settles + SLEEPS within a few seconds (the marginal-touch gap prevents a
            // resting contact wake-loop). Fold-then-separate is already proven by joint-collide-on above.
            var chainW = new WorldConfig
            {
                Name = "chain",
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Scale = 0.4f, Gravity = false, Position = new Vec3Config { X = 0f, Y = 8f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Scale = 0.8f, Gravity = true, Position = new Vec3Config { X = 0f, Y = 6.5f, Z = 0f } },
                    new WorldObject { Id = 3, Type = "cube", Scale = 0.8f, Gravity = true, Position = new Vec3Config { X = 0f, Y = 4.9f, Z = 0f } },
                    new WorldObject { Id = 4, Type = "cube", Scale = 0.8f, Gravity = true, Position = new Vec3Config { X = 0f, Y = 3.3f, Z = 0f } },
                },
                Joints = new List<JointConfig>
                {
                    new JointConfig { Id = 10, Kind = "ballsocket", BodyA = 1, BodyB = 2, AnchorA = new Vec3Config { Y = -0.41f }, AnchorB = new Vec3Config { Y = 0.81f }, Collide = true },
                    new JointConfig { Id = 11, Kind = "ballsocket", BodyA = 2, BodyB = 3, AnchorA = new Vec3Config { Y = -0.81f }, AnchorB = new Vec3Config { Y = 0.81f }, Collide = true },
                    new JointConfig { Id = 12, Kind = "ballsocket", BodyA = 3, BodyB = 4, AnchorA = new Vec3Config { Y = -0.81f }, AnchorB = new Vec3Config { Y = 0.81f }, Collide = true },
                },
            };
            var chainS = new PriviewNetworkScene(new DisplayManagerAsync(), chainW, isServer: false, "127.0.0.1", 0, online: false);
            chainS.Start();
            for (int i = 0; i < 360; i++) chainS.StepPhysicsForTest(1f / 60f);   // 6 s
            bool chainSleeps = chainS.BodySleepingForTest(2) == true && chainS.BodySleepingForTest(3) == true && chainS.BodySleepingForTest(4) == true;
            Console.WriteLine($"  testlab-chain-rest: colliding chain sleeps l2={chainS.BodySleepingForTest(2)}, l3={chainS.BodySleepingForTest(3)}, l4={chainS.BodySleepingForTest(4)} (no jitter) -> {(chainSleeps ? "ok" : "BAD")}");
            tl &= chainSleeps;

            // (F4 door-Collide) TestLab's J4 door uses Collide=true (evaluated + adopted): a post+door hinge with
            // Collide=true still reaches its ±90° limit (the swing sweeps AWAY from the post) and doesn't jitter at
            // the stop. (A motor is added HERE only to drive it to the limit; TestLab's door has no motor and
            // rests stably — sleeps, jitter 0 — verified during evaluation.)
            var doorW = new WorldConfig
            {
                Name = "door",
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 1, Type = "cube", Scale = 0.3f, Gravity = false, Position = new Vec3Config { X = 0f, Y = 1.5f, Z = 0f } },
                    new WorldObject { Id = 2, Type = "cube", Scale = 0.6f, Gravity = true, Position = new Vec3Config { X = 1.2f, Y = 1.5f, Z = 0f } },
                },
                Joints = new List<JointConfig> { new JointConfig { Id = 10, Kind = "hinge", BodyA = 1, BodyB = 2,
                    Axis = new Vec3Config { Y = 1f }, AnchorA = new Vec3Config { X = 0.3f }, AnchorB = new Vec3Config { X = -0.6f },
                    LimitEnabled = true, LowerLimit = -1.57f, UpperLimit = 1.57f,
                    MotorEnabled = true, MotorTargetSpeed = 2f, MaxMotorTorque = 30f, Collide = true } },
            };
            var doorS = new PriviewNetworkScene(new DisplayManagerAsync(), doorW, isServer: false, "127.0.0.1", 0, online: false);
            doorS.Start();
            float doorMaxYaw = 0f;
            for (int i = 0; i < 180; i++)   // 3 s — the motor drives the door to its +limit
            {
                doorS.StepPhysicsForTest(1f / 60f);
                doorMaxYaw = MathF.Max(doorMaxYaw, MathF.Abs(doorS.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.LocalRotate.Y));
            }
            // now let it rest 3 s at the limit and measure jitter.
            float doorJitter = 0f; Vector3 dprev = doorS.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.Position;
            for (int i = 0; i < 180; i++)
            {
                doorS.StepPhysicsForTest(1f / 60f);
                Vector3 dnow = doorS.EditableEntries.First(e => e.Descriptor.Id == 2).Instance.Position;
                if (i >= 120) doorJitter = MathF.Max(doorJitter, (dnow - dprev).Length());
                dprev = dnow;
            }
            bool doorOk = doorMaxYaw > 1.4f && doorMaxYaw < 1.62f && doorJitter < 0.01f;   // reaches ~±90° limit + no jitter at the stop
            Console.WriteLine($"  testlab-door-collide: reachedYaw={doorMaxYaw:F3} (limit 1.57), stop jitter={doorJitter:F5} (<0.01) -> {(doorOk ? "ok" : "BAD")}");
            tl &= doorOk;

            // (F5-A) WINDMILL geometry: the blade (id 10) completes >=1 full revolution while its body NEVER
            // overlaps the post (id 9) — its rotation plane is offset in Z, so the boxes are always Z-separated
            // (a rotation about +Z can't change the Z extent). Simulate the BUILT TestLab windmill.
            var wm = new PriviewNetworkScene(new DisplayManagerAsync(), TestLab.BuildTestLab(), isServer: false, "127.0.0.1", 0, online: false);
            wm.Start();
            var wmPost = wm.EditableEntries.First(e => e.Descriptor.Id == 9).Instance;    // post  (Scale 0.3 -> half-Z 0.3)
            var wmBlade = wm.EditableEntries.First(e => e.Descriptor.Id == 10).Instance;  // blade (Scale 0.8 -> half-Z 0.8)
            float totalRot = 0f, prevRotZ = wmBlade.LocalRotate.Z, worstZOverlap = -999f;
            for (int i = 0; i < 420; i++)   // 7 s @ 1.2 rad/s -> ~8 rad > 2π
            {
                wm.StepPhysicsForTest(1f / 60f);
                float d = wmBlade.LocalRotate.Z - prevRotZ;
                while (d > MathF.PI) d -= 2f * MathF.PI; while (d < -MathF.PI) d += 2f * MathF.PI;   // shortest-arc delta
                totalRot += d; prevRotZ = wmBlade.LocalRotate.Z;
                float zOverlap = MathF.Min(wmBlade.Position.Z + 0.8f, wmPost.Position.Z + 0.3f)
                               - MathF.Max(wmBlade.Position.Z - 0.8f, wmPost.Position.Z - 0.3f);   // >0 = boxes overlap in Z
                worstZOverlap = MathF.Max(worstZOverlap, zOverlap);
            }
            bool spun = MathF.Abs(totalRot) > 2f * MathF.PI;   // completed >= 1 full revolution
            bool noPostOverlap = worstZOverlap < 0.005f;       // Z-separated throughout (Slop) -> the boxes never overlap
            bool windmillOk = spun && noPostOverlap;
            Console.WriteLine($"  testlab-windmill: totalRot={totalRot:F2} rad (>{2f * MathF.PI:F2} = 1 rev), worst blade/post Z-overlap={worstZOverlap:F3} (<Slop, cleared) -> {(windmillOk ? "ok" : "BAD")}");
            tl &= windmillOk;

            // (F5-D) TRAPDOOR: the lid (id 19) flops open under gravity ON LOAD and comes to REST at the hinge's
            // Upper limit (1.2 rad ≈ 69° about X — held BEFORE its 90° free hang), then settles — limits
            // demonstrated with no user input.
            var td = new PriviewNetworkScene(new DisplayManagerAsync(), TestLab.BuildTestLab(), isServer: false, "127.0.0.1", 0, online: false);
            td.Start();
            var tdLid = td.EditableEntries.First(e => e.Descriptor.Id == 19).Instance;
            for (int i = 0; i < 480; i++) td.StepPhysicsForTest(1f / 60f);   // 8 s -> settle at the stop
            float lastMove = 0f; Vector3 tdPrev = tdLid.Position;
            for (int i = 0; i < 120; i++)   // measure residual motion over the final 2 s
            {
                td.StepPhysicsForTest(1f / 60f);
                lastMove = MathF.Max(lastMove, (tdLid.Position - tdPrev).Length());
                tdPrev = tdLid.Position;
            }
            float lidAngle = MathF.Abs(tdLid.LocalRotate.X);
            bool lidAtStop = MathF.Abs(lidAngle - 1.2f) < 0.12f;   // settled at the Upper stop (1.2), held before its 90° free hang
            bool lidAsleep = td.BodySleepingForTest(19) == true;
            bool lidRested = lidAsleep || lastMove < 2e-3f;   // asleep, or at rest against the limit (a limit constraint can keep a body marginally awake)
            bool trapdoorOk = lidAtStop && lidRested;
            Console.WriteLine($"  testlab-trapdoor: lid angle={lidAngle:F3} (Upper 1.20 stop), asleep={lidAsleep}, residualMove={lastMove:F5} -> {(trapdoorOk ? "ok" : "BAD")}");
            tl &= trapdoorOk;

            // (F5 Collide evals — informational): windmill Collide=true (blade never contacts the post, so it's
            // a no-op) and trapdoor Collide=true (lid vs frame). Report full-rev/jam and settle/jitter evidence.
            {
                var wmC = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "wmC",
                    Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true },
                    Platform = new PlatformConfig { Enabled = false },
                    Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Scale = 0.3f, Gravity = false, Position = new Vec3Config { X = 0f, Y = 5f, Z = -8f } },
                        new WorldObject { Id = 2, Type = "cube", Scale = 0.8f, Gravity = true, Position = new Vec3Config { X = 0f, Y = 5f, Z = -6.8f } } },
                    Joints = new List<JointConfig> { new JointConfig { Id = 10, Kind = "hinge", BodyA = 1, BodyB = 2, Axis = new Vec3Config { Z = 1f },
                        AnchorA = new Vec3Config { Z = 1.2f }, AnchorB = new Vec3Config(), MotorEnabled = true, MotorTargetSpeed = 1.2f, MaxMotorTorque = 60f, Collide = true } } },
                    isServer: false, "127.0.0.1", 0, online: false);
                wmC.Start();
                var wb = wmC.EditableEntries.First(e => e.Descriptor.Id == 2).Instance; float rot = 0f, pz = wb.LocalRotate.Z;
                for (int i = 0; i < 420; i++) { wmC.StepPhysicsForTest(1f / 60f); float dd = wb.LocalRotate.Z - pz; while (dd > MathF.PI) dd -= 2f * MathF.PI; while (dd < -MathF.PI) dd += 2f * MathF.PI; rot += dd; pz = wb.LocalRotate.Z; }
                Console.WriteLine($"  [eval] windmill Collide=true: totalRot={rot:F2} rad (>2π = no jam) — moot (blade clears the post, no contact possible)");

                var tdC = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "tdC",
                    Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true },
                    Platform = new PlatformConfig { Enabled = false },
                    Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Scale = 0.5f, Gravity = false, Position = new Vec3Config { X = 0f, Y = 3f, Z = -8f } },
                        new WorldObject { Id = 2, Type = "cube", Scale = 0.5f, Gravity = true, Position = new Vec3Config { X = 0f, Y = 3f, Z = -6.98f } } },
                    Joints = new List<JointConfig> { new JointConfig { Id = 10, Kind = "hinge", BodyA = 1, BodyB = 2, Axis = new Vec3Config { X = 1f },
                        AnchorA = new Vec3Config { Z = 0.51f }, AnchorB = new Vec3Config { Z = -0.51f }, LimitEnabled = true, LowerLimit = 0f, UpperLimit = 1.2f, Collide = true } } },
                    isServer: false, "127.0.0.1", 0, online: false);
                tdC.Start();
                var tl2 = tdC.EditableEntries.First(e => e.Descriptor.Id == 2).Instance;
                for (int i = 0; i < 480; i++) tdC.StepPhysicsForTest(1f / 60f);
                float tj = 0f; Vector3 tp = tl2.Position;
                for (int i = 0; i < 120; i++) { tdC.StepPhysicsForTest(1f / 60f); tj = MathF.Max(tj, (tl2.Position - tp).Length()); tp = tl2.Position; }
                Console.WriteLine($"  [eval] trapdoor Collide=true: lidAngle={MathF.Abs(tl2.LocalRotate.X):F3} (stop 1.2), residualMove={tj:F5}, asleep={tdC.BodySleepingForTest(2)}");
            }

            ok &= tl;
        }

        // C1-4c — the joint marker is now a thin LINE mesh (not a placeholder sphere): its world bbox spans
        // both anchors and is thin perpendicular to the A→B axis; a hinge adds an axis stub (more geometry +
        // the bbox extends along the axis direction). Visual correctness (it reads as a coloured line) is
        // verified live — here we only assert the marker GEOMETRY.
        {
            var A = new Vector3(-1f, 0.5f, 2f);
            var B = new Vector3(3f, 0.5f, 2f);   // A→B along +X (extent 4)
            var ball = PriviewNetworkScene.BuildJointMarkerMesh(A, B, "ballsocket", null);
            ball.UpdateGeometry();
            Vector3 mn = ball.WorldMin, mx = ball.WorldMax;
            bool Contains(Vector3 p) =>
                mn.X - 1e-3f <= p.X && p.X <= mx.X + 1e-3f &&
                mn.Y - 1e-3f <= p.Y && p.Y <= mx.Y + 1e-3f &&
                mn.Z - 1e-3f <= p.Z && p.Z <= mx.Z + 1e-3f;
            float exX = mx.X - mn.X, exY = mx.Y - mn.Y, exZ = mx.Z - mn.Z;
            bool spans = Contains(A) && Contains(B);
            bool thin = exY < 0.2f && exZ < 0.2f && Math.Abs(exX - 4f) < 0.2f;   // ~2r perpendicular, ~|B−A| along the axis
            int ballTris = ball.FaceCount;
            bool ballOk = spans && thin && ballTris == 12;
            Console.WriteLine($"  joint-marker-ball: spans={spans}, thin(exX={exX:F2},exY={exY:F2},exZ={exZ:F2})={thin}, tris={ballTris}(want 12) -> {(ballOk ? "ok" : "BAD")}");
            bool mok = ballOk;

            // Hinge with a +Y axis: more geometry than the plain line, and the bbox extends along +Y (the stub).
            var hinge = PriviewNetworkScene.BuildJointMarkerMesh(A, B, "hinge", new Vector3(0f, 1f, 0f));
            hinge.UpdateGeometry();
            int hingeTris = hinge.FaceCount;
            float hingeExY = hinge.WorldMax.Y - hinge.WorldMin.Y;
            bool moreGeom = hingeTris > ballTris;
            bool axisExtends = hingeExY > 0.5f;   // the stub (halfLen 0.4 → ~0.8 span) grows Y beyond the thin tube
            bool hingeOk = moreGeom && axisExtends;
            Console.WriteLine($"  joint-marker-hinge: tris={hingeTris}(>ball {ballTris}={moreGeom}), Y-extent={hingeExY:F2}(axis-extends={axisExtends}) -> {(hingeOk ? "ok" : "BAD")}");
            mok &= hingeOk;

            ok &= mok;
        }

        // C1-5 — joint NETWORK SYNC: the JointConfig.Id + the pack path carrying joints (A2), the three
        // live-edit ops (JointSpawn/Modify/Delete) applied through the CLIENT handler, and a joining client
        // building joints from the received world (clearing any prior joint state).
        {
            bool cok = true;

            // A2: BuildLiveWorldConfig (the OnWorldRequested pack path) carries the LIVE joints by reference,
            // and a joint entry's Descriptor.Id equals its cfg.Id (one id everywhere).
            var authWorld = new WorldConfig
            {
                Name = "jointpack",
                Physics = new PhysicsConfig { GravityEnabled = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Color = "White", Gravity = true } },
            };
            var auth = new PriviewNetworkScene(new DisplayManagerAsync(), authWorld, isServer: false, "127.0.0.1", 0, online: false);
            auth.Start();
            auth.SelectForTest(0);
            auth.SpawnTypeForTest("joint");                       // authority spawn assigns cfg.Id
            var liveCfg = auth.WorldJointsForTest[0];
            var snap = auth.LiveWorldSnapshot();
            bool packRef = snap.Joints.Count == 1 && ReferenceEquals(snap.Joints[0], liveCfg);
            var authEntry = auth.EditableEntries.First(e => e.Descriptor.Type == "joint");
            bool packOk = packRef && liveCfg.Id >= 0 && authEntry.Descriptor.Id == liveCfg.Id;
            Console.WriteLine($"  joint-sync-pack: LiveConfig.Joints carries live cfg (ref={packRef}), cfg.Id={liveCfg.Id}, entryId==cfgId={authEntry.Descriptor.Id == liveCfg.Id} -> {(packOk ? "ok" : "BAD")}");
            cok &= packOk;

            // Client-apply of the three ops on a headless scene (crafted WorldEditPackets → the real handler).
            var client = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "jointclient",
                Physics = new PhysicsConfig { GravityEnabled = true }, Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Color = "White", Gravity = true } } },
                isServer: false, "127.0.0.1", 0, online: false);
            client.Start();

            // Op 4 (JointSpawn): a crafted cfg with a server id → entry built with Descriptor.Id == cfg.Id.
            var spawnCfg = new JointConfig { Id = 500, Kind = "ballsocket", BodyA = 1, BodyB = -1 };
            client.ApplyWorldEditForTest(new WorldEditPacket { Op = 4, Id = spawnCfg.Id, ObjectJson = JsonSerializer.Serialize(spawnCfg) });
            var je = client.EditableEntries.FirstOrDefault(e => e.Descriptor.Type == "joint");
            bool spawnOk = client.WorldJointsForTest.Count == 1 && je != null && je.Joint != null && je.Joint.Id == 500 && je.Descriptor.Id == 500;
            client.ApplyWorldEditForTest(new WorldEditPacket { Op = 4, Id = spawnCfg.Id, ObjectJson = JsonSerializer.Serialize(spawnCfg) });   // repeat → idempotent
            bool idemOk = client.WorldJointsForTest.Count == 1 && client.EditableEntries.Count(e => e.Descriptor.Type == "joint") == 1;
            Console.WriteLine($"  joint-sync-op4: entry id=500 built ({spawnOk}); repeat Op4 idempotent ({idemOk}) -> {((spawnOk && idemOk) ? "ok" : "BAD")}");
            cok &= spawnOk && idemOk;

            // Op 3 (JointModify): the LIVE cfg is mutated IN PLACE (SAME reference the bridge/marker hold).
            var liveJoint = client.EditableEntries.First(e => e.Descriptor.Type == "joint").Joint!;
            var modCfg = new JointConfig { Id = 500, Kind = "hinge", BodyA = 1, BodyB = -1,
                Axis = new Vec3Config { X = 0f, Y = 0f, Z = 1f }, LimitEnabled = true, LowerLimit = -0.7f, UpperLimit = 0.9f, Collide = true };   // F4: Collide rides the op-3 JSON
            client.ApplyWorldEditForTest(new WorldEditPacket { Op = 3, Id = 500, ObjectJson = JsonSerializer.Serialize(modCfg) });
            var liveJoint2 = client.EditableEntries.First(e => e.Descriptor.Type == "joint").Joint!;
            bool modOk = ReferenceEquals(liveJoint, liveJoint2) && liveJoint.Kind == "hinge" && liveJoint.LimitEnabled
                && Math.Abs(liveJoint.LowerLimit + 0.7f) < 1e-4f && Math.Abs(liveJoint.UpperLimit - 0.9f) < 1e-4f && liveJoint.Id == 500 && liveJoint.Collide;
            Console.WriteLine($"  joint-sync-op3: in-place (same-ref={ReferenceEquals(liveJoint, liveJoint2)}), kind={liveJoint.Kind}, limit={liveJoint.LimitEnabled}, collide={liveJoint.Collide}, id={liveJoint.Id} -> {(modOk ? "ok" : "BAD")}");
            cok &= modOk;

            // Op 5 (JointDelete): the entry + the cfg are gone.
            client.ApplyWorldEditForTest(new WorldEditPacket { Op = 5, Id = 500 });
            bool delOk = client.WorldJointsForTest.Count == 0 && !client.EditableEntries.Any(e => e.Descriptor.Type == "joint");
            Console.WriteLine($"  joint-sync-op5: joints={client.WorldJointsForTest.Count}, entry gone={!client.EditableEntries.Any(e => e.Descriptor.Type == "joint")} -> {(delOk ? "ok" : "BAD")}");
            cok &= delOk;

            // Join path: a scene that already had a joint receives a fresh world with two joints → the prior
            // joint state is cleared and two joint entries are built (Descriptor.Id == cfg.Id for each).
            var join = new PriviewNetworkScene(new DisplayManagerAsync(), new WorldConfig { Name = "pre",
                Physics = new PhysicsConfig { GravityEnabled = true }, Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Gravity = true } },
                Joints = new List<JointConfig> { new JointConfig { Id = 3, Kind = "ballsocket", BodyA = 1, BodyB = -1 } } },
                isServer: false, "127.0.0.1", 0, online: false);
            join.Start();
            bool hadPre = join.EditableEntries.Count(e => e.Descriptor.Type == "joint") == 1;
            var received = new WorldConfig { Name = "arrived", Physics = new PhysicsConfig { GravityEnabled = true },
                Platform = new PlatformConfig { Enabled = false },
                Objects = new List<WorldObject> { new WorldObject { Id = 7, Type = "cube", Gravity = true }, new WorldObject { Id = 8, Type = "sphere", Radius = 1f } },
                Joints = new List<JointConfig>
                {
                    new JointConfig { Id = 40, Kind = "hinge", BodyA = 7, BodyB = -1, Axis = new Vec3Config { X = 0f, Y = 1f, Z = 0f } },
                    new JointConfig { Id = 41, Kind = "distance", BodyA = 8, BodyB = 7, RestLength = 2f },
                } };
            join.ApplyReceivedWorldForTest(received);
            var joinJoints = join.EditableEntries.Where(e => e.Descriptor.Type == "joint").ToList();
            bool joinOk = hadPre && joinJoints.Count == 2 && join.WorldJointsForTest.Count == 2
                && joinJoints.Any(e => e.Joint!.Id == 40 && e.Joint.Kind == "hinge")
                && joinJoints.Any(e => e.Joint!.Id == 41 && e.Joint.Kind == "distance")
                && joinJoints.All(e => e.Descriptor.Id == e.Joint!.Id);
            Console.WriteLine($"  joint-sync-join: pre-had-joint={hadPre}, after-arrival joints={joinJoints.Count} (ids {string.Join(",", joinJoints.Select(e => e.Joint!.Id))}) -> {(joinOk ? "ok" : "BAD")}");
            cok &= joinOk;

            ok &= cok;
        }

        // ---- C2-5: dynamic MESH object -> real DynamicHull bridge (a gravity+collides mesh simulates as its true
        //      convex hull, not a loose OBB; a box-like PRIMITIVE stays an OBB; a scale edit rebuilds the hull). ----
        {
            bool bok = true;
            // Headless world: a floor platform + a gravity+collides MESH (monkey) dropped from height + a
            // gravity+collides cube PRIMITIVE dropped alongside (offset in X so they don't touch).
            WorldConfig BridgeWorld() => new WorldConfig
            {
                Name = "c25bridge", Graphics = new GraphicsConfig { Shadows = false },
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 60f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true, Restitution = 0f },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 10, Type = "mesh", Mesh = "monkey", Color = "White",
                        Position = new Vec3Config { X = 0f, Y = 4f, Z = 0f }, Scale = 1f, Collides = true, Gravity = true },
                    new WorldObject { Id = 11, Type = "cube", Color = "Red",
                        Position = new Vec3Config { X = 8f, Y = 4f, Z = 0f }, Scale = 1f, Collides = true, Gravity = true },
                },
            };
            var sc = new PriviewNetworkScene(new DisplayManagerAsync(), BridgeWorld(), isServer: false, "127.0.0.1", 0, online: false);
            sc.Start();
            // the scene assigns fresh ids on load, so resolve by descriptor Type, not the config's Id.
            var meshE = sc.EditableEntries.First(e => e.Descriptor.Type == "mesh");
            var cubeE = sc.EditableEntries.First(e => e.Descriptor.Type == "cube");
            int meshId = meshE.Descriptor.Id, cubeId = cubeE.Descriptor.Id;

            var meshObj = (Object3d)meshE.Instance;
            float startY = meshObj.Position.Y;
            float dt = 1f / 60f;
            for (int i = 0; i < 300; i++) sc.StepPhysicsForTest(dt);          // ~5 s: fall + settle (+ sleep)

            // (1) the MESH object simulates as a HULL (not a loose OBB); the CUBE primitive stays a Box.
            var meshKind = sc.BodyKindForTest(meshId);
            var cubeKind = sc.BodyKindForTest(cubeId);
            bool meshIsHull = meshKind == ColliderKind.Hull;
            bool cubeIsBox = cubeKind == ColliderKind.Box;

            // (2) the mesh FELL and now RESTS: dropped well below its start, and over the next frames it's still.
            Vector3 before = meshObj.Position;
            for (int i = 0; i < 30; i++) sc.StepPhysicsForTest(dt);
            float restJitter = (meshObj.Position - before).Length();
            bool fell = meshObj.Position.Y < startY - 1f;
            bool rests = restJitter < 0.01f;
            // (3) NO COM DRIFT: the mesh's lowest world vertex sits ON the floor (y≈0). WorldMin is geometry-based,
            //     so an inconsistent COM offset (place vs sync-back) would leave the mesh off the floor by that
            //     offset. This proves the hull place/sync round-trips for ANY mesh shape.
            bool onFloor = MathF.Abs(meshObj.WorldMin.Y) < 0.1f;
            bool bounded = MathF.Abs(meshObj.Position.X) < 5f && MathF.Abs(meshObj.Position.Z) < 5f;
            bool fallOk = meshIsHull && cubeIsBox && fell && rests && onFloor && bounded;
            Console.WriteLine($"  c25-mesh-falls-as-hull: meshKind={meshKind}(Hull), cubeKind={cubeKind}(Box), y {startY:F2}->{meshObj.Position.Y:F2} (fell={fell}), jitter={restJitter:F5} (rests={rests}), worldMinY={meshObj.WorldMin.Y:F4} (onFloor={onFloor}) -> {(fallOk ? "ok" : "BAD")}");
            bok &= fallOk;

            // (4) SCALE REBUILD: editing the mesh's Scale rebuilds the hull from the new scaled locals (the cached
            //     build-scale follows o.Scale) WITHOUT losing that it's a Hull. Verified via the build-scale hook +
            //     Kind (the rebuild fires regardless of sleep state; velocity/sleep are preserved per the bridge).
            meshObj.Scale = 1.5f; meshObj.UpdateGeometry();
            sc.StepPhysicsForTest(dt);                                        // next step rebuilds the hull at the new scale
            bool stillHull = sc.BodyKindForTest(meshId) == ColliderKind.Hull;
            bool rebuilt = MathF.Abs(sc.HullBuiltScaleForTest(meshId) - 1.5f) < 1e-4f;
            bool scaleOk = stillHull && rebuilt;
            Console.WriteLine($"  c25-scale-rebuild: builtScale={sc.HullBuiltScaleForTest(meshId):F3}(1.5), stillHull={stillHull} -> {(scaleOk ? "ok" : "BAD")}");
            bok &= scaleOk;

            ok &= bok;
        }

        // ---- C4: the player CAPSULE collision (vertical segment + radius, resolved via the REUSED sphere
        //      resolvers). Real body height: stands on the floor, blocked by walls, blocked by head- and
        //      foot-height obstacles a lone centre-sphere would miss. Driven headless via the C4 ForTest hooks. ----
        {
            bool pok = true;
            float half = PriviewNetworkScene.CapsuleHalfForTest;    // 0.55
            float rad = PriviewNetworkScene.CapsuleRadiusForTest;   // 0.35
            // A floor platform (top at y≈0) + one movable collidable box obstacle repositioned per sub-test.
            WorldConfig CapWorld() => new WorldConfig
            {
                Name = "c4cap", Graphics = new GraphicsConfig { Shadows = false },
                Platform = new PlatformConfig { Enabled = true, Shape = "square", Size = 60f, Color = "Gray", Position = new Vec3Config { X = 0f, Y = 0f, Z = 0f } },
                Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 9.8f, CollisionEnabled = true },
                Objects = new List<WorldObject>
                {
                    new WorldObject { Id = 20, Type = "cube", Color = "White",
                        Position = new Vec3Config { X = 100f, Y = 100f, Z = 100f }, Scale = 2f, Collides = true, Gravity = false },   // parked far away
                },
            };
            var pc = new PriviewNetworkScene(new DisplayManagerAsync(), CapWorld(), isServer: false, "127.0.0.1", 0, online: false);
            pc.Start();
            var box = (Object3d)pc.EditableEntries.First(e => e.Descriptor.Type == "cube").Instance;
            var platform = (Object3d)pc.EditableEntries.First(e => e.Descriptor.Type == "platform").Instance;
            float floorTop = platform.WorldMax.Y;
            void PlaceBox(Vector3 pos, float scale) { box.Position = pos; box.Scale = scale; box.UpdateGeometry(); }   // move/resize + refresh AABB

            // (1) STANDS ON FLOOR: dropped from above, the FOOT settles on the floor (Y = floorTop + half + rad), onGround.
            {
                PlaceBox(new Vector3(100f, 100f, 100f), 2f);        // keep the box far away
                pc.PlayerBodyForTest = new Vector3(0f, floorTop + 3f, 0f);
                for (int i = 0; i < 240; i++) pc.StepPlayerForTest(1f / 60f);
                float restY = pc.PlayerBodyForTest.Y, wantY = floorTop + half + rad;
                bool onGround = pc.PlayerOnGroundForTest;
                bool good = MathF.Abs(restY - wantY) < 0.02f && onGround;
                Console.WriteLine($"  c4-stands-on-floor: restY={restY:F4} (want {wantY:F4}=floorTop+half+rad), onGround={onGround} -> {(good ? "ok" : "BAD")}");
                pok &= good;
            }

            // (2) BLOCKED BY A WALL: a tall box spanning the player's height; push the body into it -> ejected to the
            //     near face, body centre ≈ rad from the face (no penetration).
            {
                PlaceBox(new Vector3(5f, 5f, 0f), 2f);
                float wallMinX = box.WorldMin.X;                    // near (−X) face
                pc.PlayerBodyForTest = new Vector3(wallMinX - rad + 0.2f, 5f, 0f);   // pushed 0.2 INTO the wall's reach
                pc.ResolveBodyCollisionForTest();
                float px = pc.PlayerBodyForTest.X;
                bool good = px <= wallMinX - rad + 1e-3f && MathF.Abs(px - (wallMinX - rad)) < 0.05f;   // at the face − rad, not through it
                Console.WriteLine($"  c4-blocked-by-wall: px={px:F4} (want {wallMinX - rad:F4}=faceMinX−rad) -> {(good ? "ok" : "BAD")}");
                pok &= good;
            }

            // (3) HEAD-HEIGHT BAR (the capsule win): a bar overhead whose BOTTOM is above a lone centre-sphere's
            //     reach (py+rad) but within the capsule's top cap (py+half+rad). The capsule's head hits it -> pushed
            //     DOWN; a centre-sphere would miss. Contrast: the same bar well ABOVE the cap does nothing.
            {
                float py = 5f;
                float boxScale = 2f;
                PlaceBox(new Vector3(0f, py, 0f), boxScale);
                float boxHalfY = (box.WorldMax.Y - box.WorldMin.Y) * 0.5f;
                float wantBottom = py + 0.6f;                       // rad(0.35) < 0.6 < half+rad(0.9)
                PlaceBox(new Vector3(0f, wantBottom + boxHalfY, 0f), boxScale);   // centre so the bottom lands at wantBottom, footprint over the player
                bool sphereWouldMiss = box.WorldMin.Y > py + rad;   // a lone centre-sphere (reach py+rad) never reaches the bar
                pc.PlayerBodyForTest = new Vector3(0f, py, 0f);
                float before = pc.PlayerBodyForTest.Y;
                pc.ResolveBodyCollisionForTest();
                float after = pc.PlayerBodyForTest.Y;
                bool pushedDown = after < before - 0.05f;
                // contrast: raise the bar well above the capsule top -> no collision.
                PlaceBox(new Vector3(0f, py + half + rad + 0.5f + boxHalfY, 0f), boxScale);
                pc.PlayerBodyForTest = new Vector3(0f, py, 0f);
                float b2 = pc.PlayerBodyForTest.Y;
                pc.ResolveBodyCollisionForTest();
                bool noPushHigh = MathF.Abs(pc.PlayerBodyForTest.Y - b2) < 1e-4f;
                bool good = sphereWouldMiss && pushedDown && noPushHigh;
                Console.WriteLine($"  c4-head-bar: barBottom={wantBottom:F2}>sphereReach={py + rad:F2}(sphere-misses={sphereWouldMiss}), capsulePushedDown={pushedDown} ({before:F3}->{after:F3}); raised-bar noPush={noPushHigh} -> {(good ? "ok" : "BAD")}");
                pok &= good;
            }

            // (4) FOOT-HEIGHT OBSTACLE (symmetric): a low obstacle whose TOP is below a centre-sphere's downward
            //     reach (py−rad) but within the capsule's foot cap (py−half−rad). It pushes the capsule UP.
            {
                float py = 5f;
                float boxScale = 2f;
                PlaceBox(new Vector3(0f, py, 0f), boxScale);
                float boxHalfY = (box.WorldMax.Y - box.WorldMin.Y) * 0.5f;
                float wantTop = py - 0.6f;                          // py−0.9 < py−0.6 < py−0.35
                PlaceBox(new Vector3(0f, wantTop - boxHalfY, 0f), boxScale);
                bool sphereWouldMiss = box.WorldMax.Y < py - rad;
                pc.PlayerBodyForTest = new Vector3(0f, py, 0f);
                float before = pc.PlayerBodyForTest.Y;
                pc.ResolveBodyCollisionForTest();
                float after = pc.PlayerBodyForTest.Y;
                bool pushedUp = after > before + 0.05f;
                bool good = sphereWouldMiss && pushedUp;
                Console.WriteLine($"  c4-foot-obstacle: barTop={wantTop:F2}<sphereReach={py - rad:F2}(sphere-misses={sphereWouldMiss}), capsulePushedUp={pushedUp} ({before:F3}->{after:F3}) -> {(good ? "ok" : "BAD")}");
                pok &= good;
            }

            ok &= pok;
        }

        Console.WriteLine(ok ? "EDITOR TEST PASSED" : "EDITOR TEST FAILED");
    }

    // Non-interactive check of the raycast pick: two spheres along +X, a ray down +X picks
    // the nearer one; a ray pointing away picks nothing. Reuses PriviewNetworkScene.PickNearest.
    static void PickSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== PICK SELF-TEST ===");

        var near = new PriviewNetworkScene.EditEntry
        {
            Descriptor = new WorldObject { Type = "sphere", Radius = 1f },
            Instance = new Sphere(new Vector3(5f, 0f, 0f), Vector3.Zero, 1f),
        };
        var far = new PriviewNetworkScene.EditEntry
        {
            Descriptor = new WorldObject { Type = "sphere", Radius = 1f },
            Instance = new Sphere(new Vector3(10f, 0f, 0f), Vector3.Zero, 1f),
        };
        var editables = new List<PriviewNetworkScene.EditEntry> { far, near }; // far first on purpose

        var towards = new Ray(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f).Norm());
        var away = new Ray(new Vector3(0f, 0f, 0f), new Vector3(-1f, 0f, 0f).Norm());

        int hit = PriviewNetworkScene.PickNearest(towards, editables);
        int miss = PriviewNetworkScene.PickNearest(away, editables);

        Console.WriteLine($"Aiming +X -> index {hit} (expect 1, the nearer sphere at X=5)");
        Console.WriteLine($"Aiming -X -> index {miss} (expect -1, no hit)");

        bool ok = hit == 1 && miss == -1;
        Console.WriteLine(ok ? "PICK TEST PASSED" : "PICK TEST FAILED");
    }

    // Non-interactive round-trip of the world-transfer format: build a world (mesh + cube +
    // sphere), Pack it, push the packet through its own serialize -> deserialize (bytes),
    // Unpack, then assert the config and the monkey .obj text are recovered faithfully.
    static void WorldSyncSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== WORLD SYNC SELF-TEST ===");

        void Fail(string reason) => Console.WriteLine($"WORLD SYNC TEST FAILED: {reason}");

        var world = new WorldConfig
        {
            Name = "synctest",
            Graphics = new GraphicsConfig { Shadows = true, Bvh = false, ExtraLight = true, DisableCameraLight = true, Renderer = "gpu" },
            Platform = new PlatformConfig { Enabled = true, Name = "the floor", Size = 12f, Color = "Cyan", Collides = false, Gravity = true, Position = new Vec3Config { X = 1.5f, Y = -0.5f, Z = 2.5f } },
            Physics = new PhysicsConfig { GravityEnabled = true, GravityStrength = 12.5f, CollisionEnabled = false, Restitution = 0.4f },
            Objects = new List<WorldObject>
            {
                new WorldObject { Id = 10, Type = "mesh", Mesh = "monkey",
                    Position = new Vec3Config { X = 1f, Y = 2f, Z = 3f },
                    Rotation = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                    Scale = 1.5f, Color = "Red", Anchor = "Center", RotateSpeed = 0.5f, Radius = 1f, Collider = "obb" },
                new WorldObject { Id = 11, Type = "cube", Name = "my crate",
                    Position = new Vec3Config { X = -1f, Y = 0f, Z = 2f },
                    Scale = 2f, Color = "Green", Collides = false, Gravity = true, Mass = 3.5f, Restitution = 0.8f, Friction = 0.35f, RollingFriction = 0.12f, ColorFade = 0.5f, Texture = "brick.png", TextureScale = 2f, TextureFace = 4, TextureFilter = 2 },
                new WorldObject { Id = 12, Type = "sphere",
                    Position = new Vec3Config { X = 4f, Y = 1f, Z = -2f },
                    Radius = 2.5f, Color = "Blue" },
                new WorldObject { Id = 13, Type = "light",
                    Position = new Vec3Config { X = 5f, Y = 4f, Z = 1f },
                    Power = 800f, Color = "Magenta", LightKind = "spot",
                    Direction = new Vec3Config { X = 0.5f, Y = -1f, Z = 0.25f },
                    ConeAngle = 22f, LightSize = 1.5f, LightSpin = 0.75f,
                    BeamCount = 3, ConeShape = "triangle", ColorInfluence = 0.85f, ColorFade = 0.4f },
                new WorldObject { Id = 14, Type = "light",
                    Position = new Vec3Config { X = -3f, Y = 5f, Z = -1f },
                    Power = 650f, Color = "Cyan", LightKind = "area",
                    Direction = new Vec3Config { X = 0f, Y = -1f, Z = 0f },
                    LightSize = 2f, AreaShape = "triangle" },
                new WorldObject { Id = 15, Type = "flatpicture",
                    Position = new Vec3Config { X = 2f, Y = 1.5f, Z = -3f },
                    Rotation = new Vec3Config { X = 0f, Y = 1.57f, Z = 0f },
                    Scale = 1.8f, Color = "Yellow", ColorFade = 0.2f, Collides = false, Texture = "poster.png" },
                new WorldObject { Id = 16, Type = "camera", Name = "watchtower",
                    Position = new Vec3Config { X = -6f, Y = 3f, Z = 4f },
                    Rotation = new Vec3Config { X = 0f, Y = 0.5f, Z = -0.2f },
                    Collides = false, CameraKind = "follow", FollowTargetId = 11 },
            },
            // C1-5: joints ride the world-sync ConfigJson — two kinds with ids set + EVERY field populated
            // (regardless of the kind's relevance) so the whole JointConfig round-trips through the packet.
            Joints = new List<JointConfig>
            {
                new JointConfig { Id = 20, Kind = "ballsocket", BodyA = 11, BodyB = -1,
                    AnchorA = new Vec3Config { X = 0.1f, Y = 0.2f, Z = 0.3f },
                    AnchorB = new Vec3Config { X = 1f, Y = 5f, Z = 0f },
                    Axis = new Vec3Config { X = 0.1f, Y = 0.9f, Z = 0f },
                    LimitEnabled = true, LowerLimit = -0.5f, UpperLimit = 1.5f,
                    MotorEnabled = true, MotorTargetSpeed = 1.1f, MaxMotorTorque = 7f,
                    RestLength = 3.3f, SpringEnabled = true, Frequency = 2.2f, DampingRatio = 0.6f, Collide = true },   // F4: non-default Collide rides save/sync
                new JointConfig { Id = 21, Kind = "distance", BodyA = 12, BodyB = 11,
                    AnchorA = new Vec3Config { X = 0f, Y = 1f, Z = 0f }, AnchorB = new Vec3Config { X = 2f, Y = 0f, Z = -1f },
                    Axis = new Vec3Config { X = 0f, Y = 0f, Z = 1f },
                    LimitEnabled = false, LowerLimit = -1.2f, UpperLimit = 0.8f,
                    MotorEnabled = true, MotorTargetSpeed = 2.5f, MaxMotorTorque = 15f,
                    RestLength = 2.5f, SpringEnabled = true, Frequency = 3f, DampingRatio = 0.4f },
            },
        };

        // Pack, then round-trip the packet through its own byte (de)serialization. (This world references
        // brick.png/poster.png which aren't on disk, so TextureData stays empty here — the texture-BYTES
        // streaming is exercised in its own blocks below with real on-disk PNGs.)
        WorldSyncPacket packet = WorldSync.Pack(world, AppPaths.ModelsFolder, AppPaths.TexturesFolder);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var w = new BinaryWriter(ms)) packet.Serialize(w);
            bytes = ms.ToArray();
        }
        Console.WriteLine($"Packet size: {bytes.Length} bytes ({world.Objects.Count} objects, {packet.MeshTexts.Count} mesh file(s), {packet.TextureData.Count} inline texture(s)).");

        var received = new WorldSyncPacket();
        using (var r = new BinaryReader(new MemoryStream(bytes))) received.Deserialize(r);

        var (back, meshTexts, _) = WorldSync.Unpack(received);

        // Compare the recovered config field-by-field.
        string? reason = CompareWorlds(world, back);
        if (reason != null) { Fail(reason); return; }

        // Mesh text must round-trip and match the on-disk file exactly.
        string monkeyPath = Path.Combine(AppPaths.ModelsFolder, "monkey.obj");
        if (!meshTexts.TryGetValue("monkey", out var recoveredObj) || string.IsNullOrEmpty(recoveredObj))
        { Fail("recovered mesh text for 'monkey' is missing/empty."); return; }
        if (recoveredObj != File.ReadAllText(monkeyPath))
        { Fail("recovered 'monkey' .obj text does not match the on-disk file."); return; }

        Console.WriteLine($"Recovered: name={back.Name}, objects={back.Objects.Count}, monkey .obj chars={recoveredObj.Length}");

        // WorldSettings delta: round-trip the packet through its OWN serialize/deserialize, then
        // re-parse the JSON payloads and assert the PlatformConfig + GraphicsConfig survive intact.
        {
            var settings = new WorldSettingsPacket
            {
                PlatformJson = JsonSerializer.Serialize(world.Platform),
                GraphicsJson = JsonSerializer.Serialize(world.Graphics),
            };
            byte[] sbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) settings.Serialize(w);
                sbytes = ms.ToArray();
            }
            var recvSettings = new WorldSettingsPacket();
            using (var r = new BinaryReader(new MemoryStream(sbytes))) recvSettings.Deserialize(r);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var pb = JsonSerializer.Deserialize<PlatformConfig>(recvSettings.PlatformJson, opts);
            var gb = JsonSerializer.Deserialize<GraphicsConfig>(recvSettings.GraphicsJson, opts);
            bool settingsOk =
                pb != null && gb != null &&
                pb.Enabled == world.Platform.Enabled && pb.Shape == world.Platform.Shape &&
                Math.Abs(pb.Size - world.Platform.Size) < 1e-4f && pb.Color == world.Platform.Color &&
                Math.Abs(pb.Position.X - world.Platform.Position.X) < 1e-4f &&
                Math.Abs(pb.Position.Y - world.Platform.Position.Y) < 1e-4f &&
                Math.Abs(pb.Position.Z - world.Platform.Position.Z) < 1e-4f &&
                gb.Shadows == world.Graphics.Shadows && gb.Bvh == world.Graphics.Bvh &&
                gb.ExtraLight == world.Graphics.ExtraLight && gb.DisableCameraLight == world.Graphics.DisableCameraLight &&
                gb.Renderer == world.Graphics.Renderer;
            Console.WriteLine($"WorldSettings round-trip: {sbytes.Length} bytes, platform shape={pb?.Shape} size={pb?.Size} color={pb?.Color}, graphics shadows={gb?.Shadows} bvh={gb?.Bvh} -> {(settingsOk ? "ok" : "BAD")}");
            if (!settingsOk) { Fail("WorldSettings packet round-trip lost platform/graphics fields."); return; }
        }

        // PlayerLeft delta: round-trip the packet through its OWN serialize/deserialize; NetId survives.
        {
            var left = new PlayerLeftPacket { NetId = 4242 };
            byte[] lbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) left.Serialize(w);
                lbytes = ms.ToArray();
            }
            var recvLeft = new PlayerLeftPacket();
            using (var r = new BinaryReader(new MemoryStream(lbytes))) recvLeft.Deserialize(r);
            bool leftOk = recvLeft.NetId == 4242;
            Console.WriteLine($"PlayerLeft round-trip: {lbytes.Length} bytes, NetId={recvLeft.NetId} -> {(leftOk ? "ok" : "BAD")}");
            if (!leftOk) { Fail("PlayerLeft packet round-trip lost NetId."); return; }
        }

        // N2 — PlayerInfo delta: round-trip the packet through its OWN serialize/deserialize; NetId + Nick
        // survive, including an empty nick and a 16-char (max-length) nick.
        {
            bool RoundTrip(int netId, string nick, out int gotId, out string gotNick)
            {
                var info = new PlayerInfoPacket { NetId = netId, Nick = nick };
                byte[] ibytes;
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms)) info.Serialize(w);
                    ibytes = ms.ToArray();
                }
                var recv = new PlayerInfoPacket();
                using (var r = new BinaryReader(new MemoryStream(ibytes))) recv.Deserialize(r);
                gotId = recv.NetId; gotNick = recv.Nick;
                return gotId == netId && gotNick == nick;
            }
            bool named = RoundTrip(7, "Nova_Player-01", out _, out _);   // typical
            bool empty = RoundTrip(3, "", out _, out string emptyNick); // empty nick
            bool maxLen = RoundTrip(9999, "ABCDEFGHIJKLMNOP", out _, out string longNick);   // 16 chars
            bool infoOk = named && empty && maxLen;
            Console.WriteLine($"PlayerInfo round-trip: named={named}, empty(len={emptyNick.Length})={empty}, 16-char(len={longNick.Length})={maxLen} -> {(infoOk ? "ok" : "BAD")}");
            if (!infoOk) { Fail("PlayerInfo packet round-trip lost NetId/Nick."); return; }
        }

        // MeshChunk delta: round-trip the packet through its OWN serialize/deserialize; fields survive.
        {
            var chunk = new MeshChunkPacket { MeshName = "bigmesh", Index = 3, Total = 7, Data = "v 1.0 2.0 3.0\n" };
            byte[] cbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) chunk.Serialize(w);
                cbytes = ms.ToArray();
            }
            var recvChunk = new MeshChunkPacket();
            using (var r = new BinaryReader(new MemoryStream(cbytes))) recvChunk.Deserialize(r);
            bool chunkOk = recvChunk.MeshName == "bigmesh" && recvChunk.Index == 3 && recvChunk.Total == 7 && recvChunk.Data == "v 1.0 2.0 3.0\n";
            Console.WriteLine($"MeshChunk round-trip: {cbytes.Length} bytes, name={recvChunk.MeshName} idx={recvChunk.Index}/{recvChunk.Total} -> {(chunkOk ? "ok" : "BAD")}");
            if (!chunkOk) { Fail("MeshChunk packet round-trip lost fields."); return; }
        }

        // PhysicsSync delta: round-trip the compact state batch (id + pos + FULL linVel + rot + angVel per entry).
        {
            var ps = new PhysicsSyncPacket
            {
                Ids = new[] { 7, 42 },
                Positions = new[] { new Vector3(1f, 2f, 3f), new Vector3(-4f, 5.5f, 6f) },
                LinVel = new[] { new Vector3(1.2f, -9.8f, 0.3f), Vector3.Zero },
                Rotations = new[] { new Vector3(0.1f, 0.2f, 0.3f), new Vector3(-0.4f, 0.5f, -0.6f) },
                AngVel = new[] { new Vector3(1.5f, -2.5f, 0.5f), new Vector3(0f, 3f, -1f) },
            };
            byte[] pbytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms)) ps.Serialize(w);
                pbytes = ms.ToArray();
            }
            var recv = new PhysicsSyncPacket();
            using (var r = new BinaryReader(new MemoryStream(pbytes))) recv.Deserialize(r);
            bool psOk = recv.Ids.Length == 2 && recv.Ids[0] == 7 && recv.Ids[1] == 42
                && Math.Abs(recv.Positions[1].Y - 5.5f) < 1e-5f
                && Math.Abs(recv.LinVel[0].X - 1.2f) < 1e-5f && Math.Abs(recv.LinVel[0].Y - (-9.8f)) < 1e-5f && Math.Abs(recv.LinVel[0].Z - 0.3f) < 1e-5f
                && recv.LinVel[1].Length() == 0f
                && Math.Abs(recv.Rotations[0].Y - 0.2f) < 1e-5f && Math.Abs(recv.Rotations[1].Z - (-0.6f)) < 1e-5f
                && Math.Abs(recv.AngVel[0].X - 1.5f) < 1e-5f && Math.Abs(recv.AngVel[1].Y - 3f) < 1e-5f;
            Console.WriteLine($"PhysicsSync round-trip: {pbytes.Length} bytes, n={recv.Ids.Length}, pos1.Y={recv.Positions[1].Y:F2}, linVel0=({recv.LinVel[0].X:F1},{recv.LinVel[0].Y:F1},{recv.LinVel[0].Z:F1}), rot0.Y={recv.Rotations[0].Y:F2}, angvel0.X={recv.AngVel[0].X:F2} -> {(psOk ? "ok" : "BAD")}");
            if (!psOk) { Fail("PhysicsSync packet round-trip lost fields."); return; }
        }

        // PhysicsSync chunking (E4): SplitIntoChunks slices a batch into ≤maxPerChunk-entry packets that
        // together reproduce the source order — so a chunked UDP flush loses nothing.
        {
            int N = 45;
            var ids = new int[N]; var posn = new Vector3[N]; var lin = new Vector3[N]; var rota = new Vector3[N]; var angv = new Vector3[N];
            for (int i = 0; i < N; i++)
            {
                ids[i] = 1000 + i;
                posn[i] = new Vector3(i, i * 2, i * 3);
                lin[i] = new Vector3(-i, i, -i * 2);
                rota[i] = new Vector3(i * 0.01f, i * 0.02f, i * 0.03f);
                angv[i] = new Vector3(i * 0.1f, -i * 0.1f, i * 0.2f);
            }

            var chunks = PhysicsSyncPacket.SplitIntoChunks(ids, posn, lin, rota, angv, 20);

            // 45 @ 20/chunk → 3 chunks sized 20/20/5; concatenation reproduces the source order + values.
            bool sizeOk = chunks.Count == 3 && chunks[0].Ids.Length == 20 && chunks[1].Ids.Length == 20 && chunks[2].Ids.Length == 5;
            bool orderOk = true;
            int flat = 0;
            foreach (var c in chunks)
            {
                for (int j = 0; j < c.Ids.Length; j++)
                {
                    if (c.Ids[j] != ids[flat] || c.Positions[j] != posn[flat] || c.LinVel[j] != lin[flat]
                        || c.Rotations[j] != rota[flat] || c.AngVel[j] != angv[flat]) orderOk = false;
                    flat++;
                }
            }
            bool countOk = flat == N;

            // Edge cases: exact multiple, one-over, and empty.
            bool edgeOk =
                PhysicsSyncPacket.SplitIntoChunks(new int[20], new Vector3[20], new Vector3[20], new Vector3[20], new Vector3[20], 20).Count == 1
                && PhysicsSyncPacket.SplitIntoChunks(new int[21], new Vector3[21], new Vector3[21], new Vector3[21], new Vector3[21], 20) is { Count: 2 } c21 && c21[0].Ids.Length == 20 && c21[1].Ids.Length == 1
                && PhysicsSyncPacket.SplitIntoChunks(new int[0], new Vector3[0], new Vector3[0], new Vector3[0], new Vector3[0], 20).Count == 0;

            bool chunkSplitOk = sizeOk && orderOk && countOk && edgeOk;
            Console.WriteLine($"PhysicsSync chunking: N={N}/max20 -> {chunks.Count} chunks (20/20/5), order={orderOk}, edges(20->1,21->2,0->0)={edgeOk} -> {(chunkSplitOk ? "ok" : "BAD")}");
            if (!chunkSplitOk) { Fail("PhysicsSync SplitIntoChunks sizing/order/edge mismatch."); return; }
        }

        // Split/reassemble: cut a long string into MeshChunkSize pieces, reassemble by Index, compare.
        {
            const int chunkSize = 16384;   // mirrors PriviewNetworkScene.MeshChunkSize
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40000; i++) sb.Append((char)('a' + (i % 26)));
            string original = sb.ToString();

            int total = (original.Length + chunkSize - 1) / chunkSize;
            var parts = new string[total];
            for (int i = 0; i < total; i++)
            {
                int start = i * chunkSize;
                parts[i] = original.Substring(start, Math.Min(chunkSize, original.Length - start));
            }
            string reassembled = string.Concat(parts);
            bool splitOk = total == 3 && reassembled == original;   // 40000 -> ceil(40000/16384) = 3 chunks
            Console.WriteLine($"MeshChunk split: len={original.Length} -> {total} chunks, reassembled match={reassembled == original} -> {(splitOk ? "ok" : "BAD")}");
            if (!splitOk) { Fail("MeshChunk split/reassemble mismatch."); return; }
        }

        // ---- Network TEXTURE sync (A1): stream PNG bytes so a peer without the file still sees it ----

        // A distinct 2x2 RGBA image (all channels used) shared by the texture blocks below.
        var texPixels = new[]
        {
            new Rgba32(10, 20, 30, 255), new Rgba32(200, 40, 60, 128),
            new Rgba32(70, 180, 90, 200), new Rgba32(15, 25, 240, 90),
        };

        // (1) Texture round-trip: a world referencing an on-disk PNG -> Pack reads its BYTES inline ->
        // Serialize -> Deserialize -> Unpack yields the identical bytes, which re-decode to the texture.
        {
            Directory.CreateDirectory(AppPaths.TexturesFolder);
            string texName = "__sync_tex_roundtrip__.png";
            string texPath = Path.Combine(AppPaths.TexturesFolder, texName);
            byte[] png = PngEncode(2, 2, texPixels, 6, 0);
            try
            {
                File.WriteAllBytes(texPath, png);
                var texWorld = new WorldConfig
                {
                    Name = "textest",
                    Objects = new List<WorldObject> { new WorldObject { Id = 1, Type = "cube", Texture = texName } },
                };
                var tp = WorldSync.Pack(texWorld, AppPaths.ModelsFolder, AppPaths.TexturesFolder);
                bool packedInline = tp.TextureData.TryGetValue(texName, out var packedBytes) && packedBytes!.SequenceEqual(png);

                byte[] tb;
                using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) tp.Serialize(w); tb = ms.ToArray(); }
                var tr = new WorldSyncPacket();
                using (var r = new BinaryReader(new MemoryStream(tb))) tr.Deserialize(r);
                var (_, _, texData) = WorldSync.Unpack(tr);

                bool survived = texData.TryGetValue(texName, out var recv) && recv!.SequenceEqual(png);
                var decoded = PngDecoder.Decode(recv!);
                bool decodeOk = decoded.Width == 2 && decoded.Height == 2;
                bool ok = packedInline && survived && decodeOk;
                Console.WriteLine($"Texture round-trip: packed inline={packedInline}, {png.Length} bytes survive={survived}, decode={decoded.Width}x{decoded.Height} -> {(ok ? "ok" : "BAD")}");
                if (!ok) { Fail("Texture round-trip lost/altered the PNG bytes."); return; }
            }
            finally { try { File.Delete(texPath); } catch { } }
        }

        // (2) TextureChunk round-trip: the binary chunk packet survives its own serialize/deserialize.
        {
            var tc = new TextureChunkPacket { TextureName = "big.png", Index = 2, Total = 5, Data = new byte[] { 0, 255, 1, 254, 128, 64, 200 } };
            byte[] cbytes;
            using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) tc.Serialize(w); cbytes = ms.ToArray(); }
            var recv = new TextureChunkPacket();
            using (var r = new BinaryReader(new MemoryStream(cbytes))) recv.Deserialize(r);
            bool tcOk = recv.TextureName == "big.png" && recv.Index == 2 && recv.Total == 5 && recv.Data.SequenceEqual(tc.Data);
            Console.WriteLine($"TextureChunk round-trip: {cbytes.Length} bytes, name={recv.TextureName} idx={recv.Index}/{recv.Total} data[{recv.Data.Length}] -> {(tcOk ? "ok" : "BAD")}");
            if (!tcOk) { Fail("TextureChunk packet round-trip lost fields."); return; }
        }

        // (3) TextureChunk split/reassemble: cut a > threshold byte[] at the chunk size, reassemble by
        // Index, assert byte-identical (PNGs are usually > 49 KB, so this is the common path).
        {
            const int chunkSize = 16384;      // mirrors PriviewNetworkScene.MeshChunkSize
            const int threshold = 49152;      // mirrors the inline threshold
            byte[] original = new byte[60000]; // deterministic > threshold payload (4 chunks)
            for (int i = 0; i < original.Length; i++) original[i] = (byte)((i * 7 + 3) & 0xFF);

            int total = (original.Length + chunkSize - 1) / chunkSize;
            var parts = new byte[total][];
            for (int i = 0; i < total; i++)
            {
                int start = i * chunkSize;
                int len = Math.Min(chunkSize, original.Length - start);
                parts[i] = new byte[len];
                Array.Copy(original, start, parts[i], 0, len);
            }
            byte[] reassembled = new byte[original.Length];
            int off = 0;
            foreach (var p in parts) { Array.Copy(p, 0, reassembled, off, p.Length); off += p.Length; }
            bool splitOk = original.Length > threshold && total == 4 && reassembled.SequenceEqual(original);
            Console.WriteLine($"TextureChunk split: len={original.Length} (> {threshold}) -> {total} chunks, reassembled match={reassembled.SequenceEqual(original)} -> {(splitOk ? "ok" : "BAD")}");
            if (!splitOk) { Fail("TextureChunk split/reassemble mismatch."); return; }
        }

        // (4) Peer-without-file: the peer's OWN textures/ lacks the file, but the streamed bytes land in
        // received/textures/ -> loading from there attaches the texture (non-null, correct WxH), while the
        // default folder still returns null (proving it truly wasn't local).
        {
            Directory.CreateDirectory(AppPaths.ReceivedTexturesFolder);
            string name = "__peer_recv_tex__.png";
            string absentPath = Path.Combine(AppPaths.TexturesFolder, name);
            string recvPath = Path.Combine(AppPaths.ReceivedTexturesFolder, name);
            byte[] png = PngEncode(2, 2, texPixels, 6, 0);
            try
            {
                try { if (File.Exists(absentPath)) File.Delete(absentPath); } catch { }   // ensure the peer lacks it locally
                File.WriteAllBytes(recvPath, png);                                          // the streamed bytes arrive here

                var fromReceived = TextureLoader.Get(AppPaths.ReceivedTexturesFolder, name);
                var fromDefault = TextureLoader.Get(AppPaths.TexturesFolder, name);
                bool peerOk = fromReceived != null && fromReceived.Width == 2 && fromReceived.Height == 2 && fromDefault == null;
                Console.WriteLine($"Peer-without-file: received-folder load={(fromReceived != null ? $"{fromReceived.Width}x{fromReceived.Height}" : "NULL")}, default-folder load={(fromDefault == null ? "null (absent)" : "present")} -> {(peerOk ? "ok" : "BAD")}");
                if (!peerOk) { Fail("Peer-without-file: streamed texture did not attach from received/textures."); return; }
            }
            finally { try { File.Delete(recvPath); } catch { } }
        }

        // (5) Runtime texture PUSH (A1 tail): a LIVE texture-change edit streams the PNG bytes on the EDIT
        // path (not only on world sync). Emit the EXACT chunks the edit path sends for an on-disk PNG, then
        // feed them to a peer that LACKS the file and assert it materialises + attaches — reusing the A1
        // peer-without-file end, now triggered by the edit path (StreamTextureToPeers) rather than world sync.
        {
            Directory.CreateDirectory(AppPaths.TexturesFolder);
            Directory.CreateDirectory(AppPaths.ReceivedTexturesFolder);
            string name = "__edit_push_tex__.png";
            string authPath = Path.Combine(AppPaths.TexturesFolder, name);
            string recvPath = Path.Combine(AppPaths.ReceivedTexturesFolder, name);
            byte[] png = PngEncode(2, 2, texPixels, 6, 0);
            try
            {
                // Authority has the texture in textures/; the edit path emits the chunks it would stream.
                File.WriteAllBytes(authPath, png);
                var chunks = PriviewNetworkScene.EmitTextureChunksForTest(name);

                // The emitted chunks reassemble to the on-disk PNG bit-for-bit (the real texture bytes go out).
                byte[] emitted = new byte[chunks.Sum(c => c.Data.Length)];
                int eoff = 0; foreach (var c in chunks) { Array.Copy(c.Data, 0, emitted, eoff, c.Data.Length); eoff += c.Data.Length; }
                bool emitOk = chunks.Count >= 1 && chunks.All(c => c.TextureName == name && c.Total == chunks.Count) && emitted.SequenceEqual(png);

                // The peer LACKS the file locally — remove any local copies so only the stream can carry it.
                try { File.Delete(authPath); } catch { }
                try { if (File.Exists(recvPath)) File.Delete(recvPath); } catch { }

                // A peer receives the streamed chunks -> OnTextureChunkReceived reassembles -> MaterializeTexture.
                var peer = new PriviewNetworkScene(new DisplayManagerAsync(),
                    new WorldConfig { Name = "peer", Platform = new PlatformConfig { Enabled = false } }, isServer: false, "127.0.0.1", 0, online: false);
                foreach (var c in chunks) peer.ReceiveTextureChunkForTest(c);

                bool wrote = File.Exists(recvPath) && File.ReadAllBytes(recvPath).SequenceEqual(png);
                var fromReceived = TextureLoader.Get(AppPaths.ReceivedTexturesFolder, name);   // attaches from where the stream landed
                var fromDefault = TextureLoader.Get(AppPaths.TexturesFolder, name);            // the peer's default still lacks it
                bool attachOk = fromReceived != null && fromReceived.Width == 2 && fromReceived.Height == 2 && fromDefault == null;
                bool pushOk = emitOk && wrote && attachOk;
                Console.WriteLine($"Runtime texture push: emit {chunks.Count} chunk(s) bytes-match={emitOk}, peer materialised={wrote}, attach received={(fromReceived != null ? $"{fromReceived.Width}x{fromReceived.Height}" : "NULL")}/default={(fromDefault == null ? "null (absent)" : "present")} -> {(pushOk ? "ok" : "BAD")}");
                if (!pushOk) { Fail("Runtime texture push: edit-path stream did not reach the peer / attach."); return; }
            }
            finally { try { File.Delete(authPath); } catch { } try { File.Delete(recvPath); } catch { } }
        }

        Console.WriteLine("WORLD SYNC TEST PASSED");
    }

    static string? CompareWorlds(WorldConfig a, WorldConfig b)
    {
        const float eps = 1e-4f;
        bool Eq(float x, float y) => Math.Abs(x - y) < eps;

        if (a.Name != b.Name) return $"name '{a.Name}' != '{b.Name}'";
        if (a.Graphics.Shadows != b.Graphics.Shadows || a.Graphics.Bvh != b.Graphics.Bvh ||
            a.Graphics.ExtraLight != b.Graphics.ExtraLight || a.Graphics.DisableCameraLight != b.Graphics.DisableCameraLight ||
            a.Graphics.Renderer != b.Graphics.Renderer)
            return "graphics flags differ";
        if (a.Platform.Name != b.Platform.Name ||
            a.Platform.Enabled != b.Platform.Enabled || !Eq(a.Platform.Size, b.Platform.Size) ||
            PriviewNetworkScene.ParseColor(a.Platform.Color, Rgba32.White) != PriviewNetworkScene.ParseColor(b.Platform.Color, Rgba32.White) ||
            a.Platform.Shape != b.Platform.Shape || !Eq(a.Platform.Width, b.Platform.Width) || !Eq(a.Platform.Depth, b.Platform.Depth) ||
            a.Platform.Collides != b.Platform.Collides || a.Platform.Gravity != b.Platform.Gravity ||
            !Eq(a.Platform.Position.X, b.Platform.Position.X) || !Eq(a.Platform.Position.Y, b.Platform.Position.Y) || !Eq(a.Platform.Position.Z, b.Platform.Position.Z))
            return "platform differs";
        if (a.Physics.GravityEnabled != b.Physics.GravityEnabled) return "physics gravity-enabled differs";
        if (!Eq(a.Physics.GravityStrength, b.Physics.GravityStrength)) return "physics gravity-strength differs";
        if (a.Physics.CollisionEnabled != b.Physics.CollisionEnabled) return "physics collision-enabled differs";
        if (!Eq(a.Physics.Restitution, b.Physics.Restitution)) return "physics restitution differs";
        if (a.Objects.Count != b.Objects.Count) return $"object count {a.Objects.Count} != {b.Objects.Count}";

        for (int i = 0; i < a.Objects.Count; i++)
        {
            var x = a.Objects[i];
            var y = b.Objects[i];
            if (x.Id != y.Id) return $"object[{i}].Id {x.Id} != {y.Id}";
            if (x.Name != y.Name) return $"object[{i}].Name '{x.Name}' != '{y.Name}'";
            if (x.Type != y.Type) return $"object[{i}].Type '{x.Type}' != '{y.Type}'";
            if (x.Mesh != y.Mesh) return $"object[{i}].Mesh '{x.Mesh}' != '{y.Mesh}'";
            if (!Eq(x.Position.X, y.Position.X) || !Eq(x.Position.Y, y.Position.Y) || !Eq(x.Position.Z, y.Position.Z))
                return $"object[{i}].Position differs";
            if (!Eq(x.Rotation.X, y.Rotation.X) || !Eq(x.Rotation.Y, y.Rotation.Y) || !Eq(x.Rotation.Z, y.Rotation.Z))
                return $"object[{i}].Rotation differs";
            if (!Eq(x.Scale, y.Scale)) return $"object[{i}].Scale differs";
            if (PriviewNetworkScene.ParseColor(x.Color, Rgba32.White) != PriviewNetworkScene.ParseColor(y.Color, Rgba32.White))
                return $"object[{i}].Color '{x.Color}' != '{y.Color}'";
            if (x.Anchor != y.Anchor) return $"object[{i}].Anchor '{x.Anchor}' != '{y.Anchor}'";
            if (!Eq(x.RotateSpeed, y.RotateSpeed)) return $"object[{i}].RotateSpeed differs";
            if (!Eq(x.Radius, y.Radius)) return $"object[{i}].Radius differs";
            if (x.Collides != y.Collides) return $"object[{i}].Collides {x.Collides} != {y.Collides}";
            if (x.Gravity != y.Gravity) return $"object[{i}].Gravity {x.Gravity} != {y.Gravity}";
            if (x.Collider != y.Collider) return $"object[{i}].Collider '{x.Collider}' != '{y.Collider}'";
            if (!Eq(x.Mass, y.Mass)) return $"object[{i}].Mass {x.Mass} != {y.Mass}";
            if (!Eq(x.Restitution, y.Restitution)) return $"object[{i}].Restitution {x.Restitution} != {y.Restitution}";
            if (!Eq(x.Friction, y.Friction)) return $"object[{i}].Friction {x.Friction} != {y.Friction}";
            if (!Eq(x.RollingFriction, y.RollingFriction)) return $"object[{i}].RollingFriction {x.RollingFriction} != {y.RollingFriction}";
            if (!Eq(x.ColorFade, y.ColorFade)) return $"object[{i}].ColorFade {x.ColorFade} != {y.ColorFade}";
            if (x.Texture != y.Texture) return $"object[{i}].Texture '{x.Texture}' != '{y.Texture}'";
            if (!Eq(x.TextureScale, y.TextureScale)) return $"object[{i}].TextureScale {x.TextureScale} != {y.TextureScale}";
            if (x.TextureFace != y.TextureFace) return $"object[{i}].TextureFace {x.TextureFace} != {y.TextureFace}";
            if (x.TextureFilter != y.TextureFilter) return $"object[{i}].TextureFilter {x.TextureFilter} != {y.TextureFilter}";
            if (!Eq(x.Power, y.Power)) return $"object[{i}].Power differs";
            // ---- light-only rich fields ----
            if (x.LightKind != y.LightKind) return $"object[{i}].LightKind '{x.LightKind}' != '{y.LightKind}'";
            if (!Eq(x.Direction.X, y.Direction.X) || !Eq(x.Direction.Y, y.Direction.Y) || !Eq(x.Direction.Z, y.Direction.Z))
                return $"object[{i}].Direction differs";
            if (!Eq(x.ConeAngle, y.ConeAngle)) return $"object[{i}].ConeAngle differs";
            if (!Eq(x.LightSize, y.LightSize)) return $"object[{i}].LightSize differs";
            if (!Eq(x.LightSpin, y.LightSpin)) return $"object[{i}].LightSpin differs";
            if (x.BeamCount != y.BeamCount) return $"object[{i}].BeamCount {x.BeamCount} != {y.BeamCount}";
            if (x.ConeShape != y.ConeShape) return $"object[{i}].ConeShape '{x.ConeShape}' != '{y.ConeShape}'";
            if (x.AreaShape != y.AreaShape) return $"object[{i}].AreaShape differs";
            if (!Eq(x.ColorInfluence, y.ColorInfluence)) return $"object[{i}].ColorInfluence differs";
            // ---- camera-only fields ----
            if (x.CameraKind != y.CameraKind) return $"object[{i}].CameraKind '{x.CameraKind}' != '{y.CameraKind}'";
            if (x.FollowTargetId != y.FollowTargetId) return $"object[{i}].FollowTargetId {x.FollowTargetId} != {y.FollowTargetId}";
        }

        // ---- C1-5: joints (empty on both sides for a joint-free world → no change to existing callers) ----
        if (a.Joints.Count != b.Joints.Count) return $"joint count {a.Joints.Count} != {b.Joints.Count}";
        for (int i = 0; i < a.Joints.Count; i++)
        {
            var x = a.Joints[i];
            var y = b.Joints[i];
            if (x.Id != y.Id) return $"joint[{i}].Id {x.Id} != {y.Id}";
            if (x.Kind != y.Kind) return $"joint[{i}].Kind '{x.Kind}' != '{y.Kind}'";
            if (x.BodyA != y.BodyA) return $"joint[{i}].BodyA {x.BodyA} != {y.BodyA}";
            if (x.BodyB != y.BodyB) return $"joint[{i}].BodyB {x.BodyB} != {y.BodyB}";
            if (!Eq(x.AnchorA.X, y.AnchorA.X) || !Eq(x.AnchorA.Y, y.AnchorA.Y) || !Eq(x.AnchorA.Z, y.AnchorA.Z)) return $"joint[{i}].AnchorA differs";
            if (!Eq(x.AnchorB.X, y.AnchorB.X) || !Eq(x.AnchorB.Y, y.AnchorB.Y) || !Eq(x.AnchorB.Z, y.AnchorB.Z)) return $"joint[{i}].AnchorB differs";
            if (x.Collide != y.Collide) return $"joint[{i}].Collide {x.Collide} != {y.Collide}";
            if (!Eq(x.Axis.X, y.Axis.X) || !Eq(x.Axis.Y, y.Axis.Y) || !Eq(x.Axis.Z, y.Axis.Z)) return $"joint[{i}].Axis differs";
            if (x.LimitEnabled != y.LimitEnabled) return $"joint[{i}].LimitEnabled differs";
            if (!Eq(x.LowerLimit, y.LowerLimit) || !Eq(x.UpperLimit, y.UpperLimit)) return $"joint[{i}].Limit differs";
            if (x.MotorEnabled != y.MotorEnabled) return $"joint[{i}].MotorEnabled differs";
            if (!Eq(x.MotorTargetSpeed, y.MotorTargetSpeed) || !Eq(x.MaxMotorTorque, y.MaxMotorTorque)) return $"joint[{i}].Motor differs";
            if (!Eq(x.RestLength, y.RestLength)) return $"joint[{i}].RestLength differs";
            if (x.SpringEnabled != y.SpringEnabled) return $"joint[{i}].SpringEnabled differs";
            if (!Eq(x.Frequency, y.Frequency) || !Eq(x.DampingRatio, y.DampingRatio)) return $"joint[{i}].Spring differs";
        }
        return null;
    }

}
