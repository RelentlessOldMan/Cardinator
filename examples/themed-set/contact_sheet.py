"""
Contact sheets, to look a set over in one image (written to <workspace>/review/_sheets/):

  python contact_sheet.py review <workspace> <show>   # the rendered cards (pages of 12 for a whole deck)
  python contact_sheet.py frames <workspace> <theme>  # the usable composed frames of a theme, to pick one
  python contact_sheet.py paints <workspace> <theme>  # every raw paint, rejected ones too (to see why)

Look at EVERY card: title inside its bar, rules text inside the box and readable, nothing clipped. Crop and zoom
where the text is small. Needs: pip install pillow
"""
import os, sys
from PIL import Image


def grid(images, cols, w, h, out):
    rows = (len(images) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * w, max(1, rows) * h), "white")
    for i, im in enumerate(images):
        im = im.convert("RGBA").resize((w, h))
        bg = Image.new("RGBA", im.size, (255, 0, 255, 255))   # magenta shows through a frame's art window
        bg.alpha_composite(im)
        sheet.paste(bg.convert("RGB"), ((i % cols) * w, (i // cols) * h))
    sheet.save(out)
    print("wrote", os.path.abspath(out))


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    mode, ws, key = sys.argv[1:]
    out = os.path.join(ws, "review", "_sheets")
    os.makedirs(out, exist_ok=True)
    if mode == "review":
        d = os.path.join(ws, "review", key)
        files = sorted(f for f in os.listdir(d) if f.endswith(".png"))
        if len(files) <= 6:
            grid([Image.open(os.path.join(d, f)) for f in files], 3, 560, 784, os.path.join(out, f"{key}.png"))
        else:   # a whole deck: pages of 12, still big enough to read the rules text
            for n, i in enumerate(range(0, len(files), 12), 1):
                grid([Image.open(os.path.join(d, f)) for f in files[i:i + 12]], 4, 420, 588,
                     os.path.join(out, f"{key}_p{n}.png"))
    elif mode == "frames":
        fr = os.path.join(ws, "frames")
        dirs = sorted(d for d in os.listdir(fr) if d.startswith(key + "-"))
        print("  ".join(dirs))
        grid([Image.open(os.path.join(fr, d, "frame.png")) for d in dirs], max(1, len(dirs)), 300, 420,
             os.path.join(out, f"{key}_frames.png"))
    elif mode == "paints":
        p = os.path.join(ws, "frames", "_paints")
        files = sorted((f for f in os.listdir(p) if f.startswith(key + "_") and f[len(key) + 1:-4].isdigit()),
                       key=lambda f: int(f[len(key) + 1:-4]))
        grid([Image.open(os.path.join(p, f)) for f in files], 4, 300, 450, os.path.join(out, f"{key}_paints.png"))
    else:
        sys.exit(__doc__)
