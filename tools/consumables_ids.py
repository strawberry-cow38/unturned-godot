#!/usr/bin/env python3
"""Map each RETAIL item (authoritative ID from its .dat) -> its extracted held mesh (consumeout/<name>.txt).
Walks ALL of Bundles/Items recursively (retail nests Food/Drinks/... etc.); only the 136 food/medical items
have an extracted mesh, so only those match. Reports the demo ids + total."""
import os, re, glob
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\consumeout"
meshes = set(n.strip().lower() for n in open(os.path.join(OUT, "consumables.txt")) if n.strip())
def dat_id(folder):
    for dat in glob.glob(os.path.join(folder, "*.dat")):
        if "english" in os.path.basename(dat).lower(): continue
        try:
            for ln in open(dat, encoding="utf-8-sig", errors="ignore"):
                m = re.match(r"\s*ID\s+(\d+)", ln)
                if m: return int(m.group(1))
        except Exception: pass
    return None
byid = {}
for root, dirs, files in os.walk(BASE):
    fn = os.path.basename(root).lower()
    if fn not in meshes: continue          # only folders whose name matches an extracted mesh
    i = dat_id(root)
    if i is not None and i not in byid: byid[i] = fn
rows = sorted(byid.items())
with open(os.path.join(OUT, "consumables.tsv"), "w") as f:
    for i, nm in rows: f.write(f"{i}\t{nm}\n")
matched_meshes = set(v for _, v in rows)
print(f"MAPPED {len(rows)} / {len(meshes)} meshes | meshes with NO id: {sorted(meshes - matched_meshes)}")
for did in (13,14,15,95): print(f"  demo id {did}: {dict(rows).get(did,'<<MISSING>>')}")
