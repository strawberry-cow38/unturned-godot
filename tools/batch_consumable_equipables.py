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
    """Map a Unity local rotation into the frame THE CLIPS USE.

    ⚠ These parts are driven by the consumable Use clips, so they have to live in the clips' frame, not the
    held-mesh one. batch_consumable_anims converts a bone position as (x, y, -z) and a bone rotation as
    (-x, -y, z, w) -- negate Z, not X and Z. Ripping the parts in the OTHER convention (X and Z, which is what
    extract_consumable uses for a static held mesh) left them resting in one frame and animating in another, so
    the bag opened sideways instead of up (strawberry: "its oriented wrong. the bag should open directly up").
    Vertices, rest position and rest rotation all follow the clip now. One negated axis flips handedness, so the
    winding reversal below stays correct.

    ⚠ The frame change N negates X and Z, so a rotation transforms by CONJUGATION -- R_g = N R_u N, not N R_u.
    Multiplying by the 180-degree Y turn once (which is what I shipped first) leaves every part carrying a
    spurious half-turn: an UNROTATED part came out as (0,1,0,0) instead of identity, which is visible in the
    ripped tsv and would have mirrored every spoon. Conjugating by a 180-degree Y turn is just "negate the axis
    the same way a position is negated", so the vector part takes N and the angle is untouched."""
    qx, qy, qz, qw = q.get("x", 0.0), q.get("y", 0.0), q.get("z", 0.0), q.get("w", 1.0)
    return (-qx, -qy, qz, qw)

def convert(mesh_obj, label, name):
    txt = mesh_obj.read().export()
    Vs, Ns, Ts, Fs = [], [], [], []
    for line in txt.splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v":    Vs.append((float(p[1]), float(p[2]), -float(p[3])))
        elif p[0] == "vn": Ns.append((float(p[1]), float(p[2]), -float(p[3])))
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
    # ⚠ A PART CAN HAVE ITS OWN MATERIAL, and the interesting ones do (strawberry 2026-09-10: "the chips
    # themselves are missing a texture and render as white squares"). bag_chips' Bone_3/Bone_4 -- the actual
    # crisps -- use Material_Chips_Filling/Texture_Chips_Filling, not the bag's; canned_beans' Bone_0/Bone_2/
    # Bone_3 use Material_Can_Beans_Filling. This tool used to take the FIRST meshed part's albedo and paint
    # every part with it, so every food with a filling rendered its filling in the wrapper's texture on
    # filling-shaped UVs -- i.e. white. Rip the texture PER PART and let each one name its own.
    texfiles = {}

    # ⚠ WHICH PARTS ARE ACTUALLY DRAWN IS DATA, NOT A NAMING CONVENTION. The prefab carries an LODGroup and its
    # LOD0 names exactly the renderers retail shows up close -- bag_chips LOD0 is Bone_0..Bone_4 with Model_1 as
    # the LOD1 copy, while canned_beans LOD0 is Model_0 + Model_2 + Model_3 + Bone_0..Bone_3. So "Model_n is an
    # LOD" and "Bone_n is the model" are BOTH wrong, and every rule I invented from the names lost real geometry
    # or drew a mesh on top of itself. Read the group instead.
    lod0, has_group = set(), False
    lg = comp_of(pf.read_typetree(), ("LODGroup",))
    if lg:
        lods = lg.read_typetree().get("m_LODs", [])
        if lods:
            has_group = True
            for r in lods[0].get("renderers", []):
                ro = by_id.get(r.get("renderer", {}).get("m_PathID"))
                if not ro: continue
                g = by_id.get(ro.read_typetree().get("m_GameObject", {}).get("m_PathID"))
                if g: lod0.add(str(g.read_typetree().get("m_Name")))

    def emit(ctt, part, parent):
        """ One node -> one row. Meshless nodes are emitted too (meshfile "-"): Stat_Tracker is animated by
        canned_beans' clips, and Bone_5 parents Bone_6 in several items, so dropping them both breaks track
        resolution and orphans real geometry. """
        nonlocal alb
        cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        gtt = cgo.read_typetree() if cgo else {}
        mf = comp_of(gtt, ("MeshFilter",)) if gtt else None
        mesh = pptr(mf.read_typetree().get("m_Mesh", {})) if mf else None
        fn, tex = "-", "-"
        if mesh:
            body, _ = convert(mesh, part, name)
            fn = "%s_%s.txt" % (nl, part.lower())
            open(os.path.join(OUT, fn), "w").write(body)
            mr = comp_of(gtt, ("MeshRenderer",))
            mats = mr.read_typetree().get("m_Materials", []) if mr else []
            mo = pptr(mats[0]) if mats else None
            if mo:
                for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                    nmp, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
                    if nmp != "_MainTex" or not isinstance(val, dict): continue
                    to = pptr(val.get("m_Texture", {}))
                    if not to: break
                    key = to.path_id
                    if key not in texfiles:
                        # The FIRST texture keeps the historical <item>_albedo.png name: the old single-mesh hold
                        # path still asks for it by that name, so renaming it would break every un-ripped item.
                        if not alb:
                            texfiles[key] = nl + "_albedo.png"; alb = True
                        else:
                            safe = "".join(ch if ch.isalnum() else "_" for ch in str(to.read_typetree().get("m_Name", "tex"))).strip("_").lower()
                            texfiles[key] = "%s_%s.png" % (nl, safe or "tex")
                        to.read().image.convert("RGBA").save(os.path.join(OUT, texfiles[key]))
                    tex = texfiles[key]
                    break
        lp, ls = ctt.get("m_LocalPosition", {}), ctt.get("m_LocalScale", {})
        qx, qy, qz, qw = yaw180(ctt.get("m_LocalRotation", {}))
        # draw flag: LOD0 membership when the prefab has an LODGroup; otherwise everything meshed, and the
        # caller de-duplicates any Model_n chain by index (the pre-LODGroup fallback, now only a safety net).
        draw = 1 if (fn != "-" and (part in lod0 if has_group else True)) else 0
        rows.append("%s\t%s\t%.6f %.6f %.6f\t%.6f %.6f %.6f %.6f\t%.6f %.6f %.6f\t%s\t%d\t%s" % (
            part, fn, lp.get("x", 0.0), lp.get("y", 0.0), -lp.get("z", 0.0),
            qx, qy, qz, qw, ls.get("x", 1.0), ls.get("y", 1.0), ls.get("z", 1.0), parent, draw, tex))

    def walk(tr_tt, parent):
        """ RECURSIVE. The old version read only the prefab root's DIRECT children, but the clips address paths
        like .../Item_Root/Bone_5/Bone_6 -- ten curves across five items nest a second level, and a Bone_6 keyed
        in Bone_5's space but parented to the root lands nowhere at all. """
        for ch in tr_tt.get("m_Children", []):
            ct = by_id.get(ch.get("m_PathID"))
            if not ct: continue
            ctt = ct.read_typetree()
            cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
            if not cgo: continue
            part = str(cgo.read_typetree().get("m_Name"))
            emit(ctt, part, parent)
            walk(ctt, part)

    # The prefab ROOT is the node retail renames to the item id and hangs off the hand hook -- the "13" in
    # .../Right_Hook/13/Bone_0 -- and it carries its own Pos/Rot/Scale curves. It is what places the item
    # relative to the hand, so the bag the other hand reaches into does not ride the fist holding it.
    rtt = tr.read_typetree()
    emit(rtt, "Item_Root", "-")
    walk(rtt, "Item_Root")
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
