using System;
using System.Linq;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.4 Level Up (<i>Student of Warfare</i>): the rules are read the way Scryfall writes them ("LEVEL 2-6",
/// "3/3", that level's rules) and the text box is drawn as bands, each with its own P/T box, the level bands
/// with a LEVEL badge; the base P/T moves into the first band, so there's no corner P/T box.
/// </summary>
public class LevelUpTests
{
    private const string Rules =
        "Level up {W} ({W}: Put a level counter on this. Level up only as a sorcery.)\nLEVEL 2-6\n3/3\nFirst strike\nLEVEL 7+\n4/4\nDouble strike";

    private static CardModel Student(string template = "") => new()
    {
        Name = "Student of Warfare", ManaCost = "{W}", TypeLine = "Creature — Human Knight", RulesText = Rules,
        Power = "1", Toughness = "1", TemplateName = template,
    };

    [Fact]
    public void Parse_ReadsTheBaseBandAndEachLevelWithItsPt()
    {
        var bands = CardRenderer.ParseLevelUp(Student());
        Assert.Equal(3, bands.Count);
        Assert.Equal(new CardRenderer.LevelBand(null, "1/1", "Level up {W} ({W}: Put a level counter on this. Level up only as a sorcery.)"), bands[0]);
        Assert.Equal(new CardRenderer.LevelBand("2-6", "3/3", "First strike"), bands[1]);
        Assert.Equal(new CardRenderer.LevelBand("7+", "4/4", "Double strike"), bands[2]);
    }

    [Fact]
    public void Parse_KeepsMultiLineLevelRules_AndToleratesSpacingAndCase()
    {
        var card = Student();
        card.RulesText = "Level up {1}\r\nlevel 4 - 5\r\n2/4\r\nFlying\r\nOther Merfolk get +1/+1.";
        Assert.True(card.IsLevelUp);
        var b = CardRenderer.ParseLevelUp(card)[1];
        Assert.Equal("4-5", b.Level);
        Assert.Equal("2/4", b.Pt);
        Assert.Equal("Flying\nOther Merfolk get +1/+1.", b.Text);
    }

    [Theory]
    [InlineData("Flying")]
    [InlineData("Level up {2}")]                       // the keyword alone isn't enough to draw bands
    [InlineData("Gain 2 life. LEVEL 2-6 in the middle of a line")]
    public void NotALeveler_WithoutALevelLine(string rules)
        => Assert.False(new CardModel { RulesText = rules }.IsLevelUp);

    [Fact]
    public void Layout_BandsStackDownTheTextBox_EachWithItsPtBox_AndTheLevelsWithABadge() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var spec = tpl.Spec.WithSubBorderApplied();
        var (bands, size) = new CardRenderer(new SymbolService()).LevelUpLayout(Student(tpl.Name), spec);
        var tb = new Rect(spec.EffectiveTextBox.X, spec.EffectiveTextBox.Y, spec.EffectiveTextBox.W, spec.EffectiveTextBox.H);

        Assert.Equal(3, bands.Count);
        Assert.True(size >= Math.Min(14, spec.RulesFont.Size), $"rules shrank to {size}");
        for (int i = 0; i < bands.Count; i++)
        {
            var (band, rect, text, badge, pt) = bands[i];
            Assert.True(tb.Contains(rect), $"band {i} leaves the text box");
            if (i > 0) Assert.Equal(bands[i - 1].rect.Bottom, rect.Top, 3);   // stacked, no gaps or overlaps
            Assert.NotNull(pt);
            Assert.True(rect.Contains(pt!.Value) && pt.Value.Left > text.Right, $"band {i}'s P/T box is misplaced");
            Assert.Equal(i > 0, badge != null);
            if (badge is { } b) Assert.True(rect.Contains(b) && b.Right < text.Left, $"band {i}'s badge is misplaced");
        }
    });

    [Fact]
    public void Render_HasNoCornerPtBox_AndDrawsTheBands() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var r = new CardRenderer(new SymbolService());
        var leveler = Student(tpl.Name);
        var plain = Student(tpl.Name); plain.RulesText = "Level up {W}";   // the same card with no LEVEL lines

        // Changing the base P/T only touches the first band (top of the text box), not the corner box.
        var other = Student(tpl.Name); other.Power = "9";
        var a = TestHelpers.Pixels(r.RenderToBitmap(leveler, tpl));
        var b = TestHelpers.Pixels(r.RenderToBitmap(other, tpl));
        int w = 750, minY = int.MaxValue, maxY = -1;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 30)
            { int y = i / 4 / w; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        var spec = tpl.Spec.WithSubBorderApplied();
        double tbMid = spec.EffectiveTextBox.Y + spec.EffectiveTextBox.H / 2;
        Assert.True(maxY >= 0 && maxY < tbMid, $"the base P/T drew at y={minY}..{maxY}, not in the first band");
        Assert.True(TestHelpers.HasContent(r.RenderToBitmap(plain, tpl)));
    });

    [Fact]
    public void EveryInstalledFrame_DrawsALeveler_WithoutPixelErrors() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
        {
            var card = Student(tpl.Name);
            var resolved = TemplateService.ResolveFor(card, tpl);
            var issues = RenderInspector.Inspect(r.RenderToBitmap(card, resolved), card, resolved.Spec);
            Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        }
    });

    [Fact]
    public void Checks_NoteALevelUpCardWithoutLevelLines()
    {
        var card = Student(); card.RulesText = "Level up {W}\nLevel 2-6: 3/3 first strike";
        Assert.Contains(CardValidator.Validate(card, new TemplateSpec()), i => i.Code == "levelup-no-levels");
        Assert.DoesNotContain(CardValidator.Validate(Student(), new TemplateSpec()), i => i.Code == "levelup-no-levels");
    }
}
