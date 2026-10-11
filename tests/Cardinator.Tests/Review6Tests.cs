using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The sixth independent review (1.6.31): Esc in Edit details… throwing the dialog's work away unasked; a flip card
/// on a full-art frame stopping a whole render run; the command line's art= guessing pictures by near names, ignoring
/// mistyped options and taking an option for a missing path; Frame Design and Save writing over changes made
/// elsewhere, or splitting a moved set; text under a sideways seal and a Saga or Class creature's P/T box; offline
/// symbols squashed; closing during a lookup losing its work. Each test fails with its fix taken out. In the
/// STA-window collection because some build windows, swap the console or answer prompts.
/// </summary>
[Collection("STAWindows")]
public class Review6Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    // --- Edit details… -------------------------------------------------------------------------------

    [Fact]
    public void EditDetails_CancelWithChanges_AsksFirst_AndKeepEditingKeepsThem()
        => OnAppThread(() =>
        {
            var prompts = new List<string>();
            var card = new CardModel { Name = "Bear", RulesText = "Old text." };
            var w = new DetailsWindow(card);
            ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Negative; };   // Keep editing
            try
            {
                Invoke(w, "OnCancel", w, new RoutedEventArgs());
                Assert.Empty(prompts);                       // nothing changed: nothing to ask

                card.RulesText = "Minutes of new text.";
                Invoke(w, "OnCancel", w, new RoutedEventArgs());
                Assert.Single(prompts);
                Assert.Equal("Minutes of new text.", card.RulesText);

                ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;   // Discard
                Invoke(w, "OnCancel", w, new RoutedEventArgs());
                Assert.Equal("Old text.", card.RulesText);
            }
            finally { ConfirmDialog.TestAnswer = null; }
        });

    [Fact]
    public void EditDetails_EscInATextBox_IsNotCancel()
        => OnAppThread(() =>
        {
            Assert.False(FrameDesignWindow.KeysCloseWindow(new TextBox()));
            var w = new DetailsWindow(new CardModel { Name = "Bear" });
            static IEnumerable<object> All(object o) => new[] { o }.Concat(o is DependencyObject d
                ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(All) : Enumerable.Empty<object>());
            var cancel = All(w).OfType<Button>().Single(b => Equals(b.Content, "Cancel"));
            Assert.False(cancel.IsCancel);   // the Cancel button no longer takes every Esc, typing included
        });

    // --- rendering ------------------------------------------------------------------------------------

    [Fact]
    public void AFlipCard_OnAFullArtFrameWithAWiderGap_Renders()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("flipgap");
            try
            {
                var spec = new TemplateService().LoadAll().First(t => t.Spec.FullArt && !t.Spec.CustomFrame).Spec.Clone();
                spec.Name = "Wide Gap " + Guid.NewGuid().ToString("N")[..6];
                spec.TextBox.Y += 6;   // type line to text box: 12px, where the scrim came out negative and threw
                var dir = Directory.CreateDirectory(Path.Combine(root, "wide-gap")).FullName;
                spec.Save(Path.Combine(dir, "template.json"));
                var tpl = new TemplateService().LoadFrom(root).Single();
                var card = new CardModel
                {
                    Name = "Apprentice", TypeLine = "Creature — Human Wizard", RulesText = "Flip it.", Power = "1", Toughness = "2",
                    HalfLayout = "flip", TemplateName = tpl.Name,
                    OtherHalf = new CardModel { Name = "Ascended", TypeLine = "Legendary Creature", RulesText = "Flying", Power = "3", Toughness = "4" },
                };
                var bmp = new CardRenderer(new SymbolService(drawnOnly: true)).RenderToBitmap(card, TemplateService.ResolveFor(card, tpl), 1);
                Assert.True(bmp.PixelWidth > 0);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void RenderCards_SkipsASetFileBesideTheCards()
    {
        var root = TempRoot("rendercards6");
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "cards")).FullName;
            new CardModel { Name = "Good Card", TypeLine = "Instant" }.Save(Path.Combine(dir, "good.json"));
            ProjectWriter.Write(Path.Combine(dir, "my-set.json"), new[] { new CardModel { Name = "In The Set" } }, "my-set", "", "", new SetProfile());
            int code = -1;
            TestHelpers.RunSta(() => code = SelfTest.RunRenderCards(dir, Path.Combine(root, "out"), new[] { "nosym" }));
            Assert.Equal(0, code);
            Assert.Equal(new[] { "good-card.png" }, Directory.GetFiles(Path.Combine(root, "out")).Select(Path.GetFileName));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SealedGate_SidewaysFrame_FindsItsSeal()
        => TestHelpers.RunSta(() =>
        {
            var gate = new TemplateService().LoadAll().First(t => t.Name == "Sealed Gate");
            var battle = new CardModel { Name = "Siege", TypeLine = "Battle — Siege", Defense = "5" };
            var sideways = TemplateService.ResolveFor(battle, gate);
            Assert.True(sideways.Spec.CanvasWidth > sideways.Spec.CanvasHeight, "not the sideways frame");
            Assert.NotNull(CardRenderer.BottomOrnament(sideways, sideways.Spec));
        });

    [Theory]
    [InlineData("Enchantment Creature — Class Beast")]
    [InlineData("Enchantment Creature — Saga Wizard")]
    public void ASagaOrClassCreature_sRows_EndBeforeItsPtBox(string type)
        => TestHelpers.RunSta(() =>
        {
            var tpl = new TemplateService().LoadAll().First(t => t.Name == "Full Art");
            string rules = type.Contains("Saga")
                ? string.Join("\n", Enumerable.Range(1, 3).Select(i => new string('I', i) + " — " + string.Concat(Enumerable.Repeat("Each opponent loses one life and you gain one life. ", 3))))
                : "Gain 1 life.\n{2}: Level 2\n" + string.Concat(Enumerable.Repeat("Whenever a creature dies, each opponent loses 1 life. ", 4))
                  + "\n{4}: Level 3\n" + string.Concat(Enumerable.Repeat("Creatures you control get +2/+2 and have deathtouch. ", 4));
            var card = new CardModel { Name = "Beast", TypeLine = type, RulesText = rules, Power = "5", Toughness = "5" };
            var (runs, avoid) = new CardRenderer(new SymbolService(drawnOnly: true)).InspectBadgedLayout(card, tpl.Spec);
            Assert.NotNull(avoid);
            var pt = avoid!.Value;
            Assert.DoesNotContain(runs, r => r.IntersectsWith(pt) && r.Width > 1);
        });

    [Fact]
    public void AnOfflineSymbolWiderThanItsDisc_KeepsTheShapeOfAPip()
        => TestHelpers.RunSta(() =>
        {
            var symbols = new SymbolService(drawnOnly: true);
            var pip = symbols.GetSymbol("{2}")!.Drawing.Bounds;
            foreach (var t in new[] { "{1000000}", "{CHOOSE ONE}", "{HW}" })
            {
                var b = symbols.GetSymbol(t)!.Drawing.Bounds;
                Assert.Equal(pip.Width, b.Width, 1);
                Assert.Equal(pip.Height, b.Height, 1);
            }
        });

    // --- command line ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("--restyle a.cardinator fram=Midnight", "unknown option")]
    [InlineData("--renderset a.cardinator only=Vault nosym", "path is missing")]
    [InlineData("--import list.txt frame=Midnight", "path is missing")]
    [InlineData("--render c.json c.png dpi=300 nosim", "unknown option")]
    [InlineData("--listframes extra", "unknown option")]
    public void ABadOption_IsAUsageError(string line, string says)
        => Assert.Contains(says, CliArgs.Problem(line.Split(' ')) ?? "");

    [Theory]
    [InlineData("--restyle a.cardinator frame=Midnight only=A|B templates=x")]
    [InlineData("--renderset a.cardinator out sheet A4 nosym")]
    [InlineData("--import list.txt s.cardinator noart append name=Show templates=f")]
    [InlineData("--selftest anything=goes")]
    public void GoodOptions_Pass(string line) => Assert.Null(CliArgs.Problem(line.Split(' ')));

    [Fact]
    public void ATemplatesFolderThatIsntThere_IsAUsageError()
    {
        var gone = Path.Combine(Path.GetTempPath(), "cardinator-no-such-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(2, SetCommands.RunListFrames(new[] { "templates=" + gone }));
    }

    [Fact]
    public void ArtMatching_FromTheCommandLine_IsExactNamesOnly_AndNameNamesTheSet()
    {
        var root = TempRoot("exactart");
        try
        {
            Directory.CreateDirectory(root);
            var list = Path.Combine(root, "deck.csv");
            File.WriteAllText(list, "name,type,text\nForest,Basic Land — Forest,\nSnow-Covered Forest,Basic Snow Land — Forest,\n");
            var art = Directory.CreateDirectory(Path.Combine(root, "art")).FullName;
            File.WriteAllBytes(Path.Combine(art, "Snow-Covered Forest.png"), TestHelpers.PngBytes(40, 30));
            var set = Path.Combine(root, "sets", "show.building.cardinator");
            var old = Console.Out;
            Console.SetOut(new StringWriter());
            int code;
            try { code = SetCommands.RunImport(list, set, new[] { "art=" + art, "noart", "name=show" }); }
            finally { Console.SetOut(old); }
            Assert.Equal(0, code);
            var project = CardProject.LoadResolved(set, out _);
            Assert.Equal("show", project.Name);
            Assert.True(string.IsNullOrEmpty(project.Cards.Single(c => c.Name == "Forest").ArtPath), "Forest got another card's picture");
            Assert.False(string.IsNullOrEmpty(project.Cards.Single(c => c.Name == "Snow-Covered Forest").ArtPath));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- writing over changes made elsewhere ---------------------------------------------------------

    [Fact]
    public void FrameDesign_Apply_AsksWhenTheFrameWasSavedElsewhere()
        => OnAppThread(() =>
        {
            var root = TempRoot("framechanged");
            try
            {
                var spec = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame).Spec.Clone();
                spec.Name = "Shared Frame " + Guid.NewGuid().ToString("N")[..6];
                var dir = Directory.CreateDirectory(Path.Combine(root, "shared-frame")).FullName;
                var json = Path.Combine(dir, "template.json");
                spec.Save(json);
                File.AppendAllText(json, "\n");   // laid out unlike Apply writes it, so Apply's own write is a change
                var tpl = new TemplateService().LoadFrom(root).Single();
                var win = new FrameDesignWindow(tpl) { Standalone = true };
                win.Show();

                File.AppendAllText(json, " ");   // --editframe / another window / a script saved it meanwhile
                var elsewhere = File.ReadAllText(json);
                var prompts = new List<string>();
                ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Cancel; };
                try { Invoke(win, "OnApply", win, new RoutedEventArgs()); }
                finally { ConfirmDialog.TestAnswer = null; }
                Assert.Contains("Frame changed elsewhere", prompts);
                Assert.Equal(elsewhere, File.ReadAllText(json));   // Cancel: their change is still there

                // Its own Apply doesn't count as "elsewhere" next time.
                var answers = new List<string>();
                ConfirmDialog.TestAnswer = t => { answers.Add(t); return ConfirmResult.Negative; };   // Replace them
                try { Invoke(win, "OnApply", win, new RoutedEventArgs()); }
                finally { ConfirmDialog.TestAnswer = null; }
                Assert.Equal(new[] { "Frame changed elsewhere" }, answers);
                Assert.NotEqual(elsewhere, File.ReadAllText(json));   // replaced
                prompts.Clear();
                ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Cancel; };
                try { Invoke(win, "OnApply", win, new RoutedEventArgs()); }
                finally { ConfirmDialog.TestAnswer = null; }
                Assert.DoesNotContain("Frame changed elsewhere", prompts);
                win.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void Save_NoticesASetChangedElsewhere_OrMoved()
        => OnAppThread(() =>
        {
            var root = TempRoot("savechanged");
            try
            {
                var folder = Path.Combine(root, "My Set");
                var setFile = Path.Combine(folder, "My Set.cardinator");
                ProjectWriter.Write(setFile, new[] { new CardModel { Name = "Hero" } }, "My Set", "", "", new SetProfile());
                var main = new MainWindow { SuppressClosePrompt = true };
                Invoke(main, "LoadProjectFile", setFile);
                Assert.Null(main.SaveConflict());

                File.SetLastWriteTimeUtc(setFile, DateTime.UtcNow.AddMinutes(5));   // a command saved it meanwhile
                Assert.False(main.SaveConflict()!.Value.Moved);

                Directory.Move(folder, Path.Combine(root, "Renamed Set"));
                var moved = main.SaveConflict()!.Value;
                Assert.True(moved.Moved);
                Assert.Contains("without its art", moved.Message);   // the folder is gone, not just the file
                main.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- the window ------------------------------------------------------------------------------------

    [Fact]
    public void AnEditDuringALookup_IsOneUndoStepWithIt()
        => OnAppThread(() =>
        {
            var main = new MainWindow { SuppressClosePrompt = true };
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_undoTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!;
            timer.Stop();
            main.Busy = true;
            main.SelectedCard!.RulesText = "Looked-up text, art still coming";
            Assert.False(timer.IsEnabled, "the undo timer ran while the lookup was still going");
            main.Busy = false;
            Assert.True(timer.IsEnabled, "the held edit was never queued");
            main.Close();
        });

    [Fact]
    public void ClosingDuringALookup_Asks()
        => OnAppThread(() =>
        {
            var main = new MainWindow();
            main.Busy = true;
            var prompts = new List<string>();
            ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Affirmative; };   // Keep working
            try
            {
                var e = new CancelEventArgs();
                Invoke(main, "OnWindowClosing", main, e);
                Assert.True(e.Cancel);
                Assert.Contains("Still working", prompts);
            }
            finally { ConfirmDialog.TestAnswer = null; main.Busy = false; main.SuppressClosePrompt = true; main.Close(); }
        });

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
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
