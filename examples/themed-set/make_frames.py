"""
Rebuild every frame of a workspace from its settings - after a fresh checkout, or after changing a theme.

  python make_frames.py <workspace>

Goes through <workspace>/themes/*.json and runs the script for each one's "kind":
  painted  (the default)  paint_frame.py --compose: the frames from the theme's kept OpenAI paints (no painting)
  layered                 layered_frame.py: from the theme's kept layers (no painting)
  drawing                 drawing_frame.py: from your own drawing (needs CARDINATOR=<exe> or Cardinator.exe on
                          the PATH)
Nothing here calls OpenAI: what was painted is kept in frames/_paints and frames/_layers. See docs/THEMED-SETS.md.
"""
import json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = {"painted": ["paint_frame.py", "--compose"], "layered": ["layered_frame.py"], "drawing": ["drawing_frame.py"]}

if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    ws, failed = sys.argv[1], []
    for f in sorted(os.listdir(os.path.join(ws, "themes"))):
        if f.endswith(".json"):
            key = f[:-5]
            with open(os.path.join(ws, "themes", f), encoding="utf-8") as fh:
                kind = json.load(fh).get("kind", "painted")
            script, *opts = SCRIPT[kind]
            print(f"== {key} ({kind})", flush=True)
            if subprocess.run([sys.executable, os.path.join(HERE, script), ws, key, *opts]).returncode:
                failed.append(key)
    if failed:
        sys.exit("Not built: " + ", ".join(failed))
