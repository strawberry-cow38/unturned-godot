#!/usr/bin/env python3
"""Extract source-accurate consumable EFFECT stats from every retail Food/Water/Medical item .dat ->
content/consumable_stats.tsv: id  health food water virus disinfectant energy bleeding bones qmin qmax
(bleeding/bones: 0=None 1=Heal 2=Cut/Break -- source ItemConsumeableAsset.PopulateAsset).
qmin/qmax = the item's Quality_Min/Quality_Max spawn band (source ItemAsset: default 10/90). Food CONDITION
rides `quality` (0-100): a WORLD-spawned FOOD/WATER item rolls Random(qmin, qmax) for its freshness, and eating
one at <50 quality scales its food/water down + can infect you (UseableConsumeable.performUseOnSelf). Perishable
raw foods ship Quality_Max 50 (can spawn already-moldy); preserved (MRE) ship Quality_Min 50.
Paths are overridable via env UG_ITEMS_DIR / UG_STATS_OUT so this runs off the box's own bundle mirror."""
import os, re, glob
BASE = os.environ.get("UG_ITEMS_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items")
OUT  = os.environ.get("UG_STATS_OUT",  r"C:\claude-workspace\unturned-godot\game\content\consumable_stats.tsv")
def parse_dat(folder):
    for dat in glob.glob(os.path.join(folder, "*.dat")):
        if "english" in os.path.basename(dat).lower(): continue
        kv = {}
        try:
            for ln in open(dat, encoding="utf-8-sig", errors="ignore"):
                m = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)\s+(\S+)", ln)
                if m: kv.setdefault(m.group(1), m.group(2))
                else:
                    m2 = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)\s*$", ln)   # bare flag key (e.g. "Bleeding")
                    if m2: kv.setdefault(m2.group(1), "")
        except Exception: pass
        return kv
    return {}
def u8(kv, k, default=0):
    try: return max(0, min(255, int(kv.get(k, str(default)))))
    except: return default
BLEED = {"none":0,"heal":1,"cut":2}; BONES = {"none":0,"heal":1,"break":2}
rows = []
for root, dirs, files in os.walk(BASE):
    kv = parse_dat(root)
    if not kv: continue
    t = kv.get("Type", "").lower()
    if t not in ("food", "water", "medical"): continue
    try: i = int(kv.get("ID", "-1"))
    except: continue
    if i < 0: continue
    bleeding = 1 if "Bleeding" in kv and kv.get("Bleeding","")=="" else BLEED.get(kv.get("Bleeding_Modifier","none").lower(), 0)
    bones    = 1 if "Bones"    in kv and kv.get("Bones","")==""    else BONES.get(kv.get("Bones_Modifier","none").lower(), 0)
    qmin = u8(kv, "Quality_Min", 10)   # source ItemAsset defaults: min 10, max 90 when the .dat is silent
    qmax = u8(kv, "Quality_Max", 90)
    rows.append((i, u8(kv,"Health"), u8(kv,"Food"), u8(kv,"Water"), u8(kv,"Virus"), u8(kv,"Disinfectant"), u8(kv,"Energy"), bleeding, bones, qmin, qmax))
rows = sorted(set(rows))
with open(OUT, "w") as f:
    for r in rows: f.write("\t".join(str(x) for x in r) + "\n")
print(f"wrote {len(rows)} consumable stat rows")
for did in (13,14,15,95):
    r = next((x for x in rows if x[0]==did), None); print(f"  id {did}: {r}")
# a few interesting: antibiotics(disinfect), energy drink(energy)
for nm,did in (("antibiotics?",0),): pass
print("  sample energy/virus rows:", [r for r in rows if r[5]>0 or r[6]>0][:4])
