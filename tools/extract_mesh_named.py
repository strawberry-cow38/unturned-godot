"""extract_mesh_named.py <MeshName> <OUT.obj> -- rip ONE named Mesh out of core.masterbundle.

The per-item extractors (extract_consumable.py et al) go in through an ITEM prefab and take its Model_0.
This one takes a mesh by NAME, for geometry that lives inside another item's prefab rather than being an
item of its own -- e.g. Stew_Spoon_0, the spoon that only exists as part of the canned-stew eating
animation. Same viewmodel convention as the rest: negate X and Z, reverse the winding.
"""
import UnityPy, os, sys
ROOT = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
name, out = sys.argv[1], sys.argv[2]
env = UnityPy.load(os.path.join(ROOT, "core.masterbundle"))
mesh = None
for o in env.objects:
    if o.type.name != "Mesh": continue
    try: d = o.read()
    except Exception: continue
    if d.m_Name == name: mesh = d; break
if mesh is None: print("NOT FOUND:", name); sys.exit(1)
txt = mesh.export()
Vs, Ns, Ts, Fs = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p: continue
    if p[0] == "v":    Vs.append((-float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vn": Ns.append((-float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vt": Ts.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), int(q[1]) if len(q) > 1 and q[1] else None, int(q[2]) if len(q) > 2 and q[2] else None))
        Fs.append(list(reversed(idx)))
L = [f"# {name} rip -> Godot (X+Z negated, winding reversed)"]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
    L.append(s)
open(out, "w").write("\n".join(L) + "\n")
xs=[v[0] for v in Vs]; ys=[v[1] for v in Vs]; zs=[v[2] for v in Vs]
print(f"wrote {out}: {len(Vs)} verts, {len(Fs)} tris, "
      f"size {max(xs)-min(xs):.3f} x {max(ys)-min(ys):.3f} x {max(zs)-min(zs):.3f} m")
