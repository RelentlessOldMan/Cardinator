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

## `--render <card.json> <out.png|.jpg> [scale=N] [dpi=N] [bleed=N] [q=N]`

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

## Notes

- Input list/CSV format is described in [`examples/README.md`](../examples/README.md) and the
  [User Guide](USER_GUIDE.md#4-making-many-cards-at-once).
- Scryfall calls are throttled (~100 ms apart) and time out gracefully; offline runs still render
  with drawn mana pips and any fields you supplied.
- These modes are also how the project is verified without opening the GUI (handy on a headless or
  remote machine).
