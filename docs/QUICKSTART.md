# Cardinator — Quick Start

*This guide is generated automatically by `Cardinator.exe --docs`; the screenshots are the real app.*

Cardinator makes custom Magic-style cards from your own art — one at a time, or hundreds at once.

![The Cardinator editor](images/app-main.png)

## Make one card

1. **Name it.** Type a name in the CARD NAME box. To reuse a real card's stats, type its name and
   click **Search** — Cardinator fills the mana cost, type, rules text, power/toughness and even
   downloads the art from Scryfall. (You can override anything afterwards.)
2. **Pick a frame.** Choose one from the FRAME dropdown. Click **Design…** to mix and match the
   frame's look (below).
3. **Add your art.** Click **Change art…**, **Paste** a copied image or URL, or just drag an image
   onto the window. Drag on the preview to pan, scroll to zoom.
4. **Edit the details.** Click **Edit details…** for mana cost, type line, rules/flavor text,
   power/toughness, loyalty, set, collector number, artist and more. Type mana as `{2}{U}{U}` —
   it renders as symbols, in the title and inside rules text.
5. **Watch the CHECKS panel.** It flags problems as you go — missing art, an unrecognized symbol, a
   footer overlapping the text box, duplicate collector numbers — so nothing surprises you at export.
6. **Export.** Click **Export PNG…** for a print-quality image, or **Copy image** to paste it
   straight into a chat.

### Editing the details
![Card details editor](images/app-details.png)

### Designing the frame
Mix and match the frame's style, connected panels, textured background, bottom taper, top emblem,
royal sub-border, border thickness and colors — all live.

![Frame design](images/app-frame-design.png)

## Frames at a glance
| Gold Multicolor | Azure Modern | Full Art |
|---|---|---|
| ![](images/hero-gold.png) | ![](images/hero-modern.png) | ![](images/hero-fullart.png) |

## Make a whole set at once

1. Put your card names in a text or CSV file — one per line, or with columns for art and frame:
   ```
   Lightning Bolt, bolt.png, Crimson Red
   Counterspell,   counter.png, Ocean Blue
   ```
2. Click **Import list / CSV…** (or drop the file on the window). Cardinator fills any blank fields
   from Scryfall and matches art files by name.
3. Click **Look up missing** if you left fields blank, and **Match art folder…** to attach a folder
   of images by filename.
4. **Export all…** writes every card to a PNG, or **Print sheet…** lays them out at real card size
   on Letter/A4 pages (with cut marks and optional bleed) ready to print.

## Keyboard shortcuts
`Ctrl+S` save · `Ctrl+O` open · `Ctrl+N` new · `Ctrl+L` look up · `Ctrl+E` export · `Ctrl+D` duplicate · `F1` help

---
*Cardinator is for personal/fan use. Card frames and symbols are original; it isn't affiliated with
Wizards of the Coast.*