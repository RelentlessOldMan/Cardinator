using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// H4: the CHECKS panel warns when a special-layout card's rules-text syntax won't parse into badges, so a
/// mistyped planeswalker/saga/class doesn't silently render blank. Uses the renderer's own parsers.
/// </summary>
public class LayoutGuidanceTests
{
    private static TemplateSpec Spec() => new() { Name = "T", CanvasWidth = 750, CanvasHeight = 1050 };

    [Fact]
    public void Planeswalker_WithoutLoyaltyAbilities_IsFlagged()
    {
        var card = new CardModel
        {
            Name = "Jace", TypeLine = "Legendary Planeswalker — Jace", Loyalty = "4",
            RulesText = "Draw a card.\nCounter target spell.",   // no +N:/-N: costs
        };
        Assert.Contains(CardValidator.Validate(card, Spec()), i => i.Code == "pw-no-abilities");
    }

    [Fact]
    public void Planeswalker_WithProperAbilities_IsNotFlagged()
    {
        var card = new CardModel
        {
            Name = "Jace", TypeLine = "Legendary Planeswalker — Jace", Loyalty = "4",
            RulesText = "+1: Draw a card.\n-3: Return target creature to its owner's hand.",
        };
        Assert.DoesNotContain(CardValidator.Validate(card, Spec()), i => i.Code == "pw-no-abilities");
    }

    [Fact]
    public void Saga_WithoutChapters_IsFlagged()
    {
        var card = new CardModel
        {
            Name = "History", TypeLine = "Enchantment — Saga",
            RulesText = "Do something.\nThen do something else.",   // no "I —" markers
        };
        Assert.Contains(CardValidator.Validate(card, Spec()), i => i.Code == "saga-no-chapters");
    }

    [Fact]
    public void Saga_WithChapters_IsNotFlagged()
    {
        var card = new CardModel
        {
            Name = "History", TypeLine = "Enchantment — Saga",
            RulesText = "I — Draw a card.\nII, III — Create a token.",
        };
        Assert.DoesNotContain(CardValidator.Validate(card, Spec()), i => i.Code == "saga-no-chapters");
    }

    [Fact]
    public void StrayLoyaltyCreature_IsNotAlsoFlaggedForMissingAbilities()
    {
        // A creature with a stray loyalty gets the loyalty-nonplaneswalker warning, NOT pw-no-abilities.
        var card = new CardModel
        {
            Name = "Bear", TypeLine = "Creature — Bear", Power = "2", Toughness = "2", Loyalty = "3",
            RulesText = "Vigilance.",
        };
        var issues = CardValidator.Validate(card, Spec());
        Assert.Contains(issues, i => i.Code == "loyalty-nonplaneswalker");
        Assert.DoesNotContain(issues, i => i.Code == "pw-no-abilities");
    }
}
