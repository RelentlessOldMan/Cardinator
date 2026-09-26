using System.IO;
using System.Text.RegularExpressions;

namespace Cardinator.Services;

/// <summary>Small shared string helpers for turning card/template names into safe file paths.</summary>
public static class TextUtil
{
    /// <summary>Lowercase kebab slug for folder/file names: "Gold Multicolor" -> "gold-multicolor".</summary>
    public static string Slug(string? s)
    {
        var chars = (s ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');   // collapse runs in one pass
        return string.IsNullOrEmpty(slug) ? "card" : slug;
    }

    // Windows reserved device names — a file named CON.png / NUL.png etc. does not create a normal file.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A filename safe on Windows: invalid characters replaced, trailing dots/spaces removed
    /// (the filesystem strips them silently, which can collide two names), and reserved device names
    /// (CON, NUL, COM1…) prefixed so they can't hit a device instead of a file.</summary>
    public static string SafeFileName(string? name, string fallback = "card")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "")
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        // Trailing dots/spaces are silently dropped by Windows — remove them so "Foo." and "Foo" don't collide.
        clean = clean.TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(clean)) return fallback;
        // A reserved device name, with or without an extension, is unsafe — prefix it.
        var stem = clean.Contains('.') ? clean[..clean.IndexOf('.')] : clean;
        if (ReservedNames.Contains(stem)) clean = "_" + clean;
        return clean;
    }
}
