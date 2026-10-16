using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;
using Cardinator;

namespace Cardinator.Tests;

/// <summary>
/// The ninth independent review (docs/REVIEW-2026-10-16.md), batch A — data safety (1.7.6). Export all deleting the
/// user's own date-named pictures; a command saving over the window's save; sets saved loose in Cardinator Sets or
/// inside another set's folder; Scryfall down saving a set of blank cards; two imports into one new file; a recovery
/// copy dropping a newer version's fields. Each test fails with its fix taken out. In the STA-window collection
/// because some build windows or swap <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class Review10Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    private static (int code, string output, string errors) Capture(Func<int> run)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o);
        Console.SetError(e);
        try { return (run(), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    // --- A1: Export all only removes pictures an export wrote ------------------------------------------------

    [Fact]
    public void AWholeSetExport_LeavesTheUsersOwnNumberedPicturesAlone()
        => OnAppThread(() =>
        {
            var root = TempRoot("own-pics");
            try
            {
                Directory.CreateDirectory(root);
                var templates = new TemplateService().LoadAll();
                var mine = new[] { "2024_holiday.png", "20241010_142233.png", "0001_mine.png" };
                foreach (var m in mine) File.WriteAllBytes(Path.Combine(root, m), TestHelpers.PngBytes(4, 4));

                BatchService.ExportAll(new[] { new CardModel { Name = "One" } }, templates, root, new SymbolService(), wholeSet: true);
                BatchService.ExportAll(new[] { new CardModel { Name = "One" } }, templates, root, new SymbolService(), wholeSet: true);

                foreach (var m in mine) Assert.True(File.Exists(Path.Combine(root, m)), m + " was deleted");
                Assert.True(File.Exists(Path.Combine(root, "001_one.png")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void AWholeSetExport_RemovesItsOwnEarlierBack_AndIgnoresAHandEditedPathInTheList()
        => OnAppThread(() =>
        {
            var root = TempRoot("own-back");
            var outside = TempRoot("outside");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(outside);
                var templates = new TemplateService().LoadAll();
                var dfc = new CardModel { Name = "Face", BackFace = new CardModel { Name = "Other" } };
                BatchService.ExportAll(new[] { dfc }, templates, root, new SymbolService(), wholeSet: true);
                Assert.True(File.Exists(Path.Combine(root, "001_face-back.png")));

                // A list someone edited to point outside the folder never reaches File.Delete.
                var victim = Path.Combine(outside, "001_victim.png");
                File.WriteAllBytes(victim, TestHelpers.PngBytes(4, 4));
                File.AppendAllText(Path.Combine(root, BatchService.ExportManifestName), Path.Combine("..", Path.GetFileName(outside), "001_victim.png") + Environment.NewLine + victim + Environment.NewLine);

                BatchService.ExportAll(new[] { new CardModel { Name = "Face" } }, templates, root, new SymbolService(), wholeSet: true);
                Assert.False(File.Exists(Path.Combine(root, "001_face-back.png")));   // the card isn't double-faced any more
                Assert.True(File.Exists(victim));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
        });

    // --- A2 / A5: a command and the window saving one set ----------------------------------------------------

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("""{ "object": "error", "status": 404 }""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NoneInCollection() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{ "object": "list", "not_found": [], "data": [] }""", Encoding.UTF8, "application/json") };

    private static string Names(string set) => string.Join(",", CardProject.LoadResolved(set, out _).Cards.Select(c => c.Name));

    [Fact]
    public void ACommand_DoesntSaveOverASaveMadeWhileItRan()
    {
        var root = TempRoot("cmd-race");
        var set = Path.Combine(root, "S", "S.cardinator");
        bool savedElsewhere = false;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            // While the import is busy with Scryfall, the window (or a script) saves the set.
            if (!savedElsewhere)
            {
                savedElsewhere = true;
                ProjectWriter.Write(set, new[] { new CardModel { Name = "First" }, new CardModel { Name = "Saved meanwhile" } }, "S", "", "", new SetProfile());
            }
            return Task.FromResult(req.RequestUri!.AbsolutePath.Contains("collection") ? NoneInCollection() : NotFound());
        }));
        try
        {
            ProjectWriter.Write(set, new[] { new CardModel { Name = "First" } }, "S", "", "", new SetProfile());
            var list = Path.Combine(root, "deck.txt");
            File.WriteAllText(list, "1 Lightning Bolt\n");

            var (code, _, errors) = Capture(() => SetCommands.RunImport(list, set, new[] { "noart", "append" }));

            Assert.True(savedElsewhere);
            Assert.Equal(1, code);
            Assert.Contains("changed by something else", errors);
            Assert.Equal("First,Saved meanwhile", Names(set));
        }
        finally { ScryfallClient.TestHttp = null; try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TwoImportsIntoOneNewFile_TheSecondDoesntOverwriteTheFirst()
    {
        var root = TempRoot("import-race");
        var set = Path.Combine(root, "S.cardinator");
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
            Task.FromResult(req.RequestUri!.AbsolutePath.Contains("collection") ? NoneInCollection() : NotFound())));
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var err = new StringWriter();
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.txt");
            File.WriteAllText(list, "1 Lightning Bolt\n");
            Console.SetOut(new StringWriter());
            Console.SetError(TextWriter.Synchronized(err));

            // The first import holds the set; the second has checked the file isn't there and waits its turn.
            var first = SetLock.Take(set)!;
            var second = Task.Run(() => SetCommands.RunImport(list, set, new[] { "noart" }));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!err.ToString().Contains("waiting") && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Assert.Contains("waiting", err.ToString());
            ProjectWriter.Write(set, new[] { new CardModel { Name = "From the first import" } }, "S", "", "", new SetProfile());
            first.Dispose();

            Assert.Equal(2, second.GetAwaiter().GetResult());
            Assert.Equal("From the first import", Names(set));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            ScryfallClient.TestHttp = null;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void TheWindowsSave_WaitsForACommandThatHasTheSet_AndOtherwiseSavesNothing()
        => OnAppThread(() =>
        {
            var root = TempRoot("win-lock");
            var set = Path.Combine(root, "S", "S.cardinator");
            var oldWait = Cardinator.MainWindow.SaveLockWait;
            try
            {
                ProjectWriter.Write(set, new[] { new CardModel { Name = "Before" } }, "S", "", "", new SetProfile());
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", set);
                main.Cards[0].Name = "Edited in the window";
                Cardinator.MainWindow.SaveLockWait = TimeSpan.FromMilliseconds(200);

                using (SetLock.Take(set)!)   // a --restyle is working on the set
                {
                    Assert.False((bool)Invoke(main, "SaveProject", false)!);
                    Assert.Contains("command", main.Status);
                    Assert.Equal("Before", Names(set));
                }

                Assert.True((bool)Invoke(main, "SaveProject", false)!);   // once it's done, Save works
                Assert.Equal("Edited in the window", Names(set));
                main.Close();
            }
            finally
            {
                Cardinator.MainWindow.SaveLockWait = oldWait;
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void SaveIntoAFolderNotMadeYet_IsntMistakenForABusySet()
        => OnAppThread(() =>
        {
            var root = TempRoot("win-newdir");
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                main.Cards.Clear();
                main.Cards.Add(new CardModel { Name = "New" });
                var set = Path.Combine(root, "Deep", "New", "New.cardinator");
                typeof(Cardinator.MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, set);
                Assert.True((bool)Invoke(main, "SaveProject", false)!, main.Status);
                Assert.True(File.Exists(set));
                main.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- A4: Scryfall down for maintenance ------------------------------------------------------------------------

    [Fact]
    public void Import_WhileScryfallAnswers503_SavesNothing_AndExits1()
    {
        var root = TempRoot("scryfall-503");
        int calls = 0;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down for maintenance") });
        }));
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.txt");
            File.WriteAllText(list, "1 Lightning Bolt\n1 Llanowar Elves\n1 Counterspell\n");
            var set = Path.Combine(root, "Set.cardinator");

            var (code, _, errors) = Capture(() => SetCommands.RunImport(list, set, new[] { "noart" }));

            Assert.Equal(1, code);
            Assert.Contains("nothing saved", errors);
            Assert.False(File.Exists(set));
            Assert.True(calls <= 6, $"{calls} requests: the cards after the first were each tried again");   // the batch + one lookup, 3 tries each
        }
        finally { ScryfallClient.TestHttp = null; try { Directory.Delete(root, true); } catch { } }
    }

    // --- A6: a recovered set keeps what a newer Cardinator wrote ---------------------------------------------------

    [Fact]
    public void ARecoveredSetFromANewerVersion_StillWarns_AndKeepsItsFieldsAndVersion()
        => OnAppThread(() =>
        {
            var root = TempRoot("recovered-newer");
            string? crashed = null;
            var asked = new List<string>();
            ConfirmDialog.TestAnswer = title => { asked.Add(title); return title.Contains("newer") ? ConfirmResult.Affirmative : ConfirmResult.Cancel; };
            try
            {
                Directory.CreateDirectory(root);
                var set = Path.Combine(root, "Set.cardinator");
                File.WriteAllText(set, """{ "name": "Set", "formatVersion": 9, "futureSet": 7, "cards": [ { "name": "A" } ] }""");

                // A session edits it and writes its recovery copy, then "crashes".
                var first = new Cardinator.MainWindow();
                Assert.True((bool)Invoke(first, "LoadProjectFile", set)!);
                first.SelectedCard!.Name = "Edited";
                first.CommitHistory();
                first.WriteRecovery();
                crashed = Path.Combine(RecoveryStore.Dir, $"T-{Guid.NewGuid():N}.cardinator");
                File.Copy(RecoveryStore.FileFor(set), crashed);
                File.WriteAllText(crashed + ".meta", JsonSerializer.Serialize(new RecoveryStore.Meta(set, "Set", DateTime.Now, int.MaxValue - 7)));
                first.SuppressClosePrompt = true;
                first.Close();

                asked.Clear();
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Assert.True(main.OpenRecovered(Assert.Single(RecoveryStore.Pending(), e => e.File == crashed)));
                Assert.Contains(asked, t => t.Contains("newer"));   // still warned before saving over it
                Assert.True((bool)Invoke(main, "SaveProject", false)!, main.Status);

                var saved = CardProject.Load(set);
                Assert.Equal("Edited", saved.Cards.Single().Name);
                Assert.Equal(9, saved.FormatVersion);   // an older Cardinator opening it still warns
                Assert.True(saved.Unknown?.ContainsKey("futureSet"), "the newer version's field was dropped");
                main.Close();
            }
            finally
            {
                ConfirmDialog.TestAnswer = null;
                if (crashed != null) RecoveryStore.Delete(crashed);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void SavingAnOlderSet_StampsTheCurrentVersion_WhichKnowsAboutCardBacksAndHalfFrames()
    {
        var root = TempRoot("stamp");
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Set.cardinator");
            File.WriteAllText(set, """{ "name": "Set", "formatVersion": 1, "cards": [ { "name": "A" } ] }""");
            var p = CardProject.LoadResolved(set, out _);
            ProjectWriter.Write(set, p.Cards, p.Name, "", "", p.Profile, unknown: p.Unknown, formatVersion: p.FormatVersion);
            Assert.True(CardProject.CurrentFormatVersion >= 2);
            Assert.Equal(CardProject.CurrentFormatVersion, CardProject.Load(set).FormatVersion);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- A7: smaller safety items ---------------------------------------------------------------------------------

    [Fact]
    public void ARecoveredSetWhoseFolderMovedSinceTheCrash_AsksBeforeSaving()
        => OnAppThread(() =>
        {
            var root = TempRoot("recovered-moved");
            string? crashed = null;
            try
            {
                var set = Path.Combine(root, "Old", "S.cardinator");
                ProjectWriter.Write(set, new[] { new CardModel { Name = "A" } }, "S", "", "", new SetProfile());
                crashed = Path.Combine(RecoveryStore.Dir, $"M-{Guid.NewGuid():N}.cardinator");
                RecoveryStore.Write(crashed, set, "S", new[] { new CardModel { Name = "Recovered" } }, "", "", new SetProfile());
                File.WriteAllText(crashed + ".meta", JsonSerializer.Serialize(new RecoveryStore.Meta(set, "S", DateTime.Now, int.MaxValue - 7)));
                Directory.Move(Path.Combine(root, "Old"), Path.Combine(root, "New"));   // moved while the app was down

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Assert.True(main.OpenRecovered(Assert.Single(RecoveryStore.Pending(), e => e.File == crashed)));
                var conflict = main.SaveConflict();
                Assert.NotNull(conflict);
                Assert.True(conflict!.Value.Moved);
                main.Close();
            }
            finally
            {
                if (crashed != null) RecoveryStore.Delete(crashed);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void AnAtomicWrite_WaitsOutAFileHeldOpenForAMoment()
    {
        var root = TempRoot("held");
        try
        {
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "Set.cardinator");
            File.WriteAllText(file, "old");
            var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);   // a sync client reading it
            var release = Task.Run(async () => { await Task.Delay(150); held.Dispose(); });

            IoUtil.AtomicWriteText(file, "new");

            release.Wait();
            Assert.Equal("new", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheSetLock_WaitsOutALockFileStillBeingDeleted()
    {
        var root = TempRoot("lock-pending");
        try
        {
            Directory.CreateDirectory(root);
            var set = Path.Combine(root, "Set.cardinator");
            var lockPath = set + ".lock";
            // The last holder's lock file, closed (so deleted) while a scanner still has it open: "delete pending".
            var holder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            var scanner = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            holder.Dispose();
            var opened = Record.Exception(() => new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None).Dispose());
            Assert.IsType<UnauthorizedAccessException>(opened);   // what Windows says about a delete-pending file
            var release = Task.Run(async () => { await Task.Delay(200); scanner.Dispose(); });

            using var taken = SetLock.Take(set, wait: TimeSpan.FromSeconds(5));

            release.Wait();
            Assert.NotNull(taken);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- A3: every set in a folder of its own -----------------------------------------------------------------

    [Fact]
    public void TheFirstSetSavedInTheSetsLibrary_GetsAFolderOfItsOwn()
    {
        var root = TempRoot("library");
        try
        {
            var library = Path.Combine(root, "Cardinator Sets");
            Directory.CreateDirectory(library);   // empty, as on a fresh install
            Assert.Equal(Path.Combine(library, "First", "First.cardinator"),
                SetFolder.OwnFolderPath(Path.Combine(library, "First.cardinator"), library));

            // An empty folder the user made for the set is still used as it is.
            var mine = Path.Combine(root, "my new folder");
            Directory.CreateDirectory(mine);
            Assert.Equal(Path.Combine(mine, "First.cardinator"), SetFolder.OwnFolderPath(Path.Combine(mine, "First.cardinator"), library));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SaveAsInsideAnotherSetsFolder_GoesBesideThatSet_NotInsideIt()
    {
        var root = TempRoot("beside");
        try
        {
            var sets = Path.Combine(root, "Sets");
            var mySet = Path.Combine(sets, "MySet");
            ProjectWriter.Write(Path.Combine(mySet, "MySet.cardinator"), new[] { new CardModel { Name = "A" } }, "MySet", "", "", new SetProfile());

            Assert.Equal(Path.Combine(sets, "MySet v2", "MySet v2.cardinator"),
                SetFolder.OwnFolderPath(Path.Combine(mySet, "MySet v2.cardinator"), null));

            // A set kept loose in a shared folder isn't "a set's folder": a new one beside it still nests right there.
            var loose = Path.Combine(root, "Desktop");
            ProjectWriter.Write(Path.Combine(loose, "Old.cardinator"), new[] { new CardModel { Name = "A" } }, "Old", "", "", new SetProfile());
            Assert.Equal(Path.Combine(loose, "New", "New.cardinator"), SetFolder.OwnFolderPath(Path.Combine(loose, "New.cardinator"), null));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SaveAs_StartsBesideTheSetsOwnFolder()
        => OnAppThread(() =>
        {
            var root = TempRoot("dialog-folder");
            try
            {
                var set = Path.Combine(root, "Sets", "MySet", "MySet.cardinator");
                ProjectWriter.Write(set, new[] { new CardModel { Name = "A" } }, "MySet", "", "", new SetProfile());
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", set);
                Assert.Equal(Path.Combine(root, "Sets"), (string)Invoke(main, "SaveDialogFolder")!);
                main.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ASharedSet_UnpacksIntoTheSetsFolderGiven_NotTheAppsDataFolder()
        => OnAppThread(() =>
        {
            var root = TempRoot("shared-into");
            try
            {
                var src = Path.Combine(root, "src", "Gift", "Gift.cardinator");
                ProjectWriter.Write(src, new[] { new CardModel { Name = "A" } }, "Gift", "", "", new SetProfile());
                var zip = Path.Combine(root, "Gift.zip");
                SetPackager.ExportSetWithFrames(src, Array.Empty<string>(), zip);
                var library = Path.Combine(root, "Cardinator Sets");
                Directory.CreateDirectory(library);

                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                Assert.True(main.OpenSharedSet(zip, library), main.Status);
                var opened = (string)typeof(Cardinator.MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                Assert.StartsWith(library + Path.DirectorySeparatorChar, opened, StringComparison.OrdinalIgnoreCase);
                main.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    internal static void OnAppThread(Action action)
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

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }
}
