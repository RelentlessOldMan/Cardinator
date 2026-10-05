using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

public class BatchSheetTests
{
    private static List<CardModel> SampleSet(string template) => new()
    {
        new() { Name = "Alpha", ManaCost = "{R}", TypeLine = "Instant", RulesText = "Deal 3 damage.", TemplateName = template },
        new() { Name = "Beta", ManaCost = "{2}{G}", TypeLine = "Creature — Bear", Power = "3", Toughness = "3", TemplateName = template },
        new() { Name = "Gamma", ManaCost = "{2}{U}{U}", TypeLine = "Legendary Planeswalker — Gamma", Loyalty = "4", RulesText = "+1: Draw.\n-3: Bounce.", TemplateName = template },
    };

    [Fact]
    public void ExportAll_WritesBackFacePng_ForDoubleFacedCard()   // DFC
        => TestHelpers.RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cardinator_dfc_batch_" + Guid.NewGuid().ToString("N"));
            try
            {
                var templates = new TemplateService().LoadAll();
                var cards = new List<CardModel>
                {
                    new()
                    {
                        Name = "Werewolf", TypeLine = "Creature — Human", Power = "2", Toughness = "2",
                        TemplateName = templates[0].Name, DfcStyle = "sunmoon",
                        BackFace = new CardModel { Name = "Nightform", TypeLine = "Creature — Werewolf", Power = "4", Toughness = "4" },
                    },
                };

                var result = BatchService.ExportAll(cards, templates, dir, new SymbolService());

                Assert.Equal(1, result.Exported);
                var files = Directory.GetFiles(dir, "*.png").Select(Path.GetFileName).ToList();
                Assert.Contains(files, f => f!.EndsWith("_werewolf.png"));
                Assert.Contains(files, f => f!.EndsWith("_werewolf-back.png"));   // the back face too
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

    [Fact]
    public void ExportAll_WritesOnePngPerCard_AtFullResolution()
        => TestHelpers.RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cardinator_batch_" + Guid.NewGuid().ToString("N"));
            try
            {
                var templates = new TemplateService().LoadAll();
                var symbols = new SymbolService();
                var cards = SampleSet(templates[0].Name);

                var result = BatchService.ExportAll(cards, templates, dir, symbols);

                Assert.Equal(3, result.Exported);
                Assert.Empty(result.Errors);
                var files = Directory.GetFiles(dir, "*.png");
                Assert.Equal(3, files.Length);

                var frame = BitmapFrame.Create(new Uri(files[0]));
                Assert.Equal(1500, frame.PixelWidth);
                Assert.Equal(2100, frame.PixelHeight);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

    [Fact]
    public void Sheet_Compose_TenCards_TwoLetterPages()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            var cards = Enumerable.Range(1, 10).Select(i => new CardModel
            {
                Name = "Card " + i,
                TypeLine = "Creature — Test",
                Power = "1",
                Toughness = "1",
                TemplateName = templates[0].Name,
            }).ToList();

            var pages = SheetExporter.Compose(cards, templates, symbols, PageSpec.Letter).ToList();

            Assert.Equal(2, pages.Count);                       // 9 per page -> 2 pages
            Assert.Equal(2550, pages[0].PixelWidth);
            Assert.Equal(3300, pages[0].PixelHeight);
            Assert.True(Math.Abs(pages[0].DpiX - 300) < 0.5);
            Assert.True(TestHelpers.HasContent(pages[0]));
        });

    [Fact]
    public void Sheet_DoubleSided_InterleavesFrontAndBackPages()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            var cards = SampleSet(templates[0].Name);   // 3 cards -> 1 front page
            var back = BackRenderer.Render(supersample: 1);

            var pages = SheetExporter.ComposeDoubleSided(cards, templates, symbols, PageSpec.Letter, back).ToList();

            Assert.Equal(2, pages.Count);                       // 1 front + 1 back
            Assert.Equal(2550, pages[0].PixelWidth);
            Assert.True(TestHelpers.HasContent(pages[0]));      // fronts
            Assert.True(TestHelpers.HasContent(pages[1]));      // backs
        });

    [Fact]
    public void Sheet_DoubleSided_MirrorsBackColumns()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            // One card fills front slot 0 (top-LEFT). Its back must land in the top-RIGHT slot so a
            // long-edge flip lines them up.
            var cards = new List<CardModel> { new() { Name = "Solo", TypeLine = "Creature — Test", Power = "1", Toughness = "1", TemplateName = templates[0].Name } };
            var back = BackRenderer.Render(supersample: 1);

            var pages = SheetExporter.ComposeDoubleSided(cards, templates, symbols, PageSpec.Letter, back).ToList();
            var backPage = pages[1];

            // Cell centers: 3 cols, CardW=750, gridW=2250, marginX=150, marginY=75, CardH=1050.
            var topLeft = PixelAt(backPage, 150 + 375, 75 + 525);
            var topRight = PixelAt(backPage, 150 + 2 * 750 + 375, 75 + 525);

            Assert.True(NearWhite(topLeft));    // front was left → back-left must be blank
            Assert.False(NearWhite(topRight));  // back art mirrored to the right
        });

    [Fact]
    public void Sheet_DoubleSided_PrintsADoubleFacedCardsRealBack_NotTheGenericOne()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var symbols = new SymbolService();
            // One double-faced card in front slot 0 → its REAL back face belongs in the mirrored slot,
            // where a single-faced card would get the shared card back instead.
            var dfc = new CardModel
            {
                Name = "Daybound", TypeLine = "Creature — Human", Power = "1", Toughness = "1",
                TemplateName = templates[0].Name,
                BackFace = new CardModel
                {
                    Name = "Nightbound", TypeLine = "Creature — Werewolf", Power = "4", Toughness = "4",
                    TemplateName = templates[0].Name,
                },
            };
            var plain = new CardModel { Name = "Plain", TypeLine = "Instant", TemplateName = templates[0].Name };
            var back = BackRenderer.Render(supersample: 1);

            var pages = SheetExporter.ComposeDoubleSided(new List<CardModel> { dfc, plain }, templates, symbols,
                PageSpec.Letter, back).ToList();
            var backPage = pages[1];

            // 3 cols: front slot 0 (left) mirrors to the right-hand column, slot 1 (middle) stays middle.
            var dfcBackSlot = PixelAt(backPage, 150 + 2 * 750 + 375, 75 + 525);
            var genericSlot = PixelAt(backPage, 150 + 750 + 375, 75 + 525);

            Assert.False(NearWhite(dfcBackSlot));
            Assert.False(NearWhite(genericSlot));
            // The real back face is a rendered CARD, so it cannot look like the generic card back.
            Assert.False(dfcBackSlot.SequenceEqual(genericSlot),
                "the double-faced card's slot shows the generic back instead of its own back face");
        });

    private static byte[] PixelAt(BitmapSource bs, int x, int y)
    {
        var c = new CroppedBitmap(bs, new Int32Rect(x, y, 1, 1));
        var p = new byte[4];
        c.CopyPixels(p, 4, 0);
        return p;   // BGRA
    }

    private static bool NearWhite(byte[] p) => p[0] > 240 && p[1] > 240 && p[2] > 240;

    [Fact]
    public void Sheet_Compose_A4_HasExpectedDimensions()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var pages = SheetExporter.Compose(
                new List<CardModel> { SampleCards.Blank(templates[0].Name) },
                templates, new SymbolService(), PageSpec.A4).ToList();

            Assert.Single(pages);
            Assert.Equal(2480, pages[0].PixelWidth);
            Assert.Equal(3508, pages[0].PixelHeight);
        });
}
