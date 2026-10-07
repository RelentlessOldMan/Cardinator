using System.IO;
using System.Reflection;

namespace Cardinator.Services;

/// <summary>
/// Extracts the bundled sample TEMPLATES (raster custom frames = frame.png + template.json) into
/// CardinatorData/templates on first run, so they show up as built-in frames without any procedural
/// drawing. A missing file is written; a file a newer version ships differently is refreshed — but only
/// when it's still exactly what an earlier version wrote (recorded in <see cref="ManifestName"/>), so a frame
/// the user changed is never overwritten. Idempotent and cheap after the first call.
/// </summary>
public static class SampleTemplates
{
    // Resource names look like "Cardinator.Assets.templates.<slug>.frame.png" / ".template.json".
    private const string Marker = ".templates.";

    /// <summary>In the templates folder: each extracted file (by its path there) and the hash of the bytes this app
    /// last wrote to it. A file whose hash still matches was left alone by the user, so an upgrade may replace it.</summary>
    internal const string ManifestName = ".bundled-frames.json";
    private static bool _done;
    private static readonly object _lock = new();

    /// <summary>Splits a resource's "&lt;slug&gt;" part into the template folder and an optional variant
    /// subfolder: a frame's flip/landscape versions live in <c>Assets/templates/&lt;slug&gt;/flip/</c>, which
    /// MSBuild names "&lt;slug&gt;.flip.frame.png".</summary>
    private static (string slug, string? variant) SplitVariant(string slug)
    {
        foreach (var v in TemplateService.VariantKeys)
            if (slug.EndsWith("." + v, System.StringComparison.OrdinalIgnoreCase))
                return (slug[..^(v.Length + 1)], v);
        return (slug, null);
    }

    /// <summary>The slugs of the templates bundled in the exe (e.g. the pcc_* alchemy frames). Used to
    /// protect them (and the procedural built-ins) from deletion in the UI.</summary>
    public static IReadOnlyCollection<string> BundledSlugs()
    {
        var slugs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames())
            {
                int i = res.IndexOf(Marker, System.StringComparison.Ordinal);
                if (i < 0) continue;
                var rest = res[(i + Marker.Length)..];

                string file;
                if (rest.EndsWith(".frame.png", System.StringComparison.OrdinalIgnoreCase)) file = "frame.png";
                else if (rest.EndsWith(".template.json", System.StringComparison.OrdinalIgnoreCase)) file = "template.json";
                else continue;
                var slug = SplitVariant(rest[..^(file.Length + 1)]).slug;
                if (slug.Length > 0) slugs.Add(slug);
            }
        }
        catch { /* best-effort; an empty set just means nothing extra is protected */ }
        return slugs;
    }

    /// <summary>Writes any bundled template files that aren't already on disk. Safe to call repeatedly.</summary>
    public static void EnsureExtracted()
    {
        if (_done) return;
        lock (_lock)
        {
            if (_done) return;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var manifestPath = System.IO.Path.Combine(AppPaths.TemplatesDir, ManifestName);
                var manifest = ReadManifest(manifestPath);
                bool manifestChanged = false;
                foreach (var res in asm.GetManifestResourceNames())
                {
                    int i = res.IndexOf(Marker, System.StringComparison.Ordinal);
                    if (i < 0) continue;
                    var rest = res[(i + Marker.Length)..];   // "<slug>.frame.png" or "<slug>.template.json"

                    string file;
                    if (rest.EndsWith(".frame.png", System.StringComparison.OrdinalIgnoreCase)) file = "frame.png";
                    else if (rest.EndsWith(".template.json", System.StringComparison.OrdinalIgnoreCase)) file = "template.json";
                    else continue;
                    var (slug, variant) = SplitVariant(rest[..^(file.Length + 1)]);   // strip ".frame.png" / ".template.json"
                    if (slug.Length == 0) continue;

                    var dir = System.IO.Path.Combine(AppPaths.TemplatesDir, slug);
                    if (variant != null) dir = System.IO.Path.Combine(dir, variant);
                    var dest = System.IO.Path.Combine(dir, file);
                    var key = System.IO.Path.GetRelativePath(AppPaths.TemplatesDir, dest).Replace('\\', '/');
                    try
                    {
                        byte[] bundled;
                        using (var stream = asm.GetManifestResourceStream(res))
                        {
                            if (stream == null) continue;
                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            bundled = ms.ToArray();
                        }
                        var bundledHash = Hash(bundled);
                        if (File.Exists(dest))
                        {
                            var onDisk = Hash(File.ReadAllBytes(dest));
                            if (onDisk == bundledHash)
                            {
                                // Up to date — remember it as ours, so a later version may refresh it.
                                if (!manifest.TryGetValue(key, out var known) || known != onDisk) { manifest[key] = onDisk; manifestChanged = true; }
                                continue;
                            }
                            // Different: refresh only what an earlier version wrote and nobody has touched since.
                            if (!manifest.TryGetValue(key, out var written) || written != onDisk) continue;
                        }
                        Directory.CreateDirectory(dir);
                        IoUtil.AtomicWriteBytes(dest, bundled);
                        manifest[key] = bundledHash;
                        manifestChanged = true;
                    }
                    catch { /* skip this one; a missing frame just won't appear in the list */ }
                }
                if (manifestChanged)
                    try { IoUtil.AtomicWriteText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest)); } catch { /* best effort */ }
            }
            catch { /* never let template extraction block startup */ }
            _done = true;
        }
    }

    /// <summary>Forgets that extraction ran this session (tests run it again against changed files).</summary>
    internal static void ResetForTests() { lock (_lock) _done = false; }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
        }
        catch { /* unreadable: start over — files then only get recorded, never replaced, until it's rebuilt */ }
        return new Dictionary<string, string>();
    }

    private static string Hash(byte[] bytes) => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
