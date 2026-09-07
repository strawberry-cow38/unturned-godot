#!/usr/bin/env python3
"""The level-crossing BOOM ARM. Crossing_1/_2's object.prefab carries a SkinnedMeshRenderer named Hinge_0 at
(0,0,2.5) up the post -- 44 verts, the barrier beam -- and the port's prop rip only ever took Model_0 (the 20-vert
post), so the crossing has no arm (master 2026-09-07: "the crossing gate is missing its arm/beam thing").

Verifies the axis convention against the SHIPPED Crossing_1.obj before trusting it on the arm, the same way the
magazine mirror was settled -- extract Model_0, compare, and only then write the arm out the same way.
"""
import UnityPy, numpy as np, os, sys
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CONTENT = r"C:\claude-workspace\unturned-godot\game\content\objects"
env = UnityPy.load(MB); by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None
def trs(pos,q,s):
    x,y,z,w=q["x"],q["y"],q["z"],q["w"]
    R=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],[2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],[2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
    M=np.eye(4); M[:3,:3]=R@np.diag([s["x"],s["y"],s["z"]]); M[:3,3]=[pos["x"],pos["y"],pos["z"]]; return M

def grab(prop, node):
    p = next((o for pa,o in env.container.items() if pa.lower().endswith("signs/%s/object.prefab"%prop) and o.type.name=="GameObject"), None)
    if not p: return None
    out = {}
    def walk(pid, PM):
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree(); nm = tt.get("m_Name","")
        tr = comp_of(tt, ("Transform",))
        if not tr: return
        trt = tr.read_typetree(); M = PM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        mc = comp_of(tt, ("MeshFilter","SkinnedMeshRenderer"))
        if mc and nm == node:
            mp = mc.read_typetree().get("m_Mesh", {}).get("m_PathID")
            if mp in by_id: out["m"] = (by_id[mp], M)
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M)
    rt = comp_of(p.read_typetree(), ("Transform",))
    rl = trs(*[rt.read_typetree()[k] for k in ("m_LocalPosition","m_LocalRotation","m_LocalScale")])
    walk(p.path_id, np.linalg.inv(rl))
    return out.get("m")

def verts_of(mo, M, sx, sz):
    V, T, F = [], [], []
    for line in mo.read().export().splitlines():
        q = line.split()
        if not q: continue
        if q[0] == "v":
            w = M @ np.array([float(q[1]), float(q[2]), float(q[3]), 1.0])
            V.append((sx*w[0], w[1], sz*w[2]))
        elif q[0] == "vt": T.append((float(q[1]), float(q[2])))
        elif q[0] == "f":
            idx = []
            for tok in q[1:]:
                a = tok.split("/")
                idx.append((int(a[0]), int(a[1]) if len(a) > 1 and a[1] else None))
            F.append(idx)
    return V, T, F

# --- 1) settle the convention on the KNOWN-GOOD post ---------------------------------------------
got = grab("crossing_1", "Model_0")
if not got: print("Model_0 not found"); sys.exit(1)
ship = [tuple(map(float,l.split()[1:4])) for l in open(os.path.join(CONTENT,"Crossing_1.obj")) if l.startswith("v ")]
bb = lambda A: [(round(min(c[i] for c in A),4), round(max(c[i] for c in A),4)) for i in range(3)]
SX = SZ = None
for sx in (1,-1):
    for sz in (1,-1):
        V,_,_ = verts_of(got[0], got[1], sx, sz)
        if bb(V) == bb(ship): SX, SZ = sx, sz
        print("  x*%+d z*%+d -> %s" % (sx, sz, "MATCH" if bb(V)==bb(ship) else "no"))
if SX is None: print("NO convention matches the shipped post -- stopping"); sys.exit(1)
print("prop convention: x*%+d, y, z*%+d  (guns use -x,y,-z; props are NOT the same)" % (SX, SZ))

# --- 2) the arm, written the same way -------------------------------------------------------------
for prop in ("crossing_1", "crossing_2"):
    g = grab(prop, "Hinge_0")
    if not g: print(prop, "no Hinge_0"); continue
    # ⚠ IDENTITY, not the node transform. Hinge_0 is a SkinnedMeshRenderer and its verts are already in BIND
    # (root-relative) space -- raw they span z 1.11..1.55, i.e. sitting on top of the 1.5 m post exactly where a
    # boom belongs. Baking the renderer node's own (0,-2.5,0) on top moved the beam to ground level and turned it.
    V, T, F = verts_of(g[0], np.eye(4), SX, SZ)
    out = os.path.join(CONTENT, "%s_Hinge_0_door.obj" % prop.capitalize().replace("crossing","Crossing"))
    out = os.path.join(CONTENT, "Crossing_%s_Hinge_0_door.obj" % prop.split("_")[1])
    with open(out, "w") as fh:
        fh.write("# Crossing boom arm -- SkinnedMeshRenderer 'Hinge_0' from %s/object.prefab (tools/extract_crossing_arm.py)\n" % prop)
        for v in V: fh.write("v %.6f %.6f %.6f\n" % v)
        for t in T: fh.write("vt %.6f %.6f\n" % t)
        for f in F: fh.write("f " + " ".join(("%d/%d"%(a,b)) if b else str(a) for a,b in f) + "\n")
    mn=[min(c[i] for c in V) for i in range(3)]; mx=[max(c[i] for c in V) for i in range(3)]
    print("  %s  %d verts  size %.2f x %.2f x %.2f  z[%.2f,%.2f]" % (os.path.basename(out), len(V), mx[0]-mn[0], mx[1]-mn[1], mx[2]-mn[2], mn[2], mx[2]))
