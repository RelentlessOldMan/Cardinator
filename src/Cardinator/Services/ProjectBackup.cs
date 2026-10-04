using System.IO;

namespace Cardinator.Services;

/// <summary>
/// Keeps rolling, timestamped copies of a project file so the user's work survives a bad save, a buggy
/// build, or an upgrade problem. On each Save we copy the <em>previous</em> good file aside into a
/// <c>backups/</c> folder next to the project, then prune to the most recent N. This complements the two
/// safety layers already in place: atomic writes (<see cref="IoUtil"/>) never truncate a good file
/// mid-write, and a file that fails to load is preserved as <c>.corrupt-backup</c>.
/// </summary>
public static class ProjectBackup
{
    public const int DefaultKeep = 15;

    /// <summary>The folder where a project's backups live (a <c>backups/</c> sibling of the project file).</summary>
    public static string BackupDir(string projectPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "", "backups");

    /// <summary>
    /// Copies the existing on-disk project file aside into <c>backups/</c> before it is overwritten, naming
    /// the copy <c>&lt;name&gt;.&lt;timestamp&gt;&lt;ext&gt;</c>, then prunes to the most recent
    /// <paramref name="keep"/>. No-op if the file doesn't exist yet (first save). Best-effort: a backup
    /// failure must never block the actual save, so it returns the backup path or null and never throws.
    /// </summary>
    public static string? BackupExisting(string projectPath, string timestamp, int keep = DefaultKeep)
    {
        try
        {
            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath)) return null;

            var dir = BackupDir(projectPath);
            Directory.CreateDirectory(dir);

            var name = Path.GetFileNameWithoutExtension(projectPath);
            var ext = Path.GetExtension(projectPath);
            var dest = UniquePath(Path.Combine(dir, $"{name}.{timestamp}{ext}"));
            File.Copy(projectPath, dest);

            Prune(projectPath, keep);
            return dest;
        }
        catch { return null; }   // never let backup trouble block a save
    }

    /// <summary>Existing backups for a project, most-recent (by write time) first.</summary>
    public static IReadOnlyList<string> ListBackups(string projectPath)
    {
        var dir = BackupDir(projectPath);
        if (!Directory.Exists(dir)) return System.Array.Empty<string>();

        var name = Path.GetFileNameWithoutExtension(projectPath);
        var ext = Path.GetExtension(projectPath);
        // Match only this project's backups: "<name>.<something><ext>".
        return Directory.EnumerateFiles(dir, $"{name}.*{ext}")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, System.StringComparer.Ordinal)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Deletes all but the most recent <paramref name="keep"/> backups of this project.</summary>
    public static void Prune(string projectPath, int keep = DefaultKeep)
    {
        if (keep < 0) keep = 0;
        var backups = ListBackups(projectPath);
        foreach (var old in backups.Skip(keep))
            try { File.Delete(old); } catch { /* best effort */ }
    }

    private static string UniquePath(string basePath)
    {
        if (!File.Exists(basePath)) return basePath;
        var dir = Path.GetDirectoryName(basePath)!;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
