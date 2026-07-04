using Nova3DVisualiser.Implementation;

namespace Nova3DVisualiser.AbstractClass;

// A screen REGION [X0,Y0,W,H] (pixels within the full Width×Height buffer) rendered from Camera. A Scene
// returns one Viewport per view each frame (GetViewports); the default SINGLE view is the whole screen
// with the render camera, and a split-screen scene returns several regions across several cameras. The
// region's pixels map to the camera's FULL view (region-relative uv using the region's own aspect).
public readonly struct Viewport
{
    public readonly int X0, Y0, W, H;
    public readonly Camera Camera;

    public Viewport(int x0, int y0, int w, int h, Camera camera)
    {
        X0 = x0; Y0 = y0; W = w; H = h; Camera = camera;
    }
}
