# Command-line reference

`Cardinator.exe` opens the app when run with no arguments. It also has several **headless modes**
(no window) for scripting, batch work, and verification. Run `Cardinator.exe --help` any time to
see this list.

All output paths are created if they don't exist. Exit code is `0` on success, non-zero on error.

---

## `--help`, `-h` / `--version`, `-v`

```powershell
Cardinator.exe --help       # print usage
Cardinator.exe --version    # print version
```

## `--selftest <outDir>`

Renders the built-in sample cards to PNGs. A quick way to confirm rendering works end-to-end.

```powershell
Cardinator.exe --selftest out
```

## `--lookup "<card name>" <outDir>`

Fetches one card from Scryfall, prints its fields, and renders it to a PNG.

```powershell
Cardinator.exe --lookup "Lightning Bolt" out
```

## `--render <card.json> <out.png|.jpg> [scale=N] [dpi=N] [bleed=N] [q=N] [nosym]`

Renders a single saved card (a `CardModel` JSON — the same shape used inside a `.cardinator`
project). Uses the card's `templateName`, falling back to the first template. Output is JPEG when
the path ends in `.jpg`/`.jpeg`, otherwise PNG.

Options (for print-ready output):
- `scale=N` — supersample factor (default 2 → 1500×2100; use 3–4 for larger).
- `dpi=N` — stamp the image DPI metadata (e.g. `dpi=300`).
- `bleed=N` — add an N-pixel print bleed margin on every side (edge-extended, e.g. `bleed=36`).
- `q=N` — JPEG quality 1–100 (default 92).
- `nosym` — don't fetch Scryfall mana symbols; use the app's own generic pips (offline / for
  screenshots that shouldn't embed third-party symbol art).

```powershell
Cardinator.exe --render mycard.json mycard.png
Cardinator.exe --render mycard.json mycard.jpg scale=3 dpi=300 bleed=36 q=95
```

Minimal `card.json`:

```json
{
  "name": "Nightfall Wanderer",
  "manaCost": "{2}{U}{R}",
  "typeLine": "Legendary Creature — Spirit Scout",
  "rulesText": "Flying, haste\n{T}: Add {U} or {R}.",
  "power": "3",
  "toughness": "4",
  "artPath": "C:\\art\\wanderer.png",
  "templateName": "Ocean Blue"
}
```

## `--rendercards <cards-dir | card.json> <outDir> [templates=<dir>] [scale=N] [nosym]`

Batch-renders **a whole folder of ready-made card JSONs** to PNGs — point it at a folder of cards
(and, optionally, a folder of custom frames) and out come finished cards. Each card is written to
`<outDir>/<slug>.png` (from its name; duplicates get `-2`, `-3`, …), resolving its `templateName`
against the available templates and falling back to the first one (noting which cards fell back).

- `templates=<dir>` — **also** load custom frames straight from a folder of template folders
  (each a `frame.png` + `template.json`), **without installing them first**. Frames loaded this way
  win name-clashes over the installed ones, so you can iterate on a frame and render against it in
  one step.
- `scale=N` — supersample factor (default 2 → 1500×2100).
- `nosym` — use the app's own generic mana pips instead of fetching Scryfall symbols.

```powershell
# render every card in .\mycards to .\out using the installed frames
Cardinator.exe --rendercards mycards out

# iterate: render cards against frames sitting in a working folder (no install step)
Cardinator.exe --rendercards mycards out templates=C:\work\frames scale=2
```

This is the fastest way to preview a set of cards, or to script card generation from a pipeline:
generate/author the `card.json` files and the frame folders, then run one command to render them all.

## `--search "<query>" <outDir> [sheet] [a4] [max=N]`

Imports a **Scryfall search** — pulls matching cards *with their real art* and renders them. By
default writes individual PNGs; add `sheet` to compose printable 3×3 pages (`a4` for A4). `max=N`
caps the number of cards (default 60).

```powershell
Cardinator.exe --search "t:dragon c:r" out
Cardinator.exe --search "set:dom r:mythic" sheets sheet max=18
```

Query syntax is Scryfall's (`t:` type, `c:` color, `set:`, `cmc=`, `r:` rarity, …).

## `--cardback <out.png> ["Wordmark"]`

Renders the decorative **card back** (for double-sided printing). Optional wordmark text.

```powershell
Cardinator.exe --cardback back.png "My Set"
```

## `--newtemplate "<name>" <frame.png | url> [fullart]`

Creates a custom template (the frame/border that sits on top of the art) from your own image — a
local file or a URL. Add `fullart` to make a full-art template (art fills the card, text sits on it
with a shadow). The template appears in the app's Frame dropdown after a restart / reload.

```powershell
Cardinator.exe --newtemplate "My Frame" C:\frames\myframe.png
Cardinator.exe --newtemplate "Showcase" https://example.com/frame.png fullart
```

Your frame image should be a **transparent PNG** where the art window is see-through. Cardinator
writes a starter `template.json` next to it with default regions — tune those to match your frame.

## `--frames <outDir> [nosym]`

Renders one sample card on **every installed frame** — a style showcase (built-ins plus any you've
imported). Add `nosym` to use the app's own generic pips instead of fetching Scryfall symbols.

```powershell
Cardinator.exe --frames out
Cardinator.exe --frames out nosym
```

## `--permute <card.json> <outDir>`

Renders one card across **32 combinations** of the composable frame knobs (connected panels,
textured background, bottom taper, top emblem, …) — a quick way to preview how the modern /
composable style options interact.

```powershell
Cardinator.exe --permute mycard.json out
```

## `--batch <list.csv> <outDir> [artDir]`

The whole many-cards pipeline: parse a name list / CSV, fill blank fields from Scryfall, optionally
match art from `artDir` by filename, and render every card to a PNG.

```powershell
Cardinator.exe --batch examples/02-custom-set/cards.csv out examples/02-custom-set
Cardinator.exe --batch names.txt out C:\myart      # also match art from a folder
```

## `--sheet <list.csv> <outDir> [artDir] [a4]`

Same input as `--batch`, but composes **printable 3×3 sheet pages** (real card size, 300 DPI, with
cut marks) instead of individual cards — one PNG per page. Add `a4` for A4 paper (default Letter).

```powershell
Cardinator.exe --sheet examples/01-real-cards-custom-art/deck.csv sheets examples/01-real-cards-custom-art
Cardinator.exe --sheet examples/02-custom-set/cards.csv sheets examples/02-custom-set a4
```

---

## Documentation / QA modes

These render the app itself (its windows, a full QA pass, or the illustrated quick-start) — handy
after any UI or frame change so the docs never drift from the code. They work off-screen, so they
run even on a locked or headless machine.

### `--qa <outDir>`

Renders **every layout across the frames**, runs automated checks, and writes two contact sheets
(`qa-layouts.png`, `qa-frames.png`) plus a `QA-REPORT.txt` summary.

```powershell
Cardinator.exe --qa qa-out
```

### `--docs <outDir>`

One-command documentation build: regenerates **all documentation screenshots** (the app windows +
a few hero card renders + a frames overview) and writes an illustrated `QUICKSTART.md` that embeds
them, into `<outDir>` (images go in `<outDir>/images`).

```powershell
Cardinator.exe --docs docs-out
```

### `--uishot <outDir>`

Renders the app's own windows (main, details, frame design, bulk edit, help) to PNGs — the raw
documentation screenshots, without the QUICKSTART wrapper.

```powershell
Cardinator.exe --uishot shots
```

### `--appshot <list.csv> <artDir> <out.png> ["name"]`

Renders the main window with a real deck loaded from a CSV (fields filled from Scryfall) — a
"here's the app doing the thing" screenshot. Pass a name substring to pre-select a card.

```powershell
Cardinator.exe --appshot examples/01-real-cards-custom-art/deck.csv examples/01-real-cards-custom-art app.png "Bolt"
```

---

## Notes

- Input list/CSV format is described in [`examples/README.md`](../examples/README.md) and the
  [User Guide](USER_GUIDE.md#4-making-many-cards-at-once).
- Scryfall calls are throttled (~100 ms apart) and time out gracefully; offline runs still render
  with drawn mana pips and any fields you supplied.
- These modes are also how the project is verified without opening the GUI (handy on a headless or
  remote machine).
