using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public List<CardModel> Cards { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CardProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<CardProject>(json, JsonOpts)
               ?? throw new InvalidDataException($"Could not parse project: {path}");

        // Guard against a malformed file with a null/absent cards array or null entries.
        project.Cards = project.Cards?.Where(c => c != null).ToList() ?? new List<CardModel>();
        project.Name ??= "Untitled Project";
        project.ArtBaseDir ??= "";
        project.DefaultTemplate ??= "";
        return project;
    }

    public void Save(string path)
    {
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
}
