using System.IO;
using System.Reflection;

namespace Cardinator.Services;

/// <summary>
/// Extracts the bundled starter-card artwork (embedded in the exe) into CardinatorData/samples on first
/// run, so the single starter card renders with real art. Idempotent and cheap after the first call.
/// </summary>
public static class SampleAssets
{
    private const string Marker = ".samples.";   // resource names look like "Cardinator.Assets.samples.<file>"
    private static bool _done;
    private static readonly object _lock = new();

    /// <summary>A bundled sample picture — the starter card's — wherever it is now (a saved set copies it into its own
    /// art folder): same file name and size as one in the exe. A lookup treats it as no art of the user's own.</summary>
    public static bool IsBundled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var name = System.IO.Path.GetFileName(path);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames())
            {
                int i = res.IndexOf(Marker, System.StringComparison.Ordinal);
                if (i < 0 || !string.Equals(res[(i + Marker.Length)..], name, System.StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = asm.GetManifestResourceStream(res);
                return stream != null && File.Exists(path) && new FileInfo(path).Length == stream.Length;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Full path to a bundled sample image in the samples folder (whether or not it's extracted yet).</summary>
    public static string Path(string fileName) => System.IO.Path.Combine(AppPaths.SamplesDir, fileName);

    /// <summary>Writes any bundled sample images that aren't already on disk. Safe to call repeatedly.</summary>
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
                    var file = res[(i + Marker.Length)..];
                    var dest = Path(file);
                    if (File.Exists(dest)) continue;
                    try
                    {
                        using var stream = asm.GetManifestResourceStream(res);
                        if (stream == null) continue;
                        IoUtil.AtomicWrite(dest, tmp =>
                        {
                            using var fs = File.Create(tmp);
                            stream.CopyTo(fs);
                        });
                    }
                    catch { /* skip this one; the card just falls back to a blank art window */ }
                }
            }
            catch { /* never let sample extraction block startup */ }
            _done = true;
        }
    }
}
