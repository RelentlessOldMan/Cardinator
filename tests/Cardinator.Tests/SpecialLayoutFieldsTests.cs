using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>
/// Locks the contract the new "Special layouts" authoring fields (Subtitle / Adventure / Land big-symbols)
/// depend on: the model derives the right layout from them, and they persist through a save/clone round-trip.
/// </summary>
public class SpecialLayoutFieldsTests
{
    [Fact]
    public void AdventureName_MakesTheCardAnAdventure()
    {
        var card = new CardModel { Name = "Bear", TypeLine = "Creature — Bear" };
        Assert.False(card.IsAdventure);
        card.AdventureName = "Hunt the Weak";
        Assert.True(card.IsAdventure);
    }

    [Fact]
    public void LandSymbol_TokensDriveTheBigLandSymbols()
    {
        var card = new CardModel { Name = "Dual", TypeLine = "Land", LandSymbol = "{R}{G}" };
        Assert.Equal(new[] { "{R}", "{G}" }, card.BigLandSymbols);
    }

    [Fact]
    public void LandSymbol_IsIgnoredForNonLands()
    {
        var card = new CardModel { Name = "X", TypeLine = "Creature", LandSymbol = "{R}{G}" };
        Assert.Empty(card.BigLandSymbols);
    }

    [Fact]
    public void SpecialLayoutFields_SurviveACloneRoundTrip()
    {
        var card = new CardModel
        {
            Name = "Fancy", TypeLine = "Land",
            Subtitle = "The First Step",
            LandSymbol = "{W}{U}", LandSymbolStyle = "splitv",
            AdventureName = "Quest", AdventureCost = "{1}{G}",
            AdventureType = "Sorcery — Adventure", AdventureText = "Draw a card.",
        };

        var clone = card.Clone();

        Assert.Equal("The First Step", clone.Subtitle);
        Assert.Equal("{W}{U}", clone.LandSymbol);
        Assert.Equal("splitv", clone.LandSymbolStyle);
        Assert.Equal("Quest", clone.AdventureName);
        Assert.Equal("{1}{G}", clone.AdventureCost);
        Assert.Equal("Sorcery — Adventure", clone.AdventureType);
        Assert.Equal("Draw a card.", clone.AdventureText);
    }
}
