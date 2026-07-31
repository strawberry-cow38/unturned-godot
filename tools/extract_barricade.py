"""Extract a barricade deployable's world mesh, its albedo, and any heat volumes it carries.

  python tools/extract_barricade.py campfire Campfire

Two things this does that extract_trap_meshes.py does not:

  * the albedo comes from the picked renderer's OWN _MainTex, not "the biggest Texture2D reachable
    from the prefab". That heuristic is how the airdrop plane ended up wearing a gun skin -- when the
    real material points at a small or placeholder texture, "biggest" walks past it and grabs
    whatever else is nearby, and the result looks textured enough that nobody checks.
  * it reports the `Burning` / `Warm` child volumes. BarricadeManager attaches a TemperatureTrigger to
    any child with those names, using transform.localScale.x as the RADIUS -- that is where a
    campfire's 10 m warm sphere and 0.75 m burning core actually live, and they are invisible in the
    .dat.
"""
import UnityPy, os, sys, json, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
SUB = (sys.argv[1] if len(sys.argv) > 1 else "campfire").lower()
TARGET = sys.argv[2] if len(sys.argv) > 2 else SUB.capitalize()

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
want = f"items/barricades/{SUB}/barricade.prefab"
prefab = next((o for p, o in env.container.items()
               if o.type.name == "GameObject" and p.lower().endswith(want)), None)
if prefab is None:
    print("no barricade.prefab matching", want); sys.exit(1)


def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID")) if isinstance(c, dict) else None
        if co: yield co


def comp_of(tt, names):
    for co in comps(tt):
        if co.type.name in names: return co
    return None


def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w),   2*(x*z+y*w)],
                  [2*(x*y+z*w),   1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w),   2*(y*z+x*w),   1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M


nodes = {}          # path_id -> (matrix, mesh pid, name, renderer)
volumes = []        # the TemperatureTrigger children


def walk(pid, parentM):
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree()
    name = tt.get("m_Name", "") or ""
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    if name in ("Burning", "Warm"):
        # radius = localScale.x, exactly as TemperatureTrigger reads it
        volumes.append(dict(kind=name, radius=float(trt["m_LocalScale"]["x"]),
                            pos=[round(M[i, 3], 4) for i in range(3)]))
    if name.lower() not in ("dead", "ragdoll", "effect", "nav", "block", "trap"):
        mf = comp_of(tt, ("MeshFilter",))
        nodes[pid] = (M, mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None,
                      name, comp_of(tt, ("MeshRenderer", "SkinnedMeshRenderer")))
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)


rt = comp_of(prefab.read_typetree(), ("Transform",)).read_typetree()
walk(prefab.path_id, np.linalg.inv(trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])))

named = [(p, n) for p, n in nodes.items() if n[1]]
pick = [p for p, n in named if n[2].lower() == "model_0"] or [p for p, n in named]
print("meshes:", [nodes[p][2] for p, _ in named], "-> picked:", [nodes[p][2] for p in pick])

Vs, Ns, Ts, Fs, used = [], [], [], [], []
for gp in pick:
    M, mp, nm, _ = nodes[gp]
    if not mp or mp not in by_id: continue
    M = M.copy(); M[0, 3] = -M[0, 3]        # same X convention the other deployable rips use
    used.append(nm)
    Rn = np.linalg.inv(M[:3, :3]).T
    vb, tb, nb = len(Vs), len(Ts), len(Ns)
    for line in by_id[mp].read().export().splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); Vs.append((w[0], w[1], w[2]))
        elif p[0] == "vn":
            n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
            L = float(np.linalg.norm(n)); Ns.append(tuple(n / L if L > 0 else n))
        elif p[0] == "vt": Ts.append((p[1], p[2]))
        elif p[0] == "f":
            s = "f"
            for tok in p[1:]:
                q = tok.split("/")
                s += " %d" % (int(q[0]) + vb)
                if len(q) > 1 and q[1]: s += "/%d" % (int(q[1]) + tb)
                if len(q) > 2 and q[2]: s += ("/" if len(q) > 1 and q[1] else "//") + "%d" % (int(q[2]) + nb)
            Fs.append(s)

if not Vs:
    print("NO GEOMETRY"); sys.exit(1)
lines = ["v %.6f %.6f %.6f" % v for v in Vs] + ["vt %s %s" % t for t in Ts]
lines += ["vn %.6f %.6f %.6f" % n for n in Ns] + Fs
open(os.path.join(OUT, TARGET + ".obj"), "w").write("\n".join(lines) + "\n")
a = np.array(Vs)
print("wrote %s.obj parts=%s verts=%d bbox X %.2f..%.2f Y %.2f..%.2f Z %.2f..%.2f"
      % (TARGET, used, len(Vs), a[:, 0].min(), a[:, 0].max(), a[:, 1].min(), a[:, 1].max(),
         a[:, 2].min(), a[:, 2].max()))

# albedo: the picked renderers' own _MainTex, largest among THOSE. Never a texture we merely reached.
best = None
for gp in pick:
    mr = nodes[gp][3]
    if mr is None: continue
    for matref in mr.read_typetree().get("m_Materials", []):
        mat = by_id.get(matref.get("m_PathID"))
        if not mat: continue
        for entry in mat.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
            if isinstance(entry, (list, tuple)) and len(entry) == 2: nm_, e_ = entry
            elif isinstance(entry, dict): nm_, e_ = entry.get("first"), entry.get("second")
            else: continue
            if nm_ != "_MainTex" or not isinstance(e_, dict): continue
            tex = by_id.get((e_.get("m_Texture") or {}).get("m_PathID"))
            if tex and tex.type.name == "Texture2D":
                img = tex.read().image
                if not best or img.width * img.height > best[0]:
                    best = (img.width * img.height, img, mat.read_typetree().get("m_Name"))
if best:
    best[1].save(os.path.join(OUT, TARGET + "_tex.png"))
    print(f"wrote {TARGET}_tex.png {best[1].width}x{best[1].height} from material {best[2]}")
else:
    print("no _MainTex on the picked renderers -- flat colour")

if volumes:
    open(os.path.join(OUT, TARGET + "_heat.json"), "w").write(json.dumps(volumes, indent=1))
    for v in volumes:
        print(f"  heat volume {v['kind']:8s} radius {v['radius']:.2f} m at {v['pos']}")
else:
    print("  no Burning/Warm volumes on this barricade")
