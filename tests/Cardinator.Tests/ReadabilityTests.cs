using System.Windows.Media;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.23, from rendering awkward real cards on every frame: numbers that vanished into their badges (white on the
/// pale Parchment and Sealed Gate badges, near-black on Midnight's near-black P/T box), a basic land's big symbol drawn
/// over the sample frames' seal, and Sealed Gate's footer lost in its stone texture. Frames that already read keep their
/// look. Each test fails with its fix taken out. In the STA-window collection because some render.
/// </summary>
[Collection("STAWindows")]
public class ReadabilityTests
{
    private static Template Frame(string name) => new TemplateService().LoadAll().First(t => t.Name == name);

    private static bool IsWhite(Brush b) => b is SolidColorBrush s && s.Color == Colors.White;

    [Theory]
    [InlineData("Sealed Gate", false)]
    [InlineData("Parchment", false)]
    [InlineData("Gold Multicolor", true)]
    [InlineData("Alchemist's Steel", true)]
    [InlineData("Azure Modern", true)]
    [InlineData("Midnight", true)]
    public void BadgeNumbers_AreWhite_UnlessTheBadgeIsTooPaleForIt(string frame, bool white)
        => Assert.Equal(white, IsWhite(CardRenderer.BadgeInk(Frame(frame).Spec)));

    [Fact]
    public void SealedGatesLoyaltyBadges_DrawTheirNumbersInDarkInk() => TestHelpers.RunSta(() =>
    {
        // Two planeswalkers that differ only in one badge's number: the pixels that differ are that number's ink.
        var gate = Frame("Sealed Gate");
        var r = new CardRenderer(new SymbolService());
        byte[] Render(string cost) => TestHelpers.Pixels(r.RenderToBitmap(new CardModel
        {
            Name = "Walker", TypeLine = "Legendary Planeswalker — Test", RulesText = cost + ": Draw a card.", TemplateName = gate.Name,
        }, gate));
        var a = Render("+2");
        var b = Render("+7");
        int dark = 0, light = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) < 90) continue;
            int sum = a[i] + a[i + 1] + a[i + 2];
            if (sum < 250) dark++; else if (sum > 600) light++;
        }
        Assert.True(dark > 15 && dark > light, $"the badge's number is drawn light ({light} light vs {dark} dark pixels) on a pale badge");
    });

    [Fact]
    public void PtNumbers_KeepTheFramesInk_UnlessItVanishesOnTheBox()
    {
        Assert.Equal(Colors.White, CardRenderer.PtInk(Frame("Midnight").Spec));   // #141414 on a near-black box
        foreach (var name in new[] { "Alchemist's Steel", "Gold Multicolor", "Parchment", "Sealed Gate", "Planeswalker" })
        {
            var spec = Frame(name).Spec;
            Assert.Equal(TemplateSpec.ParseColor(spec.PtFont.Color), CardRenderer.PtInk(spec));
        }
    }

    [Fact]
    public void MidnightsPtBox_ShowsItsNumbersInLightInk() => TestHelpers.RunSta(() =>
    {
        var midnight = Frame("Midnight");
        var card = new CardModel { Name = "Beast", TypeLine = "Creature — Beast", Power = "4", Toughness = "4", TemplateName = midnight.Name };
        var box = CardRenderer.RulesAvoid(card, midnight.Spec.WithSubBorderApplied())!.Value;
        var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, midnight);
        var px = TestHelpers.Pixels(bmp);
        int light = 0;
        for (int y = (int)box.Top; y < (int)box.Bottom; y++)
            for (int x = (int)box.Left; x < (int)box.Right; x++)
            {
                int i = (y * bmp.PixelWidth + x) * 4;
                if (px[i] > 200 && px[i + 1] > 200 && px[i + 2] > 200) light++;
            }
        Assert.True(light > 60, $"only {light} light pixels in the P/T box — the numbers are still dark on dark");
    });

    [Fact]
    public void ABasicLandsBigSymbol_StaysOffTheFramesSeal() => TestHelpers.RunSta(() =>
    {
        var steel = Frame("Alchemist's Steel");
        var medallion = CardRenderer.BottomOrnament(steel, steel.Spec.WithSubBorderApplied())!.Value;
        var r = new CardRenderer(new SymbolService());
        CardModel Card(string type) => new() { Name = "Island", TypeLine = type, TemplateName = steel.Name };
        Assert.True(Card("Basic Land — Island").ShowBigLandSymbol);
        var blank = TestHelpers.Pixels(r.RenderToBitmap(Card("Basic Land"), steel));
        var bmp = r.RenderToBitmap(Card("Basic Land — Island"), steel);
        var px = TestHelpers.Pixels(bmp);
        int w = bmp.PixelWidth, inside = 0, elsewhere = 0;
        for (int y = 0; y < bmp.PixelHeight; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(px[i] - blank[i]) + Math.Abs(px[i + 1] - blank[i + 1]) + Math.Abs(px[i + 2] - blank[i + 2]) < 40) continue;
                if (medallion.Contains(new System.Windows.Point(x, y))) inside++; else elsewhere++;
            }
        Assert.True(elsewhere > 2000, "the land symbol didn't draw");
        Assert.Equal(0, inside);
    });

    [Fact]
    public void SealedGatesFooter_IsLightAndOutlined_OnItsStoneTexture()
    {
        var font = Frame("Sealed Gate").Spec.CreditFont;
        Assert.True(font.Shadow);
        var c = TemplateSpec.ParseColor(font.Color);
        Assert.True(c.R > 200 && c.G > 200 && c.B > 200, $"footer ink {font.Color} isn't light");
    }
}
