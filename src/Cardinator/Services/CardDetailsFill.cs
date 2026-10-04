using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Copies the printed DETAILS of a looked-up card (mana cost, type, rules, stats, printing) onto a target
/// card, while deliberately leaving the target's own identity alone — its Name, ArtPath and TemplateName
/// (frame) are never touched. This is what the "Fill from Scryfall" search in the details editor uses, so a
/// card can be named anything (e.g. "Boogie Woogie") and still borrow another card's stats.
/// </summary>
public static class CardDetailsFill
{
    /// <summary>Non-destructive fill for the main-window "look up this card" (Ctrl+L): copies the face's name
    /// and detail fields ONLY where the face actually has a value, so a lookup never silently blanks something
    /// the user typed (e.g. clearing a hand-entered loyalty when the looked-up card has none). Art and frame
    /// are untouched (handled separately).</summary>
    public static void MergeNonEmpty(CardModel target, CardModel face)
    {
        if (!string.IsNullOrWhiteSpace(face.Name)) target.Name = face.Name;
        if (!string.IsNullOrWhiteSpace(face.ManaCost)) target.ManaCost = face.ManaCost;
        if (!string.IsNullOrWhiteSpace(face.TypeLine)) target.TypeLine = face.TypeLine;
        if (!string.IsNullOrWhiteSpace(face.RulesText)) target.RulesText = face.RulesText;
        if (!string.IsNullOrWhiteSpace(face.Power)) target.Power = face.Power;
        if (!string.IsNullOrWhiteSpace(face.Toughness)) target.Toughness = face.Toughness;
        if (!string.IsNullOrWhiteSpace(face.Loyalty)) target.Loyalty = face.Loyalty;
        if (!string.IsNullOrWhiteSpace(face.SetCode)) target.SetCode = face.SetCode;
        if (!string.IsNullOrWhiteSpace(face.CollectorNumber)) target.CollectorNumber = face.CollectorNumber;
        if (!string.IsNullOrWhiteSpace(face.Rarity)) target.Rarity = face.Rarity;
    }

    /// <summary>Applies <paramref name="face"/>'s details to <paramref name="target"/> in place.</summary>
    public static void ApplyScryfall(CardModel target, CardModel face)
    {
        target.ManaCost = face.ManaCost;
        target.TypeLine = face.TypeLine;
        target.RulesText = face.RulesText;
        target.Power = face.Power;
        target.Toughness = face.Toughness;
        target.Loyalty = face.Loyalty;
        target.SetCode = face.SetCode;
        target.CollectorNumber = face.CollectorNumber;
        target.Rarity = face.Rarity;
        // Intentionally NOT copied: Name, ArtPath, ArtScale/Offset, TemplateName, FlavorText, Artist.
    }
}
