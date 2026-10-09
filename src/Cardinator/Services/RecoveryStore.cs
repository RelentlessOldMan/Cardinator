using System.IO;
using System.Text.Json;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// The safety net for unsaved work that never reaches a Save: a copy of the open set written every minute while
/// it has unsaved changes, and again when Windows logs off, shuts down or restarts (when the app is never asked to
/// close) or the app hits an error. Each copy lives in <c>CardinatorData\recovery</c> next to a small
/// <c>.meta</c> file saying which set it belongs to; the next launch offers to open it. A clean Save, or choosing
/// "Don't save", removes the copy this session wrote — never another session's.
/// </summary>
public static class RecoveryStore
{
    /// <summary>Which set, when, and which session wrote it. <see cref="Session"/> is null in a copy written before
    /// 1.6.20, which named its session by process id alone.</summary>
    public sealed record Meta(string SetPath, string Name, DateTime SavedAt, int Pid, string? Session = null);
    public sealed record Entry(string File, Meta Meta);

    /// <summary>This run of the app. Part of every recovery file's name, so a later session working on the same set
    /// writes a file of its own and never overwrites a crashed session's copy that hasn't been dealt with yet —
    /// and a process id Windows hands out again after a restart can't make an old copy look like ours.</summary>
    private static readonly string Session = Guid.NewGuid().ToString("N")[..12];

    public static string Dir => Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "recovery")).FullName;

    /// <summary>This session's recovery file for a set (one per set file; "untitled" for a set never saved).</summary>
    public static string FileFor(string setPath)
    {
        string key;
        if (string.IsNullOrWhiteSpace(setPath)) key = "untitled";
        else
        {
            var full = Path.GetFullPath(setPath).ToUpperInvariant();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..10];
            key = TextUtil.SafeFileName(Path.GetFileNameWithoutExtension(setPath)) + "-" + hash;
        }
        return Path.Combine(Dir, $"{key}-{Session}.cardinator");
    }

    /// <summary>Writes the cards as they are (art paths absolute, nothing copied anywhere) plus the meta file.</summary>
    public static void Write(string file, string setPath, string name, IEnumerable<CardModel> cards,
        string artBaseDir, string defaultTemplate, SetProfile profile)
    {
        var project = new CardProject
        {
            Name = name,
            ArtBaseDir = artBaseDir,
            DefaultTemplate = defaultTemplate,
            Profile = profile.Clone(),
            Cards = cards.Select(c => c.Clone()).ToList(),
        };
        project.Save(file);
        IoUtil.AtomicWriteText(file + ".meta",
            JsonSerializer.Serialize(new Meta(setPath ?? "", name ?? "", DateTime.Now, Environment.ProcessId, Session)));
    }

    /// <summary>Removes a recovery file and its meta (best effort).</summary>
    public static void Delete(string file)
    {
        try { File.Delete(file); } catch { }
        try { File.Delete(file + ".meta"); } catch { }
    }

    /// <summary>Recovery files left by a session that's no longer running, newest first.</summary>
    public static List<Entry> Pending()
    {
        var list = new List<Entry>();
        string dir;
        try { dir = Dir; } catch { return list; }
        foreach (var f in Directory.EnumerateFiles(dir, "*.cardinator"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(f + ".meta"));
                if (meta == null || IsRunning(meta)) continue;   // this or another open window's live copy
                list.Add(new Entry(f, meta));
            }
            catch { /* no or unreadable meta: not ours to offer */ }
        }
        return list.OrderByDescending(e => e.Meta.SavedAt).ToList();
    }

    /// <summary>Moves a recovery copy the user declined into <c>recovery\declined</c> (the newest few are kept)
    /// rather than deleting it — saying no by mistake must not be the end of the work.</summary>
    public static void Decline(string file)
    {
        try
        {
            var declined = Directory.CreateDirectory(Path.Combine(Dir, "declined")).FullName;
            var dest = Path.Combine(declined, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.Now:yyyyMMdd-HHmmss}.cardinator");
            File.Move(file, dest, overwrite: true);
            try { File.Delete(file + ".meta"); } catch { }
            foreach (var old in new DirectoryInfo(declined).GetFiles("*.cardinator")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(5))
                try { old.Delete(); } catch { }
        }
        catch { /* leave it where it is: it'll be offered again */ }
    }

    private static bool IsRunning(Meta meta)
    {
        if (meta.Session != null)
        {
            if (meta.Session == Session) return true;
            if (meta.Pid == Environment.ProcessId) return false;   // our process id, reused after a restart
        }
        else if (meta.Pid == Environment.ProcessId) return true;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(meta.Pid);
            return !p.HasExited && p.ProcessName.StartsWith("Cardinator", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
