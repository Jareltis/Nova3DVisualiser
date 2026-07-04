using Nova3DVisualiser;
using CameraMode = SampleGame.Scenes.PriviewNetworkScene.CameraMode;

namespace SampleGame.Scenes;

// Camera-rig math extracted verbatim from PriviewNetworkScene (zero behaviour change): the per-mode
// body→camera offset + the derived camera position. Uses PriviewNetworkScene.CameraMode (aliased). The rig
// distances moved here with it (they were used only by CameraOffsetFor). Covered by editortest.
public static class CameraMath
{
    // 3rd-person rig: the camera sits this far BEHIND the body along the look direction and this far ABOVE.
    public const float ThirdPersonBack = 4f;
    public const float ThirdPersonUp = 1.5f;
    // 2nd-person rig: the camera sits this far IN FRONT of the body (opposite side from 3rd person) and this
    // far ABOVE, and its orientation looks BACK at the body (so you see your own front).
    public const float SecondPersonFront = 4f;
    public const float SecondPersonUp = 1.5f;

    // ---- Camera RIG: the camera derives from the body via a per-mode offset (pure math so it is unit-
    // testable). FIRSTPERSON: the camera sits AT the body (its head/eye anchor — the body is at eye level,
    // matching today's feel and the remote-avatar convention). THIRDPERSON: behind + above the body along
    // the yaw look direction. SECONDPERSON: in FRONT + above (opposite side from 3rd person); its LOOK is
    // set separately to face back at the body. Placed-camera modes have no body offset. ----
    public static Vector3 CameraOffsetFor(Vector3 lookRotate, CameraMode mode)
    {
        if (mode == CameraMode.ThirdPerson)
        {
            Vector3 forward = new Vector3(1, 0, 0).Rotate(new Vector3(0, lookRotate.Y, 0));   // yaw-only look
            return forward * (-ThirdPersonBack) + new Vector3(0, ThirdPersonUp, 0);           // behind + above
        }
        if (mode == CameraMode.SecondPerson)
        {
            Vector3 forward = new Vector3(1, 0, 0).Rotate(new Vector3(0, lookRotate.Y, 0));   // yaw-only look
            return forward * (SecondPersonFront) + new Vector3(0, SecondPersonUp, 0);         // in FRONT + above
        }
        return Vector3.Zero;   // first-person (and placed-camera modes) — camera at the body's eye
    }

    // The camera position for a given body position + look rotation + mode. body + the mode offset.
    public static Vector3 CameraPositionFor(Vector3 bodyPos, Vector3 lookRotate, CameraMode mode)
        => bodyPos + CameraOffsetFor(lookRotate, mode);
}
