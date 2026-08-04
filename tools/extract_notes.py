"""Rip every readable NOTE object's text (Interactability Note): GUID + Name + the Interactability_Text_Line_N
lines from each object's English.dat -> content/note_texts.tsv (guid<TAB>name<TAB>line0<TAB>line1...). The .dat
GUID is already the netguid form used by placements/guid_mesh, so the port keys notes off it directly. Shared
objects, so this covers every map (PEI + Washington) at once."""
import os, glob, re
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Objects"
OUT  = r"C:\claude-workspace\unturned-godot\game\content\note_texts.tsv"
rows = []
for datp in glob.glob(os.path.join(BUND, "**", "*.dat"), recursive=True):
    try: txt = open(datp, "r", errors="ignore").read()
    except Exception: continue
    if re.search(r"Interactability\s+Note", txt) is None: continue
    g = re.search(r"GUID\s+([0-9a-fA-F]{32})", txt)
    if not g: continue
    guid = g.group(1).lower()
    eng = os.path.join(os.path.dirname(datp), "English.dat")
    if not os.path.exists(eng): continue
    et = open(eng, "r", errors="ignore").read()
    nm = re.search(r"^\s*Name\s+(.+?)\s*$", et, re.M)
    name = (nm.group(1) if nm else os.path.basename(os.path.dirname(datp))).replace("\t", " ").strip()
    lines = []
    i = 0
    while True:
        lm = re.search(r"^\s*Interactability_Text_Line_%d\b[ \t]*(.*)$" % i, et, re.M)
        if lm is None: break
        lines.append(lm.group(1).replace("\t", " ").rstrip())
        i += 1
    rows.append((guid, name, lines))

rows.sort(key=lambda r: r[1])
with open(OUT, "w", encoding="utf-8") as f:
    for guid, name, lines in rows:
        f.write("\t".join([guid, name] + lines) + "\n")
print("wrote %d notes -> note_texts.tsv" % len(rows))
for guid, name, lines in rows[:8]:
    print("  %s  %s  (%d lines)" % (guid[:8], name, len(lines)))
