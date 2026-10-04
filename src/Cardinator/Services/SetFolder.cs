using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Makes a saved project's folder self-contained: copies each card's external images (art AND set symbol)
/// into the set's <c>art</c> subfolder and repoints the cards at the copies, so the folder can be moved,
/// zipped or shared and still render. Pure file I/O (no UI), so it's unit-testable with a temp folder.
/// </summary>
public static class SetFolder
{
    /// <summary>
    /// Copies every card's external art and set-symbol image into <c>&lt;projectFolder&gt;/art</c>, repointing
    /// the cards at the copies. Images already inside the folder are left alone (re-saving never duplicates),
    /// and a file shared by several cards (e.g. one set symbol for the whole set) is copied only once.
    /// Returns the number of images that exist but could NOT be copied in (they stay absolute and won't travel).
    /// </summary>
    public static int LocalizeImages(IReadOnlyList<CardModel> cards, string projectFolder)
    {
        var artDir = Path.Combine(projectFolder, "art");
        string artRoot;
        try { Directory.CreateDirectory(artDir); artRoot = Path.GetFullPath(artDir) + Path.DirectorySeparatorChar; }
        catch { return 0; }

        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source full → dest
        int stranded = 0;

        string? Localize(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            string full;
            try { full = Path.GetFullPath(p); } catch { return p; }
            if (full.StartsWith(artRoot, StringComparison.OrdinalIgnoreCase)) return p;   // already in this set
            if (!File.Exists(full)) return p;                                             // missing → art-blank check handles it
            if (copied.TryGetValue(full, out var existing)) return existing;              // shared image → one copy

            var dest = Path.Combine(artDir,
                ImageIntake.UniqueFileName(Path.GetFileNameWithoutExtension(full), Path.GetExtension(full)));
            try { File.Copy(full, dest, overwrite: false); copied[full] = dest; return dest; }
            catch { stranded++; return p; }   // couldn't copy — keep the original path (won't travel)
        }

        foreach (var c in cards)
        {
            c.ArtPath = Localize(c.ArtPath) ?? "";
            c.SetSymbolPath = Localize(c.SetSymbolPath) ?? "";
        }
        return stranded;
    }

    /// <summary>True if <paramref name="folder"/> already holds a <c>.cardinator</c> project other than
    /// <paramref name="thisProjectPath"/> — used to warn before two sets share one folder's art/ and out/.</summary>
    public static bool ContainsOtherProject(string folder, string? thisProjectPath)
    {
        try
        {
            var self = string.IsNullOrEmpty(thisProjectPath) ? null : Path.GetFullPath(thisProjectPath);
            return Directory.EnumerateFiles(folder, "*.cardinator").Any(f =>
                self == null || !string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>How many cards point at an art file that doesn't exist — for a load-time "N cards are missing
    /// art" summary (e.g. a set unzipped without its art/ folder).</summary>
    public static int CountMissingArt(IEnumerable<CardModel> cards)
    {
        int missing = 0;
        foreach (var c in cards)
        {
            if (string.IsNullOrWhiteSpace(c.ArtPath)) continue;
            try { if (!File.Exists(Path.GetFullPath(c.ArtPath))) missing++; } catch { missing++; }
        }
        return missing;
    }
}
