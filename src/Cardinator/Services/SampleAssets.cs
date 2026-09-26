using System.IO;
using System.Reflection;

namespace Cardinator.Services;

/// <summary>
/// Extracts the bundled sample artwork (embedded in the exe) into CardinatorData/samples on first run,
/// so the starter cards render with real art and the user has a small art library to pick from. Idempotent
/// and cheap after the first call.
/// </summary>
public static class SampleAssets
{
    private const string Marker = ".samples.";   // resource names look like "Cardinator.Assets.samples.<file>"
    private static bool _done;
    private static readonly object _lock = new();

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
