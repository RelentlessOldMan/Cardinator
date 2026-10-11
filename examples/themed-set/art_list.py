"""
Keep a show's pictures as LINKS instead of files: art/<show>/art.tsv lists every picture with where it came from,
so a backup (or a git repo) holds a few KB of links rather than hundreds of MB of images, and a fresh checkout
downloads them again.

art.tsv columns (tab-separated, one picture per line, # comments allowed):
  file      the picture's file name in art/<show>/ (named after the real card, e.g. "Sol Ring.png")
  url       where it was downloaded from
  sha256    the file's checksum, so a re-download is known to be the same picture (also accepted: one that looks
            identical at the same size, e.g. the original of a picture that was re-saved as JPG)
  crop      optional "x,y,w,h": the download was cropped to this box (e.g. a tall picture framed on the face)
  size      optional "WxH": then resized to this (e.g. a 4K original kept at 2560x1440)
  look      the picture's size and an 8x8 thumbnail, so a fresh download that was re-encoded (different bytes,
            same picture) is still recognised when there's no copy to compare it with

  python art_list.py add   <workspace> <show> <file> <url> [x,y,w,h]   # download a picture (cropped) and list it
  python art_list.py make  <workspace> <show> [links.md]   # list every picture already in the folder; links come
                                                           # from the existing art.tsv, or rows "| name | ... url |"
                                                           # of a markdown table (later rows win)
  python art_list.py fetch <workspace> <show>              # download every listed picture that's missing
  python art_list.py check <workspace> <show> [--learn]    # download each one again and compare (finds dead links);
                                                           # --learn records the size of pictures kept smaller

A picture whose link dies or changes can't come back from the list - keep those files themselves ("check" names
them). Downloads use curl (built into Windows 10+, macOS and Linux). Needs: pip install pillow
"""
import hashlib, io, os, re, subprocess, sys
from PIL import Image, ImageChops, ImageStat

IMAGE = (".png", ".jpg", ".jpeg", ".webp")
AGENTS = ("Mozilla/5.0", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def download(url):
    """curl, not Python's downloader (some image hosts refuse it); a short user agent first, a browser's if refused.
    Web links only (an art.tsv from a backup or a friend can't make it read a local file), at most 200 MB."""
    if not re.match(r"https?://", url, re.I):
        raise IOError(f"not a web link (http/https): {url}")
    for ua in AGENTS:
        r = subprocess.run(["curl", "-sfL", "--proto", "=http,https", "--proto-redir", "=http,https",
                            "--max-filesize", "200M", "--max-time", "60", "-A", ua, url], capture_output=True)
        if not r.returncode and r.stdout:
            return r.stdout
    raise IOError(f"download failed (curl {r.returncode}): {url}")


def render(data, file, crop="", size=""):
    """The bytes of the picture as kept: the download itself when it's already that format and untouched,
    otherwise converted (cropped, resized) and saved in the file's format."""
    ext = os.path.splitext(file)[1].lower()
    im = Image.open(io.BytesIO(data))
    native = {"PNG": ".png", "JPEG": ".jpg", "WEBP": ".webp"}.get(im.format)
    if not crop and not size and (native == ext or (native == ".jpg" and ext == ".jpeg")):
        return data
    im = im.convert("RGB")
    if crop:
        x, y, w, h = (int(v) for v in crop.split(","))
        im = im.crop((x, y, x + w, y + h))
    if size:
        im = im.resize(tuple(int(v) for v in size.lower().split("x")), Image.LANCZOS)
    out = io.BytesIO()
    fmt = {".png": "PNG", ".webp": "WEBP"}.get(ext, "JPEG")
    im.save(out, fmt, **({"quality": 95} if fmt == "JPEG" else {}))
    return out.getvalue()


def look(data):
    """"WxH:<8x8 grey thumbnail as hex>" - a fingerprint of what the picture shows, not of its bytes."""
    im = Image.open(io.BytesIO(data))
    return "%dx%d:" % im.size + im.convert("L").resize((8, 8), Image.BILINEAR).tobytes().hex()


def looks_like(data, fingerprint):
    """The picture in data matches a look() fingerprint: same size, thumbnails within a few levels."""
    if not fingerprint:
        return False
    a, b = look(data).split(":"), fingerprint.split(":")
    if a[0] != b[0]:
        return False
    x, y = bytes.fromhex(a[1]), bytes.fromhex(b[1])
    return sum(abs(p - q) for p, q in zip(x, y)) / len(x) < 4


def same_picture(a, b):
    """Two picture files show the same image: identical bytes, or the same size and visually identical (a picture
    re-saved as JPG differs by a few levels per pixel)."""
    if a == b:
        return True
    ia, ib = Image.open(io.BytesIO(a)).convert("RGB"), Image.open(io.BytesIO(b)).convert("RGB")
    return ia.size == ib.size and max(ImageStat.Stat(ImageChops.difference(ia, ib)).mean) < 4


def safe_name(file):
    """A picture's file name as art.tsv gives it, if it is a plain name in the art folder (never "../x" or a path)."""
    ok = file and file == os.path.basename(file) and not re.search(r'[\\/:*?"<>|]', file) and file.strip(". ")
    return file if ok else None


def read(path):
    rows = {}
    if os.path.exists(path):
        for line in open(path, encoding="utf-8-sig"):   # -sig: Notepad and Excel may save it with a BOM
            if line.strip() and not line.startswith("#"):
                p = line.rstrip("\n").split("\t") + [""] * 5
                rows[p[0]] = dict(url=p[1], sha=p[2], crop=p[3], size=p[4], look=p[5])
    return rows


def write(path, rows):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# file\turl\tsha256\tcrop (x,y,w,h)\tsize (WxH)\tlook - see art_list.py\n")
        for f in sorted(rows, key=str.lower):
            r = rows[f]
            fh.write("\t".join([f, r["url"], r["sha"], r["crop"], r["size"], r.get("look", "")]).rstrip("\t") + "\n")


def links_from_markdown(md):
    """{name: url} from markdown table rows whose first cell is a name and some cell holds an http(s) link."""
    out = {}
    for line in open(md, encoding="utf-8"):
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        url = next((m.group(0) for c in cells[1:] for m in [re.search(r"https?://\S+", c)] if m), None)
        if cells and url:
            out[cells[0].lower()] = url
    return out


def main(mode, ws, show, rest):
    art = os.path.join(ws, "art", show)
    lst = os.path.join(art, "art.tsv")
    rows = read(lst)
    if mode == "add":
        if len(rest) < 2:
            sys.exit(__doc__)
        file, url = rest[0], rest[1]
        if not safe_name(file):
            sys.exit(f"Give a plain file name (no folders): {file}")
        crop = rest[2] if len(rest) > 2 else ""
        data = render(download(url), file, crop)
        open(os.path.join(art, file), "wb").write(data)
        rows[file] = dict(url=url, sha=sha(data), crop=crop, size="", look=look(data))
        write(lst, rows)
        print("added", file)
    elif mode == "make":
        md = links_from_markdown(rest[0]) if rest else {}
        for f in sorted(os.listdir(art)):
            if f.lower().endswith(IMAGE):
                r = rows.get(f, dict(url="", sha="", crop="", size="", look=""))
                r["url"] = r["url"] or md.get(os.path.splitext(f)[0].lower(), "")
                data = open(os.path.join(art, f), "rb").read()
                r["sha"], r["look"] = sha(data), look(data)
                rows[f] = r
        rows = {f: r for f, r in rows.items() if os.path.exists(os.path.join(art, f))}
        write(lst, rows)
        print(f"listed {len(rows)} picture(s); {sum(1 for r in rows.values() if not r['url'])} without a link")
    elif mode in ("fetch", "check"):
        bad, learn = [], "--learn" in rest
        for f, r in rows.items():
            if not safe_name(f):   # a line pointing outside the art folder is never written (or read)
                bad.append(f)
                continue
            path = os.path.join(art, f)
            have = open(path, "rb").read() if os.path.exists(path) else None
            if mode == "fetch" and have:
                continue
            try:
                raw = download(r["url"]) if r["url"] else None
                data = render(raw, f, r["crop"], r["size"]) if raw else None
                if learn and data and have and not r["size"] and not same_picture(have, data):
                    kept = "%dx%d" % Image.open(io.BytesIO(have)).size   # the same picture, kept smaller?
                    smaller = render(raw, f, r["crop"], kept)
                    if same_picture(have, smaller):
                        r["size"], data = kept, smaller
            except Exception:
                data = None
            ok = data is not None and (sha(data) == r["sha"] or looks_like(data, r["look"])
                                       or (have is not None and same_picture(have, data))
                                       or (mode == "fetch" and not r["sha"]))
            if not ok:
                bad.append(f)
            elif mode == "fetch":
                open(path, "wb").write(data)
        if learn:
            write(lst, rows)
        print(f"{'downloaded' if mode == 'fetch' else 're-downloaded the same'}: {len(rows) - len(bad)} of {len(rows)}")
        if bad:
            print("NOT the same picture (dead/changed link, or no link) - keep these files themselves:\n  "
                  + "\n  ".join(bad))
        return bad
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    sys.stdout.reconfigure(errors="replace")   # a name the console can't show doesn't crash it
    sys.exit(1 if main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]) else 0)
