using System.IO;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>A parsed card plus whether its blank fields should be filled from Scryfall.</summary>
public sealed record ImportedCard(CardModel Card, bool NeedsLookup);

/// <summary>
/// Parses card lists in flexible formats so users can create many cards at once:
///  - a plain list of card names (one per line);
///  - "Name | art/path.png" or "Name &lt;TAB&gt; art/path.png" per line;
///  - CSV/TSV with a header row (columns matched by friendly aliases, any order).
///
/// Lines that are blank or start with '#' are ignored. Missing text fields are flagged for
/// a Scryfall lookup; anything the user supplies is kept as an override.
/// </summary>
public static class ImportService
{
    // Column header aliases (lower-cased, punctuation-insensitive).
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["name"] = "name", ["card"] = "name", ["cardname"] = "name", ["title"] = "name",
        ["art"] = "art", ["image"] = "art", ["artpath"] = "art", ["imagepath"] = "art",
        ["picture"] = "art", ["artwork"] = "art", ["file"] = "art", ["img"] = "art",
        ["mana"] = "mana", ["manacost"] = "mana", ["cost"] = "mana",
        ["type"] = "type", ["typeline"] = "type", ["types"] = "type",
        ["rules"] = "rules", ["text"] = "rules", ["rulestext"] = "rules",
        ["oracle"] = "rules", ["oracletext"] = "rules", ["ability"] = "rules", ["abilities"] = "rules",
        ["flavor"] = "flavor", ["flavour"] = "flavor", ["flavortext"] = "flavor",
        ["power"] = "power", ["pow"] = "power",
        ["toughness"] = "toughness", ["tough"] = "toughness", ["tou"] = "toughness",
        ["pt"] = "pt", ["powertoughness"] = "pt",
        ["loyalty"] = "loyalty", ["loy"] = "loyalty",
        ["template"] = "template", ["frame"] = "template", ["border"] = "template",
        ["color"] = "template", ["colour"] = "template",
        ["artist"] = "artist", ["illustrator"] = "artist", ["illus"] = "artist",
        ["set"] = "set", ["setcode"] = "set", ["expansion"] = "set",
        ["collector"] = "collector", ["collectornumber"] = "collector", ["number"] = "collector", ["num"] = "collector",
        ["rarity"] = "rarity",
        ["copyright"] = "copyright", ["copy"] = "copyright",
        ["lookup"] = "lookup", ["scryfall"] = "lookup", ["fetch"] = "lookup",
    };

    public static List<ImportedCard> Parse(string content, string? artBaseDir, string defaultTemplate)
    {
        content = (content ?? "").TrimStart('﻿');   // strip a UTF-8 BOM so line 0 isn't polluted
        var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var lines = rawLines
            .Where(l => l.Trim().Length > 0
                        && !l.TrimStart().StartsWith('#')
                        && !l.TrimStart().StartsWith("//"))   // deck-list comment lines
            .ToList();
        if (lines.Count == 0) return new();

        var delimiter = DetectDelimiter(lines[0]);
        if (delimiter != '\0' && LooksLikeHeader(lines[0], delimiter))
            return ParseDelimited(lines, delimiter, artBaseDir, defaultTemplate);

        return ParsePlain(lines, artBaseDir, defaultTemplate);
    }

    private static char DetectDelimiter(string line)
    {
        if (line.Contains('\t')) return '\t';
        if (line.Contains(',')) return ',';
        return '\0';
    }

    private static bool LooksLikeHeader(string line, char delimiter)
        => SplitLine(line, delimiter).Any(f => Aliases.ContainsKey(Normalize(f)));

    // --- plain list ---------------------------------------------------------

    // Deck-list helpers: a leading quantity ("4 " / "2x "), a trailing "(SET) 123" printing hint, and
    // common section headers. Set codes are required to be uppercase so real names ending in "(...)"
    // (e.g. reminder-style names) aren't mistaken for a hint.
    private static readonly Regex QtyPrefix = new(@"^(\d{1,3})\s*[xX]?\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex SetHint = new(@"\s*\(([A-Z0-9]{2,6})\)\s*(\d+)?\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> SectionHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "deck", "sideboard", "commander", "maybeboard", "companion" };

    private static List<ImportedCard> ParsePlain(List<string> lines, string? artBaseDir, string defaultTemplate)
    {
        var result = new List<ImportedCard>();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (SectionHeaders.Contains(line)) continue;                 // "Deck" / "Sideboard" headers
            if (line.StartsWith("SB:", StringComparison.OrdinalIgnoreCase)) line = line[3..].Trim();

            // Allow "Name | art" or "Name <TAB> art".
            string art = "";
            int sep = line.IndexOf('\t');
            if (sep < 0) sep = line.IndexOf('|');
            if (sep >= 0)
            {
                art = line[(sep + 1)..].Trim();
                line = line[..sep].Trim();
            }

            // Leading quantity: "4 Lightning Bolt" / "2x Counterspell" -> N copies.
            int qty = 1;
            var q = QtyPrefix.Match(line);
            if (q.Success) { qty = Math.Clamp(int.Parse(q.Groups[1].Value), 1, 99); line = q.Groups[2].Value.Trim(); }

            // Trailing printing hint: "Lightning Bolt (M10) 146".
            string setCode = "", collector = "";
            var h = SetHint.Match(line);
            if (h.Success)
            {
                setCode = h.Groups[1].Value;
                collector = h.Groups[2].Success ? h.Groups[2].Value : "";
                line = line[..h.Index].Trim();
            }

            if (line.Length == 0) continue;
            for (int i = 0; i < qty; i++)
            {
                var card = new CardModel
                {
                    Name = line,
                    ArtPath = ResolveArt(art, artBaseDir),
                    SetCode = setCode,
                    CollectorNumber = collector,
                    TemplateName = defaultTemplate,
                };
                result.Add(new ImportedCard(card, NeedsLookup: true));
            }
        }
        return result;
    }

    // --- delimited (CSV/TSV) ------------------------------------------------

    private static List<ImportedCard> ParseDelimited(List<string> lines, char delimiter, string? artBaseDir, string defaultTemplate)
    {
        var header = SplitLine(lines[0], delimiter).Select(Normalize).ToList();
        var col = new Dictionary<string, int>();
        for (int i = 0; i < header.Count; i++)
            if (Aliases.TryGetValue(header[i], out var key) && !col.ContainsKey(key))
                col[key] = i;

        var result = new List<ImportedCard>();
        for (int r = 1; r < lines.Count; r++)
        {
            var fields = SplitLine(lines[r], delimiter);
            string Get(string key)
            {
                if (col.TryGetValue(key, out var idx) && idx < fields.Count)
                    return fields[idx].Trim();
                return "";
            }

            var name = Get("name");
            if (name.Length == 0) continue;

            var card = new CardModel
            {
                Name = name,
                ArtPath = ResolveArt(Get("art"), artBaseDir),
                ManaCost = ManaText.NormalizeCost(Get("mana")),
                TypeLine = Get("type"),
                RulesText = Unescape(Get("rules")),
                FlavorText = Unescape(Get("flavor")),
                Power = Get("power"),
                Toughness = Get("toughness"),
                Loyalty = Get("loyalty"),
                Artist = Get("artist"),
                SetCode = Get("set"),
                CollectorNumber = Get("collector"),
                Rarity = Get("rarity"),
                Copyright = Get("copyright"),
                TemplateName = Get("template") is { Length: > 0 } t ? t : defaultTemplate,
            };

            var pt = Get("pt");
            if (pt.Contains('/') && card.Power.Length == 0 && card.Toughness.Length == 0)
            {
                var bits = pt.Split('/', 2);
                card.Power = bits[0].Trim();
                card.Toughness = bits[1].Trim();
            }

            bool blank = card.ManaCost.Length == 0 && card.TypeLine.Length == 0 && card.RulesText.Length == 0;
            bool needsLookup = ParseLookupFlag(Get("lookup")) ?? blank;
            result.Add(new ImportedCard(card, needsLookup));
        }
        return result;
    }

    private static bool? ParseLookupFlag(string v) => v.ToLowerInvariant() switch
    {
        "" => null,
        "1" or "true" or "yes" or "y" => true,
        "0" or "false" or "no" or "n" => false,
        _ => null,
    };

    // --- helpers ------------------------------------------------------------

    /// <summary>Splits a delimited line, honoring double-quoted fields ("" escapes a quote).</summary>
    private static List<string> SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    private static string Normalize(string header)
        => new(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Unescape(string s) => s.Replace("\\n", "\n");

    private static string ResolveArt(string art, string? artBaseDir)
    {
        if (string.IsNullOrWhiteSpace(art)) return "";
        art = art.Trim().Trim('"');
        if (Path.IsPathRooted(art)) return art;
        if (!string.IsNullOrWhiteSpace(artBaseDir))
            return Path.Combine(artBaseDir, art);
        return art;
    }
}
