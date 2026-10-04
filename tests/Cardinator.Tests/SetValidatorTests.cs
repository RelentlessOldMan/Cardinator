using System.IO;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>W4: whole-set validation — returns every card with a real problem, skips clean/Info-only cards.</summary>
public class SetValidatorTests
{
    private static TemplateSpec Spec() => new() { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };

    [Fact]
    public void ValidateAll_ReturnsOnlyCardsWithWarningsOrErrors()
    {
        var clean = new CardModel { Name = "Clean", TypeLine = "Creature — Bear", Power = "2", Toughness = "2" };
        var broken = new CardModel { Name = "Broken", Power = "2" };   // half-pt warning
        var cards = new[] { clean, broken };

        var problems = SetValidator.ValidateAll(cards, _ => Spec());

        Assert.Single(problems);
        Assert.Same(broken, problems[0].Card);
        Assert.Contains(problems[0].Issues, i => i.Code == "half-pt");
    }

    [Fact]
    public void ValidateAll_SkipsCardsWithOnlyInfoFindings()
    {
        // Two cards sharing a name produce only an Info ("dup-name") — not surfaced in the set report.
        var a = new CardModel { Name = "Twin", TypeLine = "Creature", Power = "1", Toughness = "1" };
        var b = new CardModel { Name = "Twin", TypeLine = "Creature", Power = "1", Toughness = "1" };

        var problems = SetValidator.ValidateAll(new[] { a, b }, _ => Spec());

        Assert.Empty(problems);
    }

    [Fact]
    public void ValidateAll_FlagsMissingFrameAcrossTheSet()
    {
        var card = new CardModel { Name = "X", TypeLine = "Creature", Power = "1", Toughness = "1", TemplateName = "Gone" };
        var problems = SetValidator.ValidateAll(new[] { card }, _ => Spec(), new[] { "Ocean Blue" });
        Assert.Contains(problems[0].Issues, i => i.Code == "frame-missing");
    }
}
