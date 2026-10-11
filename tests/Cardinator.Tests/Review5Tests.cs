using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The fifth independent review (1.6.30): Frame Design deleting a frame picture the user made, or overwriting the
/// frame when Enter was pressed on Cancel; a prototype's text sized for a seal and P/T box a line lower than drawn;
/// a cancelled Edit details… leaving an undo step; themed sets re-imported twice over or copying their pictures again;
/// Fill blanks swapping a custom-numbered card's art for a crossover's; an offline import trying every card. Each
/// test fails with its fix taken out. In the STA-window collection because some build windows or swap
/// <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class Review5Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    // --- Frame Design ---------------------------------------------------------------------------------

    /// <summary>A drawn frame copied into a folder of its own, with the user's picture as its frame.png (no hash: the
    /// docs' "replace it with your own transparent PNG").</summary>
    private static (Template Tpl, string Dir, byte[] Picture) HandMadeFrame(string root)
    {
        var spec = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame).Spec.Clone();
        spec.Name = "My Painted Frame " + Guid.NewGuid().ToString("N")[..6];
        var dir = Directory.CreateDirectory(Path.Combine(root, "my-painted-frame")).FullName;
        spec.Save(Path.Combine(dir, "template.json"));
        var picture = TestHelpers.PngBytes(spec.CanvasWidth, spec.CanvasHeight, 0xFF33AA55);
        File.WriteAllBytes(Path.Combine(dir, "frame.png"), picture);
        var tpl = new TemplateService().LoadFrom(root).Single();
        return (tpl, dir, picture);
    }

    [Fact]
    public void FrameDesign_Apply_KeepsThePictureTheUserPutInADrawnFrame()
        => OnAppThread(() =>
        {
            var root = TempRoot("handmade");
            try
            {
                var (tpl, dir, picture) = HandMadeFrame(root);
                Assert.Equal(picture, File.ReadAllBytes(Path.Combine(dir, "frame.png")));   // loading kept it
                var win = new FrameDesignWindow(tpl) { Standalone = true };
                win.Show();
                win.NameBox.Text = tpl.Name;
                Invoke(win, "OnApply", win, new RoutedEventArgs());
                Assert.Equal(picture, File.ReadAllBytes(Path.Combine(dir, "frame.png")));

                // Save as new… takes the picture along (and the copy is hand-made too: no hash beside it).
                var copy = Path.Combine(root, "copy");
                Invoke(win, "WriteTemplate", copy, dir);
                Assert.Equal(picture, File.ReadAllBytes(Path.Combine(copy, "frame.png")));
                Assert.True(TemplateService.IsHandMade(Path.Combine(copy, "frame.png"), Path.Combine(copy, "frame.png.hash")));
                win.Close();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void FrameDesign_EnterOnAButton_PressesThatButton_NotApply()
        => OnAppThread(() =>
        {
            Assert.False(FrameDesignWindow.EnterApplies(new Button()));      // Cancel, Save as new…
            Assert.False(FrameDesignWindow.EnterApplies(new TextBox()));
            Assert.True(FrameDesignWindow.EnterApplies(new ComboBox()));
            Assert.True(FrameDesignWindow.EnterApplies(new CheckBox()));     // a knob: Enter still applies
            Assert.True(FrameDesignWindow.KeysCloseWindow(new Button()));    // and Esc on a button still cancels
        });

    // --- prototype / mutate sizing --------------------------------------------------------------------

    [Theory]
    [InlineData("Alchemist's Steel")]
    [InlineData("Sealed Gate")]
    [InlineData("Slate Artifact")]
    public void APrototypesRest_IsDrawnAtTheSizeTheBandChose(string frame) => TestHelpers.RunSta(() =>
    {
        var t = new TemplateService().LoadAll().First(x => x.Name == frame);
        var r = new CardRenderer(new SymbolService());
        var rest = "Menace, lifelink, ward—Pay life equal to this creature's power.\nWhen this creature enters, each opponent sacrifices a creature with the greatest power among creatures they control, then loses life equal to that creature's toughness.";
        int checkedSizes = 0;
        foreach (var more in new[] { "", " Then draw a card.", " Then draw a card and each opponent loses 1 life.", " Then draw a card, then discard a card unless you control an artifact. Scry 2." })
        {
            var card = new CardModel
            {
                Name = "Card", TypeLine = "Artifact Creature — Horror", Power = "6", Toughness = "5", TemplateName = frame,
                RulesText = "Prototype {1}{B}{B} — 3/3\n" + rest + more,
            };
            r.RenderToBitmap(card, t);
            if (r.LastTopBandSize > Math.Min(11, t.Spec.RulesFont.Size)) checkedSizes++;   // above the floor: no clamp
            else continue;
            Assert.True(r.LastTextBoxSize == r.LastTopBandSize,
                $"{frame}{more}: the band chose {r.LastTopBandSize} but the rest drew at {r.LastTextBoxSize}");
        }
        Assert.True(checkedSizes > 0, "every case was at the floor size");
    });

    // --- Edit details… --------------------------------------------------------------------------------

    [Fact]
    public void CancellingEditDetails_LeavesTheSetSaved_AndNoUndoStep()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow();
            try
            {
                main.SelectedCard = main.Cards[0];
                main.CommitHistory();
                Invoke(main, "MarkSavedPoint");
                Assert.False(main.Dirty);
                var steps = History(main);
                var card = main.SelectedCard!;
                var before = card.Clone();

                // What DetailsWindow does: edits the live card, sits open past the undo timer, and Cancel puts it back.
                var dialog = new Window { Width = 50, Height = 50, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                dialog.Loaded += (_, _) =>
                {
                    card.RulesText += " typed";
                    var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    wait.Tick += (_, _) => { wait.Stop(); card.CopyFrom(before); dialog.Close(); };
                    wait.Start();
                };
                main.ShowCardDialog(dialog);
                main.CommitHistory();   // as OnEditDetails does after the dialog

                Assert.False(main.Dirty, "a cancelled edit left the set marked unsaved");
                Assert.True(History(main) == steps, "a cancelled edit left an undo step");
            }
            finally { main.SuppressClosePrompt = true; main.Close(); }
        });

    // --- import into a themed set ----------------------------------------------------------------------

    [Fact]
    public void ImportingIntoARetitledSet_SkipsTheCardsItAlreadyHas()
    {
        var set = new List<CardModel> { new() { Name = "The Ring", Subtitle = "Sol Ring" }, new() { Name = "Mind Stone" } };
        var incoming = ImportService.Parse("1 Sol Ring\n1 Mind Stone\n1 Arcane Signet", "", "Default");
        var kept = ImportService.RemoveNamesAlreadyIn(set, incoming);
        Assert.Equal(new[] { "Arcane Signet" }, kept.Select(k => k.Card.Name));
        Assert.Equal(2, ImportService.CountNamesAlreadyIn(set, incoming));
    }

    [Fact]
    public void BringingTheSamePictureIntoASetAgain_ReusesItsCopy()
    {
        var root = TempRoot("samepic");
        try
        {
            var sets = Directory.CreateDirectory(Path.Combine(root, "sets")).FullName;
            var art = Directory.CreateDirectory(Path.Combine(root, "art")).FullName;
            var pic = Path.Combine(art, "Sol Ring.png");
            File.WriteAllBytes(pic, TestHelpers.PngBytes(20, 20));
            var other = Path.Combine(art, "Mind Stone.png");
            File.WriteAllBytes(other, TestHelpers.PngBytes(20, 20, 0xFFAA0000));

            for (int run = 0; run < 3; run++)   // three builds, each a fresh set in the same folder
            {
                var cards = new List<CardModel> { new() { Name = "Sol Ring", ArtPath = pic }, new() { Name = "Mind Stone", ArtPath = other } };
                SetFolder.LocalizeImages(cards, sets);
                Assert.StartsWith(Path.Combine(sets, "art"), cards[0].ArtPath);
            }
            Assert.Equal(2, Directory.GetFiles(Path.Combine(sets, "art")).Length);

            File.WriteAllBytes(pic, TestHelpers.PngBytes(20, 20, 0xFF00AA00));   // a new picture under the old name
            SetFolder.LocalizeImages(new List<CardModel> { new() { Name = "Sol Ring", ArtPath = pic } }, sets);
            Assert.Equal(3, Directory.GetFiles(Path.Combine(sets, "art")).Length);
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

    private const string MarvelBolt = """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "msc", "collector_number": "806", "rarity": "uncommon", "set_type": "commander", "promo_types": ["universesbeyond"], "image_uris": { "art_crop": "https://cards.scryfall.io/art_crop/thor.jpg" } }""";
    private const string ClueBolt = """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "clu", "collector_number": "141", "rarity": "uncommon", "set_type": "draft_innovation", "image_uris": { "art_crop": "https://cards.scryfall.io/art_crop/bolt.jpg" } }""";

    [Fact]
    public async Task FillBlanks_OnACustomNumberedCard_GetsARegularPrinting()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            lock (asked) asked.Add(Uri.UnescapeDataString(req.RequestUri!.PathAndQuery));
            return Task.FromResult(
                path.EndsWith("/collection") ? Json("""{ "object": "list", "data": [], "not_found": [ {} ] }""")
                : path.EndsWith("/named") ? Json(MarvelBolt)
                : Json($$"""{ "object": "list", "has_more": false, "data": [ {{ClueBolt}} ] }"""));
        }));
        try
        {
            // The set's own code and the app's numbering, not a deck list's "(SET) 123" hint (Fill blanks).
            var card = new CardModel { Name = "Lightning Bolt", SetCode = "XYZ", CollectorNumber = "001/060" };
            await BatchService.FillFromScryfallAsync(new[] { new ImportedCard(card, NeedsLookup: true) }, downloadArt: false);
            Assert.Contains(asked, a => a.Contains("/cards/search"));   // the regular printing was asked for
            Assert.Equal("XYZ", card.SetCode);   // its own set code stays
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ACardsOwnSetAndNumber_ThatScryfallHas_KeepsThatPrinting()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            lock (asked) asked.Add(Uri.UnescapeDataString(req.RequestUri!.PathAndQuery));
            return Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/collection")
                ? Json($$"""{ "object": "list", "data": [ {{MarvelBolt}} ], "not_found": [] }""")
                : Json($$"""{ "object": "list", "has_more": false, "data": [ {{ClueBolt}} ] }"""));
        }));
        try
        {
            var card = new CardModel { Name = "Lightning Bolt", SetCode = "MSC", CollectorNumber = "806" };   // a CSV's
            await BatchService.FillFromScryfallAsync(new[] { new ImportedCard(card, NeedsLookup: true) }, downloadArt: false);
            Assert.Equal(("MSC", "806"), (card.SetCode, card.CollectorNumber));
            Assert.DoesNotContain(asked, a => a.Contains("/cards/search"));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task AnImportWithNoConnection_StopsTryingAfterTheFirstCard()
    {
        int calls = 0;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("No such host is known.");
        }));
        try
        {
            var cards = ImportService.Parse("1 Sol Ring\n1 Mind Stone\n1 Arcane Signet\n1 Fellwar Stone\n1 Command Tower", "", "Default");
            var report = await BatchService.FillFromScryfallAsync(cards, downloadArt: false);
            Assert.Equal(5, report.NotFound.Count);
            Assert.True(calls <= 2, $"{calls} requests with no connection");   // the batch, then one card
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public void ADeckListPrinting_WithADaggerInItsNumber_IsReadAsAPrinting()
    {
        var card = ImportService.Parse("1 Army of Allah (ARN) 2†", "", "Default").Single().Card;
        Assert.Equal(("Army of Allah", "ARN", "2†"), (card.Name, card.SetCode, card.CollectorNumber));
    }

    // --- command line ---------------------------------------------------------------------------------

    [Fact]
    public void RenderCards_WithAFileItCouldntRead_ExitsWithAnError()
    {
        var root = TempRoot("rendercards");
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "cards")).FullName;
            new CardModel { Name = "Good Card", TypeLine = "Instant" }.Save(Path.Combine(dir, "good.json"));
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            int code = 0;
            TestHelpers.RunSta(() => code = SelfTest.RunRenderCards(dir, Path.Combine(root, "out"), new[] { "nosym" }));
            Assert.Equal(1, code);
            Assert.True(File.Exists(Path.Combine(root, "out", "good-card.png")), "the good card wasn't rendered");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static int History(Cardinator.MainWindow main)
        => (int)typeof(Cardinator.MainWindow).GetField("_histIdx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!;

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
