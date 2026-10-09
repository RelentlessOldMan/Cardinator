using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.21, from a trial run of the released exe on real deck exports: a set + number that belongs to a different card
/// no longer swaps that card in for the one the list names, and an upgrade refreshes a sample frame only when neither
/// its picture nor its layout was changed. Each test fails with its fix taken out. In the STA-window collection
/// because they swap <see cref="ScryfallClient.TestHttp"/> and re-run the one-per-session frame extraction.
/// </summary>
[Collection("STAWindows")]
public class TrialRunTests
{
    // --- a set + number that's a different card -------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string GlazeFiend = """{ "object": "card", "name": "Glaze Fiend", "type_line": "Artifact Creature — Illusion", "oracle_text": "Flying", "set": "2xm", "collector_number": "95", "rarity": "common", "power": "0", "toughness": "1" }""";
    private const string Arena = """{ "object": "card", "name": "Phyrexian Arena", "type_line": "Enchantment", "oracle_text": "At the beginning of your upkeep, you draw a card and you lose 1 life.", "set": "2x2", "collector_number": "86", "rarity": "rare" }""";

    [Fact]
    public async Task ASetAndNumberThatAreAnotherCard_DontReplaceTheCardTheListNames()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            lock (asked) asked.Add(req.RequestUri!.PathAndQuery);
            return Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/collection")
                ? Json($$"""{ "object": "list", "data": [ {{GlazeFiend}} ], "not_found": [] }""")
                : Json(Arena));
        }));
        try
        {
            var items = ImportService.Parse("\"Count\",\"Name\",\"Edition\",\"Collector Number\"\n\"1\",\"Phyrexian Arena\",\"2xm\",\"95\"", "", "Default");
            await BatchService.FillFromScryfallAsync(items, downloadArt: false);
            var card = items.Single().Card;
            Assert.Equal("Phyrexian Arena", card.Name);
            Assert.Contains("you lose 1 life", card.RulesText);
            Assert.Contains(asked, a => a.Contains("/cards/named"));   // looked up by name instead
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Fact]
    public async Task ASetAndNumberThatAreTheCard_AreStillUsed_WithoutANameLookup()
    {
        var asked = new List<string>();
        ScryfallClient.TestHttp = new HttpClient(new FakeHandler(req =>
        {
            lock (asked) asked.Add(req.RequestUri!.PathAndQuery);
            return Task.FromResult(Json($$"""{ "object": "list", "data": [ {{GlazeFiend}} ], "not_found": [] }"""));
        }));
        try
        {
            var items = ImportService.Parse("name,set,number\nglaze fiend,2xm,95", "", "Default");
            await BatchService.FillFromScryfallAsync(items, downloadArt: false);
            Assert.Equal("Flying", items.Single().Card.RulesText);
            Assert.DoesNotContain(asked, a => a.Contains("/cards/named"));
        }
        finally { ScryfallClient.TestHttp = null; }
    }

    [Theory]
    [InlineData("Lorien Revealed", true)]
    [InlineData("LÓRIEN REVEALED", true)]
    [InlineData("Delver of Secrets", true)]
    [InlineData("Insectile Aberration", true)]
    [InlineData("Delver of Secrets // Insectile Aberration", true)]
    [InlineData("Delver of Secrets//Insectile Aberration", true)]
    [InlineData("Glaze Fiend", false)]
    public void ANameMatches_ItsCardIgnoringCaseAccentsAndPunctuation(string listed, bool matches)
    {
        var lorien = new List<CardModel> { new() { Name = "Lórien Revealed" } };
        var delver = new List<CardModel> { new() { Name = "Delver of Secrets" }, new() { Name = "Insectile Aberration" } };
        Assert.Equal(matches, BatchService.NamesMatch(listed, lorien) || BatchService.NamesMatch(listed, delver));
    }

    // --- sample frames refresh per folder -------------------------------------------------------------------

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static Dictionary<string, string> Manifest(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))! : new();

    [Fact]
    public void AnUpgrade_LeavesASampleFramesPicture_WhenTheUserChangedItsLayout()
    {
        SampleTemplates.EnsureExtracted();
        var dir = Path.Combine(AppPaths.TemplatesDir, "pcc_sealed_gate");
        string png = Path.Combine(dir, "frame.png"), json = Path.Combine(dir, "template.json");
        var manifestPath = Path.Combine(AppPaths.TemplatesDir, SampleTemplates.ManifestName);
        byte[] shippedPng = File.ReadAllBytes(png), shippedJson = File.ReadAllBytes(json);
        var manifestBefore = File.ReadAllText(manifestPath);
        try
        {
            // The picture is as an earlier version wrote it; the layout the user tuned to that picture.
            var oldPng = shippedPng.Concat(new byte[] { 0 }).ToArray();
            File.WriteAllBytes(png, oldPng);
            var m = Manifest(manifestPath);
            m["pcc_sealed_gate/frame.png"] = Hash(oldPng);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(m));
            var mine = Encoding.UTF8.GetBytes("{ \"name\": \"Sealed Gate\", \"note\": \"text box moved to fit the old picture\" }");
            File.WriteAllBytes(json, mine);

            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.Equal(Hash(oldPng), Hash(File.ReadAllBytes(png)));
            Assert.Equal(Hash(mine), Hash(File.ReadAllBytes(json)));

            // A file missing from such a folder still comes back.
            File.Delete(png);
            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.Equal(Hash(shippedPng), Hash(File.ReadAllBytes(png)));
        }
        finally
        {
            File.WriteAllBytes(png, shippedPng);
            File.WriteAllBytes(json, shippedJson);
            File.WriteAllText(manifestPath, manifestBefore);
        }
    }

    [Fact]
    public void AnUpgrade_RefreshesBothFilesOfASampleFrame_WhenNeitherWasChanged()
    {
        SampleTemplates.EnsureExtracted();
        var dir = Path.Combine(AppPaths.TemplatesDir, "pcc_sealed_gate");
        string png = Path.Combine(dir, "frame.png"), json = Path.Combine(dir, "template.json");
        var manifestPath = Path.Combine(AppPaths.TemplatesDir, SampleTemplates.ManifestName);
        byte[] shippedPng = File.ReadAllBytes(png), shippedJson = File.ReadAllBytes(json);
        var manifestBefore = File.ReadAllText(manifestPath);
        try
        {
            var oldPng = shippedPng.Concat(new byte[] { 0 }).ToArray();
            var oldJson = Encoding.UTF8.GetBytes("{ \"name\": \"Sealed Gate (an older version)\" }");
            File.WriteAllBytes(png, oldPng);
            File.WriteAllBytes(json, oldJson);
            var m = Manifest(manifestPath);
            m["pcc_sealed_gate/frame.png"] = Hash(oldPng);
            m["pcc_sealed_gate/template.json"] = Hash(oldJson);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(m));

            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.Equal(Hash(shippedPng), Hash(File.ReadAllBytes(png)));
            Assert.Equal(Hash(shippedJson), Hash(File.ReadAllBytes(json)));
        }
        finally
        {
            File.WriteAllBytes(png, shippedPng);
            File.WriteAllBytes(json, shippedJson);
            File.WriteAllText(manifestPath, manifestBefore);
        }
    }
}
