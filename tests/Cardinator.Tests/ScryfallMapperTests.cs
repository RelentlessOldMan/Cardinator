using Cardinator.Services;

namespace Cardinator.Tests;

public class ScryfallMapperTests
{
    [Fact]
    public void MapFaces_NormalCard_SingleFaceWithFields()
    {
        const string json = """
        {
          "name": "Grizzly Bears",
          "mana_cost": "{1}{G}",
          "type_line": "Creature — Bear",
          "oracle_text": "",
          "power": "2",
          "toughness": "2",
          "set": "lea",
          "collector_number": "200",
          "rarity": "common"
        }
        """;

        var faces = ScryfallMapper.MapFaces(json);

        Assert.Single(faces);
        var c = faces[0];
        Assert.Equal("Grizzly Bears", c.Name);
        Assert.Equal("{1}{G}", c.ManaCost);
        Assert.Equal("2", c.Power);
        Assert.Equal("LEA", c.SetCode);          // upper-cased
        Assert.Equal("200", c.CollectorNumber);
        Assert.Equal("C", c.Rarity);             // common -> C
    }

    [Fact]
    public void MapFaces_DoubleFaced_ReturnsBothFaces_WithSharedSetRarity()
    {
        const string json = """
        {
          "name": "Huntmaster of the Fells // Ravager of the Fells",
          "layout": "transform",
          "set": "dka",
          "collector_number": "140",
          "rarity": "mythic",
          "card_faces": [
            { "name": "Huntmaster of the Fells", "mana_cost": "{2}{R}{G}", "type_line": "Creature — Human Werewolf", "oracle_text": "x", "power": "2", "toughness": "2" },
            { "name": "Ravager of the Fells", "mana_cost": "", "type_line": "Creature — Werewolf", "oracle_text": "y", "power": "4", "toughness": "4" }
          ]
        }
        """;

        var faces = ScryfallMapper.MapFaces(json);

        Assert.Equal(2, faces.Count);
        Assert.Equal("Huntmaster of the Fells", faces[0].Name);
        Assert.Equal("Ravager of the Fells", faces[1].Name);
        Assert.Equal("4", faces[1].Power);
        Assert.All(faces, f => Assert.Equal("DKA", f.SetCode));
        Assert.All(faces, f => Assert.Equal("M", f.Rarity));
    }

    [Fact]
    public void MapFaces_Planeswalker_ReadsLoyalty()
    {
        const string json = """
        {
          "name": "Jace, the Mind Sculptor",
          "mana_cost": "{2}{U}{U}",
          "type_line": "Legendary Planeswalker — Jace",
          "oracle_text": "+2: Look.\n0: Draw.\n-1: Return.\n-12: Exile.",
          "loyalty": "3",
          "set": "wwk",
          "collector_number": "31",
          "rarity": "mythic"
        }
        """;

        var c = ScryfallMapper.MapFirst(json)!;
        Assert.Equal("3", c.Loyalty);
        Assert.True(c.IsPlaneswalker);
    }

    [Fact]
    public void MapFaces_Adventure_ReturnsOneCardWithSubSpell()
    {
        const string json = """
        {
          "name": "Bonecrusher Giant // Stomp",
          "layout": "adventure",
          "set": "eld",
          "collector_number": "115",
          "rarity": "uncommon",
          "card_faces": [
            { "name": "Bonecrusher Giant", "mana_cost": "{2}{R}", "type_line": "Creature — Giant", "oracle_text": "Whenever…", "power": "4", "toughness": "3" },
            { "name": "Stomp", "mana_cost": "{1}{R}", "type_line": "Instant — Adventure", "oracle_text": "Damage can't be prevented…" }
          ]
        }
        """;

        var faces = ScryfallMapper.MapFaces(json);
        Assert.Single(faces);
        var c = faces[0];
        Assert.Equal("Bonecrusher Giant", c.Name);
        Assert.Equal("4", c.Power);
        Assert.Equal("Stomp", c.AdventureName);
        Assert.Equal("{1}{R}", c.AdventureCost);
        Assert.Equal("Instant — Adventure", c.AdventureType);
        Assert.True(c.IsAdventure);
    }

    [Fact]
    public void MapFaces_Saga_SingleCardDetected()
    {
        const string json = """
        { "name": "History of Benalia", "layout": "saga", "mana_cost": "{1}{W}{W}",
          "type_line": "Enchantment — Saga", "oracle_text": "I, II — Create.\nIII — Buff.", "rarity": "mythic", "set": "dom" }
        """;
        var c = ScryfallMapper.MapFirst(json)!;
        Assert.True(c.IsSaga);
        Assert.Equal("saga", c.Layout);
    }

    [Fact]
    public void MapFaces_Split_ReturnsTwoCards()
    {
        const string json = """
        { "name": "Fire // Ice", "layout": "split", "card_faces": [
            { "name": "Fire", "mana_cost": "{1}{R}", "type_line": "Instant", "oracle_text": "a" },
            { "name": "Ice", "mana_cost": "{1}{U}", "type_line": "Instant", "oracle_text": "b" } ] }
        """;
        Assert.Equal(2, ScryfallMapper.MapFaces(json).Count);
    }

    [Theory]
    [InlineData("common", "C")]
    [InlineData("uncommon", "U")]
    [InlineData("rare", "R")]
    [InlineData("mythic", "M")]
    [InlineData("bogus", "")]
    public void MapRarity_MapsKnownValues(string input, string expected)
        => Assert.Equal(expected, ScryfallMapper.MapRarity(input));
}
