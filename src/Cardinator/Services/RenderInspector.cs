using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Pixel-level checks on a rendered card, for the QA harness. Catches the visual bugs that geometry
/// alone can't: a black border that's missing/too thin on one side (the "border gone on the bottom"
/// bug), and an art window that came out blank because the art path was broken (the saga art bug).
/// </summary>
public static class RenderInspector
{
    /// <summary>Inspects what <paramref name="renderer"/> draws for a card. A split or aftermath card is two
    /// small plain cards, so each half is rendered and inspected on its own, with the template it's drawn with (the other half's issues labelled); anything
    /// else inspects <paramref name="rendered"/> (or a fresh render).</summary>
    public static IReadOnlyList<ValidationIssue> InspectCard(CardRenderer renderer, CardModel card, Template template,
        BitmapSource? rendered = null)
    {
        if (!card.IsSplit) return Inspect(rendered ?? renderer.RenderToBitmap(card, template), card, template.Spec);
        var g = CardRenderer.SplitGeometry(card, template);
        var (a, b) = (g.First, g.Other);
        return Inspect(renderer.RenderToBitmap(a.Card, a.Template), a.Card, a.Template.Spec)
            .Concat(Inspect(renderer.RenderToBitmap(b.Card, b.Template), b.Card, b.Template.Spec)
                .Select(i => new ValidationIssue(i.Severity, i.Code, "Other half: " + i.Message, i.Field)))
            .ToList();
    }

    public static IReadOnlyList<ValidationIssue> Inspect(BitmapSource bmp, CardModel card, TemplateSpec templateSpec)
    {
        // A split card's render is two small cards turned sideways — not the layout these checks measure.
        // InspectCard inspects its halves instead.
        if (card.IsSplit) return System.Array.Empty<ValidationIssue>();

        var issues = new List<ValidationIssue>();
        var spec = templateSpec.WithSubBorderApplied();

        var conv = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        conv.CopyPixels(px, stride, 0);

        bool NearBlack(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int i = y * stride + x * 4;
            return px[i] < 40 && px[i + 1] < 40 && px[i + 2] < 40;
        }
        bool NearWhite(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int i = y * stride + x * 4;
            return px[i] > 240 && px[i + 1] > 240 && px[i + 2] > 240;
        }
        // The dark fill the renderer paints into an empty/failed art window (CardBacking = RGB 8,8,10).
        // Matched as a TIGHT BAND around that exact color — not just "dark" — so genuinely dark art (and
        // crucially pure black, 0,0,0) isn't mistaken for an unfilled window. The backing is a flat fill with
        // no supersampling dither in the interior, so an exact-ish match is safe.
        bool NearArtBacking(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int i = y * stride + x * 4;
            return px[i]     is >= 3 and <= 13    // B ≈ 8
                && px[i + 1] is >= 3 and <= 13    // G ≈ 8
                && px[i + 2] is >= 5 and <= 15;   // R ≈ 10
        }

        // --- border thickness on each side (measured at the mid-point, away from the rounded corners) ---
        double scale = w / (double)spec.CanvasWidth;
        double expected = System.Math.Max(0, spec.BorderThickness) * scale;
        if (expected >= 4)
        {
            int cx = w / 2, cy = h / 2;

            // The bottom border can't be measured down the middle alone when the footer RIDES on it: that
            // light "001/99 — Artist" text sits inside the rim and stops the upward scan early, flagging a
            // perfectly good card. So measure at several columns — the gap left of the footer (CardRenderer
            // insets it by border+12), the middle, and out to the right past most footer lines — and keep the
            // thickest. Text can only SHORTEN a reading, and a genuinely missing border is thin at every one,
            // so this never hides a real problem; a single column could land in a rounded corner instead.
            int BottomRun(int x) { int n = 0; while (n < h && NearBlack(x, h - 1 - n)) n++; return n; }
            int bot = BottomRun(cx);
            if ((spec.FooterPlacement ?? "").Trim().Equals("border", System.StringComparison.OrdinalIgnoreCase))
                foreach (var x in new[] { (int)((spec.BorderThickness + 6) * scale), w / 4, (int)(w * 0.75), (int)(w * 0.88) })
                    bot = System.Math.Max(bot, BottomRun(System.Math.Clamp(x, 1, w - 2)));

            int top = 0; while (top < h && NearBlack(cx, top)) top++;
            int left = 0; while (left < w && NearBlack(left, cy)) left++;
            int right = 0; while (right < w && NearBlack(w - 1 - right, cy)) right++;

            double minOk = expected * 0.5;   // flag a side only when it's clearly too thin/missing
            void Side(string name, int t)
            {
                if (t < minOk)
                    issues.Add(new(IssueSeverity.Error, "border-thin",
                        $"Black border on the {name} is missing or too thin ({t / scale:F0}px vs {spec.BorderThickness:F0}px expected)."));
            }
            Side("top", top); Side("bottom", bot); Side("left", left); Side("right", right);
        }

        // --- empty art window (art assigned but the window rendered blank) ---
        // "Blank" is the window left at its empty state: the old white, OR the dark CardBacking the renderer
        // now paints so uncovered areas never flash white. Either way means the art didn't draw.
        if (!string.IsNullOrWhiteSpace(card.ArtPath) && spec.ArtWindow != null && spec.ArtWindow.W > 4)
        {
            var aw = spec.ArtWindow;
            int x0 = (int)((aw.X + aw.W * 0.25) * scale), x1 = (int)((aw.X + aw.W * 0.75) * scale);
            int y0 = (int)((aw.Y + aw.H * 0.25) * scale), y1 = (int)((aw.Y + aw.H * 0.75) * scale);
            int total = 0, empty = 0;
            for (int y = y0; y <= y1; y += System.Math.Max(1, (y1 - y0) / 12))
                for (int x = x0; x <= x1; x += System.Math.Max(1, (x1 - x0) / 12))
                { total++; if (NearWhite(x, y) || NearArtBacking(x, y)) empty++; }
            if (total > 0 && empty >= total * 0.92)
                issues.Add(new(IssueSeverity.Error, "art-blank", "Art is assigned but the art window rendered blank (art failed to load?)."));
        }

        return issues;
    }
}
