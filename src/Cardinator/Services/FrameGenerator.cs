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
        var rtb = RenderToBitmap(spec);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(framePngPath)!);
        using var fs = File.Create(framePngPath);
        encoder.Save(fs);
    }

    /// <summary>Renders the frame overlay for a spec straight to a BitmapSource (no file), for the live
    /// frame-design preview. Must be called on an STA thread; use <see cref="GenerateBitmap"/> otherwise.</summary>
    public static RenderTargetBitmap RenderToBitmap(TemplateSpec spec)
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
        rtb.Freeze();
        return rtb;
    }

    /// <summary>STA-safe wrapper around <see cref="RenderToBitmap"/>.</summary>
    public static BitmapSource GenerateBitmap(TemplateSpec spec)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return RenderToBitmap(spec);
        BitmapSource? result = null; Exception? captured = null;
        var t = new Thread(() => { try { result = RenderToBitmap(spec); } catch (Exception ex) { captured = ex; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (captured != null) throw captured;
        return result!;
    }

    private static void Draw(DrawingContext dc, TemplateSpec original)
    {
        // Royal sub-border: draw the base frame with the panels/art inset to make room, then lay the
        // ornate ring in the reclaimed space just inside the black border. WithSubBorderApplied returns
        // the same instance when the knob is off, so nothing changes for non-royal templates.
        var spec = original.WithSubBorderApplied();
        bool royal = !ReferenceEquals(spec, original);

        if (spec.FullArt) { DrawFullArtFrame(dc, spec); DrawTopEmblem(dc, spec); if (royal) DrawSubBorder(dc, original); return; }
        switch ((spec.FrameStyle ?? "classic").Trim().ToLowerInvariant())
        {
            case "clean": DrawCleanFrame(dc, spec); break;
            case "ornate": DrawOrnateFrame(dc, spec); break;
            case "borderless": DrawBorderlessFrame(dc, spec); break;
            case "overlay": DrawOverlayFrame(dc, spec); break;
            case "faded": DrawFadedFrame(dc, spec); break;
            case "wave": DrawClassicFrame(dc, spec); break;   // "wave" = classic base + wave topper
            case "modern":
            case "composable": DrawComposableFrame(dc, spec); break;
            default: DrawClassicFrame(dc, spec); break;
        }
        DrawTopEmblem(dc, spec);   // composable topper over any base style
        if (royal) DrawSubBorder(dc, original);
    }

    /// <summary>An ornate "royal" sub-border ring drawn just inside the black card border (the panels are
    /// already inset to clear it — see <see cref="TemplateSpec.WithSubBorderApplied"/>). A textured gold
    /// band with bright/dark bevel keylines, a beaded inner rim, and gem flourishes at the four corners —
    /// the wave crown's cousin, but wrapping the whole card. Uses the ORIGINAL (un-inset) spec so it lands
    /// in the reclaimed ring, not on the panels.</summary>
    private static void DrawSubBorder(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var gold = Lighten(TemplateSpec.ParseColor(c.Frame2), 0.28);
        var deep = TemplateSpec.ParseColor(c.Frame);
        var edge = TemplateSpec.ParseColor(c.PanelBorder);
        double b = Math.Max(0, spec.BorderThickness);
        double t = Math.Max(4, spec.SubBorderThickness);
        double cardR = spec.CornerRadius;

        var full = new Rect(0, 0, W, H);
        var outer = Inset(full, b + 2);      // just inside the black border
        var inner = Inset(outer, t);         // inner edge of the ring
        double outerR = Math.Max(2, cardR - b - 2);
        double innerR = Math.Max(2, outerR - t);

        // 1) Metallic gold band with a 3-stop sheen (bright top / deep middle / warm bottom) + texture.
        var ringGeo = Exclude(RoundedGeom(outer, outerR), RoundedGeom(inner, innerR));
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.5, 1) };
        band.GradientStops.Add(new GradientStop(Lighten(gold, 0.48), 0));
        band.GradientStops.Add(new GradientStop(deep, 0.5));
        band.GradientStops.Add(new GradientStop(Lighten(gold, 0.30), 1));
        band.Freeze();
        dc.DrawGeometry(band, null, ringGeo);
        DrawTexture(dc, ringGeo, 1.0);

        // 2) Bevel keylines: bright on the outer rim, dark on the inner rim (light from the upper-right).
        dc.DrawRoundedRectangle(null, FrozenPen(Lighten(gold, 0.6), 2), Inset(outer, 1.5),
            Math.Max(2, outerR - 1.5), Math.Max(2, outerR - 1.5));
        dc.DrawRoundedRectangle(null, FrozenPen(Darken(edge, 0.10), 2), inner, innerR, innerR);

        // 3) Beaded inner rim — a classic royal touch: small gold beads marching around the inner edge.
        var beadFill = new SolidColorBrush(Lighten(gold, 0.5)); beadFill.Freeze();
        var beadEdge = FrozenPen(edge, 1);
        double bead = 3.0, step = 13;
        double ix0 = inner.X + innerR, ix1 = inner.Right - innerR;
        double iy0 = inner.Y + innerR, iy1 = inner.Bottom - innerR;
        for (double x = ix0; x <= ix1; x += step)
        {
            dc.DrawEllipse(beadFill, beadEdge, new Point(x, inner.Y + 3.5), bead, bead);
            dc.DrawEllipse(beadFill, beadEdge, new Point(x, inner.Bottom - 3.5), bead, bead);
        }
        for (double y = iy0; y <= iy1; y += step)
        {
            dc.DrawEllipse(beadFill, beadEdge, new Point(inner.X + 3.5, y), bead, bead);
            dc.DrawEllipse(beadFill, beadEdge, new Point(inner.Right - 3.5, y), bead, bead);
        }

        // 4) Gem flourishes on the four corners of the ring.
        double gco = Math.Max(t, outerR * 0.42);
        Gem(dc, gold, edge, outer.X + gco, outer.Y + gco, 8);
        Gem(dc, gold, edge, outer.Right - gco, outer.Y + gco, 8);
        Gem(dc, gold, edge, outer.X + gco, outer.Bottom - gco, 8);
        Gem(dc, gold, edge, outer.Right - gco, outer.Bottom - gco, 8);
    }

    /// <summary>Draws the composable top-of-card decoration (topper), layered over whatever base frame
    /// was drawn. "wave" back-compat: the legacy frameStyle "wave" implies a wave topper.</summary>
    private static void DrawTopEmblem(DrawingContext dc, TemplateSpec spec)
    {
        var e = (spec.TopEmblem ?? "none").Trim().ToLowerInvariant();
        if (e is "none" or "" && string.Equals(spec.FrameStyle, "wave", StringComparison.OrdinalIgnoreCase))
            e = "wave";
        switch (e)
        {
            case "wave": DrawWaveCrown(dc, spec); break;
            case "regal": DrawRegalCrown(dc, spec); break;
            case "leaves": DrawLeafCrown(dc, spec); break;
        }
    }

    /// <summary>A regal M15/M19-style legendary crown: a notched banner bar hugging the top of the card
    /// above the title, in the frame-highlight color with a bright keyline and center gem.</summary>
    private static void DrawRegalCrown(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth;
        var c = spec.Colors;
        var gold = Lighten(TemplateSpec.ParseColor(c.Frame2), 0.30);
        var deep = TemplateSpec.ParseColor(c.Frame);
        var edge = TemplateSpec.ParseColor(c.PanelBorder);
        double b = Math.Max(0, spec.BorderThickness);

        double left = b + 4, right = W - (b + 4), top = b + 2;
        double baseY = spec.TitleBar.Y - 6;   // the crown's lower edge, just above the title
        double notch = 12;                     // depth of the crown notches
        int teeth = 9;
        double seg = (right - left) / teeth;

        // Crown body: straight top, a notched (battlement-like) lower edge that dips in regular teeth.
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(left, top), true, true);
            s.LineTo(new Point(right, top), true, false);
            s.LineTo(new Point(right, baseY), true, false);
            for (int i = 0; i < teeth; i++)
            {
                double x1 = right - (i + 1) * seg;
                double midHi = right - (i + 0.5) * seg;
                // dip down to a notch then back up — a repeating crown tooth
                s.LineTo(new Point(midHi, baseY + notch), true, false);
                s.LineTo(new Point(x1, baseY), true, false);
            }
        }
        g.Freeze();

        var fill = new LinearGradientBrush(Lighten(gold, 0.35), deep, new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, FrozenPen(edge, 2.5), g);
        // bright keyline along the top + a center gem flourish
        dc.DrawLine(FrozenPen(Lighten(gold, 0.5), 2), new Point(left + 8, top + 4), new Point(right - 8, top + 4));
        Gem(dc, gold, edge, W / 2, baseY - 2, 8);
    }

    /// <summary>A leafy scroll crown baked across the top of the card (design topper, always on).</summary>
    private static void DrawLeafCrown(DrawingContext dc, TemplateSpec spec)
    {
        var c = spec.Colors;
        var gold = Lighten(TemplateSpec.ParseColor(c.Frame2), 0.30);
        var edge = TemplateSpec.ParseColor(c.PanelBorder);
        double b = Math.Max(0, spec.BorderThickness);
        double left = b + 10, right = spec.CanvasWidth - (b + 10);
        double baseY = spec.TitleBar.Y - 4;

        dc.DrawLine(FrozenPen(gold, 2), new Point(left, baseY), new Point(right, baseY));
        int n = 13;
        double step = (right - left) / (n - 1);
        var fill = new SolidColorBrush(gold);
        for (int i = 0; i < n; i++)
        {
            double x = left + i * step;
            double h = (i % 2 == 0) ? 11 : 17;
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(x, baseY), true, true);
                s.BezierTo(new Point(x - 7, baseY - h * 0.5), new Point(x - 3, baseY - h), new Point(x, baseY - h), true, false);
                s.BezierTo(new Point(x + 3, baseY - h), new Point(x + 7, baseY - h * 0.5), new Point(x, baseY), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, FrozenPen(edge, 1.2), g);
        }
        Gem(dc, gold, edge, (left + right) / 2, baseY - 2, 8);
    }

    /// <summary>Borderless: no frame — just translucent floating panels over the full-bleed art.</summary>
    private static void DrawBorderlessFrame(DrawingContext dc, TemplateSpec spec)
    {
        double panelR = Math.Max(8, spec.PanelRadius);
        var shadow = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        var fill = new SolidColorBrush(Color.FromArgb(190, 14, 16, 22));
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 235, 238, 245)), 2);

        var title = ToRect(spec.TitleBar);
        var type = ToRect(spec.TypeBar);
        var text = ToRect(spec.EffectiveTextBox);
        var lower = new Rect(Math.Min(type.X, text.X), type.Y,
            Math.Max(type.Width, text.Width), text.Bottom - type.Y);

        foreach (var rect in new[] { title, lower })
        {
            dc.DrawRoundedRectangle(shadow, null, new Rect(rect.X - 5, rect.Y + 7, rect.Width, rect.Height), panelR, panelR);
            dc.DrawRoundedRectangle(fill, edge, rect, panelR, panelR);
        }
        // P/T box is drawn per-card by the renderer (creatures only).
    }

    /// <summary>Wave: a classic frame plus a colorful scalloped "wave" crown flowing across the top of
    /// the card (over the frame, above the title), like a showcase/extended treatment.</summary>
    private static void DrawWaveCrown(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth;
        var c = spec.Colors;
        var wave = TemplateSpec.ParseColor(c.Frame2);         // the vivid frame-highlight color
        var deep = TemplateSpec.ParseColor(c.Frame);
        var edge = TemplateSpec.ParseColor(c.PanelBorder);

        // Band sits inside the black border (drawn later), over the top frame, above the title bar.
        double b = Math.Max(0, spec.BorderThickness);
        double left = b + 6, right = W - (b + 6), top = b + 2;
        double baseY = spec.TitleBar.Y - 10;   // scallop baseline just above the title
        double amp = 12;
        int n = 6;
        double seg = (right - left) / n;

        Geometry BuildBand(double lift)
        {
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(left, top), true, true);
                s.LineTo(new Point(right, top), true, false);
                s.LineTo(new Point(right, baseY - lift), true, false);
                for (int i = 0; i < n; i++)
                {
                    double x1 = right - (i + 1) * seg;
                    double midx = right - (i + 0.5) * seg;
                    s.QuadraticBezierTo(new Point(midx, baseY + amp - lift), new Point(x1, baseY - lift), true, false);
                }
                s.LineTo(new Point(left, top), true, false);
            }
            g.Freeze();
            return g;
        }

        // A deeper echo behind for a layered, flowing look, then the bright wave on top.
        dc.DrawGeometry(new SolidColorBrush(Darken(deep, 0.15)), null, BuildBand(-8));
        var fill = new LinearGradientBrush(Lighten(wave, 0.40), wave, new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        var band = BuildBand(0);
        dc.DrawGeometry(fill, FrozenPen(edge, 2.5), band);

        // Bright crest line along the scalloped edge + a couple of foam gems for flourish.
        var crest = new StreamGeometry();
        using (var s = crest.Open())
        {
            s.BeginFigure(new Point(right, baseY), false, false);
            for (int i = 0; i < n; i++)
            {
                double x1 = right - (i + 1) * seg;
                double midx = right - (i + 0.5) * seg;
                s.QuadraticBezierTo(new Point(midx, baseY + amp), new Point(x1, baseY), true, false);
            }
        }
        crest.Freeze();
        dc.DrawGeometry(null, FrozenPen(Lighten(wave, 0.6), 2), crest);
    }

    /// <summary>Overlay: full-bleed art with a cinematic dark band + gold trim over the lower card,
    /// where the type line and rules text sit directly on the art. A title scrip keeps the name legible.</summary>
    private static void DrawOverlayFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var frame2 = TemplateSpec.ParseColor(c.Frame2);
        var gold = Lighten(frame2, 0.35);

        var title = ToRect(spec.TitleBar);
        var type = ToRect(spec.TypeBar);
        var text = ToRect(spec.EffectiveTextBox);

        // Cinematic bottom band: transparent at the top, deepening to near-opaque at the card bottom.
        double bandTop = type.Y - 26;
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 6, 8, 12), 0));
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(150, 6, 8, 12), 0.22));
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(232, 6, 8, 12), 0.55));
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(245, 6, 8, 12), 1));
        grad.Freeze();
        dc.DrawRectangle(grad, null, new Rect(0, bandTop, W, H - bandTop));

        // Rounded translucent title plate — anchored to the inner edge of the black border so it's
        // never clipped by it (the plate top sits just inside the border, whatever its thickness).
        double b = Math.Max(0, spec.BorderThickness);
        double plateTop = Math.Max(title.Y - 8, b + 6);
        var plate = new Rect(title.X - 7, plateTop, title.Width + 14, title.Bottom + 8 - plateTop);
        var tScrim = new SolidColorBrush(Color.FromArgb(165, 6, 8, 12)); tScrim.Freeze();
        dc.DrawRoundedRectangle(tScrim, FrozenPen(gold, 2), plate, 12, 12);

        // A translucent rules plate with a gold keyline, so the body text has a subtle seat.
        var body = new Rect(text.X - 6, type.Y - 6, text.Width + 12, text.Bottom - type.Y + 12);
        var bodyScrim = new SolidColorBrush(Color.FromArgb(120, 6, 8, 12)); bodyScrim.Freeze();
        dc.DrawRoundedRectangle(bodyScrim, FrozenPen(Color.FromArgb(150, gold.R, gold.G, gold.B), 2), body, 12, 12);

        // Subtle gold keyline beneath the type line, aligned to the body plate's inner edges (not
        // extending past them).
        dc.DrawLine(FrozenPen(Lighten(gold, 0.15), 1.6),
            new Point(body.X + 12, type.Bottom + 4), new Point(body.Right - 12, type.Bottom + 4));
        // P/T box is drawn per-card by the renderer (creatures only).
    }

    /// <summary>Faded: classic panels, but the frame melts into the art with soft gradient edges.</summary>
    private static void DrawFadedFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var borderColor = TemplateSpec.ParseColor(c.Border);
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var frame2Color = TemplateSpec.ParseColor(c.Frame2);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);

        var full = new Rect(0, 0, W, H);
        var inner = Inset(full, 14);
        var art = ToRect(spec.ArtWindow);
        double cardR = spec.CornerRadius, panelR = spec.PanelRadius;

        dc.DrawGeometry(new SolidColorBrush(borderColor), null, Exclude(RoundedGeom(full, cardR), RoundedGeom(inner, cardR - 6)));
        dc.DrawGeometry(new SolidColorBrush(frameColor), null,
            Exclude(RoundedGeom(inner, cardR - 6), new RectangleGeometry(art, panelR, panelR)));

        // Wide, obvious gradient bands that melt the frame into the art on all four sides (no hard
        // keyline). Two overlapping passes per side make the flowing fade clearly visible.
        var into = Color.FromArgb(0, frameColor.R, frameColor.G, frameColor.B);
        double fadeW = 64;
        foreach (var pass in new[] { fadeW, fadeW * 0.5 })
        {
            SoftEdge(dc, new Rect(art.X, art.Y, art.Width, pass), frameColor, into, 90);               // top
            SoftEdge(dc, new Rect(art.X, art.Bottom - pass, art.Width, pass), frameColor, into, 270);  // bottom
            SoftEdge(dc, new Rect(art.X, art.Y, pass, art.Height), frameColor, into, 0);               // left
            SoftEdge(dc, new Rect(art.Right - pass, art.Y, pass, art.Height), frameColor, into, 180);  // right
        }

        DrawPanel(dc, new SolidColorBrush(panelColor), new Pen(new SolidColorBrush(panelBorderColor), 3), panelColor, spec.TitleBar, panelR);
        DrawPanel(dc, new SolidColorBrush(panelColor), new Pen(new SolidColorBrush(panelBorderColor), 3), panelColor, spec.TypeBar, panelR);
        DrawPanel(dc, new SolidColorBrush(panelColor), new Pen(new SolidColorBrush(panelBorderColor), 3), panelColor, spec.EffectiveTextBox, panelR);
    }

    private static void SoftEdge(DrawingContext dc, Rect rect, Color solid, Color transparent, double angleDeg)
    {
        Point start, end;
        switch (angleDeg)
        {
            case 90: start = new Point(0.5, 0); end = new Point(0.5, 1); break;   // top: solid at top → clear at bottom
            case 270: start = new Point(0.5, 1); end = new Point(0.5, 0); break;  // bottom
            case 0: start = new Point(0, 0.5); end = new Point(1, 0.5); break;    // left
            default: start = new Point(1, 0.5); end = new Point(0, 0.5); break;   // right
        }
        var g = new LinearGradientBrush(solid, transparent, start, end);
        g.Freeze();
        dc.DrawRectangle(g, null, rect);
    }

    /// <summary>The default MTG-style frame: gradient border, beveled art window and panels, filigree.</summary>
    private static void DrawClassicFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;

        var borderColor = TemplateSpec.ParseColor(c.Border);
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var frame2Color = TemplateSpec.ParseColor(c.Frame2);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);

        var border = new SolidColorBrush(borderColor);
        var panel = new SolidColorBrush(panelColor);
        var panelPen = new Pen(new SolidColorBrush(panelBorderColor), 3);

        var frameBrush = new LinearGradientBrush(frame2Color, frameColor, new Point(0, 0), new Point(0.4, 1));

        var full = new Rect(0, 0, W, H);
        var inner = Inset(full, 14);
        var art = ToRect(spec.ArtWindow);

        double cardR = spec.CornerRadius, panelR = spec.PanelRadius;
        double b = Math.Max(0, spec.BorderThickness);   // decorations sit inboard of the black border
        double keyIn = b + 6;

        dc.DrawGeometry(border, null, Exclude(RoundedGeom(full, cardR), RoundedGeom(inner, cardR - 6)));
        dc.DrawGeometry(frameBrush, null,
            Exclude(RoundedGeom(inner, cardR - 6), new RectangleGeometry(art, panelR, panelR)));
        // Inner keyline sits inboard of the thick black card border so it stays visible.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Darken(panelBorderColor, 0.10)), 3),
            Inset(full, keyIn), Math.Max(3, cardR - keyIn), Math.Max(3, cardR - keyIn));

        var artOuter = Inset(art, -9);
        dc.DrawGeometry(new SolidColorBrush(panelBorderColor), null,
            Exclude(new RectangleGeometry(artOuter, panelR, panelR), new RectangleGeometry(art, panelR, panelR)));
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Lighten(frame2Color, 0.25)), 2.5),
            Inset(art, -3.5), panelR, panelR);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 2.5),
            Inset(art, -0.5), panelR, panelR);

        DrawPanel(dc, panel, panelPen, panelColor, spec.TitleBar, panelR);
        DrawPanel(dc, panel, panelPen, panelColor, spec.TypeBar, panelR);
        DrawPanel(dc, panel, panelPen, panelColor, spec.EffectiveTextBox, panelR);
        // P/T box is drawn per-card by the renderer (creatures only), not baked into the frame.

        // Little 3D rivet studs at the title/type corners.
        var studGold = Lighten(frame2Color, 0.35);
        DrawStuds(dc, ToRect(spec.TitleBar), studGold, panelBorderColor);
        DrawStuds(dc, ToRect(spec.TypeBar), studGold, panelBorderColor);

        if (spec.Embellishments)
            DrawEmbellishments(dc, Inset(full, b + 4), art, frame2Color, panelBorderColor);
    }

    /// <summary>A flat, modern frame: solid slab, crisp thin edges, flat panels, no bevel or filigree.</summary>
    private static void DrawCleanFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var borderColor = TemplateSpec.ParseColor(c.Border);
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);

        var full = new Rect(0, 0, W, H);
        var art = ToRect(spec.ArtWindow);
        double cardR = spec.CornerRadius, panelR = spec.PanelRadius;

        // Flat frame slab with the art window punched out.
        dc.DrawGeometry(new SolidColorBrush(frameColor), null,
            Exclude(RoundedGeom(full, cardR), new RectangleGeometry(art, panelR, panelR)));
        // Crisp outer edge.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(borderColor), 8), Inset(full, 4), cardR, cardR);
        // Art-window keyline (no bevel) + a soft drop shadow inside for a little depth.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(panelBorderColor), 3.5), art, panelR, panelR);
        // Flat panels: solid fill + bold border + a bottom-left drop shadow for pop.
        var pen = new Pen(new SolidColorBrush(panelBorderColor), 2.5);
        var fill = new SolidColorBrush(panelColor);
        foreach (var r in new[] { spec.TitleBar, spec.TypeBar, spec.EffectiveTextBox })   // no P/T box (drawn per-card)
        {
            var rect = ToRect(r);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)), null,
                new Rect(rect.X - 4, rect.Y + 6, rect.Width, rect.Height), panelR, panelR);
            dc.DrawRoundedRectangle(fill, pen, rect, panelR, panelR);
        }
    }

    /// <summary>A heavily decorated frame: metallic band, a title banner, bold corner scrollwork + gems.</summary>
    private static void DrawOrnateFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var borderColor = TemplateSpec.ParseColor(c.Border);
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var frame2Color = TemplateSpec.ParseColor(c.Frame2);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);

        var full = new Rect(0, 0, W, H);
        var art = ToRect(spec.ArtWindow);
        double cardR = spec.CornerRadius, panelR = spec.PanelRadius;
        double b = Math.Max(0, spec.BorderThickness);   // decorations sit inboard of the black border
        var gold = Lighten(frame2Color, 0.35);

        // Outer dark edge.
        dc.DrawGeometry(new SolidColorBrush(borderColor), null,
            Exclude(RoundedGeom(full, cardR), RoundedGeom(Inset(full, 11), cardR - 5)));

        // Thick metallic band (3-stop sheen), art window punched out.
        var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.55, 1) };
        band.GradientStops.Add(new GradientStop(Lighten(frame2Color, 0.4), 0));
        band.GradientStops.Add(new GradientStop(frameColor, 0.5));
        band.GradientStops.Add(new GradientStop(Lighten(frame2Color, 0.22), 1));
        band.Freeze();
        var bandGeo = Exclude(RoundedGeom(Inset(full, 11), cardR - 5), new RectangleGeometry(art, panelR, panelR));
        dc.DrawGeometry(band, null, bandGeo);
        DrawTexture(dc, bandGeo, TextureStrength(spec));   // procedural stone-ish speckle on the border

        // Double pinline (bright metallic + dark) — inboard of the thick black card border.
        double p1 = b + 6, p2 = b + 13;
        dc.DrawRoundedRectangle(null, FrozenPen(Lighten(frame2Color, 0.55), 4), Inset(full, p1), Math.Max(3, cardR - p1), Math.Max(3, cardR - p1));
        dc.DrawRoundedRectangle(null, FrozenPen(Darken(panelBorderColor, 0.10), 2.5), Inset(full, p2), Math.Max(3, cardR - p2), Math.Max(3, cardR - p2));

        // Tapered gold accent pieces along each inner edge (thick at the corners, tapering to the middle).
        TaperedEdges(dc, Inset(full, p1), gold, panelBorderColor);

        // Art window bevel with a bright inner keyline.
        var artOuter = Inset(art, -10);
        dc.DrawGeometry(new SolidColorBrush(panelBorderColor), null,
            Exclude(new RectangleGeometry(artOuter, panelR, panelR), new RectangleGeometry(art, panelR, panelR)));
        dc.DrawRoundedRectangle(null, FrozenPen(Lighten(frame2Color, 0.5), 3), Inset(art, -4.5), panelR, panelR);

        // Panels (type / text / P-T) — light gradient with a gold top edge. Title uses a banner.
        var panelBrush = new LinearGradientBrush(Lighten(panelColor, 0.10), panelColor, new Point(0, 0), new Point(0, 1));
        panelBrush.Freeze();
        var pPen = FrozenPen(panelBorderColor, 3);
        foreach (var r in new[] { spec.TypeBar, spec.EffectiveTextBox })   // no P/T box (drawn per-card)
        {
            var rect = ToRect(r);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), null,
                new Rect(rect.X - 4, rect.Y + 6, rect.Width, rect.Height), panelR, panelR);   // drop shadow (bottom-left)
            dc.DrawRoundedRectangle(panelBrush, pPen, rect, panelR, panelR);
            dc.DrawLine(FrozenPen(gold, 2), new Point(rect.X + panelR, rect.Y + 3), new Point(rect.Right - panelR, rect.Y + 3));
        }

        // Filigree divider under the type line, and rivet studs on the type bar.
        var tb = ToRect(spec.TypeBar);
        FiligreeDivider(dc, gold, panelBorderColor, tb.X + 18, tb.Bottom + 5, tb.Right - 18);
        DrawStuds(dc, tb, gold, panelBorderColor);

        // Title banner (light nameplate with a gold ornate outline + gems), keeps the title readable.
        DrawBanner(dc, ToRect(spec.TitleBar), panelBrush, panelBorderColor, gold);

        // Bold corner scrollwork + big gems, inboard of the black border. Only the TOP corners, so the
        // bottom-left collector/artist footer and the bottom-right P/T box stay clean and uncluttered.
        double m = b + 14;
        OrnateCorner(dc, gold, panelBorderColor, full.X + m, full.Y + m, 1, 1);
        OrnateCorner(dc, gold, panelBorderColor, full.Right - m, full.Y + m, -1, 1);
    }

    /// <summary>A mottled/marbled metal texture (deterministic): soft blotches for marbling plus a fine
    /// grain on top. Clearly visible so a frame reads as brushed/marbled metal, scaled by strength.</summary>
    private static void DrawTexture(DrawingContext dc, Geometry clip, double strength = 1.0)
    {
        if (strength <= 0) return;
        dc.PushClip(clip);
        var rnd = new Random(20260924);

        // 1) Soft marbling blotches (larger, low-alpha) for the veined/marbled look.
        int blotches = (int)(200 * strength);
        for (int i = 0; i < blotches; i++)
        {
            double x = rnd.NextDouble() * 760, y = rnd.NextDouble() * 1060;
            double s = 7 + rnd.NextDouble() * 18;
            byte a = (byte)rnd.Next(10, 24);
            var col = rnd.Next(2) == 0 ? Color.FromArgb(a, 255, 255, 255) : Color.FromArgb(a, 0, 0, 0);
            dc.DrawEllipse(new SolidColorBrush(col), null, new Point(x, y), s, s * 0.66);
        }

        // 2) Fine grain on top (small, higher-alpha) so it reads as texture, not a flat gradient.
        int count = (int)(3400 * strength);
        for (int i = 0; i < count; i++)
        {
            double x = rnd.NextDouble() * 760, y = rnd.NextDouble() * 1060;
            double s = 0.6 + rnd.NextDouble() * 2.6;
            byte a = (byte)rnd.Next(20, 66);
            var col = rnd.Next(2) == 0 ? Color.FromArgb(a, 255, 255, 255) : Color.FromArgb(a, 0, 0, 0);
            dc.DrawEllipse(new SolidColorBrush(col), null, new Point(x, y), s, s);
        }
        dc.Pop();
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var p = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    private static void Gem(DrawingContext dc, Color fill, Color edge, double x, double y, double r)
    {
        dc.DrawGeometry(new SolidColorBrush(fill), FrozenPen(edge, 2), Diamond(x, y, r));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), null,
            new Point(x + r * 0.25, y - r * 0.3), r * 0.28, r * 0.28);   // highlight upper-right
    }

    private static void DrawBanner(DrawingContext dc, Rect bar, Brush fill, Color edge, Color gold)
    {
        double midY = bar.Y + bar.Height / 2;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(bar.X - 14, midY), true, true);
            s.LineTo(new Point(bar.X + 8, bar.Y - 3), true, false);
            s.LineTo(new Point(bar.Right - 8, bar.Y - 3), true, false);
            s.LineTo(new Point(bar.Right + 14, midY), true, false);
            s.LineTo(new Point(bar.Right - 8, bar.Bottom + 3), true, false);
            s.LineTo(new Point(bar.X + 8, bar.Bottom + 3), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(fill, FrozenPen(edge, 3), g);
        dc.DrawGeometry(null, FrozenPen(gold, 2), g);
        Gem(dc, gold, edge, bar.X - 7, midY, 9);
        Gem(dc, gold, edge, bar.Right + 7, midY, 9);
    }

    private static void OrnateCorner(DrawingContext dc, Color gold, Color edge, double cx, double cy, int sx, int sy)
    {
        dc.PushTransform(LocalTransform(cx, cy, sx, sy));
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(48, 0), false, false);
            s.BezierTo(new Point(20, 0), new Point(0, 5), new Point(0, 26), true, false);
            s.LineTo(new Point(0, 48), true, false);
            s.BeginFigure(new Point(48, 0), false, false);   // outward curl at the arm end
            s.BezierTo(new Point(58, -5), new Point(55, 14), new Point(41, 11), true, false);
            s.BeginFigure(new Point(0, 48), false, false);   // inward curl at the down-arm end
            s.BezierTo(new Point(-5, 58), new Point(14, 55), new Point(11, 41), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, FrozenPen(gold, 6.5), g);
        dc.DrawGeometry(null, FrozenPen(edge, 2), g);
        dc.Pop();
        Gem(dc, gold, edge, cx, cy, 12);
    }

    /// <summary>Gold tapered wedges running from each corner along every edge, tapering to nothing at
    /// the edge midpoints — the "tapering border" look of old card frames.</summary>
    private static void TaperedEdges(DrawingContext dc, Rect r, Color gold, Color edge)
    {
        var fill = new SolidColorBrush(Color.FromArgb(215, gold.R, gold.G, gold.B)); fill.Freeze();
        var pen = FrozenPen(edge, 1.2);
        double d = 18;   // taper depth at each corner
        double midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;

        void Tri(Point a, Point b, Point c)
        {
            var g = new StreamGeometry();
            using (var s = g.Open()) { s.BeginFigure(a, true, true); s.LineTo(b, true, true); s.LineTo(c, true, true); }
            g.Freeze();
            dc.DrawGeometry(fill, pen, g);
        }

        Tri(new Point(r.Left, r.Top), new Point(r.Left, r.Top + d), new Point(midX, r.Top));       // top-left → top-mid
        Tri(new Point(r.Right, r.Top), new Point(r.Right, r.Top + d), new Point(midX, r.Top));      // top-right → top-mid
        Tri(new Point(r.Left, r.Bottom), new Point(r.Left, r.Bottom - d), new Point(midX, r.Bottom));
        Tri(new Point(r.Right, r.Bottom), new Point(r.Right, r.Bottom - d), new Point(midX, r.Bottom));
        Tri(new Point(r.Left, r.Top), new Point(r.Left + d, r.Top), new Point(r.Left, midY));       // top-left → left-mid
        Tri(new Point(r.Left, r.Bottom), new Point(r.Left + d, r.Bottom), new Point(r.Left, midY));
        Tri(new Point(r.Right, r.Top), new Point(r.Right - d, r.Top), new Point(r.Right, midY));
        Tri(new Point(r.Right, r.Bottom), new Point(r.Right - d, r.Bottom), new Point(r.Right, midY));
    }

    private static void FiligreeDivider(DrawingContext dc, Color gold, Color edge, double x1, double y, double x2)
    {
        double cx = (x1 + x2) / 2;
        var pen = FrozenPen(gold, 2.8);
        dc.DrawLine(pen, new Point(x1, y), new Point(cx - 16, y));
        dc.DrawLine(pen, new Point(cx + 16, y), new Point(x2, y));
        Gem(dc, gold, edge, cx, y, 8);
    }

    // --- ornamental embellishments -----------------------------------------

    private static void DrawEmbellishments(DrawingContext dc, Rect inner, Rect art, Color frame2, Color panelBorder)
    {
        var light = new Pen(new SolidColorBrush(Lighten(frame2, 0.45)), 2.8)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        light.Freeze();
        var stud = new SolidColorBrush(Lighten(frame2, 0.22)); stud.Freeze();
        var studEdge = new Pen(new SolidColorBrush(panelBorder), 1.6); studEdge.Freeze();

        double m = 13;
        double lx = inner.X + m, ty = inner.Y + m, rx = inner.Right - m;
        // Top corners only — keep the bottom margin clear for the footer + P/T box.
        CornerScroll(dc, light, stud, studEdge, lx, ty, 1, 1);
        CornerScroll(dc, light, stud, studEdge, rx, ty, -1, 1);

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

    /// <summary>Composable frame: independent knobs (PanelBackground, ConnectedPanels, BottomTaper,
    /// FadedEdges, TopEmblem, texture, border) that mix and match. frameStyle "modern" is this with the
    /// M15-ish defaults on.</summary>
    private static void DrawComposableFrame(DrawingContext dc, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        var c = spec.Colors;
        var frameColor = TemplateSpec.ParseColor(c.Frame);
        var frame2Color = TemplateSpec.ParseColor(c.Frame2);
        var panelColor = TemplateSpec.ParseColor(c.Panel);
        var panelBorderColor = TemplateSpec.ParseColor(c.PanelBorder);
        double cardR = spec.CornerRadius, panelR = Math.Max(10, spec.PanelRadius);
        double b = Math.Max(0, spec.BorderThickness);
        var light = Lighten(frame2Color, 0.45);
        var dark = Darken(panelBorderColor, 0.05);
        var art = ToRect(spec.ArtWindow);
        var bgRect = Inset(new Rect(0, 0, W, H), b);
        double bgR = Math.Max(6, cardR - b);
        bool fullBleedArt = art.Width >= W * 0.92 && art.Height >= H * 0.92;

        // Full-bleed layout (thing4): art fills the card; skip the background + art bevel and just lay the
        // connected/flat panels over the art (title on art at top, type+text at the bottom).
        if (fullBleedArt)
        {
            foreach (var reg in new[] { spec.TitleBar, spec.TypeBar, spec.EffectiveTextBox })
            {
                var rect = ToRect(reg);
                if (spec.ConnectedPanels)
                    ModernPlate(dc, rect, panelColor, frame2Color, dark, panelR, curvedEnds: reg == spec.TitleBar);
                else
                {
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null,
                        new Rect(rect.X - 3, rect.Y + 5, rect.Width, rect.Height), panelR, panelR);
                    dc.DrawRoundedRectangle(new SolidColorBrush(panelColor), FrozenPen(panelBorderColor, 2.5), rect, panelR, panelR);
                }
            }
            return;
        }

        // 1) Background (optional): textured colored fill the panels sit on, art window punched out. The
        //    colored frame fills the interior, then a BLACK collector strip covers the area below the text
        //    box (like real cards — the footer sits there). The black edge rim (drawn later) stays even.
        if (spec.PanelBackground)
        {
            var bgGeo = Exclude(RoundedGeom(bgRect, bgR), new RectangleGeometry(art, panelR, panelR));
            // Near-uniform frame colour top-to-bottom (only a whisper of a gradient for life), so the area
            // below the description panel reads as the same frame colour as the sides — not a darker block.
            var bgBrush = new LinearGradientBrush(Lighten(frame2Color, 0.08), Darken(frame2Color, 0.05), new Point(0, 0), new Point(0, 1));
            bgBrush.Freeze();
            dc.DrawGeometry(bgBrush, null, bgGeo);
            DrawTexture(dc, bgGeo, TextureStrength(spec));
            if (spec.BottomTaper)
            {
                // "partial": the taper's foot sits at the bottom of the description panel, so the colored
                // background curves in to meet the panel and nothing dark shows below it. "full": the foot
                // runs to the very bottom edge of the card.
                bool full = string.Equals(spec.TaperStyle, "full", StringComparison.OrdinalIgnoreCase);
                double footY = full ? bgRect.Bottom : Math.Min(bgRect.Bottom, ToRect(spec.EffectiveTextBox).Bottom);
                dc.PushClip(bgGeo);
                TaperBottom(dc, bgRect, Darken(frameColor, 0.30), footY);
                dc.Pop();
            }
            dc.DrawRoundedRectangle(null, FrozenPen(light, 2), Inset(bgRect, 3.5), Math.Max(3, bgR - 3.5), Math.Max(3, bgR - 3.5));
        }
        else
        {
            dc.DrawRoundedRectangle(null, FrozenPen(frameColor, 3), Inset(bgRect, 2), Math.Max(3, bgR - 2), Math.Max(3, bgR - 2));
        }

        // 2) Art window edge. Connected: a THICK beveled band (same raised look as the nameplates).
        if (spec.FadedEdges)
        {
            var into = Color.FromArgb(0, frameColor.R, frameColor.G, frameColor.B);
            double fw = 40;
            SoftEdge(dc, new Rect(art.X, art.Y, art.Width, fw), frameColor, into, 90);
            SoftEdge(dc, new Rect(art.X, art.Bottom - fw, art.Width, fw), frameColor, into, 270);
            SoftEdge(dc, new Rect(art.X, art.Y, fw, art.Height), frameColor, into, 0);
            SoftEdge(dc, new Rect(art.Right - fw, art.Y, fw, art.Height), frameColor, into, 180);
        }
        else if (spec.ConnectedPanels)
        {
            double aw = 8;   // beveled art band thickness
            var outer = Inset(art, -aw);
            var band = new LinearGradientBrush(Lighten(frame2Color, 0.42), Darken(frame2Color, 0.22), new Point(0, 0), new Point(0, 1));
            band.Freeze();
            dc.DrawGeometry(band, FrozenPen(dark, 2.5),
                Exclude(new RectangleGeometry(outer, panelR + aw, panelR + aw), new RectangleGeometry(art, panelR, panelR)));
            dc.DrawRoundedRectangle(null, FrozenPen(Lighten(frame2Color, 0.6), 1.6), Inset(art, -aw + 1.5), panelR + aw - 1.5, panelR + aw - 1.5);   // outer highlight
            dc.DrawRoundedRectangle(null, FrozenPen(DarkenC(dark), 1.6), Inset(art, -0.5), panelR, panelR);   // inner dark edge
        }
        else
        {
            dc.DrawRoundedRectangle(null, FrozenPen(panelBorderColor, 3), art, panelR, panelR);
        }

        // 3) Nameplates + text box — thick 3D beveled panels with curved ends (title + type).
        foreach (var reg in new[] { spec.TitleBar, spec.TypeBar, spec.TextBox })
        {
            var rect = ToRect(reg);
            if (spec.ConnectedPanels)
                ModernPlate(dc, rect, panelColor, frame2Color, dark, panelR, curvedEnds: reg == spec.TitleBar || reg == spec.TypeBar);
            else
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null,
                    new Rect(rect.X - 3, rect.Y + 5, rect.Width, rect.Height), panelR, panelR);
                dc.DrawRoundedRectangle(new SolidColorBrush(panelColor), FrozenPen(panelBorderColor, 2.5), rect, panelR, panelR);
            }
        }
    }

    private static Color DarkenC(Color c) => Darken(c, 0.25);

    /// <summary>A nameplate/panel with a THICK 3D beveled border: a raised frame-colored band (bright
    /// top highlight, dark bottom shade) around a slightly recessed text area. When
    /// <paramref name="curvedEnds"/> is set, the left/right ends bulge outward into real curves.</summary>
    private static void ModernPlate(DrawingContext dc, Rect rect, Color panelFill, Color frame2, Color dark, double r, bool curvedEnds = false)
    {
        double bulge = curvedEnds ? Math.Min(13, rect.Height * 0.24) : 0;
        Geometry Shape(Rect rr, double rad, double bl) => bl > 0 ? CurvedBar(rr, rad, bl) : RoundedGeom(rr, rad);

        // Drop shadow.
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
            Shape(new Rect(rect.X - 2, rect.Y + 3, rect.Width, rect.Height), r, bulge));

        // Raised beveled band (the thick nameplate border): bright at the top, dark at the bottom.
        var band = new LinearGradientBrush(Lighten(frame2, 0.42), Darken(frame2, 0.22), new Point(0, 0), new Point(0, 1));
        band.Freeze();
        var outer = Shape(rect, r, bulge);
        dc.DrawGeometry(band, FrozenPen(dark, 2.5), outer);
        // bevel: bright highlight top + left, dark shade bottom + right
        dc.DrawLine(FrozenPen(Lighten(frame2, 0.62), 2), new Point(rect.X + r, rect.Y + 2.5), new Point(rect.Right - r, rect.Y + 2.5));
        dc.DrawLine(FrozenPen(Darken(frame2, 0.38), 1.6), new Point(rect.X + r, rect.Bottom - 2.5), new Point(rect.Right - r, rect.Bottom - 2.5));

        // Recessed inner text area (darker at the top edge for an inset look).
        var innerRect = Inset(rect, 7);
        var innerGeo = Shape(innerRect, Math.Max(2, r - 4), Math.Max(0, bulge - 5));
        var innerBrush = new LinearGradientBrush(Darken(panelFill, 0.06), Lighten(panelFill, 0.05), new Point(0, 0), new Point(0, 1));
        innerBrush.Freeze();
        dc.DrawGeometry(innerBrush, FrozenPen(Darken(frame2, 0.28), 1.2), innerGeo);
        dc.DrawLine(FrozenPen(Color.FromArgb(60, 0, 0, 0), 1), new Point(innerRect.X + 3, innerRect.Y + 1.5), new Point(innerRect.Right - 3, innerRect.Y + 1.5));
    }

    /// <summary>A rounded bar whose left/right ends bulge outward into convex curves (for curved titles).</summary>
    private static Geometry CurvedBar(Rect r, double round, double bulge)
    {
        double x0 = r.X, x1 = r.Right, y0 = r.Y, y1 = r.Bottom, my = (y0 + y1) / 2;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(x0 + round, y0), true, true);
            s.LineTo(new Point(x1 - round, y0), true, false);
            s.QuadraticBezierTo(new Point(x1, y0), new Point(x1, y0 + round), true, false);
            s.QuadraticBezierTo(new Point(x1 + bulge, my), new Point(x1, y1 - round), true, false);   // right curve
            s.QuadraticBezierTo(new Point(x1, y1), new Point(x1 - round, y1), true, false);
            s.LineTo(new Point(x0 + round, y1), true, false);
            s.QuadraticBezierTo(new Point(x0, y1), new Point(x0, y1 - round), true, false);
            s.QuadraticBezierTo(new Point(x0 - bulge, my), new Point(x0, y0 + round), true, false);   // left curve
            s.QuadraticBezierTo(new Point(x0, y0), new Point(x0 + round, y0), true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Bold darker corner sweeps that visibly taper the colored background toward its base — the
    /// M15 "kickstand" look. Each corner is a tall wedge climbing up the side and curving in to the bottom
    /// centre, filled with a vertical gradient (deepening toward the bottom) plus a bright bevel edge so it
    /// reads as depth. Uses a darkened FRAME colour, never black, so the even outer border is untouched.</summary>
    private static void TaperBottom(DrawingContext dc, Rect r, Color shade, double footY)
    {
        double rise = r.Height * 0.24;       // how high the corner wedge climbs the side
        double reach = r.Width * 0.46;       // how far it sweeps toward the centre
        var deep = Darken(shade, 0.15);
        foreach (int sx in new[] { 1, -1 })
        {
            double ex = sx == 1 ? r.X : r.Right;   // outer edge x for this corner
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(ex, footY - rise), true, true);
                s.BezierTo(new Point(ex + sx * 10, footY - rise * 0.35),
                           new Point(ex + sx * 70, footY),
                           new Point(ex + sx * reach, footY), true, false);
                s.LineTo(new Point(ex, footY), true, false);
            }
            g.Freeze();
            // Vertical gradient: soft where it meets the frame, deep and opaque at the base.
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(70, shade.R, shade.G, shade.B), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(210, deep.R, deep.G, deep.B), 0.55));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(240, deep.R, deep.G, deep.B), 1));
            grad.Freeze();
            dc.DrawGeometry(grad, FrozenPen(Color.FromArgb(90, 255, 255, 255), 1.4), g);
        }
    }

    private static double TextureStrength(TemplateSpec spec)
    {
        if (string.Equals(spec.Texture, "none", StringComparison.OrdinalIgnoreCase)) return 0;
        return Math.Clamp(spec.TextureStrength, 0, 3);
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
        // Drop shadow (bottom-LEFT) gives the panel a raised, 3D look with light from the upper-right.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)), null,
            new Rect(rect.X - 4, rect.Y + 6, rect.Width, rect.Height), radius, radius);
        dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);
        // top highlight + bottom shade for a stronger bevel
        dc.DrawLine(new Pen(new SolidColorBrush(Lighten(panelColor, 0.55)), 1.8),
            new Point(rect.X + radius, rect.Y + 3), new Point(rect.Right - radius, rect.Y + 3));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1.4),
            new Point(rect.X + radius, rect.Bottom - 2.5), new Point(rect.Right - radius, rect.Bottom - 2.5));
    }

    /// <summary>Little 3D "rivet" studs at a panel's corners — bits that stick out for style.</summary>
    private static void DrawStuds(DrawingContext dc, Rect rect, Color studColor, Color edge)
    {
        foreach (var p in new[]
        {
            new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y),
            new Point(rect.X, rect.Bottom), new Point(rect.Right, rect.Bottom),
        })
        {
            // shadow bottom-left, highlight upper-right (light from the upper-right)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new Point(p.X - 2, p.Y + 2.5), 6.5, 6.5);
            dc.DrawEllipse(new SolidColorBrush(studColor), new Pen(new SolidColorBrush(edge), 2), p, 6.5, 6.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), null, new Point(p.X + 1.7, p.Y - 1.9), 2.0, 2.0);
        }
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

    private static Rect ToRect(Region r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));

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
