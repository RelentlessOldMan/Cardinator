using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// The one place a set is written to disk, in the one order that keeps the user's work safe:
/// <list type="number">
/// <item>copy external images into the set's <c>art</c> folder so the folder is self-contained,</item>
/// <item>serialize with image paths made RELATIVE to the set folder (resolved back on load),</item>
/// <item>copy the previous good file aside into <c>backups/</c> — <b>before</b> anything overwrites it,</item>
/// <item>write atomically, so a crash or full disk can never truncate a good file.</item>
/// </list>
/// Step 3 landing after step 4 would back up the NEW bytes and quietly destroy the safety net, which is
/// why this sequence lives in one testable place rather than inline in the save handler.
/// </summary>
public static class ProjectWriter
{
    /// <summary>Writes the set to <paramref name="path"/>. Returns how many images exist but could not be
    /// copied into the set folder (they stay absolute and won't travel with it).</summary>
    public static int Write(string path, IReadOnlyList<CardModel> cards, string name,
        string artBaseDir, string defaultTemplate, SetProfile profile, string? timestamp = null)
    {
        var projFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // 1. Self-contained: external art + set symbols (both faces) are copied into <set>\art.
        int stranded = SetFolder.LocalizeImages(cards, projFolder, profile);
        try { Directory.CreateDirectory(Path.Combine(projFolder, "out")); } catch { /* best effort */ }

        // 2. Portable: the set symbol in the profile travels with the folder like card art.
        var savedProfile = profile.Clone();
        savedProfile.SetSymbolPath = CardProject.RelativeArtPath(savedProfile.SetSymbolPath, projFolder);

        var project = new CardProject
        {
            Name = name,
            ArtBaseDir = artBaseDir,
            DefaultTemplate = defaultTemplate,
            Profile = savedProfile,
            Cards = cards.Select(c => CloneWithRelativeArt(c, projFolder)).ToList(),
        };

        // 3. Roll the PREVIOUS good file aside, then 4. write atomically. Order matters: see the summary.
        ProjectBackup.BackupExisting(path, timestamp ?? DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        project.Save(path);
        return stranded;
    }

    /// <summary>A clone of the card whose art + set-symbol paths (both faces) are relative to the set
    /// folder, so the saved file is portable. Paths outside the folder stay absolute.</summary>
    private static CardModel CloneWithRelativeArt(CardModel card, string projFolder)
    {
        var clone = card.Clone();
        CardProject.MakeArtRelative(clone, projFolder);
        return clone;
    }
}
