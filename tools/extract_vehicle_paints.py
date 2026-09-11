#!/usr/bin/env python3
"""Extract the vehicle spraypaint cans: item id -> paint colour.

Retail's Vehicle_Paint_Tool items each carry a single `PaintColor #rrggbb` in their .dat, which is the colour
the can sprays onto a vehicle's paintable texels (the same channel vehicle_paint.gdshader already tints from
VehicleAsset.getDefaultPaintColor). 32 of them, and the port had no concept of the type at all.

    python extract_vehicle_paints.py <BUNDLES_DIR> <OUT_TSV>
writes  <item id> <TAB> <rrggbb> <TAB> <English name>
"""
import os, re, sys, glob

BUND = sys.argv[1]
OUT  = sys.argv[2]

def kv(txt, k):
    m = re.search(r"(?im)^\s*" + re.escape(k) + r"\s+(.+?)\s*$", txt)
    return m.group(1) if m else None

rows = []
for datp in glob.glob(os.path.join(BUND, "Items", "**", "*.dat"), recursive=True):
    if os.path.basename(datp).lower() == "english.dat": continue
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    if (kv(txt, "Type") or "").strip().lower() != "vehicle_paint_tool": continue
    iid = kv(txt, "ID")
    col = kv(txt, "PaintColor") or ""
    hexv = col.strip().lstrip("#")
    if not (iid and iid.isdigit() and re.fullmatch(r"[0-9A-Fa-f]{6}", hexv)):
        print(f"[paint] {os.path.basename(datp)}: no usable ID/PaintColor (id={iid} colour={col!r})"); continue
    name = os.path.splitext(os.path.basename(datp))[0]
    eng = os.path.join(os.path.dirname(datp), "English.dat")
    if os.path.exists(eng):
        n = kv(open(eng, encoding="utf-8-sig", errors="ignore").read(), "Name")
        if n: name = n
    rows.append((int(iid), hexv, name))

rows.sort()
with open(OUT, "w") as f:
    for iid, hexv, name in rows: f.write(f"{iid}\t{hexv}\t{name}\n")
print(f"[paint] {len(rows)} spraypaints -> {OUT}")
