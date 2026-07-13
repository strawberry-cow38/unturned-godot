import glob, os, re
VEH = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Vehicles"
rows = []
for dat in glob.glob(VEH + r"\*\*.dat"):
    txt = open(dat, encoding="utf-8-sig", errors="ignore").read()
    name = os.path.basename(os.path.dirname(dat))
    idm = re.search(r"^\s*ID\s+(\d+)", txt, re.M)
    # every "#rrggbb" (with optional // faction label) in the .dat = a paint colour
    hexes = re.findall(r'"(#[0-9a-fA-F]{6})"', txt)
    israndom = bool(re.search(r"RandomHueOrGrayscale", txt))
    gray = re.search(r"GrayscaleChance\s+([\d.]+)", txt)
    if hexes or israndom:
        rows.append((int(idm.group(1)) if idm else 0, name, hexes, israndom, gray.group(1) if gray else "-"))
rows.sort()
for id_, name, hexes, israndom, gray in rows:
    tag = "RANDOM(gray=%s)" % gray if israndom else "LIST"
    print(f"{name:18s} id={id_:<4} {tag:18s} {hexes}")
print(len(rows), "vehicles with paint")
