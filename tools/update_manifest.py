import os, re
OUT = r"C:\claude-workspace\unturned-godot\game\content\resources"
TREES = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Trees"
lines = []
for line in open(os.path.join(OUT, "resources.txt")):
    parts = line.split()
    if len(parts) < 2:
        continue
    name, n = parts[0], parts[1]
    dat = os.path.join(TREES, name, name + ".dat")
    hol = "NONE"
    if os.path.exists(dat):
        m = re.search(r"Holiday_Restriction\s+(\S+)", open(dat, errors="ignore").read())
        if m:
            hol = m.group(1)
    lines.append(f"{name} {n} {hol}")
open(os.path.join(OUT, "resources.txt"), "w").write("\n".join(lines) + "\n")
print("\n".join(lines))
