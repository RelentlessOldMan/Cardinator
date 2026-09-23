using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace Cardinator.Services;

/// <summary>
/// Brings external images into the project: saves a pasted clipboard bitmap or downloads an
/// image URL into the local art cache, returning a stable local file path the card can point at.
/// The path/extension logic is kept pure here so it can be unit-tested without a UI or network.
/// </summary>
public static class ImageIntake
{
    public static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>True if the text looks like an http(s) URL we could download.</summary>
    public static bool IsHttpUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>True if the path/URL ends in a known raster image extension.</summary>
    public static bool LooksLikeImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path.Split('?')[0]).ToLowerInvariant();
        return ImageExtensions.Contains(ext);
    }

    /// <summary>Choose a file extension from a content-type, falling back to the URL, then .png.</summary>
    public static string ExtensionFor(string? contentType, string? url)
    {
        var ct = contentType?.ToLowerInvariant() ?? "";
        if (ct.Contains("png")) return ".png";
        if (ct.Contains("jpeg") || ct.Contains("jpg")) return ".jpg";
        if (ct.Contains("gif")) return ".gif";
        if (ct.Contains("bmp")) return ".bmp";
        if (ct.Contains("webp")) return ".webp";
        var ext = Path.GetExtension((url ?? "").Split('?')[0]).ToLowerInvariant();
        return ImageExtensions.Contains(ext) ? ext : ".png";
    }

    /// <summary>A collision-resistant local filename (no directory) for a given base + extension.</summary>
    public static string UniqueFileName(string? baseName, string ext)
    {
        var clean = TextUtil.SafeFileName(baseName, "art");
        if (!ext.StartsWith('.')) ext = "." + ext;
        return clean + "-" + ShortId() + ext;
    }

    /// <summary>Saves a bitmap into the art cache as a PNG and returns its full path.</summary>
    public static string SaveBitmap(BitmapSource bitmap, string? baseName = null)
    {
        var path = Path.Combine(AppPaths.ArtCacheDir, UniqueFileName(baseName ?? "pasted", ".png"));
        CardExporter.SavePng(bitmap, path);
        return path;
    }

    /// <summary>Downloads an image URL into the art cache and returns its full path.</summary>
    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "Cardinator/1.0");
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        // Guard against links that return a web page (HTML) instead of an actual image.
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        bool isImage = (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                       || LooksLikeImagePath(url);
        if (!isImage)
            throw new InvalidOperationException("That link didn't return an image file.");

        var ext = ExtensionFor(mediaType, url);
        var baseName = Path.GetFileNameWithoutExtension(url.Split('?')[0]);
        var path = Path.Combine(AppPaths.ArtCacheDir, UniqueFileName(baseName, ext));
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    private static string ShortId() => Guid.NewGuid().ToString("N")[..8];
}
