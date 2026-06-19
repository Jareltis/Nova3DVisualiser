using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Interfaces;

namespace Nova3DVisualiser.Implementation;

public class Camera(Vector3 position, Vector3 localRotate) : GameObject(position, localRotate), ICamera
{
    private Vector3 _rayStartPosition = Vector3.Zero;

    public Ray GetRayForUv(Vector2 uv)
    {
        Vector3 rayDirection = new Vector3(3, uv.Y, uv.X);

        Vector3 rayWithPitch = rayDirection.Rotate(new Vector3(0, 0, LocalRotate.Z));

        Vector3 finalRayDirection = rayWithPitch.Rotate(new Vector3(0, LocalRotate.Y, 0)).Norm();

        Vector3 finalRayStart = Position;

        return new Ray(finalRayStart, finalRayDirection);
    }
}