"""
Build a LAYERED card frame: an OpenAI-painted background texture and emblem, with the frame's border, keylines,
art-window bevel and text plates drawn over it - plus its flip and sideways (landscape) versions. The other way
to make a frame is paint_frame.py, where OpenAI paints the whole frame; this one gives a cleaner, more regular
look, and every part can be recoloured or moved without painting again.

  python layered_frame.py <workspace> <theme>           # build frames/<theme>/ (+ flip/ and landscape/)
  python layered_frame.py <workspace> <theme> --paint   # first paint the theme's layers that don't exist yet

Reads <workspace>/themes/<theme>.json ("kind": "layered"):
  name                      the frame's name in the app
  layers.background         {"file": "steel.png", "prompt": "..."}: the texture, cover-fit to the card
  layers.emblem             {"file": "crest.png", "prompt": "...", "height": 96, "tint": [r, g, b]?}: the
                            medallion seated on the bottom of the rules box (tint multiplies its colours)
  tint, tintAlpha           a colour laid over the texture, and how strongly (0-1)
  border, metal, metal2     the outer border ring and the two metal keylines inside it, [r, g, b]
  plate, plateAlpha, plateBorder, plateHighlight    the text plates' fill (and its opacity 0-255), rim and top
                            highlight line
  colors                    {"frame", "frame2"}: the colours the app draws its own parts with (P/T box, gem)
  fonts                     {role: [family, colour]} for title/type/rules/flavor/pt/credit; a third value
                            true/false gives that role its own shadow (e.g. a light footer on a light frame)
  shadow                    true for light text with a shadow (dark plates), false for dark text (light plates)
The layers live in <workspace>/frames/_layers/ (they can't be painted the same way twice: keep them). --paint
uses OPENAI_API_KEY (gpt-image-2 for the background; gpt-image-1 for the emblem, the only one that can paint on
a transparent background - say in the prompt that it "floats ALONE on a completely empty transparent
background"). Needs: pip install pillow (and openai for --paint). See docs/THEMED-SETS.md.
"""
import base64, json, os, sys
from PIL import Image, ImageDraw, ImageChops, ImageFilter, ImageOps

# Cardinator's logical card (750x1050), drawn at 2x, then stored 1250 px wide.
CW, CH, SS, OUT_W = 750, 1050, 2, 1250
CARD_R, BORDER, PANEL_R = 48, 28, 10
TITLE = (48, 48, 654, 62)
ART = (48, 124, 654, 470)
TYPE = (48, 604, 654, 54)
TEXT = (48, 664, 654, 286)
CREDIT = (54, 974, 264, 30)
PTBOX = (572, 926, 126, 72)      # the app re-nests it into the rules box
EMBLEM_CY = 950                  # the medallion straddles the rules box's bottom edge
# Sideways card (1050x750): full-width plates, an art strip, the medallion under the rules box.
SIDE = dict(titleBar=(48, 46, 954, 62), artWindow=(48, 120, 954, 296), typeBar=(48, 426, 954, 54),
            textBox=(48, 490, 954, 170), ptBox=(872, 636, 126, 72), creditBar=(54, 680, 420, 26))
SIDE_EMBLEM = (525, 660)


def D(v):   # logical -> drawn px
    return tuple(int(round(x * SS)) for x in v) if isinstance(v, (tuple, list)) else int(round(v * SS))


def box(r):
    x, y, w, h = r
    return [D(x), D(y), D(x + w), D(y + h)]


def hexc(rgb):
    return "#%02X%02X%02X" % tuple(rgb[:3])


def theme(ws, key):
    with open(os.path.join(ws, "themes", key + ".json"), encoding="utf-8-sig") as fh:
        t = json.load(fh)
    for k in ("tint", "border", "metal", "metal2", "plate", "plateBorder", "plateHighlight"):
        t[k] = tuple(t[k])
    return t


def cover_fit(im, w, h):
    im = im.convert("RGB")
    s = max(w / im.width, h / im.height)
    nw, nh = int(im.width * s + 0.5), int(im.height * s + 0.5)
    im = im.resize((nw, nh), Image.LANCZOS)
    x, y = (nw - w) // 2, (nh - h) // 2
    return im.crop((x, y, x + w, y + h))


def emblem_image(ws, t):
    e = t["layers"]["emblem"]
    im = Image.open(os.path.join(ws, "frames", "_layers", e["file"])).convert("RGBA")
    bb = im.getchannel("A").getbbox()
    im = im.crop(bb) if bb else im
    if e.get("tint"):
        f = e["tint"]
        r, g, b, a = im.split()
        im = Image.merge("RGBA", [c.point(lambda v, k=k: min(255, int(v * k))) for c, k in zip((r, g, b), f)] + [a])
    return im


def draw(ws, t, cw, ch, plates, art, emblem_at):
    """The frame at 2x: texture -> tint -> border ring -> keylines -> art-window bevel -> plates -> emblem ->
    art window punched see-through -> rounded corners. emblem_at = (cx, cy) logical, cx None = centred."""
    W, H = D(cw), D(ch)
    bg = Image.open(os.path.join(ws, "frames", "_layers", t["layers"]["background"]["file"]))
    card = cover_fit(bg, W, H).convert("RGBA")
    card = Image.alpha_composite(card, Image.new("RGBA", (W, H), t["tint"] + (int(t["tintAlpha"] * 255),)))
    d = ImageDraw.Draw(card, "RGBA")
    b = D(BORDER)
    d.rounded_rectangle([b / 2, b / 2, W - 1 - b / 2, H - 1 - b / 2], radius=D(CARD_R) - b / 2,
                        outline=t["border"] + (255,), width=b)
    for inset, col, wd in ((5, t["metal"] + (255,), 3), (9, t["metal2"] + (140,), 1.5)):
        i = D(BORDER + inset)
        d.rounded_rectangle([i, i, W - 1 - i, H - 1 - i], radius=D(CARD_R - BORDER - inset), outline=col, width=D(wd))
    ax0, ay0, ax1, ay1 = box(art)
    bev, pr = D(6), D(PANEL_R)
    d.rounded_rectangle([ax0 - bev, ay0 - bev, ax1 + bev, ay1 + bev], radius=pr + bev,
                        outline=t["metal"] + (255,), width=D(5))
    d.rounded_rectangle([ax0 - bev + D(2), ay0 - bev + D(2), ax1 + bev - D(2), ay1 + bev - D(2)], radius=pr + bev,
                        outline=t["metal2"] + (150,), width=D(1.5))
    d.rounded_rectangle([ax0, ay0, ax1, ay1], radius=pr, outline=(0, 0, 0, 150), width=D(2))

    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))   # plates on their own layer so their opacity counts
    od = ImageDraw.Draw(ov, "RGBA")
    for r in plates:
        x0, y0, x1, y1 = box(r)
        od.rounded_rectangle([x0 - D(3), y0 + D(4), x1 - D(3), y1 + D(4)], radius=pr, fill=(0, 0, 0, 90))   # shadow
        od.rounded_rectangle([x0, y0, x1, y1], radius=pr, fill=t["plate"] + (t["plateAlpha"],))
        od.rounded_rectangle([x0, y0, x1, y1], radius=pr, outline=t["plateBorder"] + (255,), width=D(2.5))
        od.line([x0 + pr, y0 + D(2), x1 - pr, y0 + D(2)], fill=t["plateHighlight"] + (200,), width=D(1.5))
    card = Image.alpha_composite(card, ov)

    if emblem_at:
        em = emblem_image(ws, t)
        eh = D(t["layers"]["emblem"]["height"])
        ew = int(em.width * (eh / em.height))
        em = em.resize((ew, eh), Image.LANCZOS)
        ex = (W - ew) // 2 if emblem_at[0] is None else D(emblem_at[0]) - ew // 2
        ey = D(emblem_at[1]) - eh // 2
        sh = Image.new("RGBA", (W, H), (0, 0, 0, 0))   # a soft shadow so it sits on the plate or the frame
        pad = int(eh * 0.16)
        ImageDraw.Draw(sh).ellipse([ex - pad, ey - pad, ex + ew + pad, ey + eh + pad], fill=(0, 0, 0, 85))
        card = Image.alpha_composite(card, sh.filter(ImageFilter.GaussianBlur(D(9))))
        card.alpha_composite(em, (ex, ey))

    hole = Image.new("L", (W, H), 255)
    ImageDraw.Draw(hole).rounded_rectangle([ax0, ay0, ax1, ay1], radius=pr, fill=0)
    card.putalpha(ImageChops.multiply(card.getchannel("A"), hole))
    corners = Image.new("L", (W, H), 0)
    ImageDraw.Draw(corners).rounded_rectangle([0, 0, W - 1, H - 1], radius=D(CARD_R), fill=255)
    card.putalpha(ImageChops.multiply(card.getchannel("A"), corners))
    return card


def font(t, role, size, bold=False, italic=False, align="left"):
    family, colour, *own = t["fonts"][role]   # a third value: this role's own shadow setting
    fs = dict(family=family, size=size, color=colour, bold=bold, italic=italic, align=align)
    if (own or [t["shadow"]])[0]:
        fs["shadow"], fs["shadowColor"] = True, "#000000"
    return fs


def R(r, nd=None):
    x, y, w, h = r
    return dict(x=x, y=y, w=w, h=h) if nd is None else dict(x=round(x, nd), y=round(y, nd), w=round(w, nd), h=round(h, nd))


def template(t):
    return {
        "name": t["name"],
        "canvasWidth": CW, "canvasHeight": CH,
        "fullArt": False, "customFrame": True, "frameStyle": "classic", "embellishments": False,
        "cornerRadius": CARD_R, "borderThickness": BORDER, "panelRadius": PANEL_R, "ptBevel": True,
        "footerPlacement": "frame", "legendaryCrown": False, "crownStyle": "none", "topEmblem": "none",
        "colors": {"border": hexc(t["border"]), "frame": t["colors"]["frame"], "frame2": t["colors"]["frame2"],
                   "panel": hexc(t["plate"]), "panelBorder": hexc(t["plateBorder"])},
        "titleBar": R(TITLE), "artWindow": R(ART), "typeBar": R(TYPE), "textBox": R(TEXT),
        "ptBox": R(PTBOX), "creditBar": R(CREDIT),
        "titleFont": font(t, "title", 33, bold=True),
        "typeFont": font(t, "type", 24, bold=True),
        "rulesFont": font(t, "rules", 25),
        "flavorFont": font(t, "flavor", 24, italic=True),
        "ptFont": font(t, "pt", 32, bold=True, align="center"),
        "creditFont": font(t, "credit", 13, italic=True),
        "manaSymbolSize": 35, "rulesSymbolSize": 26,
    }


def flip_regions(spec):
    """Where a flip card's parts go (the top half; the bottom is its mirror) - the same rule as the app's
    TemplateSpec.ToFlip(), so a picture frame and a drawn frame lay a flip card out alike."""
    t, a, y, x, p = (spec[k] for k in ("titleBar", "artWindow", "typeBar", "textBox", "ptBox"))
    h = spec.get("canvasHeight", 1050)
    gap = min(max(x["y"] - (y["y"] + y["h"]), 4), 16)
    art_gap = min(max(a["y"] - (t["y"] + t["h"]), 4), 20)
    type_h, text_top = y["h"], t["y"] + t["h"] + gap
    min_text = spec.get("rulesFont", {}).get("size", 25) * 3.6
    type_bottom = h / 2 - h * 0.17 - art_gap
    text_h = type_bottom - type_h - gap - text_top
    if text_h < min_text:
        type_bottom += min_text - text_h
        text_h = min_text
    art_top = type_bottom + art_gap
    pt_h = min(p["h"], type_h + 16)
    return dict(titleBar=(t["x"], t["y"], t["w"], t["h"]),
                textBox=(x["x"], text_top, x["w"], text_h),
                typeBar=(y["x"], type_bottom - type_h, y["w"], type_h),
                artWindow=(a["x"], art_top, a["w"], h - 2 * art_top),
                ptBox=(y["x"] + y["w"] - p["w"], type_bottom - type_h / 2 - pt_h / 2, p["w"], pt_h))


def mirror_top_half(img):
    w, h = img.size
    top = img.crop((0, 0, w, h // 2))
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.alpha_composite(top, (0, 0))
    out.alpha_composite(ImageOps.flip(top), (0, h - h // 2))
    return out


def save(folder, img, spec):
    os.makedirs(folder, exist_ok=True)
    img.save(os.path.join(folder, "frame.png"), optimize=True)
    with open(os.path.join(folder, "template.json"), "w", encoding="utf-8") as fh:
        json.dump(spec, fh, indent=2)
    print(f"  {folder} ({img.size[0]}x{img.size[1]})")


def build(ws, key):
    t = theme(ws, key)
    for layer in t["layers"].values():
        if not os.path.exists(os.path.join(ws, "frames", "_layers", layer["file"])):
            sys.exit(f"frames/_layers/{layer['file']} is missing: paint it with --paint")
    out = os.path.join(ws, "frames", key)
    base = template(t)
    print(t["name"])

    img = draw(ws, t, CW, CH, [TITLE, TYPE, TEXT], ART, (None, EMBLEM_CY))
    save(out, img.resize((OUT_W, OUT_W * CH // CW), Image.LANCZOS), base)

    # Flip: the top half's plates drawn, then mirrored onto the bottom (no medallion).
    g = flip_regions(base)
    img = draw(ws, t, CW, CH, [g["titleBar"], g["textBox"], g["typeBar"]], g["artWindow"], None)
    spec = dict(base)
    spec.update({k: R(v, 2) for k, v in g.items()})
    spec.update(cardLayout="flip", footerPlacement="border", legendaryCrown=False)
    save(os.path.join(out, "flip"), mirror_top_half(img).resize((OUT_W, OUT_W * CH // CW), Image.LANCZOS), spec)

    # Sideways.
    img = draw(ws, t, CH, CW, [SIDE["titleBar"], SIDE["typeBar"], SIDE["textBox"]], SIDE["artWindow"], SIDE_EMBLEM)
    spec = dict(base)
    spec.update({k: R(v, 2) for k, v in SIDE.items()})
    spec.update(canvasWidth=CH, canvasHeight=CW)
    save(os.path.join(out, "landscape"), img.resize((OUT_W * CH // CW, OUT_W), Image.LANCZOS), spec)


def paint(ws, key):
    """Paint the theme's layers that don't exist yet (they're kept: the same prompt never paints the same picture)."""
    from openai import OpenAI
    client, t = OpenAI(), theme(ws, key)
    os.makedirs(os.path.join(ws, "frames", "_layers"), exist_ok=True)
    for role, size, transparent in (("background", "1024x1536", False), ("emblem", "1024x1024", True)):
        layer = t["layers"][role]
        path = os.path.join(ws, "frames", "_layers", layer["file"])
        if os.path.exists(path):
            continue
        args = dict(model="gpt-image-1" if transparent else "gpt-image-2", prompt=layer["prompt"], size=size,
                    n=1, quality="high")
        if transparent:
            args["background"] = "transparent"
        r = client.images.generate(**args)
        with open(path, "wb") as fh:
            fh.write(base64.b64decode(r.data[0].b64_json))
        print("painted", path)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    if "--paint" in sys.argv[3:]:
        paint(sys.argv[1], sys.argv[2])
    build(sys.argv[1], sys.argv[2])
