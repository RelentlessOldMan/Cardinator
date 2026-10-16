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
            // On disk before it replaces the old file: after a power cut the rename can otherwise be there while the
            // bytes aren't, leaving an empty set file where a good one was.
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) fs.Flush(flushToDisk: true);
            MoveOver(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    /// <summary>How long a move over a file someone else has open (OneDrive syncing it, an antivirus scan, an image
    /// viewer) is retried before giving up. Tests shorten it.</summary>
    internal static int MoveTries = 5;
    internal static TimeSpan MoveRetryPause = TimeSpan.FromMilliseconds(100);

    private static void MoveOver(string tmp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (attempt < MoveTries && IsBusy(ex))
            {
                Thread.Sleep(MoveRetryPause);
            }
        }
    }

    /// <summary>The destination is held open by another program for now (a sharing or lock violation, or a file
    /// that's being deleted), as opposed to a path that can never be written.</summary>
    internal static bool IsBusy(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,   // a pending delete, or a sync client holding it, reads as access denied
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        _ => false,
    };
}
