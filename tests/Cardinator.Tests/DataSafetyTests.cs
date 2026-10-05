using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The data-safety guarantees a real user depends on: a saved set is self-contained INCLUDING the back
/// faces of double-faced cards, restoring a backup keeps the art links working, a project's backups are
/// never pruned by a sibling project, and a user-imported frame image is never destroyed by a bad decode.
/// </summary>
public class DataSafetyTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "safety_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 });
        return path;
    }

    // --- A1: the back face's art must travel with the set ----------------------

    [Fact]
    public void LocalizeImages_CopiesTheBackFaceArt_IntoTheSetFolder()
    {
        var set = TempDir();
        var outside = TempDir();
        try
        {
            var backArt = WritePng(Path.Combine(outside, "nightform.png"));
            var card = new CardModel { Name = "Werewolf", ArtPath = WritePng(Path.Combine(outside, "front.png")) };
            card.BackFace = new CardModel { Name = "Nightform", ArtPath = backArt };

            var stranded = SetFolder.LocalizeImages(new[] { card }, set);

            Assert.Equal(0, stranded);
            // The back face's art was copied into <set>\art and the card repointed at the copy.
            var localized = card.BackFace!.ArtPath;
            Assert.True(File.Exists(localized), "back-face art was not copied into the set folder");
            Assert.StartsWith(Path.GetFullPath(Path.Combine(set, "art")), Path.GetFullPath(localized),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(set, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void MakeArtRelative_And_ResolveArt_CoverTheBackFace()
    {
        var folder = TempDir();
        try
        {
            var card = new CardModel { Name = "F", ArtPath = Path.GetFullPath(Path.Combine(folder, "art", "f.png")) };
            card.BackFace = new CardModel
            {
                Name = "B",
                ArtPath = Path.GetFullPath(Path.Combine(folder, "art", "b.png")),
                SetSymbolPath = Path.GetFullPath(Path.Combine(folder, "art", "sym.png")),
            };

            CardProject.MakeArtRelative(card, folder);
            Assert.False(Path.IsPathRooted(card.BackFace!.ArtPath), "back-face art stayed absolute — it won't travel");
            Assert.False(Path.IsPathRooted(card.BackFace.SetSymbolPath));

            CardProject.ResolveArt(card, folder);
            Assert.True(Path.IsPathRooted(card.BackFace.ArtPath), "back-face art was not resolved back to absolute");
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, "art", "b.png")), card.BackFace.ArtPath);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public void CountMissingArt_CountsAMissingBackFaceArt()
    {
        var card = new CardModel { Name = "F" };   // front has no art at all
        card.BackFace = new CardModel
        {
            Name = "B",
            ArtPath = Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N") + ".png"),
        };

        Assert.Equal(1, SetFolder.CountMissingArt(new[] { card }));
    }

    // --- A4: restoring a backup must keep the art links working ----------------

    [Fact]
    public void ArtRootFor_AFileInsideBackups_ResolvesAgainstTheSetFolder()
    {
        var root = TempDir();
        try
        {
            var set = Path.Combine(root, "MySet");
            Directory.CreateDirectory(set);
            var proj = Path.Combine(set, "MySet.cardinator");
            var backup = Path.Combine(set, "backups", "MySet.20260104-120000.cardinator");

            // A normal project file resolves against its own folder...
            Assert.Equal(Path.GetFullPath(set), Path.GetFullPath(ProjectBackup.ArtRootFor(proj)));
            // ...but a backup belongs to the SET one level up, not to backups\.
            Assert.Equal(Path.GetFullPath(set), Path.GetFullPath(ProjectBackup.ArtRootFor(backup)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RestoringABackup_KeepsRelativeArtPathsWorking()
    {
        var root = TempDir();
        try
        {
            // A saved set: art lives in <set>\art and is stored RELATIVE in the project file.
            var set = Path.Combine(root, "MySet");
            Directory.CreateDirectory(set);
            WritePng(Path.Combine(set, "art", "pic.png"));
            var projPath = Path.Combine(set, "MySet.cardinator");
            var saved = new CardProject
            {
                Name = "MySet",
                Cards = { new CardModel { Name = "A", ArtPath = Path.Combine("art", "pic.png") } },
            };
            saved.Save(projPath);

            // Save again so a backup of the previous good file exists, then restore that backup.
            var backup = ProjectBackup.BackupExisting(projPath, "20260104-120000");
            Assert.NotNull(backup);

            var reloaded = CardProject.Load(backup!);
            foreach (var c in reloaded.Cards)
                CardProject.ResolveArt(c, ProjectBackup.ArtRootFor(backup!));

            Assert.True(File.Exists(reloaded.Cards[0].ArtPath),
                "a restored backup lost its art link (resolved against backups\\ instead of the set folder)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // --- A5: one project's pruning must not delete another's backups -----------

    [Fact]
    public void ListBackups_IgnoresASiblingProjectWithADottedPrefix()
    {
        var dir = TempDir();
        try
        {
            var mine = Path.Combine(dir, "MySet.cardinator");
            var sibling = Path.Combine(dir, "MySet.v2.cardinator");
            File.WriteAllText(mine, "mine");
            File.WriteAllText(sibling, "sibling");

            ProjectBackup.BackupExisting(mine, "20260104-120000");
            ProjectBackup.BackupExisting(sibling, "20260104-120001");

            var listed = ProjectBackup.ListBackups(mine);
            Assert.Single(listed);
            Assert.Contains("MySet.20260104-120000", Path.GetFileName(listed[0]));
            // The sibling's own backup is still listed for the sibling.
            Assert.Single(ProjectBackup.ListBackups(sibling));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Prune_LeavesASiblingProjectsBackupsAlone()
    {
        var dir = TempDir();
        try
        {
            var mine = Path.Combine(dir, "MySet.cardinator");
            var sibling = Path.Combine(dir, "MySet.v2.cardinator");
            File.WriteAllText(sibling, "sibling");
            var siblingBackup = ProjectBackup.BackupExisting(sibling, "20260104-120000");
            Assert.NotNull(siblingBackup);

            // Many saves of MySet, pruning hard — must never touch MySet.v2's backups.
            for (int i = 0; i < 4; i++)
            {
                File.WriteAllText(mine, "v" + i);
                ProjectBackup.BackupExisting(mine, $"2026010{i}-1200{i}0", keep: 1);
            }

            Assert.True(File.Exists(siblingBackup!), "pruning MySet deleted MySet.v2's backup");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- the save pipeline's ORDER is the guarantee ----------------------------

    [Fact]
    public void Saving_BacksUpThePreviousVersionsBytes_NotTheNewOnes()
    {
        var root = TempDir();
        try
        {
            var set = Path.Combine(root, "MySet");
            Directory.CreateDirectory(set);
            var path = Path.Combine(set, "MySet.cardinator");
            var profile = new SetProfile();

            var v1 = new List<CardModel> { new() { Name = "Version One" } };
            ProjectWriter.Write(path, v1, "MySet", "", "", profile, timestamp: "20260104-120000");
            Assert.Empty(ProjectBackup.ListBackups(path));   // nothing to back up on a first save

            var v2 = new List<CardModel> { new() { Name = "Version Two" } };
            ProjectWriter.Write(path, v2, "MySet", "", "", profile, timestamp: "20260104-120001");

            // The live file is v2 and the backup holds v1. If the backup ran AFTER the write it would
            // hold v2 as well — silently destroying the rollback the user is told they have.
            Assert.Equal("Version Two", CardProject.Load(path).Cards[0].Name);
            var backups = ProjectBackup.ListBackups(path);
            Assert.Single(backups);
            Assert.Equal("Version One", CardProject.Load(backups[0]).Cards[0].Name);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Saving_MakesTheSetSelfContained_IncludingABackFacesArt()
    {
        var root = TempDir();
        var outside = TempDir();
        try
        {
            var set = Path.Combine(root, "MySet");
            Directory.CreateDirectory(set);
            var path = Path.Combine(set, "MySet.cardinator");

            var card = new CardModel { Name = "F", ArtPath = WritePng(Path.Combine(outside, "front.png")) };
            card.BackFace = new CardModel { Name = "B", ArtPath = WritePng(Path.Combine(outside, "back.png")) };

            ProjectWriter.Write(path, new List<CardModel> { card }, "MySet", "", "", new SetProfile());

            // Both faces' art was copied in and stored relative, so the folder can be zipped and shared.
            var json = File.ReadAllText(path);
            Assert.DoesNotContain(outside.Replace("\\", "\\\\"), json);
            var reloaded = CardProject.Load(path);
            foreach (var face in reloaded.Cards[0].Faces())
                Assert.False(Path.IsPathRooted(face.ArtPath), $"{face.Name} art stayed absolute");

            CardProject.ResolveArt(reloaded.Cards[0], set);
            foreach (var face in reloaded.Cards[0].Faces())
                Assert.True(File.Exists(face.ArtPath), $"{face.Name} art is missing from the set folder");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    // --- A3: a user's imported frame image is irreplaceable --------------------

    [Fact]
    public void AnUnreadableCustomFrame_IsPreserved_NotDeletedAndOverwritten()
    {
        var root = TempDir();
        try
        {
            // A custom (user-imported) template: frame.png is the ONLY copy of the user's image, and it
            // has no FrameSrc to regenerate from. Here it fails to decode (truncated/garbage).
            var dir = Path.Combine(root, "mine");
            Directory.CreateDirectory(dir);
            var spec = new TemplateSpec { Name = "Mine", CustomFrame = true };
            spec.Save(Path.Combine(dir, "template.json"));
            var framePath = Path.Combine(dir, "frame.png");
            var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };   // not a decodable image
            File.WriteAllBytes(framePath, original);

            TestHelpers.RunSta(() => new TemplateService().LoadFrom(root));

            // The bytes must still exist somewhere in the folder (kept in place or set aside) — never
            // deleted, and never replaced by a generated placeholder frame.
            var survivor = Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => File.ReadAllBytes(f).SequenceEqual(original));
            Assert.NotNull(survivor);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
