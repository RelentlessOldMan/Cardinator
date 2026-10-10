using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.24: a card looked up by name alone came as Scryfall's default printing, which is sometimes a special one —
/// Lightning Bolt's is a Marvel crossover, so it got Thor's art. Now a regular paper printing is used when there is
/// one; a card only ever printed as a crossover keeps it, and a list that names the printing gets that printing. Each
/// test fails with its fix taken out. In the STA-window collection because they swap <see cref="ScryfallClient.TestHttp"/>.
/// </summary>
[Collection("STAWindows")]
public class RegularPrintingTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("""{ "object": "error", "status": 404 }""", Encoding.UTF8, "application/json") };

    private const string MarvelBolt = """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "msc", "collector_number": "806", "rarity": "uncommon", "set_type": "commander", "promo_types": ["universesbeyond"], "image_uris": { "art_crop": "https://cards.scryfall.io/art_crop/thor.jpg" } }""";
    private const string ClueBolt = """{ "object": "card", "name": "Lightning Bolt", "type_line": "Instant", "oracle_text": "Lightning Bolt deals 3 damage to any target.", "set": "clu", "collector_number": "141", "rarity": "uncommon", "set_type": "draft_innovation", "image_uris": { "art_crop": "https://cards.scryfall.io/art_crop/bolt.jpg" } }""";
    private const string Emeritus = """{ "object": "card", "name": "Emeritus of Conflict // Lightning Bolt", "layout": "modal_dfc", "set": "sos", "collector_number": "113", "rarity": "mythic", "set_type": "expansion", "card_faces": [ { "name": "Emeritus of Conflict", "type_line": "Creature" }, { "name": "Lightning Bolt", "type_line": "Instant" } ] }""";

    private static string List(params string[] cards) => $$"""{ "object": "list", "has_more": false, "data": [ {{string.Join(",", cards)}} ] }""";

    [Theory]
    [InlineData("""{ "promo_types": ["universesbeyond"] }""", true)]
    [InlineData("""{ "security_stamp": "triangle" }""", true)]
    [InlineData("""{ "promo": true }""", true)]
    [InlineData("""{ "digital": true }""", true)]
    [InlineData("""{ "set_type": "funny" }""", true)]
    [InlineData("""{ "set_type": "expansion", "promo": false, "security_stamp": "oval", "promo_types": ["boosterfun"] }""", false)]
    public void ASpecialPrinting_IsKnownByScryfallsFlags(string json, bool special)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(special, ScryfallMapper.IsSpecialPrinting(doc.RootElement));
    }

    [Fact]
    public async Task ACrossoverDefault_GivesWayToARegularPrinting_NotACardWithAFaceOfThatName()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            lock (asked) asked.Add(Uri.UnescapeDataString(req.RequestUri!.PathAndQuery));
            return Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/named") ? Json(MarvelBolt) : Json(List(Emeritus, ClueBolt)));
        }));
        try
        {
            var faces = await new ScryfallClient().LookupFacesAsync("lightning bolt");
            Assert.Equal(("Lightning Bolt", "CLU", "141"), (faces[0].Name, faces[0].SetCode, faces[0].CollectorNumber));
            Assert.Contains("bolt.jpg", faces[0].ArtUrl);
            Assert.Contains(asked, a => a.Contains("/cards/search") && a.Contains("-is:ub"));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ACardOnlyEverPrintedAsACrossover_KeepsThatPrinting()
    {
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req => Task.FromResult(
            req.RequestUri!.AbsolutePath.EndsWith("/named") ? Json(MarvelBolt) : NotFound())));
        try
        {
            var faces = await new ScryfallClient().LookupFacesAsync("lightning bolt");
            Assert.Equal("MSC", faces[0].SetCode);
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ADeckListByName_GetsARegularPrinting_ButAListedPrintingIsKept()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(async req =>
        {
            var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
            lock (asked) asked.Add(Uri.UnescapeDataString(req.RequestUri!.PathAndQuery) + " " + body);
            if (req.RequestUri!.AbsolutePath.EndsWith("/collection"))
                return Json($$"""{ "object": "list", "data": [ {{MarvelBolt}} ], "not_found": [] }""");
            return Json(List(ClueBolt));
        }));
        try
        {
            var byName = ImportService.Parse("Lightning Bolt", "", "Default");
            await BatchService.FillFromScryfallAsync(byName, downloadArt: false);
            Assert.Equal(("CLU", "141"), (byName.Single().Card.SetCode, byName.Single().Card.CollectorNumber));

            asked.Clear();
            var listed = ImportService.Parse("Lightning Bolt (MSC) 806", "", "Default");
            await BatchService.FillFromScryfallAsync(listed, downloadArt: false);
            Assert.Equal(("MSC", "806"), (listed.Single().Card.SetCode, listed.Single().Card.CollectorNumber));
            Assert.DoesNotContain(asked, a => a.Contains("/cards/search"));
        }
        finally { ScryfallClient.TestHttp = null; }
    }
}
