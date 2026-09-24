# Example 05 · Print & play (double-sided)

**Print your deck at home and give every card a matching back.** Cardinator renders a decorative
card back you can print on the reverse of your sheets.

## What's here

```
05-print-and-play/
  output/card-back.png   <- the card back (checked in)
```

## Make the back

**App:** there isn't a button for this yet — use the command line.

**Command line:**

```powershell
Cardinator.exe --cardback examples/05-print-and-play/output/card-back.png "Aria's Deck"
```

The optional last argument is a wordmark printed under the emblem (your set's name).

![Card back](output/card-back.png)

## Print it double-sided

1. Print a **fronts** sheet from another example, e.g.
   `examples/01-custom-set/output/sheet_page_01.png` (9 cards, real size, with cut marks).
2. Print a page of **backs** on the reverse — put nine copies of `card-back.png` on a page (3×3),
   or use your printer's "print N-up" option, and enable **duplex / two-sided** printing.
3. Cut along the marks.

> Tip: the sheets are laid out at true card size (2.5″ × 3.5″, 300 DPI), so "actual size" /
> "100% scale" in your print dialog gives cards that fit standard sleeves.
