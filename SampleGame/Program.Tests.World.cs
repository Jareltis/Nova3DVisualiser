using System.Net;
using System.Runtime.InteropServices;
using NStack;
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
using Terminal.Gui;

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
            var camFp = PriviewNetworkScene.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.FirstPerson);
            var camTp = PriviewNetworkScene.CameraPositionFor(body, look, PriviewNetworkScene.CameraMode.ThirdPerson);
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
                    Scale = 2f, Color = "Green", Collides = false, Gravity = true, Mass = 3.5f, Restitution = 0.8f, Friction = 0.35f, RollingFriction = 0.12f, ColorFade = 0.5f, Texture = "brick.png", TextureScale = 2f, TextureFace = 4, TextureFilter = 1 },
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
            }
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
        }
        return null;
    }

}
