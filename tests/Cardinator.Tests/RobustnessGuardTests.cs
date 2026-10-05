using System;
using System.IO;
using System.Linq;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Guards from the fresh-eyes review's Batch D: a file from a newer Cardinator is flagged before it gets
/// overwritten, hand-edited nulls don't crash the card, re-importing a double-faced card is recognised as a
/// duplicate, real Scryfall symbols aren't flagged as typos, and a failed export can't lose the old zip.
/// </summary>
public class RobustnessGuardTests
{
    // --- D3: a file written by a newer version --------------------------------

    [Fact]
    public void AProjectFromANewerVersion_LoadsButIsFlagged()
    {
        var path = Path.Combine(Path.GetTempPath(), "newer_" + Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            // Unknown fields AND a future format version — it must still load (tolerant-read promise).
            File.WriteAllText(path, """
            { "name": "Future Set", "formatVersion": 99, "somethingNew": { "a": 1 },
              "cards": [ { "name": "Card", "typeLine": "Instant", "unknownField": true } ] }
            """);

            var project = CardProject.Load(path);

            Assert.Equal("Future Set", project.Name);
            Assert.Single(project.Cards);
            Assert.True(project.IsFromNewerVersion, "a newer formatVersion must be reported so the user is warned");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ACurrentOrOlderProject_IsNotFlagged()
    {
        var path = Path.Combine(Path.GetTempPath(), "old_" + Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            File.WriteAllText(path, """{ "name": "Old Set", "cards": [] }""");   // pre-versioning: no formatVersion
            Assert.False(CardProject.Load(path).IsFromNewerVersion);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // --- D4: a hand-edited file with nulls still works ------------------------

    [Fact]
    public void ACardWithNullStrings_LoadsAndItsComputedPropertiesWork()
    {
        var path = Path.Combine(Path.GetTempPath(), "nulls_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
            { "name": "Nulled", "typeLine": null, "rulesText": null, "manaCost": null, "landSymbol": null }
            """);

            var card = CardModel.Load(path);

            Assert.Equal("", card.TypeLine);
            Assert.False(card.IsPlaneswalker);     // these all read TypeLine and used to throw
            Assert.False(card.IsSaga);
            Assert.False(card.IsLand);
            Assert.Empty(card.BigLandSymbols);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void AProjectWithNullCardStrings_LoadsClean()
    {
        var path = Path.Combine(Path.GetTempPath(), "pnulls_" + Guid.NewGuid().ToString("N") + ".cardinator");
        try
        {
            File.WriteAllText(path, """
            { "name": "S", "cards": [ { "name": "A", "typeLine": null,
                "backFace": { "name": "B", "typeLine": null } } ] }
            """);

            var card = CardProject.Load(path).Cards[0];

            Assert.Equal("", card.TypeLine);
            Assert.Equal("", card.BackFace!.TypeLine);   // the back face is coalesced too
            Assert.False(card.BackFace.IsSaga);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // --- D4: duplicate detection sees through DFC name forms -------------------

    [Theory]
    [InlineData("Delver of Secrets // Insectile Aberration")]
    [InlineData("delver of secrets")]
    [InlineData("  Delver of Secrets  ")]
    public void ReimportingADoubleFacedCard_IsSeenAsADuplicate(string incomingName)
    {
        var existing = new[] { new CardModel { Name = "Delver of Secrets" } };
        var incoming = new[] { new ImportedCard(new CardModel { Name = incomingName }, NeedsLookup: true) };

        Assert.Equal(1, ImportService.CountNamesAlreadyIn(existing, incoming));
        Assert.Empty(ImportService.RemoveNamesAlreadyIn(existing, incoming));
    }

    [Fact]
    public void ADifferentCard_IsStillNotADuplicate()
    {
        var existing = new[] { new CardModel { Name = "Delver of Secrets" } };
        var incoming = new[] { new ImportedCard(new CardModel { Name = "Lightning Bolt" }, NeedsLookup: true) };

        Assert.Equal(0, ImportService.CountNamesAlreadyIn(existing, incoming));
        Assert.Single(ImportService.RemoveNamesAlreadyIn(existing, incoming));
    }

    // --- D4: real symbols are not reported as typos ---------------------------

    [Theory]
    [InlineData("{G/U/P}")]      // two-colour Phyrexian (Tamiyo, Compleated)
    [InlineData("{B/G/P}")]
    [InlineData("{HW}")]         // half symbols
    public void RealScryfallSymbols_AreNotFlagged(string cost)
    {
        var spec = new TemplateSpec { Name = "T" };
        spec.Normalize();
        var card = new CardModel { Name = "X", TypeLine = "Instant", ManaCost = cost };

        Assert.DoesNotContain(CardValidator.Validate(card, spec), i => i.Code == "bad-symbol");
    }

    [Fact]
    public void AnActualTypo_IsStillFlagged()
    {
        var spec = new TemplateSpec { Name = "T" };
        spec.Normalize();
        var card = new CardModel { Name = "X", TypeLine = "Instant", ManaCost = "{G/Z}" };

        Assert.Contains(CardValidator.Validate(card, spec), i => i.Code == "bad-symbol");
    }
}
