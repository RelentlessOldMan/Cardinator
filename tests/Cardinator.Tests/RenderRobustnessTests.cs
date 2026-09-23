using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

public class RenderRobustnessTests
{
    private static (CardRenderer renderer, Template template) Setup()
    {
        var templates = new TemplateService().LoadAll();
        return (new CardRenderer(new SymbolService()), templates[0]);
    }

    [Fact]
    public void SameCard_RendersIdenticalBytes_Twice()
        => TestHelpers.RunSta(() =>
        {
            var (renderer, template) = Setup();
            var card = new CardModel
            {
                Name = "Determinism Test",
                ManaCost = "{2}{R}{W}",
                TypeLine = "Legendary Creature — Test",
                RulesText = "Haste\n{T}: Deal 1 damage. (This is reminder text.)",
                Power = "3",
                Toughness = "3",
                TemplateName = template.Name,
            };

            var a = TestHelpers.Pixels(renderer.RenderToBitmap(card, template, 1));
            var b = TestHelpers.Pixels(renderer.RenderToBitmap(card, template, 1));
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.AsSpan().SequenceEqual(b), "render was not deterministic");
        });

    [Fact]
    public void EmptyCard_DoesNotThrow_AndIsCorrectSize()
        => TestHelpers.RunSta(() =>
        {
            var (renderer, template) = Setup();
            var bmp = renderer.RenderToBitmap(new CardModel { TemplateName = template.Name }, template, 1);
            Assert.Equal(750, bmp.PixelWidth);
            Assert.Equal(1050, bmp.PixelHeight);
        });

    [Fact]
    public void VeryLongTextAndUnicode_DoesNotThrow()
        => TestHelpers.RunSta(() =>
        {
            var (renderer, template) = Setup();
            var card = new CardModel
            {
                Name = "A Ridiculously Long Legendary Name That Should Auto-Shrink Nicely — 日本語 ★",
                ManaCost = "{10}{W}{U}{B}{R}{G}",
                TypeLine = "Legendary Artifact Creature — Very Long Type Line Of Doom",
                RulesText = string.Join("\n", Enumerable.Repeat(
                    "This is a very long ability with {T} symbols and (reminder text) that repeats. 日本語テキスト.", 8)),
                Power = "99",
                Toughness = "99",
                TemplateName = template.Name,
            };
            var bmp = renderer.RenderToBitmap(card, template, 1);
            Assert.Equal(750, bmp.PixelWidth);
            Assert.True(TestHelpers.HasContent(bmp));
        });

    [Fact]
    public void DegenerateTemplate_BadFontsAndZeroSizes_DoesNotThrow()
        => TestHelpers.RunSta(() =>
        {
            var (renderer, template) = Setup();
            var spec = template.Spec;

            // Simulate a hand-broken template.json: empty font family + zero/invalid sizes.
            spec.TitleFont.Family = "";
            spec.TitleFont.Size = 0;
            spec.TypeFont.Family = "   ";
            spec.RulesFont.Size = -5;
            spec.RulesFont.Family = "This Font Does Not Exist 12345";

            var card = new CardModel
            {
                Name = "Broken Fonts",
                ManaCost = "{1}{G}",
                TypeLine = "Creature — Test",
                RulesText = "Trample\n{T}: Draw a card.",
                Power = "2",
                Toughness = "2",
                TemplateName = template.Name,
            };

            var bmp = renderer.RenderToBitmap(card, template, 1);   // must not throw
            Assert.Equal(750, bmp.PixelWidth);
            Assert.True(TestHelpers.HasContent(bmp));
        });

    [Fact]
    public void HybridAndPhyrexianMana_Render_WithContent()
        => TestHelpers.RunSta(() =>
        {
            var (renderer, template) = Setup();
            var card = new CardModel
            {
                Name = "Hybrid Test",
                ManaCost = "{B/G}{U/P}{2/W}",
                TypeLine = "Creature — Test",
                RulesText = "{G/W}: Do a thing. {T}, {W/U}: Do another.",
                Power = "2",
                Toughness = "2",
                TemplateName = template.Name,
            };
            var bmp = renderer.RenderToBitmap(card, template, 1);
            Assert.True(TestHelpers.HasContent(bmp));
        });
}
