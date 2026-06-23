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
        _raytracer.Render(snap, Width, Height, _aspectRatio, BrightnessBuffer, ColorBuffer);
    }

    public void Dispose() => _raytracer.Dispose();
}
