using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;

namespace Nova3DVisualiser.Gpu;

/// <summary>
/// A <see cref="Screen"/> that fills the brightness/color buffers on the GPU instead of the CPU,
/// then reuses ConsoleScreenAsync's glyph mapping, UI overlay and diff-present unchanged. Only the
/// per-pixel raytrace step is overridden (FillBuffers).
///
/// The GPU path is the "fast path": opaque closest-hit, point/directional/circular-spot lights, hard
/// shadows. RenderScale (the CPU detail-stride control) is ignored here — the GPU always renders full
/// resolution. Transparency, area soft shadows, beam fans and shaped cones are CPU-only.
/// </summary>
public sealed class GpuScreen : ConsoleScreenAsync, IDisposable
{
    private readonly GpuRaytracer _raytracer;

    public string AcceleratorName => _raytracer.AcceleratorName;

    private GpuScreen(GpuRaytracer raytracer) => _raytracer = raytracer;

    /// <summary>
    /// Builds a GPU screen, or returns null (with a reason) if no hardware GPU is usable — the caller
    /// then falls back to the CPU <see cref="ConsoleScreenAsync"/>. Never throws.
    /// </summary>
    public static GpuScreen? TryCreate(out string status)
    {
        try
        {
            var rt = new GpuRaytracer(requireGpu: true);
            status = rt.AcceleratorName;
            return new GpuScreen(rt);
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return null;
        }
    }

    protected override void FillBuffers(Scene scene)
    {
        SceneSnapshot snap = scene.BuildSnapshot();

        // One GPU dispatch per viewport (region + its camera). Single view = one full-screen viewport with
        // the render camera, byte-identical to before. The region aspect + camera reduction come from the
        // SAME engine helpers the CPU path uses (RegionAspect / BuildCameraView), keeping them in lockstep.
        var viewports = scene.GetViewports(Width, Height);
        var gviews = new GpuViewport[viewports.Count];
        for (int k = 0; k < viewports.Count; k++)
        {
            var vp = viewports[k];
            CameraView cv = scene.BuildCameraView(vp.Camera);
            gviews[k] = new GpuViewport
            {
                X0 = vp.X0, Y0 = vp.Y0, W = vp.W, H = vp.H,
                Aspect = RegionAspect(vp.W, vp.H, _pixelAspect),
                CamPos = cv.CamPos, BasisX = cv.BasisX, BasisY = cv.BasisY, BasisZ = cv.BasisZ, Focal = cv.Focal,
            };
        }
        _raytracer.RenderViews(snap, Width, Height, gviews, BrightnessBuffer, ColorBuffer);
    }

    public void Dispose() => _raytracer.Dispose();
}
