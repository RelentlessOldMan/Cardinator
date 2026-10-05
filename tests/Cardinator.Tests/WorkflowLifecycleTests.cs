using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// End-to-end data-lifecycle guarantees: the user must never lose work, silently drop a field on
/// save/reload, or have a repeated save mutate their data. These exercise the persistence round-trip
/// (import/create → edit → save → reopen → resume) at the model layer, without a UI.
/// </summary>
public class WorkflowLifecycleTests
{
    /// <summary>Every writable, non-transient CardModel property — the set that MUST survive a save/load.</summary>
    private static PropertyInfo[] PersistedProps() =>
        typeof(CardModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToArray();

    /// <summary>Compares every persisted field of two cards, recursing into the back face (a CardModel,
    /// so a plain Assert.Equal would compare references and always fail).</summary>
    private static void AssertSamePersistedFields(CardModel expected, CardModel actual, string where = "card")
    {
        foreach (var p in PersistedProps())
        {
            var a = p.GetValue(expected);
            var b = p.GetValue(actual);
            if (p.PropertyType == typeof(CardModel))
            {
                Assert.Equal(a == null, b == null);
                if (a != null) AssertSamePersistedFields((CardModel)a, (CardModel)b!, where + "." + p.Name);
                continue;
            }
            Assert.Equal(a, b);
        }
    }

    /// <summary>Fills every persisted property with a distinctive value so an omission is detectable.</summary>
    private static CardModel FullyPopulated()
    {
        var card = new CardModel();
        int i = 1;
        foreach (var p in PersistedProps())
        {
            if (p.PropertyType == typeof(string)) p.SetValue(card, $"val-{p.Name}-{i}");
            else if (p.PropertyType == typeof(double)) p.SetValue(card, 0.125 * i + 0.5);
            else if (p.PropertyType == typeof(bool)) p.SetValue(card, true);
            else if (p.PropertyType == typeof(int)) p.SetValue(card, 1000 + i);
            else if (p.PropertyType == typeof(CardModel))
                p.SetValue(card, new CardModel { Name = $"back-{i}", TypeLine = "Creature — Back" });
            else
                throw new Xunit.Sdk.XunitException(
                    $"{p.Name} is persisted as {p.PropertyType.Name}, which this test doesn't populate — "
                    + "add a case so the save/reload round-trip actually covers it.");
            i++;
        }
        return card;
    }

    [Fact]
    public void CardProject_SaveReload_PreservesEveryPersistedField()
    {
        var card = FullyPopulated();
        var path = Path.Combine(Path.GetTempPath(), $"cp-full-{Guid.NewGuid():N}.cardinator");
        try
        {
            new CardProject { Name = "Full", ArtBaseDir = "base", DefaultTemplate = "T",
                              Cards = { card } }.Save(path);
            var loaded = CardProject.Load(path).Cards.Single();

            AssertSamePersistedFields(card, loaded);   // field-by-field; catches any omission
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CardModel_Clone_PreservesEveryPersistedField()
    {
        // Undo/redo snapshots and export use the same serialize/deserialize path, so clone fidelity
        // is the same guarantee as save fidelity.
        var card = FullyPopulated();
        var clone = card.Clone();
        AssertSamePersistedFields(card, clone);
    }

    [Fact]
    public void ArtUrl_IsTransient_NotPersisted()
    {
        var card = new CardModel { Name = "A", ArtUrl = "https://example/art.png" };
        var path = Path.Combine(Path.GetTempPath(), $"cp-url-{Guid.NewGuid():N}.cardinator");
        try
        {
            new CardProject { Cards = { card } }.Save(path);
            Assert.DoesNotContain("example/art.png", File.ReadAllText(path));   // not written
            Assert.Equal("", CardProject.Load(path).Cards.Single().ArtUrl);     // defaults on reload
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void RepeatedSave_OfUnchangedState_IsByteIdentical()
    {
        // Saving the same state twice must not reorder, duplicate, or otherwise churn the file.
        var project = new CardProject
        {
            Name = "Deck",
            Cards = { new CardModel { Name = "A", ManaCost = "{R}" },
                      new CardModel { Name = "B", ManaCost = "{U}" } },
        };
        var path = Path.Combine(Path.GetTempPath(), $"cp-rep-{Guid.NewGuid():N}.cardinator");
        try
        {
            project.Save(path);
            var first = File.ReadAllText(path);
            project.Save(path);
            var second = File.ReadAllText(path);
            Assert.Equal(first, second);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void EditSaveReload_TwoRounds_PreservesEdits()
    {
        // import/create → edit → save → reopen → edit → save → reopen, verifying both rounds.
        var path = Path.Combine(Path.GetTempPath(), $"cp-2round-{Guid.NewGuid():N}.cardinator");
        try
        {
            new CardProject { Cards = { new CardModel { Name = "First", Power = "1", Toughness = "1" } } }.Save(path);

            var p1 = CardProject.Load(path);
            p1.Cards[0].RulesText = "Flying";
            p1.Cards.Add(new CardModel { Name = "Second" });
            p1.Save(path);

            var p2 = CardProject.Load(path);
            Assert.Equal(2, p2.Cards.Count);
            Assert.Equal("Flying", p2.Cards[0].RulesText);
            Assert.Equal("1", p2.Cards[0].Power);
            Assert.Equal("Second", p2.Cards[1].Name);

            p2.Cards[1].Loyalty = "4";
            p2.Save(path);

            var p3 = CardProject.Load(path);
            Assert.Equal("4", p3.Cards[1].Loyalty);
            Assert.Equal("Flying", p3.Cards[0].RulesText);   // earlier edit still intact
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Save_ToUnwritablePath_Throws_AndLeavesPriorFileIntact()
    {
        // A failed save must never destroy the previously-saved-good file (atomic write contract).
        var dir = Path.Combine(Path.GetTempPath(), $"cp-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "deck.cardinator");
        try
        {
            new CardProject { Name = "Good", Cards = { new CardModel { Name = "A" } } }.Save(path);
            var good = File.ReadAllText(path);

            // A path whose "directory" is actually the existing file can't be created — save must throw.
            var bogus = Path.Combine(path, "nested.cardinator");
            Assert.ThrowsAny<Exception>(() =>
                new CardProject { Name = "Bad", Cards = { new CardModel { Name = "B" } } }.Save(bogus));

            Assert.Equal(good, File.ReadAllText(path));   // original untouched
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EnsureLocalCopy_ExternalFile_IsCopiedIntoDataDir()
    {
        // Interactively-picked art must be copied into the portable cache so the project stays
        // self-contained even if the original source is later moved or deleted.
        var ext = Path.Combine(Path.GetTempPath(), $"ext-art-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(ext, new byte[] { 1, 2, 3, 4 });
        try
        {
            var local = ImageIntake.EnsureLocalCopy(ext);
            Assert.NotEqual(ext, local);
            Assert.True(File.Exists(local));
            Assert.StartsWith(Path.GetFullPath(AppPaths.DataDir), Path.GetFullPath(local),
                              StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(local));

            // A file already inside the data dir is returned unchanged (no needless duplication).
            Assert.Equal(Path.GetFullPath(local), Path.GetFullPath(ImageIntake.EnsureLocalCopy(local)));
        }
        finally { try { File.Delete(ext); } catch { } }
    }

    [Fact]
    public void EnsureLocalCopy_MissingFile_ReturnsInputUnchanged()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.png");
        Assert.Equal(missing, ImageIntake.EnsureLocalCopy(missing));
    }
}
