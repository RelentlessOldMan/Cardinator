using System.Collections.Concurrent;
using System.IO;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Whether a card's art can actually be drawn. A file that's there but won't decode — a half-finished download, a
/// zero-byte file, a web page saved as .png, a format this PC has no codec for — printed as a black art window while
/// every check (<c>--info</c>, Check all cards, Export) only asked whether the file existed and said "ok".
/// Decoding is cached by the file's time and size, so checking a whole set again costs nothing.
/// </summary>
public static class ArtCheck
{
    public const string Missing = "missing", Unreadable = "unreadable";

    private static readonly ConcurrentDictionary<string, (long Stamp, bool Ok)> Decoded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when <paramref name="path"/> is blank (no art) or a picture that decodes; otherwise
    /// <see cref="Missing"/> or <see cref="Unreadable"/>.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        long stamp;
        try
        {
            full = Path.GetFullPath(path);
            var fi = new FileInfo(full);
            if (!fi.Exists) return Missing;
            stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            if (fi.Length == 0) return Unreadable;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return Missing; }

        if (Decoded.TryGetValue(full, out var hit) && hit.Stamp == stamp) return hit.Ok ? null : Unreadable;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { return null; }   // locked for a moment (a sync client, antivirus), or no memory: says nothing about the picture
        bool ok;
        try { ok = ImageIntake.LoadOriented(bytes).PixelWidth > 0; }
        catch (OutOfMemoryException) { return null; }   // likewise
        catch { ok = false; }
        Decoded[full] = (stamp, ok);
        return ok ? null : Unreadable;
    }

    /// <summary>"Name's art …" lines for every face of <paramref name="cards"/> whose art is missing or can't be read.</summary>
    public static List<string> Notes(IEnumerable<CardModel> cards)
    {
        var notes = new List<string>();
        foreach (var face in cards.SelectMany(c => c.Parts()))
            if (Problem(face.ArtPath) is { } why)
                notes.Add($"{(string.IsNullOrWhiteSpace(face.Name) ? "a card" : face.Name)}'s art \"{face.ArtPath}\" "
                    + (why == Missing ? "isn't there" : "can't be read (damaged, empty, or not a picture)")
                    + " — its art window is drawn empty.");
        return notes;
    }
}
