using System.IO;

namespace Cardinator.Services;

/// <summary>
/// Resolves where Cardinator keeps its data (templates, symbol cache, samples, exports).
/// Prefers a "CardinatorData" folder next to the exe so the app is fully portable; if that
/// location isn't writable (e.g. installed under Program Files) it falls back to %APPDATA%.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _dataDir = new(ResolveDataDir);

    public static string DataDir => _dataDir.Value;
    public static string TemplatesDir => EnsureDir(Path.Combine(DataDir, "templates"));
    public static string SymbolsDir => EnsureDir(Path.Combine(DataDir, "symbols"));
    public static string SamplesDir => EnsureDir(Path.Combine(DataDir, "samples"));
    public static string OutputDir => EnsureDir(Path.Combine(DataDir, "output"));

    /// <summary>Where pasted/downloaded art is copied so cards keep a stable local path.</summary>
    public static string ArtCacheDir => EnsureDir(Path.Combine(DataDir, "art"));

    private static string ResolveDataDir()
    {
        // Environment.ProcessPath points at the real exe even for single-file publishes.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "CardinatorData");
        if (TryEnsureWritable(candidate)) return candidate;

        // Portable location isn't writable (e.g. installed under Program Files) — try %APPDATA%,
        // then a temp folder as a last resort, so the app can always start with clear behavior.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cardinator");
        if (TryEnsureWritable(appData)) return appData;

        var temp = Path.Combine(Path.GetTempPath(), "Cardinator");
        if (TryEnsureWritable(temp)) return temp;

        // Nothing is writable; return the portable candidate and let the specific failure surface
        // where it happens (with a real path in the message) rather than crashing during startup.
        return candidate;
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
