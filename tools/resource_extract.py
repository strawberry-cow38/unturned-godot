import UnityPy, os, struct, uuid, collections
import numpy as np
BUND  = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
TREES = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Terrain\Trees.dat"
OUT   = r"C:\claude-workspace\unturned-godot\game\content\resources"
os.makedirs(OUT, exist_ok=True)

GUID2NAME = {
 "c25f4502b6484b52a867254fb8124436":"Birch_1","70ae7c7e858d4b91b750886e490c3db0":"Maple_1",
 "63cb368c94b14000aabc5325b048cfa3":"Maple_0","20236e1b231540a8a18c3793eb8b4513":"Birch_0",
 "c6ea0e0c72da44c3874e95e8f867e330":"Bush_1","df28240a7c894b18ba867ee9cd188c42":"Bush_0",
 "759d51c460a84c299d04cf10aa9e31d8":"Pine_0","0feb9a5e6aab4075bca6250778da8f27":"Pine_1",
 "57c15ead4dce46b1ae5503fed72fbc8d":"Cane_00","1fe1c4c68878495db67dc64b54c705bf":"Metal_2",
 "9de57b59a51642b2919e0f8377a8da82":"Mushroom_Red_0","6d205c5ff4ad451088bfb08181b38982":"Mushroom_Brown_0",
 "00ca63d659c14c1eba1a8daf990ade6b":"Snow_Pile_00","3626e5d3a2164322b181b50093775be7":"Bush_Mauve",
 "00dc814316434b3786b8404debe5e4d0":"Bush_Indigo","4572d94b478245a193db823903c843d2":"Bush_Jade",
 "cefa0db922fd4dd48fdae0cf148ae088":"Bush_Amber","a52b48e1d14442e29270e7938a80165b":"Bush_Russet",
 "8b13c5683de049ff88110c7a06390db8":"Bush_Teal","7a144f4e5b674c3487a10d1343b307a7":"Bush_Vermillion",
 "d43dce6f901c44fbaa63fd332848793a":"Bush_Hanu","78bc337548f742c8ab3da7cea55a3e25":"Clay_1",
 "4def378848484654ac8ac97560d59fd2":"Clay_0","40ce1b8f427d4188930df302423f6d1d":"Clay_2",
 "58ff3047e8ec4ccbbc2352faf529acef":"Clay_4","74192f26950545d8aabc0e84a2372f9e":"Clay_3",
 "f0707c1712804e6fbe1a7d925cb33ca4":"Ornament_0_XMAS",
}
env = UnityPy.load(BUND)
by_id = {o.path_id: o for o in env.objects}
cont  = {p.lower(): o for p, o in env.container.items()}
def tt(o): return o.read_typetree()
def comp_pids(go_tt):
    for c in go_tt.get("m_Component", []):
        d = c.get("component", c) if isinstance(c, dict) else c
        yield d.get("m_PathID")
def get_transform(go_tt):
    for pid in comp_pids(go_tt):
        o = by_id.get(pid)
        if o and o.type.name in ("Transform", "RectTransform"): return o
    return None
def go_of(tr_tt): return by_id.get(tr_tt["m_GameObject"]["m_PathID"])
def trs(tr_tt):
    t = tr_tt["m_LocalPosition"]; r = tr_tt["m_LocalRotation"]; s = tr_tt["m_LocalScale"]
    T = np.eye(4); T[0,3], T[1,3], T[2,3] = t["x"], t["y"], t["z"]
    x, y, z, w = r["x"], r["y"], r["z"], r["w"]
    R = np.eye(4)
    R[0,0]=1-2*(y*y+z*z); R[0,1]=2*(x*y-z*w);   R[0,2]=2*(x*z+y*w)
    R[1,0]=2*(x*y+z*w);   R[1,1]=1-2*(x*x+z*z); R[1,2]=2*(y*z-x*w)
    R[2,0]=2*(x*z-y*w);   R[2,1]=2*(y*z+x*w);   R[2,2]=1-2*(x*x+y*y)
    return T @ R @ np.diag([s["x"], s["y"], s["z"], 1.0])
def find_child(tr_tt, name):
    for c in tr_tt.get("m_Children", []):
        ch = by_id.get(c["m_PathID"])
        if ch and tt(go_of(tt(ch))).get("m_Name") == name: return ch
    return None
def renderer_tex(go_tt):
    for pid in comp_pids(go_tt):
        o = by_id.get(pid)
        if o and o.type.name == "MeshRenderer":
            for mt in tt(o).get("m_Materials", []):
                mo = by_id.get(mt.get("m_PathID"))
                if mo and mo.type.name == "Material":
                    for e in tt(mo).get("m_SavedProperties", {}).get("m_TexEnvs", []):
                        nm = e[0] if isinstance(e, (list, tuple)) else e.get("first")
                        te = e[1] if isinstance(e, (list, tuple)) else e.get("second")
                        if nm == "_MainTex":
                            tp = (te.get("m_Texture") or {}).get("m_PathID")
                            to = by_id.get(tp)
                            if to and to.type.name == "Texture2D": return to
    return None
def collect(tr, M, out):
    trtt = tt(tr); Ml = M @ trs(trtt); go = go_of(trtt); gott = tt(go)
    for pid in comp_pids(gott):
        o = by_id.get(pid)
        if o and o.type.name == "MeshFilter":
            mo = by_id.get(tt(o).get("m_Mesh", {}).get("m_PathID"))
            if mo and mo.type.name == "Mesh":
                out.append((mo, Ml, renderer_tex(gott)))
    for c in trtt.get("m_Children", []):
        ch = by_id.get(c["m_PathID"])
        if ch: collect(ch, Ml, out)
def bake_one(mesh, M, path):
    s = mesh.read().export(); V, VT, VN, F = [], [], [], []
    R = M[:3, :3]   # rotation(+uniform scale); for normals we renormalize after, so this is correct for tree TRS
    for ln in s.splitlines():
        t = ln.split()
        if not t: continue
        if t[0] == "v":
            w = M @ np.array([float(t[1]), float(t[2]), float(t[3]), 1.0]); V.append((w[0], w[1], w[2]))
        elif t[0] == "vt": VT.append((t[1], t[2]))
        elif t[0] == "vn":
            n = R @ np.array([float(t[1]), float(t[2]), float(t[3])])
            L = (n[0]*n[0] + n[1]*n[1] + n[2]*n[2]) ** 0.5 or 1.0
            VN.append((n[0]/L, n[1]/L, n[2]/L))
        elif t[0] == "f": F.append(t[1:])
    with open(path, "w") as f:
        for (x, y, z) in V: f.write(f"v {x} {y} {z}\n")
        for (u, v) in VT: f.write(f"vt {u} {v}\n")
        for (a, b, c) in VN: f.write(f"vn {a} {b} {c}\n")   # <-- the missing lines that broke the editor importer
        for face in F: f.write("f " + " ".join(face) + "\n")
    return len(V)

manifest = []
for name in dict.fromkeys(GUID2NAME.values()):
    go = cont.get(f"assets/coremasterbundle/trees/{name.lower()}/resource.prefab")
    if not go:
        print(f"{name}: NO prefab -- skip"); continue
    root = get_transform(tt(go)); m0 = find_child(tt(root), "Model_0")
    parts = []
    if m0: collect(m0, np.eye(4), parts)
    else:
        for c in tt(root).get("m_Children", []):
            ch = by_id.get(c["m_PathID"])
            if ch: collect(ch, np.eye(4), parts)
    np_ = 0
    for i, (mesh, M, tex) in enumerate(parts):
        nv = bake_one(mesh, M, os.path.join(OUT, f"{name}_{i}.obj"))
        if tex is not None:
            try: tex.read().image.save(os.path.join(OUT, f"{name}_{i}_tex.png"))
            except Exception as e: print(f"   {name}_{i} tex err {e}")
        np_ += 1
    manifest.append((name, np_))
    print(f"{name}: {np_} parts")

with open(os.path.join(OUT, "resources.txt"), "w") as f:
    for name, n in manifest: f.write(f"{name} {n}\n")

# instances per resource: 9 floats (pos, euler, scale)
d = open(TREES, "rb").read(); p = [0]
def u8():
    v = d[p[0]]; p[0]+=1; return v
def u16():
    v = struct.unpack_from("<H", d, p[0])[0]; p[0]+=2; return v
def i32():
    v = struct.unpack_from("<i", d, p[0])[0]; p[0]+=4; return v
def f32():
    v = struct.unpack_from("<f", d, p[0])[0]; p[0]+=4; return v
u8(); cnt = i32(); buckets = collections.defaultdict(list)
for _ in range(cnt):
    n = u16(); g = d[p[0]:p[0]+n]; p[0]+=n
    row = tuple(f32() for _ in range(9)); u8()
    nm = GUID2NAME.get(str(uuid.UUID(bytes_le=g)).replace("-", ""))
    if nm: buckets[nm].append(row)
for nm, rows in buckets.items():
    with open(os.path.join(OUT, nm + ".bin"), "wb") as f:
        f.write(struct.pack("<i", len(rows)))
        for r in rows: f.write(struct.pack("<9f", *r))
print("instances:", sum(len(r) for r in buckets.values()), "trees;", len(manifest), "resources with meshes")
