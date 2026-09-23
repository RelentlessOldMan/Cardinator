using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cardinator.Services;

/// <summary>Writes a rendered card bitmap to a PNG or JPEG file, optionally at a target DPI.</summary>
public static class CardExporter
{
    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Save(encoder, path);
    }

    public static void SaveJpg(BitmapSource bitmap, string path, int quality = 92)
    {
        // JPEG has no alpha; flatten onto white so transparent card corners don't turn black.
        var opaque = Flatten(bitmap, Colors.White);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        Save(encoder, path);
    }

    /// <summary>Saves to PNG or JPEG based on the file extension (.jpg/.jpeg = JPEG, else PNG).</summary>
    public static void Save(BitmapSource bitmap, string path, int jpgQuality = 92)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg") SaveJpg(bitmap, path, jpgQuality);
        else SavePng(bitmap, path);
    }

    /// <summary>Returns a copy of the bitmap tagged with the given DPI (metadata for print tools).</summary>
    public static BitmapSource StampDpi(BitmapSource src, double dpi)
    {
        if (Math.Abs(src.DpiX - dpi) < 0.01 && Math.Abs(src.DpiY - dpi) < 0.01) return src;
        var px = ToBgra32(src, out int stride);
        var outBmp = BitmapSource.Create(src.PixelWidth, src.PixelHeight, dpi, dpi,
            PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    private static void Save(BitmapEncoder encoder, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static BitmapSource Flatten(BitmapSource src, Color bg)
    {
        var px = ToBgra32(src, out int stride);
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = px[i + 3] / 255.0;
            px[i] = (byte)(px[i] * a + bg.B * (1 - a));
            px[i + 1] = (byte)(px[i + 1] * a + bg.G * (1 - a));
            px[i + 2] = (byte)(px[i + 2] * a + bg.R * (1 - a));
            px[i + 3] = 255;
        }
        var outBmp = BitmapSource.Create(src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY,
            PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    internal static byte[] ToBgra32(BitmapSource src, out int stride)
    {
        var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        stride = conv.PixelWidth * 4;
        var px = new byte[conv.PixelHeight * stride];
        conv.CopyPixels(px, stride, 0);
        return px;
    }
}
