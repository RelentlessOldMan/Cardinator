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
    public void Prune_KeepsTheMostRecentN_PlusTheDaysFirst()
    {
        // Create 5 backups on one day with increasing timestamps; keep 3.
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(_proj, "v" + i);
            ProjectBackup.BackupExisting(_proj, $"20260104-12000{i}", keep: 3);
        }

        var remaining = ProjectBackup.ListBackups(_proj);
        // Newest first: the last three timestamps survive, and so does the day's first (the state before
        // that day's saves); the ones in between go.
        Assert.Equal(4, remaining.Count);
        Assert.Contains("120005", remaining[0]);
        Assert.Contains("120003", remaining[2]);
        Assert.Contains(remaining, b => b.Contains("120001"));
        Assert.DoesNotContain(remaining, b => b.Contains("120002"));
    }

    [Fact]
    public void SavingAnUnchangedSet_AddsNoBackup_SoItCantPushTheHistoryOut()
    {
        File.WriteAllText(_proj, "before the mistake");
        var first = ProjectBackup.BackupExisting(_proj, "20260104-120000", keep: 3);
        File.WriteAllText(_proj, "half the set deleted");
        // Ctrl+S habit: many saves, nothing changing.
        for (int i = 1; i <= 20; i++)
            ProjectBackup.BackupExisting(_proj, $"20260104-1201{i:00}", keep: 3);

        var remaining = ProjectBackup.ListBackups(_proj);
        Assert.Equal(2, remaining.Count);   // one copy of each distinct version
        Assert.Contains(remaining, b => File.ReadAllText(b) == "before the mistake");
        Assert.Equal(first, remaining.Last());
    }

    [Fact]
    public void ABurstOfRealSaves_KeepsTheFirstBackupOfEachDay()
    {
        // Day 1: the good version, then 20 edits. Day 2: 20 more. keep=5 recent.
        File.WriteAllText(_proj, "good");
        ProjectBackup.BackupExisting(_proj, "20260101-090000", keep: 5);
        for (int i = 1; i <= 20; i++)
        {
            File.WriteAllText(_proj, "day1 edit " + i);
            ProjectBackup.BackupExisting(_proj, $"20260101-1000{i:00}", keep: 5);
        }
        for (int i = 1; i <= 20; i++)
        {
            File.WriteAllText(_proj, "day2 edit " + i);
            ProjectBackup.BackupExisting(_proj, $"20260102-1000{i:00}", keep: 5);
        }

        var texts = ProjectBackup.ListBackups(_proj).Select(File.ReadAllText).ToList();
        Assert.Contains("good", texts);              // day 1's first
        Assert.Contains("day2 edit 1", texts);       // day 2's first (in the app: the file as day 1 left it)
        Assert.Equal(7, texts.Count);                // + the 5 most recent
    }

    [Theory]
    [InlineData(@"MySet\backups\MySet.20261001-120000.cardinator", @"MySet\MySet.cardinator")]
    [InlineData(@"MySet\backups\MySet.v2.20261001-120000-3.cardinator", @"MySet\MySet.v2.cardinator")]
    [InlineData(@"MySet\backups\copied by hand.cardinator", null)]   // no set file above it: a set kept in a "backups" folder
    [InlineData(@"MySet\MySet.cardinator", null)]
    public void ABackupFile_KnowsWhichSetFileItBacksUp(string file, string? setFile)
    {
        var got = ProjectBackup.ProjectFileForBackup(Path.Combine(_dir, file));
        Assert.Equal(setFile is null or "" ? setFile : Path.Combine(_dir, setFile), got);
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
