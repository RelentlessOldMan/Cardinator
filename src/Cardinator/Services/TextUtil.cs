using System.IO;

namespace Cardinator.Services;

/// <summary>Small shared string helpers for turning card/template names into safe file paths.</summary>
public static class TextUtil
{
    /// <summary>Lowercase kebab slug for folder/file names: "Gold Multicolor" -> "gold-multicolor".</summary>
    public static string Slug(string? s)
    {
        var chars = (s ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "card" : slug;
    }

    /// <summary>A filename with any invalid characters replaced by underscores.</summary>
    public static string SafeFileName(string? name, string fallback = "card")
    {
        var clean = new string((name ?? "")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? fallback : clean;
    }
}
