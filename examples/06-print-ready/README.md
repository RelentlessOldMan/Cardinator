# Example 06 · Print-ready export (bleed + DPI)

**Exporting for a print service** (MakePlayingCards, PrinterStudio, etc.) that wants a **bleed
margin**, a specific **DPI**, and often **JPEG**. Cardinator does all three from one command.

## What's here

```
06-print-ready/
  card.json              <- a single card
  art/hero.png           <- placeholder artwork
  output/print-ready.jpg <- exported with bleed + 300 DPI (checked in)
```

## Export it

```powershell
Cardinator.exe --render examples/06-print-ready/card.json examples/06-print-ready/output/print-ready.jpg scale=3 dpi=300 bleed=36 q=95
```

- `scale=3` — supersample for a large, crisp image.
- `dpi=300` — stamp 300 DPI so print tools size it correctly.
- `bleed=36` — add a 36-px (×scale) safe-cut margin, edge-extended, on every side.
- `q=95` — JPEG quality (a `.jpg` path writes JPEG; `.png` writes PNG).

The result is **2466 × 3366** with a filled bleed border around the card:

![Print-ready card with bleed](output/print-ready.jpg)

> Check your print service's spec sheet for the exact bleed and size they want, then match `bleed`
> and `scale`. For a plain high-res PNG with no bleed, just use `--render card.json out.png scale=3`.
