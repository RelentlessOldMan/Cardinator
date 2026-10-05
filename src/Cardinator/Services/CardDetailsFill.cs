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
        if (!string.IsNullOrWhiteSpace(face.Defense)) target.Defense = face.Defense;
        if (!string.IsNullOrWhiteSpace(face.SetCode)) target.SetCode = face.SetCode;
        if (!string.IsNullOrWhiteSpace(face.CollectorNumber)) target.CollectorNumber = face.CollectorNumber;
        if (!string.IsNullOrWhiteSpace(face.Rarity)) target.Rarity = face.Rarity;
    }

    /// <summary>
    /// Attaches the extra faces of a looked-up card to <paramref name="card"/>, the same way on every import
    /// path. A genuinely two-sided card (transform / modal DFC / battle / reversible / double-faced token)
    /// becomes ONE card with a <see cref="CardModel.BackFace"/> and a default sun/moon indicator. A flip card
    /// (Kamigawa) becomes ONE card with its upside-down half as <see cref="CardModel.OtherHalf"/>. Any other
    /// multi-face layout (split, aftermath — not drawn yet) is printed on a single side, so its extra faces
    /// are NOT a back face — they're returned for the caller to add as separate cards.
    /// </summary>
    /// <returns>The faces that were not absorbed as a back face (empty for a single- or double-faced card).</returns>
    public static IReadOnlyList<CardModel> AttachFaces(CardModel card, IReadOnlyList<CardModel> faces)
    {
        if (faces.Count <= 1) return Array.Empty<CardModel>();
        if (string.Equals(faces[0].Layout, "flip", StringComparison.OrdinalIgnoreCase))
        {
            var half = faces[1].Clone();
            half.ManaCost = "";               // the flipped half is never cast — real ones print no cost
            half.TemplateName = card.TemplateName;
            card.HalfLayout = "flip";
            card.OtherHalf = half;
            return Array.Empty<CardModel>();
        }
        if (!ScryfallMapper.IsTwoSidedLayout(faces[0].Layout))
            return faces.Skip(1).ToList();

        var backUrl = faces[1].ArtUrl;        // transient: Clone() doesn't carry it
        var back = faces[1].Clone();
        back.ArtUrl = backUrl;
        back.TemplateName = card.TemplateName;
        card.BackFace = back;
        if (string.IsNullOrWhiteSpace(card.DfcStyle)) card.DfcStyle = "sunmoon";
        return Array.Empty<CardModel>();
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
        target.Defense = face.Defense;
        target.SetCode = face.SetCode;
        target.CollectorNumber = face.CollectorNumber;
        target.Rarity = face.Rarity;
        // Intentionally NOT copied: Name, ArtPath, ArtScale/Offset, TemplateName, FlavorText, Artist.
    }
}
