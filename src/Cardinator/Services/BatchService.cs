using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

public sealed record FillReport(int Found, int Filled, List<string> NotFound);
public sealed record BatchExportResult(int Exported, List<string> Errors);

/// <summary>
/// Batch operations over many cards: fill blank fields from Scryfall, and render every card
/// to a PNG. Progress is reported as human-readable strings for the UI/CLI.
/// </summary>
public static class BatchService
{
    /// <summary>
    /// For each card flagged NeedsLookup, fetches it from Scryfall and fills any blank fields.
    /// User-supplied overrides are preserved. Returns the cards (mutated in place) via the input list.
    /// </summary>
    public static async Task<FillReport> FillFromScryfallAsync(
        IReadOnlyList<ImportedCard> cards,
        IProgress<string>? progress = null,
        bool downloadArt = true,
        CancellationToken ct = default,
        IProgress<double>? percent = null)
    {
        var client = new ScryfallClient();
        int found = 0, filled = 0;
        var notFound = new List<string>();

        // Cards that share a name + printing hint resolve to the same card, so look each unique request
        // up only once (a 4-of playset or repeated basics collapses to one lookup + one art download).
        var groups = cards.Where(c => c.NeedsLookup)
            .GroupBy(c => $"{c.Card.Name}{c.Card.SetCode}{c.Card.CollectorNumber}")
            .ToList();

        // Art downloads to run after the lookups. Each hits Scryfall's image CDN (which — unlike the API —
        // isn't rate-limited), so they parallelize; keyed by URL so identical art downloads once.
        var artJobs = new List<(CardModel card, string url)>();
        var meldJobs = new List<(CardModel card, CardModel part, bool halfGiven)>();   // meld parts whose melded card is fetched after

        // Diagnostic log written to CardinatorData so import failures can be inspected exactly.
        var log = new List<string> { $"=== Import {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {groups.Count} unique card(s) ===" };

        // Fills every card in a group from its resolved faces, queuing art + double-faced backs.
        void ApplyFaces(IGrouping<string, ImportedCard> group, List<CardModel> faces)
        {
            foreach (var member in group)
            {
                found++;
                FillBlanks(member.Card, faces[0], canonicalName: true);
                filled++;
                if (downloadArt && Blank(member.Card.ArtPath) && !Blank(faces[0].ArtUrl))
                    artJobs.Add((member.Card, faces[0].ArtUrl));

                // Genuinely two-sided: attach the back as THIS card's back face (one card, two faces). Only
                // the first extra face is used (standard DFC); the renderer/model keep it to one level. A
                // split/flip/aftermath card is printed on one side, so AttachFaces hands those faces back
                // instead — batch import fills from the primary face and logs the rest (they're separate
                // cards, which a per-line batch fill has no slot for).
                var unused = CardDetailsFill.AttachFaces(member.Card, faces);
                if (!Blank(faces[0].MeldResultUrl)) meldJobs.Add((member.Card, faces[0], member.MeldHalfGiven));
                else if (!Blank(faces[0].MeldWith)) member.Card.MeldWith = faces[0].MeldWith;
                if (member.Card.BackFace is { } back && downloadArt && !Blank(back.ArtUrl))
                    artJobs.Add((back, back.ArtUrl));
                foreach (var extra in unused)
                    log.Add($"  note: \"{member.Card.Name}\" is a {faces[0].Layout} card — the other half " +
                            $"(\"{extra.Name}\") was not added as a separate card.");
            }
        }

        // Phase 1a: one batched /cards/collection request per 75 cards (vs. one call per card). Prefer an
        // exact printing (set + collector) when we have it — that resolves double-faced cards and specific
        // versions that a fuzzy name lookup would miss.
        string Ident(CardModel c) => !Blank(c.SetCode) && !Blank(c.CollectorNumber)
            ? $"\"{c.Name}\" [set={c.SetCode} cn={c.CollectorNumber}]"
            : $"\"{c.Name}\" [by name]";

        progress?.Report($"Looking up {groups.Count} card(s) on Scryfall…");
        var refs = groups.Select(g =>
        {
            var c = g.First().Card;
            return (!Blank(c.SetCode) && !Blank(c.CollectorNumber))
                ? new ScryfallClient.CardRef(null, c.SetCode, c.CollectorNumber)
                : new ScryfallClient.CardRef(c.Name, null, null);
        }).ToList();
        log.Add($"refs: {refs.Count(r => r.Set != null)} by set+cn, {refs.Count(r => r.Name != null)} by name");

        // A failed chunk only loses its own cards (they fall through to the single lookups below).
        var chunkFailures = new List<string>();
        var matches = await client.LookupCollectionAsync(refs, ct, chunkFailures);
        foreach (var f in chunkFailures) log.Add($"batch collection chunk FAILED: {f}");
        log.Add($"batch returned {matches.Count} card(s)");
        percent?.Report(0.4);

        var bySetCn = new Dictionary<string, List<CardModel>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<CardModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var faces in matches)
        {
            if (faces.Count == 0) continue;
            var f = faces[0];
            if (!Blank(f.SetCode) && !Blank(f.CollectorNumber))
                bySetCn.TryAdd(f.SetCode + "|" + f.CollectorNumber, faces);
            // Index by the returned name AND (for DFCs) each face name, so "Front // Back" or "Front" both match.
            foreach (var face in faces)
                if (!Blank(face.Name)) byName.TryAdd(face.Name, faces);
        }

        var unresolved = new List<IGrouping<string, ImportedCard>>();
        foreach (var group in groups)
        {
            var c = group.First().Card;
            List<CardModel>? faces = null;
            if (!Blank(c.SetCode) && !Blank(c.CollectorNumber))
                bySetCn.TryGetValue(c.SetCode + "|" + c.CollectorNumber, out faces);
            if (faces == null) byName.TryGetValue(c.Name, out faces);
            // Last resort within the batch: a DFC whose imported name is "Front // Back" — match the front.
            if (faces == null && c.Name.Contains("//"))
                byName.TryGetValue(c.Name.Split("//")[0].Trim(), out faces);
            if (faces != null) { ApplyFaces(group, faces); log.Add($"batch OK  {Ident(c)} -> {faces[0].Name}"); }
            else unresolved.Add(group);
        }

        // Phase 1b: fuzzy single lookups only for the stragglers the batch couldn't match (typos, cards
        // whose printing hint was off). Usually a handful, so the ~10/s throttle barely matters.
        int fi = 0;
        foreach (var group in unresolved)
        {
            ct.ThrowIfCancellationRequested();
            var c = group.First().Card;
            var name = c.Name;
            progress?.Report($"Looking up {name}…");
            percent?.Report(0.4 + (double)(++fi) / Math.Max(1, unresolved.Count) * 0.1);
            try
            {
                var faces = await client.LookupFacesAsync(name, ct);
                if (faces.Count > 0) { ApplyFaces(group, faces.ToList()); log.Add($"fuzzy OK  {Ident(c)} -> {faces[0].Name}"); }
                else { notFound.Add(name); log.Add($"NOT FOUND {Ident(c)} (no fuzzy match)"); }
            }
            catch (ScryfallException ex) { notFound.Add($"{name} ({ex.Message})"); log.Add($"NOT FOUND {Ident(c)} ({ex.Message})"); }
        }

        // Phase 1c: a meld part's back is the melded card (Bruna/Gisela → Brisela) — fetched once per melded card.
        foreach (var byResult in meldJobs.GroupBy(j => j.part.MeldResultUrl))
        {
            ct.ThrowIfCancellationRequested();
            CardModel? melded = null;
            try { melded = (await client.LookupUriAsync(byResult.Key, ct)).FirstOrDefault(); }
            catch (ScryfallException ex) { log.Add($"meld result FAILED {byResult.Key}: {ex.Message}"); }
            foreach (var (card, part, halfGiven) in byResult)
            {
                if (card.IsMeld && card.BackFace is { } own)
                {
                    // A CSV meld row: what the list wrote for the melded card stays; Scryfall fills the rest.
                    if (!Blank(part.MeldWith) && Blank(card.MeldWith)) card.MeldWith = part.MeldWith;
                    if (melded == null) continue;
                    FillBlanks(own, melded, canonicalName: Blank(own.Name));
                    if (!halfGiven) card.MeldHalf = CardDetailsFill.MeldHalfFor(part, melded);
                    log.Add($"meld  \"{card.Name}\" -> back is the {card.MeldHalf} half of \"{own.Name}\" (filled from Scryfall)");
                    if (downloadArt && Blank(own.ArtPath) && !Blank(melded.ArtUrl)) artJobs.Add((own, melded.ArtUrl));
                    continue;
                }
                if (!CardDetailsFill.AttachMeld(card, part, melded)) continue;
                log.Add($"meld  \"{card.Name}\" -> back is the {card.MeldHalf} half of \"{melded!.Name}\"");
                if (card.BackFace is { } mb && downloadArt && !Blank(mb.ArtUrl)) artJobs.Add((mb, mb.ArtUrl));
            }
        }

        log.Add($"--- {found} filled, {notFound.Count} not found ---");
        try { File.WriteAllLines(Path.Combine(AppPaths.DataDir, "last-import-log.txt"), log); } catch { /* logging is best-effort */ }

        // Phase 2: parallel art downloads from the CDN (bounded concurrency), sharing one file per URL.
        if (artJobs.Count > 0)
        {
            var byUrl = artJobs.GroupBy(j => j.url).ToList();
            int artDone = 0;
            using var sem = new SemaphoreSlim(6);
            var tasks = byUrl.Select(async grp =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    string? path = null;
                    try { path = await ImageIntake.DownloadAsync(grp.Key, ct); }
                    catch { /* keep the card(s) without art rather than failing the batch */ }
                    if (path != null)
                        foreach (var (card, _) in grp)
                        {
                            card.ArtPath = path;
                            CardDetailsFill.SplitSharedArt(card);   // a split card's halves each get their side
                        }
                }
                finally
                {
                    sem.Release();
                    int d = Interlocked.Increment(ref artDone);
                    progress?.Report($"Downloading art {d}/{byUrl.Count}…");
                    percent?.Report(0.5 + (double)d / byUrl.Count * 0.5);   // art fills the second half
                }
            }).ToList();
            await Task.WhenAll(tasks);
        }

        progress?.Report($"Scryfall: {found} found, {notFound.Count} not found.");
        return new FillReport(found, filled, notFound);
    }

    /// <summary>Renders every card to a PNG (at 2x) in outDir. Filenames are index_slug.png.</summary>
    public static BatchExportResult ExportAll(
        IReadOnlyList<CardModel> cards,
        IReadOnlyList<Template> templates,
        string outDir,
        SymbolService symbols,
        IProgress<string>? progress = null,
        IProgress<double>? percent = null)
    {
        if (templates.Count == 0)
            return new BatchExportResult(0, new List<string> { "No templates available to render with." });

        Directory.CreateDirectory(outDir);
        var renderer = new CardRenderer(symbols);

        // Tolerate duplicate template names (last one wins) instead of throwing.
        var byName = new Dictionary<string, Template>();
        foreach (var t in templates) byName[t.Name] = t;
        var fallback = templates[0];

        int exported = 0;
        var errors = new List<string>();
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            try
            {
                Template TemplateFor(CardModel c) =>
                    (c.TemplateName is { Length: > 0 } n && byName.TryGetValue(n, out var t)) ? t : fallback;

                var bmp = CardExporter.AtCardSize(renderer.RenderToBitmap(card, TemplateFor(card), supersample: 2));
                var path = Path.Combine(outDir, $"{i + 1:000}_{TextUtil.Slug(card.Name)}.png");
                CardExporter.SavePng(bmp, path);
                exported++;

                // Double-faced: write the back alongside as "NNN_slug-back.png".
                if (card.BackFace is { } back)
                {
                    var backBmp = CardExporter.AtCardSize(renderer.RenderToBitmap(back, TemplateFor(back), supersample: 2));
                    CardExporter.SavePng(backBmp, Path.Combine(outDir, $"{i + 1:000}_{TextUtil.Slug(card.Name)}-back.png"));
                }
                progress?.Report($"Exported {i + 1}/{cards.Count}: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                errors.Add($"{card.Name}: {ex.Message}");
            }
            percent?.Report((double)(i + 1) / cards.Count);
        }
        return new BatchExportResult(exported, errors);
    }

    /// <summary>Flattens cards into printable faces for a single-sided sheet: each card's front followed by
    /// its back face (if any) as its own slot — so both sides of a double-faced card can be cut out. (The
    /// double-sided sheet instead pairs the real back behind the front, so it doesn't use this.)</summary>
    public static List<CardModel> ExpandFaces(IReadOnlyList<CardModel> cards)
    {
        var result = new List<CardModel>();
        foreach (var c in cards)
        {
            result.Add(c);
            if (c.BackFace is { } back) result.Add(back);
        }
        return result;
    }

    /// <summary>Fills any blank fields of <paramref name="target"/> from <paramref name="src"/>.</summary>
    public static void FillBlanks(CardModel target, CardModel src, bool canonicalName)
    {
        if (canonicalName && !Blank(src.Name)) target.Name = src.Name;
        if (Blank(target.ManaCost)) target.ManaCost = src.ManaCost;
        if (Blank(target.TypeLine)) target.TypeLine = src.TypeLine;
        if (Blank(target.RulesText)) target.RulesText = src.RulesText;
        if (Blank(target.Power)) target.Power = src.Power;
        if (Blank(target.Toughness)) target.Toughness = src.Toughness;
        if (Blank(target.Loyalty)) target.Loyalty = src.Loyalty;
        if (Blank(target.Defense)) target.Defense = src.Defense;
        if (Blank(target.SetCode)) target.SetCode = src.SetCode;
        if (Blank(target.CollectorNumber)) target.CollectorNumber = src.CollectorNumber;
        if (Blank(target.Rarity)) target.Rarity = src.Rarity;
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
