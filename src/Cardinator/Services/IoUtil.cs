using System.IO;

namespace Cardinator.Services;

/// <summary>
/// Atomic-write helpers: write to a sibling temp file then move it into place, so a crash, cancellation,
/// or disk-full mid-write can never leave a truncated/corrupt file where a good one used to be. Used for
/// everything the user can't easily recreate — project files, card/template JSON, and exported images.
/// </summary>
public static class IoUtil
{
    /// <summary>Writes text to <paramref name="path"/> atomically (temp file + move).</summary>
    public static void AtomicWriteText(string path, string text)
        => AtomicWrite(path, tmp => File.WriteAllText(tmp, text));

    /// <summary>Writes bytes to <paramref name="path"/> atomically (temp file + move).</summary>
    public static void AtomicWriteBytes(string path, byte[] bytes)
        => AtomicWrite(path, tmp => File.WriteAllBytes(tmp, bytes));

    /// <summary>
    /// Copies a file that failed to load aside to a ".corrupt-backup" sibling so the user can still
    /// recover or inspect it even after re-saving over the original (L9). Idempotent — if a backup
    /// already exists it is kept (not overwritten). Returns the backup path, or null if nothing was
    /// backed up (missing file / copy failure).
    /// </summary>
    public static string? BackupCorrupt(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var backup = path + ".corrupt-backup";
            if (!File.Exists(backup)) File.Copy(path, backup);
            return backup;
        }
        catch { return null; }   // best-effort; never let backup failure mask the original error
    }

    /// <summary>Runs <paramref name="writeToTemp"/> against a sibling temp path, then moves it over the
    /// destination. On any failure the temp file is cleaned up and the original destination is left intact.</summary>
    public static void AtomicWrite(string path, Action<string> writeToTemp)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Unique temp name in the same directory so File.Move is a cheap same-volume rename.
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            writeToTemp(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
