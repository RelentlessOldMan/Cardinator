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

    /// <summary>Composes single-sided pages, LAZILY: each page is yielded as it is rendered, so
    /// <see cref="Save"/> can write it and let it go. A full page is ~33 MB, so materializing them all
    /// would cost about 0.8 GB for a 100-card double-sided deck.</summary>
    public static IEnumerable<BitmapSource> Compose(
        IReadOnlyList<CardModel> cards,
        IReadOnlyList<Template> templates,
        SymbolService symbols,
        PageSpec page,
        IProgress<string>? progress = null)
    {
        // Validated eagerly (outside the iterator) so a bad page spec throws on the call, not on the
        // first enumeration — the caller's try/catch would otherwise miss it.
        Validate(templates, page);
        return Iterate();

        IEnumerable<BitmapSource> Iterate()
        {
            var ctx = new Layout(templates, symbols, page);
            int perPage = page.PerPage;
            int pageCount = (cards.Count + perPage - 1) / perPage;

            for (int p = 0; p < pageCount; p++)
            {
                int pageStart = p * perPage;
                yield return ctx.RenderPage(slot =>
                {
                    int index = pageStart + slot;
                    return index >= cards.Count ? null : ctx.RenderCard(cards[index]);
                }, mirror: false);
                progress?.Report($"Composed page {p + 1}/{pageCount}");
            }
        }
    }

    /// <summary>
    /// Composes double-sided sheets: each front page is immediately followed by a back page whose columns
    /// are mirrored, so a long-edge (flip-horizontal) duplex print lines the backs up behind the fronts.
    /// One shared card back is used for every card. Pages come out front, back, front, back, …
    /// </summary>
    public static IEnumerable<BitmapSource> ComposeDoubleSided(
        IReadOnlyList<CardModel> cards,
        IReadOnlyList<Template> templates,
        SymbolService symbols,
        PageSpec page,
        BitmapSource back,
        IProgress<string>? progress = null)
    {
        Validate(templates, page);
        return Iterate();

        IEnumerable<BitmapSource> Iterate()
        {
            var ctx = new Layout(templates, symbols, page);
            int perPage = page.PerPage;
            int pageCount = (cards.Count + perPage - 1) / perPage;

            for (int p = 0; p < pageCount; p++)
            {
                int pageStart = p * perPage;
                yield return ctx.RenderPage(slot =>
                {
                    int index = pageStart + slot;
                    return index >= cards.Count ? null : ctx.RenderCard(cards[index]);
                }, mirror: false);

                yield return ctx.RenderPage(slot =>
                {
                    int index = pageStart + slot;
                    if (index >= cards.Count) return null;
                    // A double-faced card prints its REAL back face; everything else gets the generic back.
                    return cards[index].BackFace is { } bf ? ctx.RenderCard(bf) : back;
                }, mirror: true);

                progress?.Report($"Composed sheet {p + 1}/{pageCount} (front + back)");
            }
        }
    }

    private static void Validate(IReadOnlyList<Template> templates, PageSpec page)
    {
        if (templates.Count == 0) throw new InvalidOperationException("No templates available to compose a sheet.");
        if (page.Cols * CardW > page.Width || page.Rows * CardH > page.Height)
            throw new InvalidOperationException($"{page.Cols}x{page.Rows} cards don't fit on a {page.Width}x{page.Height} page.");
    }

    /// <summary>The shared page geometry + card-to-template resolution, so single-sided and double-sided
    /// sheets lay out through exactly ONE code path (they used to be two loops that had to stay identical).</summary>
    private sealed class Layout
    {
        private readonly CardRenderer _renderer;
        private readonly Dictionary<string, Template> _byName;
        private readonly Template _fallback;
        private readonly PageSpec _page;
        private readonly double _gridW, _gridH, _marginX, _marginY;

        public Layout(IReadOnlyList<Template> templates, SymbolService symbols, PageSpec page)
        {
            _renderer = new CardRenderer(symbols);
            _byName = templates.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());   // first wins; never throws on dup names
            _fallback = templates[0];
            _page = page;
            _gridW = page.Cols * CardW;
            _gridH = page.Rows * CardH;
            _marginX = (page.Width - _gridW) / 2;
            _marginY = (page.Height - _gridH) / 2;
        }

        public BitmapSource RenderCard(CardModel card)
        {
            var template = (card.TemplateName is { Length: > 0 } n && _byName.TryGetValue(n, out var t)) ? t : _fallback;
            return _renderer.RenderToBitmap(card, template, supersample: 1);
        }

        /// <summary>Lays out one page from a per-slot image function, optionally mirroring columns (for backs,
        /// so a long-edge duplex print lines them up behind the fronts).</summary>
        public BitmapSource RenderPage(Func<int, BitmapSource?> slotImage, bool mirror)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _page.Width, _page.Height));
                for (int slot = 0; slot < _page.PerPage; slot++)
                {
                    var img = slotImage(slot);
                    if (img == null) continue;
                    int col = slot % _page.Cols, row = slot / _page.Cols;
                    int drawCol = mirror ? _page.Cols - 1 - col : col;
                    double x = _marginX + drawCol * CardW, y = _marginY + row * CardH;
                    dc.DrawImage(img, new Rect(x, y, CardW, CardH));
                }
                if (_page.CutMarks) DrawCutMarks(dc, _page, _marginX, _marginY, _gridW, _gridH);
            }
            // Render at 96 DPI so 1 drawing unit == 1 pixel, then stamp the real print DPI onto the image
            // so it prints at true card size (2.5" x 3.5").
            var rtb = new RenderTargetBitmap(_page.Width, _page.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return StampDpi(rtb, _page.Dpi);
        }
    }

    /// <summary>Saves each page as it arrives. Takes a lazy sequence so only one full-size page is ever held
    /// in memory, however many pages the deck makes.</summary>
    public static List<string> Save(IEnumerable<BitmapSource> pages, string outDir, string baseName = "sheet")
    {
        Directory.CreateDirectory(outDir);
        var paths = new List<string>();
        int i = 0;
        foreach (var bmp in pages)
        {
            var path = Path.Combine(outDir, $"{baseName}_page_{++i:00}.png");
            CardExporter.SavePng(bmp, path);
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
