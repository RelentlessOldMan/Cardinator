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

        double cardR = 30, panelR = 10;

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
    }

    /// <summary>Full-art frame: just a clean edge + thin keyline, leaving the art fully visible.</summary>
    private static void DrawFullArtFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var full = new Rect(0, 0, W, H);
        double cardR = 30;

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
