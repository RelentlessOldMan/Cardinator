using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Tier A of the second independent review (1.6.14): work that could be lost, and regressions from 1.6.12/1.6.13.
/// Each test fails with its fix taken out. In the STA-window collection because some swap
/// <see cref="ScryfallClient.TestHttp"/> or the Scryfall throttle.
/// </summary>
[Collection("STAWindows")]
public class Review2TierATests
{
    // --- unsaved state ------------------------------------------------------------------------------

    private static string NewSet(string root, params string[] names)
    {
        var setFile = Path.Combine(root, "S", "S.cardinator");
        Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
        ProjectWriter.Write(setFile, names.Select(n => new CardModel { Name = n }).ToList(), "S", "", "", new SetProfile());
        return setFile;
    }

    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    [Fact]
    public void SaveThenUndoThenANewEdit_IsStillUnsaved()
        => OnAppThread(() =>
        {
            var root = TempRoot("savedidx");
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", NewSet(root, "Before"));
                main.SelectedCard!.Name = "Typed";
                main.CommitHistory();
                Invoke(main, "SaveProject", false);
                main.Undo();
                main.SelectedCard!.RulesText = "A different edit";
                main.CommitHistory();   // lands on the history index the save had
                Assert.True(main.Dirty, "the new edit isn't on disk, but the set says it's saved");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void SavingArtFromOutsideTheSet_DoesNotBringTheUnsavedMarkBack()
        => OnAppThread(() =>
        {
            var root = TempRoot("localize");
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", NewSet(root, "Card"));
                var outside = Path.Combine(root, "pic.png");
                File.WriteAllBytes(outside, TinyPng());
                main.SelectedCard!.ArtPath = outside;   // art from outside the set folder
                Invoke(main, "SaveProject", false);      // copies it into S\art and re-points the card
                Assert.StartsWith(Path.Combine(root, "S", "art"), main.SelectedCard!.ArtPath);
                main.CommitHistory();                    // the undo debounce firing after the save
                Assert.False(main.Dirty, "the save's own art copy counted as a new edit");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ArtArrivingForACardYouClickedAwayFrom_MarksTheSetUnsaved()
        => OnAppThread(() =>
        {
            var root = TempRoot("pasteart");
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", NewSet(root, "A", "B"));
                var a = main.Cards[0];
                main.SelectedCard = main.Cards[1];   // the user clicked away while A's art downloaded
                Assert.False(main.Dirty);
                var pic = Path.Combine(root, "pic.png");
                File.WriteAllBytes(pic, TinyPng());
                Invoke(main, "SetArt", a, pic);
                Assert.False(string.IsNullOrEmpty(a.ArtPath));
                Assert.True(main.Dirty, "closing now would lose A's art without asking");
                Assert.True(main.CanUndo);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ArtArrivingForACardThatWasUndone_GoesNowhere()
        => OnAppThread(() =>
        {
            var root = TempRoot("orphan");
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var gone = new CardModel { Name = "Gone" };   // e.g. Ctrl+Z rebuilt the cards mid-download
                var pic = Path.Combine(root, "pic.png");
                Directory.CreateDirectory(root);
                File.WriteAllBytes(pic, TinyPng());
                main.Dirty = false;
                Invoke(main, "SetArt", gone, pic);
                Assert.Equal("", gone.ArtPath ?? "");
                Assert.Contains("wasn't added", main.Status);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- imports: a stray quote, and "(alt)" -------------------------------------------------------

    [Fact]
    public void Tsv_AStrayQuoteInAField_DoesNotSwallowTheRowsAfterIt()
    {
        var r = ImportService.ParseWithReport("name\trules\nGrizzly Bears\tA 2\" tall bear\nShock\tDeal 2\nGiant Growth\t+3/+3\n", null, "T");
        Assert.Equal(new[] { "Grizzly Bears", "Shock", "Giant Growth" }, r.Cards.Select(c => c.Card.Name));
        Assert.Equal("A 2\" tall bear", r.Cards[0].Card.RulesText);
    }

    [Fact]
    public void Csv_AStrayQuoteInAComment_DoesNotSwallowTheRowsAfterIt()
    {
        var r = ImportService.ParseWithReport("name,mana\nShock,{R}\n# to do, \"the 6 stack\nBolt,{R}\nGrowth,{G}\n# done, \"\n", null, "T");
        Assert.Equal(new[] { "Shock", "Bolt", "Growth" }, r.Cards.Select(c => c.Card.Name));
    }

    [Fact]
    public void Csv_AStrayQuoteInTwoRows_DoesNotJoinTheRowsBetween()
    {
        // Two stray quotes pair up across lines if any quote opens a field — the unclosed-quote fallback can't save this.
        var r = ImportService.ParseWithReport("name,flavor\nGiant,Stands 40\" tall\nShock,Zap\nOgre,A 9\" club\nBolt,Ow\n", null, "T");
        Assert.Equal(new[] { "Giant", "Shock", "Ogre", "Bolt" }, r.Cards.Select(c => c.Card.Name));
    }

    [Fact]
    public void Csv_AStrayQuoteInTheFirstLineComment_DoesNotCrash()
    {
        var r = ImportService.ParseWithReport("# Jacob's 3\" cards\nname,mana\nShock,{R}\n", null, "T");
        Assert.Equal("Shock", Assert.Single(r.Cards).Card.Name);
    }

    [Fact]
    public void Csv_AQuoteThatOpensAFieldButNeverCloses_DoesNotSwallowTheRowsAfterIt()
    {
        var r = ImportService.ParseWithReport("name,rules\nA,\"oops no closing quote\nB,plain\nC,plain\n", null, "T");
        Assert.Equal(new[] { "A", "B", "C" }, r.Cards.Select(c => c.Card.Name));
    }

    [Fact]
    public void Csv_QuotedFieldsWithLineBreaks_StillWork()
    {
        var r = ImportService.ParseWithReport("name,rules\nA,\"line one\nline \"\"two\"\"\"\nB,plain\n", null, "T");
        Assert.Equal(new[] { "A", "B" }, r.Cards.Select(c => c.Card.Name));
        Assert.Equal("line one\nline \"two\"", r.Cards[0].Card.RulesText);
    }

    [Theory]
    [InlineData("Goblin King (alt)", "Goblin King (alt)", "")]
    [InlineData("Fire (v2)", "Fire (v2)", "")]
    [InlineData("Hazmat Suit (Used)", "Hazmat Suit (Used)", "")]
    [InlineData("Sol Ring (cmr) 472", "Sol Ring", "CMR")]
    [InlineData("Lightning Bolt (M10)", "Lightning Bolt", "M10")]
    public void AListLine_KeepsALowercaseBracketWordInTheName_UnlessANumberFollows(string line, string name, string set)
    {
        var c = Assert.Single(ImportService.Parse(line + "\n", null, "T")).Card;
        Assert.Equal((name, set), (c.Name, c.SetCode));
    }

    // --- lookups never replace a back face or other half you made ---------------------------------

    private static List<CardModel> DfcFaces() => new()
    {
        new CardModel { Name = "Delver of Secrets", Layout = "transform", TypeLine = "Creature — Human Wizard" },
        new CardModel { Name = "Insectile Aberration", Layout = "transform", TypeLine = "Creature — Human Insect", RulesText = "Flying" },
    };

    [Fact]
    public void ALookup_KeepsTheBackFaceYouMade()
    {
        var card = new CardModel { Name = "Delver of Secrets" };
        var mine = new CardModel { Name = "My Own Back", ArtPath = @"C:\art\mine.png", RulesText = "Mine" };
        card.BackFace = mine;
        CardDetailsFill.AttachFaces(card, DfcFaces());
        Assert.Same(mine, card.BackFace);
        Assert.Equal(("My Own Back", "Mine", @"C:\art\mine.png"), (mine.Name, mine.RulesText, mine.ArtPath));
        Assert.Null(card.OtherHalf);
    }

    [Fact]
    public void ALookup_OfASplitCardAgain_KeepsTheOtherHalfAndItsArt_FillingOnlyBlanks()
    {
        var card = new CardModel { Name = "Fire" };
        var ice = new CardModel { Name = "Ice", ArtPath = @"C:\art\ice.png" };   // from a CSV's split columns
        card.HalfLayout = "split";
        card.OtherHalf = ice;
        var faces = new List<CardModel>
        {
            new() { Name = "Fire", Layout = "split", ManaCost = "{1}{R}" },
            new() { Name = "Ice", Layout = "split", ManaCost = "{1}{U}", TypeLine = "Instant", RulesText = "Tap target permanent." },
        };
        CardDetailsFill.AttachFaces(card, faces);
        Assert.Same(ice, card.OtherHalf);
        Assert.Equal(@"C:\art\ice.png", ice.ArtPath);
        Assert.Equal(("{1}{U}", "Instant", "Tap target permanent."), (ice.ManaCost, ice.TypeLine, ice.RulesText));
    }

    [Fact]
    public void ALookup_NeverGivesACardWithABackFaceAnOtherHalfToo()
    {
        var card = new CardModel { Name = "Fire" };
        card.BackFace = new CardModel { Name = "Night side" };
        CardDetailsFill.AttachFaces(card, new List<CardModel> { new() { Name = "Fire", Layout = "split" }, new() { Name = "Ice", Layout = "split" } });
        Assert.Null(card.OtherHalf);
    }

    [Fact]
    public void ALookup_StillAttachesABackToACardThatHasNone()
    {
        var card = new CardModel { Name = "Delver of Secrets" };
        CardDetailsFill.AttachFaces(card, DfcFaces());
        Assert.Equal("Insectile Aberration", card.BackFace?.Name);
    }

    // --- a hand-made frame picture is never drawn over ----------------------------------------------

    private static TemplateSpec Procedural() => BuiltInTemplates.All().First(s => !s.CustomFrame);

    [Fact]
    public void AHandMadeFlipLayoutWithNoHash_IsNotDrawnOver()
    {
        var dir = TempRoot("handmade");
        Directory.CreateDirectory(dir);
        try
        {
            var frame = Path.Combine(dir, "frame.png");
            var mine = TinyPng();
            File.WriteAllBytes(frame, mine);   // a hand-made flip/ layout: no .hash, no customFrame flag
            TemplateService.EnsureFrame(Procedural(), frame);
            Assert.Equal(mine, File.ReadAllBytes(frame));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void APictureReplacedInACopiedProceduralTemplate_IsNotDrawnOver()
    {
        var dir = TempRoot("copied");
        Directory.CreateDirectory(dir);
        try
        {
            var spec = Procedural();
            var frame = Path.Combine(dir, "frame.png");
            TemplateService.EnsureFrame(spec, frame);   // the copied template's drawn frame + its .hash
            File.SetLastWriteTimeUtc(frame + ".hash", DateTime.UtcNow.AddMinutes(-10));
            var mine = TinyPng();
            File.WriteAllBytes(frame, mine);             // "replace it with your own transparent PNG"
            spec.Name = "My Frame";                      // ...after changing its name, so the hash no longer matches
            TemplateService.EnsureFrame(spec, frame);
            Assert.Equal(mine, File.ReadAllBytes(frame));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FramesDrawnByEarlierVersions_AreRedrawnForTheModernPanelFix()
        => Assert.True(TemplateSpec.RenderFormatVersion >= 2, "1.6.13 changed what the modern frame draws");

    // --- a set kept in a folder called "Backups" ----------------------------------------------------

    [Fact]
    public void ASetKeptInAFolderNamedBackups_IsAnOrdinarySet()
    {
        var root = TempRoot("backupsfolder");
        try
        {
            var dir = Path.Combine(root, "Backups");
            Directory.CreateDirectory(Path.Combine(dir, "art"));
            var set = Path.Combine(dir, "MySet.cardinator");
            Assert.Null(ProjectBackup.ProjectFileForBackup(set));
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(ProjectBackup.ArtRootFor(set)));

            // A real backup (timestamped) of a set is still one.
            var backup = Path.Combine(root, "Real", "backups", "Real.20261001-120000.cardinator");
            Assert.Equal(Path.Combine(root, "Real"), Path.GetFullPath(ProjectBackup.ArtRootFor(backup)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AHandRenamedFileInASetsBackupsFolder_IsStillTreatedAsABackup()
    {
        var root = TempRoot("handbackup");
        try
        {
            var setDir = Path.Combine(root, "MySet");
            Directory.CreateDirectory(Path.Combine(setDir, "backups"));
            File.WriteAllText(Path.Combine(setDir, "MySet.cardinator"), "{}");
            Assert.Equal("", ProjectBackup.ProjectFileForBackup(Path.Combine(setDir, "backups", "copied by hand.cardinator")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- recovery copies --------------------------------------------------------------------------

    [Fact]
    public void UnsavedWork_GetsARecoveryCopy_ThatASaveRemoves()
        => OnAppThread(() =>
        {
            var root = TempRoot("recovery");
            try
            {
                var setFile = NewSet(root, "Before");
                var main = new Cardinator.MainWindow();   // a real window: recovery copies are on
                Invoke(main, "LoadProjectFile", setFile);
                main.SelectedCard!.Name = "Unsaved edit";
                main.CommitHistory();
                main.WriteRecovery();   // the minute timer, or Windows ending the session
                var copy = RecoveryStore.FileFor(setFile);
                Assert.True(File.Exists(copy));
                Assert.Contains("Unsaved edit", File.ReadAllText(copy));
                var meta = JsonSerializer.Deserialize<RecoveryStore.Meta>(File.ReadAllText(copy + ".meta"))!;
                Assert.Equal(setFile, meta.SetPath);

                Invoke(main, "SaveProject", false);
                Assert.False(File.Exists(copy), "a saved set's recovery copy should be gone");
                main.SuppressClosePrompt = true;
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ARecoveryCopyFromASessionThatEnded_OpensAsTheSetItBelongsTo_Unsaved()
        => OnAppThread(() =>
        {
            var root = TempRoot("recoveryopen");
            string? copy = null;
            try
            {
                var setFile = NewSet(root, "On disk");
                copy = RecoveryStore.FileFor(setFile);
                RecoveryStore.Write(copy, setFile, "S", new[] { new CardModel { Name = "Recovered" } }, "", "", new SetProfile());
                // As if the app that wrote it is gone (Windows restarted).
                File.WriteAllText(copy + ".meta", JsonSerializer.Serialize(new RecoveryStore.Meta(setFile, "S", DateTime.Now, int.MaxValue - 7)));
                var entry = Assert.Single(RecoveryStore.Pending(), e => e.File == copy);

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Assert.True(main.OpenRecovered(entry));
                Assert.Equal("Recovered", main.Cards.Single().Name);
                Assert.True(main.Dirty);
                Assert.Equal(setFile, GetField<string>(main, "_projectPath"));   // Save goes back to the set
            }
            finally
            {
                if (copy != null) RecoveryStore.Delete(copy);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void ARunningSessionsRecoveryCopy_IsNotOffered()
    {
        var copy = RecoveryStore.FileFor(Path.Combine(Path.GetTempPath(), $"live-{Guid.NewGuid():N}.cardinator"));
        try
        {
            RecoveryStore.Write(copy, "x", "x", new[] { new CardModel { Name = "Live" } }, "", "", new SetProfile());
            Assert.DoesNotContain(RecoveryStore.Pending(), e => e.File == copy);   // written by this process
        }
        finally { RecoveryStore.Delete(copy); }
    }

    [Fact]
    public void ADeclinedRecoveryCopy_IsKeptAside_NotDeleted()
    {
        var copy = RecoveryStore.FileFor(Path.Combine(Path.GetTempPath(), $"declined-{Guid.NewGuid():N}.cardinator"));
        RecoveryStore.Write(copy, "x", "x", new[] { new CardModel { Name = "Declined" } }, "", "", new SetProfile());
        RecoveryStore.Decline(copy);
        Assert.False(File.Exists(copy));
        var kept = Directory.GetFiles(Path.Combine(RecoveryStore.Dir, "declined"), Path.GetFileNameWithoutExtension(copy) + ".*");
        Assert.NotEmpty(kept);
        foreach (var k in kept) File.Delete(k);
    }

    // --- Scryfall's rate limits ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.scryfall.com/cards/named?fuzzy=bolt", 500)]
    [InlineData("https://api.scryfall.com/cards/search?q=t:goblin", 500)]
    [InlineData("https://api.scryfall.com/cards/collection", 500)]
    [InlineData("https://api.scryfall.com/cards/random", 500)]
    [InlineData("https://api.scryfall.com/cards/m10/146", 100)]
    [InlineData("https://api.scryfall.com/symbology", 100)]
    public void ScryfallRequests_AreSpacedAsScryfallAsks(string url, int ms)
        => Assert.Equal(TimeSpan.FromMilliseconds(ms), ScryfallClient.GapFor(new Uri(url)));

    [Fact]
    public async Task NamedLookups_AreSentNoFasterThanTwoASecond()
    {
        var times = new List<DateTime>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ =>
        {
            lock (times) times.Add(DateTime.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{ "object": "card", "name": "Bolt", "type_line": "Instant" }""", Encoding.UTF8, "application/json") });
        }));
        try
        {
            var client = new ScryfallClient();
            for (int i = 0; i < 3; i++) await client.LookupFacesAsync("Bolt " + i);
            Assert.Equal(3, times.Count);
            for (int i = 1; i < times.Count; i++)
                Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMilliseconds(450), $"gap {i} was {(times[i] - times[i - 1]).TotalMilliseconds} ms");
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ATooManyRequestsAnswer_WaitsOutScryfallsLockout()
    {
        var times = new List<DateTime>();
        var saved = ScryfallClient.TooManyRequestsWait;
        ScryfallClient.TooManyRequestsWait = TimeSpan.FromMilliseconds(1500);
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ =>
        {
            lock (times) times.Add(DateTime.UtcNow);
            return Task.FromResult(times.Count == 1
                ? new HttpResponseMessage((HttpStatusCode)429)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{ "object": "card", "name": "Bolt", "type_line": "Instant" }""", Encoding.UTF8, "application/json") });
        }));
        try
        {
            var faces = await new ScryfallClient().LookupFacesAsync("Bolt");
            Assert.Equal("Bolt", faces[0].Name);
            Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(1400), "retried before the lockout was over");
        }
        finally { ScryfallClient.TestHttp = null; ScryfallClient.TooManyRequestsWait = saved; }
    }

    // --- cards added through Scryfall search -------------------------------------------------------

    [Fact]
    public void ASearchResultsBackFace_GetsTheFrontsFrame()
    {
        var json = """
        { "data": [ { "name": "Delver of Secrets // Insectile Aberration", "layout": "transform", "card_faces": [
            { "name": "Delver of Secrets", "type_line": "Creature — Human Wizard", "oracle_text": "" },
            { "name": "Insectile Aberration", "type_line": "Creature — Human Insect", "oracle_text": "Flying" } ] } ] }
        """;
        var card = Assert.Single(ScryfallMapper.MapSearch(json));
        Assert.NotNull(card.BackFace);
        CardDetailsFill.TakeFrame(card, "Crimson Red");
        Assert.Equal(("Crimson Red", "Crimson Red"), (card.TemplateName, card.BackFace!.TemplateName));
    }

    // --- Frame Design: Esc / Enter in an open dropdown ----------------------------------------------

    [Fact]
    public void FrameDesign_EscOrEnterInAnOpenDropdown_LeavesTheWindowAlone()
        => OnAppThread(() =>
        {
            Assert.False(FrameDesignWindow.KeysCloseWindow(new ComboBoxItem()));
            Assert.False(FrameDesignWindow.KeysCloseWindow(new TextBox()));
            Assert.True(FrameDesignWindow.KeysCloseWindow(new ComboBox()));
            Assert.True(FrameDesignWindow.KeysCloseWindow(new Button()));
        });

    // --- downloads that time out, and a cancelled meld lookup ---------------------------------------

    [Fact]
    public async Task ADownloadThatTimesOut_IsAnOrdinaryFailure_NotACancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var saved = ImageIntake.DownloadTimeout;
        ImageIntake.DownloadTimeout = TimeSpan.FromMilliseconds(400);
        var accept = listener.AcceptTcpClientAsync();   // accepts, then never answers
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ImageIntake.DownloadAsync($"http://127.0.0.1:{port}/slow.png"));
            Assert.IsNotAssignableFrom<OperationCanceledException>(ex);
            Assert.Contains("too long", ex.Message);
        }
        finally
        {
            ImageIntake.DownloadTimeout = saved;
            listener.Stop();
            try { (await accept).Dispose(); } catch { }
        }
    }

    [Fact]
    public async Task AMeldLookupThatFinishesAfterCancel_LeavesTheCardAlone()
    {
        using var cts = new CancellationTokenSource();
        // The reply arrives in full, and only then does the dialog close (before the lookup's continuation runs).
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new CancelAfterReading("""{ "object": "card", "name": "Brisela, Voice of Nightmares", "type_line": "Legendary Creature — Eldrazi Angel", "collector_number": "15b" }""", cts),
        })));
        try
        {
            var card = new CardModel { Name = "Bruna, the Fading Light" };
            var part = new CardModel { Name = "Bruna, the Fading Light", MeldResultUrl = "https://api.scryfall.com/cards/emn/15b", CollectorNumber = "15" };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CardDetailsFill.AttachMeldAsync(new ScryfallClient(), card, part, cts.Token));
            Assert.Null(card.BackFace);
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    // --- helpers ----------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    /// <summary>A JSON body that cancels a token once it has been read out completely.</summary>
    private sealed class CancelAfterReading(string json, CancellationTokenSource cts) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
            cts.Cancel();
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static void Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private static T GetField<T>(object target, string field)
        => (T)target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(target)!;

    private static void OnAppThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri("/Cardinator;component/Theme.xaml", UriKind.Relative)));
                try { action(); }
                finally { TestHelpers.EndUiThread(); }
            }
            catch (System.Reflection.TargetInvocationException ex) { captured = ex.InnerException ?? ex; }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
