using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Covers the print/export options: bleed padding, DPI stamping, and PNG/JPEG selection.</summary>
public class ExportTests
{
    private static BitmapSource Solid(int w, int h, byte alpha = 255)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 80; px[i + 2] = 40; px[i + 3] = alpha; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void AddBleed_GrowsByTwiceTheBleedOnEachAxis()
    {
        var padded = PrintExporter.AddBleed(Solid(100, 140), 20);
        Assert.Equal(140, padded.PixelWidth);   // 100 + 2*20
        Assert.Equal(180, padded.PixelHeight);  // 140 + 2*20
    }

    [Fact]
    public void AddBleed_Zero_ReturnsSameBitmap()
    {
        var src = Solid(50, 50);
        Assert.Same(src, PrintExporter.AddBleed(src, 0));
    }

    [Fact]
    public void StampDpi_SetsRequestedDpi()
    {
        var stamped = CardExporter.StampDpi(Solid(20, 20), 300);
        Assert.Equal(300, stamped.DpiX, 1);
        Assert.Equal(300, stamped.DpiY, 1);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    public void Save_WritesNonEmptyFile_ForExtension(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"exp-{Guid.NewGuid():N}{ext}");
        try
        {
            CardExporter.Save(Solid(60, 84, alpha: 128), path);   // alpha exercises JPEG flatten
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally { File.Delete(path); }
    }
}
