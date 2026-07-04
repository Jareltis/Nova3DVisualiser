using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nova3DVisualiser.AbstractClass;
public abstract class Screen
{
    // Dimensions + the frame buffers are no longer fixed for the screen's lifetime: a terminal resize
    // (window stretch or Ctrl+scroll font zoom changes the cell grid) re-sizes them via ResizeBuffers.
    public int Width { get; protected set; }
    public int Height { get; protected set; }

    protected float[] BrightnessBuffer;
    protected Rgb24[] ColorBuffer;

    protected Screen(int width, int height)
    {
        Width = width;
        Height = height;
        BrightnessBuffer = new float[width * height];
        ColorBuffer = new Rgb24[width * height];
    }

    // Re-sizes the screen dimensions + the shared frame buffers to a new console size (already clamped to
    // a positive minimum by the caller). Used by the per-frame resize check so both the CPU and GPU render
    // paths fill correctly-sized buffers after a stretch/zoom.
    protected void ResizeBuffers(int width, int height)
    {
        Width = width;
        Height = height;
        BrightnessBuffer = new float[width * height];
        ColorBuffer = new Rgb24[width * height];
    }

    public virtual void RenderFrame(Scene scene)
    {
        Parallel.For(0, Height, j =>
        {
            for (int i = 0; i < Width; i++)
            {
                Vector2 uv = CalculateUV(i, j);

                var pixelData = scene.GetPixelData(uv);

                int index = j * Width + i;
                BrightnessBuffer[index] = pixelData.Brightness;
                ColorBuffer[index] = pixelData.Color;
            }
        });

        Present();
    }

    protected abstract Vector2 CalculateUV(int i, int j);
    protected abstract void Present();
    public abstract void PrintText(string text, Vector2Int position);
}