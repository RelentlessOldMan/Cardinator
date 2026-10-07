using System.Threading;
using System.Windows.Media.Imaging;

namespace Cardinator.Tests;

/// <summary>Shared helpers for render tests (STA thread + pixel checks).</summary>
internal static class TestHelpers
{
    /// <summary>Runs an action on a dedicated STA thread (required by RenderTargetBitmap).</summary>
    public static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }

    /// <summary>A real PNG (a solid w×h block of <paramref name="argb"/>, default opaque blue) as bytes.</summary>
    public static byte[] PngBytes(int w, int h, uint argb = 0xFF2850A0)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4)
        { px[i] = (byte)argb; px[i + 1] = (byte)(argb >> 8); px[i + 2] = (byte)(argb >> 16); px[i + 3] = (byte)(argb >> 24); }
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Pixels(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>True if the bitmap has any visible, non-white pixel (i.e. something was drawn).</summary>
    public static bool HasContent(BitmapSource bmp)
    {
        var pixels = Pixels(bmp);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a > 10 && (r < 200 || g < 200 || b < 200)) return true;
        }
        return false;
    }
}
