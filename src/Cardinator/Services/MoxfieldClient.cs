using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Pure helpers for Moxfield deck import: recognizing a deck URL, extracting its id, and turning the
/// deck JSON into a plain decklist (which then flows through the same <see cref="ImportService"/> parser
/// as file/paste import). The network fetch lives in <see cref="MoxfieldFetcher"/> (it needs a real
/// browser engine to get past Cloudflare); keeping the parsing here makes it unit-testable.
/// </summary>
public static class MoxfieldClient
{
    private static readonly Regex DeckIdRe =
        new(@"moxfield\.com/decks/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The boards we treat as "the deck" for card-making, in a natural read order (commander
    /// first). Excludes maybeboard/tokens/etc.</summary>
    private static readonly string[] OrderedBoards =
        { "commanders", "mainboard", "companions", "signatureSpells", "sideboard" };

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
    /// Turns a Moxfield deck JSON into a plain decklist ("1 Sol Ring (LTC) 273" per line), optionally
    /// prefixed with a "# Deck Name" comment. This feeds the shared <see cref="ImportService"/> parser,
    /// so the user can review/edit the list before importing. Handles both the v3 shape
    /// (<c>boards.{mainboard,commanders,…}.cards</c>) and the older flat v2 shape.
    /// </summary>
    public static string ToDeckList(string json, bool includeName = true)
    {
        var (name, entries) = Enumerate(json);
        var sb = new StringBuilder();
        if (includeName && !string.IsNullOrWhiteSpace(name)) sb.Append("# ").Append(name).Append("\n\n");
        foreach (var (qty, cardName, set, cn) in entries)
        {
            sb.Append(qty).Append(' ').Append(cardName);
            if (set.Length > 0)
            {
                sb.Append(" (").Append(set).Append(')');
                if (cn.Length > 0) sb.Append(' ').Append(cn);
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Maps a Moxfield deck JSON directly into importable cards (used by tests; the app path goes
    /// through <see cref="ToDeckList"/> + <see cref="ImportService"/> so everything shares one parser).</summary>
    public static (string name, List<ImportedCard> cards) ParseDeck(string json, string defaultTemplate)
    {
        var (name, entries) = Enumerate(json);
        var cards = new List<ImportedCard>();
        foreach (var (qty, cardName, set, cn) in entries)
            for (int i = 0; i < qty; i++)
                cards.Add(new ImportedCard(new CardModel
                {
                    Name = cardName, SetCode = set, CollectorNumber = cn, TemplateName = defaultTemplate,
                }, NeedsLookup: true));
        return (name, cards);
    }

    /// <summary>Walks the deck JSON and yields (quantity, name, SET, collector) for each deck card,
    /// commander-first, tolerating both the v3 and v2 shapes.</summary>
    private static (string name, List<(int qty, string name, string set, string cn)> entries) Enumerate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var entries = new List<(int, string, string, string)>();

        // v3 nests boards under "boards"; v2 has them as top-level properties. Look up by our ordered
        // names in whichever container applies.
        JsonElement container = root.TryGetProperty("boards", out var boards)
                                && boards.ValueKind == JsonValueKind.Object ? boards : root;

        foreach (var boardName in OrderedBoards)
            if (container.TryGetProperty(boardName, out var board))
                AddBoard(board, entries);

        return (name, entries);
    }

    private static void AddBoard(JsonElement board, List<(int, string, string, string)> into)
    {
        // A board is either { "cards": { <id>: {quantity, card} } } (v3) or { <name>: {quantity, card} } (v2).
        var cards = board.ValueKind == JsonValueKind.Object
                    && board.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Object
            ? c : board;
        if (cards.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in cards.EnumerateObject())
        {
            var item = entry.Value;
            if (item.ValueKind != JsonValueKind.Object) continue;

            int qty = item.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 1;
            qty = Math.Clamp(qty, 1, 99);
            if (!item.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.Object) continue;

            var cardName = card.TryGetProperty("name", out var cn) ? (cn.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(cardName)) continue;

            var set = (card.TryGetProperty("set", out var s) ? (s.GetString() ?? "") : "").ToUpperInvariant();
            var collector = card.TryGetProperty("cn", out var cnum) ? (cnum.GetString() ?? "") : "";
            into.Add((qty, cardName, set, collector));
        }
    }
}
