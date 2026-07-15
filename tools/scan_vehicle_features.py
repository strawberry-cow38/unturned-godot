#!/usr/bin/env python3
"""scan_vehicle_features.py -- audit drivable vehicle.prefabs: headlight lens SHAPE (round vs square),
paintable body meshes, wheel/seat counts, light nodes. Ground truth for the port (strawberry: which PEI
vehicles have circle headlights + anything else the port missed).

Usage: python scan_vehicle_features.py <name> [<name> ...]   (name = the vehicles/<name>/ dir)
Headlight shape heuristic: rip the "Headlights" lens mesh, split L/R by X sign, and for one lens measure
(a) vert count and (b) angular coverage of verts around the centroid in the forward-facing plane.
A disc lens = many verts spread evenly around 360deg -> ROUND. A quad/box lens = ~4 corner clusters -> SQUARE.
"""
import UnityPy, numpy as np, sys, os, math
_B = os.environ.get("UG_MASTERBUNDLE") or os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")
env = UnityPy.load(_B); by_id = {o.path_id: o for o in env.objects}

def comps(tt, names):
    out = []
    for c in tt.get("m_Component", []):
        cc = c.get("component", c) if isinstance(c, dict) else c
        co = by_id.get(cc.get("m_PathID") if isinstance(cc, dict) else None)
        if co and co.type.name in names: out.append(co)
    return out

def trs(pos, q, s):
    x, y, z, w = q["x"], q["y"], q["z"], q["w"]
    R = np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                  [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                  [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])
    M = np.eye(4); M[:3, :3] = R @ np.diag([s["x"], s["y"], s["z"]]); M[:3, 3] = [pos["x"], pos["y"], pos["z"]]
    return M

def mesh_verts(mo, M):
    """world verts (root-cancelled, Z-flip) of mesh object mo under transform M."""
    R = M[:3, :3]; V = []
    for line in mo.read().export().splitlines():
        p = line.split()
        if p and p[0] == "v":
            w = R @ np.array([float(p[1]), float(p[2]), float(p[3])])
            V.append((w[0], w[1], -w[2]))
    return np.array(V) if V else np.zeros((0, 3))

def _hull(pts):
    """Andrew's monotone chain convex hull of 2D points -> ordered hull verts."""
    pts = sorted(set(map(tuple, np.round(pts, 5))))
    if len(pts) < 3: return pts
    def cross(o, a, b): return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])
    lo = []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0: lo.pop()
        lo.append(p)
    up = []
    for p in reversed(pts):
        while len(up) >= 2 and cross(up[-2], up[-1], p) <= 0: up.pop()
        up.append(p)
    return lo[:-1] + up[:-1]

def classify_lens(V):
    """V = verts of ONE headlight lens. circularity 4*pi*A/P^2: circle=1.0, square~0.79, rect<0.7."""
    if len(V) < 3: return ("?", len(V), 0.0)
    P = V - V.mean(axis=0)
    _, _, Vt = np.linalg.svd(P, full_matrices=False)   # least-variance axis = the lens facing normal
    proj = P @ Vt[:2].T                                 # project onto the lens face plane
    H = _hull(proj)
    if len(H) < 3: return ("?", len(V), 0.0)
    area = abs(sum(H[i][0]*H[(i+1) % len(H)][1] - H[(i+1) % len(H)][0]*H[i][1] for i in range(len(H)))) / 2
    per = sum(math.dist(H[i], H[(i+1) % len(H)]) for i in range(len(H)))
    circ = 4 * math.pi * area / (per * per) if per else 0
    shape = "ROUND" if circ >= 0.85 else ("square" if circ >= 0.74 else "rect/other")
    return (shape, len(V), round(circ, 2))

def scan(name):
    sub = f"vehicles/{name}/vehicle.prefab"
    try:
        prefab = next(o for p, o in env.container.items() if p.lower().endswith(sub) and o.type.name == "GameObject")
    except StopIteration:
        print(f"\n### {name}: NO vehicle.prefab"); return
    root_trt = comps(prefab.read_typetree(), ("Transform", "RectTransform"))[0].read_typetree()
    root_inv = np.linalg.inv(trs(root_trt["m_LocalPosition"], root_trt["m_LocalRotation"], root_trt["m_LocalScale"]))
    meshes = {}         # name -> [(verts, M)]
    wheels = seats = 0; hl_nodes = tl_nodes = 0; steer = False
    def walk(pid, parentM, pname=""):
        nonlocal wheels, seats, hl_nodes, tl_nodes, steer
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree(); nm = tt.get("m_Name", "?")
        tr = comps(tt, ("Transform", "RectTransform"))
        if not tr: return
        trt = tr[0].read_typetree(); M = parentM @ trs(trt["m_LocalPosition"], trt["m_LocalRotation"], trt["m_LocalScale"])
        if comps(tt, ("WheelCollider",)): wheels += 1
        if nm.startswith("Steer"): steer = True
        if pname == "Headlights" and nm == "Lamp": hl_nodes += 1
        if pname == "Taillights" and nm == "Lamp": tl_nodes += 1
        if pname == "Seats" and nm.startswith("Seat"): seats += 1
        mf = comps(tt, ("MeshFilter", "SkinnedMeshRenderer"))
        if mf:
            mp = mf[0].read_typetree().get("m_Mesh", {}).get("m_PathID")
            if mp in by_id:
                mn = by_id[mp].read_typetree().get("m_Name", "?")
                meshes.setdefault(mn, []).append((by_id[mp], M, nm))
        for ch in trt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"), M, nm)
    walk(prefab.path_id, root_inv)
    # body paint meshes = Model_0/Model_1 (top-level, biggest)
    def vcount(mo): return mo.read_typetree().get("m_VertexData", {}).get("m_VertexCount", 0)
    body = []
    for mn in ("Model_0", "Model_1"):
        if mn in meshes:
            vc = max(vcount(t[0]) for t in meshes[mn])
            body.append(f"{mn}({vc}v)")
    # headlight shape
    hl = "none"
    if "Headlights" in meshes:
        mo, M, _ = meshes["Headlights"][0]
        V = mesh_verts(mo, M)
        if len(V):
            xs = V[:, 0]; L = V[xs < np.median(xs)]; Rr = V[xs >= np.median(xs)]
            lens = L if len(L) >= len(Rr) else Rr
            shape, nv, cov = classify_lens(lens)
            hl = f"{shape} (lens {nv}v, {cov}% arc; mesh {len(V)}v)"
    tl = f"{len(mesh_verts(*meshes['Taillights'][0][:2]))}v" if "Taillights" in meshes else "none"
    print(f"\n### {name}")
    print(f"  body paint meshes: {', '.join(body) or 'NONE(!)'}")
    print(f"  headlight lens shape: {hl}")
    print(f"  taillight mesh: {tl} | headlight lamps: {hl_nodes} | taillight lamps: {tl_nodes}")
    print(f"  wheels: {wheels} | seats: {seats} | steer mesh: {steer}")

for n in sys.argv[1:]:
    scan(n)
