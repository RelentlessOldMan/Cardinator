using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// End-to-end render checks: build templates, render cards, and assert the output is the expected
/// size and actually has content drawn. Rendering needs an STA thread (RenderTargetBitmap).
/// </summary>
public class RendererSmokeTests
{
    [Fact]
    public void Samples_RenderToExpectedSize_WithContent()
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            Assert.NotEmpty(templates);
            var renderer = new CardRenderer(new SymbolService());
            var template = templates[0];

            foreach (var card in SampleCards.All(template.Name))
            {
                var bmp = renderer.RenderToBitmap(card, template, supersample: 1);
                Assert.Equal(750, bmp.PixelWidth);
                Assert.Equal(1050, bmp.PixelHeight);
                Assert.True(HasContent(bmp), $"'{card.Name}' rendered blank");
            }
        });

    [Fact]
    public void Supersample_ScalesDimensions()
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var renderer = new CardRenderer(new SymbolService());
            var bmp = renderer.RenderToBitmap(SampleCards.Blank(templates[0].Name), templates[0], supersample: 2);
            Assert.Equal(1500, bmp.PixelWidth);
            Assert.Equal(2100, bmp.PixelHeight);
        });

    [Theory]
    [MemberData(nameof(SpecialLayoutCards))]
    public void SpecialLayouts_Render_WithContent(string label, CardModel card)
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var renderer = new CardRenderer(new SymbolService());
            card.TemplateName = templates[0].Name;
            var bmp = renderer.RenderToBitmap(card, templates[0], supersample: 1);
            Assert.Equal(750, bmp.PixelWidth);
            Assert.True(HasContent(bmp), $"{label} rendered blank");
        });

    public static IEnumerable<object[]> SpecialLayoutCards()
    {
        yield return new object[] { "planeswalker", new CardModel
        {
            Name = "Test Walker", ManaCost = "{2}{U}{U}", TypeLine = "Legendary Planeswalker — Test",
            Loyalty = "4", RulesText = "+1: Draw a card.\n-2: Deal 2 damage.\n-7: Win the game.",
        }};
        yield return new object[] { "saga", new CardModel
        {
            Name = "Test Saga", ManaCost = "{1}{W}{W}", TypeLine = "Enchantment — Saga",
            RulesText = "(Reminder text here.)\nI, II — Make a token.\nIII — Buff your team.",
        }};
        yield return new object[] { "adventure", new CardModel
        {
            Name = "Test Giant", ManaCost = "{2}{R}", TypeLine = "Creature — Giant", Power = "4", Toughness = "3",
            RulesText = "Whenever this becomes a target, deal 2 damage.",
            AdventureName = "Smash", AdventureCost = "{1}{R}", AdventureType = "Instant — Adventure",
            AdventureText = "Smash deals 2 damage to any target.", Layout = "adventure",
        }};
    }

    // --- helpers ------------------------------------------------------------

    private static bool HasContent(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a > 10 && (r < 200 || g < 200 || b < 200)) return true;  // any non-white, visible pixel
        }
        return false;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
