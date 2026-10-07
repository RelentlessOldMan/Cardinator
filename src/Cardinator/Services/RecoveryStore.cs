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
    public sealed record Meta(string SetPath, string Name, DateTime SavedAt, int Pid);
    public sealed record Entry(string File, Meta Meta);

    public static string Dir => Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "recovery")).FullName;

    /// <summary>The recovery file for a set: one per set file, or per running app for a set never saved.</summary>
    public static string FileFor(string setPath)
    {
        string key;
        if (string.IsNullOrWhiteSpace(setPath)) key = $"untitled-{Environment.ProcessId}";
        else
        {
            var full = Path.GetFullPath(setPath).ToUpperInvariant();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..10];
            key = TextUtil.SafeFileName(Path.GetFileNameWithoutExtension(setPath)) + "-" + hash;
        }
        return Path.Combine(Dir, key + ".cardinator");
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
            JsonSerializer.Serialize(new Meta(setPath ?? "", name ?? "", DateTime.Now, Environment.ProcessId)));
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
                if (meta == null || IsRunning(meta.Pid)) continue;   // another open window's live copy
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

    private static bool IsRunning(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("Cardinator", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
