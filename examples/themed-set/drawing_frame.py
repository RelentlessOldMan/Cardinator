"""
Turn YOUR OWN frame drawing into Cardinator frames: cut its windows see-through, let the app fit it to the card
(zoom / offset / border, kept editable in the app's frame designer), and make its flip and sideways versions.

  python drawing_frame.py <workspace> <name>

Reads <workspace>/themes/<name>.json ("kind": "drawing"):
  drawing    the picture, in <workspace>/frames/_drawings/
  crop       optional [x0, y0, x1, y1]: the part of the picture that is the card (trims an empty margin)
  checker    optional {"plates": [[x0, y0, x1, y1], ...], "fill": [r, g, b]}: the drawing shows "transparent" as
             a light-grey checkerboard (as image sites do); that becomes see-through, except inside these plate
             boxes (the title bar, the rules box), which get a solid fill for the text to sit on
  holes      optional [[x0, y0, x1, y1], ...]: windows to cut see-through (rounded), e.g. a dark-filled art window
  frames     the frame folders made from it: <workspace>/frames/<frame>/template.json holds each one's fit (in the
             app: frameSrc, frameZoom, frameOffsetX/Y, frameBorder, frameBorderColor) and where the text goes
All boxes are in the drawing's own pixels, before the crop.

The cut picture is written into each frame folder (under the template's frameSrc name), Cardinator composes
frame.png from it (CARDINATOR=<exe>, default Cardinator.exe on the PATH), and frame_variants.py makes the flip
and landscape versions. A new frame: copy a frame folder, change its fit in the app, and add it to "frames".
Needs: pip install pillow numpy
"""
import json, os, shutil, subprocess, sys
import numpy as np
from PIL import Image, ImageDraw, ImageChops

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import frame_variants


def cut(ws, t):
    im = Image.open(os.path.join(ws, "frames", "_drawings", t["drawing"])).convert("RGBA")
    if t.get("checker"):
        a = np.asarray(im).astype(int)
        rgb = a[..., :3]
        checker = (rgb.min(2) > 165) & ((rgb.max(2) - rgb.min(2)) < 30)   # light and grey
        out = a.astype(np.uint8)
        out[..., 3][checker] = 0
        fill = t["checker"]["fill"]
        for x0, y0, x1, y1 in t["checker"]["plates"]:
            m = np.zeros(checker.shape, bool)
            m[y0:y1, x0:x1] = True
            m &= checker
            for c in range(3):
                out[..., c][m] = fill[c]
            out[..., 3][m] = 255
        im = Image.fromarray(out, "RGBA")
    if t.get("holes"):
        hole = Image.new("L", im.size, 255)
        for r in t["holes"]:
            ImageDraw.Draw(hole).rounded_rectangle(r, radius=16, fill=0)
        im.putalpha(ImageChops.multiply(im.getchannel("A"), hole))
    if t.get("crop"):
        im = im.crop(tuple(t["crop"]))
    return im


def build(ws, name):
    with open(os.path.join(ws, "themes", name + ".json"), encoding="utf-8") as fh:
        t = json.load(fh)
    exe = os.environ.get("CARDINATOR", "Cardinator.exe")
    if not shutil.which(exe):
        sys.exit(f"Cardinator not found ({exe}): set CARDINATOR=<path to Cardinator.exe>")
    src = cut(ws, t)
    folders = [os.path.join(ws, "frames", f) for f in t["frames"]]
    for d in folders:
        with open(os.path.join(d, "template.json"), encoding="utf-8") as fh:
            name = json.load(fh).get("frameSrc") or ""
        if name.lower() in ("", "frame.png") or os.path.basename(name) != name:
            sys.exit(f"{d}: its template.json needs a \"frameSrc\" file name for the drawing (not frame.png, which the "
                     "app makes from it)")
        src.save(os.path.join(d, name))
    # The app composes frame.png for every frame with a source when it loads a frames folder - afresh whenever the
    # source file or the fit changed, so the frames already there stay until then.
    subprocess.run([exe, "--listframes", "templates=" + os.path.join(ws, "frames")], check=True, stdout=subprocess.DEVNULL)
    for d in folders:
        print(os.path.basename(d))
        frame_variants.build(d)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    build(sys.argv[1], sys.argv[2])
