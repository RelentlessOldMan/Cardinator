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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
