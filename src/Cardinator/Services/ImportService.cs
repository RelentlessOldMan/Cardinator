using System.IO;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>A parsed card plus whether its blank fields should be filled from Scryfall.</summary>
public sealed record ImportedCard(CardModel Card, bool NeedsLookup);

/// <summary>The outcome of parsing a list: the cards, plus how many non-blank/non-comment lines
/// were dropped because they couldn't be turned into a card (so the UI can tell the user instead
/// of silently losing them).</summary>
public sealed record ImportResult(List<ImportedCard> Cards, int Skipped);

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

    /// <summary>
    /// True when the pasted text is nothing but web link(s) — i.e. the user pasted a deck URL instead of
    /// the exported list. Deck sites (Moxfield, Archidekt, …) block apps from opening links directly, so
    /// the UI uses this to steer the user to the site's Export button rather than attempting a doomed fetch.
    /// </summary>
    private static string NameKey(string? name)
    {
        var n = (name ?? "").Trim();
        // A double-faced card is written either way ("Delver of Secrets" or "Delver of Secrets // Insectile
        // Aberration"), and Scryfall canonicalises to the front face — so key on the front half, otherwise
        // re-importing the same deck list silently appends duplicates instead of warning.
        int split = n.IndexOf("//", StringComparison.Ordinal);
        if (split > 0) n = n[..split].Trim();
        return n.ToLowerInvariant();
    }

    /// <summary>Counts how many incoming cards share a name (case-insensitive) with a card already in the
    /// project — so re-importing the same list can warn instead of silently appending duplicates (L3).</summary>
    public static int CountNamesAlreadyIn(IEnumerable<CardModel> existing, IEnumerable<ImportedCard> incoming)
    {
        var have = new HashSet<string>((existing ?? Enumerable.Empty<CardModel>()).Select(c => NameKey(c.Name)));
        return (incoming ?? Enumerable.Empty<ImportedCard>()).Count(i => have.Contains(NameKey(i.Card.Name)));
    }

    /// <summary>Returns the incoming cards whose names aren't already in the project (keeps order; L3).</summary>
    public static List<ImportedCard> RemoveNamesAlreadyIn(IEnumerable<CardModel> existing, IEnumerable<ImportedCard> incoming)
    {
        var have = new HashSet<string>((existing ?? Enumerable.Empty<CardModel>()).Select(c => NameKey(c.Name)));
        return (incoming ?? Enumerable.Empty<ImportedCard>()).Where(i => !have.Contains(NameKey(i.Card.Name))).ToList();
    }

    public static bool LooksLikeOnlyLinks(string? content)
    {
        var lines = (content ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count > 0 && lines.All(l =>
            l.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("www.", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses a list and returns only the cards (back-compat convenience).</summary>
    public static List<ImportedCard> Parse(string content, string? artBaseDir, string defaultTemplate)
        => ParseWithReport(content, artBaseDir, defaultTemplate).Cards;

    /// <summary>Parses a list and also reports how many meaningful lines were dropped (L5).</summary>
    public static ImportResult ParseWithReport(string content, string? artBaseDir, string defaultTemplate)
    {
        content = (content ?? "").TrimStart('﻿');   // strip a UTF-8 BOM so line 0 isn't polluted
        var rawLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var lines = rawLines
            .Where(l => l.Trim().Length > 0
                        && !l.TrimStart().StartsWith('#')
                        && !l.TrimStart().StartsWith("//"))   // deck-list comment lines
            .ToList();
        if (lines.Count == 0) return new(new(), 0);

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
    // Printing hint "(SET) 123": uppercase set code, optional collector that may be alphanumeric with a
    // hyphen (foils/variants like "273p", and The List / PLST numbers like "M20-14"). Anchored at end so
    // real names ending in "(...)" aren't mistaken for a hint.
    private static readonly Regex SetHint = new(@"\s*\(([A-Z0-9]{2,6})\)\s*([0-9A-Za-z★\-]+)?\s*$", RegexOptions.Compiled);
    // Trailing foil/etched marker some deck sites (e.g. Moxfield) append, e.g. "... (LTC) 273 *F*".
    private static readonly Regex FoilMarker = new(@"\s*\*[A-Za-z]\*\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> SectionHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "deck", "sideboard", "commander", "maybeboard", "companion" };

    private static ImportResult ParsePlain(List<string> lines, string? artBaseDir, string defaultTemplate)
    {
        var result = new List<ImportedCard>();
        int skipped = 0;
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

            // Strip a trailing foil/etched marker before reading the printing hint at the end of the line.
            line = FoilMarker.Replace(line, "").TrimEnd();

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

            if (line.Length == 0) { skipped++; continue; }   // nothing left after stripping qty/hint/art
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
        return new ImportResult(result, skipped);
    }

    // --- delimited (CSV/TSV) ------------------------------------------------

    private static ImportResult ParseDelimited(List<string> lines, char delimiter, string? artBaseDir, string defaultTemplate)
    {
        var header = SplitLine(lines[0], delimiter).Select(Normalize).ToList();
        var col = new Dictionary<string, int>();
        for (int i = 0; i < header.Count; i++)
            if (Aliases.TryGetValue(header[i], out var key) && !col.ContainsKey(key))
                col[key] = i;

        var result = new List<ImportedCard>();
        int skipped = 0;
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
            if (name.Length == 0) { skipped++; continue; }   // data row with no card name

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
        return new ImportResult(result, skipped);
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
