namespace Nova3DVisualiser;

// A camera reduced to what the raytracers need per view: world position, the trig-free rotation basis
// (ray dir = BasisX*Focal + BasisY*uv.Y + BasisZ*uv.X) and the focal length. Plain data (no ILGPU), so
// the engine can build it and hand it to the GPU project per viewport. Built by Scene.BuildCameraView;
// the CPU raytrace uses the source Camera directly, the GPU uses this reduction.
public readonly struct CameraView
{
    public readonly Vector3 CamPos, BasisX, BasisY, BasisZ;
    public readonly float Focal;

    public CameraView(Vector3 camPos, Vector3 basisX, Vector3 basisY, Vector3 basisZ, float focal)
    {
        CamPos = camPos; BasisX = basisX; BasisY = basisY; BasisZ = basisZ; Focal = focal;
    }
}
