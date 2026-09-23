using System;
using System.Threading;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Covers the additional built-in frames and the decorative card back.</summary>
public class MoreTemplatesTests
{
    [Theory]
    [InlineData("Midnight")]
    [InlineData("Parchment")]
    [InlineData("Sunset Orange")]
    [InlineData("Full Art")]
    public void NewBuiltInTemplate_IsAvailable(string name)
    {
        var templates = new TemplateService().LoadAll();
        Assert.Contains(templates, t => t.Name == name);
    }

    [Fact]
    public void EveryBuiltInTemplate_RendersWithContent()
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var renderer = new CardRenderer(new SymbolService());
            var card = new CardModel
            {
                Name = "Coverage", ManaCost = "{1}{G}", TypeLine = "Creature — Test",
                RulesText = "Trample", Power = "2", Toughness = "2",
            };
            foreach (var t in templates)
            {
                card.TemplateName = t.Name;
                var bmp = renderer.RenderToBitmap(card, t, 1);
                Assert.Equal(t.Spec.CanvasWidth, bmp.PixelWidth);
                Assert.True(HasContent(bmp), $"template '{t.Name}' rendered blank");
            }
        });

    [Fact]
    public void CardBack_RendersAtCardSize_WithContent()
        => RunSta(() =>
        {
            var bmp = BackRenderer.Render("CARDINATOR", supersample: 1);
            Assert.Equal(750, bmp.PixelWidth);
            Assert.Equal(1050, bmp.PixelHeight);
            Assert.True(HasContent(bmp));
        });

    private static bool HasContent(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 10 && (pixels[i + 2] < 200 || pixels[i + 1] < 200 || pixels[i] < 200)) return true;
        return false;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
