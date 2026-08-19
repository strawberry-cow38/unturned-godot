#!/usr/bin/env python3
"""Derive road/rail CONNECTION POINTS for the tile props, from the meshes themselves.

strawberry 2026-08-19: "giving certain props road/rail connection points. for example all the road 'cap'
props have snap points where roads will connect to".

The rule is read off the geometry rather than typed in by hand, because a snap point 20 cm out is worse
than none and hand-typed coordinates rot the moment a mesh is re-extracted:

  * these props are square tiles (Road_* are 24x24, mesh Z up before the placement pitch),
  * the road SURFACE is a strip narrower than the tile (16 m on the 24 m tiles),
  * so a tile EDGE that the road opens onto carries top-surface vertices at the strip's boundary --
    strictly INSIDE the tile corners -- while a closed edge carries only its two corner vertices.

Emits content/objects/road_connectors.txt:  <PropName> <x> <y> <z> <dx> <dy> <dz>
one line per connection, mesh-local, with the outward normal. Runtime transforms them by the placement
basis, so no assumption about the Z-up->Y-up pitch is baked into the data.
"""
import os, sys, glob

OBJ_DIR = os.path.join(os.path.dirname(__file__), "..", "game", "content", "objects")
OUT = os.path.join(OBJ_DIR, "road_connectors.txt")
# Only the tile families that actually carry a road/rail surface. Deliberately a allow-list: running this
# over every prop would "find" connections on any square mesh with a inset top face.
# Road_ and Bridge_ only. Tunnel_Line_Cap_0 is deliberately EXCLUDED: its modal vertex plane is the tile
# underside, not a carriageway, and its top surface is the tunnel ROOF -- the rule below reads either as an
# opening and emits nonsense. A tunnel mouth needs its own look; better absent than wrong.
FAMILIES = ("Road_", "Bridge_")
MIN_OPENING = 8.0    # metres: a real carriageway. Rejects the 0-wide slivers the cap kerbs produce.
MAX_OPENING = 20.0   # ...and the other end. The carriageway in this tileset is 16-16.5 m; spans of 31 and
                     # 44 m turned up on the big bridge decks, which is the deck's SIDE structure being read
                     # as an opening. A 44 m mouth on a 16 m road is not a thing, so it is rejected loudly.

def load(path):
    v = []
    for line in open(path):
        if line.startswith("v "):
            a = line.split()
            v.append((float(a[1]), float(a[2]), float(a[3])))
    return v

def edge_name_for(axis, val, x0, x1, y0, y1):
    if axis == 0: return "+X" if abs(val - x1) < 0.01 else "-X"
    return "+Y" if abs(val - y1) < 0.01 else "-Y"

def connectors(v, eps=0.02):
    # THE ROAD PLANE, not the top of the mesh. Reading zmax put the cap props' raised kerb wall in as the
    # surface, which produced 0-wide "openings" on their closed edges and a 20 m one on Road_Quad_Cap_0.
    # The carriageway is the modal vertex height -- it is the biggest flat sheet in these tiles by a margin.
    import collections
    zc = collections.Counter(round(p[2], 2) for p in v)
    zroad = zc.most_common(1)[0][0]
    top = [p for p in v if abs(p[2] - zroad) < eps]
    rejected = []
    # Tile bounds come from the ROAD PLANE's own extent, so a kerb sticking out past it (Road_*_Cap_* reach
    # y=14 while the tile ends at 12) cannot move where we think the edges are.
    xs = [p[0] for p in top]; ys = [p[1] for p in top]
    x0, x1, y0, y1, zmax = min(xs), max(xs), min(ys), max(ys), zroad
    out = []
    for axis, val, name, outward in ((0, x1, "+X", (1, 0, 0)), (0, x0, "-X", (-1, 0, 0)),
                                     (1, y1, "+Y", (0, 1, 0)), (1, y0, "-Y", (0, -1, 0))):
        edge = [p for p in top if abs(p[axis] - val) < eps]
        if not edge:
            continue
        other = 1 - axis
        lo, hi = (y0, y1) if axis == 0 else (x0, x1)
        # strictly INSIDE the corners = the road strip meets this edge
        inner = [p[other] for p in edge if lo + 0.1 < p[other] < hi - 0.1]
        if len(inner) < 2:
            continue
        span = max(inner) - min(inner)
        if span < MIN_OPENING or span > MAX_OPENING:
            rejected.append((edge_name_for(axis, val, x0, x1, y0, y1), round(span, 2)))
            continue
        mid = 0.5 * (min(inner) + max(inner))
        pt = [0.0, 0.0, zmax]
        pt[axis] = val
        pt[other] = mid
        out.append((pt, outward, name, round(span, 2)))
    return out, rejected

def main():
    rows, report = [], []
    for path in sorted(glob.glob(os.path.join(OBJ_DIR, "*.obj"))):
        name = os.path.basename(path)[:-4]
        if not name.startswith(FAMILIES) or name.endswith("_lod1"):
            continue
        try:
            v = load(path)
        except Exception as e:
            print(f"  !! {name}: {e}", file=sys.stderr); continue
        if not v:
            continue
        cs, rej = connectors(v)
        if rej:
            report.append(f"  {name:22} REJECTED {rej} (outside {MIN_OPENING}-{MAX_OPENING} m carriageway)")
        if not cs:
            report.append(f"  {name:22} !! NO connectors derived -- geometry not understood, left out")
            continue
        for pt, d, edge, width in cs:
            rows.append(f"{name} {pt[0]:.3f} {pt[1]:.3f} {pt[2]:.3f} {d[0]} {d[1]} {d[2]}")
        report.append(f"  {name:22} {len(cs)} connector(s): " +
                      ", ".join(f"{e}(w={w})" for _, _, e, w in cs))
    with open(OUT, "w") as f:
        f.write("# prop connection points, DERIVED by tools/extract_road_connectors.py -- do not hand-edit\n")
        f.write("# <PropName> <x> <y> <z> <dx> <dy> <dz>   (mesh-local, outward normal)\n")
        f.write("\n".join(rows) + "\n")
    print(f"wrote {len(rows)} connectors for {len(report)} props -> {os.path.relpath(OUT)}")
    print("\n".join(report))

main()
