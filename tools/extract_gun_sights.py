import UnityPy, os, re, glob
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
SIGHTS = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Sights"
content = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = env.container
def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None
def child_named(prefab, name):
    tr = comp_of(prefab.read_typetree(), ("Transform",))
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        ctt = ct.read_typetree()
        cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == name:
            return cgo, ctt["m_LocalPosition"]
    return None, None
def gun_sight_hook(gun):
    p = next((o for pa, o in cont.items() if f"/guns/{gun}/item.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p: return None
    _, lp = child_named(p, "Sight")
    return (lp["x"], lp["y"], lp["z"]) if lp else None
def sight_model(folder):
    p = next((o for pa, o in cont.items() if f"/sights/{folder}/sight.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p: return None, None
    m0, lp = child_named(p, "Model_0")
    if not m0: return None, None
    mf = comp_of(m0.read_typetree(), ("MeshFilter",))
    mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
    return mesh, (lp["x"], lp["y"], lp["z"])
def export_mesh(mesh, outpath, name):
    txt = mesh.read().export(); Vs, Ns, Ts, Fs = [], [], [], []
    for line in txt.splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v": Vs.append((float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vn": Ns.append((float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vt": Ts.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/"); idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
            Fs.append(list(reversed(idx)))
    L = ["# %s sight" % name] + ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts] + ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    open(outpath, "w").write("\n".join(L) + "\n"); return len(Vs)
smap = {}
for dat in glob.glob(SIGHTS + r"\*\*.dat"):
    m = re.search(r"^\s*ID\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if m: smap[m.group(1)] = os.path.basename(os.path.dirname(dat)).lower()
guns = [l.strip().split("\t")[0] for l in open(content + r"\guns_visual.tsv") if l.strip()]
lines = []
for gun in guns:
    dat = os.path.join(content, gun + ".dat")
    if not os.path.exists(dat): continue
    m = re.search(r"^\s*Sight\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if not m: continue
    folder = smap.get(m.group(1))
    if not folder: continue
    hook = gun_sight_hook(gun); mesh, sm0 = sight_model(folder)
    if not (hook and mesh and sm0): print(f"{gun}: sight {m.group(1)} ({folder}) -- missing hook/mesh"); continue
    nv = export_mesh(mesh, os.path.join(content, gun + "_sight.txt"), gun)
    mount = (round(hook[0] + sm0[0], 4), round(hook[1] + sm0[1], 4), round(-(hook[2] + sm0[2]), 4))
    lines.append(f"{gun}\t{gun}_sight.txt\t{mount[0]},{mount[1]},{mount[2]}")
    print(f"{gun}: sight {m.group(1)} ({folder}) verts={nv} mount={mount}")
open(content + r"\sights.tsv", "w").write("\n".join(lines) + "\n")
print("sights.tsv:", len(lines), "guns with default sights")

# --- FIX the ADS aim hooks. guns_visual.tsv's aim was Sight-hook + a FIXED eaglefire offset (extract_gun.py:157,
# "fit to eaglefire, tune later"), so any gun whose sight geometry differs aimed off-centre. Recompute the REAL aim =
# z/x-neg(SightHook + sight Model_0 local + sight's Aim hook), the exact composition the hand-tuned eaglefire uses
# (verified: reproduces its 0,-0.4688,-0.2098), and patch guns_visual.tsv's aim column per sighted gun.
def sight_aim_local(folder):   # the sight prefab's Model_0 child "Aim" (the aperture eye point), local to Model_0
    p = next((o for pa, o in cont.items() if f"/sights/{folder}/sight.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p: return None
    m0, _ = child_named(p, "Model_0")
    if not m0: return None
    _, alp = child_named(m0, "Aim")
    return (alp["x"], alp["y"], alp["z"]) if alp else None

real_aims = {}
for gun in guns:
    dat = os.path.join(content, gun + ".dat")
    if not os.path.exists(dat): continue
    m = re.search(r"^\s*Sight\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if not m: continue
    folder = smap.get(m.group(1))
    if not folder: continue
    hook = gun_sight_hook(gun); _, sm0 = sight_model(folder); aim = sight_aim_local(folder)
    if not (hook and sm0 and aim): print(f"AIM {gun}: no Aim hook (sight {folder}) -- keeping old"); continue
    real_aims[gun] = (round(-(hook[0]+sm0[0]+aim[0]), 4), round(hook[1]+sm0[1]+aim[1], 4), round(-(hook[2]+sm0[2]+aim[2]), 4))

gvpath = content + r"\guns_visual.tsv"
out, patched = [], 0
for line in open(gvpath):
    if not line.strip(): out.append(line.rstrip("\n")); continue
    c = line.rstrip("\n").split("\t")
    if len(c) >= 4 and c[0] in real_aims:
        ra = real_aims[c[0]]; print(f"PATCH {c[0]}: aim {c[2]} -> {ra[0]},{ra[1]},{ra[2]}"); c[2] = f"{ra[0]},{ra[1]},{ra[2]}"; patched += 1
    out.append("\t".join(c))
open(gvpath, "w").write("\n".join(out) + "\n")
print("patched", patched, "REAL aim hooks into guns_visual.tsv")
