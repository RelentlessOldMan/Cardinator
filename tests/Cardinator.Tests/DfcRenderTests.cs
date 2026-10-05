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

    private static CardModel Dfc() => new() { Name = "Front", DfcStyle = "sunmoon", BackFace = new CardModel { Name = "Back" } };

    [Fact]
    public void Badge_IsKeyedToTheTitlePanel_NotTheCardCorner()
    {
        // The same frame with its title bar lower down (e.g. a decorative band on top) moves the badge with it,
        // and a thicker border alone doesn't move it at all.
        var stock = new TemplateSpec { TitleBar = new Region { X = 48, Y = 46, W = 654, H = 60 } };
        var low = new TemplateSpec { TitleBar = new Region { X = 48, Y = 96, W = 654, H = 60 } };
        var thick = new TemplateSpec { TitleBar = new Region { X = 48, Y = 46, W = 654, H = 60 }, BorderThickness = 60 };

        var a = CardRenderer.DfcBadge(Dfc(), stock)!.Value;
        var b = CardRenderer.DfcBadge(Dfc(), low)!.Value;
        var c = CardRenderer.DfcBadge(Dfc(), thick)!.Value;

        Assert.Equal(a.c.X, b.c.X, 3);
        Assert.Equal(a.c.Y + 50, b.c.Y, 3);
        Assert.Equal(a.c, c.c);
        Assert.InRange(b.c.Y, 96, 96 + 60);                     // centred within the panel's rows, not above it
    }

    [Fact]
    public void Badge_KeepsItsStockPosition_OnTheBuiltInFrames()
    {
        // Built-in frames looked right before the re-keying; on the standard bar (48,46) nothing moves.
        var b = CardRenderer.DfcBadge(Dfc(), new TemplateSpec { TitleBar = new Region { X = 48, Y = 46, W = 654, H = 60 }, BorderThickness = 28 })!.Value;
        Assert.Equal(62.5, b.c.X, 0);
        Assert.Equal(62.5, b.c.Y, 0);
    }

    [Fact]
    public void Badge_IsPushedInward_WhenThePanelHugsTheEdge()
    {
        // A title panel flush with the card edge would put half the badge off the card — push it on.
        var b = CardRenderer.DfcBadge(Dfc(), new TemplateSpec { TitleBar = new Region { X = 0, Y = 0, W = 750, H = 60 } })!.Value;
        Assert.True(b.c.X - b.r >= 12 - 0.01, "badge is closer than the minimum gap to the left edge");
        Assert.True(b.c.Y - b.r >= 12 - 0.01, "badge is closer than the minimum gap to the top edge");
    }

    [Fact]
    public void NoBadge_ForSingleFacedCards_OrStyleNone()
    {
        var spec = new TemplateSpec();
        Assert.Null(CardRenderer.DfcBadge(new CardModel { Name = "Plain", DfcStyle = "sunmoon" }, spec));
        var none = Dfc(); none.DfcStyle = "none";
        Assert.Null(CardRenderer.DfcBadge(none, spec));
    }
}
