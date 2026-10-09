using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.22, from the same trial run: a planeswalker's or saga's long text ran under the seal the sample frames set into
/// the bottom of the text box. Ordinary rules text already stopped above it; these layouts now do too. Each case fails
/// with the fix taken out. In the STA-window collection because it renders.
/// </summary>
[Collection("STAWindows")]
public class FrameSealTests
{
    // --- long planeswalker / saga text and the frame's seal (Class goes through the same layout) ------------------------------------------

    [Theory]
    [InlineData("Legendary Planeswalker — Jace", "+2: Look at the top card of target player's library. You may put that card on the bottom of that player's library.\n0: Draw three cards, then put two cards from your hand on top of your library in any order.\n−1: Return target creature to its owner's hand.\n−12: Exile all cards from target player's library, then that player shuffles their hand into their library.")]
    [InlineData("Enchantment Land — Urza's Saga", "(As this Saga enters and after your draw step, add a lore counter. Sacrifice after III.)\nI — This Saga gains \"{T}: Add {C}.\"\nII — This Saga gains \"{2}, {T}: Create a 0/0 colorless Construct artifact creature token with 'This token gets +1/+1 for each artifact you control.'\"\nIII — Search your library for an artifact card with mana cost {0} or {1}, put it onto the battlefield, then shuffle.")]
    public void LongPlaneswalkerAndSagaText_StaysOffTheFramesSeal(string type, string rules) => TestHelpers.RunSta(() =>
    {
        var steel = new TemplateService().LoadAll().First(t => t.Name == "Alchemist's Steel");
        var medallion = CardRenderer.BottomOrnament(steel, steel.Spec.WithSubBorderApplied())!.Value;
        var r = new CardRenderer(new SymbolService());
        CardModel Card(string text) => new() { Name = "Long", TypeLine = type, RulesText = text, TemplateName = steel.Name };
        var blank = TestHelpers.Pixels(r.RenderToBitmap(Card(""), steel));
        var bmp = r.RenderToBitmap(Card(rules), steel);
        var px = TestHelpers.Pixels(bmp);
        int w = bmp.PixelWidth, inside = 0, elsewhere = 0;
        for (int y = 0; y < bmp.PixelHeight; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(px[i] - blank[i]) + Math.Abs(px[i + 1] - blank[i + 1]) + Math.Abs(px[i + 2] - blank[i + 2]) < 40) continue;
                if (medallion.Contains(new System.Windows.Point(x, y))) inside++; else elsewhere++;
            }
        Assert.True(elsewhere > 2000, "the rules text didn't draw");
        Assert.Equal(0, inside);
    });
}
