using System.IO;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>A parsed card plus whether its blank fields should be filled from Scryfall.</summary>
/// <remarks><paramref name="MeldHalfGiven"/> is false only for a CSV meld card whose half the list didn't say (nor its
/// partner's): the lookup then picks it from Scryfall's collector numbers.</remarks>
/// <param name="PrintingHint">The set / collector number came from a deck list's "(SET) 123" hint — a guess at a real
/// printing, which the lookup replaces with the printing it actually found — rather than the card's own data.</param>
public sealed record ImportedCard(CardModel Card, bool NeedsLookup, bool MeldHalfGiven = true, bool PrintingHint = false);

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
        ["defense"] = "defense", ["defence"] = "defense", ["def"] = "defense",
        ["orientation"] = "orientation",
        // A flip card's upside-down half (any of these columns makes the row a flip card).
        ["flipname"] = "flipname", ["flippedname"] = "flipname",
        ["fliptype"] = "fliptype", ["fliptypeline"] = "fliptype",
        ["fliprules"] = "fliprules", ["fliptext"] = "fliprules",
        ["flippower"] = "flippower", ["fliptoughness"] = "fliptoughness", ["flippt"] = "flippt",
        // A split card's second half (any of these columns makes the row a split card).
        ["splitname"] = "splitname", ["splitcost"] = "splitcost", ["splitmana"] = "splitcost",
        ["splitmanacost"] = "splitcost", ["splittype"] = "splittype", ["splittypeline"] = "splittype",
        ["splitrules"] = "splitrules", ["splittext"] = "splitrules", ["splitflavor"] = "splitflavor",
        ["splitart"] = "splitart", ["splitframe"] = "splitframe", ["splittemplate"] = "splitframe",
        // A meld card: the melded card it's half of (any of these columns makes the row a meld card). Rows naming
        // the same melded card are partners; the melded card's text can be written on either row.
        ["meld"] = "meldname", ["meldname"] = "meldname", ["meldedname"] = "meldname", ["meldedcard"] = "meldname",
        ["meldresult"] = "meldname", ["meldinto"] = "meldname",
        ["meldhalf"] = "meldhalf", ["meldside"] = "meldhalf",
        ["meldwith"] = "meldwith", ["meldpartner"] = "meldwith",
        ["meldcost"] = "meldcost", ["meldmana"] = "meldcost", ["meldmanacost"] = "meldcost",
        ["meldtype"] = "meldtype", ["meldtypeline"] = "meldtype",
        ["meldrules"] = "meldrules", ["meldtext"] = "meldrules", ["meldflavor"] = "meldflavor",
        ["meldpower"] = "meldpower", ["meldtoughness"] = "meldtoughness", ["meldpt"] = "meldpt",
        ["meldart"] = "meldart",
        ["template"] = "template", ["frame"] = "template", ["border"] = "template",
        ["color"] = "template", ["colour"] = "template",
        ["artist"] = "artist", ["illustrator"] = "artist", ["illus"] = "artist",
        ["set"] = "set", ["setcode"] = "set", ["expansion"] = "set", ["editioncode"] = "set",
        // A deck site's "Edition": the set code on Moxfield ("m10"), the set's full name on Deckbox ("Magic 2010",
        // with the code in "Edition Code") — so it's only taken as the set when it looks like a code.
        ["edition"] = "edition",
        ["collector"] = "collector", ["collectornumber"] = "collector", ["number"] = "collector", ["num"] = "collector",
        ["cardnumber"] = "collector",
        ["rarity"] = "rarity",
        ["copyright"] = "copyright", ["copy"] = "copyright",
        ["lookup"] = "lookup", ["scryfall"] = "lookup", ["fetch"] = "lookup",
        // How many copies of the row (a deck export's "Count" column): "4" or "4x".
        ["qty"] = "qty", ["quantity"] = "qty", ["count"] = "qty", ["copies"] = "qty", ["amount"] = "qty",
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
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        static bool Meaningful(string l) => l.Trim().Length > 0
                                            && !l.TrimStart().StartsWith('#')
                                            && !l.TrimStart().StartsWith("//");   // deck-list comment lines
        var lines = content.Split('\n').Where(Meaningful).ToList();
        if (lines.Count == 0) return new(new(), 0);

        var delimiter = HeaderDelimiter(lines[0]);
        if (delimiter != '\0')
        {
            // A CSV record can span lines: a quoted field may hold line breaks (multi-line rules from Excel).
            var records = SplitRecords(content, delimiter).Where(Meaningful).ToList();
            if (records.Count == 0) return new(new(), 0);
            return ParseDelimited(records, delimiter, artBaseDir, defaultTemplate);
        }

        return ParsePlain(lines, artBaseDir, defaultTemplate);
    }

    /// <summary>The delimiter of a header row — tab, semicolon (Excel in comma-decimal locales) or comma —
    /// whichever splits it into the most known column names; '\0' when the line isn't a header.</summary>
    private static char HeaderDelimiter(string line)
    {
        char best = '\0';
        int bestHits = 0;
        foreach (var d in new[] { '\t', ';', ',' })
        {
            if (!line.Contains(d)) continue;
            int hits = SplitLine(line, d).Count(f => Aliases.ContainsKey(Normalize(f)));
            if (hits > bestHits) { best = d; bestHits = hits; }
        }
        return best;
    }

    /// <summary>Splits text into CSV records: at line breaks, except inside a double-quoted field. Only a quote
    /// that STARTS a field opens one (as in <see cref="SplitLine"/>), so a stray quote in plain text — 'Stands
    /// 40" tall', or one in a # comment line — can't swallow the rows after it. A quoted field that never closes
    /// means the quotes weren't CSV quoting after all: the file is then split at every line break.</summary>
    private static List<string> SplitRecords(string content, char delimiter)
    {
        var records = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, fieldStart = true, comment = false;
        for (int i = 0; i < content.Length; i++)
        {
            char ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cur.Append("\"\""); i++; continue; }
                    inQuotes = false;
                }
                cur.Append(ch);
                continue;
            }
            if (ch == '\n') { records.Add(cur.ToString()); cur.Clear(); fieldStart = true; comment = false; continue; }
            if (cur.Length == 0 && (ch == '#' || (ch == '/' && i + 1 < content.Length && content[i + 1] == '/'))) comment = true;
            if (!comment)
            {
                if (ch == '"' && fieldStart) { inQuotes = true; fieldStart = false; }
                else if (ch == delimiter) fieldStart = true;
                else if (ch != ' ') fieldStart = false;
            }
            cur.Append(ch);
        }
        if (inQuotes) return content.Split('\n').ToList();   // unbalanced: not quoting, so no record spans lines
        records.Add(cur.ToString());
        return records;
    }

    /// <summary>Reads a list file as text: UTF-8 (with or without a BOM, or UTF-16 with one), and otherwise
    /// Windows-1252 — what Excel's plain "CSV" save writes, where é, ’ and — aren't valid UTF-8.</summary>
    public static string ReadListFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))
                   || bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (bom) return File.ReadAllText(path);   // the BOM says which
        try { return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }

    // --- plain list ---------------------------------------------------------

    // Deck-list helpers: a leading quantity ("4 " / "2x "), a trailing "(SET) 123" printing hint, and
    // common section headers.
    private static readonly Regex QtyPrefix = new(@"^(\d{1,3})\s*[xX]?\s+(.+)$", RegexOptions.Compiled);
    // Printing hint "(SET) 123": a 2–6 character set code in either case (some exports write "(cmr)"; it's
    // upper-cased when read), optional collector that may be alphanumeric with a hyphen (foils/variants like
    // "273p", and The List / PLST numbers like "M20-14"). Anchored at end; names that end in "(...)" have
    // spaces in the brackets, so they aren't mistaken for a hint.
    private static readonly Regex SetHint = new(@"\s*\(([A-Za-z0-9]{2,6})\)\s*([0-9A-Za-z★\-]+)?\s*$", RegexOptions.Compiled);
    // Trailing foil/etched marker some deck sites (e.g. Moxfield) append, e.g. "... (LTC) 273 *F*".
    private static readonly Regex FoilMarker = new(@"\s*\*[A-Za-z]\*\s*$", RegexOptions.Compiled);
    // Deck sites head their sections "Sideboard", "Sideboard:", "Creatures (25)"… — never card names.
    private static readonly HashSet<string> SectionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "deck", "main", "mainboard", "main deck", "sideboard", "commander", "commanders", "maybeboard", "companion",
        "considering", "tokens", "creature", "creatures", "instant", "instants", "sorcery", "sorceries",
        "artifact", "artifacts", "enchantment", "enchantments", "planeswalker", "planeswalkers",
        "land", "lands", "battle", "battles", "spells", "other",
    };
    private static readonly Regex SectionSuffix = new(@"\s*(\(\d+\))?\s*:?\s*$", RegexOptions.Compiled);

    private static ImportResult ParsePlain(List<string> lines, string? artBaseDir, string defaultTemplate)
    {
        var result = new List<ImportedCard>();
        int skipped = 0;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (SectionHeaders.Contains(SectionSuffix.Replace(line, ""))) continue;   // "Deck" / "Sideboard:" / "Lands (36)"
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
            // A lowercase code counts only with a collector number after it ("(cmr) 472"): on its own it's more
            // likely part of a custom card's name — "Goblin King (alt)", "Fire (v2)".
            if (h.Success && (h.Groups[2].Success || !h.Groups[1].Value.Any(char.IsLower)))
            {
                setCode = h.Groups[1].Value.ToUpperInvariant();
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
                result.Add(new ImportedCard(card, NeedsLookup: true, PrintingHint: setCode.Length > 0));
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
        var melds = new List<(int index, bool halfGiven)>();
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
                Defense = Get("defense"),
                Orientation = Get("orientation"),
                Artist = Get("artist"),
                SetCode = Get("set") is { Length: > 0 } set ? set : LooksLikeSetCode(Get("edition")) ? Get("edition") : "",
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

            // Flip card columns: the upside-down half shares the row's art and set details.
            var half = new CardModel
            {
                Name = Get("flipname"), TypeLine = Get("fliptype"), RulesText = Unescape(Get("fliprules")),
                Power = Get("flippower"), Toughness = Get("fliptoughness"), TemplateName = card.TemplateName,
            };
            var fpt = Get("flippt");
            if (fpt.Contains('/') && half.Power.Length == 0 && half.Toughness.Length == 0)
            {
                var bits = fpt.Split('/', 2);
                half.Power = bits[0].Trim();
                half.Toughness = bits[1].Trim();
            }
            if (half.Name.Length + half.TypeLine.Length + half.RulesText.Length + half.Power.Length + half.Toughness.Length > 0)
            {
                card.HalfLayout = "flip";
                card.OtherHalf = half;
            }

            // Split card columns: the second half has its own cost, rules and art; set details are the row's.
            var split = new CardModel
            {
                Name = Get("splitname"), ManaCost = ManaText.NormalizeCost(Get("splitcost")), TypeLine = Get("splittype"),
                RulesText = Unescape(Get("splitrules")), FlavorText = Unescape(Get("splitflavor")),
                ArtPath = ResolveArt(Get("splitart"), artBaseDir), TemplateName = card.TemplateName,
            };
            if (card.OtherHalf == null
                && split.Name.Length + split.ManaCost.Length + split.TypeLine.Length + split.RulesText.Length + split.ArtPath.Length > 0)
            {
                card.HalfLayout = "split";
                card.OtherHalf = split;
                card.HalfTemplateName = Get("splitframe");   // blank = the card's frame
            }

            // Meld columns: the back face is the melded card, of which this card prints one half.
            var melded = new CardModel
            {
                Name = Get("meldname"), ManaCost = ManaText.NormalizeCost(Get("meldcost")), TypeLine = Get("meldtype"),
                RulesText = Unescape(Get("meldrules")), FlavorText = Unescape(Get("meldflavor")),
                Power = Get("meldpower"), Toughness = Get("meldtoughness"),
                ArtPath = ResolveArt(Get("meldart"), artBaseDir), TemplateName = card.TemplateName,
            };
            var mpt = Get("meldpt");
            if (mpt.Contains('/') && melded.Power.Length == 0 && melded.Toughness.Length == 0)
            {
                var bits = mpt.Split('/', 2);
                melded.Power = bits[0].Trim();
                melded.Toughness = bits[1].Trim();
            }
            var meldHalf = ParseMeldHalf(Get("meldhalf"));
            var meldWith = Get("meldwith");
            if (card.OtherHalf == null
                && (MeldFields(melded).Any(v => v.Length > 0) || meldHalf.Length > 0 || meldWith.Length > 0))
            {
                card.BackFace = melded;
                card.MeldHalf = meldHalf.Length > 0 ? meldHalf : "top";
                card.MeldWith = meldWith;
                card.DfcStyle = "meld";
                melds.Add((result.Count, meldHalf.Length > 0));
            }

            bool blank = card.ManaCost.Length == 0 && card.TypeLine.Length == 0 && card.RulesText.Length == 0;
            bool needsLookup = ParseLookupFlag(Get("lookup")) ?? blank;
            result.Add(new ImportedCard(card, needsLookup));
            // Extra copies are separate cards, as "4 Lightning Bolt" makes in a list. Not for a meld row, whose
            // partner is paired by position.
            if (!card.IsMeld)
                for (int i = 1; i < ParseQty(Get("qty")); i++) result.Add(new ImportedCard(card.Clone(), needsLookup));
        }
        PairMeldRows(result, melds);
        return new ImportResult(result, skipped);
    }

    /// <summary>A quantity cell ("4", "4x", "x4") as a copy count from 1 to 99; 1 when blank or unreadable.</summary>
    private static int ParseQty(string v)
        => int.TryParse(v.Trim().Trim('x', 'X').Trim(), out var n) ? Math.Clamp(n, 1, 99) : 1;

    /// <summary>"top"/"bottom" from a meld-half cell ("top", "upper", "1", "bottom", "lower", "2", …), or "".</summary>
    private static string ParseMeldHalf(string v) => v.Trim().ToLowerInvariant() switch
    {
        "top" or "t" or "upper" or "up" or "1" or "first" => "top",
        "bottom" or "b" or "lower" or "down" or "2" or "second" => "bottom",
        _ => "",
    };

    private static string[] MeldFields(CardModel m)
        => new[] { m.Name, m.ManaCost, m.TypeLine, m.RulesText, m.FlavorText, m.Power, m.Toughness, m.ArtPath };

    /// <summary>Partners the meld rows: rows naming the same melded card (or naming each other in meld_with) share one
    /// melded card — its text can be on either row — print opposite halves, and name each other as partners.</summary>
    private static void PairMeldRows(List<ImportedCard> result, List<(int index, bool halfGiven)> melds)
    {
        static string Key(string s) => s.Trim().ToLowerInvariant();
        var groups = new List<List<(int index, bool halfGiven)>>();
        foreach (var m in melds)
        {
            var card = result[m.index].Card;
            var group = groups.FirstOrDefault(g => g.Count < 2 && g.Any(o =>
            {
                var other = result[o.index].Card;
                return card.BackFace!.Name.Length > 0 && Key(card.BackFace.Name) == Key(other.BackFace!.Name)
                       || card.MeldWith.Length > 0 && Key(card.MeldWith) == Key(other.Name)
                       || other.MeldWith.Length > 0 && Key(other.MeldWith) == Key(card.Name);
            }));
            if (group != null) group.Add(m); else groups.Add(new() { m });
        }

        foreach (var g in groups)
        {
            var cards = g.Select(m => result[m.index].Card).ToList();
            bool given = g.Any(m => m.halfGiven);
            if (cards.Count == 2)
            {
                // One melded card: each field from whichever row wrote it.
                var backs = cards.Select(c => c.BackFace!).ToList();
                string First(Func<CardModel, string> f) => backs.Select(f).FirstOrDefault(v => v.Length > 0) ?? "";
                var merged = new CardModel
                {
                    Name = First(b => b.Name), ManaCost = First(b => b.ManaCost), TypeLine = First(b => b.TypeLine),
                    RulesText = First(b => b.RulesText), FlavorText = First(b => b.FlavorText),
                    Power = First(b => b.Power), Toughness = First(b => b.Toughness), ArtPath = First(b => b.ArtPath),
                };
                foreach (var b in backs)
                {
                    b.Name = merged.Name; b.ManaCost = merged.ManaCost; b.TypeLine = merged.TypeLine;
                    b.RulesText = merged.RulesText; b.FlavorText = merged.FlavorText;
                    b.Power = merged.Power; b.Toughness = merged.Toughness; b.ArtPath = merged.ArtPath;
                }

                // Opposite halves: a half the list gave wins; with none given, the first row is the top.
                if (g[1].halfGiven && !g[0].halfGiven) cards[0].MeldHalf = cards[1].IsMeldBottom ? "top" : "bottom";
                else if (!g[1].halfGiven) cards[1].MeldHalf = cards[0].IsMeldBottom ? "top" : "bottom";

                if (cards[0].MeldWith.Length == 0) cards[0].MeldWith = cards[1].Name;
                if (cards[1].MeldWith.Length == 0) cards[1].MeldWith = cards[0].Name;
            }
            // With no half given, a lookup may pick it from the collector numbers — only if every row of the pair is
            // looked up, so the two can't end up on the same half.
            if (!given && g.All(m => result[m.index].NeedsLookup))
                foreach (var m in g) result[m.index] = result[m.index] with { MeldHalfGiven = false };
        }
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
            // Only a quote that starts a field opens a quoted field; one inside text ('40" tall') is just a quote.
            else if (ch == '"' && cur.ToString().Trim().Length == 0) { cur.Clear(); inQuotes = true; }
            else if (ch == delimiter) { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    private static string Normalize(string header)
        => new(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>True for a set code ("m10", "PLST", "2XM"): 2–6 letters and digits, nothing else.</summary>
    private static bool LooksLikeSetCode(string s) => s.Length is >= 2 and <= 6 && s.All(char.IsAsciiLetterOrDigit);

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
