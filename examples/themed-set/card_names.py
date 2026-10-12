"""
Write a themed set's name list: every show title and the real card it stands for, as a plain text file.

  python card_names.py <set.cardinator> <out.txt>

Cards in deck order (a card repeated, like a basic land, once with "(xN)"; a back face right after its front), then the
tokens, then the counts. build_set.sh and add_tokens.py write it to <workspace>/review/<show> card names.txt every time
they change the set, so it always matches the cards.
"""
import json, os, sys

if len(sys.argv) != 3:
    sys.exit(__doc__)
set_path, out_path = sys.argv[1:]
project = json.load(open(set_path, encoding="utf-8-sig"))
g = lambda d, k: next((v for kk, v in d.items() if kk.lower() == k.lower()), None) or ""

rows, seen = [], {}
for c in g(project, "cards"):
    key = (g(c, "name"), g(c, "subtitle"))
    if key in seen:
        seen[key]["count"] += 1
        continue
    row = dict(name=key[0], real=key[1], count=1, token=g(c, "typeLine").lower().startswith("token"))
    seen[key] = row
    rows.append(row)
    for part, what in ((g(c, "backFace"), "back face"), (g(c, "otherHalf"), "other half")):
        if part:
            rows.append(dict(name=f"{g(part, 'name')}  ({what} of {key[0]})", real=g(part, "subtitle"), count=1,
                             token=False, part=True))

cards = [r for r in rows if not r["token"]]
tokens = [r for r in rows if r["token"]]
width = max((len(r["name"]) for r in rows), default=0) + 2
title = g(project, "name") or os.path.splitext(os.path.basename(set_path))[0]
out = [f"{title} - every show name and the real card it stands for", "", "CARDS", ""]
out += [f"{r['name']:<{width}}{r['real']}" + (f"  (x{r['count']})" if r["count"] > 1 else "") for r in cards]
if tokens:
    out += ["", "TOKENS", ""] + [f"{r['name']:<{width}}{r['real']}" for r in tokens]
whole = [r for r in cards if not r.get("part")]
out += ["", f"{sum(r['count'] for r in whole)} cards ({len(whole)} different)"
        + (f", plus {len(tokens)} token{'s' if len(tokens) != 1 else ''}." if tokens else ".")]
os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
with open(out_path, "w", encoding="utf-8-sig", newline="\r\n") as fh:   # opens cleanly in Notepad
    fh.write("\n".join(out) + "\n")
print(f"Wrote the name list: {out_path}")
