using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.4 Prototype (<i>Blitz Automaton</i>) and Mutate (<i>Gemrazer</i>): the rules' first line, written the way
/// Scryfall does, becomes a band across the top of the text box — a prototype's tinted in its cost's colours
/// with its own small cost and P/T plate at the right end — and the rest of the rules flow below it.
/// </summary>
public class PrototypeMutateTests
{
    private const string ProtoRules =
        "Prototype {2}{R} — 3/2 (You may cast this spell with different mana cost, color, and size. It keeps its abilities and types.)\nHaste";
    private const string MutateRules =
        "Mutate {1}{G}{G} (If you cast this spell for its mutate cost, put it over or under target non-Human creature you own.)\nReach, trample\nWhenever this creature mutates, destroy target artifact or enchantment an opponent controls.";

    private static CardModel Blitz(string template = "") => new()
    {
        Name = "Blitz Automaton", ManaCost = "{7}", TypeLine = "Artifact Creature — Construct", RulesText = ProtoRules,
        FlavorText = "Built to end the war quickly.", Power = "6", Toughness = "4", TemplateName = template,
    };

    private static CardModel Gemrazer(string template = "") => new()
    {
        Name = "Gemrazer", ManaCost = "{3}{G}", TypeLine = "Creature — Beast", RulesText = MutateRules,
        Power = "4", Toughness = "4", TemplateName = template,
    };

    [Fact]
    public void Prototype_FirstLine_GivesItsCostPtAndReminder_RestBelow()
    {
        var band = CardRenderer.ParseTopBand(Blitz())!;
        Assert.Equal("prototype", band.Kind);
        Assert.Equal("{2}{R}", band.Cost);
        Assert.Equal("3/2", band.Pt);
        Assert.StartsWith("Prototype (You may cast", band.Text);
        Assert.Equal("Haste", band.Rest);
    }

    [Theory]
    [InlineData("Prototype {1}{U}{U} - 2/2\nFlying")]       // a plain hyphen
    [InlineData("Prototype {1}{U}{U} – 2 / 2\nFlying")]     // en dash, spaced P/T
    public void Prototype_ToleratesHowTheDashAndPtAreTyped(string rules)
    {
        var band = CardRenderer.ParseTopBand(new CardModel { RulesText = rules })!;
        Assert.Equal("{1}{U}{U}", band.Cost);
        Assert.Equal("2/2", band.Pt);
        Assert.Equal("Prototype", band.Text);
        Assert.Equal("Flying", band.Rest);
    }

    [Fact]
    public void Mutate_FirstLineIsTheBand_RestBelow()
    {
        var band = CardRenderer.ParseTopBand(Gemrazer())!;
        Assert.Equal("mutate", band.Kind);
        Assert.StartsWith("Mutate {1}{G}{G} (If you cast", band.Text);
        Assert.Equal("", band.Cost);
        Assert.StartsWith("Reach, trample\nWhenever", band.Rest);
    }

    [Theory]
    [InlineData("Flying")]
    [InlineData("Haste\nPrototype {2}{R} — 3/2")]   // only the FIRST line counts
    [InlineData("Mutate this into something else.")] // the keyword needs its cost
    public void OtherCards_HaveNoBand(string rules)
        => Assert.Null(CardRenderer.ParseTopBand(new CardModel { RulesText = rules }));

    [Theory]
    [InlineData("{2}{R}", "R")]
    [InlineData("{1}{U}{U}", "U")]
    [InlineData("{W}{B}", "multi")]
    [InlineData("{3}", "colourless")]
    public void PrototypeTint_FollowsTheCostsColours(string cost, string expect)
    {
        var c = CardRenderer.PrototypeTint(cost);
        switch (expect)
        {
            case "R": Assert.True(c.R > c.G + 40 && c.R > c.B + 40); break;
            case "U": Assert.True(c.B > c.R + 40); break;
            case "multi": Assert.True(c.R > 200 && c.G > 170 && c.B < 120); break;   // gold
            default: Assert.True(Math.Abs(c.R - c.G) < 8 && Math.Abs(c.G - c.B) < 8); break;   // grey
        }
    }

    [Fact]
    public void Layout_BandOnTop_CostAndPtAtItsRightEnd_RestBelow() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Slate Artifact");
        var spec = tpl.Spec.WithSubBorderApplied();
        var card = Blitz(tpl.Name);
        var (band, text, cost, pt, below, size) = new CardRenderer(new SymbolService())
            .TopBandLayout(card, spec, CardRenderer.ParseTopBand(card)!, null);
        var tb = new Rect(spec.EffectiveTextBox.X, spec.EffectiveTextBox.Y, spec.EffectiveTextBox.W, spec.EffectiveTextBox.H);

        Assert.True(tb.Contains(band), "the band leaves the text box");
        Assert.Equal(tb.Top + 3, band.Top, 3);                          // across the TOP of the box
        Assert.True(band.Height < tb.Height * 0.6, "the band swallowed the text box");
        Assert.True(band.Contains(cost!.Value) && band.Contains(pt!.Value), "cost/P/T outside the band");
        Assert.True(cost.Value.Left > text.Right && pt.Value.Left > text.Right, "cost/P/T overlap the band's text");
        Assert.True(cost.Value.Bottom <= pt.Value.Top, "the cost isn't above the P/T plate");
        Assert.Equal(band.Bottom, below.Top, 3);
        Assert.True(size >= Math.Min(14, spec.RulesFont.Size), $"rules shrank to {size}");
    });

    [Fact]
    public void Render_DrawsATintedBand_AndKeepsTheCornerPtBox() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Slate Artifact");
        var r = new CardRenderer(new SymbolService());
        var spec = tpl.Spec.WithSubBorderApplied();
        var card = Blitz(tpl.Name);
        var (band, _, _, _, _, _) = r.TopBandLayout(card, spec, CardRenderer.ParseTopBand(card)!, null);
        var px = TestHelpers.Pixels(r.RenderToBitmap(card, tpl));
        int reds = 0, n = 0;
        for (int y = (int)band.Top + 4; y < band.Bottom - 4; y += 3)
            for (int x = (int)band.Left + 4; x < band.Left + band.Width * 0.6; x += 5)
            {
                int i = (y * 750 + x) * 4; n++;
                if (px[i + 2] > px[i + 1] + 30 && px[i + 2] > px[i] + 30) reds++;   // BGRA: red clearly dominant
            }
        Assert.True(reds > n * 0.5, $"the {{2}}{{R}} prototype band isn't red ({reds}/{n})");

        var noPt = Blitz(tpl.Name); noPt.Power = "9";   // the CARD's P/T still lives in the corner box
        Assert.NotEqual(TestHelpers.Pixels(r.RenderToBitmap(noPt, tpl)), px);
    });

    [Fact]
    public void EveryInstalledFrame_DrawsBoth_WithoutPixelErrors() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
            foreach (var card in new[] { Blitz(tpl.Name), Gemrazer(tpl.Name) })
            {
                var issues = RenderInspector.Inspect(r.RenderToBitmap(card, tpl), card, tpl.Spec);
                Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
            }
    });
}
