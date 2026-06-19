namespace Nova3DVisualiser.Interfaces;

public interface ICamera
{
    Ray GetRayForUv(Vector2 uv);
}