using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The eighth independent review (1.7.4): manual flow, a new user's first steps, scripts and AI driving the command
/// line, and edge cases that lose work. Save as after the set's folder moved; a hand-made frame thrown away when it
/// wouldn't read; fields from a newer Cardinator dropped on save; two commands saving one set at once; backups ranked
/// by the file clock; art that won't decode passing every check; stale pictures left in an export folder; cards
/// Scryfall didn't find never named; Set settings missing back faces and Undo; Undo leaving the face being edited;
/// the details window's X keeping edits; rarity Replace can't clear; "UNTITLED PROJECT" on card backs; very long
/// names; strict arguments and honest exit codes. Each test fails with its fix taken out. In the STA-window collection
/// because most build the main window or swap the console and <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class Review8Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    private static Cardinator.MainWindow NewMain()
    {
        var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
        main.Cards.Clear();
        return main;
    }

    private static IReadOnlyList<ValidationIssue> Validate(CardModel card)
        => CardValidator.Validate(card, BuiltInTemplates.All().First());

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Cardinator", "Cardinator.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static (int code, string output, string errors) Capture(Func<int> run)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o);
        Console.SetError(e);
        try { return (run(), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    // --- 1: Save as after the set's folder moved ---------------------------------------------------------

    [Fact]
    public void SaveAs_AfterTheSetFolderMoved_FindsTheArtWhereItWentWithIt()
    {
        var root = TempRoot("moved");
        try
        {
            var old = Path.Combine(root, "old");
            Directory.CreateDirectory(Path.Combine(old, "art"));
            File.WriteAllBytes(Path.Combine(old, "art", "pic.png"), TestHelpers.PngBytes(20, 20));
            var card = new CardModel { Name = "A", ArtPath = Path.Combine(old, "art", "pic.png") };
            var moved = Path.Combine(root, "moved");
            Directory.Move(old, moved);   // the window still holds the old paths

            var copy = Path.Combine(moved, "Copy.cardinator");
            ProjectWriter.Write(copy, new[] { card }, "Copy", moved, "", new SetProfile(), previousFolder: old);
            var loaded = CardProject.LoadResolved(copy, out _);
            Assert.True(File.Exists(loaded.Cards[0].ArtPath), loaded.Cards[0].ArtPath);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- 2: a hand-made frame that won't read is kept ------------------------------------------------------

    [Fact]
    public void AHandMadeFrameOnADrawnSpec_ThatWontRead_IsSetAside_NotDeleted()
        => OnAppThread(() =>
        {
            var root = TempRoot("handmade-bad");
            try
            {
                var spec = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame).Spec.Clone();
                spec.Name = "Painted " + Guid.NewGuid().ToString("N")[..6];
                var dir = Directory.CreateDirectory(Path.Combine(root, "painted")).FullName;
                spec.Save(Path.Combine(dir, "template.json"));
                var picture = Encoding.ASCII.GetBytes("not a png, but someone's only copy");
                File.WriteAllBytes(Path.Combine(dir, "frame.png"), picture);   // no .hash: put there by hand

                new TemplateService().LoadFrom(root);
                Assert.Contains(Directory.GetFiles(dir), f => File.ReadAllBytes(f).SequenceEqual(picture));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- 3: fields from a newer Cardinator survive ---------------------------------------------------------

    [Fact]
    public void FieldsANewerCardinatorWrote_SurviveOpenAndSave()
    {
        var root = TempRoot("future");
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Set.cardinator");
            File.WriteAllText(set, """
                { "name": "Set", "formatVersion": 1, "futureSet": 7,
                  "profile": { "artist": "Me", "futureProfile": "p", "cardBack": { "style": "designed", "futureBack": true } },
                  "cards": [ { "name": "A", "futureCard": { "x": 1 } } ] }
                """);
            var p = CardProject.LoadResolved(set, out _);
            ProjectWriter.Write(set, p.Cards, p.Name, root, p.DefaultTemplate ?? "", p.Profile, unknown: p.Unknown);
            Assert.Equal(0, Capture(() => SetCommands.RunRestyle(set, new[] { "setcode=NEW" })).code);
            var json = File.ReadAllText(set);
            foreach (var field in new[] { "futureSet", "futureProfile", "futureBack", "futureCard" })
                Assert.Contains(field, json);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheWindow_KeepsTheSetsFieldsFromANewerVersion_WhenItSaves()
        => OnAppThread(() =>
        {
            var root = TempRoot("future-win");
            try
            {
                Directory.CreateDirectory(root);
                var set = Path.Combine(root, "Set.cardinator");
                File.WriteAllText(set, """{ "name": "Set", "formatVersion": 1, "futureSet": 7, "cards": [ { "name": "A" } ] }""");
                var main = NewMain();
                Assert.True((bool)Invoke(main, "LoadProjectFile", set)!);
                main.Cards[0].Name = "B";
                Assert.True((bool)Invoke(main, "SaveProject", false)!);
                Assert.Contains("futureSet", File.ReadAllText(set));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    /// <summary>The fields a set file holds. When this changes, ask whether an older Cardinator reading the new file
    /// would get it wrong (not merely miss a field — unknown fields are kept): if so, raise
    /// <see cref="CardProject.CurrentFormatVersion"/> so it warns before saving over it. Then update the list.</summary>
    [Fact]
    public void TheSavedShape_IsPinned_WithItsFormatVersion()
    {
        static IEnumerable<string> Saved(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null
                        && p.GetCustomAttribute<System.Text.Json.Serialization.JsonExtensionDataAttribute>() == null)
            .Select(p => t.Name + "." + p.Name).OrderBy(n => n, StringComparer.Ordinal);
        var shape = string.Join(" ", new[] { typeof(CardProject), typeof(CardModel), typeof(SetProfile), typeof(CardBackDesign) }.SelectMany(Saved));
        Assert.Equal(1, CardProject.CurrentFormatVersion);
        Assert.True(shape == PinnedShape, "The saved shape changed — see this test's summary. It is now: " + shape);
    }

    private const string PinnedShape =
        "CardProject.ArtBaseDir CardProject.Cards CardProject.DefaultTemplate CardProject.FormatVersion "
        + "CardProject.Name CardProject.Profile CardModel.AdventureCost CardModel.AdventureName "
        + "CardModel.AdventureText CardModel.AdventureType CardModel.ArtOffsetX CardModel.ArtOffsetY "
        + "CardModel.ArtPath CardModel.ArtScale CardModel.Artist CardModel.BackFace CardModel.CollectorNumber "
        + "CardModel.Copyright CardModel.Defense CardModel.DfcStyle CardModel.FlavorText CardModel.HalfLayout "
        + "CardModel.HalfTemplateName CardModel.LandSymbol CardModel.LandSymbolStyle CardModel.Layout "
        + "CardModel.Loyalty CardModel.ManaCost CardModel.MeldHalf CardModel.MeldWith CardModel.Name "
        + "CardModel.Orientation CardModel.OtherHalf CardModel.Power CardModel.Rarity CardModel.RulesText "
        + "CardModel.SetCode CardModel.SetSymbolPath CardModel.Subtitle CardModel.TemplateName "
        + "CardModel.Toughness CardModel.TypeLine SetProfile.Artist SetProfile.CardBack SetProfile.Copyright "
        + "SetProfile.Rarity SetProfile.SetCode SetProfile.SetSymbolPath CardBackDesign.Accent "
        + "CardBackDesign.Color CardBackDesign.ImagePath CardBackDesign.Style CardBackDesign.UseSetSymbol "
        + "CardBackDesign.Wordmark";

    // --- 4: two commands on one set ------------------------------------------------------------------------

    [Fact]
    public void ASetHeldByOneCommand_MakesTheNextWait_AndGiveUpWithoutSaving()
    {
        var root = TempRoot("lock");
        var wait = SetLock.Wait;
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Set.cardinator");
            ProjectWriter.Write(set, new[] { new CardModel { Name = "A" } }, "Set", root, "", new SetProfile());
            var before = File.ReadAllText(set);
            SetLock.Wait = TimeSpan.FromMilliseconds(300);
            using (var held = SetLock.Take(set))
            {
                Assert.NotNull(held);
                Assert.Null(SetLock.Take(set));
                var (code, _, errors) = Capture(() => SetCommands.RunRestyle(set, new[] { "setcode=NEW" }));
                Assert.Equal(1, code);
                Assert.Contains("nothing saved", errors);
                Assert.Equal(before, File.ReadAllText(set));
            }
            using var again = SetLock.Take(set);   // freed when the holder ends
            Assert.NotNull(again);
        }
        finally { SetLock.Wait = wait; try { Directory.Delete(root, true); } catch { } }
    }

    // --- 5: backups ranked by when they were made ----------------------------------------------------------

    [Fact]
    public void Backups_RankByTheTimeInTheirNames_NotTheFileClock()
    {
        var root = TempRoot("bk-order");
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Set.cardinator");
            var dir = ProjectBackup.BackupDir(set);
            Directory.CreateDirectory(dir);
            string B(string stamp, DateTime mtime)
            {
                var p = Path.Combine(dir, $"Set.{stamp}.cardinator");
                File.WriteAllText(p, stamp);
                File.SetLastWriteTimeUtc(p, mtime);
                return p;
            }
            var older = B("20260101-120000", DateTime.UtcNow);                 // copied late: a new file time
            var newer = B("20260102-120000", DateTime.UtcNow.AddYears(-3));     // a restored set: an old file time
            var ahead = B(DateTime.Now.AddYears(2).ToString("yyyyMMdd-HHmmss"), DateTime.UtcNow);   // the clock ran ahead
            Assert.Equal(new[] { newer, older, ahead }, ProjectBackup.ListBackups(set));

            // The backup a save just made is kept, whatever the file clock or a future name says: here one made while
            // the clock ran an hour fast ranks above it, and keep: 1 would otherwise prune the one just made.
            var fast = B(DateTime.Now.AddHours(1).ToString("yyyyMMdd-HHmmss"), DateTime.UtcNow);
            File.WriteAllText(set, "now");
            File.SetLastWriteTimeUtc(set, DateTime.UtcNow.AddYears(-5));
            var made = ProjectBackup.BackupExisting(set, DateTime.Now.ToString("yyyyMMdd-HHmmss"), keep: 1, keepDays: 0);
            Assert.NotNull(made);
            Assert.True(File.Exists(made));
            Assert.Contains(made, ProjectBackup.ListBackups(set));
            Assert.Contains(fast, ProjectBackup.ListBackups(set));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- 6: art that won't decode ----------------------------------------------------------------------------

    [Fact]
    public void ArtThatWontDecode_IsAnError_InChecksAndInfo()
    {
        var root = TempRoot("badart");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "art"));
            var bad = Path.Combine(root, "art", "half.png");
            File.WriteAllBytes(bad, TestHelpers.PngBytes(30, 30)[..40]);   // a download cut short
            var empty = Path.Combine(root, "art", "empty.png");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            Assert.Equal(ArtCheck.Unreadable, ArtCheck.Problem(bad));
            Assert.Equal(ArtCheck.Unreadable, ArtCheck.Problem(empty));
            Assert.Null(ArtCheck.Problem(null));
            var good = Path.Combine(root, "art", "good.png");
            File.WriteAllBytes(good, TestHelpers.PngBytes(30, 30));
            Assert.Null(ArtCheck.Problem(good));

            var card = new CardModel { Name = "A", TypeLine = "Instant", ArtPath = bad };
            Assert.Contains(Validate(card), i => i.Code == "art-unreadable" && i.Severity == IssueSeverity.Error);

            var set = Path.Combine(root, "Set.cardinator");
            ProjectWriter.Write(set, new[] { card }, "Set", root, "", new SetProfile());
            Assert.Contains("unreadable", Capture(() => SetCommands.RunInfo(set, Array.Empty<string>())).output);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- 7: stale pictures in an export folder ---------------------------------------------------------------

    [Fact]
    public void AWholeSetExport_RemovesTheCardsAnEarlierOneLeft_APartialOneDoesnt()
        => OnAppThread(() =>
        {
            var root = TempRoot("stale");
            try
            {
                Directory.CreateDirectory(root);
                var templates = new TemplateService().LoadAll();
                File.WriteAllBytes(Path.Combine(root, "007_deleted-card.png"), TestHelpers.PngBytes(4, 4));
                File.WriteAllBytes(Path.Combine(root, "my-notes.png"), TestHelpers.PngBytes(4, 4));
                var cards = new[] { new CardModel { Name = "One" }, new CardModel { Name = "Two" } };

                BatchService.ExportAll(cards.Take(1).ToList(), templates, root, new SymbolService(), numbers: new[] { 2 });
                Assert.True(File.Exists(Path.Combine(root, "002_one.png")));          // its number in the whole set
                Assert.True(File.Exists(Path.Combine(root, "007_deleted-card.png")));

                BatchService.ExportAll(cards, templates, root, new SymbolService(), wholeSet: true);
                Assert.False(File.Exists(Path.Combine(root, "007_deleted-card.png")));
                Assert.True(File.Exists(Path.Combine(root, "my-notes.png")));          // not one of ours
                Assert.True(File.Exists(Path.Combine(root, "001_one.png")));
                Assert.True(File.Exists(Path.Combine(root, "002_two.png")));

                // A shorter print sheet run drops the earlier run's later pages.
                File.WriteAllBytes(Path.Combine(root, "sheet_page_03.png"), TestHelpers.PngBytes(4, 4));
                var page = SheetExporter.Compose(cards.ToList(), templates, new SymbolService(), PageSpec.Letter);
                var written = SheetExporter.Save(page, root);
                Assert.False(File.Exists(Path.Combine(root, "sheet_page_03.png")));
                Assert.All(written, p => Assert.True(File.Exists(p)));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- 8 & 30: cards Scryfall didn't find, and Scryfall offline -------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("""{ "object": "error", "status": 404 }""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NoneInCollection() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{ "object": "list", "not_found": [], "data": [] }""", Encoding.UTF8, "application/json") };

    [Fact]
    public void Import_NamesTheCardsScryfallDidntFind_AndExits4()
    {
        var root = TempRoot("notfound");
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req => Task.FromResult(
            req.RequestUri!.AbsolutePath.Contains("collection") ? NoneInCollection() : NotFound())));
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.txt");
            File.WriteAllText(list, "1 Lightnig Bolt\n");
            var set = Path.Combine(root, "Set.cardinator");
            var (code, _, errors) = Capture(() => SetCommands.RunImport(list, set, new[] { "noart" }));
            Assert.Equal(4, code);
            Assert.Contains("Lightnig Bolt", errors);
            Assert.True(File.Exists(set));   // saved: the names are there to fix
            var card = CardProject.LoadResolved(set, out _).Cards.Single();
            Assert.Contains(Validate(card), i => i.Code == "name-only");
        }
        finally { ScryfallClient.TestHttp = null; try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Import_WithScryfallUnreachable_SavesNothing_AndExits1()
    {
        var root = TempRoot("offline");
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("No such host is known.")));
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.txt");
            File.WriteAllText(list, "1 Lightning Bolt\n");
            var set = Path.Combine(root, "Set.cardinator");
            var (code, _, errors) = Capture(() => SetCommands.RunImport(list, set, new[] { "noart" }));
            Assert.Equal(1, code);
            Assert.Contains("nothing saved", errors);
            Assert.False(File.Exists(set));
        }
        finally { ScryfallClient.TestHttp = null; try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ACardThatIsOnlyAName_IsWarnedAbout_AFilledOneIsnt()
    {
        Assert.Contains(Validate(new CardModel { Name = "Lightnig Bolt" }), i => i.Code == "name-only");
        Assert.DoesNotContain(Validate(new CardModel { Name = "Bolt", TypeLine = "Instant" }), i => i.Code == "name-only");
    }

    [Fact]
    public void Batch_WithAnArtFolderThatIsntThere_IsAUsageError()
    {
        var root = TempRoot("batch-art");
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.csv");
            File.WriteAllText(list, "name,type\nHero,Creature\n");
            var (code, _, errors) = Capture(() => SelfTest.RunBatch(list, Path.Combine(root, "out"), Path.Combine(root, "no such art")));
            Assert.Equal(2, code);
            Assert.Contains("not found", errors);
            Assert.Equal(2, Capture(() => SelfTest.RunSheet(list, Path.Combine(root, "out"), Path.Combine(root, "no such art"), false)).code);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RenderSet_SaysWhichArtIsMissing_OnTheErrorOutput()
        => OnAppThread(() =>
        {
            var root = TempRoot("rs-art");
            try
            {
                Directory.CreateDirectory(root);
                var set = Path.Combine(root, "Set.cardinator");
                ProjectWriter.Write(set, new[] { new CardModel { Name = "Hero", TypeLine = "Creature", ArtPath = Path.Combine(root, "gone.png") } },
                    "Set", root, "", new SetProfile());
                var (code, _, errors) = Capture(() => SetCommands.RunRenderSet(set, Path.Combine(root, "out"), Array.Empty<string>()));
                Assert.Equal(0, code);
                Assert.Contains("Hero's art", errors);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- 9: Set settings reach back faces -------------------------------------------------------------------

    [Fact]
    public void SetSettings_FillAndReplace_ReachTheBackFace()
        => OnAppThread(() =>
        {
            var main = NewMain();
            var card = new CardModel { Name = "Front", BackFace = new CardModel { Name = "Back" } };
            main.Cards.Add(card);
            main.SelectedCard = card;
            main.CommitHistory();

            main.ApplySetSettings(new SetProfile { Artist = "Me", SetCode = "SET" }, "", SetSettingsMode.FillBlanks);
            Assert.Equal("Me", card.BackFace!.Artist);
            Assert.Equal("SET", card.BackFace.SetCode);

            main.ApplySetSettings(new SetProfile { Artist = "You", SetCode = "SET" }, "", SetSettingsMode.Replace);
            Assert.Equal("You", card.Artist);
            Assert.Equal("You", card.BackFace.Artist);
        });

    // --- 10: very long names --------------------------------------------------------------------------------

    [Fact]
    public void AVeryLongCardName_StillMakesAFile()
        => OnAppThread(() =>
        {
            var root = TempRoot("longname");
            try
            {
                var name = string.Concat(Enumerable.Repeat("Exceedingly Verbose ", 20));
                Assert.True(TextUtil.Slug(name).Length <= 80);
                Assert.True(TextUtil.SafeFileName(name).Length <= 100);
                var result = BatchService.ExportAll(new[] { new CardModel { Name = name } }, new TemplateService().LoadAll(), root, new SymbolService());
                Assert.Equal(1, result.Exported);
                Assert.Empty(result.Errors);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- 19–21: Undo ----------------------------------------------------------------------------------------

    [Fact]
    public void Undo_TakesBackSetSettings_NotTheCardEditBeforeThem()
        => OnAppThread(() =>
        {
            var main = NewMain();
            var card = new CardModel { Name = "A" };
            main.Cards.Add(card);
            main.SelectedCard = card;
            main.CommitHistory();
            card.Name = "Edited";
            main.CommitHistory();

            main.ApplySetSettings(new SetProfile { Artist = "Me" }, "", SetSettingsMode.NewCardsOnly);
            Assert.Equal("Me", Field<SetProfile>(main, "_setProfile").Artist);
            main.Undo();
            Assert.Equal("", Field<SetProfile>(main, "_setProfile").Artist);
            Assert.Equal("Edited", main.Cards[0].Name);
            main.Redo();
            Assert.Equal("Me", Field<SetProfile>(main, "_setProfile").Artist);
        });

    [Fact]
    public void Undo_StaysOnTheBackFace_BeingEdited()
        => OnAppThread(() =>
        {
            var main = NewMain();
            var card = new CardModel { Name = "Front", BackFace = new CardModel { Name = "Back" } };
            main.Cards.Add(card);
            main.SelectedCard = card;
            main.CommitHistory();
            main.ShowSide(back: true);
            card.BackFace!.Name = "Back edited";
            main.CommitHistory();

            main.Undo();
            Assert.True(Field<bool>(main, "_viewingBack"));
            Assert.Equal("Back", main.SelectedCard!.BackFace!.Name);
        });

    // --- 20 & 23: Replace -----------------------------------------------------------------------------------

    [Fact]
    public void Replace_PutsAValueTheSetAlreadyHas_OnMoreCards_WhenItIsSetAgain()
        => OnAppThread(() =>
        {
            var main = NewMain();
            main.ApplySetSettings(new SetProfile { Rarity = "R" }, "", SetSettingsMode.NewCardsOnly);
            var a = new CardModel { Name = "A", Rarity = "C" };
            var b = new CardModel { Name = "B", Rarity = "C" };
            main.Cards.Add(a);
            main.Cards.Add(b);
            main.SelectedCard = a;
            main.CommitHistory();

            main.ApplySetSettings(new SetProfile { Rarity = "R" }, "", SetSettingsMode.Replace, new[] { b }, touched: new[] { SetSettingsWindow.RarityField });
            Assert.Equal("R", b.Rarity);
            Assert.Equal("C", a.Rarity);
        });

    [Fact]
    public void Replace_PickingRarityNone_EmptiesItOnTheCards()
        => OnAppThread(() =>
        {
            var main = NewMain();
            main.ApplySetSettings(new SetProfile { Rarity = "R" }, "", SetSettingsMode.NewCardsOnly);
            var a = new CardModel { Name = "A", Rarity = "M" };
            main.Cards.Add(a);
            main.SelectedCard = a;
            main.CommitHistory();

            main.ApplySetSettings(new SetProfile(), "", SetSettingsMode.Replace, touched: new[] { SetSettingsWindow.RarityField });
            Assert.Equal("", a.Rarity);
        });

    [Fact]
    public void TheSetSettingsWindow_CountsAFieldSetAgain_AsTouched_OnlyThatOne()
        => OnAppThread(() =>
        {
            var win = new SetSettingsWindow(new SetProfile { Artist = "Me", Rarity = "R" }, "", Array.Empty<string>(), "Set");
            Assert.Empty(win.Touched);
            win.ArtistBox.Text = "Me";   // no change of text: nothing raised
            win.RarityBox.SelectedItem = "C";
            win.RarityBox.SelectedItem = "R";
            Assert.Equal(new[] { SetSettingsWindow.RarityField }, win.Touched);
        });

    // --- 22: the details window's X -------------------------------------------------------------------------

    [Fact]
    public void EditDetails_ClosedWithTheX_AsksWhetherToKeepTheChanges()
        => OnAppThread(() =>
        {
            var card = new CardModel { Name = "Original" };
            var asked = new List<string>();
            try
            {
                ConfirmDialog.TestAnswer = t => { asked.Add(t); return ConfirmResult.Negative; };   // Discard
                var win = new DetailsWindow(card);
                TestHelpers.ShowOffScreen(win);
                card.Name = "Changed";
                win.Close();
                Assert.Single(asked);
                Assert.Equal("Original", card.Name);

                ConfirmDialog.TestAnswer = _ => ConfirmResult.Cancel;   // Keep editing
                var stays = new DetailsWindow(card);
                bool closed = false;
                stays.Closed += (_, _) => closed = true;
                TestHelpers.ShowOffScreen(stays);
                card.Name = "Kept";
                stays.Close();
                Assert.False(closed);
                ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;   // Keep
                stays.Close();
                Assert.True(closed);
                Assert.Equal("Kept", card.Name);

                asked.Clear();
                ConfirmDialog.TestAnswer = t => { asked.Add(t); return ConfirmResult.Affirmative; };
                var unchanged = new DetailsWindow(card);
                TestHelpers.ShowOffScreen(unchanged);
                unchanged.Close();
                Assert.Empty(asked);   // nothing to lose: no question
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    // --- 24: card backs of an unsaved set --------------------------------------------------------------------

    [Fact]
    public void AnUnsavedSet_GivesItsCardBacksNoName()
        => OnAppThread(() =>
        {
            var main = NewMain();
            var name = (string)typeof(Cardinator.MainWindow).GetProperty("BackSetName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            Assert.Equal("", name);
        });

    // --- 25, 27, 28: strict arguments ------------------------------------------------------------------------

    [Theory]
    [InlineData("--renderset", "set.cardinator", "out", "only=")]            // an unset script variable
    [InlineData("--import", "deck.txt", "set.cardinator", "frame=\"\"")]
    [InlineData("--render", "card.json", "out.png", "scale=abc")]
    [InlineData("--render", "card.json", "out.png", "scale=9")]
    [InlineData("--batch", "deck.txt", "out", "art", "extra")]
    [InlineData("--frames", "out", "biultin")]
    [InlineData("--renderset", "set.cardinator", "only=X")]                   // the output folder is missing
    [InlineData("--info", "set.cardinator", "fram=Midnight")]
    [InlineData("--qa", "out", "ful")]
    public void AMistypedCommandLine_IsAUsageError(params string[] args)
        => Assert.NotNull(CliArgs.Problem(args));

    [Theory]
    [InlineData("--renderset", "set.cardinator", "out", "only=Hero", "sheet")]
    [InlineData("--batch", "deck.txt", "out", "art")]
    [InlineData("--frames", "out", "builtin", "nosym")]
    [InlineData("--search", "t:goblin", "out", "20")]
    [InlineData("--qa", "out", "full", "cards")]
    public void ARightCommandLine_Passes(params string[] args)
        => Assert.Null(CliArgs.Problem(args));

    // --- 26: themed-set scripts survive a name the console can't show ---------------------------------------

    [Fact]
    public void ThemedSetScriptsThatPrintNames_CantCrashOnTheConsoleEncoding()
    {
        var dir = Path.Combine(RepoRoot(), "examples", "themed-set");
        foreach (var script in new[] { "add_tokens.py", "card_names.py", "art_list.py" })
            Assert.Contains("sys.stdout.reconfigure(errors=\"replace\")", File.ReadAllText(Path.Combine(dir, script)));
    }

    // --- 29: frame names ignore case -------------------------------------------------------------------------

    [Fact]
    public void FrameNames_AreMatchedIgnoringCase()
    {
        var templates = new TemplateService().LoadAll();
        var real = templates[1].Name;
        Assert.Equal(real, SetCommands.InstalledName(templates, real.ToUpperInvariant()));
        Assert.Equal("Nowhere", SetCommands.InstalledName(templates, "Nowhere"));
        Assert.Same(templates[1], BatchService.FallbackFrame(templates, real.ToLowerInvariant()));
    }

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
            catch (TargetInvocationException ex) { captured = ex.InnerException ?? ex; }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
