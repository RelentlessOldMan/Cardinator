using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

public class RendererLogicTests
{
    [Fact]
    public void ParseAbilities_ExtractsLoyaltyCosts_NormalizingMinus()
    {
        var rows = CardRenderer.ParseAbilities("+2: Look at cards.\n0: Draw three.\n-1: Return a creature.\n-12: Exile all.");

        Assert.Equal(4, rows.Count);
        Assert.Equal("+2", rows[0].cost);
        Assert.Equal("0", rows[1].cost);
        Assert.Equal("−1", rows[2].cost);      // hyphen normalized to minus sign
        Assert.Equal("−12", rows[3].cost);
        Assert.Equal("Look at cards.", rows[0].text);
    }

    [Fact]
    public void ParseAbilities_StaticAbility_HasNullCost()
    {
        var rows = CardRenderer.ParseAbilities("Whenever a creature dies, draw a card.");
        Assert.Single(rows);
        Assert.Null(rows[0].cost);
    }

    [Fact]
    public void ParseAbilities_SkipsBlankLines()
    {
        var rows = CardRenderer.ParseAbilities("+1: A.\n\n\n-3: B.");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ParseChapters_ExtractsRomanNumeralMarkers()
    {
        var rows = CardRenderer.ParseChapters("(As this Saga enters…)\nI, II — Create a token.\nIII — Buff creatures.");

        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].cost);                 // reminder line, no marker
        Assert.Equal("I,II", rows[1].cost);
        Assert.Equal("III", rows[2].cost);
        Assert.Equal("Create a token.", rows[1].text);
    }

    [Fact]
    public void ParseClassLevels_BadgesAbilitiesByLevel()
    {
        var rows = CardRenderer.ParseClassLevels(
            "(Reminder.)\nBase ability.\n{1}{R}: Level 2\nSecond level ability.\n{2}{R}: Level 3\nThird level ability.");

        Assert.Equal(6, rows.Count);
        Assert.Null(rows[0].cost);         // reminder
        Assert.Null(rows[1].cost);         // base ability
        Assert.Null(rows[2].cost);         // "{1}{R}: Level 2" shown plainly
        Assert.Equal("2", rows[3].cost);   // ability badged with level 2
        Assert.Null(rows[4].cost);
        Assert.Equal("3", rows[5].cost);
    }

    [Fact]
    public void BuildCollectorLine_CombinesParts()
    {
        var card = new CardModel { CollectorNumber = "1", Rarity = "r", SetCode = "cst" };
        Assert.Equal("1 R • CST • EN", CardRenderer.BuildCollectorLine(card));
    }

    [Fact]
    public void BuildCollectorLine_EmptyWhenNoData()
        => Assert.Equal("", CardRenderer.BuildCollectorLine(new CardModel()));

    [Fact]
    public void BuildCreditLine_ArtistAndCopyright()
    {
        var card = new CardModel { Artist = "Ada", Copyright = "© 2026" };
        Assert.Equal("Illus. Ada  •  © 2026", CardRenderer.BuildCreditLine(card));
    }

    [Fact]
    public void IsPlaneswalker_DetectedByTypeOrLoyalty()
    {
        Assert.True(new CardModel { TypeLine = "Legendary Planeswalker — Jace" }.IsPlaneswalker);
        Assert.True(new CardModel { Loyalty = "4" }.IsPlaneswalker);
        Assert.False(new CardModel { TypeLine = "Creature — Bear" }.IsPlaneswalker);
    }
}
