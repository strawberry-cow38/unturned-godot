#!/usr/bin/env python3
"""extract_consumable_equipable.py <Item> [OUTDIR] -- rip a consumable's FIRST-PERSON held model, all of it.

⚠ WHY THIS EXISTS, and why extract_consumable.py is not enough (strawberry 2026-09-10: "the chip bag should
open and we should take handfuls of chips ... various other animations use a fork or spoon when eating"):

  - extract_consumable.py rips `item.prefab`, which is the WORLD/inventory model. Retail HOLDS `equipable.prefab`.
  - and it takes `Model_0` alone. An equipable carries `Model_0..Model_n` PLUS `Bone_0..Bone_n`, and every one of
    those Bone_n is its own mesh + renderer -- the bag flaps a chip bag opens with, the spoon on a canned meal.
    They are NOT skeleton bones; nothing in the file is called "spoon", which is why searching asset names for
    one finds only the Pitchfork melee and concludes, wrongly, that retail has no cutlery.

The Use clips we already ship animate exactly these names (CU_2 runs 7.567 s and drives Bone_0..Bone_4), so the
animation half has always been correct -- we were holding a one-piece world model with none of the parts it moves.

Same Unity->Godot convention as the gun/consumable rips: negate X and Z and reverse winding. That negation is a
180-degree turn about Y, so part ROTATIONS are carried by the quaternion for that turn rather than by negating
components, which is a different (and wrong) transform.

Outputs per item: <name>_<part>.txt for every part, <name>_albedo.png, and <name>_parts.tsv --
    part <TAB> meshfile <TAB> px py pz <TAB> qx qy qz qw <TAB> sx sy sz
in the prefab's own child order, which is the order the clips address them in."""
import UnityPy, sys, os

MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
NAME = sys.argv[1]
OUTDIR = sys.argv[2] if len(sys.argv) > 2 else r"C:\claude-workspace\consumeout"
os.makedirs(OUTDIR, exist_ok=True)
nl = NAME.lower()

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def pptr(v):
    return by_id.get(v.get("m_PathID")) if isinstance(v, dict) else None

def yaw180(q):
    """Map a Unity local rotation into the X/Z-negated Godot frame: conjugation R_g = N R_u N, which is just
    the rotation AXIS taking the same negation a position does. Multiplying by the turn once instead leaves a
    spurious half-turn on every part -- identity came out as (0,1,0,0) -- and mirrors them."""
    qx, qy, qz, qw = q.get("x", 0.0), q.get("y", 0.0), q.get("z", 0.0), q.get("w", 1.0)
    return (-qx, qy, -qz, qw)

def convert_mesh(mesh_obj, label):
    txt = mesh_obj.read().export()
    Vs, Ns, Ts, Fs = [], [], [], []
    for line in txt.splitlines():
        p = line.split()
        if not p:
            continue
        if p[0] == "v":
            Vs.append((-float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vn":
            Ns.append((-float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vt":
            Ts.append((p[1], p[2]))
        elif p[0] == "f":
            idx = []
            for tok in p[1:]:
                q = tok.split("/")
                idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
            Fs.append(list(reversed(idx)))
    L = ["# %s (%s equipable rip -> Godot, X+Z negated + winding reversed)" % (label, NAME)]
    L += ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    return "\n".join(L) + "\n", len(Vs)

prefab = next((o for p, o in env.container.items()
               if p.lower().endswith("/" + nl + "/equipable.prefab") and o.type.name == "GameObject"), None)
if not prefab:
    print("NO equipable.prefab for", NAME); sys.exit(1)

root_tr = comp_of(prefab.read_typetree(), ("Transform",))
rows, albedo_written = [], False
for ch in root_tr.read_typetree().get("m_Children", []):
    ct = by_id.get(ch.get("m_PathID"))
    if not ct:
        continue
    ctt = ct.read_typetree()
    cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
    if not cgo:
        continue
    gtt = cgo.read_typetree()
    part = str(gtt.get("m_Name"))
    mf = comp_of(gtt, ("MeshFilter",))
    mesh = pptr(mf.read_typetree().get("m_Mesh", {})) if mf else None
    if not mesh:
        continue                                    # Icon / Effect / Stat_Tracker carry no geometry
    body, nv = convert_mesh(mesh, part)
    fn = "%s_%s.txt" % (nl, part.lower())
    open(os.path.join(OUTDIR, fn), "w").write(body)
    lp = ctt.get("m_LocalPosition", {})
    ls = ctt.get("m_LocalScale", {})
    qx, qy, qz, qw = yaw180(ctt.get("m_LocalRotation", {}))
    rows.append("%s\t%s\t%.6f %.6f %.6f\t%.6f %.6f %.6f %.6f\t%.6f %.6f %.6f" % (
        part, fn,
        -lp.get("x", 0.0), lp.get("y", 0.0), -lp.get("z", 0.0),
        qx, qy, qz, qw,
        ls.get("x", 1.0), ls.get("y", 1.0), ls.get("z", 1.0)))
    if not albedo_written:
        mr = comp_of(gtt, ("MeshRenderer",))
        mats = mr.read_typetree().get("m_Materials", []) if mr else []
        mo = pptr(mats[0]) if mats else None
        if mo:
            for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
                if nm == "_MainTex" and isinstance(val, dict):
                    to = pptr(val.get("m_Texture", {}))
                    if to:
                        to.read().image.convert("RGBA").save(os.path.join(OUTDIR, nl + "_albedo.png"))
                        albedo_written = True
    print("   %-12s verts=%-5d -> %s" % (part, nv, fn))

if not rows:
    print("NO parts for", NAME); sys.exit(1)
open(os.path.join(OUTDIR, nl + "_parts.tsv"), "w").write("\n".join(rows) + "\n")
print("%s: %d parts, albedo=%s" % (NAME, len(rows), "yes" if albedo_written else "NONE"))
