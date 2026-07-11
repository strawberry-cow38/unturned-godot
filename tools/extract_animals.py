import UnityPy, os, re, numpy as np
BUND = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
OUT = r"C:\claude-workspace\unturned-godot\game\content\objects"
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
pidmap = {}
for o in env.objects:
    pidmap[o.path_id] = o
def cl(go): return getattr(go, "m_Components", None) or getattr(go, "m_Component", [])
def rd(c):
    cp = c.component if hasattr(c, "component") else c; return cp.read()
def tn(co):
    try: return co.object_reader.type.name
    except: return type(co).__name__
def gtf(go):
    for c in cl(go):
        co = rd(c)
        if tn(co) in ("Transform", "RectTransform"): return co
    return None
def vget(v): return (v.X, v.Y, v.Z) if hasattr(v, "X") else (v.x, v.y, v.z)
def qget(q): return (q.X, q.Y, q.Z, q.W) if hasattr(q, "X") else (q.x, q.y, q.z, q.w)
def quat_mat(q):
    x, y, z, w = q
    return np.array([[1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y)],
                     [2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x)],
                     [2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y)]])
def local_mat(t):
    p = vget(t.m_LocalPosition); q = qget(t.m_LocalRotation); s = vget(t.m_LocalScale)
    M = np.eye(4); M[:3, :3] = quat_mat(q) * np.array(s); M[:3, 3] = p
    return M
def rpid(go):
    for c in cl(go):
        try: co = rd(c)
        except: continue
        if tn(co) in ("MeshRenderer", "SkinnedMeshRenderer"):
            for mp in getattr(co, "m_Materials", []):
                try:
                    mat = mp.read()
                    for it in mat.m_SavedProperties.m_TexEnvs:
                        nm2 = it[0] if isinstance(it, (list, tuple)) else getattr(it, "first", None)
                        e2 = it[1] if isinstance(it, (list, tuple)) else getattr(it, "second", it)
                        if nm2 == "_MainTex":
                            pid = getattr(e2.m_Texture, "m_PathID", 0)
                            if pid: return pid
                except Exception: pass
    return 0
def collect(t, acc, out, depth=0):
    if t is None or depth > 9: return
    try: go = t.m_GameObject.read()
    except: return
    for c in cl(go):
        try: co = rd(c)
        except: continue
        if tn(co) in ("MeshFilter", "SkinnedMeshRenderer"):   # animals are SkinnedMeshRenderer (rest/bind-pose mesh)
            try: out.append((go.m_Name or "", co.m_Mesh.read(), acc, rpid(go)))
            except: pass
    for ch in getattr(t, "m_Children", []):
        try:
            cht = ch.read(); collect(cht, acc @ local_mat(cht), out, depth + 1)
        except Exception: pass
def bake(txt, acc):
    R = acc[:3, :3]; out = []
    for ln in txt.splitlines():
        if ln.startswith("v "):
            q = ln.split(); v = acc @ np.array([float(q[1]), float(q[2]), float(q[3]), 1.0])
            out.append("v %.6f %.6f %.6f" % (v[0], v[1], v[2]))
        elif ln.startswith("vn "):
            q = ln.split(); nn = R @ np.array([float(q[1]), float(q[2]), float(q[3])]); L = float((nn[0]**2+nn[1]**2+nn[2]**2)**0.5)
            out.append("vn %.6f %.6f %.6f" % tuple(nn/L if L > 0 else nn))
        else: out.append(ln)
    return "\n".join(out)
def combine(parts):
    outv = []; outvt = []; outvn = []; outf = []; ov = ovt = ovn = 0
    for txt in parts:
        nv = nvt = nvn = 0
        for ln in txt.splitlines():
            if ln.startswith("v "): outv.append(ln); nv += 1
            elif ln.startswith("vt "): outvt.append(ln); nvt += 1
            elif ln.startswith("vn "): outvn.append(ln); nvn += 1
            elif ln.startswith("f "):
                nf = []
                for tk in ln.split()[1:]:
                    a = tk.split("/")
                    a[0] = str(int(a[0]) + ov)
                    if len(a) > 1 and a[1]: a[1] = str(int(a[1]) + ovt)
                    if len(a) > 2 and a[2]: a[2] = str(int(a[2]) + ovn)
                    nf.append("/".join(a))
                outf.append("f " + " ".join(nf))
        ov += nv; ovt += nvt; ovn += nvn
    return "\n".join(outv + outvt + outvn + outf) + "\n"
ANIMALS = {"Animal_Deer": "animals/deer/animal_client.prefab",
           "Animal_Pig":  "animals/pig/animal_client.prefab",
           "Animal_Cow":  "animals/cow/animal_client.prefab"}
for name, sub in ANIMALS.items():
    obj = None
    for path, o in env.container.items():
        if path.lower().endswith(sub) and o.type.name == "GameObject":
            obj = o; break
    if not obj:
        print("NO PREFAB", name); continue
    meshes = []; collect(gtf(obj.read()), np.eye(4), meshes)
    print("  ", name, "ALL meshes:", [t[0] for t in meshes])
    keep = [(m, acc, pid) for gn, m, acc, pid in meshes if not re.search(r"(lod[123]|nav|collision|ragdoll)", gn.lower())]
    if not keep: keep = [(m, acc, pid) for gn, m, acc, pid in meshes]
    parts = []
    for m, acc, pid in keep:
        try: parts.append(bake(m.export(), acc))
        except Exception as e: print("export fail", name, e)
    if not parts:
        print("NO EXPORT", name); continue
    open(os.path.join(OUT, name + ".obj"), "w").write(combine(parts))
    texpid = next((pid for m, acc, pid in keep if pid), 0)
    to = pidmap.get(texpid)
    if to:
        try: to.read().image.convert("RGBA").save(os.path.join(OUT, name + "_tex.png"))
        except Exception as e: print("tex fail", name, e)
    # report the mesh AABB so I can see orientation/scale before rendering
    import numpy as _np
    vs = [[float(x) for x in ln.split()[1:4]] for p in parts for ln in p.splitlines() if ln.startswith("v ")]
    if vs:
        a = _np.array(vs); mn = a.min(0); mx = a.max(0)
        print("OK %-14s kept=%d tex=%s  AABB size=(%.2f,%.2f,%.2f)" % (name, len(keep), bool(to), mx[0]-mn[0], mx[1]-mn[1], mx[2]-mn[2]))
