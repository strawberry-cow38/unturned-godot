#!/usr/bin/env python3
"""Extract consumable USE sounds (source ItemConsumeableAsset.use / ConsumeAudioClip) + map id->sound.
- 28 shared Sounds/Eat*/Drink* clips + per-item medical 'use' clips -> content/sounds/*.wav (UnityPy exports RIFF/WAV)
- content/consumable_sounds.tsv: id <TAB> soundfile  (from each retail .dat's ConsumeAudioClip or its in-folder use clip)."""
import UnityPy, os, re, glob
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
ITEMS = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
OUT = r"C:\claude-workspace\unturned-godot\game\content\sounds"; os.makedirs(OUT, exist_ok=True)
env = UnityPy.load(MB)
def export(o, fn):
    try:
        for _, data in o.read().samples.items():
            open(os.path.join(OUT, fn), "wb").write(data); return True
    except Exception: return False
    return False
shared = {}     # 'eatcrunchy' (lc) -> 'EatCrunchy.wav'
peritem = {}    # folder(lc) -> 'use_<folder>.wav'
for path, o in env.container.items():
    if o.type.name != "AudioClip": continue
    p = str(path); pl = p.lower(); base = os.path.splitext(os.path.basename(p))[0]
    if "/sounds/" in pl and re.match(r"(eat|drink)", base, re.I):
        if base.lower() not in shared and export(o, base + ".wav"): shared[base.lower()] = base + ".wav"
    else:
        m = re.search(r"items/(?:food|medical|water)/([^/]+)/", pl)
        if m and m.group(1).lower() not in peritem:
            if export(o, "use_" + m.group(1).lower() + ".wav"): peritem[m.group(1).lower()] = "use_" + m.group(1).lower() + ".wav"
print(f"exported {len(shared)} shared + {len(peritem)} per-item sounds")
# map id -> sound
rows = []
for root, dirs, files in os.walk(ITEMS):
    dat = next((f for f in glob.glob(os.path.join(root,"*.dat")) if "english" not in os.path.basename(f).lower()), None)
    if not dat: continue
    txt = open(dat, encoding="utf-8-sig", errors="ignore").read()
    t = re.search(r"(?m)^\s*Type\s+(\S+)", txt)
    if not t or t.group(1).lower() not in ("food","water","medical"): continue
    idm = re.search(r"(?m)^\s*ID\s+(\d+)", txt)
    if not idm: continue
    i = int(idm.group(1)); folder = os.path.basename(root).lower()
    cac = re.search(r"(?m)^\s*ConsumeAudioClip\s+(\S+)", txt)
    snd = None
    if cac:
        b = os.path.splitext(os.path.basename(cac.group(1)))[0].lower()
        snd = shared.get(b)
    if not snd and folder in peritem: snd = peritem[folder]
    if snd: rows.append((i, os.path.splitext(snd)[0]))
rows = sorted(set(rows))
with open(r"C:\claude-workspace\unturned-godot\game\content\consumable_sounds.tsv","w") as f:
    for i, s in rows: f.write(f"{i}\t{s}\n")
print(f"mapped {len(rows)} id->sound")
for did in (13,14,15,95): print(f"  id {did}: {dict(rows).get(did)}")
