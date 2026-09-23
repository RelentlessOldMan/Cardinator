using Cardinator.Services;

namespace Cardinator.Tests;

public class ImportServiceTests
{
    [Fact]
    public void Parse_PlainNames_OneCardPerLine_NeedsLookup()
    {
        var content = "# comment\nLightning Bolt\n\nSerra Angel\n";
        var cards = ImportService.Parse(content, null, "Gold Multicolor");

        Assert.Equal(2, cards.Count);
        Assert.Equal("Lightning Bolt", cards[0].Card.Name);
        Assert.True(cards[0].NeedsLookup);
        Assert.Equal("Gold Multicolor", cards[0].Card.TemplateName);
    }

    [Fact]
    public void Parse_NameWithArt_UsingPipe()
    {
        var cards = ImportService.Parse(@"Goblin | C:\art\goblin.png", null, "T");
        Assert.Single(cards);
        Assert.Equal("Goblin", cards[0].Card.Name);
        Assert.Equal(@"C:\art\goblin.png", cards[0].Card.ArtPath);
    }

    [Fact]
    public void Parse_Csv_MapsAliasedColumns_AndSplitsPt()
    {
        var content = string.Join("\n",
            "title,cost,type,text,p/t,frame,illus",
            "Custom Warrior,2W,Creature — Soldier,First strike,2/3,Ocean Blue,Me");
        var cards = ImportService.Parse(content, null, "Gold Multicolor");

        Assert.Single(cards);
        var c = cards[0].Card;
        Assert.Equal("Custom Warrior", c.Name);
        Assert.Equal("{2}{W}", c.ManaCost);              // normalized
        Assert.Equal("Creature — Soldier", c.TypeLine);
        Assert.Equal("First strike", c.RulesText);
        Assert.Equal("2", c.Power);
        Assert.Equal("3", c.Toughness);
        Assert.Equal("Ocean Blue", c.TemplateName);
        Assert.Equal("Me", c.Artist);
        Assert.False(cards[0].NeedsLookup);              // has mana/type/rules
    }

    [Fact]
    public void Parse_Csv_BlankRow_NeedsLookup()
    {
        var content = "name,template\nCounterspell,Ocean Blue";
        var cards = ImportService.Parse(content, null, "Gold Multicolor");
        Assert.True(cards[0].NeedsLookup);
        Assert.Equal("Ocean Blue", cards[0].Card.TemplateName);
    }

    [Fact]
    public void Parse_Csv_LookupColumnOverridesDefault()
    {
        var content = "name,mana,lookup\nToken Maker,{1}{G},true";
        var cards = ImportService.Parse(content, null, "T");
        Assert.True(cards[0].NeedsLookup);               // forced on despite having mana
    }

    [Fact]
    public void Parse_Csv_UnescapesNewlinesInRules()
    {
        var content = "name,rules\nX,\"Haste\\nTrample\"";
        var cards = ImportService.Parse(content, null, "T");
        Assert.Equal("Haste\nTrample", cards[0].Card.RulesText);
    }

    [Fact]
    public void Parse_Csv_ResolvesRelativeArtAgainstBaseDir()
    {
        var content = "name,art\nX,pic.png";
        var cards = ImportService.Parse(content, @"C:\base", "T");
        Assert.Equal(@"C:\base\pic.png", cards[0].Card.ArtPath);
    }

    [Fact]
    public void Parse_Tsv_DetectedAndParsed()
    {
        var content = "name\ttype\tpt\nOgre\tCreature — Ogre\t3/3";
        var cards = ImportService.Parse(content, null, "T");
        Assert.Single(cards);
        Assert.Equal("Ogre", cards[0].Card.Name);
        Assert.Equal("Creature — Ogre", cards[0].Card.TypeLine);
        Assert.Equal("3", cards[0].Card.Power);
    }

    [Fact]
    public void Parse_Csv_QuotedFieldWithComma()
    {
        var content = "name,type\nX,\"Artifact, Legendary\"";
        var cards = ImportService.Parse(content, null, "T");
        Assert.Equal("Artifact, Legendary", cards[0].Card.TypeLine);
    }

    [Fact]
    public void Parse_Csv_UnknownColumnsIgnored()
    {
        var content = "name,foo,mana\nX,ignored,{G}";
        var cards = ImportService.Parse(content, null, "T");
        Assert.Equal("{G}", cards[0].Card.ManaCost);
    }

    [Fact]
    public void Parse_Csv_MapsNewFields_LoyaltySetRarityCollector()
    {
        var content = string.Join("\n",
            "name,loyalty,set,number,rarity,copyright",
            "Walker,4,CST,7,M,© 2026");
        var c = ImportService.Parse(content, null, "T")[0].Card;
        Assert.Equal("4", c.Loyalty);
        Assert.Equal("CST", c.SetCode);
        Assert.Equal("7", c.CollectorNumber);
        Assert.Equal("M", c.Rarity);
        Assert.Equal("© 2026", c.Copyright);
    }
}
