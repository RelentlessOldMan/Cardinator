using System.IO;
using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>W1: the set's house defaults — applied to new/imported cards (blanks only) and persisted
/// on the project. Also guards that an old project file without a Profile still loads (back-compat).</summary>
public class SetProfileTests
{
    [Fact]
    public void ApplyDefaults_FillsOnlyBlankFields()
    {
        var profile = new SetProfile { SetCode = "CDN", Rarity = "R", Copyright = "© Me", Artist = "Jacob", SetSymbolPath = @"C:\sym.png" };
        var card = new CardModel { Name = "X", Artist = "Someone Else" };   // artist already set

        bool changed = profile.ApplyDefaults(card);

        Assert.True(changed);
        Assert.Equal("CDN", card.SetCode);
        Assert.Equal("R", card.Rarity);
        Assert.Equal("© Me", card.Copyright);
        Assert.Equal("Someone Else", card.Artist);        // not overwritten
        Assert.Equal(@"C:\sym.png", card.SetSymbolPath);
    }

    [Fact]
    public void ApplyDefaults_EmptyProfile_ChangesNothing()
    {
        var card = new CardModel { Name = "X" };
        Assert.False(new SetProfile().ApplyDefaults(card));
        Assert.Equal("", card.SetCode);
    }

    [Fact]
    public void IsEmpty_TracksAnySetField()
    {
        Assert.True(new SetProfile().IsEmpty);
        Assert.False(new SetProfile { SetCode = "CDN" }.IsEmpty);
    }

    [Fact]
    public void Project_RoundTrips_Profile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cardinator_proj_" + System.Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            var proj = new CardProject
            {
                Name = "Set",
                Profile = new SetProfile { SetCode = "CDN", Rarity = "M", Copyright = "© 2026", Artist = "Jacob" },
                Cards = { new CardModel { Name = "A" } },
            };
            proj.Save(path);

            var loaded = CardProject.Load(path);
            Assert.Equal("CDN", loaded.Profile.SetCode);
            Assert.Equal("M", loaded.Profile.Rarity);
            Assert.Equal("© 2026", loaded.Profile.Copyright);
            Assert.Equal("Jacob", loaded.Profile.Artist);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_OldProjectWithoutProfile_GetsEmptyProfile()   // back-compat
    {
        // A pre-W1 project file has no "profile" key at all — Load must not throw and must default it.
        var path = Path.Combine(Path.GetTempPath(), "cardinator_old_" + System.Guid.NewGuid().ToString("N") + ".cardinator");
        File.WriteAllText(path, "{ \"name\": \"Legacy\", \"cards\": [ { \"name\": \"Bolt\" } ] }");
        try
        {
            var loaded = CardProject.Load(path);
            Assert.Equal("Legacy", loaded.Name);
            Assert.Single(loaded.Cards);
            Assert.NotNull(loaded.Profile);
            Assert.True(loaded.Profile.IsEmpty);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
