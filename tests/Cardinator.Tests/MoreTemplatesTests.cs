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

    [Fact]
    public void FrameGenerator_HonorsStyleKnobs()
    {
        var spec = BuiltInTemplates.All().First(s => !s.FullArt);
        var withEmb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frm-emb-{Guid.NewGuid():N}.png");
        var noEmb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frm-plain-{Guid.NewGuid():N}.png");
        try
        {
            spec.Embellishments = true; spec.CornerRadius = 30;
            FrameGenerator.Generate(spec, withEmb);

            spec.Embellishments = false; spec.CornerRadius = 0;
            FrameGenerator.Generate(spec, noEmb);

            var a = System.IO.File.ReadAllBytes(withEmb);
            var b = System.IO.File.ReadAllBytes(noEmb);
            Assert.True(a.Length > 0 && b.Length > 0);
            Assert.False(a.AsSpan().SequenceEqual(b), "toggling embellishments/corners should change the frame");
        }
        finally
        {
            try { System.IO.File.Delete(withEmb); } catch { }
            try { System.IO.File.Delete(noEmb); } catch { }
        }
    }

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
