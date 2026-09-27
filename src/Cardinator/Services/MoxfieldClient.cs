using System.Text.Json;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Pure helpers for Moxfield deck import: recognizing a deck URL, extracting its id, and mapping the
/// deck JSON into cards. The actual network fetch lives in <see cref="MoxfieldFetcher"/> (it needs a
/// real browser engine to get past Cloudflare); keeping the parsing here makes it unit-testable.
/// </summary>
public static class MoxfieldClient
{
    private static readonly Regex DeckIdRe =
        new(@"moxfield\.com/decks/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The boards we treat as "the deck" for card-making (excludes maybeboard/tokens/etc.).</summary>
    private static readonly HashSet<string> WantedBoards =
        new(StringComparer.OrdinalIgnoreCase) { "mainboard", "commanders", "companions", "sideboard", "signatureSpells" };

    public static bool IsMoxfieldUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DeckIdRe.IsMatch(text);

    /// <summary>Pulls the public deck id out of a Moxfield deck URL, or null if there isn't one.</summary>
    public static string? ExtractDeckId(string? text)
    {
        var m = DeckIdRe.Match(text ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The Moxfield API endpoint that returns a deck's full contents as JSON.</summary>
    public static string ApiUrl(string deckId) => $"https://api2.moxfield.com/v3/decks/all/{deckId}";

    /// <summary>
    /// Maps a Moxfield deck JSON document into importable cards. Handles both the v3 shape
    /// (<c>boards.{mainboard,commanders,…}.cards</c>) and the older flat v2 shape
    /// (<c>{mainboard,commanders,…}</c>). Each card keeps its name + set/collector hint so the
    /// existing Scryfall fill can resolve exact printings and art.
    /// </summary>
    public static (string name, List<ImportedCard> cards) ParseDeck(string json, string defaultTemplate)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var cards = new List<ImportedCard>();

        if (root.TryGetProperty("boards", out var boards) && boards.ValueKind == JsonValueKind.Object)
        {
            // v3: boards is an object of named boards, each with a "cards" object.
            foreach (var board in boards.EnumerateObject())
                if (WantedBoards.Contains(board.Name))
                    AddBoard(board.Value, defaultTemplate, cards);
        }
        else
        {
            // v2: boards are top-level properties.
            foreach (var boardName in WantedBoards)
                if (root.TryGetProperty(boardName, out var board))
                    AddBoard(board, defaultTemplate, cards);
        }

        return (name, cards);
    }

    private static void AddBoard(JsonElement board, string defaultTemplate, List<ImportedCard> into)
    {
        // A board is either { "cards": { <id>: {quantity, card} } } (v3) or { <name>: {quantity, card} } (v2).
        var entries = board.ValueKind == JsonValueKind.Object
                      && board.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Object
            ? c : board;
        if (entries.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in entries.EnumerateObject())
        {
            var item = entry.Value;
            if (item.ValueKind != JsonValueKind.Object) continue;

            int qty = item.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 1;
            if (!item.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.Object) continue;

            var cardName = card.TryGetProperty("name", out var cn) ? (cn.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(cardName)) continue;

            var set = card.TryGetProperty("set", out var s) ? (s.GetString() ?? "") : "";
            var collector = card.TryGetProperty("cn", out var cnum) ? (cnum.GetString() ?? "") : "";

            for (int i = 0; i < Math.Clamp(qty, 1, 99); i++)
                into.Add(new ImportedCard(new CardModel
                {
                    Name = cardName,
                    SetCode = set.ToUpperInvariant(),
                    CollectorNumber = collector,
                    TemplateName = defaultTemplate,
                }, NeedsLookup: true));
        }
    }
}
