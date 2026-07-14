#!/usr/bin/env python3
"""Extract source-accurate consumable EFFECT stats from every retail Food/Water/Medical item .dat ->
content/consumable_stats.tsv: id  health food water virus disinfectant energy bleeding bones
(bleeding/bones: 0=None 1=Heal 2=Cut/Break -- source ItemConsumeableAsset.PopulateAsset)."""
import os, re, glob
BASE = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\consumable_stats.tsv"
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
def u8(kv, k): 
    try: return max(0, min(255, int(kv.get(k, "0"))))
    except: return 0
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
    rows.append((i, u8(kv,"Health"), u8(kv,"Food"), u8(kv,"Water"), u8(kv,"Virus"), u8(kv,"Disinfectant"), u8(kv,"Energy"), bleeding, bones))
rows = sorted(set(rows))
with open(OUT, "w") as f:
    for r in rows: f.write("\t".join(str(x) for x in r) + "\n")
print(f"wrote {len(rows)} consumable stat rows")
for did in (13,14,15,95):
    r = next((x for x in rows if x[0]==did), None); print(f"  id {did}: {r}")
# a few interesting: antibiotics(disinfect), energy drink(energy)
for nm,did in (("antibiotics?",0),): pass
print("  sample energy/virus rows:", [r for r in rows if r[5]>0 or r[6]>0][:4])
