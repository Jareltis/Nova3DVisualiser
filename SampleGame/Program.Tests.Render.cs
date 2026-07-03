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
    // Render/GPU self-tests: bvh, color, texture (+ PNG encode / UV fixtures / UV-sphere), and the GPU parity test with its snapshot scenes.

    static void BvhSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== BVH SELF-TEST ===");

        // models/ is a pure mesh library now: load each .obj raw and build its acceleration here.
        var models = new List<Object3d>();
        if (Directory.Exists(AppPaths.ModelsFolder))
        {
            foreach (string objPath in Directory.GetFiles(AppPaths.ModelsFolder, "*.obj"))
            {
                var m = ModelLoader.LoadRawMesh(AppPaths.ModelsFolder, Path.GetFileNameWithoutExtension(objPath));
                if (m == null) continue;
                m.BuildAcceleration();
                models.Add(m);
            }
        }
        if (models.Count == 0) { Console.WriteLine("No models found - cannot run self-test."); return; }

        // Pick the highest-triangle model (the monkey/Suzanne; it has a BVH).
        Object3d model = models[0];
        foreach (var m in models) if (m.FaceCount > model.FaceCount) model = m;
        Console.WriteLine($"Selected model: {model.FaceCount} triangles, HasBvh={model.HasBvh}");

        // Non-trivial transform to exercise the inverse.
        model.LocalRotate = new Vector3(0.3f, 0.7f, -0.4f);
        model.Scale = 0.3f;
        model.Position = new Vector3(1f, 0.5f, 2f);
        model.UpdateGeometry();

        // Aim rays at the (approximate) world centre of the bottom-anchored mesh.
        Vector3 totalRot = model.LocalRotate + Vector3.Zero;
        Vector3 target = (new Vector3(0f, model.Size.Y * 0.5f, 0f) * model.Scale).Rotate(totalRot) + model.Position;
        float radius = model.Size.Length() * model.Scale * 0.5f;
        Vector3 origin = target + new Vector3(6f, 2.5f, 6f);

        var rng = new Random(12345);
        int rays = 0, hits = 0, mismatches = 0, reported = 0;

        for (int i = 0; i < 4000; i++)
        {
            Vector3 aim;
            if (i % 8 == 0)
                aim = target + new Vector3(50f, 0f, -50f);   // far to the side -> guaranteed miss
            else
                aim = target + new Vector3(
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f,
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f,
                    (float)(rng.NextDouble() * 2 - 1) * radius * 1.4f);

            Vector3 dir = (aim - origin).Norm();
            var ray = new Ray(origin, dir);
            rays++;

            Object3d.UseBvh = false; var a = model.GetRenderData(ray);
            Object3d.UseBvh = true;  var b = model.GetRenderData(ray);

            bool ah = a.Intersection > -1, bh = b.Intersection > -1;
            if (ah) hits++;

            bool mismatch = ah != bh;
            if (!mismatch && ah && bh)
            {
                if (Math.Abs(a.Intersection - b.Intersection) > 1e-3f) mismatch = true;
                if (Math.Abs(a.Normal.X - b.Normal.X) > 1e-3f ||
                    Math.Abs(a.Normal.Y - b.Normal.Y) > 1e-3f ||
                    Math.Abs(a.Normal.Z - b.Normal.Z) > 1e-3f) mismatch = true;
                if (Math.Abs(a.IntersectionPoint.X - b.IntersectionPoint.X) > 1e-3f ||
                    Math.Abs(a.IntersectionPoint.Y - b.IntersectionPoint.Y) > 1e-3f ||
                    Math.Abs(a.IntersectionPoint.Z - b.IntersectionPoint.Z) > 1e-3f) mismatch = true;
            }

            if (mismatch)
            {
                mismatches++;
                if (reported < 5)
                {
                    reported++;
                    Console.WriteLine($"MISMATCH ray#{i}: dir=({dir.X:F3},{dir.Y:F3},{dir.Z:F3})");
                    Console.WriteLine($"  linear: hit={ah} t={a.Intersection:F5} n=({a.Normal.X:F3},{a.Normal.Y:F3},{a.Normal.Z:F3}) p=({a.IntersectionPoint.X:F3},{a.IntersectionPoint.Y:F3},{a.IntersectionPoint.Z:F3})");
                    Console.WriteLine($"  bvh:    hit={bh} t={b.Intersection:F5} n=({b.Normal.X:F3},{b.Normal.Y:F3},{b.Normal.Z:F3}) p=({b.IntersectionPoint.X:F3},{b.IntersectionPoint.Y:F3},{b.IntersectionPoint.Z:F3})");
                }
            }
        }

        Console.WriteLine($"Rays: {rays}, Hits: {hits}, Mismatches: {mismatches}");
        Console.WriteLine(mismatches == 0 ? "BVH SELF-TEST PASSED" : "BVH SELF-TEST FAILED");
        Object3d.UseBvh = true;
    }

    // Visual + headless display check for 24-bit truecolor. Enables VT the SAME way the renderer
    // does, reports the console mode + truecolor env hints, prints labeled swatches and a blue ramp
    // using the SAME ESC[38;2;r;g;bm emission, and finally writes a raw blue swatch so its bytes can
    // be hexdumped. The user runs this to confirm the terminal renders blue (ruling out a VT issue).
    static void ColorSelfTest()
    {
        char esc = (char)27;
        ConsoleScreenAsync.EnableVirtualTerminal();   // exact renderer VT-enable path

        Console.WriteLine("=== COLOR TEST (24-bit truecolor) ===");

        uint mode = 0; bool gotMode = false;
        if (OperatingSystem.IsWindows())
            try { gotMode = GetConsoleMode(GetStdHandle(StdOut), out mode); } catch { }
        Console.WriteLine(gotMode
            ? $"console mode = 0x{mode:X4}  (ENABLE_VIRTUAL_TERMINAL_PROCESSING 0x4 -> {((mode & 0x4u) != 0 ? "SET" : "NOT set")})"
            : "console mode = (unavailable — stdout redirected or non-Windows; run live in a terminal to see it)");
        Console.WriteLine($"WT_SESSION={Environment.GetEnvironmentVariable("WT_SESSION")}  " +
                          $"COLORTERM={Environment.GetEnvironmentVariable("COLORTERM")}  " +
                          $"TERM_PROGRAM={Environment.GetEnvironmentVariable("TERM_PROGRAM")}");
        Console.WriteLine();

        void Swatch(string label, int r, int g, int b)
            => Console.WriteLine($"{label,-9} {esc}[38;2;{r};{g};{b}m████████{esc}[0m  (escape: 38;2;{r};{g};{b})");

        Swatch("RED",      255, 0,   0);
        Swatch("GREEN",    0,   255, 0);
        Swatch("BLUE",     0,   0,   255);
        Swatch("WHITE",    255, 255, 255);
        Swatch("CYAN",     0,   255, 255);
        Swatch("MAGENTA",  255, 0,   255);
        Swatch("YELLOW",   255, 255, 0);
        Swatch("DARKBLUE", 0,   0,   128);
        Swatch("DARKCYAN", 0,   128, 128);
        Console.WriteLine();

        Console.Write("BLUE RAMP ");
        for (int v = 0; v <= 255; v += 16)
            Console.Write($"{esc}[38;2;0;0;{v}m█");
        Console.WriteLine($"{esc}[0m");
        Console.WriteLine();

        // Raw blue swatch escape, written plainly so its bytes can be hexdumped (must carry 38;2;0;0;255).
        Console.WriteLine("raw BLUE swatch escape (hexdump the line below; bytes must contain 38;2;0;0;255):");
        Console.Out.Write($"{esc}[38;2;0;0;255m████{esc}[0m");
        Console.WriteLine();

        Console.WriteLine("COLOR TEST DONE — expect: PURE BLUE shows blue (not black), ramp goes dark->blue, CYAN != GREEN.");
    }

    private const int StdOut = -11;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    // Returns null if the two worlds match (within float epsilon), else a reason string.
    // Headless end-to-end check of the Stage-1 texture pipeline: decode a KNOWN PNG (encoded in-test
    // with the BCL's ZLibStream so we feed the decoder real, valid PNGs), sample a Texture (nearest +
    // wrap), interpolate UVs on a triangle, and shade a textured BOX — asserting exact texels throughout.
    static void TextureSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== TEXTURE SELF-TEST ===");
        bool ok = true;
        static bool Approx(float a, float b) => MathF.Abs(a - b) < 1e-4f;

        // 1) DECODE — a known 2x2 RGB image (TL red, TR green, BL blue, BR yellow) round-trips exactly.
        Rgba32 red = new(255, 0, 0), green = new(0, 255, 0), blue = new(0, 0, 255), yellow = new(255, 255, 0);
        Rgba32[] rgb = { red, green, blue, yellow };
        var d = PngDecoder.Decode(PngEncode(2, 2, rgb, 2, 0));
        bool decRgb = d.Width == 2 && d.Height == 2 &&
            d.Pixels[0] == red && d.Pixels[1] == green && d.Pixels[2] == blue && d.Pixels[3] == yellow;
        Console.WriteLine($"  decode RGB 2x2: {d.Width}x{d.Height}, pixels match -> {(decRgb ? "ok" : "BAD")}");
        ok &= decRgb;

        // RGBA (colour type 6) with distinct alphas round-trips including the alpha channel.
        Rgba32[] rgba = { new(10, 20, 30, 40), new(50, 60, 70, 80), new(90, 100, 110, 120), new(130, 140, 150, 160) };
        var d2 = PngDecoder.Decode(PngEncode(2, 2, rgba, 6, 0));
        bool decRgba = d2.Width == 2 && d2.Height == 2;
        for (int i = 0; i < 4; i++) decRgba &= d2.Pixels[i] == rgba[i];
        Console.WriteLine($"  decode RGBA 2x2 (with alpha): pixels match -> {(decRgba ? "ok" : "BAD")}");
        ok &= decRgba;

        // Every PNG filter (1 Sub, 2 Up, 3 Average, 4 Paeth) un-filters back to the same pixels.
        bool filters = true;
        for (int ft = 1; ft <= 4; ft++)
        {
            var df = PngDecoder.Decode(PngEncode(2, 2, rgba, 6, ft));
            for (int i = 0; i < 4; i++) filters &= df.Pixels[i] == rgba[i];
        }
        Console.WriteLine($"  un-filter Sub/Up/Average/Paeth -> {(filters ? "ok" : "BAD")}");
        ok &= filters;

        // Unsupported variants are rejected with a CLEAR error (not silently corrupted).
        bool rejCt = false, rejBd = false;
        try { PngDecoder.Decode(PngBad(8, 3)); } catch (NotSupportedException ex) { rejCt = ex.Message.Contains("colour type"); }
        try { PngDecoder.Decode(PngBad(16, 2)); } catch (NotSupportedException ex) { rejBd = ex.Message.Contains("bit depth"); }
        Console.WriteLine($"  reject colour-type-3 -> {(rejCt ? "ok" : "BAD")}; reject 16-bit -> {(rejBd ? "ok" : "BAD")}");
        ok &= rejCt && rejBd;

        // 2) SAMPLE — nearest-neighbour at corners/centre, and WRAP outside [0,1).
        var tex = new Texture(2, 2, rgb);
        bool corners = tex.Sample(0f, 0f) == red && tex.Sample(0.6f, 0f) == green &&
                       tex.Sample(0f, 0.6f) == blue && tex.Sample(0.6f, 0.6f) == yellow;
        bool wrap = tex.Sample(1.0f, 0f) == red && tex.Sample(1.6f, 0.6f) == yellow && tex.Sample(-0.4f, 0f) == green;
        Console.WriteLine($"  sample corners -> {(corners ? "ok" : "BAD")}; wrap (repeat) -> {(wrap ? "ok" : "BAD")}");
        ok &= corners && wrap;

        // 2b) BILINEAR SAMPLE (A2) — the 2x2 image is TL red, TR green, BL blue, BR yellow; alphas equal.
        //   - at a texel edge (fu=fv=0) it equals nearest;  - halfway between two texels = their average;
        //   - at the centre of the 4 texels = their mean;  - WRAP: the last row blends with the first.
        byte al = red.A;
        bool blCentre = tex.SampleBilinear(0f, 0f) == tex.Sample(0f, 0f) && tex.SampleBilinear(0f, 0f) == red;
        bool blBetween = tex.SampleBilinear(0.25f, 0f) == new Rgba32(128, 128, 0, al);       // avg(red, green)
        bool blQuad = tex.SampleBilinear(0.25f, 0.25f) == new Rgba32(128, 128, 64, al);       // mean(red, green, blue, yellow)
        bool blWrap = tex.SampleBilinear(0f, 0.75f) == new Rgba32(128, 0, 128, al);           // avg(blue, wrapped red)
        Console.WriteLine($"  bilinear: centre=nearest -> {(blCentre ? "ok" : "BAD")}; between=avg -> {(blBetween ? "ok" : "BAD")}; " +
                          $"4-centre=mean -> {(blQuad ? "ok" : "BAD")}; wrap edge -> {(blWrap ? "ok" : "BAD")}");
        ok &= blCentre && blBetween && blQuad && blWrap;

        // 3) UV-INTERP — a triangle with corner UVs (0,0)/(1,0)/(0,1), ray hitting bary (0.8,0.1).
        var triV = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        var tri = new Triangle(new int[] { 0, 1, 2 },
            new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
        var triRd = tri.GetRenderData(new Ray(new Vector3(0.8f, 0.1f, 5f), new Vector3(0, 0, -1f)), triV, Vector3.Zero);
        bool uvInterp = triRd.Intersection > -1f && Approx(triRd.Uv.X, 0.8f) && Approx(triRd.Uv.Y, 0.1f);
        Console.WriteLine($"  uv-interp: hit uv=({triRd.Uv.X:F3},{triRd.Uv.Y:F3}) want (0.800,0.100) -> {(uvInterp ? "ok" : "BAD")}");
        ok &= uvInterp;

        // 4) END-TO-END — a textured cube; a ray hits the +Z face at (0.5,-0.2,1) => uv (0.75,0.40).
        const int W = 8, H = 8;
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = new Rgba32((byte)(x * 32), (byte)(y * 32), 128, 255);   // every texel distinct
        var tex4 = new Texture(W, H, px, "unit.png");
        var box = PriviewNetworkScene.CreateCube();
        box.Texture = tex4;   // Color defaults White, ColorFade 0 -> the shaded colour is the raw texel
        var boxRd = box.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        float eu = (0.5f + 1f) * 0.5f, ev = (-0.2f + 1f) * 0.5f;     // +Z face maps (x,y) -> ((x+1)/2,(y+1)/2)
        Rgba32 texel = tex4.Sample(eu, ev);
        Rgba32 expected = box.ShadeTexel(texel);
        bool e2e = boxRd.Intersection > -1f && Approx(boxRd.Uv.X, eu) && Approx(boxRd.Uv.Y, ev) &&
                   boxRd.Color == expected && expected == texel;
        Console.WriteLine($"  end-to-end box: uv=({boxRd.Uv.X:F3},{boxRd.Uv.Y:F3}) want ({eu:F3},{ev:F3}); " +
                          $"colour=({boxRd.Color.R},{boxRd.Color.G},{boxRd.Color.B}) want texel ({texel.R},{texel.G},{texel.B}) -> {(e2e ? "ok" : "BAD")}");
        ok &= e2e;

        // 5) PRIMITIVE UVs (Stage 3) — ramp + pyramid generate procedural per-corner UVs. They interpolate
        // linearly/barycentrically like the cube, so their generated corner UVs must match the hand-derived
        // per-face unwrap exactly (and they get BIT-EXACT GPU parity — proven by gputest).
        static bool UvEq(Vector2 uv, float u, float v) => Approx(uv.X, u) && Approx(uv.Y, v);

        var ramp = PriviewNetworkScene.CreateRamp();
        var rf = ramp.Faces;
        bool rampUv =
            UvEq(rf[0].Uv0, 0, 1) && UvEq(rf[0].Uv1, 0, 0) && UvEq(rf[0].Uv2, 1, 0) &&       // bottom (-Y): drop Y
            UvEq(rf[2].Uv0, 1, 0) && UvEq(rf[2].Uv1, 1, 1) && UvEq(rf[2].Uv2, 0, 1) &&       // sloped top: depth×rise
            UvEq(rf[6].Uv0, 0, 0) && UvEq(rf[6].Uv1, 1, 0) && UvEq(rf[6].Uv2, 1, 1);         // front cap (+Z): drop Z
        Console.WriteLine($"  ramp UVs (bottom/slope/front-cap) -> {(rampUv ? "ok" : "BAD")}");
        ok &= rampUv;

        var pyr = PriviewNetworkScene.CreatePyramid();
        var pf = pyr.Faces;
        bool pyrUv =
            UvEq(pf[0].Uv0, 0, 0) && UvEq(pf[0].Uv1, 1, 0) && UvEq(pf[0].Uv2, 1, 1) &&       // base (-Y): drop Y
            UvEq(pf[2].Uv0, 0, 0) && UvEq(pf[2].Uv1, 0.5f, 1) && UvEq(pf[2].Uv2, 1, 0);      // a side: draped triangle
        Console.WriteLine($"  pyramid UVs (base/side) -> {(pyrUv ? "ok" : "BAD")}");
        ok &= pyrUv;

        // 6) SPHERE UV (Stage 3) — equirectangular map at the cardinal directions + the -X seam (u wraps
        // 0↔1) + both poles, and that LocalRotate rotates the mapping (a +X world normal on a Y-90° sphere
        // reads the texel that +Z reads unrotated). Matches the GPU kernel's SphereUv but for the atan2/asin band.
        bool sphereUv =
            UvEq(Sphere.EquirectangularUv(new Vector3(1, 0, 0)), 0.5f, 0.5f) &&    // +X equator
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 0, 1)), 0.75f, 0.5f) &&   // +Z
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 0, -1)), 0.25f, 0.5f) &&  // -Z
            UvEq(Sphere.EquirectangularUv(new Vector3(0, 1, 0)), 0.5f, 0f) &&      // +Y north pole
            UvEq(Sphere.EquirectangularUv(new Vector3(0, -1, 0)), 0.5f, 1f) &&     // -Y south pole
            UvEq(Sphere.EquirectangularUv(new Vector3(-1, 0, 0)), 1f, 0.5f);       // -X seam
        bool sphereRot = UvEq(
            Sphere.EquirectangularUv(new Vector3(1, 0, 0).RotateInverse(new Vector3(0, MathF.PI / 2f, 0))), 0.75f, 0.5f);
        Console.WriteLine($"  sphere UV cardinals+seam+poles -> {(sphereUv ? "ok" : "BAD")}; rotates with object -> {(sphereRot ? "ok" : "BAD")}");
        ok &= sphereUv && sphereRot;

        // 7) FLAT PICTURE (Stage 3b) — a two-sided vertical quad whose four corners map the FULL texture
        // (0,0)/(1,0)/(0,1)/(1,1), right-side-up. Front AND back faces (reversed winding) both carry UVs
        // (mirrored on the back). Linear interp → BIT-EXACT GPU parity (proven by gputest).
        var pic = PriviewNetworkScene.CreateFlatPicture();
        var cf = pic.Faces;
        bool picUv =
            cf.Count == 4 &&                                                              // 2 front + 2 back tris (two-sided)
            UvEq(cf[0].Uv0, 0, 1) && UvEq(cf[0].Uv1, 1, 1) && UvEq(cf[0].Uv2, 1, 0) &&    // front: BL,BR,TR
            UvEq(cf[1].Uv0, 0, 1) && UvEq(cf[1].Uv1, 1, 0) && UvEq(cf[1].Uv2, 0, 0) &&    // front: BL,TR,TL
            UvEq(cf[2].Uv0, 0, 1) && UvEq(cf[2].Uv1, 1, 0) && UvEq(cf[2].Uv2, 1, 1);      // back (reversed): BL,TR,BR
        Console.WriteLine($"  flat-picture UVs (front+back corners, {cf.Count} tris) -> {(picUv ? "ok" : "BAD")}");
        ok &= picUv;

        // 8) CYLINDER + CONE (Stage-3 tail) — procedural UVs added because the engine's PER-FACE-CORNER
        // UV model needs NO seam-vertex duplication: side u = angle fraction (v: top 0, bottom 1), caps
        // wrap the ring onto a disc (centre 0.5,0.5). Linear interp → bit-exact parity like the others.
        var cyl = PriviewNetworkScene.CreateCylinder();
        var yf = cyl.Faces;
        bool cylUv =
            UvEq(yf[0].Uv0, 0f, 1f) && UvEq(yf[0].Uv1, 1f / 16f, 0f) && UvEq(yf[0].Uv2, 1f / 16f, 1f) &&   // first side-quad tri
            UvEq(yf[2].Uv0, 0.5f, 0.5f) && UvEq(yf[2].Uv2, 1f, 0.5f);                                       // top-cap centre + ring @angle0
        var cone = PriviewNetworkScene.CreateCone();
        var nf = cone.Faces;
        bool coneUv =
            UvEq(nf[0].Uv0, 0f, 1f) && UvEq(nf[0].Uv1, 1f / 32f, 0f) && UvEq(nf[0].Uv2, 1f / 16f, 1f) &&    // side: base→apex-midpoint→base
            UvEq(nf[1].Uv0, 0.5f, 0.5f) && UvEq(nf[1].Uv1, 1f, 0.5f);                                        // base-cap centre + ring @angle0
        Console.WriteLine($"  cylinder/cone UVs (side+cap) -> {((cylUv && coneUv) ? "ok" : "BAD")}");
        ok &= cylUv && coneUv;

        // 9) TEXTURE PARAMS (Stage 4) — face-group tagging, UV scale/tiling, per-face gating, options list.
        // (a) The cube's 12 triangles tag into 6 groups (0..5 = +X,-X,+Y,-Y,+Z,-Z), two triangles each.
        var pcube = PriviewNetworkScene.CreateCube();
        var counts = new int[6];
        bool groupsInRange = true;
        foreach (var t in pcube.Faces) { if (t.Group >= 0 && t.Group < 6) counts[t.Group]++; else groupsInRange = false; }
        bool cubeGroups = groupsInRange;
        for (int gi = 0; gi < 6; gi++) if (counts[gi] != 2) cubeGroups = false;
        Console.WriteLine($"  cube face-groups: [{string.Join(",", counts)}] want all 2 -> {(cubeGroups ? "ok" : "BAD")}");
        ok &= cubeGroups;

        // (b) Scale tiling: the +Z hit at (0.5,-0.2,1) → uv (0.75,0.40); with TextureScale=2 the sampled
        // texel is tex4.Sample(0.75*2, 0.40*2) — wrap tiles it. (tex4/eu/ev are from section 4.)
        var scube = PriviewNetworkScene.CreateCube();
        scube.Texture = tex4; scube.TextureScale = 2f;
        var scubeRd = scube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        Rgba32 scaledTexel = scube.ShadeTexel(tex4.Sample(eu * 2f, ev * 2f));
        bool scaleTiling = scubeRd.Intersection > -1f && scubeRd.Color == scaledTexel && scaledTexel != tex4.Sample(eu, ev);
        Console.WriteLine($"  scale tiling (x2): colour=({scubeRd.Color.R},{scubeRd.Color.G},{scubeRd.Color.B}) want ({scaledTexel.R},{scaledTexel.G},{scaledTexel.B}) -> {(scaleTiling ? "ok" : "BAD")}");
        ok &= scaleTiling;

        // (c) Per-face gate: with TextureFace=+Z (group 4) the +Z hit is textured; set to +X (group 0) and
        // the SAME +Z hit shows flat colour instead.
        var fcube = PriviewNetworkScene.CreateCube();
        fcube.Texture = tex4; fcube.Color = new Rgba32(30, 60, 90);
        fcube.TextureFace = 4;
        var fOn = fcube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        bool faceOn = fOn.Intersection > -1f && fOn.Group == 4 && fOn.Color == fcube.ShadeTexel(tex4.Sample(eu, ev));
        fcube.TextureFace = 0;
        var fOff = fcube.GetRenderData(new Ray(new Vector3(0.5f, -0.2f, 5f), new Vector3(0, 0, -1f)));
        bool faceOff = fOff.Intersection > -1f && fOff.Color == fcube.EffectiveColor;
        Console.WriteLine($"  texture-face gate: +Z textured -> {(faceOn ? "ok" : "BAD")}; +Z flat when face=+X -> {(faceOff ? "ok" : "BAD")}");
        ok &= faceOn && faceOff;

        // (d) Face options: a cube exposes 6 sides; a sphere (analytic, whole) exposes none.
        int cubeOpts = PriviewNetworkScene.TextureFaceOptions("cube").Length;
        int sphOpts = PriviewNetworkScene.TextureFaceOptions("sphere").Length;
        bool faceOpts = cubeOpts == 6 && sphOpts == 0;
        Console.WriteLine($"  face options: cube={cubeOpts} (want 6), sphere={sphOpts} (want 0) -> {(faceOpts ? "ok" : "BAD")}");
        ok &= faceOpts;

        // 10) IMPORTED .OBJ UVs (Stage 5) — ObjLoader now parses `vt` and feeds per-corner UVs with the
        // OBJ→image v-flip (v_tex = 1 - v_obj). A 2-tri quad with known vt: verts 1..4 at vt (0,0)(1,0)(1,1)(0,1)
        // → after the flip the corners read (0,1)(1,1)(1,0)(0,0); the fan tris are (1,2,3) and (1,3,4).
        const string quadObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nv 0 1 -1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn -1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n";
        var quadMesh = ObjLoader.Load(WriteObjFixture("uvquad", quadObj));
        var qf = quadMesh.Faces;
        bool objUv = qf.Count == 2 &&
            UvEq(qf[0].Uv0, 0, 1) && UvEq(qf[0].Uv1, 1, 1) && UvEq(qf[0].Uv2, 1, 0) &&   // tri (v1,v2,v3)
            UvEq(qf[1].Uv0, 0, 1) && UvEq(qf[1].Uv1, 1, 0) && UvEq(qf[1].Uv2, 0, 0);     // tri (v1,v3,v4)
        Console.WriteLine($"  imported .obj UVs (v-flipped, {qf.Count} tris) -> {(objUv ? "ok" : "BAD")}");
        ok &= objUv;

        // A vt-less .obj yields Zero UVs (untextured meshes stay byte-identical).
        const string plainObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nvn -1 0 0\nf 1//1 2//1 3//1\n";
        var plainMesh = ObjLoader.Load(WriteObjFixture("nouvquad", plainObj));
        bool objNoUv = plainMesh.Faces.Count == 1 &&
            UvEq(plainMesh.Faces[0].Uv0, 0, 0) && UvEq(plainMesh.Faces[0].Uv1, 0, 0) && UvEq(plainMesh.Faces[0].Uv2, 0, 0);
        Console.WriteLine($"  vt-less .obj -> Zero UVs -> {(objNoUv ? "ok" : "BAD")}");
        ok &= objNoUv;

        // 11) REAL DISK PATH (diagnostics) — exercise the actual disk→decode→attach chain in the REAL
        // AppPaths.TexturesFolder. A known-good 8-bit RGBA PNG must load; a JPEG-magic file renamed .png
        // must fail with a message that NAMES it as a JPEG. Uses unique names + cleans up after itself.
        Console.WriteLine($"  resolved textures folder: {AppPaths.TexturesFolder}");
        Directory.CreateDirectory(AppPaths.TexturesFolder);
        string goodName = "__texload_selftest__.png";
        string jpgName = "__texload_jpeg__.png";
        string goodPath = Path.Combine(AppPaths.TexturesFolder, goodName);
        string jpgPath = Path.Combine(AppPaths.TexturesFolder, jpgName);
        try
        {
            File.WriteAllBytes(goodPath, PngEncode(2, 2, rgba, 6, 0));                          // real 8-bit RGBA PNG
            File.WriteAllBytes(jpgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1 });  // JPEG/JFIF magic

            var loaded = TextureLoader.Get(goodName);
            bool diskLoad = loaded != null && loaded.Width == 2 && loaded.Height == 2;
            Console.WriteLine($"  disk load (real loader): '{goodName}' -> {(loaded != null ? $"{loaded.Width}x{loaded.Height}" : "NULL")} -> {(diskLoad ? "ok" : "BAD")}");
            ok &= diskLoad;

            string jpgReason = "";
            try { PngDecoder.Decode(File.ReadAllBytes(jpgPath)); }
            catch (Exception ex) { jpgReason = ex.Message; }
            bool jpgClear = jpgReason.Contains("JPEG", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  jpeg-as-png names the format: \"{jpgReason}\" -> {(jpgClear ? "ok" : "BAD")}");
            ok &= jpgClear;
        }
        finally
        {
            try { File.Delete(goodPath); } catch { }
            try { File.Delete(jpgPath); } catch { }
        }

        Console.WriteLine(ok ? "TEXTURE TEST PASSED" : "TEXTURE TEST FAILED");
    }

    // Writes a fixture .obj into a temp folder and returns its full path so ObjLoader.Load can read it.
    // Used by the texture self-tests to exercise the .obj `vt` parsing without polluting the models/ library.
    static string WriteObjFixture(string name, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nova_tex_fixtures");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".obj");
        File.WriteAllText(path, content);
        return path;
    }

    // --- tiny PNG ENCODER (test-only): produce a real, valid PNG so the decoder is exercised end-to-end.
    // Applies one filter type to every scanline; compresses via ZLibStream (zlib header + Adler-32); writes
    // IHDR/IDAT/IEND with correct CRC-32s. colorType 2 = RGB, 6 = RGBA; bit depth fixed at 8.
    static byte[] PngEncode(int w, int h, Rgba32[] pixels, int colorType, int filterType)
    {
        int channels = colorType == 6 ? 4 : 3;
        int stride = w * channels;
        byte[] raw = new byte[h * (stride + 1)];
        byte[] prev = new byte[stride];
        int rp = 0;
        for (int y = 0; y < h; y++)
        {
            byte[] cur = new byte[stride];
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = pixels[y * w + x];
                int o = x * channels;
                cur[o] = p.R; cur[o + 1] = p.G; cur[o + 2] = p.B;
                if (channels == 4) cur[o + 3] = p.A;
            }
            raw[rp++] = (byte)filterType;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? cur[x - channels] : 0;
                int b = prev[x];
                int c = x >= channels ? prev[x - channels] : 0;
                int fv = filterType switch
                {
                    1 => cur[x] - a,
                    2 => cur[x] - b,
                    3 => cur[x] - (a + b) / 2,
                    4 => cur[x] - PngPaeth(a, b, c),
                    _ => cur[x],
                };
                raw[rp++] = (byte)(fv & 0xFF);
            }
            prev = cur;
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        byte[] ihdr = new byte[13];
        PngPutBE32(ihdr, 0, w); PngPutBE32(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = (byte)colorType;   // [10] compression, [11] filter, [12] interlace all 0

        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        PngWriteChunk(outMs, "IHDR", ihdr);
        PngWriteChunk(outMs, "IDAT", idat);
        PngWriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    // A structurally valid PNG whose IHDR declares an UNSUPPORTED format (the decoder must reject it at
    // the IHDR check, before inflating — so the IDAT contents are irrelevant).
    static byte[] PngBad(int bitDepth, int colorType)
    {
        byte[] ihdr = new byte[13];
        PngPutBE32(ihdr, 0, 1); PngPutBE32(ihdr, 4, 1);
        ihdr[8] = (byte)bitDepth; ihdr[9] = (byte)colorType;
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[] { 0, 0 }, 0, 2);
            idat = ms.ToArray();
        }
        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        PngWriteChunk(outMs, "IHDR", ihdr);
        PngWriteChunk(outMs, "IDAT", idat);
        PngWriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    static void PngWriteChunk(MemoryStream s, string type, byte[] data)
    {
        byte[] lenb = new byte[4]; PngPutBE32(lenb, 0, data.Length); s.Write(lenb, 0, 4);
        byte[] typeb = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeb, 0, typeb.Length);
        s.Write(data, 0, data.Length);
        byte[] crcIn = new byte[typeb.Length + data.Length];
        Buffer.BlockCopy(typeb, 0, crcIn, 0, typeb.Length);
        Buffer.BlockCopy(data, 0, crcIn, typeb.Length, data.Length);
        byte[] crcb = new byte[4]; PngPutBE32(crcb, 0, unchecked((int)PngCrc32(crcIn))); s.Write(crcb, 0, 4);
    }

    static uint PngCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte bb in data)
        {
            crc ^= bb;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    static int PngPaeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    static void PngPutBE32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)((v >> 24) & 0xFF);
        buf[off + 1] = (byte)((v >> 16) & 0xFF);
        buf[off + 2] = (byte)((v >> 8) & 0xFF);
        buf[off + 3] = (byte)(v & 0xFF);
    }

    // A deterministic smooth-shaded UV sphere mesh (lat×lon grid), used by gputest to give the GPU
    // BVH a real, deep tree to traverse (>64 tris). Vertex normals = unit position (smooth shading).
    static Object3d BuildUvSphere(float radius, int lat, int lon)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        int stride = lon + 1;
        for (int i = 0; i <= lat; i++)
        {
            float theta = MathF.PI * i / lat;
            for (int j = 0; j <= lon; j++)
            {
                float phi = MathF.Tau * j / lon;
                float x = MathF.Sin(theta) * MathF.Cos(phi);
                float y = MathF.Cos(theta);
                float z = MathF.Sin(theta) * MathF.Sin(phi);
                verts.Add(new Vector3(x, y, z) * radius);
                norms.Add(new Vector3(x, y, z));
            }
        }
        var faces = new List<FacingInfo>();
        for (int i = 0; i < lat; i++)
            for (int j = 0; j < lon; j++)
            {
                int a = i * stride + j + 1, b = i * stride + j + 2;            // 1-based
                int c = (i + 1) * stride + j + 1, d = (i + 1) * stride + j + 2;
                faces.Add(new FacingInfo(new[] { a, d, b }, new[] { a, d, b }));
                faces.Add(new FacingInfo(new[] { a, c, d }, new[] { a, c, d }));
            }
        return new Object3d(verts.ToArray(), norms.ToArray(), faces.ToArray());
    }

    // A tiny deterministic scene (one cube + one sphere + one point light) used only by gputest.
    sealed class GpuTestScene : Scene
    {
        public GpuTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = true;

            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(10f, 0f, 0f);
            cube.Scale = 1.6f;
            cube.Color = new Rgba32(220, 50, 50);
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            var sphere = new Sphere(new Vector3(8f, -1f, 2.6f), Vector3.Zero, 1.3f) { Color = new Rgba32(60, 90, 230) };
            AddDisplaysObject(sphere);

            // A TRANSPARENT sphere directly in front of the cube — exercises front-to-back compositing
            // (a semi-transparent layer over the opaque cube behind it).
            var glass = new Sphere(new Vector3(4f, 0f, 0f), Vector3.Zero, 1.2f) { Color = new Rgba32(70, 230, 120, 128) };
            AddDisplaysObject(glass);

            // A high-poly, rotated mesh (1536 tris) — gives the GPU two-level BVH a real, deep tree to
            // traverse in the object's local space (with a non-trivial rotation/scale transform).
            var hi = BuildUvSphere(1f, 24, 32);
            hi.Position = new Vector3(11f, 2f, 1f);
            hi.Scale = 1.8f;
            hi.LocalRotate = new Vector3(0.4f, 0.7f, 0.2f);
            hi.Color = new Rgba32(200, 180, 60);
            hi.ColorFade = 0.5f;   // exercise colour-paleness: baked into both the CPU shade and the GPU snapshot
            hi.UpdateGeometry();
            hi.BuildAcceleration();
            AddDisplaysObject(hi);

            // One of every LightKind, with the rich extras: a point, a multi-beam SQUARE spot, and a
            // TRIANGLE area light — so the GPU/CPU parity test covers beams, cone shapes and area sampling.
            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.9f, 0.8f) });

            AddLight(new Light(new Vector3(6f, 6f, 0f), 800f)
            {
                Kind = LightKind.Spot, Direction = new Vector3(0.3f, -1f, 0f).Norm(),
                ConeAngleDeg = 35f, BeamCount = 2, ConeShape = ConeShapeKind.Square,
                Rgb = new Vector3(0.7f, 0.8f, 1f), ColorFade = 0.4f,   // pale the emission (parity check)
            });

            AddLight(new Light(new Vector3(6f, 6f, -4f), 400f)
            {
                Kind = LightKind.Area, Direction = new Vector3(0f, -1f, 0.3f).Norm(),
                AreaSize = 1.5f, AreaShape = ConeShapeKind.Triangle,
                Rgb = new Vector3(1f, 0.85f, 0.7f),
            });

            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-2 texture-parity scene: a TEXTURED cube (a known unique-texel image) plus an UNtextured cube,
    // so the GPU texture path and the flat-colour path are compared side by side against the CPU. Shadows
    // are off (this scene exists to prove texel-fetch parity is EXACT, independent of the shadow band).
    sealed class GpuTextureTestScene : Scene
    {
        public GpuTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            // A small texture with a UNIQUE texel per cell, so any wrong fetch (bad UV / wrap / channel
            // order / offset) shows up as a colour mismatch rather than blending away.
            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gputex");

            // Textured cube, tilted so several faces show and the UV interpolation is genuinely 3D.
            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(8f, 0f, 0f);
            cube.Scale = 2.2f;
            cube.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            cube.Color = Rgba32.White;   // unused when textured (both renderers sample the texel), but a bug would show white
            cube.Texture = tex;
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            // A second, UNtextured cube alongside — textured + flat objects must coexist at parity.
            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(11f, 1.6f, 2.2f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(80, 200, 120);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            // Textured RAMP + PYRAMID (Stage 3) — Object3d meshes whose procedural per-corner UVs
            // interpolate barycentrically exactly like the cube, so they must hit the SAME texel-exact
            // parity (Δ=0 interior). Tilted so several faces + the UV interpolation are genuinely 3D.
            var ramp = PriviewNetworkScene.CreateRamp();
            ramp.Position = new Vector3(9f, -1.9f, -2.2f);
            ramp.Scale = 1.3f;
            ramp.LocalRotate = new Vector3(0.2f, 0.6f, 0.05f);
            ramp.Texture = tex;
            ramp.UpdateGeometry();
            AddDisplaysObject(ramp);

            var pyr = PriviewNetworkScene.CreatePyramid();
            pyr.Position = new Vector3(10f, 1.9f, -2.4f);
            pyr.Scale = 1.3f;
            pyr.LocalRotate = new Vector3(0.1f, 0.9f, 0.15f);
            pyr.Texture = tex;
            pyr.UpdateGeometry();
            AddDisplaysObject(pyr);

            // Textured FLAT PICTURE (Stage 3b) — a two-sided vertical quad, rotated to face the camera and
            // placed CLOSE + in front so it's unoccluded. Its linear quad UVs must be texel-EXACT (Δ=0).
            var pic = PriviewNetworkScene.CreateFlatPicture();
            pic.Position = new Vector3(5.5f, 0f, 1.3f);
            pic.Scale = 1.4f;
            pic.LocalRotate = new Vector3(0.1f, 1.3f, 0.05f);
            pic.Texture = tex;
            pic.UpdateGeometry();
            AddDisplaysObject(pic);

            // Textured CYLINDER + CONE (Stage-3 tail) — their per-face-corner UVs interpolate linearly like
            // every other mesh primitive, so they too must be texel-EXACT (Δ=0). Placed clear of the others.
            var cyl = PriviewNetworkScene.CreateCylinder();
            cyl.Position = new Vector3(7f, 2.2f, -1.5f);
            cyl.Scale = 1.2f;
            cyl.LocalRotate = new Vector3(0.25f, 0.4f, 0.1f);
            cyl.Texture = tex;
            cyl.UpdateGeometry();
            AddDisplaysObject(cyl);

            var cone = PriviewNetworkScene.CreateCone();
            cone.Position = new Vector3(7f, -2.2f, 1.8f);
            cone.Scale = 1.2f;
            cone.LocalRotate = new Vector3(0.15f, 0.7f, 0.2f);
            cone.Texture = tex;
            cone.UpdateGeometry();
            AddDisplaysObject(cone);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });

            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-3 sphere-texture parity: a TEXTURED sphere (analytic equirectangular UV via atan2/asin) beside
    // an UNtextured cube. Because atan2/asin round slightly differently on the GPU, a THIN seam/pole band
    // may differ CPU↔GPU (tolerated like the shadow band); the untextured cube stays exact. Shadows off.
    sealed class GpuSphereTextureTestScene : Scene
    {
        public GpuSphereTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            // Unique-texel image (G held at 100 so every hit stays above the brightness threshold — a dark
            // texel at a seam pixel must not read as a background "miss" and inflate the silhouette count).
            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(40 + x * 24), 100, (byte)(40 + y * 24), 255);
            var tex = new Texture(TW, TH, px, "gpusphere");

            // Zero LocalRotate: the analytic intersection is then identical on CPU and GPU, so the ONLY
            // divergence is the transcendental UV — a clean measurement of the seam/pole band.
            var ball = new Sphere(new Vector3(7f, 0f, 0f), Vector3.Zero, 1.7f) { Color = Rgba32.White, Texture = tex };
            AddDisplaysObject(ball);

            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(11f, 1.4f, 2.6f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(90, 160, 210);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-4 texture-PARAMS parity: a TILED cube (TextureScale=2 → 2×2) and a SINGLE-FACE cube (only its
    // +Z group textured, the other 5 faces flat colour). Both the scale and the per-face gate are exact
    // (integer group compare + linear UV), so CPU↔GPU must be Δ=0. Shadows off.
    sealed class GpuTextureParamsTestScene : Scene
    {
        public GpuTextureParamsTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpuparams");

            // TILED cube — TextureScale=2 tiles the image 2×2 on every face.
            var tiled = PriviewNetworkScene.CreateCube();
            tiled.Position = new Vector3(8f, 0f, 0f);
            tiled.Scale = 2.2f;
            tiled.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            tiled.Texture = tex;
            tiled.TextureScale = 2f;
            tiled.UpdateGeometry();
            AddDisplaysObject(tiled);

            // SINGLE-FACE cube — only the +Z group (4) is textured; the other faces show flat colour.
            var oneFace = PriviewNetworkScene.CreateCube();
            oneFace.Position = new Vector3(11f, 1.6f, 2.4f);
            oneFace.Scale = 1.6f;
            oneFace.LocalRotate = new Vector3(0.2f, 0.9f, 0.1f);
            oneFace.Color = new Rgba32(70, 160, 90);
            oneFace.Texture = tex;
            oneFace.TextureFace = 4;
            oneFace.UpdateGeometry();
            AddDisplaysObject(oneFace);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // A2 BILINEAR parity: a BILINEAR-filtered textured cube. Bilinear blends 4 texels with float lerps that
    // round slightly differently on GPU (XMath) vs CPU (MathF), so — UNLIKE nearest — a THIN band of interior
    // pixels may differ by ~1; we require only that the band stay thin (like the sphere seam), NOT Δ=0. A
    // small 8×8 texture magnified over the cube makes most pixels land BETWEEN texels, genuinely blending.
    sealed class GpuBilinearTextureTestScene : Scene
    {
        public GpuBilinearTextureTestScene() : base(new DisplayManagerAsync()) { }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpubilinear");

            var cube = PriviewNetworkScene.CreateCube();
            cube.Position = new Vector3(8f, 0f, 0f);
            cube.Scale = 2.4f;
            cube.LocalRotate = new Vector3(0.3f, 0.5f, 0.15f);
            cube.Texture = tex;
            cube.TextureFilter = TextureFilterMode.Bilinear;   // the opt-in smoothing under test
            cube.UpdateGeometry();
            AddDisplaysObject(cube);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // Stage-5 imported-mesh parity: a TEXTURED .obj mesh (loaded via ObjLoader, so its per-corner UVs came
    // from the file's `vt` with the v-flip) beside an untextured cube. Imported UVs interpolate linearly
    // like the cube, so CPU↔GPU must be Δ=0. Shadows off. The mesh is supplied by the caller (a fixture).
    sealed class GpuImportedMeshTestScene : Scene
    {
        private readonly Object3d _mesh;
        public GpuImportedMeshTestScene(Object3d mesh) : base(new DisplayManagerAsync()) { _mesh = mesh; }

        public override void Start()
        {
            Exposure = 0.05f; Ambient = 0.1f; EnableShadows = false;

            const int TW = 8, TH = 8;
            var px = new Rgba32[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                    px[y * TW + x] = new Rgba32((byte)(20 + x * 28), (byte)(20 + y * 28), 128, 255);
            var tex = new Texture(TW, TH, px, "gpuimport");

            // The fixture quad's normal faces -X (toward the camera at the origin); a small tilt makes the
            // UV interpolation genuinely 3D. Placed close + in front so it's unoccluded.
            _mesh.Position = new Vector3(6f, 0f, 0f);
            _mesh.Scale = 1.6f;
            _mesh.LocalRotate = new Vector3(0.1f, 0.15f, 0.05f);
            _mesh.Texture = tex;
            _mesh.UpdateGeometry();
            AddDisplaysObject(_mesh);

            var plain = PriviewNetworkScene.CreateCube();
            plain.Position = new Vector3(10f, 1.4f, 2f);
            plain.Scale = 1.2f;
            plain.Color = new Rgba32(90, 160, 210);
            plain.UpdateGeometry();
            AddDisplaysObject(plain);

            AddLight(new Light(new Vector3(2f, 5f, 1f), 600f) { Rgb = new Vector3(1f, 0.95f, 0.9f) });
            SetMainCamera(new Camera(new Vector3(0f, 0f, 0f), Vector3.Zero));
        }

        public override void Update() { }
    }

    // gputest: render a fixed scene with the GPU kernel (on whatever accelerator ILGPU finds — CUDA on
    // an NVIDIA box, else the managed CPU accelerator in CI) and compare it to the engine's own CPU
    // raytracer pixel-by-pixel. They share the same intersection + shading + tone-map math for the
    // supported "fast path", so an opaque scene with a point light must match within float epsilon
    // (a few silhouette-edge pixels may differ on rounding — that is the small allowed budget).
    static void GpuSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== GPU SELF-TEST ===");

        try
        {
            var scene = new GpuTestScene();
            scene.Start();

            const int W = 64, H = 32;
            const float aspect = 1.6f;

            using var rt = new Nova3DVisualiser.Gpu.GpuRaytracer(requireGpu: false);
            Console.WriteLine($"  accelerator: {rt.AcceleratorName} (hardware GPU = {rt.IsHardwareGpu})");

            var brightness = new float[W * H];
            var color = new Rgb24[W * H];

            // Compares the GPU image to the engine's CPU image for the current shadow setting, counting
            // a pixel as a mismatch only when it exceeds (bTol, cTol). Mismatches are split into "edge"
            // (one renderer hit geometry, the other saw background — a silhouette float flip) and
            // "interior" (both hit the surface but the shaded/shadowed value differs). With shadows off
            // every feature is deterministic, so interior MUST be 0; with shadows on, soft/hard shadow
            // boundaries add a benign band of interior flips.
            (int nonBlack, int edge, int interior, float worstB, int worstC) Compare(Scene s, float bTol, int cTol)
            {
                SceneSnapshot snap = s.BuildSnapshot();
                rt.Render(snap, W, H, aspect, brightness, color);

                int nb = 0, edge = 0, interior = 0; float wb = 0f; int wc = 0;
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++)
                    {
                        float uvx = ((float)i / (W - 1) * 2f - 1f) * aspect;
                        float uvy = -((float)j / (H - 1) * 2f - 1f);
                        var cpu = s.GetPixelData(new Vector2(uvx, uvy));
                        int idx = j * W + i;
                        if (cpu.Brightness > 0.01f) nb++;

                        float db = Math.Abs(cpu.Brightness - brightness[idx]);
                        int dc = Math.Max(Math.Abs(cpu.Color.R - color[idx].R),
                                 Math.Max(Math.Abs(cpu.Color.G - color[idx].G), Math.Abs(cpu.Color.B - color[idx].B)));
                        if (db <= bTol && dc <= cTol) continue;

                        bool cpuHit = cpu.Brightness > 0.01f, gpuHit = brightness[idx] > 0.01f;
                        if (cpuHit != gpuHit) edge++;                 // silhouette hit/miss flip
                        else { interior++; wb = Math.Max(wb, db); wc = Math.Max(wc, dc); }
                    }
                return (nb, edge, interior, wb, wc);
            }

            int total = W * H;

            // Pass A — shadows OFF: intersection + transparency compositing + every light kind (incl.
            // beams, cone shapes, area sampling) + tone-map are all deterministic, so the GPU image must
            // match the CPU EXACTLY. ANY interior mismatch here (Δ>0) is a real kernel bug.
            scene.EnableShadows = false;
            var a = Compare(scene, 0.004f, 1);   // tiny: absorbs ULP/rounding noise, far below any feature-level diff
            Console.WriteLine($"  [no shadows] nonBlack={a.nonBlack}, edge={a.edge}, interior={a.interior} (worstΔb={a.worstB:F4}, worstΔc={a.worstC})");

            // Pass B — shadows ON: shadow rays are float-sensitive at boundaries (and the area light's
            // 4 occlusion samples form a soft penumbra), so a band of pixels differs by a SMALL amount.
            // We tolerate sub-penumbra noise per pixel and only require the disagreement to stay bounded
            // in magnitude and not become pervasive (which a systematic shadow bug would).
            scene.EnableShadows = true;
            var b = Compare(scene, 0.1f, 28);
            Console.WriteLine($"  [shadows]    nonBlack={b.nonBlack}, edge={b.edge}, interior={b.interior} (worstΔb={b.worstB:F4}, worstΔc={b.worstC})");

            // Pass C — TEXTURED parity (Stage 2), shadows OFF: a textured cube must fetch the SAME texel on
            // GPU and CPU. Interior must be EXACT (Δ=0). A texel-BOUNDARY band can arise where CPU (the box
            // takes the world-space non-BVH path) and GPU (local-space BVH) barycentric noise straddles a
            // texel edge — tolerated as a thin band (the analog of the shadow band) and reported explicitly.
            var texScene = new GpuTextureTestScene();
            texScene.Start();
            rt.ResetGeometryCache();   // reuse one raytracer across scenes: force a geometry re-upload (versions can collide)
            var c = Compare(texScene, 0.004f, 1);
            int texBand = c.interior;                                    // texel-edge float flips, if any
            Console.WriteLine($"  [textured]   nonBlack={c.nonBlack}, edge={c.edge}, interior={c.interior} (worstΔb={c.worstB:F4}, worstΔc={c.worstC}), texel-boundary band={texBand}");

            // Pass D — TEXTURED SPHERE (Stage 3), shadows OFF: the equirectangular UV uses atan2/asin, which
            // round slightly differently on the GPU, so a THIN seam/pole band differs (the analog of the
            // shadow band). The mesh primitives (Pass C) stay EXACT; here we only require the band to be thin.
            var sphScene = new GpuSphereTextureTestScene();
            sphScene.Start();
            rt.ResetGeometryCache();   // new scene → force a geometry re-upload
            var e = Compare(sphScene, 0.004f, 1);
            Console.WriteLine($"  [tex-sphere] nonBlack={e.nonBlack}, edge={e.edge}, seam/pole band={e.interior} (worstΔb={e.worstB:F4}, worstΔc={e.worstC})");

            // Pass F — TEXTURE PARAMS (Stage 4), shadows OFF: a TILED cube (TextureScale=2) + a SINGLE-FACE
            // cube (only +Z textured, the rest flat). Both the tiling and the per-face gate are exact
            // (integer group compare + linear UV), so CPU↔GPU must be EXACT (Δ=0, band 0).
            var paramScene = new GpuTextureParamsTestScene();
            paramScene.Start();
            rt.ResetGeometryCache();
            var pf = Compare(paramScene, 0.004f, 1);
            Console.WriteLine($"  [tex-params] nonBlack={pf.nonBlack}, edge={pf.edge}, interior={pf.interior} (worstΔb={pf.worstB:F4}, worstΔc={pf.worstC})");

            // Pass G — IMPORTED-MESH texture (Stage 5), shadows OFF: a textured .obj (UVs parsed from `vt`
            // via ObjLoader, v-flipped) must be texel-EXACT CPU↔GPU (Δ=0) — its per-corner UVs interpolate
            // linearly like the cube. Loaded from an in-test fixture .obj so no models/ file is needed.
            const string gpuQuadObj = "v 0 -1 -1\nv 0 -1 1\nv 0 1 1\nv 0 1 -1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn -1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n";
            var importMesh = ObjLoader.Load(WriteObjFixture("uvquad", gpuQuadObj));
            var importScene = new GpuImportedMeshTestScene(importMesh);
            importScene.Start();
            rt.ResetGeometryCache();
            var pg = Compare(importScene, 0.004f, 1);
            Console.WriteLine($"  [tex-import] nonBlack={pg.nonBlack}, edge={pg.edge}, interior={pg.interior} (worstΔb={pg.worstB:F4}, worstΔc={pg.worstC})");

            // Pass H — BILINEAR (A2), shadows OFF: an opt-in bilinear-filtered cube. The 4-texel float blend
            // rounds slightly differently on GPU (XMath) vs CPU (MathF), so we tolerate ±1 per pixel and only
            // require the BAND beyond that (Δc>=2) to stay THIN — NOT Δ=0. The nearest passes above prove the
            // default filter is untouched (still exact). Report the band + worstΔ.
            var bilScene = new GpuBilinearTextureTestScene();
            bilScene.Start();
            rt.ResetGeometryCache();
            var ph = Compare(bilScene, 0.004f, 1);
            Console.WriteLine($"  [tex-bilinear] nonBlack={ph.nonBlack}, edge={ph.edge}, band={ph.interior} (worstΔb={ph.worstB:F4}, worstΔc={ph.worstC})");

            // A3 — TARGETED TEXTURE RE-UPLOAD. (correctness) a texture-version-only bump re-renders the same
            // image BYTE-IDENTICALLY via the pool-only upload path; (behaviour) it re-uploads the texture pool
            // but NOT the geometry, while a geometry-version bump does re-upload geometry — proving the swap is
            // targeted, not a full geometry re-upload, with the output unchanged.
            bool reuploadOk;
            {
                var reScene = new GpuTextureTestScene();
                reScene.Start();
                rt.ResetGeometryCache();
                var snap = reScene.BuildSnapshot();

                var bright1 = new float[W * H]; var col1 = new Rgb24[W * H];
                int g0 = rt.GeometryUploads, t0 = rt.TextureUploads;
                rt.Render(snap, W, H, aspect, bright1, col1);                 // first frame: uploads both
                int gFirst = rt.GeometryUploads - g0, tFirst = rt.TextureUploads - t0;

                var bright2 = new float[W * H]; var col2 = new Rgb24[W * H];
                int g1 = rt.GeometryUploads, t1 = rt.TextureUploads;
                snap.TextureVersion++;                                        // simulate a live texture swap
                rt.Render(snap, W, H, aspect, bright2, col2);
                int gTex = rt.GeometryUploads - g1, tTex = rt.TextureUploads - t1;

                bool identical = true;                                        // byte-identical across the two paths
                for (int i = 0; i < W * H; i++)
                    if (col1[i].R != col2[i].R || col1[i].G != col2[i].G || col1[i].B != col2[i].B || bright1[i] != bright2[i])
                    { identical = false; break; }

                int g2 = rt.GeometryUploads;
                var bright3 = new float[W * H]; var col3 = new Rgb24[W * H];
                snap.GeometryVersion++;                                       // a genuine geometry change
                rt.Render(snap, W, H, aspect, bright3, col3);
                int gGeom = rt.GeometryUploads - g2;

                reuploadOk = gFirst == 1 && tFirst == 1                       // first frame uploads geometry + pool
                          && gTex == 0 && tTex == 1                           // texture-only: pool re-uploaded, geometry NOT
                          && identical                                        // ...and the image is byte-identical
                          && gGeom == 1;                                      // geometry change re-uploads geometry
                Console.WriteLine($"  [tex-reupload] first(g={gFirst},t={tFirst}), texSwap(g={gTex},t={tTex},identical={identical}), geomChange(g={gGeom}) -> {(reuploadOk ? "ok" : "BAD")}");
            }

            bool ok = a.nonBlack > 50
                      && a.interior == 0 && a.edge == 0                                          // untextured shading exact (Δ=0)
                      && (float)b.interior / total < 0.06f && (float)b.edge / total < 0.06f      // shadow boundary thin
                      && c.nonBlack > 50 && c.edge == 0
                      && (float)texBand / total < 0.02f                                          // textured mesh interior exact but for a thin texel-edge band
                      && e.nonBlack > 50 && (float)e.edge / total < 0.02f
                      && (float)e.interior / total < 0.08f                                       // sphere seam/pole band thin
                      && pf.nonBlack > 50 && pf.edge == 0 && pf.interior == 0                    // tiled + single-face cubes exact (Δ=0)
                      && pg.nonBlack > 50 && pg.edge == 0 && pg.interior == 0                    // imported textured mesh exact (Δ=0)
                      && ph.nonBlack > 50 && (float)ph.edge / total < 0.02f
                      && (float)ph.interior / total < 0.05f                                      // bilinear band thin (NOT Δ=0 — float rounding)
                      && reuploadOk;                                                             // A3: texture swap is targeted + output unchanged
            Console.WriteLine(ok ? "GPU TEST PASSED" : "GPU TEST FAILED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("GPU TEST FAILED");
        }
    }
}
