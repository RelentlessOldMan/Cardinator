using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.29, from the fourth review: on frames with a seal set into the bottom of the text box (the sample frames),
/// adventure and prototype cards ran their text — and an adventure's tinted page — under the seal, the one layout
/// family 1.6.8/1.6.22 left out. Stopping their text above the seal shrank it too far, so the lines beside the seal
/// wrap around it instead. Each case fails with the fix taken out. In the STA-window collection because it renders.
/// </summary>
[Collection("STAWindows")]
public class SealWrapTests
{
    private const string Giant = "Whenever Bonecrusher Giant becomes the target of a spell, Bonecrusher Giant deals 2 damage to that spell's controller. Then if you control another Giant, draw a card and each opponent loses 1 life.";
    private const string Stomp = "Damage can't be prevented this turn. Stomp deals 2 damage to any target. Then scry 1 and you may draw a card, then discard a card unless you control a Giant.";
    private const string Fleshgorger = "Prototype {1}{B}{B} — 3/3\nMenace, lifelink, ward—Pay life equal to Phyrexian Fleshgorger's power.\nWhen Phyrexian Fleshgorger enters, each opponent sacrifices a creature with the greatest power among creatures they control, then loses life equal to that creature's toughness.";

    [Theory]
    [InlineData("Alchemist's Steel", true)]
    [InlineData("Sealed Gate", true)]
    [InlineData("Alchemist's Steel", false)]
    [InlineData("Sealed Gate", false)]
    public void AdventureAndPrototypeText_StaysOffTheFramesSeal(string frame, bool adventure) => TestHelpers.RunSta(() =>
    {
        var t = new TemplateService().LoadAll().First(x => x.Name == frame);
        var seal = CardRenderer.BottomOrnament(t, t.Spec.WithSubBorderApplied())!.Value;
        var r = new CardRenderer(new SymbolService());
        var plain = new CardModel { Name = "Card", TypeLine = "Creature — Giant", Power = "4", Toughness = "3", TemplateName = frame };
        var card = plain.Clone();
        if (adventure)
        {
            card.RulesText = Giant;
            card.AdventureName = "Stomp"; card.AdventureCost = "{1}{R}"; card.AdventureType = "Instant — Adventure"; card.AdventureText = Stomp;
        }
        else card.RulesText = Fleshgorger;

        var blank = TestHelpers.Pixels(r.RenderToBitmap(plain, t));
        var bmp = r.RenderToBitmap(card, t);
        var px = TestHelpers.Pixels(bmp);
        // The seal is round: inside the circle its bounding box holds.
        double cx = seal.X + seal.Width / 2, cy = seal.Y + seal.Height / 2, rx = seal.Width / 2, ry = seal.Height / 2;
        int w = bmp.PixelWidth, inside = 0, elsewhere = 0;
        for (int y = 0; y < bmp.PixelHeight; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(px[i] - blank[i]) + Math.Abs(px[i + 1] - blank[i + 1]) + Math.Abs(px[i + 2] - blank[i + 2]) < 40) continue;
                double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                if (dx * dx + dy * dy <= 1) inside++; else elsewhere++;
            }
        Assert.True(elsewhere > 2000, "the text didn't draw");
        Assert.Equal(0, inside);
    });

    [Fact]
    public void WrappingAroundTheSeal_KeepsTheAdventuresTextFullSize() => TestHelpers.RunSta(() =>
    {
        var t = new TemplateService().LoadAll().First(x => x.Name == "Alchemist's Steel");
        var spec = t.Spec.WithSubBorderApplied();
        var seal = CardRenderer.BottomOrnament(t, spec);
        var card = new CardModel
        {
            Name = "Card", TypeLine = "Creature — Giant", Power = "4", Toughness = "3", RulesText = Giant,
            AdventureName = "Stomp", AdventureCost = "{1}{R}", AdventureType = "Instant — Adventure", AdventureText = Stomp,
        };
        var r = new CardRenderer(new SymbolService());
        var (_, _, _, _, _, without) = r.AdventureLayout(card, spec, null);
        var (_, _, _, _, _, with) = r.AdventureLayout(card, spec, null, seal);
        Assert.True(with >= without - 1, $"the seal shrank the text from {without} to {with}");
    });
}
