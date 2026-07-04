using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.Globalization;
using System.Text.Json;

namespace SampleGame.Scenes;

public partial class PriviewNetworkScene
{
    // Primitive + marker geometry factories and their procedural UV/group generators.

    // A small cube marker for a placed CAMERA object: a visual-only indicator (sized like a light marker),
    // oriented by the object's rotation so a Fixed camera's aim is visible. Colour is set at the call site.
    public static Object3d CreateCameraMarker()
    {
        var m = CreateCube();
        m.Scale = LightMarkerScale;
        return m;
    }

    public static Object3d CreateCube()
    {
        var verts = new Vector3[]
        {
            new Vector3(-1f, -1f, 1f),
            new Vector3(-1f, 1f, 1f),
            new Vector3(-1f, -1f, -1f),
            new Vector3(-1f, 1f, -1f),
            new Vector3(1f, -1f, 1f),
            new Vector3(1f, 1f, 1f),
            new Vector3(1f, -1f, -1f),
            new Vector3(1f, 1f, -1f)
        };
        var normals = new Vector3[]
        {
            new Vector3(-1f, 0, 0),
            new Vector3(0, 0, -1f),
            new Vector3(1f, 0, 0),
            new Vector3(0, 0, 1f),
            new Vector3(0, -1f, 0),
            new Vector3(0, 1f, 0)
        };
        var faces = new FacingInfo[]
        {
            new FacingInfo(new int[] {2,3,1}, 1),
            new FacingInfo(new int[] {4,7,3}, 2),
            new FacingInfo(new int[] {8,5,7}, 3),
            new FacingInfo(new int[] {6,1,5}, 4),
            new FacingInfo(new int[] {7,1,3}, 5),
            new FacingInfo(new int[] {4,6,8}, 6),
            new FacingInfo(new int[] {2,4,3}, 1),
            new FacingInfo(new int[] {4,8,7}, 2),
            new FacingInfo(new int[] {8,6,5}, 3),
            new FacingInfo(new int[] {6,2,1}, 4),
            new FacingInfo(new int[] {7,5,1}, 5),
            new FacingInfo(new int[] {4,2,6}, 6)
        };
        return new Object3d(verts, normals, faces, GenerateBoxUvs(verts, normals, faces), GenerateBoxGroups(normals, faces));
    }

    // Face-group id per cube triangle = which of the 6 axis faces it belongs to, ordered to match the
    // editor's TextureFace options: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z. Two triangles share each group.
    private static int[] GenerateBoxGroups(Vector3[] normals, FacingInfo[] faces)
    {
        var g = new int[faces.Length];
        for (int f = 0; f < faces.Length; f++) g[f] = AxisGroup(normals[faces[f].Normal1 - 1]);
        return g;
    }

    // Maps an axis-aligned face normal to a group id in +X,-X,+Y,-Y,+Z,-Z order (0..5).
    private static int AxisGroup(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0f ? 0 : 1;
        if (ay >= az)             return n.Y >= 0f ? 2 : 3;
        return n.Z >= 0f ? 4 : 5;
    }

    // The texture-face options for a given object type (beyond "All"): the cube exposes its 6 sides; every
    // other shape is a single "whole" group, so it offers only "All". Used by the editor's Field.TextureFace.
    public static readonly string[] CubeFaceNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
    public static string[] TextureFaceOptions(string? type)
        => string.Equals(type?.Trim(), "cube", StringComparison.OrdinalIgnoreCase) ? CubeFaceNames : Array.Empty<string>();

    // Procedural UVs for an axis-aligned box in [-1,1]^3: each of the 6 faces maps the FULL texture
    // (0,0)–(1,1). For each face corner, drop the axis the face normal points along and remap the two
    // remaining (tangent) coordinates from [-1,1] to [0,1]. Returned in face/corner order — length
    // faces.Length*3 — matching the Object3d build loop (uvs[f*3 + 0..2] are face f's three corners).
    private static Vector2[] GenerateBoxUvs(Vector3[] verts, Vector3[] normals, FacingInfo[] faces)
    {
        var uvs = new Vector2[faces.Length * 3];
        for (int f = 0; f < faces.Length; f++)
        {
            var fi = faces[f];
            Vector3 n = normals[fi.Normal1 - 1];
            uvs[f * 3]     = BoxCornerUv(verts[fi.Vertex1 - 1], n);
            uvs[f * 3 + 1] = BoxCornerUv(verts[fi.Vertex2 - 1], n);
            uvs[f * 3 + 2] = BoxCornerUv(verts[fi.Vertex3 - 1], n);
        }
        return uvs;
    }

    private static Vector2 BoxCornerUv(Vector3 p, Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        float u, v;
        if (ax >= ay && ax >= az) { u = p.Z; v = p.Y; }        // ±X face: tangents Z (u), Y (v)
        else if (ay >= ax && ay >= az) { u = p.X; v = p.Z; }   // ±Y face: tangents X (u), Z (v)
        else { u = p.X; v = p.Y; }                             // ±Z face: tangents X (u), Y (v)
        return new Vector2((u + 1f) * 0.5f, (v + 1f) * 0.5f);
    }

    // Builds a flat-shaded Object3d from local vertices + 1-based triangle index triples: one
    // normal per face, taken from the winding (Cross(b-a, c-a)) so the supplied normal always
    // agrees with the renderer's winding-based back-face test. Mirrors CreateCube's flat style.
    // uvs (optional): per-face-corner texture coordinates in face order (length tris.Count*3), threaded
    // straight into the Object3d ctor; null = untextured (Zero UVs, ignored unless a Texture is set).
    private static Object3d BuildFlat(List<Vector3> verts, List<(int a, int b, int c)> tris, Vector2[]? uvs = null)
    {
        var vertArr = verts.ToArray();
        var normals = new Vector3[tris.Count];
        var faces = new FacingInfo[tris.Count];
        for (int k = 0; k < tris.Count; k++)
        {
            var (a, b, c) = tris[k];
            Vector3 e1 = vertArr[b - 1] - vertArr[a - 1];
            Vector3 e2 = vertArr[c - 1] - vertArr[a - 1];
            normals[k] = Vector3.Cross(e1, e2).Norm();
            faces[k] = new FacingInfo(new int[] { a, b, c }, k + 1);
        }
        return new Object3d(vertArr, normals, faces, uvs);
    }

    // Unit-ish cylinder: radius 1, y in [-1, 1], origin-centred like the cube, with end caps.
    public static Object3d CreateCylinder()
    {
        const int seg = 16;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a), -1f, MathF.Sin(a))); }
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a),  1f, MathF.Sin(a))); }
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // bottom centre
        int tc = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));   // top centre

        int B(int s) => (s % seg) + 1;          // bottom ring (1-based)
        int T(int s) => seg + (s % seg) + 1;    // top ring (1-based)

        // Procedural UVs built ALONGSIDE the tris (same order), so the seam segment reaches u=1 while the
        // first reaches u=0 — no shared-vertex conflict because UVs are stored PER FACE-CORNER, not per
        // vertex. Side: u = angle fraction, v = height (bottom row v=1, top v=0). Caps: the ring wrapped
        // onto a disc in the texture, centre (0.5,0.5). Linear interp → bit-exact CPU↔GPU parity.
        Vector2 Cap(int s) { float a = MathF.Tau * s / seg; return new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)); }
        var tris = new List<(int, int, int)>();
        var uvs = new List<Vector2>();
        for (int s = 0; s < seg; s++)
        {
            int b0 = B(s), b1 = B(s + 1), t0 = T(s), t1 = T(s + 1);
            float u0 = (float)s / seg, u1 = (float)(s + 1) / seg;
            tris.Add((b0, t1, b1)); uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2(u1, 0f)); uvs.Add(new Vector2(u1, 1f));   // side quad
            tris.Add((b0, t0, t1)); uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2(u0, 0f)); uvs.Add(new Vector2(u1, 0f));
            tris.Add((tc, t1, t0)); uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s + 1)); uvs.Add(Cap(s));                     // top cap (+Y)
            tris.Add((bc, b0, b1)); uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s)); uvs.Add(Cap(s + 1));                     // bottom cap (-Y)
        }
        return BuildFlat(verts, tris, uvs.ToArray());
    }

    // Unit-ish cone: base radius 1 at y=-1, apex at (0,1,0), with a base cap.
    public static Object3d CreateCone()
    {
        const int seg = 16;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++) { float a = MathF.Tau * s / seg; verts.Add(new Vector3(MathF.Cos(a), -1f, MathF.Sin(a))); }
        int apex = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // base centre

        int B(int s) => (s % seg) + 1;

        // UVs alongside the tris (see CreateCylinder): the side triangle spans u=[s,s+1]/seg at v=1 with
        // the apex at the segment's u-midpoint at v=0; the base cap wraps the ring onto a disc (centre 0.5,0.5).
        Vector2 Cap(int s) { float a = MathF.Tau * s / seg; return new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)); }
        var tris = new List<(int, int, int)>();
        var uvs = new List<Vector2>();
        for (int s = 0; s < seg; s++)
        {
            float u0 = (float)s / seg, u1 = (float)(s + 1) / seg;
            tris.Add((B(s), apex, B(s + 1)));   // side
            uvs.Add(new Vector2(u0, 1f)); uvs.Add(new Vector2((u0 + u1) * 0.5f, 0f)); uvs.Add(new Vector2(u1, 1f));
            tris.Add((bc, B(s), B(s + 1)));     // base cap (-Y)
            uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(Cap(s)); uvs.Add(Cap(s + 1));
        }
        return BuildFlat(verts, tris, uvs.ToArray());
    }

    // Raw geometry for a spot-marker cone whose base CROSS-SECTION reflects the spot's ConeShape: apex
    // at (0,+1,0), base polygon at y=-1 sized by baseRadius, plus a base cap — same structure/winding
    // as CreateCone. Circle = 16-seg ring; Square = 4 corners; Triangle = equilateral, vertex up (+Z),
    // circumradius baseRadius (matching the engine's triangle cone). Exposed (verts+tris, 1-based) so
    // the beam-fan can bake multiple oriented copies into one mesh.
    private static void ConeMarkerGeometry(ConeShapeKind shape, float baseRadius,
                                           out List<Vector3> verts, out List<(int, int, int)> tris)
    {
        verts = new List<Vector3>();
        switch (shape)
        {
            case ConeShapeKind.Square:
                verts.Add(new Vector3(-baseRadius, -1f, -baseRadius));
                verts.Add(new Vector3( baseRadius, -1f, -baseRadius));
                verts.Add(new Vector3( baseRadius, -1f,  baseRadius));
                verts.Add(new Vector3(-baseRadius, -1f,  baseRadius));
                break;
            case ConeShapeKind.Triangle:
                for (int s = 0; s < 3; s++)   // first vertex at +Z (a = π/2), then +120°
                {
                    float a = MathF.PI / 2f + MathF.Tau * s / 3f;
                    verts.Add(new Vector3(baseRadius * MathF.Cos(a), -1f, baseRadius * MathF.Sin(a)));
                }
                break;
            default:   // Circle
                for (int s = 0; s < 16; s++)
                {
                    float a = MathF.Tau * s / 16;
                    verts.Add(new Vector3(baseRadius * MathF.Cos(a), -1f, baseRadius * MathF.Sin(a)));
                }
                break;
        }

        int n = verts.Count;
        int apex = verts.Count + 1; verts.Add(new Vector3(0,  1f, 0));
        int bc = verts.Count + 1; verts.Add(new Vector3(0, -1f, 0));   // base centre

        int B(int s) => (s % n) + 1;

        tris = new List<(int, int, int)>();
        for (int s = 0; s < n; s++)
        {
            tris.Add((B(s), apex, B(s + 1)));   // side
            tris.Add((bc, B(s), B(s + 1)));     // base cap (-Y)
        }
    }

    private static Object3d BuildConeMarker(ConeShapeKind shape, float baseRadius)
    {
        ConeMarkerGeometry(shape, baseRadius, out var v, out var t);
        return BuildFlat(v, t);
    }

    // A multi-beam spot marker: BeamCount cones fanned about the aim exactly like SpotTerm's lighting
    // fan. Each beam's orientation is BAKED into the verts (vert.Rotate uses the same X->Y->Z order as
    // Object3d's LocalRotate), so a single mesh shows beams pointing in different world directions;
    // LocalRotate stays Zero and uniform Scale commutes with the baked rotation.
    private static Object3d BuildSpotFanMarker(Vector3 dir, int beams, ConeShapeKind coneShape, float coneAngleDeg)
    {
        beams = Math.Max(2, beams);
        bool nearVertical = MathF.Abs(dir.Norm().Y) > 0.99f;
        float baseRadius = Math.Clamp(2f * MathF.Tan(coneAngleDeg * MathF.PI / 180f), 0.2f, 3f);
        ConeMarkerGeometry(coneShape, baseRadius, out var cone, out var coneTris);
        var verts = new List<Vector3>(); var tris = new List<(int, int, int)>();
        for (int k = 0; k < beams; k++)
        {
            float ang = k * (MathF.Tau / beams);
            Vector3 axis = (nearVertical ? dir.Rotate(new Vector3(ang, 0, 0)) : dir.Rotate(new Vector3(0, ang, 0))).Norm();
            Vector3 euler = DirToEuler(axis);
            int off = verts.Count;
            foreach (var v in cone) verts.Add(v.Rotate(euler));            // bake each beam's orientation
            foreach (var (a, b, c) in coneTris) tris.Add((a + off, b + off, c + off));   // 1-based, shift by vert offset
        }
        var m = BuildFlat(verts, tris);
        m.Scale = LightMarkerScale;       // uniform scale commutes with the baked rotation; LocalRotate stays Zero
        return m;
    }

    // Unit-ish pyramid: square base ±1 at y=-1, apex at (0,1,0).
    public static Object3d CreatePyramid()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f, -1f, -1f),   // 1
            new Vector3( 1f, -1f, -1f),   // 2
            new Vector3( 1f, -1f,  1f),   // 3
            new Vector3(-1f, -1f,  1f),   // 4
            new Vector3( 0f,  1f,  0f),   // 5 apex
        };
        var tris = new List<(int, int, int)>
        {
            (1, 2, 3), (1, 3, 4),                          // base (-Y)
            (1, 5, 2), (2, 5, 3), (3, 5, 4), (4, 5, 1),    // sides
        };
        return BuildFlat(verts, tris, GeneratePyramidUvs(verts, tris));
    }

    // Procedural UVs for the pyramid, matching the cube's per-face "full texture" style. The two BASE
    // tris (-Y) unwrap like the cube's -Y face: drop Y, remap X,Z from [-1,1]→[0,1]. Each triangular
    // SIDE (baseA, apex, baseB — the apex is always the middle corner) drapes the texture with the two
    // base corners at the bottom edge (0,0)/(1,0) and the apex at the top centre (0.5,1).
    private static Vector2[] GeneratePyramidUvs(List<Vector3> v, List<(int a, int b, int c)> tris)
    {
        Vector2 Xz(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Z + 1f) * 0.5f);
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            if (t < 2)   // base
            {
                uvs[t * 3] = Xz(v[a - 1]); uvs[t * 3 + 1] = Xz(v[b - 1]); uvs[t * 3 + 2] = Xz(v[c - 1]);
            }
            else         // side: (baseA, apex, baseB) → (0,0),(0.5,1),(1,0)
            {
                uvs[t * 3] = new Vector2(0f, 0f); uvs[t * 3 + 1] = new Vector2(0.5f, 1f); uvs[t * 3 + 2] = new Vector2(1f, 0f);
            }
        }
        return uvs;
    }

    // A wedge / RAMP: a triangular prism whose top is a 45° inclined plane rising in +X (the high edge is
    // the +X/+Y corner). Origin-centred, ±1 like the cube. Great for rolling a ball DOWN the slope (it
    // rolls toward -X). The sloped face's outward normal points up-and-toward -X; rotate the object to aim it.
    public static Object3d CreateRamp()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f, -1f,  1f),   // 1 A front (low)
            new Vector3( 1f, -1f,  1f),   // 2 B front (bottom of the high side)
            new Vector3( 1f,  1f,  1f),   // 3 C front (high)
            new Vector3(-1f, -1f, -1f),   // 4 A back
            new Vector3( 1f, -1f, -1f),   // 5 B back
            new Vector3( 1f,  1f, -1f),   // 6 C back
        };
        var tris = new List<(int, int, int)>
        {
            (1, 4, 5), (1, 5, 2),   // bottom (-Y)
            (1, 3, 6), (1, 6, 4),   // sloped top (the ramp surface)
            (2, 5, 6), (2, 6, 3),   // vertical back wall (+X)
            (1, 2, 3),              // front triangular cap (+Z)
            (4, 6, 5),              // back triangular cap (-Z)
        };
        return BuildFlat(verts, tris, GenerateRampUvs(verts, tris));
    }

    // Procedural UVs for the ramp, cube-style: each face maps the FULL texture (0,0)–(1,1). Axis-aligned
    // faces drop the face-normal axis and remap the two tangent coords [-1,1]→[0,1] (like the cube); the
    // sloped top (a diagonal-normal rectangle) is unwrapped along its own two edge directions — Z (depth,
    // u) and the low→high rise (x, v). One projection per tri, indexed to match CreateRamp's tris order.
    private static Vector2[] GenerateRampUvs(List<Vector3> v, List<(int a, int b, int c)> tris)
    {
        Vector2 Xz(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Z + 1f) * 0.5f);   // -Y (drop Y)
        Vector2 Zy(Vector3 p) => new Vector2((p.Z + 1f) * 0.5f, (p.Y + 1f) * 0.5f);   // +X wall (drop X)
        Vector2 Xy(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (p.Y + 1f) * 0.5f);   // ±Z caps (drop Z)
        Vector2 Slope(Vector3 p) => new Vector2((p.Z + 1f) * 0.5f, (p.X + 1f) * 0.5f); // sloped top: u=depth, v=rise
        var proj = new Func<Vector3, Vector2>[] { Xz, Xz, Slope, Slope, Zy, Zy, Xy, Xy };
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            uvs[t * 3] = proj[t](v[a - 1]); uvs[t * 3 + 1] = proj[t](v[b - 1]); uvs[t * 3 + 2] = proj[t](v[c - 1]);
        }
        return uvs;
    }

    // Builds the platform floor for the given config: a square (Size half-extent), a
    // Width x Depth rectangle, or a circular disc (diameter Size). All face +Y (up), exactly
    // like the square floor, so the renderer's winding-based cull keeps them visible from above.
    public static Object3d CreatePlatform(PlatformConfig p)
    {
        switch (p.Shape?.Trim().ToLowerInvariant())
        {
            case "rectangle": return CreatePlane(p.Width * 0.5f, p.Depth * 0.5f);
            case "circle":    return CreateDisc(p.Size * 0.5f);
            default:          return CreatePlane(p.Size, p.Size);   // "square" (and any legacy/unknown)
        }
    }

    // A flat quad on y=0 spanning [-halfX, halfX] x [-halfZ, halfZ], +Y normal (the square uses
    // halfX == halfZ == Size, preserving the original geometry exactly).
    private static Object3d CreatePlane(float halfX, float halfZ)
    {
        return new Object3d(
            new Vector3[]
            {
                new Vector3(-halfX, 0f, halfZ),
                new Vector3(halfX, 0f, halfZ),
                new Vector3(-halfX, 0f, -halfZ),
                new Vector3(halfX, 0f, -halfZ)
            },
            new Vector3[]
            {
                new Vector3(0f, 1f, 0f)
            },
            new FacingInfo[]
            {
                new FacingInfo(new int[] {2,3,1}, 1),
                new FacingInfo(new int[] {2,4,3}, 1),
            });
    }

    // A "flat picture" / billboard: a VERTICAL unit quad in the XY plane (origin-centred, ±1), standing
    // up and facing ±Z. TWO-SIDED — the two triangles are duplicated with reversed winding so the
    // winding-based back-face cull shows the image from EITHER side (mirrored on the back). Per-corner
    // UVs map the FULL texture right-side-up (u=(x+1)/2 across; v=(1-y)/2 down, so the quad's top row is
    // the image's top). Linear/barycentric interp → BIT-EXACT CPU↔GPU parity, like the cube/ramp.
    public static Object3d CreateFlatPicture()
    {
        var verts = new List<Vector3>
        {
            new Vector3(-1f,  1f, 0f),   // 1 top-left
            new Vector3( 1f,  1f, 0f),   // 2 top-right
            new Vector3(-1f, -1f, 0f),   // 3 bottom-left
            new Vector3( 1f, -1f, 0f),   // 4 bottom-right
        };
        var tris = new List<(int, int, int)>
        {
            (3, 4, 2), (3, 2, 1),   // front (+Z normal)
            (3, 2, 4), (3, 1, 2),   // back  (-Z normal — reversed winding, so it's visible from behind)
        };
        Vector2 Uv(Vector3 p) => new Vector2((p.X + 1f) * 0.5f, (1f - p.Y) * 0.5f);
        var uvs = new Vector2[tris.Count * 3];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            uvs[t * 3] = Uv(verts[a - 1]); uvs[t * 3 + 1] = Uv(verts[b - 1]); uvs[t * 3 + 2] = Uv(verts[c - 1]);
        }
        return BuildFlat(verts, tris, uvs);
    }

    // A flat disc on y=0: a triangle fan from the centre to a ring at the given radius, wound
    // (centre, ring[s+1], ring[s]) so each face's normal is +Y — same facing as the square floor.
    private static Object3d CreateDisc(float radius)
    {
        const int seg = 32;
        var verts = new List<Vector3>();
        for (int s = 0; s < seg; s++)
        {
            float a = MathF.Tau * s / seg;
            verts.Add(new Vector3(radius * MathF.Cos(a), 0f, radius * MathF.Sin(a)));
        }
        int centre = verts.Count + 1; verts.Add(new Vector3(0f, 0f, 0f));

        int R(int s) => (s % seg) + 1;   // ring vertex (1-based)
        var tris = new List<(int, int, int)>();
        for (int s = 0; s < seg; s++)
            tris.Add((centre, R(s + 1), R(s)));   // wound so Cross(b-a,c-a) points +Y

        return BuildFlat(verts, tris);
    }
}
