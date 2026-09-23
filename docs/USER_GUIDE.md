# Cardinator — User Guide

A friendly walkthrough for making cards. If you just want the short version, see the
[README](../README.md).

---

## 1. Getting started

1. Put `Cardinator.exe` anywhere you like (a folder on your Desktop is fine) and double-click it.
2. The first time it runs, it creates a **`CardinatorData`** folder right next to the exe. That's
   where it keeps templates, downloaded mana symbols, your saved projects, and exported cards.
   You can move the exe + that folder together to another PC and everything comes with it.

There's nothing to install and no runtime to download — it's a single self-contained program.

---

## 2. Make one card (the common case)

The main screen is built around the everyday flow: **look up a card, swap in your own art,
export.**

1. **Type a card name** in the box at the top and click **Search** (or press **Ctrl+L**).
   Cardinator fetches it from Scryfall and fills in the name, mana cost, type, rules text,
   power/toughness — **and the real card artwork** (pulled from Scryfall) if the card doesn't
   already have art. You can always swap the art for your own afterwards. *(No internet? Just type
   the fields yourself — see step 5.)*
2. **Pick a frame** from the **Frame** dropdown (Crimson Red, Ocean Blue, Forest Green, Gold
   Multicolor, Slate Artifact, Planeswalker, **Full Art**…). Choose **Full Art** for a card whose
   text sits directly on the artwork. Have your own frame image? Click **Import…** next to the
   dropdown to turn a transparent PNG into a template — see [Making your own frames](TEMPLATES.md).
3. **Add your art.** Three easy ways:
   - **Change art…** to browse for an image file, or
   - **Paste** — copy an image (or an image file, or even an image URL) and click Paste, or
   - **Drag an image file straight onto the window.**
4. **Position the art.** Drag on the preview to **pan**, scroll to **zoom**. (For fine control,
   open **Edit details…** and use the Art zoom / Pan sliders.)
5. **Tweak anything** by hand in **Edit details…** — every field is editable, so you can change
   the rules text, add flavor text, set rarity, artist, etc. — or make a completely original card
   with no Scryfall lookup at all.
6. **Export PNG…** saves a print-quality **1500×2100** image (into `CardinatorData/output` by
   default) — pick **PNG or JPEG** in the save dialog. Or click **Copy image** to drop the finished
   card straight onto your clipboard for pasting into Discord/chat. For print services that need a
   **bleed margin** or a specific **DPI**, use the `--render` command-line options (see the
   [command-line reference](CLI.md)); there's also a **card back** for double-sided printing
   (`--cardback`).

> **Mana symbols** use the real Scryfall artwork. The first time you use a symbol it's downloaded
> and cached; after that it works offline. Before it's cached (or with no internet), Cardinator
> draws clean colored pips instead, so nothing ever breaks.

---

## 3. Writing mana and rules text

- **Mana cost** uses Scryfall's `{...}` tokens: `{2}{R}{W}`, `{X}`, `{T}` (tap), `{C}`
  (colorless), hybrids like `{R/W}`, Phyrexian like `{U/P}`. You can also type it loosely —
  `2RW` becomes `{2}{R}{W}` automatically.
- **Rules text** understands the same `{...}` symbols *inline* — e.g. `{T}, Sacrifice a creature:
  draw a card.` Press **Enter** to start a new ability on its own line. Long text automatically
  shrinks to fit the box.
- **Flavor text** shows in italics under a divider line.

### Special card types

Cardinator recognizes these automatically from the type line / fields and lays them out for you:

| Type | How to trigger it | What you get |
|------|-------------------|--------------|
| **Planeswalker** | Type line contains "Planeswalker", set **Loyalty** | Loyalty box + one badge per ability line (`+2:`, `-3:`, `-8:`) |
| **Saga** | Type line contains "Saga" | Chapter badges (`I`, `II`, `III`) beside each chapter |
| **Class** | Type line contains "Class" | Level badges beside each level's abilities |
| **Adventure** | Fill the **Adventure** fields (or import an adventure card) | A spell sub-box on the creature |
| **Double-faced** | Look up a real DFC | Imported as two cards — front and back |

See the [gallery in the README](../README.md#gallery) for examples of each.

---

## 4. Making many cards at once

Cardinator always works on a **project** — the list of cards down the left side. Build it up card
by card, or import a whole batch.

- **Import list / CSV…** — load a file of cards. It accepts:
  - a plain **list of names**, one per line (each looked up on Scryfall);
  - `Name | C:\art\thing.png` per line (name + art);
  - a **CSV/TSV with a header row**, columns matched by friendly names in any order. See the
    [example CSV](../examples/cards.csv) and its [column reference](../examples/README.md).
- **Scryfall search…** — type a Scryfall query (e.g. `t:dragon c:r`, `set:dom`) and Cardinator
  imports up to 60 matching cards **with their real art** in one go — a fast way to build a themed
  set. (Also on the command line via `--search`.)
- **Match art folder…** — point it at a folder of images and it attaches them to cards by
  matching the filename to the card name (`serra angel.png` → "Serra Angel"). Only fills cards
  that don't already have art.
- **Look up missing** — fills any card that still has only a name.
- **Export all…** — renders every card to PNGs in a folder you choose. Runs in the background
  with a progress bar so the window stays responsive, even for 100 cards.
- **Print sheet…** — lays cards out **9 per page (3×3) at real size (2.5″×3.5″, 300 DPI)** on
  Letter (or A4) pages with cut marks — one PNG per page, ready to print and cut.

Use **New / Open / Save** in the top-right to manage projects. A project is a single
`.cardinator` file you can re-open and keep editing.

---

## 5. Keyboard shortcuts & drag-and-drop

| Shortcut | Action |
|----------|--------|
| **F1** | Open the built-in **Help** cheat sheet (also the **Help** button, top-right) |
| **Ctrl+N** | New project |
| **Ctrl+O** | Open project |
| **Ctrl+S** | Save (re-saves to the same file after the first save) |
| **Ctrl+L** | Look up the current card on Scryfall |
| **Ctrl+E** | Export the current card |
| **Ctrl+D** | Duplicate the current card |

**Drop files onto the window:**
- an **image** → sets the current card's art;
- a **`.cardinator` / `.json`** → opens that project;
- a **`.txt` / `.csv` / `.tsv`** → imports that card list.

Cardinator marks the title bar with a **●** when you have unsaved changes, and asks before you
close or open something else over unsaved work.

---

## 6. Troubleshooting

- **"Couldn't reach Scryfall" / "timed out."** You're offline or Scryfall is unreachable. You can
  still make cards by typing the fields yourself; symbols fall back to drawn pips.
- **"No card found."** Check the spelling — lookup is fuzzy but needs to be close.
- **A card renders blank in the art window.** The art file was moved or deleted. Re-attach it with
  **Change art…**. Cardinator never crashes on missing art — it just shows the empty window.
- **I want a different frame.** Add your own — see [Making your own frames](TEMPLATES.md).
- **Where did my export go?** Click **Open output folder**, or look in `CardinatorData/output`.
