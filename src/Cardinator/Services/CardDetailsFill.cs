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
