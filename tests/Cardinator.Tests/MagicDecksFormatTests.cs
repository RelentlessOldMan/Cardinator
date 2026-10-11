using System.IO;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.32: the deck format shared with the MagicDecks and MagicSimulator projects (their CONTRACT.md §1) —
/// <c>--moxfield … format=magicdecks</c> writes it, and Cardinator's list import reads it.
/// </summary>
public class MagicDecksFormatTests
{
    private const string Partners = """
    {
      "name": "For Testing",
      "boards": {
        "commanders": { "count": 2, "cards": {
          "r": { "quantity": 1, "card": { "name": "Rograkh, Son of Rohgahh", "set": "cmr", "cn": "197" } },
          "s": { "quantity": 1, "card": { "name": "Silas Renn, Seeker Adept", "set": "c16", "cn": "47" } }
        }},
        "mainboard": { "count": 6, "cards": {
          "a": { "quantity": 1, "card": { "name": "Demonic Consultation", "set": "ice", "cn": "117" } },
          "b": { "quantity": 1, "card": { "name": "Island", "set": "unf", "cn": "236" } },
          "c": { "quantity": 1, "card": { "name": "Island", "set": "one", "cn": "263" } },
          "d": { "quantity": 1, "card": { "name": "Lim-Dûl's Vault", "set": "all", "cn": "106" } },
          "e": { "quantity": 2, "card": { "name": "Expansion // Explosion", "set": "grn", "cn": "224" } }
        }},
        "companions": { "count": 1, "cards": {
          "k": { "quantity": 1, "card": { "name": "Lurrus of the Dream-Den", "set": "iko", "cn": "226" } }
        }},
        "sideboard": { "count": 1, "cards": {
          "x": { "quantity": 1, "card": { "name": "Not In The Deck", "set": "xxx", "cn": "1" } }
        }},
        "signatureSpells": { "count": 0, "cards": {} }
      }
    }
    """;

    [Fact]
    public void MoxfieldDeck_WritesTheContractFormat_Exactly()
    {
        var text = MoxfieldClient.ToMagicDecksList(Partners);
        Assert.Equal(
            "1 Rograkh, Son of Rohgahh *CMDR*\n" +
            "1 Silas Renn, Seeker Adept *CMDR*\n" +
            "\n" +
            "1 Demonic Consultation\n" +
            "2 Island\n" +                       // two printings merged into one line
            "1 Lim-Dûl's Vault\n" +              // accents kept
            "2 Expansion // Explosion\n" +       // the full two-part name
            "# companion: Lurrus of the Dream-Den\n",
            text);                               // no set codes, no sideboard, LF and a final newline
    }

    [Fact]
    public void ADeckWithNoCommander_HasNoTagAndNoBlankLine()
    {
        var json = """
        { "name": "Sixty", "boards": { "mainboard": { "cards": {
          "a": { "quantity": 4, "card": { "name": "Lightning Bolt", "set": "m10", "cn": "146" } } } } } }
        """;
        Assert.Equal("4 Lightning Bolt\n", MoxfieldClient.ToMagicDecksList(json));
    }

    [Fact]
    public void TheContractFormat_ImportsIntoCardinator()
    {
        var list = MoxfieldClient.ToMagicDecksList(Partners).Replace("\n", "\r\n");   // CRLF too
        var result = ImportService.ParseWithReport(list, "", "Default");
        Assert.Equal(0, result.Skipped);
        var names = result.Cards.Select(c => c.Card.Name).ToList();
        Assert.Equal("Rograkh, Son of Rohgahh", names[0]);                  // the tag is gone, commanders first
        Assert.Equal("Silas Renn, Seeker Adept", names[1]);
        Assert.Equal(2, names.Count(n => n == "Island"));
        Assert.DoesNotContain(names, n => n.Contains('*') || n.Contains("companion"));
        Assert.Equal(8, names.Count);
    }

    [Theory]
    [InlineData("1 Sol Ring (LTC) 273 *F*", "Sol Ring", "LTC")]
    [InlineData("1 Thrasios, Triton Hero *cmdr*", "Thrasios, Triton Hero", "")]
    [InlineData("1 Tymna the Weaver (C16) 48 *CMDR*", "Tymna the Weaver", "C16")]
    public void TrailingMarkers_AreNotPartOfTheName(string line, string name, string set)
    {
        var card = ImportService.Parse(line, "", "Default").Single().Card;
        Assert.Equal((name, set), (card.Name, card.SetCode));
    }

    [Fact]
    public void Moxfield_FormatOption_IsChecked()
    {
        Assert.Null(CliArgs.Problem(new[] { "--moxfield", "abc", "out.txt", "format=magicdecks" }));
        Assert.Equal(2, SetCommands.RunMoxfield("abc", "out.txt", new[] { "format=arena" }));
        Assert.Equal(2, SetCommands.RunMoxfield("abc", Path.Combine(Path.GetTempPath(), "x.json"), new[] { "format=magicdecks" }));
    }
}
