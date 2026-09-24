using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Renders a template's frame.png from its TemplateSpec: an opaque border + frame + panels,
/// with the art window punched out to transparency so custom art shows through underneath.
/// Users can replace the generated frame.png with their own (e.g. a Card-Conjurer PNG).
/// </summary>
public static class FrameGenerator
{
    /// <summary>Supersample factor so the generated frame stays crisp at 2x export.</summary>
    public const int Supersample = 2;

    public static void Generate(TemplateSpec spec, string framePngPath)
    {
        // RenderTargetBitmap requires an STA thread; if we're not on one (e.g. a background/CI
        // thread), do the work on a dedicated STA thread so generation works anywhere.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            GenerateCore(spec, framePngPath);
            return;
        }

        Exception? captured = null;
        var t = new Thread(() => { try { GenerateCore(spec, framePngPath); } catch (Exception ex) { captured = ex; } })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured != null) throw captured;
    }

    private static void GenerateCore(TemplateSpec spec, string framePngPath)
    {
        int w = spec.CanvasWidth * Supersample;
        int h = spec.CanvasHeight * Supersample;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Supersample, Supersample));
            Draw(dc, spec);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(framePngPath)!);
        using var fs = File.Create(framePngPath);
        encoder.Save(fs);
    }

    private static void Draw(DrawingContext dc, TemplateSpec spec)
    {
        if (spec.FullArt) { DrawFullArtFrame(dc, spec); return; }

        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;

        var borderColor = TemplateSpec.ParseColor(c.Border);
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var frame2Color = TemplateSpec.ParseColor(c.Frame2);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);

        var border = new SolidColorBrush(borderColor);
        var panel = new SolidColorBrush(panelColor);
        var panelPen = new Pen(new SolidColorBrush(panelBorderColor), 2);

        var frameBrush = new LinearGradientBrush(frame2Color, frameColor, new Point(0, 0), new Point(0.4, 1));

        var full = new Rect(0, 0, W, H);
        var inner = Inset(full, 14);
        var art = ToRect(spec.ArtWindow);

        double cardR = spec.CornerRadius, panelR = spec.PanelRadius;

        // 1. Outer black edge as a ring (does not cover the art window).
        dc.DrawGeometry(border, null, Exclude(RoundedGeom(full, cardR), RoundedGeom(inner, cardR - 6)));

        // 2. Frame fill covering the inner card, with the art window punched out.
        dc.DrawGeometry(frameBrush, null,
            Exclude(RoundedGeom(inner, cardR - 6), new RectangleGeometry(art, panelR, panelR)));

        // 2b. Inner keyline just inside the frame edge for a double-border look.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Darken(panelBorderColor, 0.10)), 2),
            Inset(inner, 6), cardR - 12, cardR - 12);

        // 3. Art window bevel: dark ring + light highlight + inner shadow line.
        var artOuter = Inset(art, -8);
        dc.DrawGeometry(new SolidColorBrush(panelBorderColor), null,
            Exclude(new RectangleGeometry(artOuter, panelR, panelR), new RectangleGeometry(art, panelR, panelR)));
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Lighten(frame2Color, 0.25)), 1.5),
            Inset(art, -3), panelR, panelR);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 2),
            Inset(art, -0.5), panelR, panelR);

        // 4. Panels (title / type / text / power-toughness).
        DrawPanel(dc, panel, panelPen, panelColor, spec.TitleBar, panelR);
        DrawPanel(dc, panel, panelPen, panelColor, spec.TypeBar, panelR);
        DrawPanel(dc, panel, panelPen, panelColor, spec.TextBox, panelR);
        DrawPanel(dc, panel, panelPen, panelColor, spec.PtBox, panelR);

        // 5. Ornamental filigree — corner scrolls, art-window curls, a top-center ornament.
        if (spec.Embellishments)
            DrawEmbellishments(dc, inner, art, frame2Color, panelBorderColor);
    }

    // --- ornamental embellishments -----------------------------------------

    private static void DrawEmbellishments(DrawingContext dc, Rect inner, Rect art, Color frame2, Color panelBorder)
    {
        var light = new Pen(new SolidColorBrush(Lighten(frame2, 0.45)), 2.0)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        light.Freeze();
        var stud = new SolidColorBrush(Lighten(frame2, 0.22)); stud.Freeze();
        var studEdge = new Pen(new SolidColorBrush(panelBorder), 1.2); studEdge.Freeze();

        double m = 13;
        double lx = inner.X + m, ty = inner.Y + m, rx = inner.Right - m, by = inner.Bottom - m;
        CornerScroll(dc, light, stud, studEdge, lx, ty, 1, 1);
        CornerScroll(dc, light, stud, studEdge, rx, ty, -1, 1);
        CornerScroll(dc, light, stud, studEdge, lx, by, 1, -1);
        CornerScroll(dc, light, stud, studEdge, rx, by, -1, -1);

        WindowCurl(dc, light, art.X + 3, art.Y + 3, 1, 1);
        WindowCurl(dc, light, art.Right - 3, art.Y + 3, -1, 1);
        WindowCurl(dc, light, art.X + 3, art.Bottom - 3, 1, -1);
        WindowCurl(dc, light, art.Right - 3, art.Bottom - 3, -1, -1);

        TopOrnament(dc, light, stud, studEdge, (inner.X + inner.Right) / 2, inner.Y + 9);
    }

    private static void CornerScroll(DrawingContext dc, Pen pen, Brush stud, Pen studEdge, double cx, double cy, int sx, int sy)
    {
        dc.PushTransform(LocalTransform(cx, cy, sx, sy));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            // An L-bracket hugging the corner, with a small outward curl at the top arm.
            c.BeginFigure(new Point(36, 0), false, false);
            c.BezierTo(new Point(15, 0), new Point(0, 3), new Point(0, 20), true, false);
            c.LineTo(new Point(0, 36), true, false);
            c.BeginFigure(new Point(36, 0), false, false);
            c.BezierTo(new Point(42, -3), new Point(41, 9), new Point(31, 8), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        dc.DrawGeometry(stud, studEdge, Diamond(0, 0, 6));
        dc.Pop();
    }

    private static void WindowCurl(DrawingContext dc, Pen pen, double cx, double cy, int sx, int sy)
    {
        dc.PushTransform(LocalTransform(cx, cy, sx, sy));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 20), false, false);
            c.BezierTo(new Point(0, 7), new Point(7, 0), new Point(20, 0), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
        dc.Pop();
    }

    private static void TopOrnament(DrawingContext dc, Pen pen, Brush stud, Pen studEdge, double cx, double cy)
    {
        dc.DrawGeometry(stud, studEdge, Diamond(cx, cy + 3, 7));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx - 11, cy + 3), false, false);
            c.BezierTo(new Point(cx - 26, cy + 1), new Point(cx - 34, cy + 6), new Point(cx - 42, cy + 1), true, false);
            c.BeginFigure(new Point(cx + 11, cy + 3), false, false);
            c.BezierTo(new Point(cx + 26, cy + 1), new Point(cx + 34, cy + 6), new Point(cx + 42, cy + 1), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static Transform LocalTransform(double cx, double cy, int sx, int sy)
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(sx, sy));
        g.Children.Add(new TranslateTransform(cx, cy));
        g.Freeze();
        return g;
    }

    private static Geometry Diamond(double x, double y, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y - r), true, true);
            c.LineTo(new Point(x + r, y), true, false);
            c.LineTo(new Point(x, y + r), true, false);
            c.LineTo(new Point(x - r, y), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Full-art frame: just a clean edge + thin keyline, leaving the art fully visible.</summary>
    private static void DrawFullArtFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var full = new Rect(0, 0, W, H);
        double cardR = spec.CornerRadius;

        var borderColor = TemplateSpec.ParseColor(spec.Colors.Border);
        var frameColor = TemplateSpec.ParseColor(spec.Colors.Frame);

        // Outer edge ring (transparent inside so the art shows through the whole card).
        dc.DrawGeometry(new SolidColorBrush(borderColor), null,
            Exclude(RoundedGeom(full, cardR), RoundedGeom(Inset(full, 14), cardR - 6)));

        // A thin colored keyline just inside the edge.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(frameColor), 3),
            Inset(full, 16), cardR - 12, cardR - 12);
    }

    private static void DrawPanel(DrawingContext dc, Brush fill, Pen pen, Color panelColor, Region r, double radius)
    {
        var rect = ToRect(r);
        dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);
        // subtle top highlight
        dc.DrawLine(new Pen(new SolidColorBrush(Lighten(panelColor, 0.5)), 1.2),
            new Point(rect.X + radius, rect.Y + 2.5), new Point(rect.Right - radius, rect.Y + 2.5));
    }

    private static Color Lighten(Color c, double amount)
    {
        byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    private static Color Darken(Color c, double amount)
    {
        byte D(byte v) => (byte)Math.Clamp(v * (1 - amount), 0, 255);
        return Color.FromRgb(D(c.R), D(c.G), D(c.B));
    }

    private static Rect ToRect(Region r) => new(r.X, r.Y, r.W, r.H);

    private static Rect Inset(Rect r, double by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private static Geometry RoundedGeom(Rect r, double radius)
    {
        var g = new RectangleGeometry(r, radius, radius);
        g.Freeze();
        return g;
    }

    private static Geometry Exclude(Geometry a, Geometry b)
    {
        var g = new CombinedGeometry(GeometryCombineMode.Exclude, a, b);
        g.Freeze();
        return g;
    }
}
