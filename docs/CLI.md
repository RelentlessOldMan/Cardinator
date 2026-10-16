# Command-line reference

`Cardinator.exe` opens the app when run with no arguments. It also has several **headless modes**
(no window) for scripting, batch work, and verification. Run `Cardinator.exe --help` any time to
see this list.

All output paths are created if they don't exist. Exit code is `0` on success, non-zero on error. An option
Cardinator doesn't know, or one given too few arguments, is exit code `2` (it never opens the window). The same
goes for the options of `--render`, `--rendercards` and the set commands: a mistyped one (`fram=Midnight`), or an
option where a path belongs (a missing path, so `only=X` would have been taken for the folder), stops with exit
code `2` and says which options it takes. A `templates=` folder that isn't there is exit code `2` too. So is an
option with nothing after the `=` (`only=` from a script variable that was never set — it used to mean "all
cards"), a number out of range (`scale=9`), an extra word after a command's arguments, and a first argument that
isn't a `--mode` at all (a file name dropped on the command line): none of them opens the window.

Notes and warnings (a frame that isn't installed, art that's missing or can't be read, cards Scryfall didn't find)
go to the **error output**, so a script reading the normal output gets only results. Frame names are matched
ignoring case (`frame=midnight` is the installed "Midnight").

What Cardinator prints is UTF-8 with plain `\n` line ends, so accented card and set names reach a script intact.

**From PowerShell or cmd:** `Cardinator.exe` is a Windows app, so PowerShell and cmd start it and go straight on:
they don't wait for it, show what it prints, or see its exit code. Pipe it to wait and see its output
(`Cardinator.exe --info "My Deck.cardinator" | Out-Host`, which also sets `$LASTEXITCODE`), or use
`Start-Process Cardinator.exe -ArgumentList ... -Wait -NoNewWindow -PassThru`. Git Bash waits by itself.

---

## `--editframe <templateDir> [card.json]`

Opens **just the frame layout editor** (a window, not headless) on one template folder (`template.json` +
`frame.png`): drag and resize the text regions, tweak fonts, and **Apply** to save in place. Pass a card
JSON to preview your real card in it instead of the sample.

```powershell
Cardinator.exe --editframe "CardinatorData\templates\my-frame" mycard.json
```

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

An `artPath` relative to the card file's folder is found there; a picture that isn't anywhere gets a `Note:`
(the art window is drawn empty).

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

A card that can't be read or drawn is reported and the rest are still rendered (exit code `1`). A saved set in
the folder (a `.json` with a list of cards) is skipped — render it with `--renderset`. Art paths work as in
`--render`.

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

Renders the default decorative **card back** (for double-sided printing). Optional wordmark text. A set's own
back (designed or a picture) is made in the app: **Set settings…** → **Save the card back as an image…**.

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

## `--frames <outDir> [nosym] [builtin]`

Renders one sample card on **every installed frame** — a style showcase (built-ins plus any you've
imported). Add `nosym` to use the app's own generic pips instead of fetching Scryfall symbols, and
`builtin` to render only the frames that ship with the app (what the docs use).

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
match art from `artDir` by filename, and render every card to a PNG (identical cards once; the summary counts
them separately).

```powershell
Cardinator.exe --batch examples/02-custom-set/cards.csv out examples/02-custom-set
Cardinator.exe --batch names.txt out C:\myart      # also match art from a folder
```

An `artDir` that isn't there is exit code `2`. Cards Scryfall didn't find are named on the error output and
drawn with what the list gave; it finishes and exits `4`, so a script can tell. When Scryfall can't be reached
for cards that need it, it stops with exit code `1` instead of drawing blank cards.

## `--sheet <list.csv> <outDir> [artDir] [a4]`

Same input as `--batch`, but composes **printable 3×3 sheet pages** (real card size, 300 DPI, with
cut marks) instead of individual cards — one PNG per page. Add `a4` for A4 paper (default Letter). The art
folder, cards not found (exit `4`) and Scryfall offline (exit `1`) work as for `--batch`. Pages an earlier, longer
run left in the folder (`sheet_page_04.png` beside a 3-page deck) are removed.

```powershell
Cardinator.exe --sheet examples/01-real-cards-custom-art/deck.csv sheets examples/01-real-cards-custom-art
Cardinator.exe --sheet examples/02-custom-set/cards.csv sheets examples/02-custom-set a4
```

---

## Working with sets

These script what you'd otherwise do in the window: get a deck, make a set from it, try frames and art,
render it. They use the same code the window does, so a set made this way opens in the app exactly as if
you'd made it there (art copied into the set's `art` folder, the previous file backed up on every save).
Exit codes: `0` done, `1` failed, `2` bad arguments, `3` nothing to do (no cards matched), `4` done but some
cards weren't found on Scryfall (`--import`: they're saved as names only, and named on the error output).
Two commands changing the same set at once take turns: the second waits for the first to finish (it says so on
the error output) and gives up without saving after 5 minutes (exit `1`). A set made by a newer Cardinator keeps
the fields this one doesn't know when it's saved again.
A whole themed set (show art, show titles, a custom footer and an OpenAI-painted frame) built from these:
[THEMED-SETS.md](THEMED-SETS.md) and the scripts in `examples/themed-set/`.

A typical round:

```powershell
# "| Out-Host" makes PowerShell wait for each step (see the top of this page)
Cardinator.exe --import https://moxfield.com/decks/<id> "concepts\steel\My Deck.cardinator" "frame=Alchemist's Steel" | Out-Host
Cardinator.exe --restyle "concepts\steel\My Deck.cardinator" art=C:\art\my-style "only=Sol Ring|Mind Stone" | Out-Host
Cardinator.exe --renderset "concepts\steel\My Deck.cardinator" renders\steel | Out-Host
Cardinator.exe --info "concepts\steel\My Deck.cardinator" | Out-Host
```

### `--moxfield <deck link | id> <out.txt | out.json> [format=magicdecks]`

Fetches a **Moxfield deck** (through the same hidden browser the app's import uses, so it needs the WebView2
runtime that Windows 10/11 already has). A `.json` name gets Moxfield's full deck JSON; anything else gets the
plain decklist the app's import box shows (`1 Sol Ring (LTC) 273` per line, commander first — it includes the
sideboard). It replaces an existing `.txt` without asking, but never overwrites a set (a `.cardinator` file, or a
`.json` one with cards): that's `--import`. A private or deleted deck gives up after about 15 seconds (exit 1).

`format=magicdecks` writes the deck format shared with the MagicDecks and MagicSimulator tools instead: each
commander as `1 Name *CMDR*`, one blank line, then the mainboard as `N Name` (the same card's lines merged), with
no set codes, no sideboard, and a companion as a last `# companion: Name` line; UTF-8, LF line ends, a final
newline. A deck with no commander gets no tagged lines and no blank line.

```
1 Rograkh, Son of Rohgahh *CMDR*
1 Silas Renn, Seeker Adept *CMDR*

1 Demonic Consultation
2 Island
```

Cardinator's own list import reads that format too (the `*CMDR*` tag and `#` comments are left out of the names).

### `--import <deck link | list file> <set.cardinator> [frame=<name>] [art=<dir>] [noart] [append] [name=<set name>] [templates=<dir>]`

Makes a **set from a deck** — a Moxfield link or id, or any list/CSV the app imports — filled from Scryfall
(details, and art unless `noart`), and saves it.

- `frame=<name>`: the set's frame. Cards a CSV gives a frame of their own keep it.
- `art=<dir>`: your own pictures, named exactly after the cards (`Sol Ring.png`; case and punctuation don't
  matter); they're used over Scryfall's. Unlike the app's **Match art**, it never guesses from a near name
  (Forest would get `Snow-Covered Forest.png`): a picture that only nearly matches is named in a `Note:` and not
  used.
- `name=<set name>`: the set's name, instead of the file's (a build that writes to a working file and then
  swaps it in keeps the real name).
- `templates=<dir>`: frames in that folder count as there, so `frame=` naming one doesn't get the "isn't
  installed" note.
- An existing set is **never touched** — unless you pass `append`, which adds the cards whose names it
  doesn't already have (a retitled card counts under its real name too).
- A picture already copied into the set's folder (same name, same bytes) is reused, not copied again.
- It won't save into a set's `backups` folder (open a backup in the app to restore it), or over a set a newer
  version of Cardinator saved (exit code 2). The set's path must be a file, not a folder (exit code 2).
- Cards Scryfall didn't find (a typo in the list) are saved as names only — fix them in the app with **Fill in
  blanks from Scryfall** — and named on the error output, exit code `4`. A build script with `set -e` stops
  there, which is the point: the set isn't finished. When Scryfall can't be reached, **nothing is saved** (exit
  code `1`): run it again when you're online.

### `--renderset <set.cardinator> <outDir> [sheet] [a4] [only=<name|name>] [templates=<dir>] [nosym]`

Renders a **saved set** as **Export** does: each card on its own frame (`NNN_name.png`, backs as `-back.png`),
a card whose frame isn't installed on the set's frame. Identical cards (a deck's 22 Wastes) are rendered once; a
printed sheet still gets every copy. `sheet` makes printable 3×3 pages instead (`a4` for
A4). `only=` renders just the cards whose name contains one of the `|`-separated parts (`|` because card names
have commas). `templates=<dir>` also uses frames straight from a folder without installing them (they win
over an installed frame of the same name). Rendering the whole set removes card pictures (`NNN_name.png`) an
earlier render left in the folder — of cards since deleted, moved or repeated — so "print everything in the
folder" never prints an old card; other files are left alone. With `only=`, each card keeps its number in the
whole set, so it writes over its own picture. Art that's missing or can't be read is named on the error output.

### `--restyle <set.cardinator> [frame=<name>] [art=<dir>] [titles=<file>] [setcode=<code>] [number=<n>] [symbol=<image>] [only=<name|name>] [templates=<dir>]`

Changes a **saved set in place**: `frame=` puts the cards on that frame (for the whole set, it also becomes
the set's frame); `art=<dir>` gives each card — and a double-faced card's back — the picture in that folder
named exactly after it (cards without one keep their art; near names are noted, not used, as for `--import`).
`templates=<dir>` works as for `--import`. `titles=<file>` retitles cards, like Universes
Beyond's flavor names: each line is `card name<TAB>new title` (UTF-8, or the Windows code page PowerShell 5's
`Set-Content` writes), and the card shows the new title with its real
name on the subtitle plate (running it again finds a retitled card by that real name). Its rules text names it
by the new title too ("Whenever Ulamog attacks" becomes "Whenever Adam attacks"). `setcode=` and `number=`
set the footer's set code and collector number on every card (a custom set's own, e.g. `666 R • JA • EN`,
instead of each real card's printing), and `symbol=<image>` puts that picture on the type line as the set symbol; for the
whole set, the set code and symbol also go into its set settings, so cards added later get them. `only=` limits it to
those cards, by name or real name. A `titles=` file that retitles no card at all (a wrong file, or none of its names
in the set) is an error (exit 3) and nothing is saved. The previous file is backed up first. Like `--import`, it refuses a file in a
`backups` folder and a set a newer version saved.

### `--info <set.cardinator> [templates=<dir>]`

Lists a set: its folder and frame, then one tab-separated line per card — number, name, frame (marked
`(not installed)` when it isn't), and art (`ok`, `missing`, `unreadable` — there but damaged, empty or not a
picture, so it prints as an empty art window — or `none`). A double-faced card's back is listed
as `Nb`.

### `--listframes [templates=<dir>]`

Prints the installed frame names, one per line — what `frame=` takes.

---

## Documentation / QA modes

These render the app itself (its windows, a full QA pass, or the illustrated quick-start) — handy
after any UI or frame change so the docs never drift from the code. They work off-screen, so they
run even on a locked or headless machine.

### `--qa <outDir> [full [<case>] [cards]]`

Renders **every layout across the frames**, runs automated checks, and writes two contact sheets
(`qa-layouts.png`, `qa-frames.png`) plus a `QA-REPORT.txt` summary.

`full` is the **layout audit**: every layout case and stress card (the longest name, cost, type line and rules, big
and star P/T) on **every** installed frame, each checked for text that's cut off, runs off the card, lands on other
text or a symbol, slides under the P/T box, sits outside the frame's boxes (rules that overflowed below the text box count, even down in the footer;
frames whose text sits on the art aren't checked for this), or sits on one of the frame's own drawn
lines. It writes one contact sheet per case (`audit-*.png`, all frames side by side) and `LAYOUT-AUDIT.txt`; exit 3
when anything was found. A `<case>` word renders only the cases whose label contains it (one matching none is an error, exit 2, listing
the cases); `cards` also keeps every render (`cards/<case>/<frame>.jpg`).

```powershell
Cardinator.exe --qa qa-out
Cardinator.exe --qa audit full
Cardinator.exe --qa audit full "Level up" cards
```

### `--docs <outDir>`

One-command documentation build: regenerates **all documentation screenshots** (the app windows +
a few hero card renders + a frames overview) and writes an illustrated `QUICKSTART.md` that embeds
them, into `<outDir>` (images go in `<outDir>/images`).

```powershell
Cardinator.exe --docs docs-out
```

### `--uishot <outDir> [layouts]`

Renders the app's own windows (main, details, help, frame design, set settings, deck list, confirm, search)
to PNGs — the raw documentation screenshots, without the QUICKSTART wrapper. With `layouts`, it instead renders
the main window in each card layout and side (front, back face, the set's card back, a split card's other half,
a meld card's melded card) and the Set settings window — for reviewing the layout UI.

```powershell
Cardinator.exe --uishot shots
Cardinator.exe --uishot shots layouts
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
- Scryfall calls are throttled (~100 ms apart) and time out gracefully. A list whose cards are complete never
  asks Scryfall, so it renders offline with drawn mana pips; one that needs Scryfall while it can't be reached
  stops (exit 1) rather than drawing blank cards.
- These modes are also how the project is verified without opening the GUI (handy on a headless or
  remote machine).
