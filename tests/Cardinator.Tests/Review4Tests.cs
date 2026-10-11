using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The fourth independent review (1.6.28): set commands that could lose a card's real name or write over a backup,
/// shared sets that dropped a frame's layouts or copied it again, lookups that swapped a listed printing, and
/// the window's unsaved marker, recovery copies and Frame Design's Esc. Each test fails with its fix taken out. In the
/// STA-window collection because some build windows or swap <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class Review4Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    // --- retitling ------------------------------------------------------------------------------------

    [Fact]
    public void Retitling_AgainByTheTitleShown_KeepsTheRealName()
    {
        var card = new CardModel { Name = "Hero" };
        SetCommands.Retitle(new List<CardModel> { card }, new[] { "Hero\tThe Collector" });
        SetCommands.Retitle(new List<CardModel> { card }, new[] { "The Collector\tThe Archivist" });
        Assert.Equal(("The Archivist", "Hero"), (card.Name, card.Subtitle));
    }

    [Fact]
    public void Retitling_TwoCardsWhoseTitlesAreEachOthersNames_IsTheSameEveryRun()
    {
        var hero = new CardModel { Name = "Hero", RulesText = "Hero attacks." };
        var villain = new CardModel { Name = "Villain" };
        var cards = new List<CardModel> { hero, villain };
        var lines = new[] { "Hero\tVillain", "Villain\tHero" };
        SetCommands.Retitle(cards, lines);
        SetCommands.Retitle(cards, lines);
        Assert.Equal(("Villain", "Hero", "Villain attacks."), (hero.Name, hero.Subtitle, hero.RulesText));
        Assert.Equal(("Hero", "Villain"), (villain.Name, villain.Subtitle));
    }

    [Fact]
    public void RenamingInText_LeavesAnotherCardsNameThatStartsWithTheShortName()
    {
        var text = "When Urza, Lord Protector enters, Urza draws. Then meld them into Urza, Planeswalker.";
        var got = SetCommands.RenameInText(text, "Adam, the First Man", "Urza, Lord Protector");
        Assert.Equal("When Adam, the First Man enters, Adam draws. Then meld them into Urza, Planeswalker.", got);
    }

    // --- set commands that must not write -------------------------------------------------------------

    private static string SavedSet(string dir, string name = "S")
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".cardinator");
        ProjectWriter.Write(file, new List<CardModel> { new() { Name = "Hero" } }, name, "", "", new SetProfile());
        return file;
    }

    [Fact]
    public void Restyle_RefusesASetANewerVersionSaved_AndLeavesItAsItWas()
    {
        var root = TempRoot("newer");
        try
        {
            var set = SavedSet(root);
            var json = File.ReadAllText(set).Replace("\"formatVersion\": 1", "\"formatVersion\": 9, \"futureThing\": 1");
            Assert.Contains("futureThing", json);
            File.WriteAllText(set, json);
            Assert.Equal(2, SetCommands.RunRestyle(set, new[] { "setcode=JA" }));
            Assert.Equal(json, File.ReadAllText(set));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Restyle_AndImportAppend_RefuseAFileInABackupsFolder()
    {
        var root = TempRoot("cliback");
        try
        {
            SavedSet(root, "E");
            var backup = Path.Combine(root, "backups", "E.20261001-120000.cardinator");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(Path.Combine(root, "E.cardinator"), backup);
            var before = File.ReadAllText(backup);
            Assert.Equal(2, SetCommands.RunRestyle(backup, new[] { "setcode=BB" }));
            var list = Path.Combine(root, "list.txt");
            File.WriteAllText(list, "1 Sol Ring");
            Assert.Equal(2, SetCommands.RunImport(list, backup, new[] { "append", "noart" }));
            Assert.Equal(before, File.ReadAllText(backup));
            Assert.False(Directory.Exists(Path.Combine(root, "backups", "backups")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Moxfield_NeverOverwritesASet()
    {
        var root = TempRoot("moxset");
        const string deck = """{ "name": "D", "boards": { "mainboard": { "cards": { "a": { "quantity": 1, "card": { "name": "Sol Ring" } } } } } }""";
        SetCommands.TestFetch = _ => Task.FromResult(deck);
        try
        {
            var set = SavedSet(root);
            var before = File.ReadAllText(set);
            Assert.Equal(2, SetCommands.RunMoxfield("YFifATPTSHG3xPz_COErPA", set));
            Assert.Equal(before, File.ReadAllText(set));

            var asJson = Path.Combine(root, "Saved as json.json");
            File.Copy(set, asJson);
            Assert.Equal(2, SetCommands.RunMoxfield("YFifATPTSHG3xPz_COErPA", asJson));

            // A deck file fetched before is just refreshed.
            var list = Path.Combine(root, "decklist.txt");
            File.WriteAllText(list, "old");
            Assert.Equal(0, SetCommands.RunMoxfield("YFifATPTSHG3xPz_COErPA", list));
            Assert.Contains("Sol Ring", File.ReadAllText(list));
        }
        finally
        {
            SetCommands.TestFetch = null;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // --- a set kept in a folder called "Backups" --------------------------------------------------------

    [Fact]
    public void ASetInAFolderNamedBackups_WithAnotherSetAboveIt_IsStillAnOrdinarySet()
    {
        var root = TempRoot("docsbackups");
        try
        {
            SavedSet(root, "Other");
            var dir = Path.Combine(root, "Backups");
            Directory.CreateDirectory(dir);
            var set = Path.Combine(dir, "MySet.cardinator");
            Assert.Null(ProjectBackup.ProjectFileForBackup(set));   // no backups of ours beside it

            Directory.CreateDirectory(Path.Combine(dir, "art"));
            File.WriteAllText(Path.Combine(dir, "Other.20261001-120000.cardinator"), "{}");
            Assert.Null(ProjectBackup.ProjectFileForBackup(set));   // its own art folder: a set folder
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(ProjectBackup.ArtRootFor(set)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- the window -----------------------------------------------------------------------------------

    [Fact]
    public void ChoosingAnotherCard_IsNotAnUnsavedChange()
        => OnAppThread(() =>
        {
            var root = TempRoot("selonly");
            try
            {
                var setFile = Path.Combine(root, "S", "S.cardinator");
                Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
                ProjectWriter.Write(setFile, new List<CardModel> { new() { Name = "A" }, new() { Name = "B" } }, "S", "", "", new SetProfile());
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", setFile);
                main.SelectedCard = main.Cards[1];
                main.SelectedCard.CopyFrom(main.SelectedCard.Clone());   // what Edit details… → Cancel does
                main.CommitHistory();
                Assert.False(main.Dirty);
                Assert.False(main.CanUndo, "choosing a card isn't an undo step");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ARecoveryCopyOlderThanTheSet_IsKnownToBeOlder()
    {
        var root = TempRoot("oldcopy");
        try
        {
            var set = SavedSet(root);
            var made = File.GetLastWriteTime(set);
            var older = new RecoveryStore.Entry(set + ".rec", new RecoveryStore.Meta(set, "S", made.AddMinutes(-10), 1));
            Assert.NotNull(Cardinator.MainWindow.SetSavedSince(older));
            var newer = new RecoveryStore.Entry(set + ".rec", new RecoveryStore.Meta(set, "S", made.AddMinutes(10), 1));
            Assert.Null(Cardinator.MainWindow.SetSavedSince(newer));
            var unsaved = new RecoveryStore.Entry(set + ".rec", new RecoveryStore.Meta("", "S", made.AddMinutes(-10), 1));
            Assert.Null(Cardinator.MainWindow.SetSavedSince(unsaved));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void FrameDesign_AsksBeforeClosingThrowsAwayChanges()
        => OnAppThread(() =>
        {
            var tpl = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame);
            var asked = 0;
            ConfirmDialog.TestAnswer = title => { asked++; return ConfirmResult.Negative; };   // "Keep editing"
            try
            {
                var win = new FrameDesignWindow(tpl) { Standalone = true };
                var closed = false;
                win.Closed += (_, _) => closed = true;
                win.Show();
                Assert.False(win.HasUnappliedChanges);
                win.BorderBox.Text = "17";   // an edit, not applied
                Assert.True(win.HasUnappliedChanges);
                win.Close();
                Assert.Equal(1, asked);
                Assert.False(closed, "Keep editing leaves the window open");

                ConfirmDialog.TestAnswer = _ => { asked++; return ConfirmResult.Affirmative; };   // "Discard"
                win.Close();
                Assert.True(closed);
                Assert.Equal(2, asked);
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    // --- shared sets ----------------------------------------------------------------------------------

    /// <summary>A shared-set zip by hand: the project file (under <paramref name="projectName"/>), one picture, and the
    /// frame in frames/&lt;slug&gt;/ with whichever layouts are given.</summary>
    private static string SharedZip(string root, string projectName, string frameName, uint colour, params string[] layouts)
    {
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"share-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var project = new CardProject
        {
            Name = "Shared", DefaultTemplate = frameName,
            Cards = { new CardModel { Name = "Hero", TemplateName = frameName, ArtPath = "art/hero.png" } },
        };
        void Add(string entry, byte[] bytes) { using var s = zip.CreateEntry(entry).Open(); s.Write(bytes); }
        var tmp = Path.Combine(root, $"p-{Guid.NewGuid():N}.cardinator");
        project.Save(tmp);
        Add(projectName, File.ReadAllBytes(tmp));
        File.Delete(tmp);
        Add("art/hero.png", TestHelpers.PngBytes(40, 30));
        var slug = TextUtil.Slug(frameName);
        var spec = Encoding.UTF8.GetBytes(SpecJson(new TemplateSpec { Name = frameName, CustomFrame = true }));
        Add($"frames/{slug}/template.json", spec);
        Add($"frames/{slug}/frame.png", TestHelpers.PngBytes(75, 105, colour));
        foreach (var v in layouts)
        {
            Add($"frames/{slug}/{v}/template.json", spec);
            Add($"frames/{slug}/{v}/frame.png", TestHelpers.PngBytes(75, 105, colour ^ 0x00FFFFFF));
        }
        return zipPath;
    }

    private static void RemoveInstalled(string frameName)
    {
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.TemplatesDir))
        {
            var spec = Path.Combine(dir, "template.json");
            if (!File.Exists(spec)) continue;
            try
            {
                var name = TemplateSpec.LoadFromJson(File.ReadAllText(spec)).Name ?? "";
                if (name == frameName || name.StartsWith(frameName + " (", StringComparison.Ordinal)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    private static int Installed(string frameName) => new TemplateService().LoadAll()
        .Count(t => t.Name == frameName || t.Name.StartsWith(frameName + " (", StringComparison.Ordinal));

    [Fact]
    public void ASharedSetsFrameLayouts_AreNotDroppedForAnInstalledFrameWithout_Them()
    {
        var root = TempRoot("sharelayouts");
        var frame = "Layout Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            SetPackager.UnpackSharedSet(SharedZip(root, "Shared.cardinator", frame, 0xFF336699), parent);   // installed, no flip
            var project = SetPackager.UnpackSharedSet(SharedZip(root, "Shared.cardinator", frame, 0xFF336699, "flip"), parent);
            var used = CardProject.Load(project).Cards.Single().TemplateName;
            var t = new TemplateService().LoadAll().Single(x => x.Name == used);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(t.FramePath)!, "flip", "frame.png")), "the set's flip layout was dropped");
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASharedSetWithoutLayouts_DoesntTakeOnAnInstalledFramesOwnLayouts()
    {
        var root = TempRoot("sharenolayouts");
        var frame = "Plain Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            SetPackager.UnpackSharedSet(SharedZip(root, "Shared.cardinator", frame, 0xFF663399, "flip"), parent);   // installed, with a flip
            var project = SetPackager.UnpackSharedSet(SharedZip(root, "Shared.cardinator", frame, 0xFF663399), parent);
            var used = CardProject.Load(project).Cards.Single().TemplateName;
            var t = new TemplateService().LoadAll().Single(x => x.Name == used);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(t.FramePath)!, "flip", "frame.png")), "the set took on a flip it doesn't have");
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASharedSetOpenedAgain_ReusesTheFrameItRenamed_InsteadOfAnotherCopy()
    {
        var root = TempRoot("sharerenamed");
        var frame = "Renamed Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            SetPackager.UnpackSharedSet(SharedZip(root, "Mine.cardinator", frame, 0xFF101010), parent);   // the recipient's own "X"
            var zip = SharedZip(root, "Shared.cardinator", frame, 0xFF20C040);                              // a different "X"
            var first = SetPackager.UnpackSharedSet(zip, parent);
            var second = SetPackager.UnpackSharedSet(zip, parent);
            Assert.Equal(frame + " (2)", CardProject.Load(first).Cards.Single().TemplateName);
            Assert.Equal(frame + " (2)", CardProject.Load(second).Cards.Single().TemplateName);
            Assert.Equal(2, Installed(frame));
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASetSavedAsJson_OpensAsASharedSet()
    {
        var root = TempRoot("sharejson");
        var frame = "Json Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var zip = SharedZip(root, "My Set.json", frame, 0xFF884422);
            Assert.True(SetPackager.IsSharedSet(zip));
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            var project = SetPackager.UnpackSharedSet(zip, parent);
            Assert.Equal(Path.Combine(parent, "My Set", "My Set.json"), project);
            Assert.Equal("Hero", CardProject.Load(project).Cards.Single().Name);
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AFrameBundle_IsNotTakenForASharedSet()
    {
        var root = TempRoot("bundlejson");
        try
        {
            Directory.CreateDirectory(root);
            var zipPath = Path.Combine(root, "frame.cardframe");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var s = zip.CreateEntry("template.json").Open()) s.Write(Encoding.UTF8.GetBytes(SpecJson(new TemplateSpec { Name = "F" })));
                using (var s = zip.CreateEntry("frame.png").Open()) s.Write(TestHelpers.PngBytes(75, 105));
            }
            Assert.False(SetPackager.IsSharedSet(zipPath));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASharedSetThatCantBeUnpacked_LeavesNoHalfUnpackedFolder()
    {
        var root = TempRoot("sharebroken");
        try
        {
            Directory.CreateDirectory(root);
            var zipPath = Path.Combine(root, "broken.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var s = zip.CreateEntry("Broken.cardinator").Open()) s.Write(Encoding.UTF8.GetBytes("""{ "cards": [] }"""));
                using (var s = zip.CreateEntry("art/a|b.png").Open()) s.Write(TestHelpers.PngBytes(4, 4));   // not a Windows file name
            }
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            Assert.ThrowsAny<Exception>(() => SetPackager.UnpackSharedSet(zipPath, parent));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASharedSetWithTheSameFileTwice_InDifferentCase_Unpacks()
    {
        var root = TempRoot("sharecase");
        try
        {
            Directory.CreateDirectory(root);
            var zipPath = Path.Combine(root, "case.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var s = zip.CreateEntry("Case.cardinator").Open()) s.Write(Encoding.UTF8.GetBytes("""{ "cards": [] }"""));
                using (var s = zip.CreateEntry("art/a.png").Open()) s.Write(TestHelpers.PngBytes(4, 4));
                using (var s = zip.CreateEntry("art/A.png").Open()) s.Write(TestHelpers.PngBytes(6, 6));
            }
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            var project = SetPackager.UnpackSharedSet(zipPath, parent);
            Assert.True(File.Exists(project));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- lookups --------------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string MarvelBolt = """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "msc", "collector_number": "806", "rarity": "uncommon", "set_type": "commander", "promo_types": ["universesbeyond"] }""";

    [Fact]
    public async Task ALineThatNamedAPrinting_KeepsScryfallsPickInTheFallbackLookup()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            lock (asked) asked.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri!.AbsolutePath.EndsWith("/collection"))
                return Task.FromResult(Json("""{ "object": "list", "data": [], "not_found": [ {} ] }"""));
            return Task.FromResult(Json(MarvelBolt));   // the fuzzy lookup by name
        }));
        try
        {
            var listed = ImportService.Parse("Lightning Bolt (MSC) 9999", "", "Default");   // a typo'd number
            await BatchService.FillFromScryfallAsync(listed, downloadArt: false);
            Assert.Equal("MSC", listed.Single().Card.SetCode);
            Assert.DoesNotContain(asked, a => a.EndsWith("/cards/search"));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ACardWithNoRegularPrinting_IsSearchedForOnce()
    {
        var searches = 0;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            Interlocked.Increment(ref searches);
            Assert.Contains("-st:memorabilia", Uri.UnescapeDataString(req.RequestUri!.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("""{ "object": "error", "status": 404 }""", Encoding.UTF8, "application/json") });
        }));
        try
        {
            var client = new ScryfallClient();
            var faces = new List<CardModel> { new() { Name = "Lightning Bolt", SetCode = "MSC", SpecialPrinting = true } };
            await client.PreferRegularPrintingAsync(faces);
            await client.PreferRegularPrintingAsync(faces);
            Assert.Equal(1, searches);
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    // --- sample frames --------------------------------------------------------------------------------

    [Fact]
    public void AnUntouchedSampleFrameFromBeforeTheManifest_IsUpdated()
    {
        SampleTemplates.ResetForTests();
        SampleTemplates.EnsureExtracted();
        var file = Path.Combine(AppPaths.TemplatesDir, "pcc_sealed_gate", "template.json");
        var manifestPath = Path.Combine(AppPaths.TemplatesDir, SampleTemplates.ManifestName);
        var manifestBefore = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null;
        var before = File.ReadAllBytes(file);
        try
        {
            // As 1.6.15 left it: the file 1.6.0 shipped, and no record that the app wrote it.
            var old = RepoFile("da8d0c3", "src/Cardinator/Assets/templates/pcc-sealed-gate/template.json");
            File.WriteAllBytes(file, old);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.NotEqual(old, File.ReadAllBytes(file));
        }
        finally
        {
            File.WriteAllBytes(file, before);
            if (manifestBefore != null) File.WriteAllText(manifestPath, manifestBefore);
            SampleTemplates.ResetForTests();
        }
    }

    /// <summary>A file as an earlier commit had it, with the line endings this checkout uses.</summary>
    private static byte[] RepoFile(string commit, string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"show {commit}:{path}")
        { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        text = text.Replace("\r\n", "\n");
        var crlf = File.ReadAllText(Path.Combine(AppPaths.TemplatesDir, "pcc_sealed_gate", "template.json")).Contains("\r\n");
        return Encoding.UTF8.GetBytes(crlf ? text.Replace("\n", "\r\n") : text);
    }

    // --- helpers --------------------------------------------------------------------------------------

    private static string SpecJson(TemplateSpec spec)
    {
        var f = Path.Combine(Path.GetTempPath(), $"cardinator-spec-{Guid.NewGuid():N}.json");
        spec.Save(f);
        try { return File.ReadAllText(f); } finally { File.Delete(f); }
    }

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

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
