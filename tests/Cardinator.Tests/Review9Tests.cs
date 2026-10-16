using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The eighth review's second half (1.7.5): the screens a new user meets. Look up on the starter card leaving its
/// 5/5, art and flavor on Lightning Bolt; a "blank" card that wasn't blank; Save starting in the app's samples
/// folder and scattering a set's art through Documents; a set symbol hidden until the card had a rarity, and "Rare"
/// drawn as a common one; the EXPORT buttons out of sight on a laptop; an empty set with no word on where to start;
/// warnings about a frame put as if the card were wrong. Each test fails with its fix taken out. In the STA-window
/// collection because most build windows or swap <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class Review9Tests
{
    private static string TempRoot(string tag) => Path.Combine(Path.GetTempPath(), $"cardinator-{tag}-{Guid.NewGuid():N}");

    // --- 11: one lookup, the same everywhere --------------------------------------------------------------

    private static string BoltJson(int artPort) => $$"""
        { "object": "card", "name": "Lightning Bolt", "layout": "normal", "mana_cost": "{R}", "type_line": "Instant",
          "oracle_text": "Lightning Bolt deals 3 damage to any target.", "rarity": "common", "set": "lea",
          "collector_number": "161", "image_uris": { "art_crop": "http://127.0.0.1:{{artPort}}/bolt.png" } }
        """;

    private static void LookUp(Cardinator.MainWindow main, CardModel card, int artPort)
    {
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BoltJson(artPort), Encoding.UTF8, "application/json"),
        })));
        main.Cards.Add(card);
        main.SelectedCard = card;
        Invoke(main, "OnLookup", null, null);
        PumpUntil(() => !main.Busy);
    }

    [Fact]
    public void LookUp_OnTheStarterCard_MakesTheRealCard_WithItsArt()
        => OnAppThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var art = new OneShotServer("image/png", TestHelpers.PngBytes(16, 12, 0xFFCC2200));
            try
            {
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                main.Cards.Clear();
                var starter = SampleCards.All(main.Templates[0].Name)[0];
                var sampleArt = starter.ArtPath;
                Assert.True(SampleAssets.IsBundled(sampleArt));
                starter.Name = "Lightning Bolt";
                LookUp(main, starter, art.Port);

                Assert.Equal(("Lightning Bolt", "{R}", "Instant"), (starter.Name, starter.ManaCost, starter.TypeLine));
                Assert.Equal(("", ""), (starter.Power, starter.Toughness));   // the angel's 5/5 is gone
                Assert.Equal("", starter.FlavorText);                         // so is her flavor text
                Assert.Equal("", starter.Artist);
                Assert.Equal("C", starter.Rarity);
                Assert.NotEqual(sampleArt, starter.ArtPath);                   // Bolt's art, not the angel
                Assert.False(SampleAssets.IsBundled(starter.ArtPath));
            }
            finally { ScryfallClient.TestHttp = null; }
        });

    [Fact]
    public void LookUp_KeepsArtFlavorAndFrame_ThatAreTheUsersOwn()
        => OnAppThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var root = TempRoot("ownart");
            Directory.CreateDirectory(root);
            using var art = new OneShotServer("image/png", TestHelpers.PngBytes(16, 12, 0xFFCC2200));
            try
            {
                var mine = Path.Combine(root, "my-bolt.png");
                File.WriteAllBytes(mine, TestHelpers.PngBytes(20, 20, 0xFF00AA00));
                var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
                main.Cards.Clear();
                var card = new CardModel
                {
                    Name = "bolt", Power = "9", Toughness = "9", FlavorText = "Mine.", Artist = "Me", ArtPath = mine,
                    TemplateName = main.Templates[1].Name,
                };
                LookUp(main, card, art.Port);

                Assert.Equal(("Lightning Bolt", "", ""), (card.Name, card.Power, card.Toughness));
                Assert.Equal(mine, card.ArtPath);
                Assert.Equal(("Mine.", "Me"), (card.FlavorText, card.Artist));
                Assert.Equal(main.Templates[1].Name, card.TemplateName);
            }
            finally { ScryfallClient.TestHttp = null; try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void IsBundled_IsTheStarterPicture_NotJustItsName()
    {
        SampleCards.All("x");   // extracts the sample art
        var sample = SampleAssets.Path("seraph-angel.png");
        Assert.True(SampleAssets.IsBundled(sample));
        var root = TempRoot("bundled");
        Directory.CreateDirectory(root);
        try
        {
            var copy = Path.Combine(root, "seraph-angel.png");   // a saved set's art\ copy is still the sample
            File.Copy(sample, copy);
            Assert.True(SampleAssets.IsBundled(copy));
            var other = Path.Combine(root, "sub", "seraph-angel.png");   // the user's own picture under that name isn't
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllBytes(other, TestHelpers.PngBytes(10, 10));
            Assert.False(SampleAssets.IsBundled(other));
            Assert.False(SampleAssets.IsBundled(Path.Combine(root, "missing.png")));
            Assert.False(SampleAssets.IsBundled(""));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void EditDetails_OnTheStarterCard_UsesTheLookedUpArt_ButNotOnTheUsersOwn()
        => OnAppThread(() =>
        {
            var starter = SampleCards.All("x")[0];
            WithDetails(starter, w => Assert.True(w.UseArtBox.IsChecked));
            var root = TempRoot("detailsart");
            Directory.CreateDirectory(root);
            try
            {
                var mine = Path.Combine(root, "mine.png");
                File.WriteAllBytes(mine, TestHelpers.PngBytes(10, 10));
                WithDetails(new CardModel { Name = "Mine", ArtPath = mine }, w => Assert.False(w.UseArtBox.IsChecked));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void StarterLeftovers_GoOnlyWhenTheyAreTheStarters()
    {
        var starter = SampleCards.All("x")[0];
        SampleCards.ClearStarterLeftovers(starter);
        Assert.Equal(("", ""), (starter.FlavorText, starter.Artist));
        var mine = new CardModel { FlavorText = "Where she passes.", Artist = "Cardinator" };
        SampleCards.ClearStarterLeftovers(mine);
        Assert.Equal(("Where she passes.", "Cardinator"), (mine.FlavorText, mine.Artist));
    }

    // --- 13: a blank card is blank ------------------------------------------------------------------------

    [Fact]
    public void ABlankCard_HasNothingForFillInBlanksToSkip()
    {
        var blank = SampleCards.Blank("Gold Multicolor");
        Assert.Equal("New Card", blank.Name);
        Assert.Equal(("", "", ""), (blank.ManaCost, blank.TypeLine, blank.RulesText));
        Assert.Equal(("", ""), (blank.Power, blank.Toughness));
        Assert.Equal("Gold Multicolor", blank.TemplateName);
    }

    // --- 14: the card's text on the main screen, a name in Edit details -----------------------------------

    [Fact]
    public void TheMainScreen_EditsTypeCostAndRules_AndEditDetailsTheName()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            main.Cards.Clear();
            var card = new CardModel { Name = "Mine", TemplateName = main.Templates[0].Name };
            main.Cards.Add(card);
            main.SelectedCard = card;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            main.TypeLineBox.Text = "Creature â€” Elf";
            main.ManaCostMainBox.Text = "{G}";
            main.RulesMainBox.Text = "{T}: Add {G}.";
            Assert.Equal(("Creature â€” Elf", "{G}", "{T}: Add {G}."), (card.TypeLine, card.ManaCost, card.RulesText));

            WithDetails(card, w =>
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                w.NameBox.Text = "Llanowar Mine";
                Assert.Equal("Llanowar Mine", card.Name);
            });
        });

    /// <summary>Opens Edit details on <paramref name="card"/>, runs <paramref name="test"/> and closes it again keeping
    /// the edits â€” also when an assert fails, so a failing test can't leave a window asking "keep your changes?"
    /// with no one to answer.</summary>
    private static void WithDetails(CardModel card, Action<DetailsWindow> test)
    {
        ConfirmDialog.TestAnswer = _ => ConfirmResult.Affirmative;
        var w = new DetailsWindow(card);
        try
        {
            TestHelpers.ShowOffScreen(w);
            test(w);
        }
        finally
        {
            try { w.Close(); } catch { }
            ConfirmDialog.TestAnswer = null;
        }
    }

    [Fact]
    public void EditDetails_RarityPicker_WritesTheLetter_AndLeavesAnOddRarityAlone()
        => OnAppThread(() =>
        {
            var card = new CardModel { Name = "R", Rarity = "rare" };
            WithDetails(card, w =>
            {
                Assert.Equal("Rare", ((ComboBoxItem)w.RarityBox.SelectedItem).Content);
                Assert.Equal("rare", card.Rarity);   // showing it doesn't rewrite it
                w.RarityBox.SelectedIndex = 4;
                Assert.Equal("M", card.Rarity);
                card.Rarity = "U";                    // a Scryfall search sets it: the picker follows
                Assert.Equal("Uncommon", ((ComboBoxItem)w.RarityBox.SelectedItem).Content);
                card.Rarity = "mythic";               // a word from elsewhere: shown as Mythic, not rewritten to "M"
                Assert.Equal("Mythic", ((ComboBoxItem)w.RarityBox.SelectedItem).Content);
                Assert.Equal("mythic", card.Rarity);
            });

            var odd = new CardModel { Name = "Odd", Rarity = "Epic" };
            WithDetails(odd, w =>
            {
                Assert.Equal("Epic", ((ComboBoxItem)w.RarityBox.SelectedItem).Content);
                Assert.Equal("Epic", odd.Rarity);
            });
        });

    // --- 15: Save puts a set in a folder of its own -------------------------------------------------------

    [Fact]
    public void Save_IntoAFolderWithOtherThings_MakesTheSetItsOwnFolder()
    {
        var root = TempRoot("ownfolder");
        Directory.CreateDirectory(root);
        try
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            Assert.Equal(Path.Combine(empty, "Elves.cardinator"), SetFolder.OwnFolderPath(Path.Combine(empty, "Elves.cardinator")));

            var busy = Path.Combine(root, "Documents");
            Directory.CreateDirectory(busy);
            File.WriteAllText(Path.Combine(busy, "letter.txt"), "hi");
            Assert.Equal(Path.Combine(busy, "Elves", "Elves.cardinator"), SetFolder.OwnFolderPath(Path.Combine(busy, "Elves.cardinator")));

            var named = Path.Combine(root, "elves");   // already the set's folder (any case)
            Directory.CreateDirectory(named);
            File.WriteAllText(Path.Combine(named, "notes.txt"), "x");
            Assert.Equal(Path.Combine(named, "Elves.cardinator"), SetFolder.OwnFolderPath(Path.Combine(named, "Elves.cardinator")));

            var existing = Path.Combine(busy, "Old.cardinator");   // replacing a set that's there: stays put
            File.WriteAllText(existing, "{}");
            Assert.Equal(existing, SetFolder.OwnFolderPath(existing));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ANewSet_IsOfferedTheSetsFolder_NotTheSamplesFolder()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var folder = (string)main.GetType().GetMethod("SaveDialogFolder", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(main, null)!;
            Assert.NotEqual(Path.GetFullPath(AppPaths.SamplesDir), Path.GetFullPath(folder));
            Assert.Equal("Cardinator Sets", Path.GetFileName(folder));
        });

    // --- 16: the set symbol and rarity words --------------------------------------------------------------

    [Theory]
    [InlineData("R", "R")]
    [InlineData("r", "R")]
    [InlineData("Rare", "R")]
    [InlineData("mythic rare", "M")]
    [InlineData(" Uncommon ", "U")]
    [InlineData("common", "C")]
    [InlineData("Epic", "EPIC")]
    [InlineData("x", "X")]
    public void RarityWords_BecomeTheirLetter(string typed, string code)
        => Assert.Equal(code, CardRenderer.RarityCode(typed));

    [Fact]
    public void TheFooter_PrintsTheLetter_NotTheWord()
    {
        var line = CardRenderer.BuildCollectorLine(new CardModel { CollectorNumber = "7", Rarity = "Rare", SetCode = "ABC" });
        Assert.Contains(" R ", " " + line + " ");
        Assert.DoesNotContain("RARE", line);
    }

    [Fact]
    public void ASetsOwnSymbol_ShowsWithoutARarity()
        => TestHelpers.RunSta(() =>
        {
            var root = TempRoot("symnorarity");
            Directory.CreateDirectory(root);
            try
            {
                var sym = Path.Combine(root, "sym.png");
                File.WriteAllBytes(sym, TestHelpers.PngBytes(60, 60, 0xFFFF0000));   // pure red
                var template = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
                var card = new CardModel { Name = "Sym", TypeLine = "Instant", SetSymbolPath = sym };
                var src = new CardRenderer(new SymbolService()).RenderToBitmap(card, template);
                var bmp = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                var px = TestHelpers.Pixels(bmp);
                var bar = template.Spec.TypeBar;
                int red = 0;
                for (int y = (int)bar.Y; y < bar.Y + bar.H; y++)
                    for (int x = (int)(bar.X + bar.W / 2); x < bar.X + bar.W; x++)
                    {
                        int i = (y * bmp.PixelWidth + x) * 4;
                        if (px[i + 2] > 230 && px[i + 1] < 30 && px[i] < 30) red++;
                    }
                Assert.True(red > 100, $"the set symbol wasn't drawn ({red} red pixels)");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

    [Fact]
    public void ARarityTheSymbolCantColour_IsNoted()
    {
        var template = BuiltInTemplates.All().First();
        Assert.Contains(CardValidator.Validate(new CardModel { Name = "E", TypeLine = "Instant", Rarity = "Epic" }, template),
            i => i.Code == "rarity-unknown");
        foreach (var ok in new[] { "", "R", "rare", "Mythic" })
            Assert.DoesNotContain(CardValidator.Validate(new CardModel { Name = "E", TypeLine = "Instant", Rarity = ok }, template),
                i => i.Code == "rarity-unknown");
    }

    // --- 17: laptop screens -------------------------------------------------------------------------------

    [Theory]
    [InlineData(1366, 697)]
    [InlineData(1280, 649)]
    public void OnALaptopScreen_EverySidebarButtonShows_AndTheEditorDoesntScroll(int width, int height)
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var root = (FrameworkElement)main.Content;
            var size = new Size(width, height);
            for (int pass = 0; pass < 3; pass++)   // the sidebar caps its buttons' height once it knows its own
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                main.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            }
            Assert.True(main.BatchScroll.ScrollableHeight < 1, $"the sidebar buttons scroll by {main.BatchScroll.ScrollableHeight:F0}px");
            Assert.True(main.CardList.ActualHeight > 100, $"the card list is only {main.CardList.ActualHeight:F0}px");
            Assert.True(main.EditorScroll.ScrollableHeight < 1, $"the editing column scrolls by {main.EditorScroll.ScrollableHeight:F0}px");
        });

    // --- 18: an empty set, frame faults, Save as ----------------------------------------------------------

    [Fact]
    public void AnEmptySet_SaysWhereToStart()
        => OnAppThread(() =>
        {
            var main = new Cardinator.MainWindow { SuppressClosePrompt = true };
            var seen = new List<string?>();
            main.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
            main.Cards.Clear();
            Assert.True(main.IsEmptySet);
            Assert.Contains(nameof(main.IsEmptySet), seen);
            main.Cards.Add(SampleCards.Blank(main.Templates[0].Name));
            Assert.False(main.IsEmptySet);
        });

    [Fact]
    public void FrameFaults_SayItsTheFrame_AndWhatToDo()
    {
        var spec = BuiltInTemplates.All().First();
        spec.TitleBar!.X = -40;   // the title box runs off the card
        var issues = CardValidator.Validate(new CardModel { Name = "X", TypeLine = "Instant" }, spec);
        var oob = Assert.Single(issues, i => i.Code == "out-of-bounds");
        Assert.Contains("frame", oob.Message);
        Assert.Contains("Frame Design", oob.Message);
    }

    [Fact]
    public void SaveAs_HasAButtonAndCtrlShiftS()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cardinator", "MainWindow.xaml"));
        Assert.Contains("Click=\"OnSaveProjectAs\"", xaml);
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cardinator", "MainWindow.xaml.cs"));
        Assert.Matches(@"Key\.S\)\s*\{\s*OnSaveProjectAs", code);
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Cardinator", "Cardinator.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    /// <summary>A tiny HTTP server that answers one request with the given type and bytes.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public OneShotServer(string contentType, byte[] body)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    var req = new StringBuilder();
                    while (!req.ToString().Contains("\r\n\r\n"))
                    {
                        int n = await stream.ReadAsync(buf);
                        if (n == 0) break;
                        req.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                }
                catch { /* the test failed elsewhere */ }
            });
        }
        public void Dispose() => _listener.Stop();
    }

    private static void PumpUntil(Func<bool> done, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(done(), "timed out");
    }

    private static void Invoke(object target, string method, params object?[] args)
        => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
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
