"""Extract the airdrop plane (core.masterbundle Level/Dropship.prefab) as its real parts.

The generic extract_object_named path is wrong for this prefab in two ways, and both are
invisible until you look at the render:

  * it merges every MeshFilter in the hierarchy into ONE obj, which welds the four spinning
    rotor blades and the three nav lights into the hull. They stop being separate things that
    can move or glow.
  * it then picks "the biggest Texture2D reachable from the prefab's materials" as the albedo.
    The hull material's _MainTex is a 2x2 grey placeholder, so that heuristic walks past it and
    lands on whatever else is nearby -- it handed back the 1024 `Airplane` sheet, which belongs
    to skins/bluntforce/dogfighter/skin_primary.mat. That is a GUN SKIN. Shark teeth and USAAF
    roundels, smeared over an aircraft that in retail has no albedo at all.

So the parts come out separately here:

  Dropship.obj        Model_0, the hull, in prefab-root space
  Dropship_rotor.obj  Model_1, ONE blade, in its own local space so it can be instanced and spun
  Dropship.json       rotor + taillight transforms, and the material colours read off the prefab

Nothing is guessed: every colour below is read from the prefab's materials, and the hull's
"texture" really is four grey pixels.
"""
import UnityPy, os, sys, json, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = ug_paths.objects_out()
CONT = "assets/coremasterbundle/level/dropship.prefab"

env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
prefab = None
for path, obj in env.container.items():
    if obj.type.name == "GameObject" and path.lower() == CONT:
        prefab = obj
        break
if prefab is None:
    print("Dropship.prefab not in core.masterbundle"); sys.exit(1)


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


nodes = {}   # name -> (world-ish matrix relative to prefab root, mesh path_id, material path_id)


def walk(pid, parentM):
    go = by_id.get(pid)
    if not go: return
    tt = go.read_typetree()
    tr = comp_of(tt, ("Transform", "RectTransform"))
    if not tr: return
    trt = tr.read_typetree()
    M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
    mf = comp_of(tt, ("MeshFilter",))
    mr = comp_of(tt, ("MeshRenderer",))
    mesh = mf.read_typetree().get("m_Mesh", {}).get("m_PathID") if mf else None
    mat = None
    if mr:
        mats = mr.read_typetree().get("m_Materials", [])
        if mats: mat = mats[0].get("m_PathID")
    nodes[tt.get("m_Name", "")] = (M, mesh, mat, trt)
    for ch in trt.get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)


rt = comp_of(prefab.read_typetree(), ("Transform",)).read_typetree()
walk(prefab.path_id, np.linalg.inv(trs(rt["m_LocalPosition"], rt["m_LocalRotation"], rt["m_LocalScale"])))
print("nodes:", ", ".join(sorted(nodes)))


def write_obj(name, mesh_pid, M):
    """Bake mesh into M's space and write a .obj. M=identity keeps the mesh's own local frame."""
    txt = by_id[mesh_pid].read().export()
    Rn = np.linalg.inv(M[:3, :3]).T
    V, T, N, F = [], [], [], []
    for line in txt.splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v":
            w = M @ np.array([float(p[1]), float(p[2]), float(p[3]), 1.0]); V.append((w[0], w[1], w[2]))
        elif p[0] == "vn":
            n = Rn @ np.array([float(p[1]), float(p[2]), float(p[3])])
            L = float(np.linalg.norm(n)); N.append(tuple(n / L if L > 0 else n))
        elif p[0] == "vt": T.append((p[1], p[2]))
        elif p[0] == "f": F.append(line)
    L = ["v %.6f %.6f %.6f" % v for v in V] + ["vt %s %s" % t for t in T]
    L += ["vn %.6f %.6f %.6f" % n for n in N] + F
    open(os.path.join(OUT, name), "w").write("\n".join(L) + "\n")
    a = np.array(V)
    print("wrote %s verts=%d tris=%d bbox X %.2f..%.2f Y %.2f..%.2f Z %.2f..%.2f"
          % (name, len(V), len(F), a[:, 0].min(), a[:, 0].max(), a[:, 1].min(), a[:, 1].max(),
             a[:, 2].min(), a[:, 2].max()))
    return dict(verts=len(V), tris=len(F))


def mat_color(pid, prop="_Color"):
    m = by_id.get(pid)
    if not m: return None
    for entry in m.read_typetree().get("m_SavedProperties", {}).get("m_Colors", []):
        if isinstance(entry, (list, tuple)) and len(entry) == 2: n_, v_ = entry
        elif isinstance(entry, dict): n_, v_ = entry.get("first"), entry.get("second")
        else: continue
        if n_ == prop and isinstance(v_, dict):
            return [v_["r"], v_["g"], v_["b"], v_.get("a", 1.0)]
    return None


def main_tex_average(pid):
    """The hull's albedo is four grey pixels. Average them rather than shipping a 2x2 png."""
    m = by_id.get(pid)
    if not m: return None
    for entry in m.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
        if isinstance(entry, (list, tuple)) and len(entry) == 2: n_, e_ = entry
        elif isinstance(entry, dict): n_, e_ = entry.get("first"), entry.get("second")
        else: continue
        if n_ != "_MainTex" or not isinstance(e_, dict): continue
        t = by_id.get((e_.get("m_Texture") or {}).get("m_PathID"))
        if not t: return None
        img = t.read().image.convert("RGB")
        px = list(img.getdata())
        avg = [sum(c[i] for c in px) / len(px) / 255.0 for i in range(3)]
        return dict(size=[img.width, img.height], srgb=[round(c, 5) for c in avg],
                    pixels=[list(c) for c in px] if img.width * img.height <= 16 else None)
    return None


man = {"source": CONT}

hullM, hullMesh, hullMat, _ = nodes["Model_0"]
man["hull"] = write_obj("Dropship.obj", hullMesh, hullM)
man["hull"]["albedo"] = main_tex_average(hullMat)
man["hull"]["color"] = mat_color(hullMat)

# One blade, in its own frame: the four Rotor_* nodes all point at the SAME mesh, so instancing
# it is what the prefab already does -- baking four copies into the hull would freeze them.
rotor_nodes = sorted(n for n in nodes if n.startswith("Rotor_"))
_, rotorMesh, rotorMat, _ = nodes[rotor_nodes[0]]
man["rotor"] = write_obj("Dropship_rotor.obj", rotorMesh, np.eye(4))
man["rotor"]["color"] = mat_color(rotorMat)
man["rotor"]["instances"] = []
for n in rotor_nodes:
    M, mesh, _, trt = nodes[n]
    assert mesh == rotorMesh, f"{n} uses a different mesh"
    # The basis is emitted as raw numbers rather than a quaternion on purpose: the consumer keeps
    # Unity's coordinates verbatim (ObjMesh CONV=1), and a 3x3 carries no convention to get wrong.
    man["rotor"]["instances"].append(dict(
        name=n,
        pos=[round(M[i, 3], 6) for i in range(3)],
        basis=[[round(M[r, c], 6) for c in range(3)] for r in range(3)],
        rot=[trt["m_LocalRotation"][k] for k in "xyzw"],
        scale=[trt["m_LocalScale"][k] for k in "xyz"]))

man["lights"] = []
for n in sorted(x for x in nodes if x.startswith("Taillight_")):
    M, mesh, mat, _ = nodes[n]
    e = write_obj(n.replace("Taillight", "Dropship_light") + ".obj", mesh, np.eye(4)) if mesh else {}
    e.update(name=n, pos=[M[0, 3], M[1, 3], M[2, 3]],
             color=mat_color(mat), emission=mat_color(mat, "_EmissionColor"))
    man["lights"].append(e)

open(os.path.join(OUT, "Dropship.json"), "w").write(json.dumps(man, indent=1))
print("wrote Dropship.json")
