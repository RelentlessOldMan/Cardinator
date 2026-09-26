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

    /// <summary>Largest art download we'll accept, to protect against hostile/misconfigured links.</summary>
    public const long MaxDownloadBytes = 32L * 1024 * 1024;

    // A single reused HttpClient (per HttpClient guidance) — creating one per download exhausts sockets.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ScryfallClient.UserAgent);
        return http;
    }

    /// <summary>Downloads an image URL into the art cache and returns its full path. Validates scheme and
    /// content-type, enforces a size cap, and writes atomically so a failed download leaves nothing behind.</summary>
    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!IsHttpUrl(url))
            throw new InvalidOperationException("Only http(s) image links can be downloaded.");

        // Per-request timeout that also honors the caller's cancellation.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var tok = timeout.Token;

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, tok);
        resp.EnsureSuccessStatusCode();

        // Accept only real images: an image/* content-type, or an unknown/binary type with an image URL.
        // Reject e.g. text/html error pages even when the URL happens to end in ".png".
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        bool typeIsImage = mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false;
        bool typeUnknown = string.IsNullOrEmpty(mediaType)
                           || mediaType!.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
        if (!(typeIsImage || (typeUnknown && LooksLikeImagePath(url))))
            throw new InvalidOperationException("That link didn't return an image file.");

        if (resp.Content.Headers.ContentLength is long declared && declared > MaxDownloadBytes)
            throw new InvalidOperationException("That image is too large to download.");

        var ext = ExtensionFor(mediaType, url);
        var baseName = Path.GetFileNameWithoutExtension(url.Split('?')[0]);
        var path = Path.Combine(AppPaths.ArtCacheDir, UniqueFileName(baseName, ext));

        // Stream to a temp file (async, so the UI thread never blocks on the network) with a hard byte
        // ceiling — Content-Length may be absent or lie — then move into place atomically.
        Directory.CreateDirectory(AppPaths.ArtCacheDir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(tok))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), tok)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                        throw new InvalidOperationException("That image is too large to download.");
                    await dst.WriteAsync(buffer.AsMemory(0, read), tok);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
        return path;
    }

    private static string ShortId() => Guid.NewGuid().ToString("N")[..8];
}
