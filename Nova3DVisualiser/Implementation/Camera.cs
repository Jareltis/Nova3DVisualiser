using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Interfaces;

namespace Nova3DVisualiser.Implementation;

public class Camera(Vector3 position, Vector3 localRotate) : GameObject(position, localRotate), ICamera
{
    private Vector3 _rayStartPosition = Vector3.Zero;

    // The original look used a hard-coded forward distance of 3 in the ray (3, uv.Y, uv.X),
    // giving a full horizontal FOV of 2*atan(1/3) across uv in [-1,1]. Fov (degrees) now feeds
    // that uv->direction spread; DefaultFov reproduces the original image exactly (focal == 3).
    public static readonly float DefaultFov = 2f * MathF.Atan(1f / 3f) * 180f / MathF.PI;  // ~= 36.87 deg

    public float Fov = DefaultFov;

    public Ray GetRayForUv(Vector2 uv)
    {
        // Focal length from FOV: a narrower FOV gives a longer focal length (zoomed in).
        // DefaultFov -> focal 3, matching the original hard-coded value.
        float focal = 1f / MathF.Tan(Fov * (MathF.PI / 360f));

        Vector3 rayDirection = new Vector3(focal, uv.Y, uv.X);

        // Roll about the forward (X) axis first — it spins the image but leaves the look
        // direction unchanged — then pitch (Z) and yaw (Y), matching the original order.
        Vector3 rayWithRoll = rayDirection.Rotate(new Vector3(LocalRotate.X, 0, 0));

        Vector3 rayWithPitch = rayWithRoll.Rotate(new Vector3(0, 0, LocalRotate.Z));

        Vector3 finalRayDirection = rayWithPitch.Rotate(new Vector3(0, LocalRotate.Y, 0)).Norm();

        Vector3 finalRayStart = Position;

        return new Ray(finalRayStart, finalRayDirection);
    }
}