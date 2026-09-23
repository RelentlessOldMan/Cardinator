# Example 02 · Proxy a real deck with your own art

**"Here's my deck list and here's my art — make me the cards."** You give a list of real card
names; Cardinator looks each one up on Scryfall for the correct text and mana symbols, and drops in
the artwork you provide.

This is a mono-red aggro core: six real cards, our own placeholder art.

## What's here

```
02-proxy-deck/
  deck.csv           <- real card names + your art + a frame
  art/               <- our placeholder artwork (swap in your own)
  output/            <- the rendered result (checked in)
```

## The input — `deck.csv`

Just the name, the art file, and a frame. Everything else is filled from Scryfall.

```csv
name,art,template
Lightning Bolt,art/bolt.png,Crimson Red
Goblin Guide,art/guide.png,Crimson Red
Monastery Swiftspear,art/swiftspear.png,Crimson Red
Lava Spike,art/spike.png,Crimson Red
Skewer the Critics,art/skewer.png,Crimson Red
Eidolon of the Great Revel,art/eidolon.png,Crimson Red
```

> Prefer to name-drop even faster? A plain `.txt` with one card name per line works too — you can
> add art afterward with **Match art folder…**.

## Run it

**App:** Import list / CSV… → `deck.csv` (it looks the cards up automatically), then **Print sheet…**

**Command line** (from the repo root; needs internet for the lookups):

```powershell
Cardinator.exe --batch examples/02-proxy-deck/deck.csv examples/02-proxy-deck/output examples/02-proxy-deck
Cardinator.exe --sheet examples/02-proxy-deck/deck.csv examples/02-proxy-deck/output examples/02-proxy-deck
```

## The output

Real rules text and mana symbols from Scryfall, your art, on the frame you picked — a printable
3×3 sheet ready to cut (`output/sheet_page_01.png`):

![Proxy deck — print sheet](output/sheet_page_01.png)

<sub>Card names, rules text and mana symbols are © Wizards of the Coast, retrieved from the Scryfall
API. The artwork here is our own. Personal / fan use only.</sub>
