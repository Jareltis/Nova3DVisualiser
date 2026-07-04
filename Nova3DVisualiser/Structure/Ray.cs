namespace Nova3DVisualiser;

public struct Ray(Vector3 rayStart, Vector3 rayDirection)
{
    public Vector3 RayStart = rayStart;
    public Vector3 RayDirection = rayDirection;

    // Per-pixel ray-cone spread (world-space footprint growth per unit ray distance) used ONLY for texture
    // mip-level selection. 0 (the default for every non-primary ray — shadow rays, tests) means "no
    // minification info", which selects the base mip level, so it never perturbs nearest/bilinear content.
    public float Cone = 0f;


    public Vector3 GetIntersectionPoint(float intersection)
    {
        return RayStart + RayDirection * intersection;
    }
}