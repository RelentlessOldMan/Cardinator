using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The third review (1.6.20), of what changed in 1.6.12–1.6.19: a crash's recovery copy that a later session
/// overwrote, exports that fell back to a different frame than the preview, two frames with one name, a frame restore
/// that redrew a frame on every load, a deck site's set column, and bundles nested without end. Each test fails with
/// its fix taken out. In the STA-window collection because some build a <see cref="Cardinator.MainWindow"/>.
/// </summary>
[Collection("STAWindows")]
public class Review3Tests
{
    // --- recovery copies -------------------------------------------------------------------------------

    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    private static string NewSet(string root)
    {
        var setFile = Path.Combine(root, "S", "S.cardinator");
        Directory.CreateDirectory(Path.GetDirectoryName(setFile)!);
        ProjectWriter.Write(setFile, new List<CardModel> { new() { Name = "On disk" } }, "S", "", "", new SetProfile());
        return setFile;
    }

    /// <summary>A copy left by a session that crashed, named as 1.6.19 and earlier named it: by set alone.</summary>
    private static string CrashedCopy(string setFile, string cardName)
    {
        var full = Path.GetFullPath(setFile).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..10];
        var copy = Path.Combine(RecoveryStore.Dir, $"S-{hash}.cardinator");
        RecoveryStore.Write(copy, setFile, "S", new[] { new CardModel { Name = cardName } }, "", "", new SetProfile());
        File.WriteAllText(copy + ".meta", JsonSerializer.Serialize(new RecoveryStore.Meta(setFile, "S", DateTime.Now, int.MaxValue - 7)));
        return copy;
    }

    [Fact]
    public void ALaterSessionEditingTheSameSet_NeverOverwritesACrashsRecoveryCopy()
        => OnAppThread(() =>
        {
            var root = TempRoot("crashcopy");
            string? crashed = null;
            try
            {
                var setFile = NewSet(root);
                crashed = CrashedCopy(setFile, "Work from before the crash");

                // "Later" at startup, then the same set opened and edited: the minute timer writes a copy.
                var main = new Cardinator.MainWindow();
                Invoke(main, "LoadProjectFile", setFile);
                main.SelectedCard!.Name = "Todays edit";
                main.CommitHistory();
                main.WriteRecovery();

                Assert.Contains("Work from before the crash", File.ReadAllText(crashed));
                Assert.Contains(RecoveryStore.Pending(), e => e.File == crashed);   // still offered next time
                Assert.Contains("Todays edit", File.ReadAllText(RecoveryStore.FileFor(setFile)));

                Invoke(main, "SaveProject", false);   // saving this session's work leaves the crash's copy alone
                Assert.True(File.Exists(crashed));
                main.SuppressClosePrompt = true;
            }
            finally
            {
                if (crashed != null) RecoveryStore.Delete(crashed);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void ACopyFromAnEndedSession_IsOffered_EvenIfItsProcessIdIsOursNow()
    {
        var copy = Path.Combine(RecoveryStore.Dir, $"reused-pid-{Guid.NewGuid():N}.cardinator");
        try
        {
            RecoveryStore.Write(copy, "", "Untitled", new[] { new CardModel { Name = "Before the restart" } }, "", "", new SetProfile());
            // Written by another session that Windows happened to give our process id after a restart.
            File.WriteAllText(copy + ".meta", JsonSerializer.Serialize(
                new RecoveryStore.Meta("", "Untitled", DateTime.Now, Environment.ProcessId, "anothersession")));
            Assert.Contains(RecoveryStore.Pending(), e => e.File == copy);
        }
        finally { RecoveryStore.Delete(copy); }
    }

    private static string? DeclinedCopyOf(string copy)
        => Directory.GetFiles(Path.Combine(RecoveryStore.Dir, "declined"), Path.GetFileNameWithoutExtension(copy) + ".*").FirstOrDefault();

    [Fact]
    public void DontSave_OnARecoveredCopy_KeepsItAside()
        => OnAppThread(() =>
        {
            var root = TempRoot("recovereddiscard");
            string? crashed = null;
            try
            {
                var setFile = NewSet(root);
                crashed = CrashedCopy(setFile, "Recovered work");
                var main = new Cardinator.MainWindow();
                Assert.True(main.OpenRecovered(Assert.Single(RecoveryStore.Pending(), e => e.File == crashed)));

                Invoke(main, "DiscardChanges");   // "Don't save" on closing or opening another set
                Assert.False(File.Exists(crashed));
                var kept = DeclinedCopyOf(crashed);
                Assert.NotNull(kept);
                Assert.Contains("Recovered work", File.ReadAllText(kept!));
                File.Delete(kept!);
                main.SuppressClosePrompt = true;
            }
            finally
            {
                if (crashed != null) RecoveryStore.Delete(crashed);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void ARecoveredCopy_ThisSessionWritesOver_IsKeptAside()
        => OnAppThread(() =>
        {
            var root = TempRoot("recoveredwrite");
            string? crashed = null;
            try
            {
                var setFile = NewSet(root);
                crashed = CrashedCopy(setFile, "Recovered work");
                var main = new Cardinator.MainWindow();
                Assert.True(main.OpenRecovered(Assert.Single(RecoveryStore.Pending(), e => e.File == crashed)));
                main.SelectedCard!.Name = "More work";
                main.CommitHistory();
                main.WriteRecovery();   // this session's own copy now carries it

                Assert.Contains("More work", File.ReadAllText(RecoveryStore.FileFor(setFile)));
                var kept = DeclinedCopyOf(crashed);
                Assert.NotNull(kept);
                File.Delete(kept!);
                Invoke(main, "DiscardChanges");
                main.SuppressClosePrompt = true;
            }
            finally
            {
                if (crashed != null) RecoveryStore.Delete(crashed);
                try { Directory.Delete(root, true); } catch { }
            }
        });

    // --- a card whose frame isn't installed: every export uses the set's house frame, as the preview does ---

    private static readonly PageSpec OneUp = new() { Width = 750, Height = 1050, Cols = 1, Rows = 1, CutMarks = false };

    private static (IReadOnlyList<Template> templates, string house) HouseFrame()
    {
        var templates = new TemplateService().LoadAll();
        return (templates, templates.First(t => t.Name != templates[0].Name).Name);   // anything but the first
    }

    private static CardModel OnFrame(string frame) => new() { Name = "Wanderer", TypeLine = "Creature — Elf", Power = "1", Toughness = "1", TemplateName = frame };

    [Fact]
    public void OnASheet_ACardWhoseFrameIsntInstalled_WearsTheHouseFrame() => TestHelpers.RunSta(() =>
    {
        var (templates, house) = HouseFrame();
        var symbols = new SymbolService();
        byte[] Sheet(string frame) => TestHelpers.Pixels(
            SheetExporter.Compose(new[] { OnFrame(frame) }, templates, symbols, OneUp, houseFrame: house).Single());

        var expected = Sheet(house);
        Assert.NotEqual(expected, Sheet(templates[0].Name));   // the two frames really look different
        Assert.Equal(expected, Sheet("A frame from someone else's PC"));
    });

    [Fact]
    public void ExportAll_ACardWhoseFrameIsntInstalled_WearsTheHouseFrame() => TestHelpers.RunSta(() =>
    {
        var (templates, house) = HouseFrame();
        var outDir = TempRoot("exporthouse");
        try
        {
            var result = BatchService.ExportAll(new[] { OnFrame(house), OnFrame("A frame from someone else's PC") },
                templates, outDir, new SymbolService(), houseFrame: house);
            Assert.Equal(2, result.Exported);
            var files = Directory.GetFiles(outDir, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            byte[] Read(string f) => TestHelpers.Pixels(BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(f))));
            Assert.Equal(Read(files[0]), Read(files[1]));
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    });

    // --- two frames with one name ------------------------------------------------------------------------

    private static string Bundle(string root, string name)
    {
        var src = Path.Combine(root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(src);
        new TemplateSpec { Name = name, CustomFrame = true }.Save(Path.Combine(src, "template.json"));
        File.WriteAllBytes(Path.Combine(src, "frame.png"), TestHelpers.PngBytes(75, 105));
        var bundle = Path.Combine(root, $"{Guid.NewGuid():N}.cardframe");
        TemplateImporter.ExportBundle(src, bundle);
        return bundle;
    }

    private static void RemoveFrames(string name)
    {
        foreach (var dir in Directory.GetDirectories(AppPaths.TemplatesDir))
            try { if (TemplateSpec.Load(Path.Combine(dir, "template.json")).Name.StartsWith(name, StringComparison.Ordinal)) Directory.Delete(dir, true); }
            catch { }
    }

    [Fact]
    public void ImportingAFrameWhoseNameIsTaken_KeepsBoth_UnderTheirOwnNames()
    {
        var root = TempRoot("dupname");
        var name = "Shared " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Directory.CreateDirectory(root);
            Assert.Equal(name, TemplateImporter.ImportBundle(Bundle(root, name)));
            Assert.Equal($"{name} (2)", TemplateImporter.ImportBundle(Bundle(root, name)));
            Assert.Equal($"{name} (3)", TemplateImporter.CreateFromFrame(name, TestHelpers.PngBytes(75, 105)));

            IReadOnlyList<Template> loaded = [];
            TestHelpers.RunSta(() => loaded = new TemplateService().LoadAll());
            Assert.Single(loaded, t => t.Name == name);
            Assert.Single(loaded, t => t.Name == $"{name} (2)");
        }
        finally
        {
            RemoveFrames(name);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void RenamingAFrameToATakenName_GivesItAFreeOne_ButKeepingItsOwnNameIsFine()
    {
        var root = TempRoot("renamedup");
        var name = "Taken " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Directory.CreateDirectory(root);
            TemplateImporter.ImportBundle(Bundle(root, name));
            var other = TemplateImporter.ImportBundle(Bundle(root, name + " other"));
            var otherDir = Directory.GetDirectories(AppPaths.TemplatesDir)
                .Single(d => File.Exists(Path.Combine(d, "template.json")) && TemplateSpec.Load(Path.Combine(d, "template.json")).Name == other);

            Assert.Equal($"{name} (2)", TemplateImporter.UniqueFrameName(name, otherDir));   // renaming "other" to the taken name
            Assert.Equal(other, TemplateImporter.UniqueFrameName(other, otherDir));            // keeping its own name
            Assert.Contains(BuiltInTemplates.All().First().Name, TemplateImporter.InstalledFrameNames());
        }
        finally
        {
            RemoveFrames(name);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // --- restoring a picture frame set aside by mistake ------------------------------------------------------

    private static (string root, string dir, TemplateSpec spec, byte[] picture) PictureFrame(string tag)
    {
        var root = TempRoot(tag);
        var dir = Path.Combine(root, "mine");
        Directory.CreateDirectory(dir);
        var spec = new TemplateSpec { Name = "Mine", CustomFrame = true };
        spec.Save(Path.Combine(dir, "template.json"));
        return (root, dir, spec, TestHelpers.PngBytes(75, 105, 0xFF8A2BE2));
    }

    private static void Load(string root) => TestHelpers.RunSta(() => new TemplateService().LoadFrom(root));

    private static byte[] Placeholder(TemplateSpec spec)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cardinator-ph-{Guid.NewGuid():N}.png");
        try { TestHelpers.RunSta(() => FrameGenerator.Generate(spec, tmp)); return File.ReadAllBytes(tmp); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ThePicture_ComesBack_EvenWhenAPlaceholderWasSetAsideAfterIt()
    {
        var (root, dir, spec, picture) = PictureFrame("restore-two");
        try
        {
            var framePath = Path.Combine(dir, "frame.png");
            var placeholder = Placeholder(spec);
            File.WriteAllBytes(Path.Combine(dir, "frame.png.corrupt-20261001-090000"), picture);       // the picture, first
            File.WriteAllBytes(Path.Combine(dir, "frame.png.corrupt-20261002-090000"), placeholder);   // then its placeholder
            File.WriteAllBytes(framePath, placeholder);                                                // and another one

            Load(root);
            Assert.Equal(picture, File.ReadAllBytes(framePath));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void APlaceholder_IsKnownByItsTag_AfterTheFramesRegionsAreEdited()
    {
        var (root, dir, spec, picture) = PictureFrame("restore-tag");
        try
        {
            var framePath = Path.Combine(dir, "frame.png");
            Load(root);   // no frame at all: a placeholder is drawn, and tagged
            Assert.True(File.Exists(framePath + TemplateService.PlaceholderTag));

            spec.ArtWindow = new Region { X = 40, Y = 90, W = 600, H = 500 };   // the regions tuned in Frame Design since
            spec.Save(Path.Combine(dir, "template.json"));
            File.WriteAllBytes(Path.Combine(dir, "frame.png.corrupt-20261001-090000"), picture);

            Load(root);
            Assert.Equal(picture, File.ReadAllBytes(framePath));
            Assert.False(File.Exists(framePath + TemplateService.PlaceholderTag));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASetAsideFrameThatIsntPutBack_IsKept_AndNeverLookedAtAgain()
    {
        var (root, dir, _, picture) = PictureFrame("restore-once");
        try
        {
            var theirNewFrame = TestHelpers.PngBytes(75, 105, 0xFF20B2AA);
            var setAside = Path.Combine(dir, "frame.png.corrupt-20261001-090000");
            File.WriteAllBytes(setAside, picture);
            File.WriteAllBytes(Path.Combine(dir, "frame.png"), theirNewFrame);

            Load(root);
            Assert.Equal(theirNewFrame, File.ReadAllBytes(Path.Combine(dir, "frame.png")));
            Assert.False(File.Exists(setAside));                                    // decided: not retried on every load
            Assert.Equal(picture, File.ReadAllBytes(Path.Combine(dir, "frame.png.kept-20261001-090000")));   // and still kept
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- a deck site's CSV export ------------------------------------------------------------------------

    [Fact]
    public void AMoxfieldCsv_KeepsTheSetItsNumberBelongsTo()
    {
        var csv = "\"Count\",\"Tradelist Count\",\"Name\",\"Edition\",\"Condition\",\"Language\",\"Foil\",\"Tags\",\"Last Modified\",\"Collector Number\"\n"
                + "\"4\",\"0\",\"Lightning Bolt\",\"m10\",\"Near Mint\",\"English\",\"\",\"\",\"2026-01-01\",\"146\"\n";
        var card = ImportService.Parse(csv, null, "").First().Card;
        Assert.Equal("m10", card.SetCode);
        Assert.Equal("146", card.CollectorNumber);
    }

    [Fact]
    public void ADeckboxCsv_TakesTheSetCode_NotTheSetsFullName()
    {
        var csv = "Count,Tradelist Count,Name,Edition,Edition Code,Card Number,Condition\n"
                + "1,0,Lightning Bolt,Magic 2010,M10,146,Near Mint\n"
                + "1,0,Counterspell,Ice Age,,,Near Mint\n";
        var cards = ImportService.Parse(csv, null, "");
        Assert.Equal("M10", cards[0].Card.SetCode);
        Assert.Equal("146", cards[0].Card.CollectorNumber);
        Assert.Equal("", cards[1].Card.SetCode);   // "Ice Age" is a name, not a code
    }

    // --- bundles inside bundles --------------------------------------------------------------------------

    [Fact]
    public void BundlesNestedDeeperThanTwo_AreNotOpened()
    {
        var root = TempRoot("nested");
        var name = "Nested " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Directory.CreateDirectory(root);
            var inner = Bundle(root, name + " deep");
            for (int level = 0; level < 3; level++)   // the deep frame three zips down; the shallow one in the outermost
            {
                var outer = Path.Combine(root, $"level{level}-{Guid.NewGuid():N}.zip");
                using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
                {
                    if (level == 2) zip.CreateEntryFromFile(Bundle(root, name + " shallow"), "shallow.cardframe");
                    zip.CreateEntryFromFile(inner, Path.GetFileName(inner));
                }
                inner = outer;
            }

            var names = TemplateImporter.ImportBundles(inner);
            Assert.Contains(name + " shallow", names);
            Assert.DoesNotContain(name + " deep", names);
        }
        finally
        {
            RemoveFrames(name);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ----------------------------------------------------------------------------------------------------

    private static void Invoke(object target, string method, params object?[] args)
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
