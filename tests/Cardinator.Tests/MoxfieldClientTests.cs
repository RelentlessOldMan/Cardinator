using System.Linq;
using Cardinator.Services;

namespace Cardinator.Tests;

public class MoxfieldClientTests
{
    [Theory]
    [InlineData("https://moxfield.com/decks/Wq2LzlPelEOT9a541Et1XQ", "Wq2LzlPelEOT9a541Et1XQ")]
    [InlineData("http://www.moxfield.com/decks/abc-123_XYZ", "abc-123_XYZ")]
    [InlineData("moxfield.com/decks/abc/primer", "abc")]
    [InlineData("https://example.com/decks/nope", null)]
    [InlineData("just some text", null)]
    public void ExtractDeckId_PullsIdFromUrl(string url, string? expected)
        => Assert.Equal(expected, MoxfieldClient.ExtractDeckId(url));

    [Theory]
    [InlineData("https://moxfield.com/decks/abc", true)]
    [InlineData("https://archidekt.com/decks/123", false)]
    [InlineData("Lightning Bolt", false)]
    public void IsMoxfieldUrl_Detects(string text, bool expected)
        => Assert.Equal(expected, MoxfieldClient.IsMoxfieldUrl(text));

    [Fact]
    public void ParseDeck_V3_ReadsBoardsQuantitiesAndPrintings_ExcludesMaybeboard()
    {
        var json = """
        {
          "name": "My Deck",
          "boards": {
            "mainboard": { "count": 3, "cards": {
              "a": { "quantity": 1, "card": { "name": "Sol Ring", "set": "ltc", "cn": "273" } },
              "b": { "quantity": 2, "card": { "name": "Forest", "set": "unf", "cn": "235" } }
            }},
            "commanders": { "count": 1, "cards": {
              "c": { "quantity": 1, "card": { "name": "Atraxa, Praetors' Voice", "set": "2xm", "cn": "197" } }
            }},
            "maybeboard": { "count": 1, "cards": {
              "d": { "quantity": 1, "card": { "name": "Should Not Import", "set": "xxx", "cn": "1" } }
            }}
          }
        }
        """;

        var (name, cards) = MoxfieldClient.ParseDeck(json, "T");

        Assert.Equal("My Deck", name);
        Assert.Equal(4, cards.Count);                                        // 1 + 2 + 1, maybeboard excluded
        Assert.DoesNotContain(cards, c => c.Card.Name == "Should Not Import");
        Assert.Equal(2, cards.Count(c => c.Card.Name == "Forest"));

        var sol = cards.First(c => c.Card.Name == "Sol Ring");
        Assert.Equal("LTC", sol.Card.SetCode);                              // uppercased
        Assert.Equal("273", sol.Card.CollectorNumber);
        Assert.Equal("T", sol.Card.TemplateName);
        Assert.True(sol.NeedsLookup);
    }

    [Fact]
    public void ParseDeck_V2_FlatBoards_AreRead()
    {
        var json = """
        {
          "name": "V2 Deck",
          "mainboard": { "Sol Ring": { "quantity": 1, "card": { "name": "Sol Ring", "set": "ltc", "cn": "273" } } },
          "commanders": { "Atraxa": { "quantity": 1, "card": { "name": "Atraxa, Praetors' Voice", "set": "2xm", "cn": "197" } } }
        }
        """;

        var (name, cards) = MoxfieldClient.ParseDeck(json, "T");

        Assert.Equal("V2 Deck", name);
        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, c => c.Card.Name == "Sol Ring");
        Assert.Contains(cards, c => c.Card.Name == "Atraxa, Praetors' Voice");
    }

    [Fact]
    public void ParseDeck_EmptyDeck_ReturnsNoCards()
    {
        var (_, cards) = MoxfieldClient.ParseDeck("""{ "name": "Empty", "boards": {} }""", "T");
        Assert.Empty(cards);
    }

    [Fact]
    public void ApiUrl_UsesV3AllEndpoint()
        => Assert.Equal("https://api2.moxfield.com/v3/decks/all/abc", MoxfieldClient.ApiUrl("abc"));

    [Fact]
    public void ToDeckList_ProducesParserFriendlyText_CommanderFirst()
    {
        var json = """
        {
          "name": "My Deck",
          "boards": {
            "mainboard": { "cards": {
              "a": { "quantity": 1, "card": { "name": "Sol Ring", "set": "ltc", "cn": "273" } },
              "b": { "quantity": 2, "card": { "name": "Forest", "set": "unf", "cn": "235" } }
            }},
            "commanders": { "cards": {
              "c": { "quantity": 1, "card": { "name": "Atraxa, Praetors' Voice", "set": "2xm", "cn": "197" } }
            }}
          }
        }
        """;

        var text = MoxfieldClient.ToDeckList(json);
        var lines = text.Replace("\r", "").Split('\n');

        Assert.Equal("# My Deck", lines[0]);                                  // deck name as a comment
        Assert.Contains("1 Atraxa, Praetors' Voice (2XM) 197", text);         // commander first, set uppercased
        Assert.Contains("1 Sol Ring (LTC) 273", text);
        Assert.Contains("2 Forest (UNF) 235", text);                          // quantity preserved
        Assert.True(Array.IndexOf(lines, "1 Atraxa, Praetors' Voice (2XM) 197")
                    < Array.IndexOf(lines, "1 Sol Ring (LTC) 273"));          // commander appears before mainboard

        // Round-trips through the shared importer.
        var cards = ImportService.Parse(text, null, "T");
        Assert.Equal(4, cards.Count);                                         // 1 + 1 + 2
        Assert.Equal(2, cards.Count(c => c.Card.Name == "Forest"));
    }
}
