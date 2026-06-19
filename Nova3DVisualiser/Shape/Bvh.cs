using System;
using System.Collections.Generic;
using Nova3DVisualiser.Implementation;

namespace Nova3DVisualiser.Shape;

// Binary BVH over an Object3d's LOCAL triangles. Built once; traversed with a ray
// already transformed into the object's local space.
public class BvhNode
{
    public Vector3 Min;
    public Vector3 Max;
    public BvhNode? Left;
    public BvhNode? Right;
    public int[]? Tris;   // non-null only for leaves; indices into the object's _faces

    public static BvhNode Build(List<int> triIndices, List<Triangle> faces, Vector3[] verts, int depth)
    {
        var node = new BvhNode();

        // Node AABB = union of every triangle's 3 local vertices.
        Vector3 min = new Vector3(float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity);
        foreach (int ti in triIndices)
        {
            Triangle t = faces[ti];
            Expand(ref min, ref max, verts[t.I0]);
            Expand(ref min, ref max, verts[t.I1]);
            Expand(ref min, ref max, verts[t.I2]);
        }
        node.Min = min;
        node.Max = max;

        if (triIndices.Count <= 4 || depth >= 24)
        {
            node.Tris = triIndices.ToArray();
            return node;
        }

        // Longest axis of the AABB.
        Vector3 extent = max - min;
        int axis = 0;
        float best = extent.X;
        if (extent.Y > best) { axis = 1; best = extent.Y; }
        if (extent.Z > best) { axis = 2; }

        // Sort by centroid along that axis, split at the median.
        triIndices.Sort((a, b) => Centroid(faces[a], verts, axis).CompareTo(Centroid(faces[b], verts, axis)));

        int mid = triIndices.Count / 2;
        var leftIdx = triIndices.GetRange(0, mid);
        var rightIdx = triIndices.GetRange(mid, triIndices.Count - mid);

        if (leftIdx.Count == 0 || rightIdx.Count == 0)
        {
            // Failed to partition (e.g. all centroids identical) -> make a leaf.
            node.Tris = triIndices.ToArray();
            return node;
        }

        node.Left = Build(leftIdx, faces, verts, depth + 1);
        node.Right = Build(rightIdx, faces, verts, depth + 1);
        return node;
    }

    public RenderData Traverse(Ray localRay, Vector3[] verts, List<Triangle> faces, Vector3 normalRotation)
    {
        if (!RayHitsAabb(localRay, Min, Max)) return RenderData.NoRender;

        if (Tris != null)
        {
            RenderData closest = RenderData.NoRender;
            foreach (int ti in Tris)
            {
                RenderData d = faces[ti].GetRenderData(localRay, verts, normalRotation);
                if (d.Intersection > -1 &&
                    (closest.Intersection == -1 || d.Intersection < closest.Intersection))
                {
                    closest = d;
                }
            }
            return closest;
        }

        RenderData l = Left!.Traverse(localRay, verts, faces, normalRotation);
        RenderData r = Right!.Traverse(localRay, verts, faces, normalRotation);

        if (l.Intersection > -1 && r.Intersection > -1)
            return l.Intersection <= r.Intersection ? l : r;
        if (l.Intersection > -1) return l;
        return r;
    }

    private static void Expand(ref Vector3 min, ref Vector3 max, Vector3 v)
    {
        if (v.X < min.X) min.X = v.X; if (v.X > max.X) max.X = v.X;
        if (v.Y < min.Y) min.Y = v.Y; if (v.Y > max.Y) max.Y = v.Y;
        if (v.Z < min.Z) min.Z = v.Z; if (v.Z > max.Z) max.Z = v.Z;
    }

    private static float Centroid(Triangle t, Vector3[] verts, int axis)
    {
        Vector3 c = (verts[t.I0] + verts[t.I1] + verts[t.I2]) * (1f / 3f);
        return axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;
    }

    private static bool RayHitsAabb(Ray ray, Vector3 min, Vector3 max)
    {
        float tmin = float.NegativeInfinity;
        float tmax = float.PositiveInfinity;

        if (!SlabAxis(ray.RayStart.X, ray.RayDirection.X, min.X, max.X, ref tmin, ref tmax)) return false;
        if (!SlabAxis(ray.RayStart.Y, ray.RayDirection.Y, min.Y, max.Y, ref tmin, ref tmax)) return false;
        if (!SlabAxis(ray.RayStart.Z, ray.RayDirection.Z, min.Z, max.Z, ref tmin, ref tmax)) return false;

        // Boolean result is invariant to localDir not being unit length (Scale > 0).
        return tmax >= MathF.Max(tmin, 0f);
    }

    private static bool SlabAxis(float start, float dir, float min, float max, ref float tmin, ref float tmax)
    {
        if (MathF.Abs(dir) < 1e-9f)
        {
            // Ray parallel to this slab: only possible if the origin is already within it.
            return start >= min && start <= max;
        }

        float inv = 1f / dir;
        float t0 = (min - start) * inv;
        float t1 = (max - start) * inv;
        if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }

        if (t0 > tmin) tmin = t0;
        if (t1 < tmax) tmax = t1;
        return tmin <= tmax;
    }
}
