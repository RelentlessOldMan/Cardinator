# Themed sets: a whole deck in the style of a show

This is the recipe for turning a real deck into a themed set: every card gets a picture from a show (or any theme
you like), a title from that world with the real card's name on the subtitle plate (like Universes Beyond's flavor
names), its own footer and set symbol, and a custom frame **painted by OpenAI** in the theme's style. Cardinator's
command line does the card work; the scripts in [`examples/themed-set/`](../examples/themed-set/) tie the steps
together. They're examples. Copy them and change them to suit what you're making.

You need: `Cardinator.exe`, Python 3 with `pip install pillow numpy openai`, bash (Git Bash on Windows), `curl`, and
an OpenAI API key (`OPENAI_API_KEY`) for painting frames. Painting costs money; everything else is free.

## The workspace

Everything for one person's sets lives in one folder (a **workspace**). Keep it out of any public repo if it holds
someone else's pictures.

```
<workspace>/
  decks/<deck>/decklist.txt     the deck: a Moxfield/Archidekt export or "1 Sol Ring" lines
  art/<show>/                   one picture per card, named after the REAL card ("Sol Ring.png";
                                a double-faced card's back under its own name; one "Wastes.png" for all of them)
  art/<show>/titles.tsv         real name <TAB> show title, one line per card face
  art/<show>/set.json           optional: {"setCode": "CCV", "collectorNumber": "001", "symbol": "symbol.png"}
  art/<show>/art.tsv            optional: where each picture came from (see "Back it up as links")
  themes/<theme>.json           a frame's settings: its kind, style brief or layers, colours and fonts
  frames/_paints/ _layers/ _drawings/   what the frames are made from (OpenAI paints and layers, your drawings)
  frames/<frame>/               the frames themselves (frame.png + template.json, flip/ and landscape/)
  sets/<show>.cardinator        built by build_set.sh, every run
  review/<show>/                the rendered cards; contact sheets in review/_sheets/
```

[`examples/themed-set/workspace-example/`](../examples/themed-set/workspace-example/) is a small starting point: a
made-up "Clockwork Carnival" theme (painted, plus a layered "Carnival Brass"), a five-card deck, its titles and
footer.

## The steps

1. **The deck.** `Cardinator.exe --moxfield <link> decks/<deck>/moxfield.json` fetches a Moxfield deck, or paste
   an export into `decks/<deck>/decklist.txt` (see [CLI.md](CLI.md) "Working with sets").
2. **The art.** One picture per card, matched to the card's role: the commander is the lead character, big threats
   are the villains, spells are events, artifacts and lands are items and places. Name each file after the real
   card. `python art_list.py add <workspace> <show> "Sol Ring.png" <url>` downloads one and remembers its link.
3. **The titles.** `titles.tsv`: every face gets a title from the theme's world, based on what its picture shows.
   Keep them to about 22 characters so they fit the title bar, and don't repeat one.
4. **The theme.** Copy `themes/carnival.json`. The brief that works describes:
   - the art style (outline weight, flat or painted, mood);
   - "The border is DENSELY ILLUSTRATED all the way around: ..." with 6–10 concrete motifs from the theme, packed
     "like a carved frieze";
   - the trim around the plates and the art window, the seals at the ends of the type line, the plates (solid dark,
     coloured rim, thin keyline) and the medallion at the bottom;
   - the palette, and what NOT to use ("NO gold, NO orange"). Never ask for pink or magenta details: magenta marks
     the art window.

   Fonts must be installed on the PC (Bahnschrift, Georgia, Trebuchet MS, Impact, Palatino Linotype, Segoe UI are
   on Windows). Put already-heavy faces like Impact in `"noBold"`. `"ptNudge": [dx, dy]` moves the P/T box.
5. **The frame.** `python paint_frame.py <workspace> <theme>` paints four variants, measures where the model really
   put each part, rejects the ones whose boxes are too small or overlap (expect one or two usable of four), and
   writes each usable one to `frames/<theme>-<n>/`. `--more` paints four more; `--compose` re-composes for free.
   `python contact_sheet.py frames <workspace> <theme>` puts them side by side to pick one. (Other kinds of
   frame are under "Three kinds of frame" below.)
6. **Preview one card.** `CARDS="Sol Ring" bash build_set.sh <workspace> <deck> <show> "<Theme Name> <n>"` builds
   the set and renders just that card into `review/<show>/`. Change things until that card is right.
7. **The whole deck.** The same command without `CARDS`. Identical cards (basic lands) are rendered once.
8. **Look at every card.** `python contact_sheet.py review <workspace> <show>` makes pages of 12. Check each title
   sits inside its bar, the rules text fits its box, and nothing is cut off.

`build_set.sh` rebuilds the set from the deck list every time: pictures are matched by the **real** card names, so
it never restyles a set that was already retitled. It stops if any card has no show title.

## Three kinds of frame

Each `themes/<name>.json` says which kind of frame it is (`"kind"`), and one script builds each kind. Only the
settings file is yours; the scripts are the same for everyone.

| Kind | Script | Made from | Looks like |
|---|---|---|---|
| `painted` (default) | `paint_frame.py` | OpenAI paints the whole frame from a brief (`frames/_paints/`) | densely illustrated, every frame different |
| `layered` | `layered_frame.py` | an OpenAI texture and emblem (`frames/_layers/`), with the border, keylines, bevel and text plates drawn over them from the theme's colours | clean and regular; recolour or move anything without painting again |
| `drawing` | `drawing_frame.py` | your own frame picture (`frames/_drawings/`): its windows are cut see-through, and the app fits it to the card (zoom, offset, border, set in its frame designer and kept in each frame's `template.json`) | exactly your drawing |

Layered frames come with a **flip** and a **sideways** version, redrawn at the new layout. For a picture frame
(a drawing, or anything that is one finished image), `frame_variants.py <frame folder>` makes them by cutting the
frame's own pixels into bands and rearranging them. `drawing_frame.py` runs it for you.

`python make_frames.py <workspace>` rebuilds every frame of a workspace from its settings, without calling OpenAI,
so composed frames never need to be kept.

## Back it up as links

Pictures are most of a workspace's size. `art.tsv` keeps each picture as its link instead:

```
python art_list.py make  <workspace> <show>            # list the pictures in the folder (with their checksums)
python art_list.py check <workspace> <show> --learn    # download each one again and compare
python art_list.py fetch <workspace> <show>            # on a fresh checkout: download them all
```

`check` names any picture that doesn't come back the same (dead link, changed picture, or one you generated):
keep those files themselves. What the frames are made from can't be made again (OpenAI never paints the same
picture twice), so keep `frames/_paints/`, `frames/_layers/` and `frames/_drawings/`, plus each drawing frame's
`template.json` (its fit). Everything else (sets, renders, composed frames) is rebuilt by the scripts:
`make_frames.py` for the frames, `build_set.sh` for the cards.

## Lessons (each one was a real mistake)

- **The art looked stamped on top of the frame.** The window was punched as a plain rectangle, cutting off the
  corner ornaments the model painted reaching into it. `paint_frame.py` punches the painted magenta itself, so the
  ornaments overlap the picture like a real cut-out.
- **A frame passed with its art window over the title bar.** Checking minimum sizes isn't enough: `check()` also
  wants a maximum height and the parts in order, top to bottom.
- **A pink keyline in the brief stretched the "art window" over the plates.** Detection takes the biggest solid
  magenta block, and briefs never ask for pink.
- **Rules text came out tiny** because the script cut the text box at a guessed medallion line and inset it, on
  top of Cardinator doing both. Leave that to the app.
- **Title descenders hit the subtitle plate** on a short title bar with a tall font: the title size follows the
  painted bar's height.
- **Faces cut off in tall pictures.** The art window is wider than tall, so a portrait picture loses its top and
  bottom. Crop the file around the face (`art_list.py add ... x,y,w,h` records the crop).
- **Art not found after retitling.** Files are named after the real cards: build from the deck list every time,
  art before titles.
- **Preview one card first**, and render the whole deck only once that card is right.
