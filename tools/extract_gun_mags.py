import UnityPy, os, re, glob
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
MAGS = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Magazines"
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
def gun_mag_hook(gun):   # the gun item.prefab's "Magazine" child hook (where the mag mounts)
    p = next((o for pa, o in cont.items() if f"/guns/{gun}/item.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p: return None
    _, lp = child_named(p, "Magazine")
    return (lp["x"], lp["y"], lp["z"]) if lp else None
def mag_model(folder):   # the magazine.prefab's Model_0 mesh + its local pos (the MOUNTED attachment model, like sight.prefab)
    p = next((o for pa, o in cont.items() if f"/magazines/{folder}/magazine.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p:   # fallback: some mags carry the model on item.prefab instead
        p = next((o for pa, o in cont.items() if f"/magazines/{folder}/item.prefab" in pa.lower() and o.type.name == "GameObject"), None)
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
    L = ["# %s magazine" % name] + ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts] + ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    open(outpath, "w").write("\n".join(L) + "\n"); return len(Vs)
smap = {}
for dat in glob.glob(MAGS + r"\*\*.dat"):
    m = re.search(r"^\s*ID\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if m: smap[m.group(1)] = os.path.basename(os.path.dirname(dat)).lower()
guns = [l.strip().split("\t")[0] for l in open(content + r"\guns_visual.tsv") if l.strip()]
lines = []
for gun in guns:
    dat = os.path.join(content, gun + ".dat")
    if not os.path.exists(dat): continue
    m = re.search(r"^\s*Magazine\s+(\d+)", open(dat, encoding="utf-8-sig", errors="ignore").read(), re.M)
    if not m: print(f"{gun}: no Magazine (shell/bow/launcher) -- skip"); continue
    folder = smap.get(m.group(1))
    if not folder: print(f"{gun}: mag {m.group(1)} not in Items/Magazines -- skip"); continue
    hook = gun_mag_hook(gun); mesh, mm0 = mag_model(folder)
    if not (hook and mesh and mm0): print(f"{gun}: mag {m.group(1)} ({folder}) -- hook={hook is not None} mesh={mesh is not None}"); continue
    nv = export_mesh(mesh, os.path.join(content, gun + "_mag.txt"), gun)
    mount = (round(hook[0] + mm0[0], 4), round(hook[1] + mm0[1], 4), round(-(hook[2] + mm0[2]), 4))
    lines.append(f"{gun}\t{gun}_mag.txt\t{mount[0]},{mount[1]},{mount[2]}")
    print(f"{gun}: mag {m.group(1)} ({folder}) verts={nv} mount={mount}")
open(content + r"\mags.tsv", "w").write("\n".join(lines) + "\n")
print("mags.tsv:", len(lines), "guns with default magazines")
