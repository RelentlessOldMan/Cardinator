using System.IO;
using System.IO.Compression;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.26, from the shared-set trial: a "Share set + frames" zip had to be unzipped by hand before the set could be
/// opened (opened from inside the zip, Windows runs it from a temp folder: no art, and saves lost there), and since
/// 1.6.20 importing a frame that was already installed made a copy, "Name (2)". Now the zip opens as a set — unpacked
/// into a folder of its own, its frames installed (an identical one reused), its cards pointed at a frame that had to
/// be renamed — and a drop mid-job is turned away. Each test fails with its fix taken out. In the STA-window
/// collection because some build a <see cref="Cardinator.MainWindow"/>.
/// </summary>
[Collection("STAWindows")]
public class SharedSetTests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    /// <summary>A saved set using a custom frame, shared as a zip; returns the zip. The frame is installed while sharing
    /// and removed again, as on the sender's machine vs. the recipient's.</summary>
    private static string ShareASet(string root, string frameName, uint colour = 0xFF2850A0)
    {
        var setDir = Path.Combine(root, "sender", "My Set");
        Directory.CreateDirectory(Path.Combine(setDir, "art"));
        File.WriteAllBytes(Path.Combine(setDir, "art", "hero.png"), TestHelpers.PngBytes(40, 30));
        var project = Path.Combine(setDir, "My Set.cardinator");
        new CardProject
        {
            Name = "My Set", DefaultTemplate = frameName,
            Cards = { new CardModel { Name = "Hero", TemplateName = frameName, ArtPath = Path.Combine("art", "hero.png") } },
        }.Save(project);

        var frameDir = Path.Combine(root, "sender", "frames", TextUtil.Slug(frameName));
        Directory.CreateDirectory(frameDir);
        new TemplateSpec { Name = frameName, CustomFrame = true }.Save(Path.Combine(frameDir, "template.json"));
        File.WriteAllBytes(Path.Combine(frameDir, "frame.png"), TestHelpers.PngBytes(75, 105, colour));
        var zip = Path.Combine(root, "My Set.zip");
        SetPackager.ExportSetWithFrames(project, new[] { frameDir }, zip);
        return zip;
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

    [Fact]
    public void ASharedSet_Unpacks_InstallsItsFrame_AndOpensWithItsArt()
    {
        var root = TempRoot("shared");
        var frame = "Shared Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var zip = ShareASet(root, frame);
            Assert.True(SetPackager.IsSharedSet(zip));
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);

            var project = SetPackager.UnpackSharedSet(zip, parent);
            Assert.Equal(Path.Combine(parent, "My Set", "My Set.cardinator"), project);
            Assert.True(File.Exists(Path.Combine(parent, "My Set", "art", "hero.png")));
            Assert.Contains(new TemplateService().LoadAll(), t => t.Name == frame);
            Assert.Equal(frame, CardProject.Load(project).Cards.Single().TemplateName);

            // Opened again: a folder of its own, the first left as it was, the frame not copied.
            var again = SetPackager.UnpackSharedSet(zip, parent);
            Assert.Equal(Path.Combine(parent, "My Set (2)", "My Set.cardinator"), again);
            Assert.Single(new TemplateService().LoadAll(), t => t.Name.StartsWith(frame, StringComparison.Ordinal));
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ADifferentFrameOfTheSameName_IsKept_AndTheSetsCardsUseTheirOwn()
    {
        var root = TempRoot("shared-clash");
        var frame = "Clash Frame " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            // The recipient already has a (different) frame of that name.
            var mineZip = Path.Combine(root, "mine.cardframe");
            var mine = Path.Combine(root, "mine");
            Directory.CreateDirectory(mine);
            new TemplateSpec { Name = frame, CustomFrame = true }.Save(Path.Combine(mine, "template.json"));
            File.WriteAllBytes(Path.Combine(mine, "frame.png"), TestHelpers.PngBytes(75, 105, 0xFFAA2222));
            TemplateImporter.ExportBundle(mine, mineZip);
            Assert.Equal(frame, TemplateImporter.ImportBundle(mineZip));

            var zip = ShareASet(root, frame, 0xFF2850A0);
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            var project = CardProject.Load(SetPackager.UnpackSharedSet(zip, parent));
            Assert.Equal($"{frame} (2)", project.Cards.Single().TemplateName);
            Assert.Equal($"{frame} (2)", project.DefaultTemplate);
            Assert.Contains(new TemplateService().LoadAll(), t => t.Name == frame);   // theirs is still there
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheSameFrameImportedTwice_IsTheOneAlreadyInstalled()
    {
        var root = TempRoot("reimport");
        var frame = "Twice " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            new TemplateSpec { Name = frame, CustomFrame = true }.Save(Path.Combine(src, "template.json"));
            File.WriteAllBytes(Path.Combine(src, "frame.png"), TestHelpers.PngBytes(75, 105));
            var bundle = Path.Combine(root, "f.cardframe");
            TemplateImporter.ExportBundle(src, bundle);

            Assert.Equal(frame, TemplateImporter.ImportBundles(bundle).Single());
            Assert.Equal(frame, TemplateImporter.ImportBundles(bundle).Single());
            Assert.Single(new TemplateService().LoadAll(), t => t.Name.StartsWith(frame, StringComparison.Ordinal));
        }
        finally { RemoveInstalled(frame); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ASharedSetZip_NeverWritesOutsideItsOwnFolder()
    {
        var root = TempRoot("shared-slip");
        try
        {
            var zip = Path.Combine(root, "evil.zip");
            Directory.CreateDirectory(root);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("Evil.cardinator").Open())) w.Write("""{ "name": "Evil", "cards": [] }""");
                using (var w = new StreamWriter(z.CreateEntry("art/../../escaped.txt").Open())) w.Write("x");
                using (var w = new StreamWriter(z.CreateEntry("art/ok.txt").Open())) w.Write("ok");
            }
            var parent = Path.Combine(root, "recipient");
            Directory.CreateDirectory(parent);
            var project = SetPackager.UnpackSharedSet(zip, parent);
            Assert.True(File.Exists(Path.Combine(parent, "Evil", "art", "ok.txt")));
            Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
            Assert.True(File.Exists(project));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AZipOfFramesAlone_IsNotASharedSet()
    {
        var root = TempRoot("frames-only");
        try
        {
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, "frames.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("a/template.json").Open())) w.Write("{}");
            Assert.False(SetPackager.IsSharedSet(zip));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- in the window --------------------------------------------------------------------------------

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

    private static Task Drop(Cardinator.MainWindow main, string file) =>
        (Task)main.GetType().GetMethod("DropFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(main, new object[] { new[] { file }, new Point(0, 0) })!;

    [Fact]
    public void ASharedSetDroppedOnTheWindow_OpensTheSet()
        => OnAppThread(() =>
        {
            var root = TempRoot("shared-drop");
            var frame = "Dropped Set Frame " + Guid.NewGuid().ToString("N")[..6];
            string? unpacked = null;
            try
            {
                var zip = ShareASet(root, frame);
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var asked = new List<string>();
                ConfirmDialog.TestAnswer = t => { asked.Add(t); return ConfirmResult.Negative; };
                try { Drop(main, zip).GetAwaiter().GetResult(); }
                finally { ConfirmDialog.TestAnswer = null; }
                Assert.True(asked.All(t => t == "Unsaved changes"), "asked: " + string.Join(", ", asked));
                Assert.Equal("Hero", Assert.Single(main.Cards).Name);
                Assert.Contains(main.Templates, t => t.Name == frame);
                Assert.Contains("Unpacked to", main.Status);
                unpacked = Path.GetDirectoryName(Path.GetFullPath(main.Cards[0].ArtPath));   // …\My Set\art
                Assert.True(File.Exists(main.Cards[0].ArtPath), "the card's art didn't come with it");
            }
            finally
            {
                RemoveInstalled(frame);
                if (unpacked != null) try { Directory.Delete(Path.GetDirectoryName(unpacked)!, true); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        });

    [Fact]
    public void ADropWhileAJobRuns_IsTurnedAway()
        => OnAppThread(() =>
        {
            var root = TempRoot("busy-drop");
            try
            {
                Directory.CreateDirectory(root);
                var set = Path.Combine(root, "Other.cardinator");
                new CardProject { Name = "Other", Cards = { new CardModel { Name = "Intruder" } } }.Save(set);
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                var before = main.Cards.Select(c => c.Name).ToList();
                main.Busy = true;
                Drop(main, set).GetAwaiter().GetResult();
                Assert.Equal(before, main.Cards.Select(c => c.Name).ToList());
                Assert.Contains("Still working", main.Status);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });
}
