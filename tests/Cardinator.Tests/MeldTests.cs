using System;
using System.IO;
using System.Linq;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.5 meld cards (<i>Bruna, the Fading Light</i> + <i>Gisela, the Broken Blade</i> → <i>Brisela, Voice of
/// Nightmares</i>): each part is a double-faced card whose back face is the melded card, of which it prints one half
/// (<see cref="CardModel.MeldHalf"/>). The melded card is drawn whole, bigger and turned a quarter, across the two
/// backs laid side by side — the top half on one card's back, the bottom half on the other's.
/// </summary>
public class MeldTests
{
    private const string BrunaJson = """
        { "object": "card", "name": "Bruna, the Fading Light", "layout": "meld", "set": "emn", "collector_number": "15",
          "rarity": "rare", "mana_cost": "{5}{W}{W}", "type_line": "Legendary Creature — Angel Horror",
          "oracle_text": "Flying, vigilance", "power": "5", "toughness": "7",
          "all_parts": [
            { "object": "related_card", "component": "meld_result", "name": "Brisela, Voice of Nightmares", "uri": "https://api.scryfall.com/cards/brisela" },
            { "object": "related_card", "component": "meld_part", "name": "Bruna, the Fading Light", "uri": "https://api.scryfall.com/cards/bruna" },
            { "object": "related_card", "component": "meld_part", "name": "Gisela, the Broken Blade", "uri": "https://api.scryfall.com/cards/gisela" } ] }
        """;

    private const string BriselaJson = """
        { "object": "card", "name": "Brisela, Voice of Nightmares", "layout": "meld", "set": "emn", "collector_number": "15b",
          "rarity": "mythic", "mana_cost": "", "type_line": "Legendary Creature — Eldrazi Angel",
          "oracle_text": "Flying, first strike, vigilance, lifelink", "power": "9", "toughness": "10",
          "all_parts": [
            { "object": "related_card", "component": "meld_result", "name": "Brisela, Voice of Nightmares", "uri": "https://api.scryfall.com/cards/brisela" },
            { "object": "related_card", "component": "meld_part", "name": "Bruna, the Fading Light", "uri": "https://api.scryfall.com/cards/bruna" } ] }
        """;

    private static CardModel Brisela(string template = "") => new()
    {
        Name = "Brisela, Voice of Nightmares", TypeLine = "Legendary Creature — Eldrazi Angel",
        RulesText = "Flying, first strike, vigilance, lifelink", Power = "9", Toughness = "10", TemplateName = template,
    };

    private static CardModel Part(string half, string template = "")
    {
        var c = new CardModel
        {
            Name = half == "top" ? "Gisela, the Broken Blade" : "Bruna, the Fading Light", ManaCost = "{2}{W}{W}",
            TypeLine = "Legendary Creature — Angel Horror", RulesText = "Flying", Power = "4", Toughness = "3",
            TemplateName = template, DfcStyle = "meld",
        };
        c.BackFace = Brisela(template);
        c.MeldHalf = half;
        return c;
    }

    [Fact]
    public void Mapper_ReadsTheMeldPartnerAndTheMeldedCard_FromAllParts()
    {
        var bruna = ScryfallMapper.MapFaces(BrunaJson).Single();
        Assert.Equal("Gisela, the Broken Blade", bruna.MeldWith);
        Assert.Equal("https://api.scryfall.com/cards/brisela", bruna.MeldResultUrl);

        var brisela = ScryfallMapper.MapFaces(BriselaJson).Single();   // the melded card itself is just a card
        Assert.Equal("", brisela.MeldWith);
        Assert.Equal("", brisela.MeldResultUrl);
    }

    [Theory]
    [InlineData("15", "bottom")]   // Brisela is 15b: Bruna (15) carries its bottom half (the credits)
    [InlineData("28", "top")]      // Gisela
    public void AttachMeld_MakesTheBackTheMeldedCard_AndPicksTheHalfFromTheCollectorNumber(string number, string half)
    {
        var part = ScryfallMapper.MapFaces(BrunaJson).Single();
        part.CollectorNumber = number;
        var card = new CardModel { Name = part.Name, TemplateName = "Gold Multicolor" };
        Assert.True(CardDetailsFill.AttachMeld(card, part, ScryfallMapper.MapFaces(BriselaJson).Single()));

        Assert.True(card.IsMeld);
        Assert.Equal(half, card.MeldHalf);
        Assert.Equal("Brisela, Voice of Nightmares", card.BackFace!.Name);
        Assert.True(card.BackFace.IsMeldBack);
        Assert.Equal("Gold Multicolor", card.BackFace.TemplateName);
        Assert.Equal("meld", card.DfcStyle);
        Assert.Equal("Gisela, the Broken Blade", card.MeldWith);
    }

    [Fact]
    public void AttachMeld_NeverReplacesAnExistingBack()
    {
        var card = new CardModel { Name = "Bruna", BackFace = new CardModel { Name = "My own back" } };
        Assert.False(CardDetailsFill.AttachMeld(card, ScryfallMapper.MapFaces(BrunaJson).Single(), Brisela()));
        Assert.Equal("My own back", card.BackFace!.Name);
        Assert.False(card.IsMeld);
    }

    [Fact]
    public void SavedAndLoaded_StaysAMeldCard_AndOldFilesAreNot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meld-{Guid.NewGuid():N}.json");
        try
        {
            Part("bottom").Save(path);
            var loaded = CardModel.Load(path);
            Assert.True(loaded.IsMeld && loaded.IsMeldBottom);
            Assert.True(loaded.BackFace!.IsMeldBack && loaded.BackFace.IsMeldBottom);

            File.WriteAllText(path, """{ "name": "Old DFC", "backFace": { "name": "Back" } }""");
            var old = CardModel.Load(path);
            Assert.True(old.IsDoubleFaced);
            Assert.False(old.IsMeld);
            Assert.False(old.BackFace!.IsMeldBack);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Geometry_TheTopHalfIsOnTheTopPartsBack_AndTheBottomHalfOnTheOthers() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        double W = tpl.Spec.CanvasWidth, H = tpl.Spec.CanvasHeight;
        var top = CardRenderer.MeldPart(Part("top").BackFace!, tpl);
        var bottom = CardRenderer.MeldPart(Part("bottom").BackFace!, tpl);
        var card = new Rect(0, 0, W, H);

        Assert.True(top.Scale > 1.3, "the melded card should be drawn bigger than a card");
        var upper = new Point(W / 2, H / 4);       // a point in the melded card's top half
        var lower = new Point(W / 2, H * 3 / 4);   // and one in its bottom half
        Assert.True(card.Contains(top.ToCard.Transform(upper)));
        Assert.False(card.Contains(top.ToCard.Transform(lower)));
        Assert.True(card.Contains(bottom.ToCard.Transform(lower)));
        Assert.False(card.Contains(bottom.ToCard.Transform(upper)));

        // Turned a quarter counter-clockwise: the melded card's top edge runs along the pair's left end, its left
        // edge along the bottom.
        Assert.True(top.ToCard.Transform(new Point(W / 2, 0)).X < top.ToCard.Transform(new Point(W / 2, H / 2)).X);
        Assert.True(top.ToCard.Transform(new Point(0, H / 4)).Y > top.ToCard.Transform(new Point(W, H / 4)).Y);
        Assert.False(top.Card.IsMeldBack);   // the melded card drawn inside is a whole, plain card
    });

    [Fact]
    public void Render_TheNameIsOnTheTopHalf_AndThePtOnTheBottomHalf() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var r = new CardRenderer(new SymbolService());
        bool Differs(string half, Action<CardModel> change)
        {
            var a = Part(half, tpl.Name); var b = Part(half, tpl.Name);
            change(b.BackFace!);
            var pa = TestHelpers.Pixels(r.RenderToBitmap(a.BackFace!, tpl));
            var pb = TestHelpers.Pixels(r.RenderToBitmap(b.BackFace!, tpl));
            int n = 0;
            for (int i = 0; i < pa.Length; i += 4)
                if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) > 30) n++;
            return n > 50;
        }
        Assert.True(Differs("top", c => c.Name = "Somebody Else Entirely"));
        Assert.False(Differs("bottom", c => c.Name = "Somebody Else Entirely"));
        Assert.True(Differs("bottom", c => c.Power = "12"));
        Assert.False(Differs("top", c => c.Power = "12"));
    });

    [Fact]
    public void Csv_RowsNamingOneMeldedCard_ArePartners_SharingItsTextAndPrintingOppositeHalves()   // 1.6.6
    {
        const string csv = """
            name,mana,type,rules,pt,meld_name,meld_type,meld_rules,meld_pt
            Gisela the Blade,{2}{R}{W}{W},Legendary Creature — Angel,"Flying, first strike",4/3,Brisela the Nightmare,Legendary Creature — Eldrazi Angel,"Flying, first strike, vigilance, lifelink",9/10
            Bruna the Light,{5}{W}{W},Legendary Creature — Angel,"Flying, vigilance",5/7,brisela the nightmare,,,
            """;
        var cards = ImportService.Parse(csv, null, "Gold Multicolor").Select(i => i.Card).ToList();
        var (gisela, bruna) = (cards[0], cards[1]);

        Assert.True(gisela.IsMeld && bruna.IsMeld);
        Assert.Equal("top", gisela.MeldHalf);
        Assert.Equal("bottom", bruna.MeldHalf);
        Assert.Equal("Bruna the Light", gisela.MeldWith);
        Assert.Equal("Gisela the Blade", bruna.MeldWith);
        Assert.Equal("meld", bruna.DfcStyle);
        // The melded card's text was only on Gisela's row; Bruna's back has it too.
        Assert.Equal("Brisela the Nightmare", bruna.BackFace!.Name);
        Assert.Equal("Flying, first strike, vigilance, lifelink", bruna.BackFace.RulesText);
        Assert.Equal("9", bruna.BackFace.Power);
        Assert.Equal("10", bruna.BackFace.Toughness);
        Assert.True(bruna.BackFace.IsMeldBack && bruna.BackFace.IsMeldBottom);
        Assert.NotSame(gisela.BackFace, bruna.BackFace);   // two cards, two backs
    }

    [Fact]
    public void Csv_AGivenHalfWins_AndThePartnerGetsTheOther()
    {
        const string csv = """
            name,type,meld_with,meld_half
            Gisela,Creature,Bruna,
            Bruna,Creature,Gisela,top
            """;
        var parsed = ImportService.Parse(csv, null, "Gold Multicolor");
        Assert.Equal("bottom", parsed[0].Card.MeldHalf);
        Assert.Equal("top", parsed[1].Card.MeldHalf);
        Assert.All(parsed, i => Assert.True(i.MeldHalfGiven));
    }

    [Fact]
    public void Csv_LookedUpRowsWithNoHalf_LetScryfallPickIt_AndOldColumnsMakeNoMeld()
    {
        var parsed = ImportService.Parse("""
            name,meld_name
            Bruna the Fading Light,Brisela
            Gisela the Broken Blade,Brisela
            """, null, "Gold Multicolor");
        Assert.All(parsed, i => Assert.True(i.NeedsLookup && i.Card.IsMeld && !i.MeldHalfGiven));

        var plain = ImportService.Parse("name,type\nGrizzly Bears,Creature", null, "Gold Multicolor").Single();
        Assert.False(plain.Card.IsDoubleFaced);
        Assert.False(plain.Card.IsMeld);

        // A flip row stays a flip card: meld columns never also give it a back.
        var flip = ImportService.Parse("name,flip_name,meld_name\nBushi,Budoka,Brisela", null, "Gold Multicolor").Single();
        Assert.True(flip.Card.OtherHalf != null && !flip.Card.IsDoubleFaced);
    }

    [Fact]
    public void EveryInstalledFrame_DrawsBothMeldHalves_AndTheMeldedCardPassesInspection() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
            foreach (var half in new[] { "top", "bottom" })
            {
                var back = Part(half, tpl.Name).BackFace!;
                var resolved = TemplateService.ResolveFor(back, tpl);
                Assert.Same(tpl, resolved);   // never turned to a landscape layout
                Assert.True(TestHelpers.HasContent(r.RenderToBitmap(back, resolved)));
                var issues = RenderInspector.InspectCard(r, back, resolved);
                Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
            }
    });
}
