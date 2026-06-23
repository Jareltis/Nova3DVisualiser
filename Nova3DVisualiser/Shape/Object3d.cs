using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.Shape;

public enum AnchorMode { Bottom, Center, Origin }

public class Object3d : GameObject, IDisplays
{
    private readonly List<Triangle> _faces;

    private readonly Vector3[] _localVertices;
    private readonly Vector3[] _worldVertices;

    private float _boundingRadius = 0;

    public Vector3 Size { get; private set; }   // bbox dimensions (w,h,d)
    private Vector3 _localCenter;                // bbox center in local space
    private Vector3 _worldCenter;                // bbox center in world space (per frame)
    private float _worldRadius;                  // bounding radius in world space (per frame)

    public Vector3 WorldMin { get; private set; }   // world-space AABB (per frame) — used as a collider
    public Vector3 WorldMax { get; private set; }

    public float Scale = 1f;
    public float RotateSpeed = 0f;

    public static bool UseBvh = true;
    private const int BvhTriangleThreshold = 64;
    private BvhNode? _bvh;

    public int FaceCount => _faces.Count;
    public bool HasBvh => _bvh != null;

    // GPU-snapshot accessors: the per-frame world vertices, the face list, and the combined rotation.
    // Read-only views — the engine still owns the geometry.
    public Vector3[] WorldVertices => _worldVertices;
    public IReadOnlyList<Triangle> Faces => _faces;
    public Vector3 TotalRotation => LocalRotate + GlobalRotate;
    public Vector3[] LocalVertices => _localVertices;

    // ---- GPU two-level BVH: the mesh's LOCAL-space triangles + a flattened, stackless BVH, built
    // ONCE and cached (geometry is static in local space). The GPU snapshot uploads these; the kernel
    // traverses them in the object's local space. Built over ALL faces regardless of the CPU BVH
    // threshold (a BVH only changes speed, not the closest-hit result, so parity is preserved). ----
    private SnapFace[]? _gpuFaces;
    private SnapBvhNode[]? _gpuNodes;
    private int[]? _gpuTriIdx;

    public SnapFace[] GpuFaces { get { EnsureGpuBvh(); return _gpuFaces!; } }
    public SnapBvhNode[] GpuNodes { get { EnsureGpuBvh(); return _gpuNodes!; } }
    public int[] GpuTriIdx { get { EnsureGpuBvh(); return _gpuTriIdx!; } }

    private void EnsureGpuBvh()
    {
        if (_gpuFaces != null) return;

        _gpuFaces = new SnapFace[_faces.Count];
        for (int i = 0; i < _faces.Count; i++)
        {
            Triangle t = _faces[i];
            _gpuFaces[i] = new SnapFace { I0 = t.I0, I1 = t.I1, I2 = t.I2, N0 = t.N0, N1 = t.N1, N2 = t.N2 };
        }

        var indices = new List<int>(_faces.Count);
        for (int i = 0; i < _faces.Count; i++) indices.Add(i);
        BvhNode root = BvhNode.Build(indices, _faces, _localVertices, 0);

        var nodes = new List<SnapBvhNode>();
        var triIdx = new List<int>();
        FlattenBvh(root, -1, nodes, triIdx);
        _gpuNodes = nodes.ToArray();
        _gpuTriIdx = triIdx.ToArray();
    }

    // Pre-order DFS flatten with exit links: left child is always the next node (index+1), so a node
    // only needs its miss/after-leaf jump (Exit). Left child's Exit = the right child's index; the
    // right child's Exit = this node's Exit.
    private static int FlattenBvh(BvhNode n, int exit, List<SnapBvhNode> nodes, List<int> triIdx)
    {
        int idx = nodes.Count;
        nodes.Add(default);   // reserve this slot; children are appended after it

        if (n.Tris != null)   // leaf
        {
            int start = triIdx.Count;
            foreach (int ti in n.Tris) triIdx.Add(ti);
            nodes[idx] = new SnapBvhNode { Min = n.Min, Max = n.Max, Exit = exit, TriStart = start, TriCount = n.Tris.Length };
            return idx;
        }

        nodes[idx] = new SnapBvhNode { Min = n.Min, Max = n.Max, Exit = exit, TriStart = 0, TriCount = 0 };
        int leftSize = CountNodes(n.Left!);
        int rightIndex = idx + 1 + leftSize;
        FlattenBvh(n.Left!, rightIndex, nodes, triIdx);   // left's exit = where the right subtree starts
        FlattenBvh(n.Right!, exit, nodes, triIdx);        // right's exit = this node's exit
        return idx;
    }

    private static int CountNodes(BvhNode n) => n.Tris != null ? 1 : 1 + CountNodes(n.Left!) + CountNodes(n.Right!);

    public Object3d(Vector3[] vertex, Vector3[] normals, FacingInfo[] facingInfos) : base(Vector3.Zero, Vector3.Zero)
    {
        _localVertices = vertex;
        _worldVertices = new Vector3[vertex.Length];

        Vector3 min = _localVertices[0];
        Vector3 max = _localVertices[0];
        foreach (var v in _localVertices)
        {
            if (v.X < min.X) min.X = v.X; if (v.X > max.X) max.X = v.X;
            if (v.Y < min.Y) min.Y = v.Y; if (v.Y > max.Y) max.Y = v.Y;
            if (v.Z < min.Z) min.Z = v.Z; if (v.Z > max.Z) max.Z = v.Z;
        }
        Size = max - min;
        _localCenter = (min + max) * 0.5f;
        foreach (var v in _localVertices)
        {
            float dist = (v - _localCenter).Length();
            if (dist > _boundingRadius) _boundingRadius = dist;
        }

        _faces = new List<Triangle>();
        foreach (var facingInfo in facingInfos)
        {
            _faces.Add(new Triangle(
                new int[] { facingInfo.Vertex1 - 1, facingInfo.Vertex2 - 1, facingInfo.Vertex3 - 1 },
                normals[facingInfo.Normal1 - 1],
                normals[facingInfo.Normal2 - 1],
                normals[facingInfo.Normal3 - 1]
            ));
        }

        UpdateGeometry();
    }

    public void ApplyAnchor(AnchorMode anchor)
    {
        if (anchor == AnchorMode.Origin) return;   // keep the raw OBJ origin

        // recompute bbox (vertices are still untransformed)
        Vector3 min = _localVertices[0];
        Vector3 max = _localVertices[0];
        foreach (var v in _localVertices)
        {
            if (v.X < min.X) min.X = v.X; if (v.X > max.X) max.X = v.X;
            if (v.Y < min.Y) min.Y = v.Y; if (v.Y > max.Y) max.Y = v.Y;
            if (v.Z < min.Z) min.Z = v.Z; if (v.Z > max.Z) max.Z = v.Z;
        }

        Vector3 shift = anchor == AnchorMode.Center
            ? new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f)  // center -> origin
            : new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);                  // Bottom: center X,Z; bottom Y -> origin

        for (int i = 0; i < _localVertices.Length; i++)
            _localVertices[i] = _localVertices[i] - shift;

        _localCenter = _localCenter - shift;   // center moves with the mesh; _boundingRadius is unchanged (all distances preserved)
    }

    public void BuildAcceleration()
    {
        if (_faces.Count <= BvhTriangleThreshold) { _bvh = null; return; }

        var indices = new List<int>(_faces.Count);
        for (int i = 0; i < _faces.Count; i++) indices.Add(i);
        _bvh = BvhNode.Build(indices, _faces, _localVertices, 0);
    }

    public void UpdateGeometry()
    {
        Vector3 totalRot = LocalRotate + GlobalRotate;

        Parallel.For(0, _localVertices.Length, i =>
        {
            _worldVertices[i] = (_localVertices[i] * Scale).Rotate(totalRot) + Position;
        });

        // World AABB: sequential min/max over the just-filled world verts (Vector3 has no Min/Max).
        Vector3 wmin = _worldVertices[0];
        Vector3 wmax = _worldVertices[0];
        for (int i = 1; i < _worldVertices.Length; i++)
        {
            Vector3 v = _worldVertices[i];
            wmin.X = MathF.Min(wmin.X, v.X); wmin.Y = MathF.Min(wmin.Y, v.Y); wmin.Z = MathF.Min(wmin.Z, v.Z);
            wmax.X = MathF.Max(wmax.X, v.X); wmax.Y = MathF.Max(wmax.Y, v.Y); wmax.Z = MathF.Max(wmax.Z, v.Z);
        }
        WorldMin = wmin;
        WorldMax = wmax;

        _worldCenter = (_localCenter * Scale).Rotate(totalRot) + Position;
        _worldRadius = _boundingRadius * Scale;
    }

    public bool BoundingSphereMissed(Vector3 rayStart, Vector3 unitDir)
    {
        Vector3 L = _worldCenter - rayStart;
        float tca = L * unitDir;
        float d2 = L * L - tca * tca;
        return d2 > _worldRadius * _worldRadius;
    }

    public RenderData GetRenderData(Ray ray)
    {
        if (BoundingSphereMissed(ray.RayStart, ray.RayDirection)) return RenderData.NoRender;

        Vector3 totalRot = LocalRotate + GlobalRotate;

        if (_bvh != null && UseBvh)
        {
            // Transform the world ray into the object's local space (do NOT normalize the
            // local direction): the ray parameter t is then identical in both spaces.
            float invScale = 1f / Scale;
            Vector3 localStart = (ray.RayStart - Position).RotateInverse(totalRot) * invScale;
            Vector3 localDir = ray.RayDirection.RotateInverse(totalRot) * invScale;
            Ray localRay = new Ray(localStart, localDir);

            RenderData hit = _bvh.Traverse(localRay, _localVertices, _faces, totalRot);
            if (hit.Intersection > -1)
            {
                // t is the world-space distance parameter; recompute the world hit point from it.
                Vector3 worldHit = ray.RayStart + ray.RayDirection * hit.Intersection;
                return new RenderData(hit.Intersection, hit.Normal, worldHit, this.Color);
            }
            return RenderData.NoRender;
        }

        RenderData closestData = RenderData.NoRender;

        foreach (var face in _faces)
        {
            var currentData = face.GetRenderData(ray, _worldVertices, totalRot);

            if (currentData.Intersection > -1)
            {
                if (closestData.Intersection == -1 || currentData.Intersection < closestData.Intersection)
                {
                    closestData = currentData;
                }
            }
        }
        if (closestData.Intersection > -1)
        {
            closestData.Color = this.Color;
        }
        return closestData;
    }
}