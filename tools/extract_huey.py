#!/usr/bin/env python3
"""Extract the retail Huey helicopter's meshes from core.masterbundle.

strawberry 2026-08-15: "theres an existing huey helicopter model etc in the game already. idk if the mesh
is extracted". It wasn't, and it is -- `assets/coremasterbundle/vehicles/huey/vehicle.prefab`.

The minicopter reuses the ROTOR meshes only. A Huey is a ~14 m troop transport and a minicopter is a ~7 m
two-seat frame, so the fuselage is the wrong silhouette entirely -- but a two-blade main rotor and a small
tail rotor are the same components at a different scale, and the retail blades read far better than boxes.
The prefab already parks each rotor on its own Transform (Rotors/Rotor_0, Rotors/Rotor_1), which is also
what confirms the spin-the-pivot approach rather than animating blade geometry.

The fuselage is written out too -- unused by the minicopter, but it is the whole cost of a future Huey
vehicle and there is no reason to make someone re-derive the extraction.

Writes OBJ (positions + triangles, `g <name>` header) to game/content/, matching what ContentProvider.ParseObj
reads and the convention every other vehicle body already follows. Unity is left-handed/Z-forward and Godot
is right-handed, so Z is negated on the way out -- the same flip the existing vehicle meshes carry.

Usage:  python3 tools/extract_huey.py [--bundle PATH] [--outdir DIR]
"""
import argparse
import os
import sys

import UnityPy

PREFAB = "assets/coremasterbundle/vehicles/huey/vehicle.prefab"
DEFAULT_BUNDLE = os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")

# Which prefab child subtrees to emit, and under what content name.
#
# NOTE THE NUMBERING: Rotor_1 is the MAIN rotor and Rotor_0 is the TAIL rotor, which is the opposite of
# what the names suggest. Confirmed from their local transforms (--dump-transforms), not from the names:
#   Rotor_1  pos (0, 3.014, 0.248)        centred, high, identity rotation  -> main
#   Rotor_0  pos (-0.446, 3.574, -6.677)  off-centre, 6.7 m aft, rotated    -> tail
#
# `own_rot` decides whether the node's OWN local rotation is baked into the emitted vertices:
#   fuselage  -> True.  Model_0/Model_1 are authored Y-long and the prefab rotates them upright; without it
#                the body comes out 11.2 m tall and 4.8 m long, which is a helicopter standing on its tail.
#   rotors    -> False. A spinning disc has to sit around its own origin, and the pivot orientation is
#                re-created in code (BuildHeliModel stands the tail rotor on edge itself).
# EACH ROTOR HAS TWO MESHES AND THEY ARE NOT LODs -- they are the two STATES the game draws:
#   Model_0 = the physical blades   (main: 11.14 x 0.86 x 0.10 -- a bar)
#   Model_1 = the spin-blur disc    (main: 11.14 x 11.14 x 0.00 -- a flat plate)
# Merging them, which the first version of this did, renders a solid opaque disc with blade stripes baked
# into it that swallows the whole airframe. Every structural test still passed -- the mesh loaded, the pivot
# spun, the machine flew -- and it took actually LOOKING at a render to see it. Emit them separately so the
# runtime can swap by rotor speed the way the game does.
WANTED = {
    "Rotors/Rotor_1/Model_0": ("huey_rotor_main_blades", True),
    "Rotors/Rotor_1/Model_1": ("huey_rotor_main_disc", True),
    "Rotors/Rotor_0/Model_0": ("huey_rotor_tail_blades", True),
    "Rotors/Rotor_0/Model_1": ("huey_rotor_tail_disc", True),
    "Model_0": ("huey_body", True),
    "Model_1": ("huey_body_1", True),
}


def tt(o):
    return o.read_typetree()


def build_index(env):
    return {o.path_id: o for o in env.objects}


def comps_of(by_id, go_tt):
    """component type name -> [object] for a GameObject typetree."""
    out = {}
    for comp in go_tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        o = by_id.get(pid)
        if o:
            out.setdefault(o.type.name, []).append(o)
    return out


def children_of(by_id, go_tt):
    """[(name, gameobject_typetree, transform_tt)] for a GameObject's Transform children."""
    cs = comps_of(by_id, go_tt)
    trs = cs.get("Transform") or cs.get("RectTransform")
    if not trs:
        return []
    out = []
    for ch in tt(trs[0]).get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct:
            continue
        ctt = tt(ct)
        go = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if not go:
            continue
        gtt = tt(go)
        out.append((gtt.get("m_Name", ""), gtt, ctt))
    return out


def find_path(by_id, root_tt, path):
    """Walk a '/'-separated child path from the prefab root."""
    cur, cur_tr = root_tt, None
    for part in path.split("/"):
        for name, gtt, ctt in children_of(by_id, cur):
            if name == part:
                cur, cur_tr = gtt, ctt
                break
        else:
            return None, None
    return cur, cur_tr


def local_offset(ctt):
    p = ctt.get("m_LocalPosition", {}) or {}
    return (float(p.get("x", 0.0)), float(p.get("y", 0.0)), float(p.get("z", 0.0)))


def local_rot(ctt):
    q = ctt.get("m_LocalRotation", {}) or {}
    return (float(q.get("x", 0.0)), float(q.get("y", 0.0)), float(q.get("z", 0.0)), float(q.get("w", 1.0)))


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by_, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by_,
            aw * by_ - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by_ - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by_ - az * bz)


def quat_apply(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    # t = 2 * (q_vec x v); v' = v + w*t + q_vec x t
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def collect_meshes(by_id, go_tt, ctt, acc, xform=((0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)), depth=0):
    """Gather (mesh_object, position, rotation) for this subtree, composing local TRS down the tree.

    Rotation is NOT optional here: --dump-transforms showed Rotor_0 and both fuselage Models carry
    non-identity local rotations (the Models are a 180 deg axis swap). Accumulating position only --
    which is what the first cut of this did -- silently emits those meshes in the wrong orientation, and
    a rotor blade that is wrong by 90 deg still looks like a rotor blade in a screenshot. Scale is
    identity on every wanted node (checked), so it is not composed.
    """
    if depth > 8:
        return
    off, rot = xform
    if ctt is not None:
        lp, lr = local_offset(ctt), local_rot(ctt)
        off = tuple(o + c for o, c in zip(off, quat_apply(rot, lp)))
        rot = quat_mul(rot, lr)
    cs = comps_of(by_id, go_tt)
    for mf in cs.get("MeshFilter", []):
        mp = tt(mf).get("m_Mesh", {}).get("m_PathID")
        mo = by_id.get(mp)
        if mo is not None:
            acc.append((mo, off, rot))
    for _name, gtt, cht in children_of(by_id, go_tt):
        collect_meshes(by_id, gtt, cht, acc, (off, rot), depth + 1)


def mesh_to_obj(mesh_objs, group):
    """Merge meshes into one OBJ string. Unity -> Godot: negate Z (and flip winding to match)."""
    lines = [f"g {group}"]
    faces = []
    base = 0
    for mo, (ox, oy, oz), rot in mesh_objs:
        # Mesh.export() is UnityPy's own OBJ writer -- it handles compressed/streamed vertex buffers, which
        # reading m_Vertices directly does not (that returns EMPTY for these meshes, and an empty list is not
        # an error: the first cut of this wrote four 0-vertex files and reported success for each).
        text = mo.read().export()
        n = 0
        for line in text.splitlines():
            p = line.split()
            if len(p) == 4 and p[0] == "v":
                vx, vy, vz = quat_apply(rot, (float(p[1]), float(p[2]), float(p[3])))
                lines.append(f"v {vx + ox:.6f} {vy + oy:.6f} {-(vz + oz):.6f}")   # Z negated: Unity LH -> Godot RH
                n += 1
            elif p and p[0] == "f":
                # OBJ faces may be v, v/vt, or v/vt/vn -- take the position index only, and reverse the
                # winding to match the mirrored Z.
                vi = [int(tok.split("/")[0]) + base for tok in p[1:]]
                if len(vi) >= 3:
                    faces.append("f " + " ".join(str(i) for i in (vi[0], vi[2], vi[1])))
        base += n
    lines.extend(faces)
    return "\n".join(lines) + "\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=DEFAULT_BUNDLE)
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    ap.add_argument("--dump-transforms", action="store_true", help="print each wanted node's local TRS and exit")
    args = ap.parse_args()

    if not os.path.exists(args.bundle):
        sys.exit(f"bundle not found: {args.bundle}")
    env = UnityPy.load(args.bundle)
    if PREFAB not in env.container:
        sys.exit(f"{PREFAB} not in bundle container")
    by_id = build_index(env)
    root_tt = tt(env.container[PREFAB])

    for path, (name, own_rot) in WANTED.items():
        gtt, ctt = find_path(by_id, root_tt, path)
        if gtt is None:
            print(f"  !! {path}: not found", file=sys.stderr)
            continue
        if args.dump_transforms:
            print(f"{path}: pos={local_offset(ctt)} rot={ctt.get('m_LocalRotation')} scale={ctt.get('m_LocalScale')}")
            continue
        acc = []
        # The node's own local POSITION is always dropped -- it is the mount point, re-created in code, and a
        # mesh has to sit around its own origin. Its own ROTATION is kept only where the authored orientation
        # is part of the model rather than of the mounting; see WANTED.
        seed = ((0.0, 0.0, 0.0), local_rot(ctt) if own_rot else (0.0, 0.0, 0.0, 1.0))
        collect_meshes(by_id, gtt, None, acc, seed)
        if not acc:
            print(f"  !! {path}: no MeshFilter in subtree", file=sys.stderr)
            continue
        obj = mesh_to_obj(acc, name)
        out = os.path.abspath(os.path.join(args.outdir, f"{name}.txt"))
        with open(out, "w") as f:
            f.write(obj)
        nv = obj.count("\nv ")
        nf = obj.count("\nf ")
        print(f"  {path:22s} -> {out}  ({nv} verts, {nf} tris, from {len(acc)} mesh(es))")


if __name__ == "__main__":
    main()
