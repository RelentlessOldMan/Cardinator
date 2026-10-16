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

    /// <summary>On top of the most recent <see cref="DefaultKeep"/>, the first backup of each of this many
    /// days is kept, so a burst of saves (Ctrl+S habit after a mistake) can't flush the older history.</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>The folder where a project's backups live (a <c>backups/</c> sibling of the project file).</summary>
    public static string BackupDir(string projectPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "", "backups");

    /// <summary>
    /// Copies the existing on-disk project file aside into <c>backups/</c> before it is overwritten, naming
    /// the copy <c>&lt;name&gt;.&lt;timestamp&gt;&lt;ext&gt;</c>, then prunes to the most recent
    /// <paramref name="keep"/> (plus the first of each recent day). No-op if the file doesn't exist yet (first
    /// save), and no new copy when the newest backup already holds exactly these bytes — saving an unchanged
    /// set over and over must not push the real history out. Best-effort: a backup failure must never block
    /// the actual save, so it returns the backup path (or the identical existing one) or null and never throws.
    /// </summary>
    public static string? BackupExisting(string projectPath, string timestamp, int keep = DefaultKeep,
        int keepDays = DefaultKeepDays)
    {
        try
        {
            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath)) return null;

            var dir = BackupDir(projectPath);
            Directory.CreateDirectory(dir);

            var newest = ListBackups(projectPath).FirstOrDefault();
            if (newest != null && SameBytes(newest, projectPath)) return newest;

            var name = Path.GetFileNameWithoutExtension(projectPath);
            var ext = Path.GetExtension(projectPath);
            var dest = UniquePath(Path.Combine(dir, $"{name}.{timestamp}{ext}"));
            File.Copy(projectPath, dest);

            Prune(projectPath, keep, keepDays, justMade: dest);
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
        if (ProjectFileForBackup(projectFilePath) != null)
            return Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>For a file inside a set's <c>backups/</c> folder: the set file it is a backup of
    /// ("MySet.20261001-120000.cardinator" → "&lt;set&gt;\MySet.cardinator"), or "" when it is in a backups folder
    /// but not named like one of ours. Null for any other file — including a set someone simply keeps in a folder
    /// called "Backups" (no timestamp in its name, and its own art folder there, or no backups of ours beside it). Saving an opened backup
    /// must go to the set file — saving it where it lies would make backups\ the set folder and break every art link.</summary>
    public static string? ProjectFileForBackup(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(full) ?? "";
        if (!string.Equals(Path.GetFileName(dir), "backups", System.StringComparison.OrdinalIgnoreCase)) return null;
        var stamped = new System.Text.RegularExpressions.Regex(@"^(.+)\.\d{8}-\d{6}(?:-\d+)?$");
        var m = stamped.Match(Path.GetFileNameWithoutExtension(full));
        var setDir = Path.GetDirectoryName(dir) ?? dir;
        if (!m.Success)
        {
            // Not named like a backup: one only if this really is a set's backups folder — a set file above it and our
            // timestamped backups in it. A set kept in a folder called "Backups" has its own art\ there instead.
            if (Directory.Exists(Path.Combine(dir, "art"))) return null;
            bool ours = Directory.Exists(setDir) && Directory.EnumerateFiles(setDir, "*.cardinator").Any()
                && Directory.EnumerateFiles(dir).Any(f => stamped.IsMatch(Path.GetFileNameWithoutExtension(f)));
            return ours ? "" : null;
        }
        return Path.Combine(Path.GetDirectoryName(dir) ?? dir, m.Groups[1].Value + Path.GetExtension(full));
    }

    /// <summary>Existing backups for a project, the most recently made first: by the time in each one's name (when it
    /// was made). The file's own time is the set file's as it was copied — old for a set restored from a zip — so ranking
    /// by it pruned a new backup the moment it was made. A name dated in the future was made while the clock ran ahead:
    /// it's older than anything made now, so it ranks after them instead of holding the top places for good.</summary>
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
        var future = System.DateTime.Now.AddDays(1).ToString("yyyyMMdd-HHmmss");
        return Directory.EnumerateFiles(dir, $"{name}.*{ext}")
            .Where(p => stamped.IsMatch(Path.GetFileName(p)))
            .Select(p => (path: p, stamp: StampOf(p) ?? ""))
            .OrderBy(b => string.CompareOrdinal(b.stamp, future) > 0)   // the clock was ahead: after the rest
            .ThenByDescending(b => b.stamp, System.StringComparer.Ordinal)
            .ThenByDescending(b => Path.GetFileName(b.path), System.StringComparer.Ordinal)
            .Select(b => Path.GetFullPath(b.path))
            .ToList();
    }

    /// <summary>Deletes this project's backups except the most recent <paramref name="keep"/> and the FIRST
    /// backup of each of the <paramref name="keepDays"/> most recent days (by the date in its name) — the
    /// state the set was in before that day's saves, which is what you want back after a bad day.</summary>
    /// <param name="justMade">The backup this save just made: never pruned, whatever the clock says.</param>
    public static void Prune(string projectPath, int keep = DefaultKeep, int keepDays = DefaultKeepDays, string? justMade = null)
    {
        if (keep < 0) keep = 0;
        var backups = ListBackups(projectPath);
        var dailyFirsts = backups
            .Select(p => (path: p, stamp: StampOf(p)))
            .Where(b => b.stamp != null)
            .GroupBy(b => b.stamp!.Substring(0, 8))
            .OrderByDescending(g => g.Key, System.StringComparer.Ordinal)
            .Take(System.Math.Max(0, keepDays))
            .Select(g => g.OrderBy(b => b.stamp, System.StringComparer.Ordinal).First().path)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        var spare = justMade == null ? null : Path.GetFullPath(justMade);
        foreach (var old in backups.Skip(keep))
            if (!dailyFirsts.Contains(old) && !string.Equals(old, spare, System.StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { /* best effort */ }
    }

    /// <summary>"20260104-120000" or "20260104-120000-2" from "&lt;name&gt;.&lt;stamp&gt;&lt;ext&gt;"; null if absent.
    /// Sorts in the order the backups were made.</summary>
    private static string? StampOf(string backupPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(backupPath),
            @"\.(\d{8}-\d{6})(?:-(\d+))?$");
        if (!m.Success) return null;
        return m.Groups[1].Value + "-" + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1).ToString("D4");
    }

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
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
