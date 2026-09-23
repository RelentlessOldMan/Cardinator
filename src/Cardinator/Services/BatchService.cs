using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

public sealed record FillReport(int Found, int Filled, List<string> NotFound, List<CardModel> ExtraBackFaces);
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
        CancellationToken ct = default)
    {
        var client = new ScryfallClient();
        int found = 0, filled = 0;
        var notFound = new List<string>();
        var extraBacks = new List<CardModel>();

        int i = 0;
        foreach (var item in cards)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (!item.NeedsLookup) continue;

            var name = item.Card.Name;
            progress?.Report($"Looking up {i}/{cards.Count}: {name}");
            IReadOnlyList<CardModel> faces;
            try
            {
                faces = await client.LookupFacesAsync(name, ct);
            }
            catch (ScryfallException ex)
            {
                notFound.Add($"{name} ({ex.Message})");
                continue;
            }

            if (faces.Count == 0) { notFound.Add(name); continue; }

            found++;
            FillBlanks(item.Card, faces[0], canonicalName: true);
            if (downloadArt) await TryDownloadArt(item.Card, faces[0].ArtUrl);
            filled++;

            // Double-faced card: add the back as an extra card sharing the front's template.
            for (int f = 1; f < faces.Count; f++)
            {
                var back = faces[f];
                back.TemplateName = item.Card.TemplateName;
                if (downloadArt) await TryDownloadArt(back, back.ArtUrl);
                extraBacks.Add(back);
            }
        }

        progress?.Report($"Scryfall: {found} found, {notFound.Count} not found.");
        return new FillReport(found, filled, notFound, extraBacks);
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
                var template = (card.TemplateName is { Length: > 0 } n && byName.TryGetValue(n, out var t))
                    ? t : fallback;
                var bmp = renderer.RenderToBitmap(card, template, supersample: 2);
                var path = Path.Combine(outDir, $"{i + 1:000}_{TextUtil.Slug(card.Name)}.png");
                CardExporter.SavePng(bmp, path);
                exported++;
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
        if (Blank(target.SetCode)) target.SetCode = src.SetCode;
        if (Blank(target.CollectorNumber)) target.CollectorNumber = src.CollectorNumber;
        if (Blank(target.Rarity)) target.Rarity = src.Rarity;
    }

    /// <summary>Downloads Scryfall art into a card that has none yet (best-effort).</summary>
    private static async Task TryDownloadArt(CardModel card, string artUrl)
    {
        if (!Blank(card.ArtPath) || string.IsNullOrWhiteSpace(artUrl)) return;
        try { card.ArtPath = await ImageIntake.DownloadAsync(artUrl); }
        catch { /* keep the card without art rather than failing the batch */ }
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
