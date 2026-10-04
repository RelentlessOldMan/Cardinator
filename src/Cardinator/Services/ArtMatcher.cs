using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Matches image files in a folder to cards by (normalized) filename, so a folder of downloaded
/// art can be attached to a whole project at once. Exact name matches win, then prefix matches,
/// then "contains" matches.
/// </summary>
public static class ArtMatcher
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>How a card's art was chosen: an exact name match, or a fuzzy (prefix/contains) guess
    /// that the user may want to double-check.</summary>
    public enum MatchKind { Exact, Fuzzy }

    /// <summary>One card that received art, and whether the match was exact or a fuzzy guess.</summary>
    public sealed record ArtMatch(CardModel Card, string ArtPath, MatchKind Kind);

    /// <summary>The outcome of a folder match: every card that got art, split by confidence (L2).</summary>
    public sealed record MatchReport(IReadOnlyList<ArtMatch> Matches)
    {
        public int Total => Matches.Count;
        public int Exact => Matches.Count(m => m.Kind == MatchKind.Exact);
        public int Fuzzy => Matches.Count(m => m.Kind == MatchKind.Fuzzy);
        public IReadOnlyList<ArtMatch> Fuzzies => Matches.Where(m => m.Kind == MatchKind.Fuzzy).ToList();
    }

    /// <summary>
    /// Assigns art paths to the given cards from images in <paramref name="folder"/>.
    /// Skips cards that already have art unless <paramref name="overwrite"/> is true.
    /// Returns the number of cards that received art.
    /// </summary>
    public static int MatchInto(IEnumerable<CardModel> cards, string folder, bool overwrite)
        => MatchIntoWithReport(cards, folder, overwrite).Total;

    /// <summary>
    /// Like <see cref="MatchInto"/>, but reports which cards matched exactly vs by a fuzzy guess so the
    /// UI can flag the guesses for review instead of binding them silently.
    /// </summary>
    public static MatchReport MatchIntoWithReport(IEnumerable<CardModel> cards, string folder, bool overwrite)
    {
        if (!Directory.Exists(folder)) return new(Array.Empty<ArtMatch>());

        // Sort by path (ordinal) so enumeration order — and therefore tie-breaking — is deterministic
        // across machines/runs rather than filesystem-dependent.
        var images = Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Select(f => (Path: f, Norm: Normalize(Path.GetFileNameWithoutExtension(f))))
            .Where(x => x.Norm.Length > 0)
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();
        if (images.Count == 0) return new(Array.Empty<ArtMatch>());

        // Exact-match index (the common case) so N cards × M files isn't a full quadratic scan; first
        // file wins for a duplicate normalized name (deterministic thanks to the sort above).
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var img in images)
            if (!exact.ContainsKey(img.Norm)) exact[img.Norm] = img.Path;

        var matches = new List<ArtMatch>();
        foreach (var card in cards)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(card.ArtPath)) continue;
            var cardNorm = Normalize(card.Name);
            if (cardNorm.Length == 0) continue;

            string? best = exact.TryGetValue(cardNorm, out var exactPath) ? exactPath : null;
            var kind = MatchKind.Exact;
            if (best == null)
            {
                int bestScore = 0;
                foreach (var img in images)   // fuzzy fallback only when there's no exact match
                {
                    int score = Score(cardNorm, img.Norm);
                    if (score > bestScore) { bestScore = score; best = img.Path; }
                }
                if (bestScore == 0) best = null;
                else kind = MatchKind.Fuzzy;
            }

            if (best != null)
            {
                card.ArtPath = Path.GetFullPath(best);
                matches.Add(new ArtMatch(card, card.ArtPath, kind));
            }
        }
        return new MatchReport(matches);
    }

    private static int Score(string card, string file)
    {
        if (card == file) return 4;
        // Fuzzy prefix/contains matches need a few shared characters, or a short name like
        // "X" would spuriously match almost every filename.
        int shared = Math.Min(card.Length, file.Length);
        if (shared < 3) return 0;
        if (card.StartsWith(file) || file.StartsWith(card)) return 3;
        if (card.Contains(file) || file.Contains(card)) return 2;
        return 0;
    }

    private static string Normalize(string s)
        => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
