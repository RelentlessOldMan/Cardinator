using System.IO;
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

            var pages = SheetExporter.Compose(cards, templates, symbols, PageSpec.Letter);

            Assert.Equal(2, pages.Count);                       // 9 per page -> 2 pages
            Assert.Equal(2550, pages[0].PixelWidth);
            Assert.Equal(3300, pages[0].PixelHeight);
            Assert.True(Math.Abs(pages[0].DpiX - 300) < 0.5);
            Assert.True(TestHelpers.HasContent(pages[0]));
        });

    [Fact]
    public void Sheet_Compose_A4_HasExpectedDimensions()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var pages = SheetExporter.Compose(
                new List<CardModel> { SampleCards.Blank(templates[0].Name) },
                templates, new SymbolService(), PageSpec.A4);

            Assert.Single(pages);
            Assert.Equal(2480, pages[0].PixelWidth);
            Assert.Equal(3508, pages[0].PixelHeight);
        });
}
