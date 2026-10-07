using System.IO;
using System.Net.Http;
using System.Windows.Media;
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

    /// <summary>
    /// Fixes a bitmap whose alpha channel is entirely zero — the lossy DIB round-trip you get when pasting
    /// an image copied from a browser (Chrome/Edge), which otherwise saves/composites as solid black. If any
    /// pixel is non-transparent the image is returned unchanged; if every pixel is transparent the alpha is
    /// forced opaque so the real RGB shows. Pure (no clipboard/UI), so it's unit-testable.
    /// </summary>
    public static BitmapSource RepairZeroAlpha(BitmapSource src)
    {
        BitmapSource bgra = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        bgra.CopyPixels(px, stride, 0);

        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 0) { if (bgra.CanFreeze) bgra.Freeze(); return bgra; }   // alpha present → leave alone

        for (int i = 3; i < px.Length; i += 4) px[i] = 255;                        // all transparent → opaque
        var fixedBmp = BitmapSource.Create(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, px, stride);
        fixedBmp.Freeze();
        return fixedBmp;
    }

    /// <summary>Cuts an image into a left and a right part at <paramref name="at"/> (a fraction of its width;
    /// the middle by default), saved into the art cache as PNGs (Scryfall's split-card art holds both halves'
    /// art side by side).</summary>
    public static (string left, string right) SplitSideBySide(string path, double at = 0.5)
    {
        var img = LoadOriented(File.ReadAllBytes(path));
        int w = img.PixelWidth, h = img.PixelHeight, half = (int)Math.Round(w * Math.Clamp(at, 0.05, 0.95));
        if (half < 1 || h < 1) throw new InvalidOperationException("Image too small to split.");
        var name = Path.GetFileNameWithoutExtension(path);
        var left = new CroppedBitmap(img, new System.Windows.Int32Rect(0, 0, half, h));
        var right = new CroppedBitmap(img, new System.Windows.Int32Rect(half, 0, w - half, h));
        left.Freeze(); right.Freeze();
        return (SaveBitmap(left, name + "-left"), SaveBitmap(right, name + "-right"));
    }

    /// <summary>Decodes an image (frozen, file not held) turned the way it's meant to be seen: phone photos are
    /// stored sideways with an EXIF "orientation" tag that WPF's decoder ignores, so it's applied here.</summary>
    public static BitmapSource LoadOriented(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return Orient(img, ReadOrientation(bytes));
    }

    /// <summary>The EXIF orientation (1–8) of an encoded image; 1 (as stored) when it has none.</summary>
    public static int ReadOrientation(byte[] bytes)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes),
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (frame.Metadata is BitmapMetadata md)
                foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    if (md.ContainsQuery(q) && md.GetQuery(q) is ushort o && o is >= 1 and <= 8) return o;
        }
        catch { /* formats without EXIF metadata (PNG, GIF, BMP…) */ }
        return 1;
    }

    /// <summary>Turns/mirrors a stored image by its EXIF orientation so it displays upright.</summary>
    public static BitmapSource Orient(BitmapSource img, int orientation)
    {
        // (clockwise turn, then mirror left-right) that undoes each stored orientation
        var (turn, mirror) = orientation switch
        {
            2 => (0, true), 3 => (180, false), 4 => (180, true),
            5 => (90, true), 6 => (90, false), 7 => (270, true), 8 => (270, false),
            _ => (0, false),
        };
        if (turn == 0 && !mirror) return img;
        var t = new TransformGroup();
        if (turn != 0) t.Children.Add(new RotateTransform(turn));
        if (mirror) t.Children.Add(new ScaleTransform(-1, 1));
        var tb = new TransformedBitmap(img, t);
        tb.Freeze();
        return tb;
    }

    /// <summary>Saves a bitmap into the art cache as a PNG and returns its full path.</summary>
    public static string SaveBitmap(BitmapSource bitmap, string? baseName = null)
    {
        var path = Path.Combine(AppPaths.ArtCacheDir, UniqueFileName(baseName ?? "pasted", ".png"));
        CardExporter.SavePng(bitmap, path);
        return path;
    }

    /// <summary>
    /// Copies an interactively-selected image into the portable art cache and returns the cached path,
    /// so a saved project stays self-contained even if the user later moves or deletes the original
    /// file. Images that already live inside the app's data folder (pasted, downloaded, or previously
    /// cached art) are returned unchanged — no needless duplication. On any failure the original path is
    /// returned, so this is never worse than referencing the source directly.
    /// </summary>
    public static string EnsureLocalCopy(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return path;
            var full = Path.GetFullPath(path);

            // Already under our data dir (art cache / samples / a prior copy) — leave it in place.
            var dataRoot = Path.GetFullPath(AppPaths.DataDir);
            if (full.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, dataRoot, StringComparison.OrdinalIgnoreCase))
                return full;

            var ext = Path.GetExtension(full);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            var dest = Path.Combine(AppPaths.ArtCacheDir,
                                    UniqueFileName(Path.GetFileNameWithoutExtension(full), ext));
            Directory.CreateDirectory(AppPaths.ArtCacheDir);
            File.Copy(full, dest, overwrite: false);
            return dest;
        }
        catch
        {
            return path;   // fall back to referencing the original — no worse than before
        }
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
    internal static TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        try { return await DownloadCoreAsync(url, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own 30 s timeout (or HttpClient's), not the caller cancelling: an ordinary download failure, so
            // callers that let a real cancellation through don't mistake it for one.
            throw new InvalidOperationException("The image took too long to download.");
        }
    }

    private static async Task<string> DownloadCoreAsync(string url, CancellationToken ct)
    {
        if (!IsHttpUrl(url))
            throw new InvalidOperationException("Only http(s) image links can be downloaded.");

        // Per-request timeout that also honors the caller's cancellation.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        var tok = timeout.Token;

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, tok);
        resp.EnsureSuccessStatusCode();

        // Accept only real images: an image/* content-type, or an unknown/binary one ("application/octet-stream",
        // S3's "binary/octet-stream", a share link's download) whose bytes turn out to be a picture — checked once
        // it's downloaded. Reject e.g. text/html error pages even when the URL happens to end in ".png".
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        bool typeIsImage = mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false;
        bool typeUnknown = string.IsNullOrEmpty(mediaType) || IsBinaryType(mediaType!);
        if (!(typeIsImage || typeUnknown))
            throw new InvalidOperationException("That link didn't return an image file.");

        if (resp.Content.Headers.ContentLength is long declared && declared > MaxDownloadBytes)
            throw new InvalidOperationException("That image is too large to download.");

        var ext = ExtensionFor(typeIsImage ? mediaType : null, url);
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
            if (!typeIsImage)
            {
                // A binary download is kept only if it's a picture, named for what it really is.
                var sniffed = SniffImageExtension(tmp) ?? throw new InvalidOperationException("That link didn't return an image file.");
                path = Path.ChangeExtension(path, sniffed);
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

    /// <summary>A content-type that says only "some bytes": application/octet-stream, binary/octet-stream and kin.</summary>
    private static bool IsBinaryType(string mediaType)
        => mediaType.EndsWith("/octet-stream", StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals("application/binary", StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals("application/x-binary", StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals("application/force-download", StringComparison.OrdinalIgnoreCase);

    /// <summary>The image extension a file's first bytes announce (PNG, JPEG, GIF, BMP, WebP), or null.</summary>
    internal static string? SniffImageExtension(string file)
    {
        var h = new byte[12];
        int n;
        using (var fs = File.OpenRead(file)) n = fs.Read(h, 0, h.Length);
        if (n >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return ".png";
        if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (n >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return ".gif";
        if (n >= 2 && h[0] == 'B' && h[1] == 'M') return ".bmp";
        if (n >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        return null;
    }
}
