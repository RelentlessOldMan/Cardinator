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
    /// (Kamigawa) becomes ONE card with its upside-down half as <see cref="CardModel.OtherHalf"/>, and a split
    /// card (incl. Duskmourn Rooms) ONE card with its second half side by side. Any other multi-face layout
    /// (aftermath — not drawn yet) is printed on a single side, so its extra faces are NOT a back face —
    /// they're returned for the caller to add as separate cards.
    /// </summary>
    /// <para>A back face or other half the card already has is the user's own work (or a list's): it is never
    /// replaced. If it's the same card as the looked-up face, only its blank fields are filled; either way nothing
    /// else is attached, and a back face or other half never takes one of its own.</para>
    /// </summary>
    /// <returns>The faces that were not absorbed as a back face (empty for a single- or double-faced card).</returns>
    public static IReadOnlyList<CardModel> AttachFaces(CardModel card, IReadOnlyList<CardModel> faces)
    {
        if (faces.Count <= 1) return Array.Empty<CardModel>();
        if (card.IsBackFace || card.IsOtherHalf) return Array.Empty<CardModel>();
        if ((card.BackFace ?? card.OtherHalf) is { } own)
        {
            if (string.Equals(own.Name.Trim(), faces[1].Name.Trim(), StringComparison.OrdinalIgnoreCase))
                FillBlanksFrom(own, faces[1], withCost: !string.Equals(faces[0].Layout, "flip", StringComparison.OrdinalIgnoreCase));
            return Array.Empty<CardModel>();
        }
        if (string.Equals(faces[0].Layout, "flip", StringComparison.OrdinalIgnoreCase))
        {
            var half = faces[1].Clone();
            half.ManaCost = "";               // the flipped half is never cast — real ones print no cost
            half.TemplateName = card.TemplateName;
            card.HalfLayout = "flip";
            card.OtherHalf = half;
            return Array.Empty<CardModel>();
        }
        if (string.Equals(faces[0].Layout, "split", StringComparison.OrdinalIgnoreCase))
        {
            var half = faces[1].Clone();
            half.TemplateName = card.TemplateName;
            card.HalfLayout = "split";
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

    /// <summary>Gives a newly added card its frame — and its back face / other half the same one when they have
    /// none (a search result's faces are built before any frame is known, and a blank frame would draw the back
    /// on whatever frame happens to be first).</summary>
    public static void TakeFrame(CardModel card, string frame)
    {
        if (string.IsNullOrEmpty(card.TemplateName)) card.TemplateName = frame;
        foreach (var part in new[] { card.BackFace, card.OtherHalf })
            if (part != null && string.IsNullOrEmpty(part.TemplateName)) part.TemplateName = card.TemplateName;
    }

    /// <summary>Fills only the empty printed fields of an existing back face / other half from its looked-up face.</summary>
    private static void FillBlanksFrom(CardModel target, CardModel face, bool withCost)
    {
        static bool B(string? s) => string.IsNullOrWhiteSpace(s);
        if (withCost && B(target.ManaCost)) target.ManaCost = face.ManaCost;   // a flipped half prints no cost
        if (B(target.TypeLine)) target.TypeLine = face.TypeLine;
        if (B(target.RulesText)) target.RulesText = face.RulesText;
        if (B(target.FlavorText)) target.FlavorText = face.FlavorText;
        if (B(target.Power)) target.Power = face.Power;
        if (B(target.Toughness)) target.Toughness = face.Toughness;
        if (B(target.Loyalty)) target.Loyalty = face.Loyalty;
        if (B(target.Defense)) target.Defense = face.Defense;
    }

    /// <summary>Makes a looked-up meld part (<i>Bruna</i>, from <paramref name="part"/>) a meld card: its back face
    /// becomes the melded card (<paramref name="melded"/>, <i>Brisela</i>), of which this card prints one half. The
    /// melded card's collector number is the bottom half's part's number plus a letter ("14b" on <i>Bruna</i>'s
    /// 14 — the bottom half carries the credits), so that part prints the bottom half and its partner the top.
    /// Only onto a card with no back or other half yet.</summary>
    public static bool AttachMeld(CardModel card, CardModel part, CardModel? melded)
    {
        if (!string.IsNullOrWhiteSpace(part.MeldWith)) card.MeldWith = part.MeldWith;
        if (melded == null || card.IsDoubleFaced || card.OtherHalf != null) return false;
        var url = melded.ArtUrl;
        var back = melded.Clone();
        back.ArtUrl = url;
        back.TemplateName = card.TemplateName;
        back.Layout = "";
        card.MeldHalf = MeldHalfFor(part, melded);
        card.BackFace = back;
        if (string.IsNullOrWhiteSpace(card.DfcStyle) || card.DfcStyle == "none") card.DfcStyle = "meld";
        return true;
    }

    /// <summary>Which half of <paramref name="melded"/> the meld part <paramref name="part"/> prints: the bottom when the
    /// melded card's number is the part's plus a letter (<i>Brisela</i> 14b on <i>Bruna</i>'s 14), else the top.</summary>
    internal static string MeldHalfFor(CardModel part, CardModel melded)
        => NumberRoot(melded.CollectorNumber) is { Length: > 0 } root && root == NumberRoot(part.CollectorNumber) ? "bottom" : "top";

    /// <summary>The digits a collector number starts with ("14b" → "14").</summary>
    private static string NumberRoot(string? n)
        => new((n ?? "").Trim().TakeWhile(char.IsDigit).ToArray());

    /// <summary>Fetches a looked-up meld part's melded card (when <see cref="CardModel.MeldResultUrl"/> is set) and
    /// attaches it with <see cref="AttachMeld"/>. Best effort: false when there's nothing to fetch or it fails.</summary>
    public static async Task<bool> AttachMeldAsync(ScryfallClient client, CardModel card, CardModel part, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(part.MeldResultUrl) || card.IsDoubleFaced || card.OtherHalf != null)
        {
            if (!string.IsNullOrWhiteSpace(part.MeldWith)) card.MeldWith = part.MeldWith;
            return false;
        }
        IReadOnlyList<CardModel> faces;
        try { faces = await client.LookupUriAsync(part.MeldResultUrl, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        ct.ThrowIfCancellationRequested();   // the dialog closed while this was in flight: don't touch the card
        return AttachMeld(card, part, faces.FirstOrDefault());
    }

    /// <summary>Scryfall's art for a split card is BOTH halves' art side by side (left half first). Once a
    /// lookup has downloaded it onto the card, give each half its own side. Only touches a split card whose
    /// other half has no art yet; if the image can't be cut, both halves share it.</summary>
    /// <summary>Where Scryfall's aftermath art crop changes from the top half's picture to the other half's
    /// (measured on <i>Destined // Lead</i>: 628 of 1024 px).</summary>
    internal const double AftermathArtCut = 0.613;

    public static void SplitSharedArt(CardModel card)
    {
        if (!card.IsSplit || card.OtherHalf is not { } half) return;
        if (string.IsNullOrWhiteSpace(card.ArtPath) || !string.IsNullOrWhiteSpace(half.ArtPath)) return;
        try
        {
            // An aftermath card's art is the wide top picture, then the sideways half's narrower one.
            var (left, right) = ImageIntake.SplitSideBySide(card.ArtPath, card.IsAftermath ? AftermathArtCut : 0.5);
            card.ArtPath = left;
            half.ArtPath = right;
        }
        catch { half.ArtPath = card.ArtPath; }
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
