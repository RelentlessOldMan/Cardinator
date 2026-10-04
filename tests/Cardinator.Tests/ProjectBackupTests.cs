using System;
using System.IO;
using System.Linq;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Automated save-time backups: rolling timestamped copies of the previous good project file so
/// a bad save / buggy build / upgrade can be rolled back.</summary>
public class ProjectBackupTests : IDisposable
{
    private readonly string _dir;
    private readonly string _proj;

    public ProjectBackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardinator_bak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _proj = Path.Combine(_dir, "MySet.cardinator");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void BackupExisting_CopiesPreviousFileIntoBackupsFolder()
    {
        File.WriteAllText(_proj, "v1");
        var backup = ProjectBackup.BackupExisting(_proj, "20260104-120000");

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup!));
        Assert.Equal("v1", File.ReadAllText(backup!));
        Assert.Equal(ProjectBackup.BackupDir(_proj), Path.GetDirectoryName(backup));
        Assert.Contains("MySet.20260104-120000", Path.GetFileName(backup));
        Assert.EndsWith(".cardinator", backup);
    }

    [Fact]
    public void BackupExisting_FirstSave_NoFileYet_IsNoOp()
    {
        Assert.Null(ProjectBackup.BackupExisting(_proj, "20260104-120000"));
        Assert.False(Directory.Exists(ProjectBackup.BackupDir(_proj)));
    }

    [Fact]
    public void BackupExisting_SameTimestamp_DoesNotClobber()
    {
        File.WriteAllText(_proj, "a");
        var b1 = ProjectBackup.BackupExisting(_proj, "20260104-120000");
        File.WriteAllText(_proj, "b");
        var b2 = ProjectBackup.BackupExisting(_proj, "20260104-120000");   // same stamp

        Assert.NotEqual(b1, b2);
        Assert.Equal("a", File.ReadAllText(b1!));
        Assert.Equal("b", File.ReadAllText(b2!));
    }

    [Fact]
    public void Prune_KeepsOnlyMostRecentN()
    {
        // Create 5 backups with increasing timestamps; keep 3.
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(_proj, "v" + i);
            ProjectBackup.BackupExisting(_proj, $"20260104-12000{i}", keep: 3);
        }

        var remaining = ProjectBackup.ListBackups(_proj);
        Assert.Equal(3, remaining.Count);
        // Newest first: the last three timestamps survive.
        Assert.Contains("120005", remaining[0]);
        Assert.Contains("120003", remaining[2]);
        Assert.DoesNotContain(remaining, b => b.Contains("120001") || b.Contains("120002"));
    }

    [Fact]
    public void ListBackups_OnlyMatchesThisProject()
    {
        File.WriteAllText(_proj, "mine");
        ProjectBackup.BackupExisting(_proj, "20260104-120000");

        // A different project's backup sitting in the same folder must not be listed for this one.
        var other = Path.Combine(_dir, "OtherSet.cardinator");
        File.WriteAllText(other, "theirs");
        ProjectBackup.BackupExisting(other, "20260104-120000");

        var mine = ProjectBackup.ListBackups(_proj);
        Assert.Single(mine);
        Assert.Contains("MySet.", Path.GetFileName(mine[0]));
    }

    [Fact]
    public void ListBackups_NoFolder_IsEmpty()
        => Assert.Empty(ProjectBackup.ListBackups(_proj));
}
