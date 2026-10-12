#!/usr/bin/env bash
# Build a themed set from a deck list and render its cards, using Cardinator's command line.
#
#   build_set.sh <workspace> <deck> <show> "<Frame Name>"
#
#   1. imports <workspace>/decks/<deck>/decklist.txt fresh into <workspace>/sets/<show>.cardinator (always from
#      scratch: art is matched by the REAL card names, so never re-style an already-retitled set; the previous
#      one is kept in sets/backups/, and the app opens it from there; a build that fails leaves it in place),
#   2. gives every card its picture from <workspace>/art/<show>/ (files named after the real cards, e.g.
#      "Sol Ring.png"; a double-faced card's back under its own name) and puts it on the frame,
#   3. retitles every card from art/<show>/titles.tsv ("real name<TAB>show title": the card shows the show title,
#      its real name goes on the subtitle plate, and its rules text calls it by the show title),
#   4. optional art/<show>/set.json gives the set its own footer and set symbol:
#      {"setCode": "XYZ", "collectorNumber": "001", "symbol": "symbol.png"}   (symbol: a file in the art folder),
#   5. adds the deck's tokens from <workspace>/decks/<deck>/tokens.tsv, if there is one (see add_tokens.py),
#   6. fails if any card is left without a show title, then renders the cards into <workspace>/review/<show>/
#      (identical cards, like basic lands, once), with the frames in <workspace>/frames/ (no install needed).
#
#   CARDS="Card A|Card B"   render just these cards (by real or show name) - preview one card before the deck
#   CARDINATOR=<exe>        Cardinator.exe to use (default: Cardinator.exe on the PATH)
# See docs/THEMED-SETS.md for the whole recipe.
set -eo pipefail
[ $# -eq 4 ] || { sed -n '2,22p' "$0"; exit 2; }
WS=$1; DECK=$2; SHOW=$3; FRAME=$4
EXE=${CARDINATOR:-Cardinator.exe}
ART="$WS/art/$SHOW"
SET="$WS/sets/$SHOW.cardinator"
OUT="$WS/review/$SHOW"

# Cardinator's output without the per-card progress lines: what it saved or rendered, and any note or warning.
said() { grep -E '^(Note|Not installed|Saved|Rendered|  !)' || true; }

mkdir -p "$WS/sets/backups"
# The set is built under a working name next to it (so its pictures land in the same sets/art/), and only takes
# the set's place once it's complete. Its own save backups are throwaway: they're removed, so they never use up
# the backup slots that keep the sets this replaced.
NEW="$WS/sets/$SHOW.building.cardinator"
rm -f "$NEW"
# name= keeps the set called "$SHOW" (not after the working file), and templates= lets the frame be one in frames/.
"$EXE" --import "$WS/decks/$DECK/decklist.txt" "$NEW" "art=$ART" noart "frame=$FRAME" "name=$SHOW" "templates=$WS/frames" | said

STYLE=()
if [ -f "$ART/set.json" ]; then
  # Read into a variable first: a bad set.json then stops the build (inside "< <(...)" the error was ignored).
  STYLE_LINES=$(python - "$ART" <<'PY'
import json, os, sys
art = sys.argv[1]
try:
    c = json.load(open(os.path.join(art, "set.json"), encoding="utf-8-sig"))
except ValueError as e:
    sys.exit(f"{os.path.join(art, 'set.json')} isn't valid JSON: {e}")
if c.get("setCode"): print("setcode=" + c["setCode"])
if c.get("collectorNumber"): print("number=" + c["collectorNumber"])
if c.get("symbol"): print("symbol=" + os.path.join(art, c["symbol"]))
PY
  )
  if [ -n "$STYLE_LINES" ]; then mapfile -t STYLE <<< "$(printf '%s' "$STYLE_LINES" | tr -d '\r')"; fi
fi
"$EXE" --restyle "$NEW" "titles=$ART/titles.tsv" "${STYLE[@]}" | said
rm -f "$WS/sets/backups/$SHOW.building."*.cardinator

python - "$NEW" <<'PY'
import json, sys
p = json.load(open(sys.argv[1], encoding="utf-8-sig"))
g = lambda c, k: next((v for kk, v in c.items() if kk.lower() == k), None) or ""
# every part: the card, its back face, and the second half of a split / flip / aftermath card
faces = [f for c in g(p, "cards") for f in (c, g(c, "backface"), g(c, "otherhalf")) if f]
left = sorted({g(f, "name") for f in faces if not g(f, "subtitle")})
if left: sys.exit("No show title in titles.tsv for: " + "; ".join(left))
bare = sorted({g(f, "subtitle") for f in faces if not g(f, "artpath")})
if bare: sys.exit("No picture in the art folder for: " + "; ".join(bare) + " (art_list.py fetch?)")
print(f"All {len(faces)} faces have show titles and pictures.")
PY

# The deck's tokens (decks/<deck>/tokens.tsv, optional) go in as show cards too - see add_tokens.py.
if [ -f "$WS/decks/$DECK/tokens.tsv" ]; then
  python "$(dirname "$0")/add_tokens.py" "$WS" "$DECK" "$SHOW" "$FRAME" "$NEW"
fi

# The set there now (with anything changed in the app since) is kept as a backup the app can open, named like
# its own backups so it's listed and pruned with them; then the new one takes its place.
if [ -f "$SET" ]; then
  mv "$SET" "$WS/sets/backups/$SHOW.$(date +%Y%m%d-%H%M%S).cardinator"
fi
mv "$NEW" "$SET"

mkdir -p "$OUT"
rm -f "$OUT"/*.png   # empty it rather than delete it, so it can stay open in Explorer
ONLY=(); [ -n "$CARDS" ] && ONLY=("only=$CARDS")
RENDERED=$("$EXE" --renderset "$SET" "$OUT" "${ONLY[@]}" "templates=$WS/frames")
echo "$RENDERED" | said
if echo "$RENDERED" | grep -q '^Not installed'; then
  echo "The frame \"$FRAME\" isn't in $WS/frames/ (check its name, or run make_frames.py): the cards were drawn on another." >&2
  exit 1
fi
