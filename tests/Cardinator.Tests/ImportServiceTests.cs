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
    public void ParseWithReport_CountsUnreadableDelimitedRows()   // L5
    {
        // Header + 2 good rows + 1 row whose Name column is blank.
        var content = "name,type\nBolt,Instant\n,Creature\nAngel,Creature";
        var r = ImportService.ParseWithReport(content, null, "T");
        Assert.Equal(2, r.Cards.Count);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void ParseWithReport_CountsPlainLinesThatReduceToNothing()   // L5
    {
        // "(ABC) 12" is a printing hint with no name in front -> nothing left after stripping.
        var content = "Lightning Bolt\n(ABC) 12";
        var r = ImportService.ParseWithReport(content, null, "T");
        Assert.Single(r.Cards);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void ParseWithReport_CleanList_ReportsZeroSkipped()   // L5
    {
        var r = ImportService.ParseWithReport("Bolt\nAngel\n# note\n\nDeck", null, "T");
        Assert.Equal(2, r.Cards.Count);
        Assert.Equal(0, r.Skipped);   // comment/blank/section-header lines are not "unreadable"
    }

    [Fact]
    public void Parse_DeckList_ExpandsQuantities()
    {
        var cards = ImportService.Parse("4 Lightning Bolt\n2x Counterspell\nBrainstorm", null, "T");
        Assert.Equal(7, cards.Count);
        Assert.Equal(4, cards.Count(c => c.Card.Name == "Lightning Bolt"));
        Assert.Equal(2, cards.Count(c => c.Card.Name == "Counterspell"));
        Assert.Equal(1, cards.Count(c => c.Card.Name == "Brainstorm"));
    }

    [Fact]
    public void Parse_DeckList_ReadsSetAndCollectorHint()
    {
        var cards = ImportService.Parse("4 Lightning Bolt (M10) 146", null, "T");
        Assert.Equal(4, cards.Count);
        Assert.Equal("Lightning Bolt", cards[0].Card.Name);
        Assert.Equal("M10", cards[0].Card.SetCode);
        Assert.Equal("146", cards[0].Card.CollectorNumber);
    }

    [Fact]
    public void Parse_MoxfieldExport_ParsesQuantitiesSetsAndSections()
    {
        // A representative Moxfield "text" export: a commander section, category comments, blank lines,
        // set/collector hints, and a foil marker.
        var export = string.Join("\n",
            "Commander",
            "1 Atraxa, Praetors' Voice (2XM) 197",
            "",
            "// Ramp",
            "1 Sol Ring (LTC) 273 *F*",
            "1 Arcane Signet (LTC) 297",
            "2 Forest (UNF) 235");
        var cards = ImportService.Parse(export, null, "T");

        Assert.Equal(5, cards.Count);                                   // 1+1+1+2, headers/comments/blank skipped
        Assert.Equal("Atraxa, Praetors' Voice", cards[0].Card.Name);
        Assert.Equal("2XM", cards[0].Card.SetCode);
        Assert.Equal("197", cards[0].Card.CollectorNumber);
        Assert.Equal("Sol Ring", cards[1].Card.Name);                  // foil marker stripped
        Assert.Equal("273", cards[1].Card.CollectorNumber);
        Assert.Equal(2, cards.Count(c => c.Card.Name == "Forest"));
    }

    [Fact]
    public void Parse_AlphanumericCollectorNumber_IsCaptured()
    {
        var cards = ImportService.Parse("1 Lightning Bolt (STA) 42p", null, "T");
        Assert.Single(cards);
        Assert.Equal("Lightning Bolt", cards[0].Card.Name);
        Assert.Equal("STA", cards[0].Card.SetCode);
        Assert.Equal("42p", cards[0].Card.CollectorNumber);
    }

    [Fact]
    public void Parse_TheListHyphenatedCollector_IsCaptured()
    {
        // The List (PLST) numbers look like "M20-14" — the hyphen must not break set/collector parsing,
        // or the "(PLST) M20-14" stays glued to the name and the lookup fails.
        var cards = ImportService.Parse("1 Disenchant (PLST) M20-14", null, "T");
        Assert.Single(cards);
        Assert.Equal("Disenchant", cards[0].Card.Name);
        Assert.Equal("PLST", cards[0].Card.SetCode);
        Assert.Equal("M20-14", cards[0].Card.CollectorNumber);
    }

    [Theory]
    [InlineData("https://moxfield.com/decks/Wq2LzlPelEOT9a541Et1XQ", true)]
    [InlineData("www.moxfield.com/decks/abc", true)]
    [InlineData("https://a\nhttps://b", true)]
    [InlineData("1 Sol Ring\nhttps://moxfield.com/x", false)]   // has a real card line too
    [InlineData("Lightning Bolt", false)]
    [InlineData("", false)]
    public void LooksLikeOnlyLinks_DetectsPastedUrls(string text, bool expected)
        => Assert.Equal(expected, ImportService.LooksLikeOnlyLinks(text));

    [Fact]
    public void Parse_DeckList_SkipsSectionHeadersAndComments_AndStripsSbPrefix()
    {
        var cards = ImportService.Parse("Deck\nLightning Bolt\n// notes\nSideboard\nSB: Duress", null, "T");
        Assert.Equal(2, cards.Count);
        Assert.Equal("Lightning Bolt", cards[0].Card.Name);
        Assert.Equal("Duress", cards[1].Card.Name);
    }

    [Fact]
    public void Parse_StripsUtf8Bom_FromFirstName()
    {
        var cards = ImportService.Parse("﻿Lightning Bolt", null, "T");
        Assert.Single(cards);
        Assert.Equal("Lightning Bolt", cards[0].Card.Name);   // no invisible leading glyph
    }

    [Fact]
    public void Parse_NumericNamePrefix_NotMistakenForQuantity()
    {
        // "1996 World Champion" starts with 4 digits and no space-after-3, so it's not a quantity.
        var cards = ImportService.Parse("1996 World Champion", null, "T");
        Assert.Single(cards);
        Assert.Equal("1996 World Champion", cards[0].Card.Name);
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
