using System;
using System.Linq;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.8 Station cards (<i>Uthros Research Craft</i>: "3+ | …" threshold bands, the P/T box on the band where it
/// becomes a creature) and Cases (<i>Case of the Burning Masks</i>: To solve and Solved bands with a magnifying glass
/// and a check mark), drawn with the level up bands. Plus the frame ornament the bottom band's text stays above.
/// </summary>
public class StationCaseTests
{
    private const string UthrosRules =
        "Station (Tap another creature you control: Put charge counters equal to its power on this Spacecraft. Station only as a sorcery. It's an artifact creature at 12+.)\n"
        + "3+ | Whenever you cast an artifact spell, draw a card. Put a charge counter on this Spacecraft.\n"
        + "12+ | Flying\nThis Spacecraft gets +1/+0 for each artifact you control.";

    private const string CaseRules =
        "When this Case enters, it deals 3 damage to target creature an opponent controls.\n"
        + "To solve — Three or more sources you controlled dealt damage this turn. (If unsolved, solve at the beginning of your end step.)\n"
        + "Solved — Sacrifice this Case: Exile the top three cards of your library. Choose one of them. You may play that card this turn.";

    private static CardModel Uthros(string template = "") => new()
    {
        Name = "Uthros Research Craft", ManaCost = "{2}{U}", TypeLine = "Artifact — Spacecraft", RulesText = UthrosRules,
        Power = "0", Toughness = "8", TemplateName = template,
    };

    private static CardModel Masks(string template = "") => new()
    {
        Name = "Case of the Burning Masks", ManaCost = "{1}{R}{R}", TypeLine = "Enchantment — Case", RulesText = CaseRules,
        TemplateName = template,
    };

    [Fact]
    public void Station_ReadsTheThresholds_KeepsTheLinesAfterOneInItsBand_AndPutsThePtWhereItBecomesACreature()
    {
        var card = Uthros();
        Assert.True(card.IsStation && card.HasBands);
        var bands = CardRenderer.ParseStation(card);
        Assert.Equal(3, bands.Count);
        Assert.Null(bands[0].Level);
        Assert.StartsWith("Station (", bands[0].Text);
        Assert.Equal("", bands[0].Pt);   // not a creature until it's stationed
        Assert.Equal(new CardRenderer.LevelBand("3+", "", "Whenever you cast an artifact spell, draw a card. Put a charge counter on this Spacecraft.", "station"), bands[1]);
        Assert.Equal(new CardRenderer.LevelBand("12+", "0/8", "Flying\nThis Spacecraft gets +1/+0 for each artifact you control.", "station"), bands[2]);
    }

    [Fact]
    public void Station_PtGoesOnTheCreatureThreshold_EvenWhenItIsNotTheLast()
    {
        var card = new CardModel
        {
            TypeLine = "Artifact — Spacecraft", Power = "4", Toughness = "4",
            RulesText = "Station (… It's an artifact creature at 8+.)\n3+ | Draw a card.\n8+ | Flying\n10+ | Hexproof",
        };
        var bands = CardRenderer.ParseStation(card);
        Assert.Equal("4/4", bands.Single(b => b.Level == "8+").Pt);
        Assert.Equal("", bands.Single(b => b.Level == "10+").Pt);

        var land = new CardModel { TypeLine = "Land — Planet", RulesText = "{T}: Add {C}.\nStation\n9+ | {T}: Add {B}{B}." };
        Assert.All(CardRenderer.ParseStation(land), b => Assert.Equal("", b.Pt));   // no P/T anywhere
    }

    [Theory]
    [InlineData("Flying")]
    [InlineData("Station (Tap another creature you control…)")]   // the keyword alone draws no bands
    [InlineData("Pay 3+ | life")]
    public void NotAStationCard_WithoutAThresholdLine(string rules)
        => Assert.False(new CardModel { RulesText = rules }.IsStation);

    [Fact]
    public void Case_ReadsTheOpeningAbility_ToSolve_AndSolved_EachKeepingItsWording()
    {
        var card = Masks();
        Assert.True(card.IsCase && card.HasBands);
        var bands = CardRenderer.ParseCase(card);
        Assert.Equal(3, bands.Count);
        Assert.Null(bands[0].Level);
        Assert.Equal("solve", bands[1].Kind);
        Assert.StartsWith("To solve — Three or more", bands[1].Text);
        Assert.EndsWith("(If unsolved, solve at the beginning of your end step.)", bands[1].Text);
        Assert.Equal("solved", bands[2].Kind);
        Assert.StartsWith("Solved — Sacrifice this Case", bands[2].Text);
        Assert.All(bands, b => Assert.Equal("", b.Pt));
        Assert.False(new CardModel { RulesText = "When this Case enters, gain 2 life." }.IsCase);
    }

    [Fact]
    public void Layout_BadgesOnTheThresholdAndCaseBands_AndOnlyTheCreatureBandHasAPtBox() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var spec = tpl.Spec.WithSubBorderApplied();
        var r = new CardRenderer(new SymbolService());
        var tb = new Rect(spec.EffectiveTextBox.X, spec.EffectiveTextBox.Y, spec.EffectiveTextBox.W, spec.EffectiveTextBox.H);

        var (station, _) = r.LevelUpLayout(Uthros(tpl.Name), spec);
        Assert.Equal(new[] { false, true, true }, station.Select(b => b.badge != null));
        Assert.Equal(new[] { false, false, true }, station.Select(b => b.pt != null));
        Assert.All(station, b => Assert.True(tb.Contains(b.rect)));

        var (cases, size) = r.LevelUpLayout(Masks(tpl.Name), spec);
        Assert.Equal(new[] { false, true, true }, cases.Select(b => b.badge != null));
        Assert.All(cases, b => Assert.Null(b.pt));
        Assert.True(size >= Math.Min(12, spec.RulesFont.Size), $"rules shrank to {size}");
    });

    [Fact]
    public void Render_StationShowsItsPt_AndCasesDraw() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var r = new CardRenderer(new SymbolService());
        // The band P/T box carries the number, so changing it changes the card.
        var a = Uthros(tpl.Name); var b = Uthros(tpl.Name); b.Power = "12";
        Assert.NotEqual(TestHelpers.Pixels(r.RenderToBitmap(a, tpl)), TestHelpers.Pixels(r.RenderToBitmap(b, tpl)));
        Assert.True(TestHelpers.HasContent(r.RenderToBitmap(Masks(tpl.Name), tpl)));
    });

    [Fact]
    public void EveryInstalledFrame_DrawsStationAndCaseCards_WithoutInspectorErrors() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
            foreach (var card in new[] { Uthros(tpl.Name), Masks(tpl.Name) })
            {
                var resolved = TemplateService.ResolveFor(card, tpl);
                Assert.True(TestHelpers.HasContent(r.RenderToBitmap(card, resolved)));
                Assert.DoesNotContain(RenderInspector.InspectCard(r, card, resolved), i => i.Severity == IssueSeverity.Error);
            }
    });

    [Fact]
    public void Ornament_FoundOnTheMedallionFrames_NotThePlainOnes_AndTheBottomBandTextStaysAboveIt() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        var steel = all.First(t => t.Name == "Alchemist's Steel");
        var spec = steel.Spec.WithSubBorderApplied();
        var medallion = CardRenderer.BottomOrnament(steel, spec);
        Assert.NotNull(medallion);
        var tb = spec.EffectiveTextBox;
        Assert.True(medallion!.Value.Bottom >= tb.Y + tb.H - 1 && medallion.Value.Height < tb.H * 0.4);
        Assert.True(Math.Abs(medallion.Value.X + medallion.Value.Width / 2 - (tb.X + tb.W / 2)) < tb.W * 0.1, "not centred");

        var gold = all.First(t => t.Name == "Gold Multicolor");
        Assert.Null(CardRenderer.BottomOrnament(gold, gold.Spec.WithSubBorderApplied()));

        var (bands, _) = new CardRenderer(new SymbolService()).LevelUpLayout(Masks(steel.Name), spec, medallion);
        Assert.True(bands[^1].text.Bottom <= medallion.Value.Top, "the Solved text runs onto the medallion");
    });

    [Fact]
    public void Validator_PointsOutAStationCardWithoutThresholds()
    {
        var tpl = new TemplateSpec { Name = "x" };
        var issues = CardValidator.Validate(new CardModel { Name = "Ship", TypeLine = "Artifact — Spacecraft", RulesText = "Station (…)\n8+: Flying" }, tpl);
        Assert.Contains(issues, i => i.Code == "station-no-thresholds");
        Assert.DoesNotContain(CardValidator.Validate(Uthros(), tpl), i => i.Code == "station-no-thresholds");
    }
}
