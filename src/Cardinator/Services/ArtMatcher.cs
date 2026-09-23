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
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Assigns art paths to the given cards from images in <paramref name="folder"/>.
    /// Skips cards that already have art unless <paramref name="overwrite"/> is true.
    /// Returns the number of cards that received art.
    /// </summary>
    public static int MatchInto(IEnumerable<CardModel> cards, string folder, bool overwrite)
    {
        if (!Directory.Exists(folder)) return 0;

        var images = Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => (Path: f, Norm: Normalize(Path.GetFileNameWithoutExtension(f))))
            .Where(x => x.Norm.Length > 0)
            .ToList();
        if (images.Count == 0) return 0;

        int matched = 0;
        foreach (var card in cards)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(card.ArtPath)) continue;
            var cardNorm = Normalize(card.Name);
            if (cardNorm.Length == 0) continue;

            string? best = null;
            int bestScore = 0;
            foreach (var img in images)
            {
                int score = Score(cardNorm, img.Norm);
                if (score > bestScore) { bestScore = score; best = img.Path; }
            }

            if (best != null && bestScore > 0)
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
