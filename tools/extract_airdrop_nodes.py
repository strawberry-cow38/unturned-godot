"""Extract a map's AIRDROP nodes from Level.hierarchy -> content/airdrop_nodes.tsv.

Retail drops a care package at `airdropNodes[Random.Range(0, count)]` -- a uniformly random pick
from nodes the map author placed. PEI has 14. They were invisible to this port because the existing
node extractor (tools/parse_nodes.py) reads Maps/<map>/Environment/Nodes.dat, and that file only
holds the 21 named LOCATION nodes. Modern Unturned keeps devkit nodes in Level.hierarchy instead, so
"nodes.tsv has no airdrops" reads as "the map has no airdrops" and is wrong.

Two things worth not repeating:

  * a Type marker is followed by its Item block, so the Position BELOW a Type belongs to it. Grabbing
    the nearest Position in a fixed window around the marker silently returns the PRECEDING block's
    coordinates -- it produces plausible in-bounds numbers for the wrong objects. Each block is
    delimited here by the next Type marker instead.
  * the port stores map coordinates with Z NEGATED (Unity's left-handed space -> Godot's). Derived,
    not assumed: Level.hierarchy puts Summerside Military Base at Z=+789.83 and the port's existing
    nodes.tsv has it at Z=-789.83, X and Y identical.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

MAP = sys.argv[1] if len(sys.argv) > 1 else "PEI"
src = ug_paths.map_file("Level.hierarchy", map_name=MAP)   # Maps/<map>/Level.hierarchy
txt = open(src, encoding="utf-8", errors="replace").read()

POS = re.compile(r'"Position"\s*\{\s*"X"\s+"(-?[\d.eE+-]+)"\s*"Y"\s+"(-?[\d.eE+-]+)"\s*"Z"\s+"(-?[\d.eE+-]+)"')
marks = [(m.start(), m.group(1)) for m in re.finditer(r'"Type"\s+"SDG\.[^,"]*\.(\w+),', txt)]
marks.append((len(txt), None))

nodes = []
for (start, kind), (end, _) in zip(marks, marks[1:]):
    if kind != "AirdropDevkitNode":
        continue
    m = POS.search(txt, start, end)
    if not m:
        print(f"  WARNING: an AirdropDevkitNode at offset {start} has no Position", file=sys.stderr)
        continue
    x, y, z = (float(g) for g in m.groups())
    nodes.append((x, y, -z))      # Z negated into the port's frame

if not nodes:
    print(f"no AirdropDevkitNodes in {src}", file=sys.stderr)
    sys.exit(1)

out = os.path.join(ug_paths.objects_out(), "..", "airdrop_nodes.tsv")
out = os.path.normpath(out)
with open(out, "w", encoding="utf-8") as f:
    for x, y, z in nodes:
        f.write("%.4f,%.4f,%.4f\n" % (x, y, z))
print(f"wrote {len(nodes)} airdrop nodes -> {out}")
for x, y, z in nodes:
    print(f"  ({x:9.2f}, {y:7.2f}, {z:9.2f})")
