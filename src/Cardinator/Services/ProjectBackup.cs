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

    /// <summary>The folder a loaded project's relative image paths should resolve against. Normally the
    /// file's own folder — but a file opened out of a <c>backups/</c> folder belongs to the SET one level up,
    /// so its relative art ("art\pic.png") must resolve against the set folder and not against
    /// <c>backups\art\pic.png</c> (which doesn't exist). Keeps Restore from breaking every art link.</summary>
    public static string ArtRootFor(string projectFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? "";
        var leaf = Path.GetFileName(dir);
        if (string.Equals(leaf, "backups", System.StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>Existing backups for a project, most-recent (by write time) first.</summary>
    public static IReadOnlyList<string> ListBackups(string projectPath)
    {
        var dir = BackupDir(projectPath);
        if (!Directory.Exists(dir)) return System.Array.Empty<string>();

        var name = Path.GetFileNameWithoutExtension(projectPath);
        var ext = Path.GetExtension(projectPath);
        // Match only THIS project's backups: "<name>.<timestamp>[-n]<ext>". The glob alone would also match
        // a sibling project whose name starts with ours plus a dot ("MySet" vs "MySet.v2"), and pruning that
        // combined pool would delete the sibling's safety net.
        var stamped = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(name) + @"\.\d{8}-\d{6}(-\d+)?"
            + System.Text.RegularExpressions.Regex.Escape(ext) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir, $"{name}.*{ext}")
            .Where(p => stamped.IsMatch(Path.GetFileName(p)))
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
