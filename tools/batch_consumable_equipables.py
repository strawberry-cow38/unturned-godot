#!/usr/bin/env python3
"""Rip every consumable's equipable parts in ONE process.

⚠ The first version shelled out to extract_consumable_equipable.py per item, which reloads a 200 MB masterbundle
each time -- ~4 s x 170, and long enough that it was killed twice part-way (once by the harness reclaiming memory,
once silently when its parent ssh session went away). Loading the bundle ONCE turns the whole job into seconds and
removes the reason it needed to survive in the background at all. The single-item CLI stays for spot checks.

Resumable anyway: an item that already has its _parts.tsv is skipped."""
import UnityPy, sys, os, collections

MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
TSV = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.tsv"
OUT = sys.argv[1] if len(sys.argv) > 1 else r"C:\claude-workspace\consumeparts"
os.makedirs(OUT, exist_ok=True)

env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = list(env.container.items())

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
    """Map a Unity local rotation into the X/Z-negated Godot frame.

    ⚠ The frame change N negates X and Z, so a rotation transforms by CONJUGATION -- R_g = N R_u N, not N R_u.
    Multiplying by the 180-degree Y turn once (which is what I shipped first) leaves every part carrying a
    spurious half-turn: an UNROTATED part came out as (0,1,0,0) instead of identity, which is visible in the
    ripped tsv and would have mirrored every spoon. Conjugating by a 180-degree Y turn is just "negate the axis
    the same way a position is negated", so the vector part takes N and the angle is untouched."""
    qx, qy, qz, qw = q.get("x", 0.0), q.get("y", 0.0), q.get("z", 0.0), q.get("w", 1.0)
    return (-qx, qy, -qz, qw)

def convert(mesh_obj, label, name):
    txt = mesh_obj.read().export()
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
                idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
            Fs.append(list(reversed(idx)))
    L = ["# %s (%s equipable rip -> Godot, X+Z negated + winding reversed)" % (label, name)]
    L += ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
        L.append(s)
    return "\n".join(L) + "\n", len(Vs)

def rip(name):
    nl = name.lower()
    pf = next((o for p, o in cont if p.lower().endswith("/" + nl + "/equipable.prefab") and o.type.name == "GameObject"), None)
    if not pf: return None
    tr = comp_of(pf.read_typetree(), ("Transform",))
    if not tr: return None
    rows, alb = [], False
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct: continue
        ctt = ct.read_typetree()
        cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if not cgo: continue
        gtt = cgo.read_typetree()
        part = str(gtt.get("m_Name"))
        mf = comp_of(gtt, ("MeshFilter",))
        mesh = pptr(mf.read_typetree().get("m_Mesh", {})) if mf else None
        if not mesh: continue
        body, _ = convert(mesh, part, name)
        fn = "%s_%s.txt" % (nl, part.lower())
        open(os.path.join(OUT, fn), "w").write(body)
        lp, ls = ctt.get("m_LocalPosition", {}), ctt.get("m_LocalScale", {})
        qx, qy, qz, qw = yaw180(ctt.get("m_LocalRotation", {}))
        rows.append("%s\t%s\t%.6f %.6f %.6f\t%.6f %.6f %.6f %.6f\t%.6f %.6f %.6f" % (
            part, fn, -lp.get("x", 0.0), lp.get("y", 0.0), -lp.get("z", 0.0),
            qx, qy, qz, qw, ls.get("x", 1.0), ls.get("y", 1.0), ls.get("z", 1.0)))
        if not alb:
            mr = comp_of(gtt, ("MeshRenderer",))
            mats = mr.read_typetree().get("m_Materials", []) if mr else []
            mo = pptr(mats[0]) if mats else None
            if mo:
                for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                    nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
                    if nm == "_MainTex" and isinstance(val, dict):
                        to = pptr(val.get("m_Texture", {}))
                        if to:
                            to.read().image.convert("RGBA").save(os.path.join(OUT, nl + "_albedo.png")); alb = True
    if not rows: return None
    open(os.path.join(OUT, nl + "_parts.tsv"), "w").write("\n".join(rows) + "\n")
    return rows

names = [l.split("\t")[0].strip() for l in open(TSV) if l.strip()]
ok = skipped = miss = 0
hist = collections.Counter(); boned = []
for n in names:
    tsvp = os.path.join(OUT, n.lower() + "_parts.tsv")
    if os.path.exists(tsvp):
        skipped += 1
        rows = open(tsvp).read().splitlines()
    else:
        rows = rip(n)
        if rows is None:
            miss += 1
            print("MISS %s (no equipable.prefab or no meshed parts)" % n, flush=True)
            continue
        ok += 1
    hist[len(rows)] += 1
    if any(r.lower().startswith("bone_") for r in rows): boned.append(n)
print("\nripped=%d skipped=%d missing=%d of %d" % (ok, skipped, miss, len(names)))
print("parts-per-item:", dict(sorted(hist.items())))
print("items with Bone_n (animated sub-pieces): %d / %d" % (len(boned), len(names)))
