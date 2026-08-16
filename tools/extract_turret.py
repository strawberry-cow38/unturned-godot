#!/usr/bin/env python3
"""Extract a vehicle's TURRET as separately articulable pieces, plus its rig points.

The heli parts extractor merges everything under a Turrets node into one mesh, which is fine for a decoration
and useless for a weapon: a turret that traverses needs the YAW ring and the PITCH gun as separate meshes,
each baked at ITS OWN pivot origin, so rotating the node rotates the right geometry about the right point.

Retail models a turret as (seatIndex, itemID, yaw limits, pitch limits) -- VehicleAsset.TurretInfo -- with the
scene supplying Yaw/Pitch/Aim transforms. The node name carries the seat: Turret_1 is seat 1, which on the
Hind is the nose gunner, matching Seat_1 from the seat extraction.

Emits <vehicle>_turret_yaw.txt / _turret_pitch.txt and prints the rig points (pivot, aim, muzzle, eject) in
Godot space (Z negated).

Usage: python3 tools/extract_turret.py --vehicle hind [--bundle PATH] [--outdir DIR]
"""
import argparse, os, sys, json
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_huey import (tt, build_index, comps_of, children_of, local_offset, local_rot,
                          quat_mul, quat_apply, mesh_to_obj)
from extract_heli_parts import CANDS


def g(v):
    """Unity -> Godot: negate Z."""
    return (round(v[0], 4), round(v[1], 4), round(-v[2], 4))


def find(by_id, node, name, depth=0):
    """Depth-first search for a named child transform; returns (gt, ct) or (None, None)."""
    for n, cgt, cct in children_of(by_id, node):
        if n == name:
            return cgt, cct
        if depth < 8:
            hit = find(by_id, cgt, name, depth + 1)
            if hit[0] is not None:
                return hit
    return None, None


def mesh_under(by_id, node, off, rot):
    """Direct Model_* children of `node` as (mesh, offset, rot), relative to the frame passed in."""
    out = []
    for n, cgt, cct in children_of(by_id, node):
        if not n.startswith("Model_"):
            continue
        lp, lr = local_offset(cct), local_rot(cct)
        coff = tuple(o + c for o, c in zip(off, quat_apply(rot, lp)))
        crot = quat_mul(rot, lr)
        cs = comps_of(by_id, cgt)
        if "MeshFilter" not in cs:
            continue
        mp = tt(cs["MeshFilter"][0]).get("m_Mesh", {}).get("m_PathID")
        if mp and mp in by_id:
            out.append((by_id[mp], coff, crot))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--vehicle", required=True)
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    args = ap.parse_args()

    env = UnityPy.load(args.bundle)
    by_id = build_index(env)
    ref = f"assets/coremasterbundle/vehicles/{args.vehicle}/vehicle.prefab"
    if ref not in env.container:
        print(f"!! {ref} missing", file=sys.stderr); return 1
    root = tt(env.container[ref])

    turrets, _ = find(by_id, root, "Turrets")
    if turrets is None:
        print(f"{args.vehicle}: no Turrets node"); return 0

    manifest = {}
    for tn, tgt, tct in children_of(by_id, turrets):
        seat = int(tn.split("_")[1]) if "_" in tn and tn.split("_")[1].isdigit() else 0
        yaw_gt, yaw_ct = find(by_id, tgt, "Yaw")
        if yaw_gt is None:
            print(f"  {tn}: no Yaw node"); continue
        pitch_gt, pitch_ct = find(by_id, yaw_gt, "Pitch")
        aim_gt, aim_ct = find(by_id, pitch_gt or yaw_gt, "Aim")

        # Each piece is baked at its OWN pivot: the yaw ring in the yaw frame, the pitch gun in the pitch
        # frame. Baking both at the vehicle root -- which is what the merged part did -- gives geometry that
        # orbits the vehicle's origin instead of the turret's when you rotate it.
        pieces = {"yaw": mesh_under(by_id, yaw_gt, (0., 0., 0.), (0., 0., 0., 1.))}
        if pitch_gt is not None:
            pieces["pitch"] = mesh_under(by_id, pitch_gt, (0., 0., 0.), (0., 0., 0., 1.))

        entry = {"seat": seat, "pivot": g(local_offset(yaw_ct))}
        if aim_ct is not None:
            entry["aim"] = g(local_offset(aim_ct))
            bar_gt, bar_ct = find(by_id, aim_gt, "Barrel")
            ej_gt, ej_ct = find(by_id, aim_gt, "Eject")
            if bar_ct is not None: entry["muzzle"] = g(local_offset(bar_ct))
            if ej_ct is not None: entry["eject"] = g(local_offset(ej_ct))

        print(f"  {tn} -> seat {seat}, pivot {entry['pivot']}"
              + (f", aim {entry.get('aim')}, muzzle {entry.get('muzzle')}" if "aim" in entry else ""))
        for part, mos in pieces.items():
            if not mos:
                print(f"    {part}: NO MESH"); continue
            obj = mesh_to_obj(mos, f"{args.vehicle}_turret_{part}")
            out = os.path.abspath(os.path.join(args.outdir, f"{args.vehicle}_turret_{part}.txt"))
            with open(out, "w") as f:
                f.write(obj)
            lo = [9e9] * 3; hi = [-9e9] * 3; n = 0
            for line in obj.split("\n"):
                if line.startswith("v "):
                    p = [float(x) for x in line.split()[1:4]]; n += 1
                    for i in range(3): lo[i] = min(lo[i], p[i]); hi[i] = max(hi[i], p[i])
            print(f"    {part}: {n:4d}v -> {os.path.basename(out)}  "
                  f"x[{lo[0]:6.2f},{hi[0]:6.2f}] y[{lo[1]:6.2f},{hi[1]:6.2f}] z[{lo[2]:6.2f},{hi[2]:6.2f}]")
            entry.setdefault("parts", {})[part] = f"{args.vehicle}_turret_{part}.txt"
        manifest[tn] = entry

    mp = os.path.join(os.path.dirname(os.path.abspath(__file__)), f"{args.vehicle}_turret_manifest.json")
    with open(mp, "w") as f:
        json.dump(manifest, f, indent=1)
    print("manifest ->", mp)
    return 0


if __name__ == "__main__":
    sys.exit(main())
