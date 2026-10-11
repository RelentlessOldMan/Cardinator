using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.27: command-line work on sets (<see cref="SetCommands"/>) — import a deck into a set, render it, restyle it,
/// describe it — so scripting Cardinator needs no throwaway code. Each goes through what the window uses, so a set
/// made here opens there as if made there. Offline: Moxfield and Scryfall are stood in for. In the STA-window
/// collection because they swap <see cref="ScryfallClient.TestHttp"/> and the console.
/// </summary>
[Collection("STAWindows")]
public class SetCommandsTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"cardinator-setcmd-{Guid.NewGuid():N}");

    private static string Frame(int i = 0) => new TemplateService().LoadAll()[i].Name;

    /// <summary>A list whose cards are complete (no Scryfall lookup), with a picture named after one of them.</summary>
    private static (string list, string artDir) ListWithArt(string root)
    {
        Directory.CreateDirectory(root);
        var list = Path.Combine(root, "deck.csv");
        File.WriteAllText(list, "name,type,text\nHero,Creature — Human,Brave.\nVillain,Creature — Horror,Scary.\nSpell,Instant,Zap.\n");
        var artDir = Path.Combine(root, "my art");
        Directory.CreateDirectory(artDir);
        File.WriteAllBytes(Path.Combine(artDir, "Hero.png"), TestHelpers.PngBytes(40, 30));
        return (list, artDir);
    }

    private static (int code, string output) Capture(Func<int> run)
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { return (run(), sw.ToString()); }
        finally { Console.SetOut(old); }
    }

    [Fact]
    public void Import_MakesASetTheWindowOpens_WithTheFrame_AndYourArtCopiedIn()
    {
        var root = TempRoot();
        try
        {
            var (list, artDir) = ListWithArt(root);
            var set = Path.Combine(root, "concept", "Deck.cardinator");
            var frame = Frame(1);

            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "frame=" + frame, "art=" + artDir, "noart" }));

            var project = CardProject.LoadResolved(set, out var folder);
            Assert.Equal(new[] { "Hero", "Villain", "Spell" }, project.Cards.Select(c => c.Name));
            Assert.All(project.Cards, c => Assert.Equal(frame, c.TemplateName));
            Assert.Equal(frame, project.DefaultTemplate);
            var hero = project.Cards[0];
            Assert.StartsWith(Path.Combine(folder, "art"), hero.ArtPath);   // copied into the set: it travels with it
            Assert.True(File.Exists(hero.ArtPath));
            Assert.Equal("", project.Cards[1].ArtPath);
            Assert.Equal(Path.Combine("art", Path.GetFileName(hero.ArtPath)), CardProject.Load(set).Cards[0].ArtPath);   // saved relative
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Import_FrameIsForCardsTheListGivesNone()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.csv");
            File.WriteAllText(list, $"name,type,frame\nHero,Creature,{Frame(2)}\nVillain,Creature,\n");
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "frame=" + Frame(1), "noart" }));
            Assert.Equal(new[] { Frame(2), Frame(1) }, CardProject.Load(set).Cards.Select(c => c.TemplateName));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Import_NeverTouchesAnExistingSet_UnlessAppending_ThenAddsOnlyNewNames()
    {
        var root = TempRoot();
        try
        {
            var (list, _) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            new CardProject { Name = "Mine", Cards = { new CardModel { Name = "Hero", TypeLine = "Mine" } } }.Save(set);
            var before = File.ReadAllBytes(set);

            Assert.Equal(2, SetCommands.RunImport(list, set, new[] { "noart" }));
            Assert.Equal(before, File.ReadAllBytes(set));

            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "noart", "append" }));
            var project = CardProject.Load(set);
            Assert.Equal("Mine", project.Name);
            Assert.Equal(new[] { "Hero", "Villain", "Spell" }, project.Cards.Select(c => c.Name));
            Assert.Equal("Mine", project.Cards[0].TypeLine);   // the set's own Hero, not the list's
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, "backups")).Any());   // backed up before the save
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    [Fact]
    public void Import_FromAMoxfieldLink_FillsTheDeckFromScryfall_CommanderFirst()
    {
        var root = TempRoot();
        const string deck = """{ "name": "Test Deck", "boards": { "mainboard": { "cards": { "a": { "quantity": 1, "card": { "name": "Sol Ring" } } } }, "commanders": { "cards": { "b": { "quantity": 1, "card": { "name": "Urza, Lord High Artificer" } } } } } }""";
        string? askedFor = null;
        SetCommands.TestFetch = id => { askedFor = id; return Task.FromResult(deck); };
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            var q = Uri.UnescapeDataString(req.RequestUri!.Query);
            var name = q.Contains("Sol Ring", StringComparison.OrdinalIgnoreCase) ? "Sol Ring" : "Urza, Lord High Artificer";
            var json = req.RequestUri.AbsolutePath.EndsWith("/named")
                ? $$"""{ "object": "card", "name": "{{name}}", "type_line": "Artifact", "oracle_text": "Text of {{name}}.", "set": "cmm", "collector_number": "1", "set_type": "core" }"""
                : """{ "object": "list", "has_more": false, "data": [] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }));
        try
        {
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport("https://moxfield.com/decks/YFifATPTSHG3xPz_COErPA", set, new[] { "noart" }));
            Assert.Equal("YFifATPTSHG3xPz_COErPA", askedFor);
            var cards = CardProject.Load(set).Cards;
            Assert.Equal(new[] { "Urza, Lord High Artificer", "Sol Ring" }, cards.Select(c => c.Name));
            Assert.Equal("Text of Sol Ring.", cards[1].RulesText);

            // --moxfield alone: the plain decklist, or the deck's JSON for a .json name.
            var txt = Path.Combine(root, "list.txt");
            Assert.Equal(0, SetCommands.RunMoxfield("YFifATPTSHG3xPz_COErPA", txt));
            Assert.Equal("# Test Deck\n\n1 Urza, Lord High Artificer\n1 Sol Ring", File.ReadAllText(txt));
            var json = Path.Combine(root, "deck.json");
            Assert.Equal(0, SetCommands.RunMoxfield("https://moxfield.com/decks/YFifATPTSHG3xPz_COErPA", json));
            Assert.Equal(deck, File.ReadAllText(json));
            Assert.Equal(2, SetCommands.RunMoxfield("not a deck", txt));
        }
        finally
        {
            SetCommands.TestFetch = null;
            ScryfallClient.TestHttp = null;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Restyle_ChangesOnlyTheNamedCards_AndTheWholeSetMovesTheHouseFrame()
    {
        var root = TempRoot();
        try
        {
            var (list, artDir) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "frame=" + Frame(0), "noart" }));

            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "frame=" + Frame(1), "only=her|spell" }));
            var p = CardProject.Load(set);
            Assert.Equal(new[] { Frame(1), Frame(0), Frame(1) }, p.Cards.Select(c => c.TemplateName));
            Assert.Equal(Frame(0), p.DefaultTemplate);   // a few cards: the house frame stays

            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "frame=" + Frame(2), "art=" + artDir }));
            p = CardProject.LoadResolved(set, out _);
            Assert.All(p.Cards, c => Assert.Equal(Frame(2), c.TemplateName));
            Assert.Equal(Frame(2), p.DefaultTemplate);
            Assert.True(File.Exists(p.Cards[0].ArtPath));   // Hero got its picture; the others had none to match
            Assert.Equal("", p.Cards[2].ArtPath);

            Assert.Equal(3, SetCommands.RunRestyle(set, new[] { "frame=x", "only=nobody" }));
            Assert.Equal(2, SetCommands.RunRestyle(set, Array.Empty<string>()));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Restyle_Art_ReachesADoubleFacedCardsBack()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Deck.cardinator");
            new CardProject { Cards = { new CardModel { Name = "Front", BackFace = new CardModel { Name = "Explore the Back" } } } }.Save(set);
            var artDir = Path.Combine(root, "art in");
            Directory.CreateDirectory(artDir);
            File.WriteAllBytes(Path.Combine(artDir, "Explore the Back.png"), TestHelpers.PngBytes(40, 30));

            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "art=" + artDir }));
            var card = CardProject.LoadResolved(set, out _).Cards.Single();
            Assert.True(File.Exists(card.BackFace!.ArtPath));
            Assert.Equal("", card.ArtPath);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Restyle_Titles_ShowTheNewTitle_WithTheRealNameAsSubtitle_AndRunAgainSafely()
    {
        var root = TempRoot();
        try
        {
            var (list, _) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "noart" }));
            var titles = Path.Combine(root, "titles.tsv");
            File.WriteAllText(titles, "# card<TAB>title\nHero\tThe Collector\nSpell\tThe Draining Spell\n");

            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "titles=" + titles }));
            var cards = CardProject.Load(set).Cards;
            Assert.Equal(new[] { ("The Collector", "Hero"), ("Villain", ""), ("The Draining Spell", "Spell") },
                cards.Select(c => (c.Name, c.Subtitle)));

            // Again, with a changed title: found by its real name (now the subtitle), not stacked.
            File.WriteAllText(titles, "Hero\tThe Archivist\n");
            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "titles=" + titles }));
            Assert.Equal(("The Archivist", "Hero"), (CardProject.Load(set).Cards[0].Name, CardProject.Load(set).Cards[0].Subtitle));

            // only= still finds a retitled card by its real name.
            Assert.Equal(new[] { "The Archivist" }, SetCommands.Only(CardProject.Load(set).Cards, "hero").Select(c => c.Name));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Restyle_SetsTheFooterAndSetSymbol_OnEveryCard()
    {
        var root = TempRoot();
        try
        {
            var (list, _) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "noart" }));
            var symbol = Path.Combine(root, "apple.png");
            File.WriteAllBytes(symbol, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));

            Assert.Equal(2, SetCommands.RunRestyle(set, new[] { "symbol=" + Path.Combine(root, "missing.png") }));
            Assert.Equal(0, SetCommands.RunRestyle(set, new[] { "setcode=JA", "number=666", "symbol=" + symbol }));

            var cards = CardProject.LoadResolved(set, out _).Cards;
            Assert.All(cards, c =>
            {
                Assert.Equal(("JA", "666"), (c.SetCode, c.CollectorNumber));
                Assert.True(File.Exists(c.SetSymbolPath), c.SetSymbolPath);
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Retitle_RulesTextNamesTheCardByItsNewTitle_ShortNameForShortName()
    {
        var card = new CardModel
        {
            Name = "Ulamog, the Ceaseless Hunger",
            RulesText = "When you cast this spell, exile two target permanents.\nWhenever Ulamog attacks, defending player exiles "
                + "the top twenty cards of their library.\nUlamog, the Ceaseless Hunger can't be countered. Ulamogs stay.",
        };
        Assert.Equal(1, SetCommands.Retitle(new[] { card }, new[] { "Ulamog, the Ceaseless Hunger\tAdam, the First Man" }));
        Assert.Equal("When you cast this spell, exile two target permanents.\nWhenever Adam attacks, defending player exiles "
            + "the top twenty cards of their library.\nAdam, the First Man can't be countered. Ulamogs stay.", card.RulesText);

        // Retitled again: the old title in the text becomes the new one.
        SetCommands.Retitle(new[] { card }, new[] { "Ulamog, the Ceaseless Hunger\tThe Exorcist" });
        Assert.StartsWith("When you cast this spell, exile two target permanents.\nWhenever The Exorcist attacks", card.RulesText);
        Assert.Contains("\nThe Exorcist can't be countered.", card.RulesText);

        var land = new CardModel { Name = "Gemstone Caverns", RulesText = "If Gemstone Caverns is in your opening hand…" };
        SetCommands.Retitle(new[] { land }, new[] { "Gemstone Caverns\tRosie's Emporium" });
        Assert.Equal("If Rosie's Emporium is in your opening hand…", land.RulesText);
    }

    [Fact]
    public void RenderSet_RendersTheChosenCards_OrSheets()
    {
        var root = TempRoot();
        try
        {
            var (list, _) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "noart" }));

            var cardsOut = Path.Combine(root, "cards");
            Assert.Equal(0, SetCommands.RunRenderSet(set, cardsOut, new[] { "only=hero|villain", "nosym" }));
            Assert.Equal(new[] { "001_hero.png", "002_villain.png" },
                Directory.GetFiles(cardsOut).Select(Path.GetFileName).Order());

            var sheetOut = Path.Combine(root, "sheets");
            Assert.Equal(0, SetCommands.RunRenderSet(set, sheetOut, new[] { "sheet", "nosym" }));
            Assert.Single(Directory.GetFiles(sheetOut, "*.png"));

            Assert.Equal(3, SetCommands.RunRenderSet(set, cardsOut, new[] { "only=nobody", "nosym" }));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Info_ListsEachCardsFrameAndArt()
    {
        var root = TempRoot();
        try
        {
            var (list, artDir) = ListWithArt(root);
            var set = Path.Combine(root, "Deck.cardinator");
            Assert.Equal(0, SetCommands.RunImport(list, set, new[] { "art=" + artDir, "noart", "frame=Not A Frame" }));

            var (code, output) = Capture(() => SetCommands.RunInfo(set, Array.Empty<string>()));
            Assert.Equal(0, code);
            Assert.Contains("1\tHero\tNot A Frame (not installed)\tok", output);
            Assert.Contains("2\tVillain\tNot A Frame (not installed)\tnone", output);

            var (_, frames) = Capture(() => SetCommands.RunListFrames(Array.Empty<string>()));
            Assert.Contains(Frame(0), frames.Split(Environment.NewLine));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
