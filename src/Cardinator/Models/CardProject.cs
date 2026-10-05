using System.IO;
using System.Text.Json;

namespace Cardinator.Models;

/// <summary>
/// A set of cards worked on together — the unit of save/load and batch export.
/// Persisted as a single .cardinator (JSON) file.
/// </summary>
public sealed class CardProject
{
    public string Name { get; set; } = "Untitled Project";

    /// <summary>Base folder used to resolve relative art paths on import (optional).</summary>
    public string ArtBaseDir { get; set; } = "";

    /// <summary>Template applied to imported cards that don't specify one.</summary>
    public string DefaultTemplate { get; set; } = "";

    /// <summary>The set's shared defaults (set code / rarity / copyright / artist / symbol) applied to new
    /// and imported cards (W1). Always present so callers never null-check it.</summary>
    public SetProfile Profile { get; set; } = new();

    public List<CardModel> Cards { get; set; } = new();

    /// <summary>The on-disk format this file was written with (0 = pre-versioning / older than 1.1.2).
    /// Lets future versions branch migration logic; readers must still tolerate any value.</summary>
    public int FormatVersion { get; set; }

    /// <summary>The format version the current build writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>True when this file was written by a NEWER Cardinator than the one reading it. The file
    /// still loads (tolerant reads are the compatibility promise), but anything this build doesn't know
    /// about is dropped on the next save — so the user is warned before they overwrite their own work.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFromNewerVersion => FormatVersion > CurrentFormatVersion;

    // Shared across all persisted types — see JsonCompat for the backward-compatibility policy.
    private static JsonSerializerOptions JsonOpts => Cardinator.Services.JsonCompat.Options;

    public static CardProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<CardProject>(json, JsonOpts)
               ?? throw new InvalidDataException($"Could not parse project: {path}");

        // Guard against a malformed file with a null/absent cards array or null entries.
        project.Cards = project.Cards?.Where(c => c != null).ToList() ?? new List<CardModel>();
        foreach (var c in project.Cards) c.CoalesceNullStrings();   // tolerate hand-edited nulls (see CardModel)
        project.Name ??= "Untitled Project";
        project.ArtBaseDir ??= "";
        project.DefaultTemplate ??= "";
        project.Profile ??= new SetProfile();
        return project;
    }

    public void Save(string path)
    {
        FormatVersion = CurrentFormatVersion;   // stamp what wrote this file
        // Atomic write: a crash/disk-full while overwriting must never corrupt the user's whole project.
        Cardinator.Services.IoUtil.AtomicWriteText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    // --- portable art paths (per-set folder) -----------------------------------
    // A saved set is a folder holding the project file + art/ + out/. Art is stored RELATIVE to that folder
    // so the whole set can be moved/zipped/shared; it's resolved back to absolute when loaded. These are pure
    // string transforms (no I/O) so they're easy to unit-test.

    /// <summary>The art path to persist: relative to <paramref name="projectFolder"/> when the art lives inside
    /// it (e.g. "art\foo.png"); otherwise the path is left as-is (absolute, or already relative/empty).</summary>
    public static string RelativeArtPath(string? artPath, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(artPath)) return artPath ?? "";
        try
        {
            if (!Path.IsPathRooted(artPath)) return artPath;   // already relative — keep
            var full = Path.GetFullPath(artPath);
            var root = Path.GetFullPath(projectFolder);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, full)
                : artPath;
        }
        catch { return artPath; }
    }

    /// <summary>The absolute art path to use in memory: a relative stored path is resolved against
    /// <paramref name="projectFolder"/>; an absolute (or empty) path is returned unchanged.</summary>
    public static string ResolveArtPath(string? artPath, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(artPath)) return artPath ?? "";
        try
        {
            return Path.IsPathRooted(artPath)
                ? artPath
                : Path.GetFullPath(Path.Combine(projectFolder, artPath));
        }
        catch { return artPath; }
    }

    /// <summary>Rewrites a card's image paths (art AND set symbol) relative to the set folder, for saving a
    /// portable project. Both are set-identity images that must travel with the folder. A double-faced
    /// card's back face is rewritten too, so its art travels with the set like the front's.</summary>
    public static void MakeArtRelative(CardModel card, string projectFolder)
    {
        foreach (var face in card.Faces())
        {
            face.ArtPath = RelativeArtPath(face.ArtPath, projectFolder);
            face.SetSymbolPath = RelativeArtPath(face.SetSymbolPath, projectFolder);
        }
    }

    /// <summary>Resolves a card's image paths (art AND set symbol) back to absolute after loading — both
    /// faces of a double-faced card.</summary>
    public static void ResolveArt(CardModel card, string projectFolder)
    {
        foreach (var face in card.Faces())
        {
            face.ArtPath = ResolveArtPath(face.ArtPath, projectFolder);
            face.SetSymbolPath = ResolveArtPath(face.SetSymbolPath, projectFolder);
        }
    }
}
