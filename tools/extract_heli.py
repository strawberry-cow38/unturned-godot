#!/usr/bin/env python3
"""Extract the whole retail helicopter fleet's meshes from core.masterbundle.

Generalises tools/extract_huey.py (tinyclaw) to every heli by REUSING its exact functions -- so the output
is byte-identical to the shipped huey pass and BuildHeliModel consumes every heli the same way. Discovery
(child-tree dump of all seven) confirmed a UNIFORM rig: body = top-level Model_0 + Model_1; rotors under
Rotors/Rotor_1 (MAIN -- centred, x~=0 z~=0) and Rotors/Rotor_0 (TAIL -- off-centre, ~7-9m aft). Names lie
(Rotor_1 is the main rotor); mapping is by transform. Each rotor keeps Model_0 (physical blades) + Model_1
(spin-blur disc) SEPARATE -- they are the two draw states, not LODs; merged they make an opaque plate.

Traps inherited from the huey pass (all in extract_huey): Mesh.export() (m_Vertices returns EMPTY, no error);
Z-negate + winding-reverse (Unity LH -> Godot RH); own_rot bakes a node's own local rotation (the fuselage is
authored Y-long then stood upright). Matches the shipped huey own_rot convention exactly. Huey is re-derived
here as a parity check -- `git diff game/content/huey_*.txt` must come back empty.

Usage: python3 tools/extract_heli.py [--bundle PATH] [--outdir DIR] [--only NAME]
"""
import argparse, os, sys
import UnityPy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_huey import tt, build_index, find_path, local_rot, collect_meshes, quat_apply


def mesh_to_obj_uv(mesh_objs, group):
    """Like extract_huey.mesh_to_obj but KEEPS UVs (+ normals): parses vt/vn from Mesh.export() and emits
    v/vt/vn faces, so the body can sample the vehicle's paintable palette by UV (it renders flat grey
    otherwise). Same Z-negate + winding-reverse convention as the position-only writer."""
    Vs, Ts, Ns, Fs = [], [], [], []
    for mo, (ox, oy, oz), rot in mesh_objs:
        text = mo.read().export()
        vb, tb, nb = len(Vs), len(Ts), len(Ns)
        for line in text.splitlines():
            p = line.split()
            if not p:
                continue
            if p[0] == "v":
                vx, vy, vz = quat_apply(rot, (float(p[1]), float(p[2]), float(p[3])))
                Vs.append((vx + ox, vy + oy, -(vz + oz)))
            elif p[0] == "vt":
                Ts.append((p[1], p[2]))
            elif p[0] == "vn":
                nx, ny, nz = quat_apply(rot, (float(p[1]), float(p[2]), float(p[3])))
                Ns.append((nx, ny, -nz))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/")
                    vi = int(q[0]) + vb
                    ti = (int(q[1]) + tb) if len(q) > 1 and q[1] else None
                    ni = (int(q[2]) + nb) if len(q) > 2 and q[2] else None
                    idx.append((vi, ti, ni))
                Fs.append(list(reversed(idx)))   # reverse winding to compensate the Z flip
    L = [f"g {group}"]
    L += ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            if ti and ni:
                s += " %d/%d/%d" % (vi, ti, ni)
            elif ni:
                s += " %d//%d" % (vi, ni)
            elif ti:
                s += " %d/%d" % (vi, ti)
            else:
                s += " %d" % vi
        L.append(s)
    return "\n".join(L) + "\n"

HELIS = {                                  # vehicle prefab dir -> output content prefix
    "hind": "hind",
    "orca": "orca",
    "skycrane": "skycrane",
    "hummingbird_spec_ops": "hummingbird",  # 3 variants share body+rotor geometry; spec_ops has no siren add-ons
    "huey": "huey",                         # parity re-derive vs the shipped extract_huey output
}
STRUCT = [                                  # (prefab child path, output suffix, own_rot) -- uniform across the fleet
    ("Rotors/Rotor_1/Model_0", "rotor_main_blades", True),
    ("Rotors/Rotor_1/Model_1", "rotor_main_disc",   True),
    ("Rotors/Rotor_0/Model_0", "rotor_tail_blades", True),
    ("Rotors/Rotor_0/Model_1", "rotor_tail_disc",   True),
    ("Model_0",                "body",              True),
    ("Model_1",                "body_1",            True),
]
CANDS = [r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle",
         os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    ap.add_argument("--only", default=None, help="one prefab dir name, e.g. hind")
    args = ap.parse_args()
    if not os.path.exists(args.bundle):
        sys.exit(f"bundle not found: {args.bundle}")
    env = UnityPy.load(args.bundle)
    by_id = build_index(env)
    for prefab_name, prefix in HELIS.items():
        if args.only and prefab_name != args.only:
            continue
        pref = f"assets/coremasterbundle/vehicles/{prefab_name}/vehicle.prefab"
        if pref not in env.container:
            print(f"!! {pref} not in bundle", file=sys.stderr)
            continue
        root_tt = tt(env.container[pref])
        print(f"== {prefix} ({prefab_name}) ==")
        for path, suffix, own_rot in STRUCT:
            gtt, ctt = find_path(by_id, root_tt, path)
            if gtt is None:
                print(f"  !! {path}: not found", file=sys.stderr)
                continue
            acc = []
            seed = ((0.0, 0.0, 0.0), local_rot(ctt) if own_rot else (0.0, 0.0, 0.0, 1.0))
            collect_meshes(by_id, gtt, None, acc, seed)
            if not acc:
                print(f"  !! {path}: no MeshFilter in subtree", file=sys.stderr)
                continue
            name = f"{prefix}_{suffix}"
            obj = mesh_to_obj_uv(acc, name)
            out = os.path.abspath(os.path.join(args.outdir, f"{name}.txt"))
            with open(out, "w") as f:
                f.write(obj)
            nv = obj.count("\nv ")
            nf = obj.count("\nf ")
            print(f"  {name:26s} {nv:5d} v {nf:5d} tri  <- {path}")


if __name__ == "__main__":
    main()
