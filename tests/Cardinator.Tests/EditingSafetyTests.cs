using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Covers the "don't silently destroy the user's work" fixes: collector numbering can fill blanks only,
/// Ctrl+L lookup never blanks hand-typed fields, and the details editor can be cancelled (snapshot restore).
/// </summary>
public class EditingSafetyTests
{
    // --- M2: collector numbering ------------------------------------------

    [Fact]
    public void Assign_NumbersEveryCard_WithZeroPaddedWidth()
    {
        var cards = Enumerable.Range(0, 12).Select(_ => new CardModel()).ToList();
        var changed = CollectorNumbering.Assign(cards, onlyBlanks: false);
        Assert.Equal(12, changed);
        Assert.Equal("01/12", cards[0].CollectorNumber);   // width tracks the total (2 digits for 12)
        Assert.Equal("12/12", cards[11].CollectorNumber);
    }

    [Fact]
    public void Assign_OnlyBlanks_KeepsExistingNumbersButNumbersTheBlanks()
    {
        var cards = new List<CardModel>
        {
            new() { CollectorNumber = "M10-146" },  // imported real number — must survive
            new(),                                  // blank
            new() { CollectorNumber = "" },         // blank
        };
        var changed = CollectorNumbering.Assign(cards, onlyBlanks: true);
        Assert.Equal(2, changed);
        Assert.Equal("M10-146", cards[0].CollectorNumber);   // preserved
        Assert.Equal("2/3", cards[1].CollectorNumber);        // positional
        Assert.Equal("3/3", cards[2].CollectorNumber);
    }

    // --- M6: non-destructive Ctrl+L ---------------------------------------

    [Fact]
    public void MergeNonEmpty_FillsFromFaceButNeverBlanksUserData()
    {
        var card = new CardModel
        {
            Name = "My Bear", TypeLine = "Creature — Bear", Power = "2", Toughness = "2", Loyalty = "7",
        };
        var face = new CardModel
        {
            Name = "Grizzly Bears", ManaCost = "{1}{G}", TypeLine = "Creature — Bear",
            Power = "2", Toughness = "2", Loyalty = "",   // vanilla creature → no loyalty
        };

        CardDetailsFill.MergeNonEmpty(card, face);

        Assert.Equal("Grizzly Bears", card.Name);   // filled from face
        Assert.Equal("{1}{G}", card.ManaCost);
        Assert.Equal("7", card.Loyalty);            // NOT blanked — the face had none
    }

    // --- M7: details editor cancel (snapshot restore) ---------------------

    [Fact]
    public void CopyFrom_RestoresEveryFieldFromASnapshot()
    {
        var card = new CardModel
        {
            Name = "Orig", ManaCost = "{G}", TypeLine = "Creature", RulesText = "trample",
            Power = "3", Toughness = "3", ArtPath = @"C:\a.png", TemplateName = "Gold Multicolor",
            SetSymbolPath = @"C:\s.png", ArtScale = 1.4,
        };
        var snapshot = card.Clone();

        // Simulate an editing session (e.g. a Scryfall fill + manual edits).
        card.Name = "Changed";
        card.ManaCost = "{U}{U}";
        card.RulesText = "flying";
        card.Power = "9";
        card.ArtScale = 2.2;

        card.CopyFrom(snapshot);   // Cancel

        Assert.Equal("Orig", card.Name);
        Assert.Equal("{G}", card.ManaCost);
        Assert.Equal("trample", card.RulesText);
        Assert.Equal("3", card.Power);
        Assert.Equal(1.4, card.ArtScale);
        Assert.Equal("Gold Multicolor", card.TemplateName);
        Assert.Equal(@"C:\s.png", card.SetSymbolPath);
    }

    [Fact]
    public void CopyFrom_RaisesChangeNotificationsSoThePreviewUpdates()
    {
        var card = new CardModel { Name = "A" };
        var snapshot = card.Clone();
        card.Name = "B";

        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        card.CopyFrom(snapshot);

        Assert.Contains(nameof(CardModel.Name), raised);
        Assert.Equal("A", card.Name);
    }
}
