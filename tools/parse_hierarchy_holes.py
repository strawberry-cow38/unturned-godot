import sys, os, re
# TERRAIN CUTS from a map's Level.hierarchy (SDG.Framework.Devkit.LandscapeHoleVolume blocks).
# Emits content/terraincuts.tsv (PEI) / terraincuts_<key>.tsv, applied by TerrainCuts at world build.
#
# Retail cuts holes in the landscape with placed VOLUMES, not with a brush -- EditorTerrain.cs:19 says so and
# the port's own Dig/Fill brush is explicitly "ours, not Devkit's". The port has the hole mask (Terrain._holes,
# per QUAD) and the editor drives it, but nothing ever read the map's authored volumes, so PEI's cut never
# existed at runtime: the terrain there was solid.
#
# Row shape:  shape <TAB> cx,cy,-cz <TAB> hx,hy,hz
#   - Z NEGATED to Godot space, as in parse_nodes.py / parse_hierarchy_locations.py / _deadzones.py.
#   - "Scale" is the volume's FULL SIZE, so half = scale/2. Derived, not assumed: PEI's FoliageVolume is
#     scale 2048 on a 2048 m map, its CartographyVolume 1922x1926 (the island, just inside the edge).
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
OUTDIR  = r"C:\claude-workspace\unturned-godot\game\content"
mapname = sys.argv[1] if len(sys.argv) > 1 else "PEI"
if len(sys.argv) > 3 and sys.argv[1] == "--local":
    path, OUTDIR = sys.argv[2], sys.argv[3]
    mapname = sys.argv[4] if len(sys.argv) > 4 else "PEI"
else:
    path = os.path.join(MAPBASE, mapname, "Level.hierarchy")

HOLE_TYPE = "SDG.Framework.Landscapes.LandscapeHoleVolume"   # NOT .Devkit. -- the deadzone volume is Devkit, this one is Landscapes
lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
n = len(lines)
cuts = []
for i, line in enumerate(lines):
    if HOLE_TYPE not in line:
        continue
    nums, shape = [], "Box"
    j = i + 1
    while j < n and '"Type"' not in lines[j]:
        p = re.findall(r'"([^"]*)"', lines[j])
        if len(p) >= 2:
            k, v = p[0], p[1]
            if k in ("X", "Y", "Z", "W"):
                nums.append(float(v))
            elif k == "Shape":
                shape = v
        j += 1
    if len(nums) < 10:
        print(f"  SKIP malformed block at line {i+1}: {len(nums)} components")
        continue
    px, py, pz = nums[0], nums[1], nums[2]
    sx, sy, sz = nums[7], nums[8], nums[9]
    cuts.append((shape, px, py, -pz, sx / 2.0, sy / 2.0, sz / 2.0))

key = re.sub(r'[^A-Za-z0-9]', '', mapname)
fn = "terraincuts.tsv" if mapname == "PEI" else ("terraincuts_%s.tsv" % key)
os.makedirs(OUTDIR, exist_ok=True)
open(os.path.join(OUTDIR, fn), "w", encoding="utf-8").write(
    "".join(f"{sh}\t{x:.4f},{y:.4f},{z:.4f}\t{hx:.4f},{hy:.4f},{hz:.4f}\n"
            for sh, x, y, z, hx, hy, hz in cuts))
print(f"map={mapname!r} terrain cuts={len(cuts)} wrote {fn}")
for sh, x, y, z, hx, hy, hz in cuts:
    print(f"  {sh} centre=({x:.2f}, {y:.2f}, {z:.2f}) half=({hx:.2f}, {hy:.2f}, {hz:.2f})"
          f"  -> spans x {x-hx:.1f}..{x+hx:.1f}, z {z-hz:.1f}..{z+hz:.1f}, y {y-hy:.1f}..{y+hy:.1f}")
