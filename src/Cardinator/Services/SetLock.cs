using System.IO;

namespace Cardinator.Services;

/// <summary>
/// Keeps two commands from changing one set at once. Each command loads the set, changes it and saves it; two run
/// together (a script's parallel <c>--restyle</c>s, an <c>--import append</c> beside one) both said "Saved" while the
/// last one to save quietly undid the other's change. Holding this from load to save makes the second wait its turn.
/// The lock is a file beside the set that the system deletes when the command ends, however it ends.
/// </summary>
public sealed class SetLock : IDisposable
{
    private readonly FileStream _file;
    private SetLock(FileStream file) => _file = file;

    /// <summary>How long a command waits for another to finish with the set before giving up.</summary>
    internal static TimeSpan Wait = TimeSpan.FromMinutes(5);

    /// <summary>Takes the lock on <paramref name="setPath"/>, waiting while another command holds it (for
    /// <paramref name="wait"/>, else <see cref="Wait"/>). Null when it couldn't be had in time (the caller stops
    /// without saving).</summary>
    public static SetLock? Take(string setPath, Action? waiting = null, TimeSpan? wait = null)
    {
        var lockPath = Path.GetFullPath(setPath) + ".lock";
        var deadline = DateTime.UtcNow + (wait ?? Wait);
        bool told = false;
        while (true)
        {
            try
            {
                return new SetLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose));
            }
            catch (DirectoryNotFoundException) { throw; }   // nothing to wait for: the folder isn't there
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!told) { waiting?.Invoke(); told = true; }
                Thread.Sleep(100);
            }
            catch (IOException) { return null; }
            // A lock file the last holder just released but a scanner or sync client still has open is "delete pending":
            // opening it says access denied, not in use. It's still someone's turn ending — wait for it the same way.
            catch (UnauthorizedAccessException) when (File.Exists(lockPath) && DateTime.UtcNow < deadline)
            {
                if (!told) { waiting?.Invoke(); told = true; }
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (File.Exists(lockPath)) { return null; }
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>A set file as it was on disk at one moment (missing, or its write time and length), to tell whether
/// something else saved it since.</summary>
public readonly record struct DiskStamp(bool Exists, DateTime WrittenUtc, long Length)
{
    public static DiskStamp Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new DiskStamp(true, fi.LastWriteTimeUtc, fi.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return default; }
    }
}
