using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cardinator.Services;

/// <summary>
/// Renders a decorative card back (a fixed design, not tied to any card's data) so a whole deck can
/// be printed double-sided. The look is a deep panel with a beveled border, a central diamond
/// emblem and a wordmark — deliberately simple and original.
/// </summary>
public static class BackRenderer
{
    public static BitmapSource Render(string wordmark = "CARDINATOR", int supersample = 2)
        => Render(null, wordmark, null, supersample);

    /// <summary>
    /// The set's card back (<see cref="Models.CardBackDesign"/>): Cardinator's own when <paramref name="design"/> is
    /// null or the default; the same layout in the set's colours with its wordmark (blank: <paramref name="setName"/>)
    /// and, if asked, the set symbol (<paramref name="setSymbolPath"/>) as its emblem; or the user's picture filling
    /// the card. A picture or symbol that can't be read falls back to the drawn back / the diamond.
    /// </summary>
    public static BitmapSource Render(Models.CardBackDesign? design, string? setName, string? setSymbolPath, int supersample = 2)
    {
        const double W = 750, H = 1050;
        supersample = Math.Clamp(supersample, 1, 8);
        int w = (int)(W * supersample), h = (int)(H * supersample);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(supersample, supersample));
            string style = (design?.Style ?? "").Trim().ToLowerInvariant();
            var picture = style == Models.CardBackDesign.PictureStyle ? LoadImage(design!.ImagePath) : null;
            if (picture != null) DrawPicture(dc, W, H, picture);
            else if (style == Models.CardBackDesign.DesignedStyle)
            {
                var symbol = design!.UseSetSymbol ? LoadImage(setSymbolPath) : null;
                var word = string.IsNullOrWhiteSpace(design.Wordmark) ? (setName ?? "") : design.Wordmark;
                Draw(dc, W, H, word.Trim().ToUpperInvariant(), Parse(design.Color, Color.FromRgb(0x1F, 0x2A, 0x44)),
                    Parse(design.Accent, Color.FromRgb(0xC9, 0xA2, 0x4B)), symbol);
            }
            else Draw(dc, W, H, "CARDINATOR");
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static Color Parse(string? hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString((hex ?? "").Trim()); }
        catch { return fallback; }
    }

    private static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            return System.IO.File.Exists(full) ? ImageIntake.LoadOriented(System.IO.File.ReadAllBytes(full)) : null;
        }
        catch { return null; }
    }

    /// <summary>The user's picture filling the card (cropped to cover it, centred), inside the rounded corners and a
    /// thin dark edge like every card's.</summary>
    private static void DrawPicture(DrawingContext dc, double W, double H, BitmapSource img)
    {
        const double cardR = 30;
        var clip = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        clip.Freeze();
        dc.PushClip(clip);
        double scale = Math.Max(W / img.PixelWidth, H / img.PixelHeight);
        double dw = img.PixelWidth * scale, dh = img.PixelHeight * scale;
        dc.DrawImage(img, new Rect((W - dw) / 2, (H - dh) / 2, dw, dh));
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x12)), 10), new Rect(0, 0, W, H), cardR, cardR);
    }

    private static void Draw(DrawingContext dc, double W, double H, string wordmark)
        => Draw(dc, W, H, wordmark, Color.FromRgb(0x24, 0x33, 0x53), Color.FromRgb(0xC9, 0xA2, 0x4B), null, Color.FromRgb(0x4C, 0x8D, 0xF0));

    private static void Draw(DrawingContext dc, double W, double H, string wordmark, Color panel, Color gold, BitmapSource? symbol,
        Color? emblem = null)
    {
        var full = new Rect(0, 0, W, H);
        double cardR = 30;

        var deep = Darken(panel, 0.45);
        var mid = panel;
        var edge = Color.FromRgb(0x0A, 0x0C, 0x12);
        var accent = emblem ?? Lighten(panel, 0.35);

        // Background gradient panel with rounded corners.
        var bg = new RadialGradientBrush(mid, deep) { GradientOrigin = new Point(0.5, 0.4), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.6 };
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(edge), 10), new Rect(0, 0, W, H), cardR, cardR);

        // Double keyline border.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(gold), 3), Inset(full, 26), cardR - 12, cardR - 12);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B)), 1.5),
            Inset(full, 34), cardR - 16, cardR - 16);

        // Central emblem: the set symbol on a designed back that asks for it, else the diamond.
        var cx = W / 2; var cy = H / 2 - 20; double s = 150;
        if (symbol is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            double fit = Math.Min(2 * s * 0.9 / symbol.PixelWidth, 2 * s * 0.9 / symbol.PixelHeight);
            double sw = symbol.PixelWidth * fit, sh = symbol.PixelHeight * fit;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), new Pen(new SolidColorBrush(gold), 3), new Point(cx, cy), s * 1.02, s * 1.02);
            dc.DrawImage(symbol, new Rect(cx - sw / 2, cy - sh / 2, sw, sh));
            DrawWordmark(dc, wordmark, cx, cy + s + 40, W, gold, mid, deep);
            return;
        }
        var diamond = new StreamGeometry();
        using (var g = diamond.Open())
        {
            g.BeginFigure(new Point(cx, cy - s), true, true);
            g.LineTo(new Point(cx + s * 0.72, cy), true, false);
            g.LineTo(new Point(cx, cy + s), true, false);
            g.LineTo(new Point(cx - s * 0.72, cy), true, false);
        }
        diamond.Freeze();
        var dgrad = new LinearGradientBrush(Lighten(accent, 0.25), Darken(accent, 0.25), new Point(0, 0), new Point(1, 1));
        dgrad.Freeze();
        dc.DrawGeometry(dgrad, new Pen(new SolidColorBrush(gold), 3), diamond);

        // Inner diamond highlight.
        var inner = new StreamGeometry();
        using (var g = inner.Open())
        {
            double s2 = s * 0.5;
            g.BeginFigure(new Point(cx, cy - s2), true, true);
            g.LineTo(new Point(cx + s2 * 0.72, cy), true, false);
            g.LineTo(new Point(cx, cy + s2), true, false);
            g.LineTo(new Point(cx - s2 * 0.72, cy), true, false);
        }
        inner.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, inner);

        DrawWordmark(dc, wordmark, cx, cy + s + 40, W, gold, mid, deep);
    }

    /// <summary>The wordmark beneath the emblem, shrunk to fit inside the keylines: a light tint of the accent on a dark
    /// panel, a dark shade of it on a light one (a pale designed back made a pale wordmark all but vanish — and that
    /// back prints behind every card).</summary>
    private static void DrawWordmark(DrawingContext dc, string wordmark, double cx, double y, double W, Color gold,
        Color mid, Color deep)
    {
        if (string.IsNullOrWhiteSpace(wordmark)) return;
        var face = new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        // The panel there is between its middle and its darker rim.
        double under = (Luminance(mid) + Luminance(deep)) / 2;
        var ink = new SolidColorBrush(under > 0.45 ? Darken(gold, 0.75) : Lighten(gold, 0.65));
        double size = 44;
        FormattedText ft;
        do
        {
            ft = new FormattedText(wordmark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, ink, 1.0);
            size -= 2;
        } while (ft.Width > W - 120 && size > 14);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, y));
    }

    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    internal static double Luminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static Rect Inset(Rect r, double by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private static Color Lighten(Color c, double a)
    { byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * a, 0, 255); return Color.FromRgb(L(c.R), L(c.G), L(c.B)); }

    private static Color Darken(Color c, double a)
    { byte D(byte v) => (byte)Math.Clamp(v * (1 - a), 0, 255); return Color.FromRgb(D(c.R), D(c.G), D(c.B)); }
}
