# Example files

Sample inputs you can try with Cardinator.

## `cards.csv`

A small card list showing the two ways to make cards in one file:

- **Fully custom** — the *Aria Stormcaller* row fills in everything by hand (mana, type, rules,
  flavor, power/toughness, art, template).
- **Name only** — *Lightning Bolt*, *Counterspell* and *Llanowar Elves* give just a name (plus a
  template, and art for the elf). Cardinator looks each one up on **Scryfall** and fills the rest.

### Try it

**In the app:** click **Import list / CSV…** and choose `cards.csv`. Anything blank is filled
from Scryfall; anything you typed is kept as your override.

**From the command line:**

```powershell
Cardinator.exe --batch examples/cards.csv out
```

This imports the list, fills the blanks from Scryfall, and renders every card to a PNG in `out/`.

> The `art/…` paths are placeholders — point them at real images (relative paths resolve next to
> the CSV) or drop them in later with **Match art folder…**. Rows without art still render on a
> plain art window.

### Column reference

Columns are matched by friendly names in any order; unknown columns are ignored. Common ones:

`name`, `art`, `mana`, `type`, `rules`, `flavor`, `power`, `toughness`, `pt` (e.g. `3/3`),
`loyalty`, `template`, `rarity`, `set`, `number`, `artist`, `copyright`, `lookup`.

In `rules` and `flavor`, write `\n` for a line break. Set `lookup` to `false` to stop a
name-only row from being fetched from Scryfall.
