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
    {
        const double W = 750, H = 1050;
        int w = (int)(W * supersample), h = (int)(H * supersample);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(supersample, supersample));
            Draw(dc, W, H, wordmark);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static void Draw(DrawingContext dc, double W, double H, string wordmark)
    {
        var full = new Rect(0, 0, W, H);
        double cardR = 30;

        var deep = Color.FromRgb(0x14, 0x1B, 0x2A);
        var mid = Color.FromRgb(0x24, 0x33, 0x53);
        var edge = Color.FromRgb(0x0A, 0x0C, 0x12);
        var accent = Color.FromRgb(0x4C, 0x8D, 0xF0);
        var gold = Color.FromRgb(0xC9, 0xA2, 0x4B);

        // Background gradient panel with rounded corners.
        var bg = new RadialGradientBrush(mid, deep) { GradientOrigin = new Point(0.5, 0.4), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.6 };
        bg.Freeze();
        dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(edge), 10), new Rect(0, 0, W, H), cardR, cardR);

        // Double keyline border.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(gold), 3), Inset(full, 26), cardR - 12, cardR - 12);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B)), 1.5),
            Inset(full, 34), cardR - 16, cardR - 16);

        // Central diamond emblem.
        var cx = W / 2; var cy = H / 2 - 20; double s = 150;
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

        // Wordmark beneath the emblem.
        var face = new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var ft = new FormattedText(wordmark ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face, 44, new SolidColorBrush(Color.FromRgb(0xEC, 0xE3, 0xC9)), 1.0);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy + s + 40));
    }

    private static Rect Inset(Rect r, double by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private static Color Lighten(Color c, double a)
    { byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * a, 0, 255); return Color.FromRgb(L(c.R), L(c.G), L(c.B)); }

    private static Color Darken(Color c, double a)
    { byte D(byte v) => (byte)Math.Clamp(v * (1 - a), 0, 255); return Color.FromRgb(D(c.R), D(c.G), D(c.B)); }
}
