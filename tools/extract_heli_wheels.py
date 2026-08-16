#!/usr/bin/env python3
"""Extract the helicopter fleet's LANDING WHEELS, which nothing else does.

extract_heli_parts.walk() refuses to descend into "Wheels" (alongside "Rotors" and "Tires"), because rotors
are handled by their own extractor. Rotors are. Wheels are not -- they were skipped there and picked up
nowhere else, so the Hind and the Orca shipped with no wheels at all and their bodies resting on the belly.
Reported by strawberry 2026-08-16 ("some helis are missing WHEELS!"). The Huey, Hummingbird and Skycrane have
no Wheels node in the first place: those are skid aircraft, and their skids are part of the body mesh.

Each Wheel_N carries Model_0 and Model_1. Following the rotor's lesson -- Model_0/Model_1 are STATES, not
LODs -- both are dumped with their bounds so the difference can be read rather than guessed.

Usage: python3 tools/extract_heli_wheels.py [--bundle PATH] [--outdir DIR] [--model 0|1]
"""
import argparse, os, sys
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_huey import (tt, build_index, comps_of, children_of, local_offset, local_rot,
                          quat_mul, quat_apply, mesh_to_obj)
from extract_heli_parts import CANDS

# Only the airframes that HAVE a Wheels node. Listing the skid aircraft here and finding nothing would be
# indistinguishable from an extractor that silently failed on them.
HELIS = {"hind": "hind", "orca": "orca"}


def wheel_meshes(by_id, root, want_model):
    """[(mesh, offset, rot)] for every /Wheels/Wheel_*/Model_<want_model>, composed root-relative."""
    found = []
    for name, wgt, wct in children_of(by_id, root):
        if name != "Wheels":
            continue
        woff, wrot = local_offset(wct), local_rot(wct)
        for wn, cgt, cct in children_of(by_id, wgt):          # Wheel_0..Wheel_N
            lp, lr = local_offset(cct), local_rot(cct)
            coff = tuple(o + c for o, c in zip(woff, quat_apply(wrot, lp)))
            crot = quat_mul(wrot, lr)
            for mn, mgt, mct in children_of(by_id, cgt):      # Model_0 / Model_1
                if mn != f"Model_{want_model}":
                    continue
                mp2, mr2 = local_offset(mct), local_rot(mct)
                moff = tuple(o + c for o, c in zip(coff, quat_apply(crot, mp2)))
                mrot = quat_mul(crot, mr2)
                cs = comps_of(by_id, mgt)
                if "MeshFilter" not in cs:
                    continue
                mp = tt(cs["MeshFilter"][0]).get("m_Mesh", {}).get("m_PathID")
                if mp and mp in by_id:
                    found.append((by_id[mp], moff, mrot, f"{wn}/{mn}"))
    return found


def bounds(obj_text):
    lo = [9e9] * 3; hi = [-9e9] * 3; n = 0
    for line in obj_text.split("\n"):
        if line.startswith("v "):
            p = [float(x) for x in line.split()[1:4]]; n += 1
            for i in range(3):
                lo[i] = min(lo[i], p[i]); hi[i] = max(hi[i], p[i])
    return lo, hi, n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    ap.add_argument("--model", type=int, default=0, help="which Model_N state to emit (0 = the intact wheel)")
    args = ap.parse_args()

    env = UnityPy.load(args.bundle)
    by_id = build_index(env)

    for prefab, prefix in HELIS.items():
        ref = f"assets/coremasterbundle/vehicles/{prefab}/vehicle.prefab"
        if ref not in env.container:
            print(f"!! {ref} missing", file=sys.stderr); continue
        root = tt(env.container[ref])

        # Report BOTH states' bounds before writing either, so "Model_1 is the flat tyre" is something read off
        # the geometry rather than assumed from the naming.
        for m in (0, 1):
            got = wheel_meshes(by_id, root, m)
            if not got:
                print(f"  {prefix} Model_{m}: none"); continue
            obj = mesh_to_obj([(o, off, rot) for o, off, rot, _ in got], f"{prefix}_wheels")
            lo, hi, n = bounds(obj)
            print(f"  {prefix} Model_{m}: {len(got)} wheel(s) {n:4d}v  "
                  f"x[{lo[0]:6.2f},{hi[0]:6.2f}] y[{lo[1]:6.2f},{hi[1]:6.2f}] z[{lo[2]:6.2f},{hi[2]:6.2f}]")

        got = wheel_meshes(by_id, root, args.model)
        if not got:
            print(f"!! {prefix}: no Model_{args.model} wheels", file=sys.stderr); continue
        obj = mesh_to_obj([(o, off, rot) for o, off, rot, _ in got], f"{prefix}_wheels")
        out = os.path.abspath(os.path.join(args.outdir, f"{prefix}_wheels.txt"))
        with open(out, "w") as f:
            f.write(obj)
        lo, hi, n = bounds(obj)
        print(f"-> {out}  ({len(got)} wheels, {n} verts, bottom y {lo[1]:.3f})")
        print(f"   nodes: {', '.join(p for *_, p in got)}")


if __name__ == "__main__":
    main()
