"""Extract ANY barricade's placed world mesh (barricade.prefab) + albedo from core.masterbundle.

    python extract_barricade_mesh.py cagelight:Cagelight_0 [<folder>:<OutName> ...]

`<folder>` is the container folder under `assets/coremasterbundle/items/barricades/`; `<OutName>` is
what lands in content/objects as `<OutName>.obj` (+ `<OutName>_tex.png`).

This is the GENERAL form of the walk/combine/albedo pass that extract_wooden_doors.py, extract_garages.py
and extract_trap_meshes.py each carry their own copy of -- same LOD0 pick, same world-transform bake, same
X-negate, so a mesh out of here lines up with every other extracted prop and needs no per-asset fixup.
Written when the cage light would otherwise have been a fourth copy; point new barricades at this instead.

Barricades are authored LYING FLAT -- DeployableDef's +90 X stand-up puts them upright at render time, and
in that flat frame authored +Y is the face and -Z is "up" once stood. See reference_unturned_prop_orientation.
"""
import UnityPy, os, sys, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
container = {p.lower(): obj for p, obj in env.container.items()}

def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        co = by_id.get(pid)
        if co: yield co

def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None

def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                  [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def find_lodgroup(go, depth=0):
    if not go or depth > 10: return None
    tt = go.read_typetree(); kids = []
    for co in comps(tt):
        if co.type.name == "LODGroup": return co
        if co.type.name == "Transform": kids = co.read_typetree().get("m_Children", [])
    for ch in kids:
        ct = by_id.get(ch.get("m_PathID"))
        if ct:
            r = find_lodgroup(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")), depth + 1)
            if r: return r
    return None

# SKIPPED SUBTREES. "radius" and "spots" join the list the doors did not need: a cage light's prefab carries a
# Radius sphere (the placement-range gizmo, a 1 m ball) and a Spots node of lights. The gizmo has a real
# MeshFilter, so combining it would bury the fixture inside a sphere ten times its size.
SKIP = {"dead", "ragdoll", "effect", "nav", "block", "trap", "radius", "spots"}

def walk(go_pid, parentM, gomap):
    go = by_id.get(go_pid)
    if not go: return
    tt = go.read_typetree()
    if (tt.get("m_Name", "") or "").lower() in SKIP: return
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mf = comp_of(tt, ("MeshFilter",))
    mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
    if mp is None:   # some barricade parts are SkinnedMeshRenderer with no MeshFilter -- read the SMR's mesh
        smr = comp_of(tt, ("SkinnedMeshRenderer",))
        mp = smr.read_typetree().get("m_Mesh", {}).get("m_PathID") if smr else None
    gomap[go_pid] = (M, mp)
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, gomap)

def mesh_name(mp):
    try: return by_id[mp].read_typetree().get("m_Name", "")
    except Exception: return ""

def lod0_gos(prefab, gomap):
    lg = find_lodgroup(prefab)
    if lg:
        lods = lg.read_typetree().get("m_LODs", [])
        if lods:
            gos = []
            for r in lods[0].get("renderers", lods[0].get("_renderers", [])):
                rp = (r.get("renderer") or {}).get("m_PathID"); rc = by_id.get(rp)
                if rc:
                    gp = rc.read_typetree().get("m_GameObject", {}).get("m_PathID")
                    if gp in gomap: gos.append(gp)
            return gos
    return [g for g, (M, mp) in gomap.items() if mp]

def extract(prefab, TARGET):
    rt = comp_of(prefab.read_typetree(), ("Transform", "RectTransform")).read_typetree()
    root_local = trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])
    gomap = {}; walk(prefab.path_id, np.linalg.inv(root_local), gomap)
    Vs, Ns, Ts, Fs, used = [], [], [], [], []
    for gp in lod0_gos(prefab, gomap):
        M, mp = gomap[gp]
        M = M.copy(); M[0, 3] = -M[0, 3]
        if not mp or mp not in by_id: continue
        used.append(mesh_name(mp))
        txt = by_id[mp].read().export()
        Rn = np.linalg.inv(M[:3, :3]).T
        vb, tb, nb = len(Vs), len(Ts), len(Ns)
        for line in txt.splitlines():
            p = line.split()
            if not p: continue
            if p[0] == "v":
                w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); Vs.append((w[0], w[1], w[2]))
            elif p[0] == "vn":
                n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])]); L = (n[0]**2+n[1]**2+n[2]**2)**0.5; Ns.append(tuple(n/L if L > 0 else n))
            elif p[0] == "vt": Ts.append((p[1], p[2]))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/"); vi = int(q[0])+vb; ti = (int(q[1])+tb) if len(q) > 1 and q[1] else None; ni = (int(q[2])+nb) if len(q) > 2 and q[2] else None
                    idx.append((vi, ti, ni))
                Fs.append(idx)
    if not Vs:
        print("  !! NO GEOMETRY for", TARGET); return False
    L = ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
            elif ni: s += " %d//%d" % (vi, ni)
            elif ti: s += " %d/%d" % (vi, ti)
            else: s += " %d" % vi
        L.append(s)
    open(os.path.join(OUT, TARGET + ".obj"), "w").write("\n".join(L) + "\n")
    best = None
    for gp, (M, mp) in gomap.items():
        go = by_id.get(gp)
        if not go: continue
        mr = comp_of(go.read_typetree(), ("MeshRenderer", "SkinnedMeshRenderer"))
        if not mr: continue
        for matref in mr.read_typetree().get("m_Materials", []):
            mat = by_id.get(matref.get("m_PathID"))
            if not mat: continue
            for entry in mat.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                if isinstance(entry, (list, tuple)) and len(entry) == 2: nm, env_ = entry
                elif isinstance(entry, dict): nm, env_ = entry.get("first", entry.get("Key", "?")), entry.get("second", entry.get("Value", {}))
                else: continue
                if not isinstance(env_, dict): continue
                tex = by_id.get((env_.get("m_Texture") or {}).get("m_PathID"))
                if tex and tex.type.name == "Texture2D":
                    try:
                        img = tex.read().image; area = img.width * img.height
                        if not best or area > best[0]: best = (area, img)
                    except Exception: pass
    tex_ok = False
    if best:
        best[1].save(os.path.join(OUT, TARGET + "_tex.png")); tex_ok = True
    xs = [v[0] for v in Vs]; ys = [v[1] for v in Vs]; zs = [v[2] for v in Vs]
    print("  ok %-18s verts=%-5d parts=%d %s tex=%s" % (TARGET, len(Vs), len(used), used, "yes" if tex_ok else "NO"))
    print("     bounds x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]" % (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
    return True

args = sys.argv[1:]
if not args:
    print(__doc__); raise SystemExit(2)
ok = 0
for spec in args:
    src, _, outname = spec.partition(":")
    if not outname: outname = src.capitalize() + "_0"
    cont = f"assets/coremasterbundle/items/barricades/{src}/barricade.prefab"
    prefab = container.get(cont)
    if not prefab or prefab.type.name != "GameObject":
        print("  !! prefab missing:", cont); continue
    try:
        if extract(prefab, outname): ok += 1
    except Exception as e:
        print("  !! FAILED", outname, repr(e))
print(f"\nDONE {ok}/{len(args)} barricade meshes -> {OUT}")
