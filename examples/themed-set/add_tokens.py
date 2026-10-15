"""
Add a deck's tokens to its themed set, as show cards, without rebuilding the set.

  python add_tokens.py <workspace> <deck> <show> "<Frame Name>" [set.cardinator]

Reads <workspace>/decks/<deck>/tokens.tsv (tab-separated, # comments allowed), one token per line:

  real name   show title   type   p/t   rules   flavor   picture   [made by]

  e.g.  Golem   The Clockwork Guard   Token Artifact Creature — Golem   3/3   Flying      Clockwork Guard.jpg

"rules" uses " / " for a new line (like cards.tsv). A type that doesn't start with "Token" gets it. The picture is a
file in <workspace>/art/<show>/ (named after the show title, since several tokens share a real name). Each token
becomes a card on the frame with the show title as its name and the real name on its subtitle plate, with the set's
footer and set symbol: the set's own (Set settings, or set.json through build_set.sh), else what all its cards share.

Tokens already in the set with the same show title are replaced, so running it again updates them; every other card
is left exactly as it is (so a token dropped from tokens.tsv stays until the next build_set.sh, or deleting it in
Cardinator). The set (default <workspace>/sets/<show>.cardinator) is backed up to sets/backups/ first.
build_set.sh runs this too, so a rebuilt set keeps its tokens. The name list (card_names.py) is written again after.
"""
import json, os, re, shutil, sys, time

if len(sys.argv) not in (5, 6):
    sys.exit(__doc__)
ws, deck, show, frame = sys.argv[1:5]
set_path = sys.argv[5] if len(sys.argv) == 6 else os.path.join(ws, "sets", show + ".cardinator")
tsv = os.path.join(ws, "decks", deck, "tokens.tsv")
art = os.path.join(ws, "art", show)
if not os.path.exists(tsv):
    sys.exit(f"No token list: {tsv}")
if not os.path.exists(set_path):
    sys.exit(f"No set to add the tokens to: {set_path} (run build_set.sh first)")

tokens = []
for n, line in enumerate(open(tsv, encoding="utf-8-sig"), 1):
    if not line.strip() or line.lstrip().startswith("#"):
        continue
    f = line.rstrip("\r\n").split("\t") + [""] * 8
    real, title, type_line, pt, rules, flavor, picture = (x.strip() for x in f[:7])
    if not (real and title and type_line):
        sys.exit(f"{tsv} line {n}: needs a real name, a show title and a type")
    if picture and not os.path.exists(os.path.join(art, picture)):
        sys.exit(f"{tsv} line {n}: no picture {os.path.join(art, picture)} (art_list.py fetch?)")
    if not type_line.lower().startswith("token"):
        type_line = "Token " + type_line   # a token's type line says so — and that's how a re-run finds it to replace
    power, _, tough = pt.partition("/")
    tokens.append(dict(real=real, title=title, type=type_line, power=power.strip(), toughness=tough.strip(),
                       rules=rules.replace(" / ", "\n"), flavor=flavor, picture=picture))

project = json.load(open(set_path, encoding="utf-8-sig"))
key = lambda d, k: next((kk for kk in d if kk.lower() == k.lower()), k)   # the file's own spelling of a field
cards = project[key(project, "cards")]
titles = {t["title"].lower() for t in tokens}
is_token = lambda c: str(c.get(key(c, "typeLine"), "")).lower().startswith("token")
kept = [c for c in cards if not (is_token(c) and str(c.get(key(c, "name"), "")).lower() in titles)]
profile = project.get(key(project, "profile")) or {}
others = [c for c in kept if not is_token(c)]


def shared(field):
    """The set's own value for a footer field (Set settings, or a whole-set --restyle), else the value every card has
    in common (set.json's), else none: never one card's real printing (its set code or collector number)."""
    if profile.get(key(profile, field)):
        return profile[key(profile, field)]
    values = {c.get(key(c, field)) or "" for c in others}
    return values.pop() if len(values) == 1 else ""

# Pictures go beside the set's other art (sets/art/), named after the token, so the set folder stays self-contained.
set_art = os.path.join(os.path.dirname(os.path.abspath(set_path)), "art")
os.makedirs(set_art, exist_ok=True)
for t in tokens:
    rel = ""
    if t["picture"]:
        name = "token-" + re.sub(r"[^a-z0-9]+", "-", t["title"].lower()).strip("-") + os.path.splitext(t["picture"])[1].lower()
        shutil.copyfile(os.path.join(art, t["picture"]), os.path.join(set_art, name))
        rel = "art\\" + name
    kept.append({
        "name": t["title"], "subtitle": t["real"], "typeLine": t["type"], "manaCost": "",
        "rulesText": t["rules"], "flavorText": t["flavor"], "power": t["power"], "toughness": t["toughness"],
        "templateName": frame, "artPath": rel,
        "setCode": shared("setCode"), "collectorNumber": shared("collectorNumber"), "setSymbolPath": shared("setSymbolPath"),
    })

if not set_path.endswith(".building.cardinator"):   # build_set.sh's set in the making needs no backup
    backups = os.path.join(os.path.dirname(os.path.abspath(set_path)), "backups")
    os.makedirs(backups, exist_ok=True)
    shutil.copyfile(set_path, os.path.join(backups, f"{show}.{time.strftime('%Y%m%d-%H%M%S')}.cardinator"))
project[key(project, "cards")] = kept
tmp = set_path + ".tmp"
with open(tmp, "w", encoding="utf-8") as fh:
    json.dump(project, fh, ensure_ascii=False, indent=2)
os.replace(tmp, set_path)
print(f"Added {len(tokens)} token(s) to {set_path} ({len(cards) - (len(kept) - len(tokens))} replaced); "
      f"the other {len(kept) - len(tokens)} cards are unchanged.")
if not set_path.endswith(".building.cardinator"):   # build_set.sh writes the name list itself once the set is built
    import subprocess
    subprocess.run([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "card_names.py"), set_path,
                    os.path.join(ws, "review", f"{show} card names.txt")], check=True)
