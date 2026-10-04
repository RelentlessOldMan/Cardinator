using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Composites a custom frame's frame.png live from its RAW source image + fit knobs
/// (zoom / pan / border / corner-round), punching the art window transparent. This keeps the
/// framing editable in the app instead of baking it into a flat image.
/// </summary>
public static class CustomFrameComposer
{
    private const int SS = 2;   // logical -> device

    public static bool HasSource(TemplateSpec spec) =>
        spec.CustomFrame && !string.IsNullOrWhiteSpace(spec.FrameSrc);

    /// <summary>Composes the device-resolution (2x) frame bitmap from a raw source image.</summary>
    public static BitmapSource Compose(TemplateSpec spec, BitmapSource raw)
    {
        raw = ApplyTranslucentKey(spec, raw);   // convert any chroma-keyed region into a translucent one

        int W = Math.Max(1, (int)Math.Round((double)spec.CanvasWidth * SS));
        int H = Math.Max(1, (int)Math.Round((double)spec.CanvasHeight * SS));
        double cornerR = Math.Max(0, spec.CornerRadius) * SS;
        double holeR = Math.Max(0, spec.PanelRadius) * SS;

        // Cover-fit the source, times zoom, then pan. Zoom below 1.0 insets the frame (leaving a margin
        // around it); the margin is filled with FrameBorderColor when set, else left transparent.
        double zoom = spec.FrameZoom <= 0 ? 1.0 : spec.FrameZoom;
        double scale = Math.Max((double)W / raw.PixelWidth, (double)H / raw.PixelHeight) * zoom;
        double rw = raw.PixelWidth * scale, rh = raw.PixelHeight * scale;
        double rx = (W - rw) / 2 + spec.FrameOffsetX * SS;
        double ry = (H - rh) / 2 + spec.FrameOffsetY * SS;

        // The frame is an overlay: we KEEP the source image's own transparency (its window), round the
        // card corners, and add a border. We do NOT punch a hole at the art region — the art is drawn
        // behind the frame and shows only through the source's transparent window, so the art box can be
        // larger than the opening and the opaque frame masks the overflow.
        var card = new RectangleGeometry(new Rect(0, 0, W, H), cornerR, cornerR);
        card.Freeze();
        _ = holeR;

        var fit = new Rect(rx, ry, rw, rh);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(card);                                   // round the outer corners; source alpha kept
            dc.DrawImage(raw, fit);                              // zoom only scales the art — no invented edge
            dc.Pop();

            double bt = Math.Max(0, spec.FrameBorder) * SS;
            if (bt > 0)
            {
                var inner = new RectangleGeometry(
                    new Rect(bt, bt, Math.Max(0, W - 2 * bt), Math.Max(0, H - 2 * bt)),
                    Math.Max(0, cornerR - bt), Math.Max(0, cornerR - bt));
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, card, inner);
                ring.Freeze();
                var brush = new SolidColorBrush(TemplateSpec.ParseColor(spec.FrameBorderColor));
                brush.Freeze();
                dc.DrawGeometry(brush, null, ring);
            }
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Converts every pixel painted with the spec's chroma key into a translucent pixel: the art
    /// behind it shows through at <c>TranslucentAmount</c> (1 = fully clear, 0 = opaque) over the backing
    /// color. Baked into the frame art, this gives a second see-through tier beyond the fully-clear window.</summary>
    public static BitmapSource ApplyTranslucentKey(TemplateSpec spec, BitmapSource raw)
    {
        if (!spec.TranslucentEnabled) return raw;

        var key = TemplateSpec.ParseColor(spec.TranslucentKey);
        var back = TemplateSpec.ParseColor(spec.TranslucentBacking);
        byte outA = (byte)Math.Round(Math.Clamp(1.0 - spec.TranslucentAmount, 0, 1) * 255);
        const int tol = 70;   // channel tolerance so slightly-dithered marker fills still match

        var conv = new FormatConvertedBitmap(raw, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        conv.CopyPixels(px, stride, 0);

        for (int i = 0; i < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a > 100 &&
                Math.Abs(r - key.R) <= tol && Math.Abs(g - key.G) <= tol && Math.Abs(b - key.B) <= tol)
            {
                px[i] = back.B; px[i + 1] = back.G; px[i + 2] = back.R; px[i + 3] = outA;
            }
        }

        var outBmp = BitmapSource.Create(w, h, conv.DpiX, conv.DpiY, PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    /// <summary>Loads the raw source, composes, and writes frame.png to <paramref name="outPath"/>.</summary>
    public static void Generate(TemplateSpec spec, string srcPath, string outPath)
    {
        var raw = LoadBitmap(srcPath);
        var img = Compose(spec, raw);
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outPath);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(fs);
    }

    /// <summary>A cache key covering the source file + every fit knob, so frame.png regenerates on change.</summary>
    public static string FitHash(TemplateSpec spec, string srcPath)
    {
        long stamp = 0;
        try { var fi = new FileInfo(srcPath); stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length; } catch { }
        var a = spec.ArtWindow;
        return string.Join("|", stamp, spec.FrameZoom, spec.FrameOffsetX, spec.FrameOffsetY,
            spec.FrameBorder, spec.FrameBorderColor, spec.CornerRadius, spec.PanelRadius,
            spec.CanvasWidth, spec.CanvasHeight, a.X, a.Y, a.W, a.H,
            spec.TranslucentEnabled, spec.TranslucentKey, spec.TranslucentAmount, spec.TranslucentBacking);
    }

    public static BitmapSource LoadBitmap(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bi.UriSource = new Uri(Path.GetFullPath(path));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
