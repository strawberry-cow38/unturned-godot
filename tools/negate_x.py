import os
content = r"C:\claude-workspace\unturned-godot\game\content"
def negx_mesh(path):
    if not os.path.exists(path): return False
    out = []
    for ln in open(path).read().splitlines():
        p = ln.split()
        if len(p) == 4 and (p[0] == "v" or p[0] == "vn"):
            out.append(f"{p[0]} {-float(p[1]):.6f} {p[2]} {p[3]}")
        else:
            out.append(ln)
    open(path, "w").write("\n".join(out) + "\n"); return True
# the EXTRACTED models only (leave the 3 hardcoded eaglefire/maplestrike/masterkey -- the repo's are already correct)
files = []
for ln in open(content + r"\guns_visual.tsv"):
    n = ln.strip().split("\t")[0]
    if n: files.append(n + "_gun.txt")
for ln in open(content + r"\melee_list.tsv"):
    n = ln.strip().split("\t")[0]
    if n: files.append(n + ".txt")
for ln in open(content + r"\sights.tsv"):
    n = ln.strip().split("\t")[0]
    if n: files.append(n + "_sight.txt")
files += ["rocket_projectile.txt", "grenade.txt"]
nm = sum(1 for f in files if negx_mesh(os.path.join(content, f)))
print("negated X in", nm, "extracted meshes")
# hooks in the tsvs (muzzle/aim in guns_visual, mount in sights)
def negx_hooks(path, cols):
    out = []
    for ln in open(path).read().splitlines():
        if not ln.strip(): continue
        c = ln.split("\t")
        for i in cols:
            if i < len(c) and "," in c[i]:
                xyz = c[i].split(","); xyz[0] = str(round(-float(xyz[0]), 4)); c[i] = ",".join(xyz)
        out.append("\t".join(c))
    open(path, "w").write("\n".join(out) + "\n")
negx_hooks(content + r"\guns_visual.tsv", [1, 2])
negx_hooks(content + r"\sights.tsv", [2])
print("negated X in tsv hooks (muzzle/aim, sight mount)")
