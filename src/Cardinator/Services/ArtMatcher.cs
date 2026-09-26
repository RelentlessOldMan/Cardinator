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

    /// <summary>
    /// Assigns art paths to the given cards from images in <paramref name="folder"/>.
    /// Skips cards that already have art unless <paramref name="overwrite"/> is true.
    /// Returns the number of cards that received art.
    /// </summary>
    public static int MatchInto(IEnumerable<CardModel> cards, string folder, bool overwrite)
    {
        if (!Directory.Exists(folder)) return 0;

        // Sort by path (ordinal) so enumeration order — and therefore tie-breaking — is deterministic
        // across machines/runs rather than filesystem-dependent.
        var images = Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Select(f => (Path: f, Norm: Normalize(Path.GetFileNameWithoutExtension(f))))
            .Where(x => x.Norm.Length > 0)
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();
        if (images.Count == 0) return 0;

        // Exact-match index (the common case) so N cards × M files isn't a full quadratic scan; first
        // file wins for a duplicate normalized name (deterministic thanks to the sort above).
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var img in images)
            if (!exact.ContainsKey(img.Norm)) exact[img.Norm] = img.Path;

        int matched = 0;
        foreach (var card in cards)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(card.ArtPath)) continue;
            var cardNorm = Normalize(card.Name);
            if (cardNorm.Length == 0) continue;

            string? best = exact.TryGetValue(cardNorm, out var exactPath) ? exactPath : null;
            if (best == null)
            {
                int bestScore = 0;
                foreach (var img in images)   // fuzzy fallback only when there's no exact match
                {
                    int score = Score(cardNorm, img.Norm);
                    if (score > bestScore) { bestScore = score; best = img.Path; }
                }
                if (bestScore == 0) best = null;
            }

            if (best != null)
            {
                card.ArtPath = Path.GetFullPath(best);
                matched++;
            }
        }
        return matched;
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
