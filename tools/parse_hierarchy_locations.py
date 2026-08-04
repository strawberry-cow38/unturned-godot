import sys, os, re
# Named LOCATION nodes for MODERN maps (Washington, etc.), whose locations live in Level.hierarchy as
# SDG.Unturned.LocationDevkitNode blocks -- NOT in Environment/Nodes.dat like PEI (that's parse_nodes.py).
# Emits content/nodes_<key>.tsv in the same "Name<TAB>X,Y,-Z" shape MapNodes reads, with Z NEGATED to Godot
# space exactly like parse_nodes.py, so a location lands where the placed objects (gen_placements) put its town.
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
OUTDIR  = r"C:\claude-workspace\unturned-godot\game\content"
mapname = sys.argv[1] if len(sys.argv) > 1 else "Washington"
# --local <path> <outdir>: parse a pulled copy off-box (for testing without the Steam install)
if len(sys.argv) > 3 and sys.argv[1] == "--local":
    path, OUTDIR, mapname = sys.argv[2], sys.argv[3], "Washington"
else:
    path = os.path.join(MAPBASE, mapname, "Level.hierarchy")

lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
LOC_TYPE = "SDG.Unturned.LocationDevkitNode"
n = len(lines)
locs = []
seen = set()   # the hierarchy lists every LocationDevkitNode TWICE (identical name+pos); keep the first of each name
for i, line in enumerate(lines):
    if LOC_TYPE not in line:
        continue
    x = y = z = name = None
    j = i + 1
    while j < n:
        lj = lines[j]
        if '"Type"' in lj:            # next node's Type -> this block is done
            break
        p = re.findall(r'"([^"]*)"', lj)
        if len(p) >= 2:
            k, v = p[0], p[1]
            if   k == "X" and x is None: x = float(v)   # first X/Y/Z after Type = the Position block (Rotation/Scale follow)
            elif k == "Y" and y is None: y = float(v)
            elif k == "Z" and z is None: z = float(v)
            elif k == "LocationName":    name = v       # last field in the Item block
        if name is not None and None not in (x, y, z):
            break
        j += 1
    if name and None not in (x, y, z) and name not in seen:
        seen.add(name)
        locs.append((name, x, y, z))

key = re.sub(r'[^A-Za-z0-9]', '', mapname)
fn = "nodes.tsv" if mapname == "PEI" else ("nodes_%s.tsv" % key)
os.makedirs(OUTDIR, exist_ok=True)
open(os.path.join(OUTDIR, fn), "w", encoding="utf-8").write(
    "\n".join(f"{nm}\t{x:.2f},{y:.2f},{-z:.2f}" for nm, x, y, z in locs) + "\n")
print(f"map={mapname!r} locations={len(locs)} wrote {fn}")
for nm, x, y, z in locs[:25]:
    print(f'  "{nm}"  ({x:.1f}, {y:.1f}, {-z:.1f})')
