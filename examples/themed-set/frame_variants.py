"""
Make the FLIP and SIDEWAYS (landscape) versions of a picture frame - a frame that is one finished image, like
one built from your own drawing (drawing_frame.py) - by cutting its own pixels into bands and rearranging them
(nothing is painted or redrawn). The cut lines are found automatically: the art window from where the frame is
see-through, the title plate from the frame's template, the rules box's inside from its run of flat dark rows;
each plate's rim travels with its band.

  python frame_variants.py <frame folder> [<frame folder> ...]

Reads <folder>/frame.png + template.json and writes <folder>/flip/ and <folder>/landscape/ (frame.png +
template.json), which the app offers as that frame's flip and sideways layouts. Layered frames make their own
(layered_frame.py redraws them instead). Needs: pip install pillow
"""
import json, os, statistics, sys
from PIL import Image, ImageOps


def runs(flags):
    out, start = [], None
    for i, f in enumerate(flags + [False]):
        if f and start is None:
            start = i
        if not f and start is not None:
            out.append((start, i))
            start = None
    return out


def analyse(src, base):
    """Rows (frame px) of the art window, the title plate and the rules box's flat inside."""
    W, H = src.size
    px = src.load()
    clear = [px[W // 2, y][3] < 10 for y in range(H)]
    a0, a1 = max(runs(clear), key=lambda r: r[1] - r[0])

    def flat(y):
        vals = [sum(px[x, y][:3]) / 3 for x in range(int(W * .3), int(W * .7), 6) if px[x, y][3] > 200]
        return len(vals) > 20 and statistics.pstdev(vals) < 3 and statistics.mean(vals) < 45
    fl = [flat(y) for y in range(H)]
    tb = base["titleBar"]   # from the template: some title plates are textured, not flat
    s = W / 750
    ti0 = int(tb["y"] * s) - 5
    ti1 = min(int((tb["y"] + tb["h"]) * s) + 10, a0 - 20)
    xi0, xi1 = max((r for r in runs(fl) if r[0] >= a1), key=lambda r: r[1] - r[0])
    return dict(a0=a0, a1=a1, ti0=ti0, ti1=ti1, xi0=xi0, xi1=xi1)


def build(folder):
    src = Image.open(os.path.join(folder, "frame.png")).convert("RGBA")
    W0, H0 = src.size
    with open(os.path.join(folder, "template.json"), encoding="utf-8-sig") as fh:
        base = json.load(fh)
    m = analyse(src, base)
    a0, a1, ti0, ti1, xi0, xi1 = (m[k] for k in ("a0", "a1", "ti0", "ti1", "xi0", "xi1"))
    ORN = (0, ti0 - 18); TITLE = (ti0 - 18, ti1); ART_TOP = (ti1, a0 + 10)
    ART_BOT = (a1 - 12, a1 + 4); TYPE = (a1 + 4, xi0 - 28); JUNC = (xi0 - 28, xi0)
    TEXT_EDGE = (xi1, min(H0, xi1 + 22)); BOTTOM = (xi1, H0)
    L = lambda b: b[1] - b[0]
    rows = lambda a, b: src.crop((0, a, W0, b))
    for k in ("frameSrc", "frameZoom", "frameOffsetX", "frameOffsetY"):   # the variants are plain pictures
        base.pop(k, None)
    S = 750 / W0                                     # frame px -> logical
    tb, yb, xb = base["titleBar"], base["typeBar"], base["textBox"]

    def stack(pieces, width):
        out = Image.new("RGBA", (width, sum(p.height for p in pieces)), (0, 0, 0, 0))
        y = 0
        for p in pieces:
            out.alpha_composite(p, (0, y))
            y += p.height
        return out

    # ---------- FLIP: ornament, title, rules box, type line, art; the top half mirrored onto the bottom ----------
    top_end = int(H0 * (0.5 - 0.17))
    T = top_end - (L(ORN) + L(TITLE) + L(JUNC) + L(TEXT_EDGE) + L(TYPE) + L(ART_TOP))
    assert T > 120, (folder, "no room for the rules box", T)
    top = stack([rows(*ORN), rows(*TITLE), rows(*JUNC), rows(xi0, xi0 + T), rows(*TEXT_EDGE), rows(*TYPE),
                 rows(*ART_TOP), rows(a0 + 10, a0 + 10 + (H0 // 2 - top_end))], W0)
    top = top.crop((0, 0, W0, H0 // 2))
    flip = Image.new("RGBA", (W0, H0), (0, 0, 0, 0))
    flip.alpha_composite(top, (0, 0))
    flip.alpha_composite(ImageOps.flip(top), (0, H0 - H0 // 2))
    y_title = ORN[1] + (tb["y"] / S - TITLE[0])
    y_text = ORN[1] + L(TITLE) + L(JUNC)
    y_type = y_text + T + L(TEXT_EDGE) + (yb["y"] / S - TYPE[0])
    y_art = y_text + T + L(TEXT_EDGE) + L(TYPE) + (a0 - ART_TOP[0])
    pt_h = min(base["ptBox"]["h"], yb["h"] + 16)
    spec = dict(base)
    spec.update(
        name=base["name"], cardLayout="flip", footerPlacement="frame", legendaryCrown=False,
        titleBar=dict(x=tb["x"], y=y_title * S, w=tb["w"], h=tb["h"]),
        textBox=dict(x=xb["x"], y=(y_text + 6) * S, w=xb["w"], h=(T - 12) * S),
        typeBar=dict(x=yb["x"], y=y_type * S, w=yb["w"], h=yb["h"]),
        artWindow=dict(x=base["artWindow"]["x"], y=y_art * S, w=base["artWindow"]["w"], h=1050 - 2 * y_art * S),
        creditBar=dict(x=60, y=1050 - 30, w=560, h=22),
    )
    spec["ptBox"] = dict(x=yb["x"] + yb["w"] - base["ptBox"]["w"], y=spec["typeBar"]["y"] + yb["h"] / 2 - pt_h / 2,
                         w=base["ptBox"]["w"], h=pt_h)
    spec.pop("subtitleBar", None)
    save(folder, "flip", flip, spec)

    # ---------- SIDEWAYS: every band widened, the art and rules box shortened ----------
    WN, HN = H0, W0
    Lk = int(W0 * 0.3)   # the left and right 30% (corner ornaments) keep their size; the middle stretches

    def widen(band, motif=False):
        w, h = band.size
        out = Image.new("RGBA", (WN, h), (0, 0, 0, 0))
        if not motif:
            out.alpha_composite(band.crop((0, 0, Lk, h)), (0, 0))
            out.alpha_composite(band.crop((Lk, 0, w - Lk, h)).resize((WN - 2 * Lk, h), Image.LANCZOS), (Lk, 0))
            out.alpha_composite(band.crop((w - Lk, 0, w, h)), (WN - Lk, 0))
            return out
        # the top ornament: keep its centre design at size, fill each side with a cross-faded copy of its neighbour
        mid = band.crop((Lk, 0, w - Lk, h))
        gap = (WN - 2 * Lk - mid.width) // 2
        F = 70

        def fade(p, left, right):
            a = p.getchannel("A").load()
            q = p.copy()
            qa = q.getchannel("A")
            qp = qa.load()
            for x in range(p.width):
                k = 1.0
                if left and x < F:
                    k = min(k, x / F)
                if right and x >= p.width - F:
                    k = min(k, (p.width - 1 - x) / F)
                if k < 1:
                    for y in range(p.height):
                        qp[x, y] = int(a[x, y] * k)
            q.putalpha(qa)
            return q
        out.alpha_composite(band.resize((WN, h), Image.LANCZOS), (0, 0))
        out.alpha_composite(fade(band.crop((Lk - gap - F, 0, Lk + F, h)), 1, 1), (Lk - F, 0))
        out.alpha_composite(fade(band.crop((w - Lk - F, 0, w - Lk + gap + F, h)), 1, 1), (WN - Lk - gap - F, 0))
        out.alpha_composite(fade(band.crop((Lk - F, 0, w - Lk + F, h)), 1, 1), (Lk + gap - F, 0))
        out.alpha_composite(fade(band.crop((0, 0, Lk, h)), 0, 1), (0, 0))
        out.alpha_composite(fade(band.crop((w - Lk, 0, w, h)), 1, 0), (WN - Lk, 0))
        return out

    Ts = int(HN * 0.22)
    Ms = HN - (L(ORN) + L(TITLE) + L(ART_TOP) + L(ART_BOT) + L(TYPE) + L(JUNC) + L(BOTTOM)) - Ts
    side = stack([widen(rows(*ORN), True), widen(rows(*TITLE)), widen(rows(*ART_TOP)),
                  widen(rows(a0 + 10, a0 + 10 + Ms)), widen(rows(*ART_BOT)), widen(rows(*TYPE)),
                  widen(rows(*JUNC)), widen(rows(xi0, xi0 + Ts)), widen(rows(*BOTTOM))], WN)
    assert side.size == (WN, HN), side.size
    dx = (WN - W0) * S
    y_art = L(ORN) + L(TITLE) + (a0 - ART_TOP[0])
    y_typeband = L(ORN) + L(TITLE) + L(ART_TOP) + Ms + L(ART_BOT)
    y_text = y_typeband + L(TYPE) + L(JUNC)
    text_h = (Ts - 70) * S
    spec = dict(base)
    spec.update(
        canvasWidth=1050, canvasHeight=750,
        titleBar=dict(x=tb["x"], y=(L(ORN) + tb["y"] / S - TITLE[0]) * S, w=tb["w"] + dx, h=tb["h"]),
        artWindow=dict(x=base["artWindow"]["x"], y=y_art * S, w=base["artWindow"]["w"] + dx, h=(Ms + 20) * S),
        typeBar=dict(x=yb["x"], y=(y_typeband + yb["y"] / S - TYPE[0]) * S, w=yb["w"] + dx, h=yb["h"]),
        textBox=dict(x=xb["x"], y=(y_text + 6) * S, w=xb["w"] + dx, h=text_h),
    )
    tbot = spec["textBox"]["y"] + text_h
    spec["ptBox"] = dict(x=base["ptBox"]["x"] + dx, y=tbot - base["ptBox"]["h"] * 0.6, w=base["ptBox"]["w"],
                         h=base["ptBox"]["h"])
    spec["creditBar"] = dict(x=base["creditBar"]["x"], y=tbot + 8, w=base["creditBar"]["w"], h=base["creditBar"]["h"])
    spec.pop("subtitleBar", None)
    save(folder, "landscape", side, spec)


def save(folder, key, img, spec):
    d = os.path.join(folder, key)
    os.makedirs(d, exist_ok=True)
    img.save(os.path.join(d, "frame.png"))
    with open(os.path.join(d, "template.json"), "w", encoding="utf-8") as fh:
        json.dump(spec, fh, indent=2)
    print(f"  {d} ({img.size[0]}x{img.size[1]})")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for f in sys.argv[1:]:
        build(f)
