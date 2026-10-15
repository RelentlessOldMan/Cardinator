using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;
using Layout = Cardinator.MainWindow.CardLayout;

namespace Cardinator.Tests;

/// <summary>
/// The seventh independent review (1.7.2): Set settings' Replace writing every field over the cards' own, and leaving
/// the card back preview stale; meld cards with no melded name pairing up wrongly; built-in frames replaced on upgrade
/// without a word; the layout dropdown's arrow keys changing the layout at every press; a face holding only flavor or
/// P/T thrown away unasked; a list file's folder kept after its list was replaced; a designed card back's wordmark
/// vanishing on a pale back; the layout audit taking overflowed rules for the footer, and a mistyped --qa case
/// passing. Each test fails with its fix taken out. In the STA-window collection because most build the main window.
/// </summary>
[Collection("STAWindows")]
public class Review7Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    private static Cardinator.MainWindow NewMain()
    {
        var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
        main.Cards.Clear();
        return main;
    }

    // --- Set settings -----------------------------------------------------------------------------------

    [Fact]
    public void Replace_PutsOnlyTheChangedFields_OnTheCards()
        => OnAppThread(() =>
        {
            var main = NewMain();
            var frames = main.Templates.Select(t => t.Name).ToList();
            main.ApplySetSettings(new SetProfile { Rarity = "R", SetCode = "SET" }, frames[0], SetSettingsMode.NewCardsOnly);
            var a = new CardModel { Name = "A", Rarity = "M", SetCode = "SET", TemplateName = frames[1] };
            main.Cards.Add(a);
            main.SelectedCard = a;
            main.CommitHistory();

            // Only the artist changed in this visit: the rarity, set code and frame shown are just what the set had.
            main.ApplySetSettings(new SetProfile { Rarity = "R", SetCode = "SET", Artist = "New" }, frames[0], SetSettingsMode.Replace);
            Assert.Equal("New", a.Artist);
            Assert.Equal("M", a.Rarity);
            Assert.Equal(frames[1], a.TemplateName);
            Assert.Contains("1 changed field", main.Status);

            main.ApplySetSettings(new SetProfile { Rarity = "R", SetCode = "SET", Artist = "New" }, frames[0], SetSettingsMode.Replace);
            Assert.Contains("nothing changed", main.Status);
            Assert.Equal("M", a.Rarity);
        });

    [Fact]
    public void SavingANewCardBack_RedrawsTheCardBackOnShow()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.ShowSide(back: true);
            var before = TestHelpers.Pixels(main.PreviewImage!);
            main.ApplySetSettings(new SetProfile { CardBack = new CardBackDesign { Style = "designed", Color = "#7A1020" } }, "",
                SetSettingsMode.NewCardsOnly);
            Assert.NotEqual(before, TestHelpers.Pixels(main.PreviewImage!));   // no RenderPreview of our own
        });

    // --- meld -------------------------------------------------------------------------------------------

    [Fact]
    public void MeldCards_WithNoMeldedName_PairByTheirPartnersNames()
        => OnAppThread(() =>
        {
            var main = NewMain();
            CardModel M(string name, string with, bool bottom) => new()
            {
                Name = name, MeldWith = with, MeldHalf = bottom ? "bottom" : "top", BackFace = new CardModel(),
            };
            var a = M("Alpha", "Beta", false);
            var c = M("Gamma", "Delta", false);
            var d = M("Delta", "Gamma", true);
            var b = M("Beta", "Alpha", true);
            foreach (var x in new[] { a, c, d, b }) main.Cards.Add(x);
            Assert.Same(b, main.MeldPartner(a));
            Assert.Same(d, main.MeldPartner(c));
            Assert.Same(a, main.MeldPartner(b));
        });

    [Fact]
    public void AddMeldPartner_GivesABlankMeldedCardAName()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            main.ChangeLayout(card, Layout.Meld);
            card.BackFace!.Name = "";
            main.AddMeldPartner();
            Assert.StartsWith("Melded card", card.BackFace!.Name);
            var partner = main.Cards[main.Cards.IndexOf(card) + 1];
            Assert.Same(partner, main.MeldPartner(card));
        });

    // --- built-in frames brought up to date ---------------------------------------------------------------

    [Fact]
    public void AReplacedBuiltIn_IsRecorded_AndPutBackOnRequest_ForGood()
        => OnAppThread(() =>
        {
            var root = TempRoot("putback");
            try
            {
                var builtIn = BuiltInTemplates.All().First();
                var dir = Path.Combine(root, TextUtil.Slug(builtIn.Name));
                Directory.CreateDirectory(dir);
                var old = builtIn.Clone();
                old.PtBox.Y += 40;
                old.Save(Path.Combine(dir, "template.json"));
                var oldJson = File.ReadAllText(Path.Combine(dir, "template.json"));
                File.WriteAllBytes(Path.Combine(dir, "frame.png"), TestHelpers.PngBytes(8, 8));

                Assert.True(TemplateService.UpgradeBuiltIn(dir, builtIn, Path.Combine(root, "replaced"), out var kept));
                Assert.NotNull(kept);
                lock (TemplateService.ReplacedBuiltIns) TemplateService.ReplacedBuiltIns.Add((builtIn.Name, dir, kept!));

                var prompts = new List<string>();
                ConfirmDialog.TestAnswer = t => { prompts.Add(t); return ConfirmResult.Negative; };   // Put my copies back
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                try { main.TellAboutReplacedFrames(); }
                finally { ConfirmDialog.TestAnswer = null; }

                Assert.Single(prompts);
                Assert.Equal(oldJson, File.ReadAllText(Path.Combine(dir, "template.json")));
                Assert.Equal(TestHelpers.PngBytes(8, 8), File.ReadAllBytes(Path.Combine(dir, "frame.png")));
                Assert.True(File.Exists(Path.Combine(dir, TemplateService.UserEditedTag)));
                Assert.False(TemplateService.UpgradeBuiltIn(dir, builtIn, Path.Combine(root, "replaced")));   // stays theirs
                lock (TemplateService.ReplacedBuiltIns) Assert.Empty(TemplateService.ReplacedBuiltIns);

                main.TellAboutReplacedFrames();   // told once
                Assert.Single(prompts);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    // --- the layout dropdown and changing layouts -----------------------------------------------------------

    [Fact]
    public void ArrowKeys_OnTheClosedLayoutDropdown_OpenIt_AndChangeNothing()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(main, "OnLayoutKeyDown", main.LayoutBox, e);
            Assert.True(e.Handled);   // the ComboBox never sees it (and opens its list: only when shown, so not here)
            Assert.Equal(Layout.Normal, Cardinator.MainWindow.LayoutOf(card));

            var tab = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(main, "OnLayoutKeyDown", main.LayoutBox, tab);
            Assert.False(tab.Handled);
        });

    [Theory]
    [InlineData("flavor")]
    [InlineData("pt")]
    [InlineData("loyalty")]
    [InlineData("defense")]
    public void ABackFaceWithOnlyFlavorOrNumbers_IsAskedAboutBeforeItGoes(string what)
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var card = main.SelectedCard!;
            main.ChangeLayout(card, Layout.DoubleFaced);
            var back = card.BackFace!;
            back.Name = ""; back.TypeLine = ""; back.RulesText = ""; back.ManaCost = ""; back.ArtPath = "";
            switch (what)
            {
                case "flavor": back.FlavorText = "A line worth keeping."; break;
                case "pt": back.Power = "4"; break;
                case "loyalty": back.Loyalty = "3"; break;
                case "defense": back.Defense = "5"; break;
            }
            int asked = 0;
            ConfirmDialog.TestAnswer = _ => { asked++; return ConfirmResult.Negative; };
            try { main.ChangeLayout(card, Layout.Normal); }
            finally { ConfirmDialog.TestAnswer = null; }
            Assert.Equal(1, asked);
        });

    // --- Import cards… --------------------------------------------------------------------------------------

    [Fact]
    public void AListFilesFolder_GoesWithItsList_NotWithOnePastedOverIt()
        => OnAppThread(() =>
        {
            var dir = TempRoot("listdir");
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "list.csv");
                File.WriteAllText(file, "name,art\nLightning Bolt,bolt.png\nShock,shock.png\n");
                var w = new DeckListWindow();
                w.LoadFile(file);
                w.DeckBox.Text = w.DeckBox.Text.Replace("Shock,shock.png", "Opt,opt.png");   // touched up: still the file's
                Assert.Equal(dir, w.BaseDir);
                Assert.Equal("list.csv", w.FileName);

                w.DeckBox.Text = "1 Counterspell\n1 Island\n";   // a different list pasted over it
                Assert.Equal("", w.BaseDir);
                Assert.Equal("", w.FileName);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

    // --- the designed card back -------------------------------------------------------------------------------

    [Theory]
    [InlineData("#F0E6D0")]
    [InlineData("#FFFFFF")]
    [InlineData("")]           // the default navy: light lettering, as before
    [InlineData("#7A1020")]
    public void TheWordmark_StandsOutFromTheBack(string color)
        => TestHelpers.RunSta(() =>
        {
            var design = new CardBackDesign { Style = "designed", Color = color, Wordmark = "WWWWWW" };
            var with = TestHelpers.Pixels(BackRenderer.Render(design, "S", null, 1));
            design.Wordmark = " ";
            var without = TestHelpers.Pixels(BackRenderer.Render(design, "S", null, 1));
            double ink = 0, ground = 0; int n = 0;
            for (int i = 0; i < with.Length; i += 4)
            {
                if (Math.Abs(with[i] - without[i]) + Math.Abs(with[i + 1] - without[i + 1]) + Math.Abs(with[i + 2] - without[i + 2]) < 90) continue;
                ink += Lum(with, i); ground += Lum(without, i); n++;
            }
            Assert.True(n > 200, $"only {n} wordmark pixels");
            Assert.True(Math.Abs(ink - ground) / n > 0.3, $"wordmark {ink / n:F2} on {ground / n:F2}");
        });

    [Fact]
    public void ADoubleSidedSheet_PrintsTheSetsDesignedBack_InItsColour_WithItsSymbol()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var cards = new List<CardModel> { new() { Name = "Solo", TypeLine = "Instant", TemplateName = templates[0].Name } };
            var red = new CardBackDesign { Style = "designed", Color = "#B01020" };
            var pages = SheetExporter.ComposeDoubleSided(cards, templates, new SymbolService(drawnOnly: true), PageSpec.Letter,
                BackRenderer.Render(red, "S", null, 1)).ToList();
            // The back of front slot 0 is the top-right slot (mirrored for a long-edge flip); read its upper panel.
            var page = TestHelpers.Pixels(pages[1]);
            int at = ((75 + 200) * pages[1].PixelWidth + 150 + 2 * 750 + 375) * 4;
            Assert.True(page[at + 2] > page[at + 1] + 40 && page[at + 2] > page[at] + 40,
                $"not red: R{page[at + 2]} G{page[at + 1]} B{page[at]}");

            var root = TempRoot("backsym");
            Directory.CreateDirectory(root);
            try
            {
                var symbol = Path.Combine(root, "sym.png");
                File.WriteAllBytes(symbol, TestHelpers.PngBytes(40, 40, 0xFF20C020));
                red.UseSetSymbol = true;
                var withSymbol = TestHelpers.Pixels(BackRenderer.Render(red, "S", symbol, 1));
                red.UseSetSymbol = false;
                Assert.NotEqual(TestHelpers.Pixels(BackRenderer.Render(red, "S", symbol, 1)), withSymbol);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    private static double Lum(byte[] bgra, int i)
        => BackRenderer.Luminance(Color.FromRgb(bgra[i + 2], bgra[i + 1], bgra[i]));

    // --- the layout audit -----------------------------------------------------------------------------------

    [Fact]
    public void TheAudit_TakesRulesUnderTheTextBox_ForOverflow_NotTheFooter() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var card = new CardModel
        {
            Name = "Overflow Probe", TypeLine = "Creature — Bear", RulesText = "Whenever this creature attacks, draw a card.",
            Power = "2", Toughness = "2", Artist = "Someone Else", TemplateName = tpl.Name,
        };
        var used = TemplateService.ResolveFor(card, tpl);
        var spec = used.Spec;
        var pt = spec.PtBox;
        var tb = spec.EffectiveTextBox;
        Drawing At(string text)
        {
            // A run beside and under the P/T box, below the text box: where an overflowing last line lands.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Georgia"), 22, Brushes.Black, 1.0), new Point(pt.X - 60, Math.Max(pt.Y + 4, tb.Y + tb.H)));
            var g = new DrawingGroup();
            g.Children.Add(new CardRenderer(new SymbolService(drawnOnly: true)).DrawToGroup(card, tpl));
            g.Children.Add(VisualTreeHelper.GetDrawing(dv));
            return g;
        }
        Assert.Contains(LayoutAudit.Inspect(At("draw a card."), card, spec), f => f.Kind == "under the P/T box");
        Assert.DoesNotContain(LayoutAudit.Inspect(At("Someone Else"), card, spec), f => f.Kind == "under the P/T box");

        Assert.True(LayoutAudit.FromTheTextBox("this creature attacks,", card));
        Assert.False(LayoutAudit.FromTheTextBox("Someone", card));
        Assert.False(LayoutAudit.FromTheTextBox("12", card));   // too short to tell
    });

    [Fact]
    public void Qa_ACaseNameMatchingNothing_IsAnError()
    {
        var dir = TempRoot("qacase");
        try { Assert.Equal(2, SelfTest.RunQaFull(dir, "no such case at all")); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private sealed class FakeSource : PresentationSource
    {
        private Visual? _root;
        public override Visual RootVisual { get => _root!; set => _root = value; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static object? Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
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
