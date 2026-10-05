using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>DFC: the corner indicator actually draws, and the front (sun) differs from the back (moon).</summary>
public class DfcRenderTests
{
    private static CardModel Dfc(string style) => new()
    {
        Name = "Transformer", TypeLine = "Creature — Human", Power = "1", Toughness = "1", DfcStyle = style,
        BackFace = new CardModel { Name = "Transformer", TypeLine = "Creature — Human", Power = "1", Toughness = "1" },
    };

    private static byte[] Pixels(CardModel card)
    {
        var tpl = new TemplateService().LoadAll().First(t => !t.Spec.FullArt);
        var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, tpl, supersample: 1);
        var conv = bmp.Format == System.Windows.Media.PixelFormats.Bgra32
            ? bmp : new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[conv.PixelHeight * stride];
        conv.CopyPixels(px, stride, 0);
        return px;
    }

    [Fact]
    public void Indicator_ChangesTheRender()
        => TestHelpers.RunSta(() =>
        {
            var with = Pixels(Dfc("sunmoon"));
            var without = Pixels(Dfc("none"));
            Assert.NotEqual(with, without);   // the sun badge changed pixels in the corner
        });

    [Fact]
    public void FrontAndBack_DrawDifferentGlyphs()
        => TestHelpers.RunSta(() =>
        {
            var front = Dfc("sunmoon");
            var sun = Pixels(front);
            var moon = Pixels(front.BackFace!);   // IsBackFace is true → crescent moon
            Assert.NotEqual(sun, moon);
        });

    [Fact]
    public void Arrow_AlsoDraws_OnBothFaces()
        => TestHelpers.RunSta(() =>
        {
            var card = Dfc("arrow");
            Assert.NotEqual(Pixels(card), Pixels(Dfc("none")));                         // front: arrow vs none
            // The BACK must carry it too — the name promised this but only the front was ever checked.
            Assert.NotEqual(Pixels(card.BackFace!), Pixels(Dfc("none").BackFace!));

            // ...and unlike sun/moon, the arrow is the SAME glyph on both faces.
            var sunmoon = Dfc("sunmoon");
            Assert.NotEqual(Pixels(sunmoon), Pixels(sunmoon.BackFace!));
        });
}
