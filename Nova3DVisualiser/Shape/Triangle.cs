using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.Shape;

public class Triangle(int[] indices, Vector3 n0, Vector3 n1, Vector3 n2, Vector2 uv0 = default, Vector2 uv1 = default, Vector2 uv2 = default, int group = 0)
{
    private readonly int _i0 = indices[0];
    private readonly int _i1 = indices[1];
    private readonly int _i2 = indices[2];

    public int I0 => _i0;
    public int I1 => _i1;
    public int I2 => _i2;
    private readonly Vector3 _n0 = n0;
    private readonly Vector3 _n1 = n1;
    private readonly Vector3 _n2 = n2;

    // Per-corner texture coordinates, parallel to the vertex normals. Zero for untextured geometry
    // (interpolates to Zero, which the object ignores when it has no Texture).
    private readonly Vector2 _uv0 = uv0;
    private readonly Vector2 _uv1 = uv1;
    private readonly Vector2 _uv2 = uv2;

    // Face-group id (which "side" of the object this triangle belongs to) — used by per-object
    // texture-face selection. 0 = the single "whole" group (default); the cube tags its 6 sides 0..5.
    private readonly int _group = group;

    // Local-space vertex normals (exposed so the GPU snapshot can bake world-space normals).
    public Vector3 N0 => _n0;
    public Vector3 N1 => _n1;
    public Vector3 N2 => _n2;

    public Vector2 Uv0 => _uv0;
    public Vector2 Uv1 => _uv1;
    public Vector2 Uv2 => _uv2;

    public int Group => _group;

    public RenderData GetRenderData(Ray ray, Vector3[] worldVertices, Vector3 rotationForNormal)
    {
        Vector3 v0 = worldVertices[_i0];
        Vector3 v1 = worldVertices[_i1];
        Vector3 v2 = worldVertices[_i2];

        Vector3 E1 = v1 - v0;
        Vector3 E2 = v2 - v0;

        Vector3 geometricNormal = Vector3.Cross(E1, E2);
        if (geometricNormal * ray.RayDirection > 0)
            return RenderData.NoRender;

        Vector3 P = Vector3.Cross(ray.RayDirection, E2);
        float det = E1 * P;
        if (Math.Abs(det) < 1e-6) return RenderData.NoRender;

        float invDet = 1.0f / det;
        Vector3 T = ray.RayStart - v0;
        float u = T * P * invDet;
        if (u < 0 || u > 1) return RenderData.NoRender;

        Vector3 Q = Vector3.Cross(T, E1);
        float v = ray.RayDirection * Q * invDet;
        if (v < 0 || u + v > 1) return RenderData.NoRender;

        float intersection = E2 * Q * invDet;
        if (intersection < 0) return RenderData.NoRender;

        // Smooth shading: barycentric interpolation of the three vertex normals.
        float w0 = 1f - u - v;
        Vector3 interpolated = _n0 * w0 + _n1 * u + _n2 * v;
        Vector3 finalNormal = interpolated.Rotate(rotationForNormal).Norm();

        // Texture coordinate: the SAME barycentric blend as the normal, so a textured surface samples
        // exactly where the smooth normal points. Zero for untextured triangles (ignored downstream).
        Vector2 uv = _uv0 * w0 + _uv1 * u + _uv2 * v;

        Vector3 intersectionPoint = ray.GetIntersectionPoint(intersection);
        return new RenderData(intersection, finalNormal, intersectionPoint, default, uv, _group);
    }
}