# Themed set scripts (examples)

Scripts that turn a deck into a themed set — show art on every card, show titles with the real names as
subtitles, a custom footer and set symbol, and a frame painted by OpenAI in the theme's style. The whole recipe,
the workspace layout and the lessons learned are in [docs/THEMED-SETS.md](../../docs/THEMED-SETS.md).

| Script | What it does |
|---|---|
| `paint_frame.py <workspace> <theme> [--more\|--compose]` | Paints frame variants with OpenAI and composes the usable ones into Cardinator frames |
| `layered_frame.py <workspace> <theme> [--paint]` | Builds a frame from an OpenAI texture and emblem with drawn plates, plus its flip and sideways versions |
| `drawing_frame.py <workspace> <name>` | Turns your own frame drawing into frames (the app fits it), plus flip and sideways versions |
| `frame_variants.py <frame folder>...` | Makes the flip and sideways versions of a picture frame from its own pixels |
| `make_frames.py <workspace>` | Rebuilds every frame of a workspace from its settings (no OpenAI calls) |
| `build_set.sh <workspace> <deck> <show> "<Frame>"` | Builds the set from the deck list (art, titles, footer, symbol) and renders it; `CARDS="A\|B"` previews just those |
| `add_tokens.py <workspace> <deck> <show> "<Frame>"` | Adds the deck's tokens (`decks/<deck>/tokens.tsv`: real name, show title, type, p/t, rules, flavor, picture) to the set as show cards, leaving every other card as it is; `build_set.sh` runs it too |
| `card_names.py <set.cardinator> <out.txt>` | Writes the name list: every show title and the real card it stands for, tokens too. `build_set.sh` and `add_tokens.py` write it to `review/<show> card names.txt` every time |
| `contact_sheet.py review\|frames\|paints <workspace> <name>` | One image to look a set (or frames) over |
| `art_list.py add\|make\|fetch\|check <workspace> <show>` | Keeps pictures as links (`art.tsv`) instead of files, and downloads them again |

`workspace-example/` is a starting point with two made-up themes: a painted one (`carnival`) and a layered one
(`carnival-brass`, paint its layers with `--paint`). These are examples: copy them and change them.
Needs Python 3 (`pip install pillow numpy openai`), bash, curl, and `OPENAI_API_KEY` for painting.
