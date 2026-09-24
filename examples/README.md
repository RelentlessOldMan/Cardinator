# Examples

Worked, ready-to-run examples. **Each folder has its input files, our own art, the exact command to
run, and the rendered output (plus app screenshots) checked in** — so you can see the whole flow
before trying your own.

> ⭐ **Start with [01 · Real Magic cards with your own custom art](01-real-cards-custom-art/)** — a
> whole deck of real cards, in your artwork, text pulled from Scryfall. That's the headline use case.

| Example | Use case | You provide | You get |
|---------|----------|-------------|---------|
| [**01 · Real cards, custom art** ⭐](01-real-cards-custom-art/) | Real Magic cards in your own artwork | a deck list of real names + art | real text from Scryfall on your art + a print sheet |
| [**02 · Custom set**](02-custom-set/) | Design your own set from a spreadsheet | a CSV of invented cards + art | every card rendered + a print sheet |
| [**03 · Full-art cards**](03-full-art/) | Text directly on the artwork | a CSV using the *Full Art* frame | full-art cards |
| [**04 · Bring your own frame**](04-custom-frame/) | Use a frame you designed yourself | a transparent frame PNG | a new template + a card on it |
| [**05 · Tokens & emblems**](05-tokens-emblems/) | The extras a deck needs | a CSV with blank mana/PT | tokens and emblems, laid out right |
| [**06 · Printing**](06-printing/) | Print at home or via a print service | your finished cards | a card back for double-sided + a bleed/300-DPI export |

Single card in a hurry? [`card.json`](card.json) renders with `--render` (see below).

**Bonus — a whole set from a Scryfall search** (art comes from Scryfall, so outputs aren't checked
in here): `Cardinator.exe --search "t:dragon c:r" out sheet max=9` fetches matching cards *with
their art* and lays them out on a print sheet.

## How to run any example

**In the app (easiest):** click **Import list / CSV…** and pick the example's `.csv`. Art paths in
the CSV resolve next to the file, so the art loads automatically. Then **Export all…** or
**Print sheet…**.

**From the command line** (run from the repo root, using the published `Cardinator.exe`):

```powershell
# render every card to PNGs, and a printable 3x3 sheet, into the example's output/ folder
Cardinator.exe --batch examples/01-real-cards-custom-art/deck.csv examples/01-real-cards-custom-art/output examples/01-real-cards-custom-art
Cardinator.exe --sheet examples/01-real-cards-custom-art/deck.csv examples/01-real-cards-custom-art/output examples/01-real-cards-custom-art
```

The third argument is the folder your art paths are relative to (here, the example folder).

## Notes

- The artwork in these examples is **our own** placeholder art (simple generated scenes) so nothing
  copyrighted ships in this repo — swap in your own images.
- Example 01 uses **real card names**; Cardinator fetches their text (and shows the real mana
  symbols) from Scryfall at run time. That's fan content — see the repo's Legal note. Everything you
  make is for personal use.
