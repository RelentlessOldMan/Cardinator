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
    public static IReadOnlyList<ValidationIssue> Inspect(BitmapSource bmp, CardModel card, TemplateSpec templateSpec)
    {
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
        // Matched tightly so genuinely dark ART isn't mistaken for an unfilled window.
        bool NearArtBacking(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int i = y * stride + x * 4;
            return px[i] <= 18 && px[i + 1] <= 16 && px[i + 2] <= 16;   // B,G,R of (8,8,10) with a little tolerance
        }

        // --- border thickness on each side (measured at the mid-point, away from the rounded corners) ---
        double scale = w / (double)spec.CanvasWidth;
        double expected = System.Math.Max(0, spec.BorderThickness) * scale;
        if (expected >= 4)
        {
            int cx = w / 2, cy = h / 2;
            int top = 0; while (top < h && NearBlack(cx, top)) top++;
            int bot = 0; while (bot < h && NearBlack(cx, h - 1 - bot)) bot++;
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
