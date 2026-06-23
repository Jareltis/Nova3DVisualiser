namespace Nova3DVisualiser;

// A flat, value-only snapshot of the scene for the GPU renderer (the separate Nova3DVisualiser.Gpu
// project). Everything here is a plain blittable struct of floats/ints — NO third-party types — so the
// engine stays dependency-free while feeding ILGPU device buffers directly. The CPU renderer never
// touches this.
//
// GPU acceleration mirrors the CPU two-level scheme: each mesh keeps its triangles + a BVH in LOCAL
// space (static — built once, uploaded once), and per frame only a small per-object record travels
// (transform + world AABB + color). The kernel transforms the ray into each object's local space and
// traverses that object's BVH, exactly like Object3d.GetRenderData. Spheres stay analytic (no BVH).
//
// The camera is reduced to an origin + three basis vectors so the primary-ray kernel needs no trig:
// dir = BasisX*Focal + BasisY*uv.Y + BasisZ*uv.X, normalized.

// One mesh triangle in LOCAL space: vertex indices into the object's local-vertex block + the three
// local vertex normals (rotated to world by the object basis in the kernel).
public struct SnapFace
{
    public int I0, I1, I2;
    public Vector3 N0, N1, N2;
}

// One flattened, STACKLESS BVH node (local space). Traversal carries a single node index: on an AABB
// hit an internal node advances to its left child (the next node in pre-order, index+1) and a leaf
// tests its faces; on a miss — or after a leaf — it jumps to Exit (-1 ends traversal). Indices are
// object-relative (add the object's NodeBase / TriIdxBase).
public struct SnapBvhNode
{
    public Vector3 Min, Max;
    public int Exit;        // next node on miss / after a leaf (-1 = done)
    public int TriStart;    // leaf: first entry in the object's triangle-index list
    public int TriCount;    // leaf: face count (>0 ⇒ leaf; 0 ⇒ internal)
}

// One scene mesh instance. Geometry lives in the global arrays at the *Base offsets (static); the
// transform / AABB / color are refreshed every frame. ColX/Y/Z are the object's rotation basis
// (Rotate of the unit axes by totalRot): worldNormal = ColX*n.x + ColY*n.y + ColZ*n.z, and the
// inverse (ray → local) is the transpose, so the kernel needs no trig.
public struct SnapObject
{
    public Vector3 Position;
    public Vector3 ColX, ColY, ColZ;   // forward rotation basis (columns of Rotate(totalRot))
    public float InvScale;             // 1 / Scale
    public Vector3 WorldMin, WorldMax; // per-frame world AABB — a cheap pre-cull before transforming
    public int VertBase, FaceBase, NodeBase, TriIdxBase;
    public float R, G, B, A;
    public int CastsShadow;
}

public struct SnapSphere
{
    public Vector3 Center;
    public float Radius;
    public float R, G, B, A;
    public int CastsShadow;
}

public struct SnapLight
{
    public int Kind;               // 0 point, 1 directional, 2 spot, 3 area
    public Vector3 Position;
    public Vector3 Rgb;            // emission 0..1
    public float Power;
    public Vector3 Direction;      // normalized aim (directional/spot/area)
    public float ConeCosOuter;     // spot: cos(coneAngle)       — outside => 0
    public float ConeCosInner;     // spot: cos(0.8*coneAngle)   — smoothstep edge between the two
    public float ConeTan;          // spot: tan(coneAngle)       — footprint radius for shaped cones
    public int BeamCount;          // spot: cones fanned about the aim (>=1)
    public int ConeShape;          // spot cross-section: 0 circle, 1 square, 2 triangle
    public int AreaShape;          // area emitter shape: 0 circle, 1 square, 2 triangle
    public float AreaSize;         // area: square half-extent / sample radius
    public float ColorInfluence;   // how strongly the light color reads over surface albedo (0..1)
}

// One frame's scene for the GPU. The geometry arrays (LocalVerts/Faces/Nodes/TriIdx) are static and
// only change when GeometryVersion changes (objects added/removed); Objects/Spheres/Lights + camera
// refresh every frame.
public class SceneSnapshot
{
    public int GeometryVersion;        // bumped when the static mesh set changes (gates GPU re-upload)
    public Vector3[] LocalVerts = Array.Empty<Vector3>();
    public SnapFace[] Faces = Array.Empty<SnapFace>();
    public SnapBvhNode[] Nodes = Array.Empty<SnapBvhNode>();
    public int[] TriIdx = Array.Empty<int>();

    public SnapObject[] Objects = Array.Empty<SnapObject>();
    public SnapSphere[] Spheres = Array.Empty<SnapSphere>();
    public SnapLight[] Lights = Array.Empty<SnapLight>();

    public Vector3 CamPos;
    public Vector3 BasisX, BasisY, BasisZ;   // ray dir = BasisX*Focal + BasisY*uv.Y + BasisZ*uv.X
    public float Focal;

    public float Ambient;
    public float Exposure;
    public int EnableShadows;                // 1/0
}
