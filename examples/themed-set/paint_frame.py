"""
Paint a whole custom card frame with OpenAI, in a theme's style, and turn it into a Cardinator frame.

OpenAI paints the ENTIRE frame (border, title bar, the art window's own decorated surround, type line, rules box,
medallion) onto Cardinator's card layout; this script then measures where the painted parts really landed and
writes a frame the app can use (frame.png + template.json) with the text placed on them.

  1. guide: the layout drawn as flat blocks (art window solid magenta inside a light-grey ring, plates grey, a
     medallion circle) at the model's 1024x1536 size;
  2. images.edit(guide + the theme's brief) -> N painted variants (the model drifts, so make several);
  3. detect where it actually put the art window (the biggest solid magenta block) and the plates (dark bands near
     each guide box); check() rejects variants whose plates are too small or overlap (expect 1-2 usable of 4);
  4. compose: stretch to 1500x2100, punch the art window where the magenta was painted (so the ornaments the model
     painted reaching into the window stay on top of the art), round the corners, write the template.

  python paint_frame.py <workspace> <theme>            # paint N (default 4) new variants and compose them
                                                       # (the earlier paints move to frames/_paints/_replaced/)
  python paint_frame.py <workspace> <theme> --more     # keep the variants so far, paint N more
  python paint_frame.py <workspace> <theme> --compose  # re-compose the existing paints only (free)

Reads <workspace>/themes/<theme>.json; writes the raw paints to <workspace>/frames/_paints/<theme>_<n>.png and
the frames to <workspace>/frames/<theme>-<n>/ (named "<theme name> <n>" in the app). Needs OPENAI_API_KEY set
(painting costs money; --compose doesn't), and: pip install pillow numpy openai
See docs/THEMED-SETS.md for the whole recipe.
"""
import base64, io, json, os, shutil, sys, time
import numpy as np
from PIL import Image, ImageDraw, ImageChops, ImageFilter

# Cardinator's logical card (750x1050; frames are drawn at 2x) and where the guide puts each part.
CW, CH, SS = 750, 1050, 2
CARD_R, BORDER = 48, 28
TITLE = (48, 48, 654, 62)
ART = (48, 124, 654, 470)
TYPE = (48, 604, 654, 54)
TEXT = (48, 664, 654, 286)
EMBLEM_CY = 950
GW, GH = 1024, 1536                                  # the image model's portrait size
RING = 16                                            # logical px: the art window's own decorated frame
ART_IN = (ART[0] + RING, ART[1] + RING, ART[2] - 2 * RING, ART[3] - 2 * RING)
MAGENTA = (255, 0, 255)
LAYOUT_NOTE = (
    " The input image is the exact layout wireframe of the card and MUST be kept: keep every block at exactly the "
    "same position and size. The large solid MAGENTA rectangle is the art window - leave it completely flat, solid "
    "magenta (#FF00FF) with crisp edges, nothing painted inside it. The light-grey band all around the magenta "
    "rectangle is the art window's OWN frame: fill that band with a thick, heavily decorated, sculpted border in "
    "the card's style, with bold ornamental corner pieces, like a picture frame made for this card. The grey bars are text plates: the top bar is "
    "the title bar, the bar under the art window is the type line, and the big box below it is the rules text box. "
    "Do NOT add any other boxes or plates (no power/toughness box, no extra panels). Give each plate a rich, ornate, sculpted rim "
    "and decoration, but keep the INSIDE of every plate flat, dark and quiet so text printed on it stays readable "
    "(no patterns, no symbols, no text inside the plates); ornaments and seals sit on the frame around the plates, "
    "never inside them, and no crest or ornament may overlap or cut into a plate - every plate is one clear, unbroken "
    "dark bar from end to end. The small circle at the bottom centre is a medallion "
    "emblem seat. Nothing sits on top of the title bar: the strip of border above it is a narrow decorative band, "
    "with no crest, face or emblem hanging over the bar. Everything "
    "outside the plates is the frame itself: make it bold, sculpted and dramatic. "
    "No words, letters or numbers anywhere. Front-on, perfectly symmetrical, card fills the whole image.")


def D(v):   # logical -> frame px
    return tuple(int(round(x * SS)) for x in v) if isinstance(v, (tuple, list)) else int(round(v * SS))


def theme(ws, key):
    """<workspace>/themes/<key>.json: name (frames are "<name> <n>"), brief (the style prompt), colors, fonts
    ({role: [family, colour]} for title/type/rules/flavor/pt/credit), shadow, style (deco/comic/bubble/plank),
    and optionally noBold (roles whose face is heavy already, e.g. Impact) and ptNudge ([dx, dy] for the P/T box)."""
    with open(os.path.join(ws, "themes", key + ".json"), encoding="utf-8") as fh:
        t = json.load(fh)
    t["fonts"] = {k: tuple(v) for k, v in t["fonts"].items()}
    return t


def S(v, axis):   # logical -> guide px
    return int(round(v * (GW / CW if axis == "x" else GH / CH)))


def guide():
    im = Image.new("RGB", (GW, GH), (12, 12, 12))
    d = ImageDraw.Draw(im)
    def box(r, fill, rad=10):
        x, y, w, h = r
        d.rounded_rectangle([S(x, "x"), S(y, "y"), S(x + w, "x"), S(y + h, "y")], radius=rad, fill=fill)
    box(TITLE, (128, 128, 128))
    box(ART, (190, 190, 190), 4)                    # the art window's own frame: a ring for the model to decorate
    box(ART_IN, MAGENTA, 6)
    box(TYPE, (128, 128, 128))
    box(TEXT, (128, 128, 128))
    cx, cy, r = S(CW / 2, "x"), S(EMBLEM_CY, "y"), S(52, "x")
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(200, 200, 200))
    return im


def generate(ws, key, n):
    """images.edit with the layout guide (and, if <theme>_style_ref.png is in frames/_paints, a frame whose look
    should carry over). Returns the painted PNGs; nothing is written, so a failed paint changes nothing."""
    from openai import OpenAI
    client = OpenAI()
    paints = os.path.join(ws, "frames", "_paints")
    buf = io.BytesIO()
    guide().save(buf, "PNG")
    buf.seek(0)
    buf.name = "guide.png"
    prompt = theme(ws, key)["brief"] + LAYOUT_NOTE
    images = [buf]
    ref = os.path.join(paints, f"{key}_style_ref.png")
    if os.path.exists(ref):
        rb = io.BytesIO(open(ref, "rb").read()); rb.name = "style.png"
        images.append(rb)
        prompt = ("The FIRST image is the layout to follow exactly. The SECOND image is a frame whose art style, "
                  "palette and motifs must be matched closely (same look and feel), but redrawn on the first "
                  "image's layout with the changes described. ") + prompt
    for model in ("gpt-image-2", "gpt-image-1"):
        try:
            for im in images: im.seek(0)
            r = client.images.edit(model=model, image=images if len(images) > 1 else buf, prompt=prompt,
                                   size=f"{GW}x{GH}", n=n, quality="high")
            break
        except Exception as e:
            print(f"  {model} failed: {str(e)[:200]}")
    else:
        raise SystemExit("generation failed (nothing was changed)")
    print(f"  painted {len(r.data)} with {model}")
    return [base64.b64decode(item.b64_json) for item in r.data]


def write_paints(ws, key, pngs, start):
    for i, png in enumerate(pngs, start):
        out = os.path.join(ws, "frames", "_paints", f"{key}_{i}.png")
        with open(out, "wb") as f:
            f.write(png)
        print("  wrote", out)


def _runs(mask):
    runs, start = [], None
    for i, v in enumerate(list(mask) + [False]):
        if v and start is None: start = i
        if not v and start is not None: runs.append((start, i)); start = None
    return runs or [(0, 0)]


def _plate(dark, guide_rect, margin=40, frac=0.6):
    """The solid inside of the plate the model painted near where the guide put it: within the guide box grown by
    a margin, the dark band of rows overlapping the guide's rows most, then its longest run of dark columns."""
    gx, gy, gw, gh = guide_rect
    H, W = dark.shape
    x0, y0 = max(0, gx - margin), max(0, gy - margin)
    x1, y1 = min(W, gx + gw + margin), min(H, gy + gh + margin)
    sub = dark[y0:y1, x0:x1]
    lo, hi = gy - gh // 2 - y0, gy + gh + gh // 2 - y0
    rows = _runs(sub[:, gx - x0:gx - x0 + gw].mean(axis=1) >= frac)
    r0, r1 = max(rows, key=lambda r: max(0, min(r[1], hi) - max(r[0], lo)))
    cols = sub[r0:r1].mean(axis=0) >= frac
    c0, c1 = max(_runs(cols), key=lambda r: r[1] - r[0])
    return (x0 + c0, y0 + r0, c1 - c0, r1 - r0)


def detect(raw):
    """Where the painted frame ACTUALLY put the art window and the plates, in logical px. The text is placed by
    what was painted, not by the guide."""
    a = np.asarray(raw.convert("RGB").resize((CW, CH), Image.LANCZOS)).astype(int)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    mag = (r > 170) & (b > 170) & (g < 110)
    # The biggest solid block of magenta rows, then of its columns (a thin pink line elsewhere mustn't stretch it).
    r0, r1 = max(_runs(mag.mean(axis=1) > 0.3), key=lambda r: r[1] - r[0])
    c0, c1 = max(_runs(mag[r0:r1].mean(axis=0) > 0.5), key=lambda r: r[1] - r[0])
    art = (c0, r0, c1 - c0, r1 - r0)
    dark = a.max(axis=2) < 48
    return dict(art=art, title=_plate(dark, TITLE), type=_plate(dark, TYPE), text=_plate(dark, TEXT))


def check(reg):
    """Reasons a painted frame can't take the text (empty = usable): plates near the size asked for, the art window
    near its size and not taller, and title, art, type line and rules box top to bottom without overlapping."""
    bad = []
    for name, want, wmin, hmin in (("title", TITLE, 0.85, 0.55), ("type", TYPE, 0.68, 0.55),
                                   ("text", TEXT, 0.80, 0.70), ("art", ART_IN, 0.78, 0.85)):
        x, y, w, h = reg[name]
        if w < wmin * want[2] or h < hmin * want[3]:
            bad.append(f"{name} {w}x{h} (want >= {int(wmin * want[2])}x{int(hmin * want[3])})")
    if reg["art"][3] > 1.15 * ART_IN[3]:
        bad.append(f"art {reg['art'][2]}x{reg['art'][3]} too tall (want <= {int(1.15 * ART_IN[3])})")
    order = ["title", "art", "type", "text"]
    for a, b in zip(order, order[1:]):
        if reg[a][1] + reg[a][3] > reg[b][1]:
            bad.append(f"{a} runs into {b}")
    return bad


def inset(rect, dx, dy=0):
    x, y, w, h = rect
    return (int(x + dx), int(y + dy), int(w - 2 * dx), int(h - 2 * dy))


def font(spec, size, shadow, bold=False, italic=False, align="left"):
    family, color = spec
    fs = dict(family=family, size=size, color=color, bold=bold, italic=italic, align=align)
    if shadow:
        fs.update(shadow=True, shadowColor="#000000")
    return fs


def template_json(name, th):
    """The frame's template.json before the measured regions go in: fonts and colours from the theme."""
    sh, fo, style = th["shadow"], th["fonts"], th["style"]
    R = lambda r: dict(x=r[0], y=r[1], w=r[2], h=r[3])
    return {
        "name": name, "canvasWidth": CW, "canvasHeight": CH,
        "fullArt": False, "customFrame": True, "frameStyle": "classic", "embellishments": False,
        "cornerRadius": CARD_R, "borderThickness": BORDER, "panelRadius": {"deco": 6, "comic": 0, "bubble": 20, "plank": 7}[style],
        "ptBevel": style != "comic", "footerPlacement": "frame",
        "legendaryCrown": False, "crownStyle": "none", "topEmblem": "none",
        "colors": th["colors"],
        "titleBar": R(TITLE), "artWindow": R(ART), "typeBar": R(TYPE), "textBox": R(TEXT),
        "ptBox": R((572, 926, 126, 72)), "creditBar": R((54, 974, 264, 30)),
        "titleFont": font(fo["title"], 34 if style != "comic" else 36, sh, bold=style != "comic"),
        "typeFont": font(fo["type"], 24, sh or style == "comic", bold=True),
        "rulesFont": font(fo["rules"], 25, sh),
        "flavorFont": font(fo["flavor"], 24, sh, italic=True),
        "ptFont": font(fo["pt"], 32, sh, bold=style != "comic", align="center"),
        "creditFont": font(fo["credit"], 13, sh, italic=True),
        "manaSymbolSize": 35, "rulesSymbolSize": 26,
    }


def compose_one(ws, key, i):
    th = theme(ws, key)
    raw = Image.open(os.path.join(ws, "frames", "_paints", f"{key}_{i}.png")).convert("RGBA")
    reg = detect(raw)
    if bad := check(reg):
        print(f"  REJECTED {key}_{i}: " + "; ".join(bad))
        return False
    W, H = D(CW), D(CH)
    card = raw.resize((W, H), Image.LANCZOS)
    px = np.asarray(card).copy()
    r, g, b = px[..., 0].astype(int), px[..., 1].astype(int), px[..., 2].astype(int)
    magenta = (r > 120) & (b > 120) & (g < 0.6 * np.minimum(r, b)) & (abs(r - b) < 90)
    # The art window is punched where the model painted magenta - its own shape, not the detected rectangle - so the
    # corner scrolls and rim it painted reaching into the window stay on top of the art (a plain rectangle cut them
    # off and the art looked stamped on over the frame). Only around the window, and a hair over the magenta edge.
    ax, ay, aw, ah = reg["art"]
    m = D(10)
    near = np.zeros_like(magenta)
    near[max(0, D(ay) - m):D(ay + ah) + m, max(0, D(ax) - m):D(ax + aw) + m] = True
    hole = Image.fromarray(((magenta & near) * 255).astype(np.uint8), "L").filter(ImageFilter.MaxFilter(5))
    hole = ImageChops.invert(hole.filter(ImageFilter.GaussianBlur(0.8)))
    # Any magenta the model left outside the window (between corner ornaments) becomes the frame's black.
    px[magenta, :3] = (14, 12, 14)
    card = Image.fromarray(px, "RGBA")
    card.putalpha(ImageChops.multiply(card.getchannel("A"), hole))
    cmask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(cmask).rounded_rectangle([0, 0, W - 1, H - 1], radius=D(CARD_R), fill=255)
    card.putalpha(ImageChops.multiply(card.getchannel("A"), cmask))

    t = template_json(f"{th['name']} {i}", th)
    R = lambda r: dict(x=int(r[0]), y=int(r[1]), w=int(r[2]), h=int(r[3]))
    tx, ty, tw, th_ = reg["text"]
    t["artWindow"] = R(reg["art"])
    t["titleBar"] = R(inset(reg["title"], 8))
    # The title fits the bar the model painted (a short bar with a tall face runs its descenders onto the subtitle
    # plate, which overlaps the bar's bottom edge), and a theme can turn off faux bold for already-heavy faces.
    t["titleFont"]["size"] = min(t["titleFont"]["size"], round(reg["title"][3] * 0.7))
    for role in th.get("noBold", []):
        t[role + "Font"]["bold"] = False
    t["typeBar"] = R(inset(reg["type"], 8))
    # The whole painted box: the app finds the medallion set into its bottom edge itself and keeps text above it,
    # and pads the box itself - so no cut or inset here (they'd take that room twice).
    t["textBox"] = R((tx, ty, tw, th_))
    # The P/T box straddles the rules box's bottom edge; a theme can nudge it ("ptNudge": [dx, dy]).
    dx, dy = th.get("ptNudge", (0, 0))
    t["ptBox"] = R((tx + tw - 126 - 4 + dx, ty + th_ - 32 + dy, 126, 64))
    # The collector line sits in the rules box's dark bottom-left corner, beside the medallion.
    t["creditBar"] = R((tx + 12, ty + th_ - 30, 230, 22))
    d = os.path.join(ws, "frames", f"{key}-{i}")
    os.makedirs(d, exist_ok=True)
    card.save(os.path.join(d, "frame.png"))
    with open(os.path.join(d, "template.json"), "w", encoding="utf-8") as fh:
        json.dump(t, fh, indent=2)
    print("  composed", d, {k: tuple(int(x) for x in v) for k, v in reg.items()})
    return True


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    ws, key = sys.argv[1], sys.argv[2]
    flags = set(sys.argv[3:])
    if flags - {"--more", "--compose"} or len(flags) > 1:
        sys.exit(f"Unknown or conflicting options: {' '.join(sys.argv[3:])}\n\n{__doc__}")
    paints = os.path.join(ws, "frames", "_paints")
    os.makedirs(paints, exist_ok=True)
    guide().save(os.path.join(paints, "_guide.png"))
    n = int(os.environ.get("N", "4"))
    numbered = lambda: sorted(int(f[len(key) + 1:-4]) for f in os.listdir(paints)
                              if f.startswith(key + "_") and f[len(key) + 1:-4].isdigit())
    if "--more" in flags:             # keep the variants so far and add N more after them
        write_paints(ws, key, generate(ws, key, n), max(numbered(), default=0) + 1)
    elif "--compose" not in flags:
        # Paint first: only once the new paints are here are the old ones set aside (never deleted - OpenAI never
        # paints the same picture twice) into frames/_paints/_replaced/, and their frames removed.
        pngs = generate(ws, key, n)
        old = numbered()
        if old:
            aside = os.path.join(paints, "_replaced", time.strftime("%Y%m%d-%H%M%S"))
            os.makedirs(aside, exist_ok=True)
            for i in old:
                shutil.move(os.path.join(paints, f"{key}_{i}.png"), os.path.join(aside, f"{key}_{i}.png"))
                shutil.rmtree(os.path.join(ws, "frames", f"{key}-{i}"), ignore_errors=True)
            print(f"  the earlier {len(old)} paint(s) are kept in {aside}")
        write_paints(ws, key, pngs, 1)
    nums = numbered()
    ok = sum(bool(compose_one(ws, key, i)) for i in nums)
    print(f"{ok} of {len(nums)} usable")
    if not ok:
        sys.exit(f"No usable frame for {key}" + ("" if nums else f" (no paints {key}_<n>.png in {paints})"))
