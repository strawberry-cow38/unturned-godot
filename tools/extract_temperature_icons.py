"""Extract PlayerLifeUI's six temperature status icons -> content/hud_temp_<state>.png.

Retail shows ONE box (PlayerLifeUI.temperatureBox, a SleekBoxIcon) whose icon swaps with the state
and which is hidden entirely while temperature == NONE. The six 40x40 sources live together in
ui/player/icons/playerlife/, the same folder the port's existing hud_*.png came from.

Named hud_temp_* rather than the retail bare names (cold.png / warm.png) because content/ is flat --
a file called "warm.png" next to "wheat.txt" and "water_source" tells you nothing about what draws it.
"""
import os
import sys

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

STATES = ("Freezing", "Cold", "Warm", "Burning", "Covered", "Acid")
OUT = os.path.normpath(os.path.join(ug_paths.objects_out(), ".."))   # content/, not content/objects/

env = UnityPy.load(os.path.join(ug_paths.bundles(), "core.masterbundle"))
want = {s.lower(): s for s in STATES}
found = {}

for path, obj in env.container.items():
    if obj.type.name != "Texture2D":
        continue
    # Match on the CONTAINER path, not just the basename: "cold.png" or "acid.png" are generic enough
    # that another folder could own one, and silently shipping some other subsystem's icon is exactly
    # the class of mistake that put a gun skin on the airdrop plane.
    if "/ui/player/icons/playerlife/" not in path.lower():
        continue
    base = os.path.splitext(os.path.basename(path))[0].lower()
    if base not in want or base in found:
        continue
    img = obj.read().image
    name = f"hud_temp_{base}.png"
    img.save(os.path.join(OUT, name))
    found[base] = (name, img.width, img.height)

for s in STATES:
    hit = found.get(s.lower())
    print(f"  {s:9s} -> {hit[0]} ({hit[1]}x{hit[2]})" if hit else f"  {s:9s} -> MISSING")

missing = [s for s in STATES if s.lower() not in found]
if missing:
    print("missing: " + ", ".join(missing), file=sys.stderr)
    sys.exit(1)
print(f"wrote {len(found)} temperature icons -> {OUT}")
