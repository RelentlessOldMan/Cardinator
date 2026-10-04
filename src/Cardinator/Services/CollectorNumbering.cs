using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>Assigns collector numbers to a set in list order (NNN/total, zero-padded to the total's width).
/// Pure (no UI) so it's unit-testable; the window decides whether to renumber all or only blanks.</summary>
public static class CollectorNumbering
{
    /// <summary>Numbers the cards <c>NNN/total</c> in list order. When <paramref name="onlyBlanks"/> is true,
    /// cards that already have a collector number keep it (so imported/real printing numbers aren't clobbered);
    /// each card still gets its positional number. Returns how many cards were (re)numbered.</summary>
    public static int Assign(IReadOnlyList<CardModel> cards, bool onlyBlanks)
    {
        int total = cards.Count;
        if (total == 0) return 0;
        int width = total.ToString().Length;
        int changed = 0;
        for (int i = 0; i < total; i++)
        {
            if (onlyBlanks && !string.IsNullOrWhiteSpace(cards[i].CollectorNumber)) continue;
            cards[i].CollectorNumber = $"{(i + 1).ToString().PadLeft(width, '0')}/{total}";
            changed++;
        }
        return changed;
    }
}
