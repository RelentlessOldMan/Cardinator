using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Covers custom-template import, Scryfall art-URL mapping, and full-art rendering.</summary>
public class CustomTemplateAndArtTests
{
    // --- custom template import ---------------------------------------------

    [Fact]
    public void CreateFromFrame_WritesLoadableFullArtTemplate()
    {
        var name = "Test Frame " + Guid.NewGuid().ToString("N")[..6];
        var created = TemplateImporter.CreateFromFrame(name, SolidPngBytes(8, 8), fullArt: true);
        var dir = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(created));
        try
        {
            Assert.True(File.Exists(Path.Combine(dir, "template.json")));
            Assert.True(File.Exists(Path.Combine(dir, "frame.png")));

            var spec = TemplateSpec.Load(Path.Combine(dir, "template.json"));
            Assert.Equal(name, spec.Name);
            Assert.True(spec.FullArt);
            Assert.True(spec.TitleFont.Shadow);           // full-art fonts get a shadow
            Assert.Equal("#FFFFFF", spec.TitleFont.Color);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CreateFromFrame_EmptyBytes_Throws()
        => Assert.Throws<ArgumentException>(() => TemplateImporter.CreateFromFrame("x", Array.Empty<byte>()));

    // --- Scryfall art URL ---------------------------------------------------

    [Fact]
    public void MapFaces_ReadsArtCropUrl()
    {
        const string json = """
        { "layout":"normal", "name":"Bolt", "type_line":"Instant", "oracle_text":"deal 3",
          "set":"lea", "collector_number":"1", "rarity":"common",
          "image_uris": { "art_crop":"https://example.com/bolt-art.jpg", "normal":"https://x/n.jpg" } }
        """;
        var card = ScryfallMapper.MapFaces(json)[0];
        Assert.Equal("https://example.com/bolt-art.jpg", card.ArtUrl);
    }

    [Fact]
    public void MapFaces_NoImageUris_ArtUrlEmpty()
    {
        const string json = """{ "layout":"normal", "name":"X", "type_line":"Instant" }""";
        Assert.Equal("", ScryfallMapper.MapFaces(json)[0].ArtUrl);
    }

    [Fact]
    public void MapSearch_MapsEachCardInDataArray()
    {
        const string json = """
        { "object":"list", "has_more":false, "data":[
          { "layout":"normal", "name":"Goblin One", "type_line":"Creature — Goblin",
            "image_uris": { "art_crop":"https://x/a.jpg" } },
          { "layout":"normal", "name":"Goblin Two", "type_line":"Creature — Goblin" }
        ]}
        """;
        var cards = ScryfallMapper.MapSearch(json);
        Assert.Equal(2, cards.Count);
        Assert.Equal("Goblin One", cards[0].Name);
        Assert.Equal("https://x/a.jpg", cards[0].ArtUrl);
        Assert.Equal("Goblin Two", cards[1].Name);
    }

    [Fact]
    public void MapSearch_EmptyOrMissingData_ReturnsEmpty()
    {
        Assert.Empty(ScryfallMapper.MapSearch("""{ "object":"list", "data":[] }"""));
        Assert.Empty(ScryfallMapper.MapSearch("""{ "object":"list" }"""));
    }

    [Fact]
    public void ArtUrl_IsNotPersistedInProjectJson()
    {
        var card = new CardModel { Name = "Z", ArtUrl = "https://example.com/secret.jpg" };
        var json = System.Text.Json.JsonSerializer.Serialize(card);
        Assert.DoesNotContain("secret.jpg", json);   // [JsonIgnore] keeps transient art URLs out
    }

    // --- full-art rendering -------------------------------------------------

    [Fact]
    public void FullArtTemplate_ArtBleedsToEdges_UnlikeFramed()
        => RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var full = templates.FirstOrDefault(t => t.Spec.FullArt);
            // A genuinely windowed frame — not full-art, and not the full-bleed styles (borderless/overlay).
            var framed = templates.FirstOrDefault(t => !t.Spec.FullArt
                && !string.Equals(t.Spec.FrameStyle, "borderless", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t.Spec.FrameStyle, "overlay", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(full);
            Assert.NotNull(framed);

            var artPath = SolidPngFile(Color.FromRgb(255, 0, 255), 900, 700);
            try
            {
                var card = new CardModel { Name = "Edge", ArtPath = artPath };
                var renderer = new CardRenderer(new SymbolService());

                // A point near the left edge, below the title: art on full-art, frame on framed.
                var fullBmp = renderer.RenderToBitmap(card, full!, 1);
                var framedBmp = renderer.RenderToBitmap(card, framed!, 1);

                Assert.True(IsMagenta(Sample(fullBmp, 30, 520)), "full-art should bleed art to the edge");
                Assert.False(IsMagenta(Sample(framedBmp, 30, 520)), "framed art should be inside its window only");
            }
            finally { File.Delete(artPath); }
        });

    [Fact]
    public void ShadowFonts_RenderWithoutThrowing()
        => RunSta(() =>
        {
            var full = new TemplateService().LoadAll().First(t => t.Spec.FullArt);
            var card = new CardModel
            {
                Name = "Shadowed Title", ManaCost = "{1}{U}", TypeLine = "Creature — Test",
                RulesText = "Flying\n{T}: Draw.", FlavorText = "On the art.", Power = "1", Toughness = "1",
            };
            var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, full, 1);
            Assert.True(HasContent(bmp));
        });

    // --- helpers ------------------------------------------------------------

    private static byte[] SolidPngBytes(int w, int h)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 80; px[i + 2] = 160; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string SolidPngFile(Color c, int w, int h)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var path = Path.Combine(Path.GetTempPath(), $"art-{Guid.NewGuid():N}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static (int r, int g, int b, int a) Sample(BitmapSource bmp, int x, int y)
    {
        var one = new CroppedBitmap(bmp, new Int32Rect(x, y, 1, 1));
        var px = new byte[4];
        one.CopyPixels(px, 4, 0);
        return (px[2], px[1], px[0], px[3]);
    }

    private static bool IsMagenta((int r, int g, int b, int a) p) => p.r > 200 && p.b > 200 && p.g < 90 && p.a > 200;

    private static bool HasContent(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 10 && (pixels[i + 2] < 200 || pixels[i + 1] < 200 || pixels[i] < 200)) return true;
        return false;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
