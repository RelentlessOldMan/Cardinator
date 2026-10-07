using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;
using static Cardinator.Tests.TestHelpers;

namespace Cardinator.Tests;

/// <summary>
/// The second tier of the 2026-10 independent review (1.6.13): each test fails with its fix taken out.
/// In the STA-window collection because two of them swap <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class ReviewTier2Tests
{
    // --- phone photos: EXIF orientation -----------------------------------------------------------

    /// <summary>A JPEG stored 400×200 — left half red, right half blue — tagged with an EXIF orientation.</summary>
    private static byte[] TaggedJpeg(int orientation)
    {
        const int w = 400, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (x < w / 2) px[i + 2] = 255; else px[i] = 255;
            }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
        var md = new BitmapMetadata("jpg");
        md.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(src, null, md, null));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static (byte r, byte g, byte b) At(BitmapSource bmp, int x, int y)
    {
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var p = new byte[4];
        conv.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), p, 4, 0);
        return (p[2], p[1], p[0]);
    }

    private static bool Red((byte r, byte g, byte b) c) => c.r > 180 && c.b < 80;
    private static bool Blue((byte r, byte g, byte b) c) => c.b > 180 && c.r < 80;

    [Theory]
    [InlineData(6, 100, 50, 100, 350)]    // turned clockwise: the stored left side is on top
    [InlineData(8, 100, 350, 100, 50)]    // turned anticlockwise: the stored left side is at the bottom
    public void APhonePhoto_IsTurnedUpright(int orientation, int redX, int redY, int blueX, int blueY)
        => RunSta(() =>
        {
            var bytes = TaggedJpeg(orientation);
            Assert.Equal(orientation, ImageIntake.ReadOrientation(bytes));
            var img = ImageIntake.LoadOriented(bytes);
            Assert.Equal((200, 400), (img.PixelWidth, img.PixelHeight));
            Assert.True(Red(At(img, redX, redY)), $"expected red at {redX},{redY}");
            Assert.True(Blue(At(img, blueX, blueY)), $"expected blue at {blueX},{blueY}");
        });

    [Fact]
    public void APhonePhoto_IsDrawnUprightOnTheCard()
        => RunSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"cardinator-exif-{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(path, TaggedJpeg(6));
            try
            {
                var template = new TemplateService().LoadAll()[0];
                var win = template.Spec.ArtWindow;
                var card = new CardModel { Name = "Sideways", TypeLine = "Instant", TemplateName = template.Name, ArtPath = path };
                var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, template, supersample: 1);
                // Upright, the photo is tall: red on top, blue below. Ignoring the tag, it's red down the left side.
                Assert.True(Red(At(bmp, (int)win.X + 20, (int)win.Y + 20)));
                Assert.True(Blue(At(bmp, (int)win.X + 20, (int)(win.Y + win.H) - 20)), "the art was drawn sideways");
            }
            finally { File.Delete(path); }
        });

    [Fact]
    public void APngWithoutOrientation_IsLeftAsItIs()
        => RunSta(() =>
        {
            var src = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgr32, null, new byte[4 * 2 * 4], 16);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            Assert.Equal(1, ImageIntake.ReadOrientation(ms.ToArray()));
            Assert.Equal((4, 2), (ImageIntake.LoadOriented(ms.ToArray()).PixelWidth, ImageIntake.LoadOriented(ms.ToArray()).PixelHeight));
        });

    // --- exported PNGs print at card size ------------------------------------------------------------

    [Fact]
    public void ExportAll_TagsEachCard_ToPrintAtRealCardSize()
        => RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cardinator-dpi-{Guid.NewGuid():N}");
            try
            {
                var templates = new TemplateService().LoadAll();
                var card = new CardModel { Name = "Printed", TypeLine = "Instant", TemplateName = templates[0].Name };
                var result = BatchService.ExportAll(new[] { card }, templates, dir, new SymbolService());
                Assert.Equal(1, result.Exported);
                var file = Directory.GetFiles(dir, "*.png").Single();
                using var fs = File.OpenRead(file);
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                // 1500 px across a 2.5 in card = 600 DPI (it used to say 96, so it printed over 6x too big).
                Assert.Equal(600, frame.DpiX, 1);
                Assert.Equal(frame.PixelWidth / 2.5, frame.DpiX, 1);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

    // --- Scryfall search keeps a double-faced card whole ---------------------------------------------

    [Fact]
    public void SearchResults_KeepADoubleFacedCardAsOneCard_WithItsBack()
    {
        const string json = """
        { "object": "list", "data": [
          { "name": "Huntmaster of the Fells // Ravager of the Fells", "layout": "transform", "set": "dka",
            "collector_number": "140", "rarity": "mythic",
            "card_faces": [
              { "name": "Huntmaster of the Fells", "mana_cost": "{2}{R}{G}", "type_line": "Creature — Human Werewolf", "oracle_text": "x", "power": "2", "toughness": "2" },
              { "name": "Ravager of the Fells", "mana_cost": "", "type_line": "Creature — Werewolf", "oracle_text": "y", "power": "4", "toughness": "4" }
            ] },
          { "name": "Fire // Ice", "layout": "split", "set": "apc", "collector_number": "128", "rarity": "uncommon",
            "card_faces": [
              { "name": "Fire", "mana_cost": "{1}{R}", "type_line": "Instant", "oracle_text": "a" },
              { "name": "Ice", "mana_cost": "{1}{U}", "type_line": "Instant", "oracle_text": "b" }
            ] },
          { "name": "Lightning Bolt", "layout": "normal", "mana_cost": "{R}", "type_line": "Instant", "oracle_text": "c" }
        ] }
        """;
        var cards = ScryfallMapper.MapSearch(json);
        Assert.Equal(new[] { "Huntmaster of the Fells", "Fire", "Lightning Bolt" }, cards.Select(c => c.Name));
        Assert.Equal("Ravager of the Fells", cards[0].BackFace?.Name);
        Assert.Equal("Ice", cards[1].OtherHalf?.Name);
    }

    // --- one failed collection request keeps the rest ------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    [Fact]
    public async Task ACollectionChunkThatFails_KeepsTheChunksThatWorked()
    {
        int call = 0;
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(_ =>
        {
            int n = Interlocked.Increment(ref call);
            if (n == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var body = $$"""{ "data": [ { "name": "Chunk {{n}}", "type_line": "Instant", "oracle_text": "" } ] }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }));
        try
        {
            var refs = Enumerable.Range(1, 160).Select(i => new ScryfallClient.CardRef($"Card {i}", null, null)).ToList();
            var failures = new List<string>();
            var found = await new ScryfallClient().LookupCollectionAsync(refs, default, failures);
            Assert.Equal(3, call);   // 75 + 75 + 10: the chunk after the failure still ran
            Assert.Equal(new[] { "Chunk 1", "Chunk 3" }, found.Select(f => f[0].Name));
            Assert.Equal("cards 76–150: Scryfall returned 500 Internal Server Error.", Assert.Single(failures));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    // --- rules text wraps around the P/T box on full-art frames --------------------------------------

    [Theory]
    [InlineData("fullart")]
    [InlineData("borderless")]
    public void OnArtForwardFrames_RulesTextWrapsAroundThePtBox(string kind)
    {
        var spec = BuiltInTemplates.All().First(s => kind == "fullart" ? s.FullArt
            : string.Equals(s.FrameStyle, kind, StringComparison.OrdinalIgnoreCase));
        var creature = new CardModel { Name = "Big", TypeLine = "Creature — Beast", Power = "5", Toughness = "5" };
        var avoid = CardRenderer.RulesAvoid(creature, spec);
        Assert.NotNull(avoid);
        var text = spec.EffectiveTextBox;
        Assert.True(avoid!.Value.IntersectsWith(new System.Windows.Rect(text.X, text.Y, text.W, text.H)),
            "the P/T box sits in the text area, so the text must wrap around it");
        Assert.Null(CardRenderer.RulesAvoid(new CardModel { Name = "Spell", TypeLine = "Instant" }, spec));
    }

    // --- the modern frame's text panel reaches the text --------------------------------------------

    [Fact]
    public void ModernFrame_WithTheFooterOnTheBorder_DrawsTheTallerTextPanel()
        => RunSta(() =>
        {
            var spec = BuiltInTemplates.All().First(s => s.FrameStyle == "modern");
            spec.FooterPlacement = "border";
            var tb = spec.TextBox;
            var eff = spec.EffectiveTextBox;
            Assert.True(eff.H > tb.H + 20);   // the text box really grows here
            var bmp = FrameGenerator.RenderToBitmap(spec);
            double k = bmp.PixelWidth / spec.CanvasWidth;   // the frame is rendered supersampled
            int x = (int)(k * (tb.X + tb.W / 2));
            var inPanel = At(bmp, x, (int)(k * (tb.Y + tb.H / 2)));
            var inExtension = At(bmp, x, (int)(k * (tb.Y + tb.H + eff.Y + eff.H) / 2));
            Assert.True(inPanel.r > 180 && inPanel.g > 180 && inPanel.b > 180, $"sampled {inPanel}, not the light text panel");
            int diff = Math.Abs(inPanel.r - inExtension.r) + Math.Abs(inPanel.g - inExtension.g) + Math.Abs(inPanel.b - inExtension.b);
            Assert.True(diff < 60, $"below the old panel was {inExtension}, the panel is {inPanel}: the text there has no panel behind it");
        });

    // --- CSV and deck lists ---------------------------------------------------------------------------

    [Fact]
    public void Csv_AQuotedFieldWithLineBreaks_StaysOneCard()
    {
        var csv = "name,rules\n\"Bolt\",\"Deal 3 damage.\nDraw a card.\"\n\"Shock\",\"Deal 2 damage.\"\n";
        var r = ImportService.ParseWithReport(csv, null, "T");
        Assert.Equal(new[] { "Bolt", "Shock" }, r.Cards.Select(c => c.Card.Name));
        Assert.Equal("Deal 3 damage.\nDraw a card.", r.Cards[0].Card.RulesText);
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void Csv_SemicolonSeparated_IsReadByColumn()
    {
        var r = ImportService.Parse("name;cost;type\nBolt;{R};Instant\n", null, "T");
        var c = Assert.Single(r).Card;
        Assert.Equal(("Bolt", "{R}", "Instant"), (c.Name, c.ManaCost, c.TypeLine));
    }

    [Fact]
    public void Csv_SavedByExcelInWindows1252_KeepsAccentsAndQuotes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cardinator-1252-{Guid.NewGuid():N}.csv");
        // "Café", "It’s — fine" in Windows-1252: é = E9, ’ = 92, — = 97 (none of them valid UTF-8 on their own)
        File.WriteAllBytes(path, new byte[] { (byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)',', (byte)'r', (byte)'u', (byte)'l', (byte)'e', (byte)'s', (byte)'\n',
            (byte)'C', (byte)'a', (byte)'f', 0xE9, (byte)',', (byte)'I', (byte)'t', 0x92, (byte)'s', (byte)' ', 0x97, (byte)' ', (byte)'f', (byte)'i', (byte)'n', (byte)'e' });
        try
        {
            var c = Assert.Single(ImportService.Parse(ImportService.ReadListFile(path), null, "T")).Card;
            Assert.Equal("Café", c.Name);
            Assert.Equal("It’s — fine", c.RulesText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Csv_Utf8WithoutABom_IsStillUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cardinator-utf8-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes("name\nCafé — ok\n"));
        try { Assert.Equal("name\nCafé — ok\n", ImportService.ReadListFile(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Csv_SplitArt_IsFoundBesideTheList_LikeTheMainArt()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "lists");
        var c = Assert.Single(ImportService.Parse("name,art,splitname,splitart\nFire,fire.png,Ice,ice.png\n", baseDir, "T")).Card;
        Assert.Equal(Path.Combine(baseDir, "fire.png"), c.ArtPath);
        Assert.Equal(Path.Combine(baseDir, "ice.png"), c.OtherHalf?.ArtPath);
    }

    [Fact]
    public void DeckList_ALowercaseSetCode_IsAPrintingHint()
    {
        var c = Assert.Single(ImportService.Parse("1 Sol Ring (cmr) 472\n", null, "T")).Card;
        Assert.Equal(("Sol Ring", "CMR", "472"), (c.Name, c.SetCode, c.CollectorNumber));
    }

    [Fact]
    public void DeckList_SectionHeadings_AreNotCards()
    {
        var list = "Creatures (2)\n2 Llanowar Elves\nSideboard:\n1 Duress\nLands (1)\n1 Forest\nMain Deck\n1 Shock\n";
        var r = ImportService.ParseWithReport(list, null, "T");
        Assert.Equal(new[] { "Llanowar Elves", "Llanowar Elves", "Duress", "Forest", "Shock" }, r.Cards.Select(c => c.Card.Name));
    }

    // --- symbols ------------------------------------------------------------------------------------

    [Fact]
    public void SymbolCacheFiles_AreDistinctPerSymbol_AndKeepTheirOldNames()
    {
        Assert.NotEqual(SymbolService.SafeFile("½"), SymbolService.SafeFile("∞"));
        Assert.Equal("R_W", SymbolService.SafeFile("R/W"));   // existing caches stay valid
        Assert.Equal("2_U", SymbolService.SafeFile("2/U"));
        Assert.Equal("T", SymbolService.SafeFile("T"));
    }

    [Fact]
    public void AnHtmlPage_IsNeverTakenForASymbol()
    {
        Assert.False(SymbolService.LooksLikeSvg(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Sign in to Wi-Fi</body></html>")));
        Assert.True(SymbolService.LooksLikeSvg(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
    }

    // --- frame links are size-capped -----------------------------------------------------------------

    [Fact]
    public async Task AFrameLink_ToSomethingHuge_IsRefused()
    {
        // A one-shot local server that claims a 40 MB image (past the 32 MB cap) and then hangs up.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[4096];
            await stream.ReadAsync(buf);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 40000000\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(new byte[1024]);
        });
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => TemplateImporter.CreateFromUrlAsync("", $"http://127.0.0.1:{port}/huge.png"));
            Assert.Contains("too large", ex.Message);
        }
        finally { listener.Stop(); try { await server; } catch { } }
    }

    // --- Frame Design number boxes ------------------------------------------------------------------

    [Theory]
    [InlineData("0,5", 0.5)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 12 ", 12)]
    [InlineData("-3,25", -3.25)]
    public void FrameDesignNumbers_ReadEitherDecimalMark(string typed, double expected)
    {
        Assert.True(Cardinator.FrameDesignWindow.TryParseNumber(typed, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void FrameDesignNumbers_RejectNonsense()
        => Assert.False(Cardinator.FrameDesignWindow.TryParseNumber("abc", out _));

    // --- derived-frame caches are bounded -----------------------------------------------------------

    [Fact]
    public void DerivedFrameCaches_StayBounded_InMemoryAndOnDisk()
        => RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cardinator-variants-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var img = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
                img.Freeze();
                var start = DateTime.UtcNow.AddHours(-1);
                var cache = new Dictionary<string, Template>();
                int total = TemplateService.VariantDiskKeep + 40;
                for (int i = 0; i < total; i++)
                {
                    var file = Path.Combine(dir, $"{i:000}.png");
                    File.WriteAllBytes(file, new byte[] { 1 });
                    File.SetLastWriteTimeUtc(file, start.AddSeconds(i));
                    TemplateService.Remember(cache, "k" + i,
                        new Template { Name = "T", Spec = new TemplateSpec(), FramePath = file, FrameImage = img });
                }
                Assert.True(cache.Count <= TemplateService.VariantCacheMax, $"{cache.Count} frames held in memory");
                Assert.True(Directory.GetFiles(dir).Length <= TemplateService.VariantDiskKeep + TemplateService.VariantCacheMax);
                Assert.All(cache.Values, v => Assert.True(File.Exists(v.FramePath), "a cached frame's file was deleted"));
                Assert.True(File.Exists(Path.Combine(dir, $"{total - 1:000}.png")));   // the newest stays
                Assert.False(File.Exists(Path.Combine(dir, "000.png")));               // the oldest goes
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

    // --- share set ----------------------------------------------------------------------------------

    [Fact]
    public void ShareSet_TakesFramesFromTheirFolders_WithTheirFlipAndSidewaysLayouts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cardinator-share-{Guid.NewGuid():N}");
        try
        {
            var project = Path.Combine(root, "set", "S.cardinator");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "{}");
            // A frame renamed in place to "New Name" still lives in its old folder.
            var frameDir = Path.Combine(root, "templates", "old-name");
            foreach (var sub in new[] { "", "flip", "landscape" })
            {
                Directory.CreateDirectory(Path.Combine(frameDir, sub));
                File.WriteAllText(Path.Combine(frameDir, sub, "template.json"), "{}");
                File.WriteAllBytes(Path.Combine(frameDir, sub, "frame.png"), new byte[] { 1 });
            }
            var dest = Path.Combine(root, "share.zip");
            Assert.Equal(1, SetPackager.ExportSetWithFrames(project, new[] { frameDir }, dest));
            using var zip = ZipFile.OpenRead(dest);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            foreach (var e in new[] { "frames/old-name/template.json", "frames/old-name/frame.png",
                                      "frames/old-name/flip/frame.png", "frames/old-name/landscape/template.json" })
                Assert.Contains(e, entries);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
