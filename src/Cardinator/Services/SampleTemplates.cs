using System.IO;
using System.Reflection;

namespace Cardinator.Services;

/// <summary>
/// Extracts the bundled sample TEMPLATES (raster custom frames = frame.png + template.json) into
/// CardinatorData/templates on first run, so they show up as built-in frames without any procedural
/// drawing. Only writes files that aren't already present, so a user's tweaks (or deletions of the
/// other files in a template) are preserved. Idempotent and cheap after the first call.
/// </summary>
public static class SampleTemplates
{
    // Resource names look like "Cardinator.Assets.templates.<slug>.frame.png" / ".template.json".
    private const string Marker = ".templates.";
    private static bool _done;
    private static readonly object _lock = new();

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
                var slug = rest[..^(file.Length + 1)];
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
                foreach (var res in asm.GetManifestResourceNames())
                {
                    int i = res.IndexOf(Marker, System.StringComparison.Ordinal);
                    if (i < 0) continue;
                    var rest = res[(i + Marker.Length)..];   // "<slug>.frame.png" or "<slug>.template.json"

                    string file;
                    if (rest.EndsWith(".frame.png", System.StringComparison.OrdinalIgnoreCase)) file = "frame.png";
                    else if (rest.EndsWith(".template.json", System.StringComparison.OrdinalIgnoreCase)) file = "template.json";
                    else continue;
                    var slug = rest[..^(file.Length + 1)];   // strip ".frame.png" / ".template.json"
                    if (slug.Length == 0) continue;

                    var dir = System.IO.Path.Combine(AppPaths.TemplatesDir, slug);
                    var dest = System.IO.Path.Combine(dir, file);
                    if (File.Exists(dest)) continue;
                    try
                    {
                        using var stream = asm.GetManifestResourceStream(res);
                        if (stream == null) continue;
                        Directory.CreateDirectory(dir);
                        IoUtil.AtomicWrite(dest, tmp =>
                        {
                            using var fs = File.Create(tmp);
                            stream.CopyTo(fs);
                        });
                    }
                    catch { /* skip this one; a missing frame just won't appear in the list */ }
                }
            }
            catch { /* never let template extraction block startup */ }
            _done = true;
        }
    }
}
