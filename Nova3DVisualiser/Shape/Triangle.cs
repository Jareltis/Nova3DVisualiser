using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces.modifier;

namespace Nova3DVisualiser.Shape;

public class Triangle(int[] indices, Vector3 n0, Vector3 n1, Vector3 n2)
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

        Vector3 intersectionPoint = ray.GetIntersectionPoint(intersection);
        return new RenderData(intersection, finalNormal, intersectionPoint);
    }
}