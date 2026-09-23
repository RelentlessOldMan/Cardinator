using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>Page layout for a print sheet. Cards render at their native 750x1050 (= 2.5"x3.5" @ 300 DPI).</summary>
public sealed class PageSpec
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Dpi { get; init; } = 300;
    public int Cols { get; init; } = 3;
    public int Rows { get; init; } = 3;
    public bool CutMarks { get; init; } = true;

    public int PerPage => Cols * Rows;

    /// <summary>US Letter (8.5" x 11") at 300 DPI.</summary>
    public static PageSpec Letter => new() { Width = 2550, Height = 3300 };

    /// <summary>A4 (210 x 297 mm) at 300 DPI.</summary>
    public static PageSpec A4 => new() { Width = 2480, Height = 3508 };
}

/// <summary>
/// Composes cards into printable pages (default 3x3 real-size cards on Letter/A4 at 300 DPI, with
/// corner cut marks) and saves one PNG per page — ready to print and cut at home.
/// </summary>
public static class SheetExporter
{
    private const double CardW = 750, CardH = 1050;

    public static List<BitmapSource> Compose(
        IReadOnlyList<CardModel> cards,
        IReadOnlyList<Template> templates,
        SymbolService symbols,
        PageSpec page,
        IProgress<string>? progress = null)
    {
        var renderer = new CardRenderer(symbols);
        var byName = templates.ToDictionary(t => t.Name, t => t);
        var fallback = templates[0];

        double gridW = page.Cols * CardW, gridH = page.Rows * CardH;
        double marginX = (page.Width - gridW) / 2, marginY = (page.Height - gridH) / 2;

        var pages = new List<BitmapSource>();
        int perPage = page.PerPage;
        int pageCount = (cards.Count + perPage - 1) / perPage;

        for (int p = 0; p < pageCount; p++)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, page.Width, page.Height));

                for (int slot = 0; slot < perPage; slot++)
                {
                    int index = p * perPage + slot;
                    if (index >= cards.Count) break;

                    int col = slot % page.Cols, row = slot / page.Cols;
                    double x = marginX + col * CardW, y = marginY + row * CardH;

                    var card = cards[index];
                    var template = (card.TemplateName is { Length: > 0 } n && byName.TryGetValue(n, out var t)) ? t : fallback;
                    var bmp = renderer.RenderToBitmap(card, template, supersample: 1);
                    dc.DrawImage(bmp, new Rect(x, y, CardW, CardH));
                }

                if (page.CutMarks)
                    DrawCutMarks(dc, page, marginX, marginY, gridW, gridH);
            }

            // Render at 96 DPI so 1 drawing unit == 1 pixel, then stamp the real print DPI
            // onto the image so it prints at true card size (2.5" x 3.5").
            var rtb = new RenderTargetBitmap(page.Width, page.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            pages.Add(StampDpi(rtb, page.Dpi));
            progress?.Report($"Composed page {p + 1}/{pageCount}");
        }

        return pages;
    }

    public static List<string> Save(IReadOnlyList<BitmapSource> pages, string outDir, string baseName = "sheet")
    {
        Directory.CreateDirectory(outDir);
        var paths = new List<string>();
        for (int i = 0; i < pages.Count; i++)
        {
            var path = Path.Combine(outDir, $"{baseName}_page_{i + 1:00}.png");
            CardExporter.SavePng(pages[i], path);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Re-wraps rendered pixels with the given DPI metadata (content unchanged).</summary>
    private static BitmapSource StampDpi(RenderTargetBitmap rtb, double dpi)
    {
        int stride = rtb.PixelWidth * 4;
        var pixels = new byte[rtb.PixelHeight * stride];
        rtb.CopyPixels(pixels, stride, 0);
        var bs = BitmapSource.Create(rtb.PixelWidth, rtb.PixelHeight, dpi, dpi,
            PixelFormats.Pbgra32, null, pixels, stride);
        bs.Freeze();
        return bs;
    }

    private static void DrawCutMarks(DrawingContext dc, PageSpec page, double marginX, double marginY, double gridW, double gridH)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1.5);
        double len = Math.Min(Math.Min(marginX, marginY), 40);
        if (len <= 2) return;

        for (int c = 0; c <= page.Cols; c++)
        {
            double x = marginX + c * CardW;
            dc.DrawLine(pen, new Point(x, marginY - len), new Point(x, marginY));
            dc.DrawLine(pen, new Point(x, marginY + gridH), new Point(x, marginY + gridH + len));
        }
        for (int r = 0; r <= page.Rows; r++)
        {
            double y = marginY + r * CardH;
            dc.DrawLine(pen, new Point(marginX - len, y), new Point(marginX, y));
            dc.DrawLine(pen, new Point(marginX + gridW, y), new Point(marginX + gridW + len, y));
        }
    }
}
