import sys, os, re
# DEADZONE volumes from a map's Level.hierarchy (SDG.Framework.Devkit.DeadzoneVolume blocks).
# Emits content/deadzones.tsv (PEI) / deadzones_<key>.tsv, read by DeadzoneMap at world build.
#
# Row shape:  shape <TAB> X,Y,-Z <TAB> hx,hy,hz <TAB> kind
#   - Z is NEGATED to Godot space, exactly as parse_nodes.py and parse_hierarchy_locations.py do, so a
#     deadzone lands where the placed objects put the ground under it.
#   - The hierarchy's "Scale" is the volume's FULL SIZE, not its half-extent. Derived, not assumed: PEI's
#     FoliageVolume is scale 2048 on a 2048 m map (2x2 of Terrain.cs's 1024 m tiles), and its
#     CartographyVolume is 1922x1926 -- the island, just inside the map edge. So half-extent = scale/2, and
#     a Sphere of scale 32 has RADIUS 16.
#   - Rates are NOT taken from the map. Retail's UnprotectedRadiationPerSecond on PEI is 6.25; ours is 0.020
#     because DeadzoneDef's numbers are DOSE rates re-derived against our infection model (see the comment
#     on DeadzoneDef.Default -- ~52 s to death at full intensity). 6.25 in that field would be 312x the
#     tuned value. The map types this zone "DefaultRadiation", i.e. "use the default radiation profile", so
#     we emit the KIND and apply our own default for it. Geometry from the map, tuning from our model.
MAPBASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps"
OUTDIR  = r"C:\claude-workspace\unturned-godot\game\content"
mapname = sys.argv[1] if len(sys.argv) > 1 else "PEI"
if len(sys.argv) > 3 and sys.argv[1] == "--local":
    path, OUTDIR = sys.argv[2], sys.argv[3]
    mapname = sys.argv[4] if len(sys.argv) > 4 else "PEI"
else:
    path = os.path.join(MAPBASE, mapname, "Level.hierarchy")

DZ_TYPE = "SDG.Framework.Devkit.DeadzoneVolume"
lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
n = len(lines)
zones = []
for i, line in enumerate(lines):
    if DZ_TYPE not in line:
        continue
    nums, shape, kind = [], "Box", "Radiation"
    j = i + 1
    while j < n and '"Type"' not in lines[j]:      # next Type ends this block
        p = re.findall(r'"([^"]*)"', lines[j])
        if len(p) >= 2:
            k, v = p[0], p[1]
            if k in ("X", "Y", "Z", "W"):
                nums.append(float(v))
            elif k == "Shape":
                shape = v
            elif k == "Deadzone_Type":
                kind = "Radiation" if "Radiation" in v else v
        j += 1
    # Position(XYZ) Rotation(XYZW) Scale(XYZ) -- the block's fixed order, same as the locations parser.
    if len(nums) < 10:
        print(f"  SKIP malformed block at line {i+1}: only {len(nums)} components")
        continue
    px, py, pz = nums[0], nums[1], nums[2]
    sx, sy, sz = nums[7], nums[8], nums[9]
    if shape == "Sphere":
        hx = hy = hz = sx / 2.0            # uniform radius; retail authors these with a uniform scale
    else:
        hx, hy, hz = sx / 2.0, sy / 2.0, sz / 2.0
    zones.append((shape, px, py, -pz, hx, hy, hz, kind))

key = re.sub(r'[^A-Za-z0-9]', '', mapname)
fn = "deadzones.tsv" if mapname == "PEI" else ("deadzones_%s.tsv" % key)
os.makedirs(OUTDIR, exist_ok=True)
body = "".join(f"{sh}\t{x:.4f},{y:.4f},{z:.4f}\t{hx:.4f},{hy:.4f},{hz:.4f}\t{kd}\n"
               for sh, x, y, z, hx, hy, hz, kd in zones)
open(os.path.join(OUTDIR, fn), "w", encoding="utf-8").write(body)
print(f"map={mapname!r} deadzones={len(zones)} wrote {fn}")
for sh, x, y, z, hx, hy, hz, kd in zones:
    print(f"  {sh} {kd} centre=({x:.2f}, {y:.2f}, {z:.2f}) half=({hx:.2f}, {hy:.2f}, {hz:.2f})")
