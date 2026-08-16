#!/usr/bin/env python3
"""Extract heli DETAIL PARTS (interior seats/steer, taillight lamp models, hind turret) positioned
ROOT-RELATIVE, grouped jeep-style (seats/steer/taillights/turret), each merged into one .txt with the SAME
Z-negate+winding convention as the body -- plus each group's real material colour, and the Seats/Seat_*
seat-POINT transforms as data. Feeds tinyclaw's Vehicle Spec `Parts = (txt, Color)[]` + a future passenger
system. Reuses tools/extract_huey.py's traversal + mesh writer.

Usage: python3 tools/extract_heli_parts.py [--bundle PATH] [--outdir DIR]
"""
import argparse, os, sys, json
import UnityPy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_huey import (tt, build_index, comps_of, children_of, local_offset, local_rot,
                          quat_mul, quat_apply, collect_meshes, mesh_to_obj)

HELIS = {"hind": "hind", "orca": "orca", "skycrane": "skycrane",
         "hummingbird_spec_ops": "hummingbird", "huey": "huey"}
CANDS = [r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle",
         os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")]
# per-type fallback colour if the material carries no explicit _Color (palette-based mats read white)
DEFAULT = {"seats": (0.12, 0.12, 0.13), "steer": (0.08, 0.08, 0.09),
           "taillights": (0.80, 0.10, 0.10), "turret": (0.16, 0.17, 0.14)}


def group_for(name, path):
    n = name.lower()
    if "turret" in path.lower() or n in ("yaw", "pitch"):
        return "turret"
    if n.startswith("seat"):
        return "seats"
    if n == "steer":
        return "steer"
    if n.startswith("taillight"):
        return "taillights"
    return None


def read_color(by_id, go_tt):
    """First MeshRenderer material's _Color (or _MainColor). None if absent/white-ish (palette-driven)."""
    for r in comps_of(by_id, go_tt).get("MeshRenderer", []):
        for mp in tt(r).get("m_Materials", []):
            mo = by_id.get(mp.get("m_PathID"))
            if not mo:
                continue
            try:
                for kv in tt(mo).get("m_SavedProperties", {}).get("m_Colors", []):
                    if kv.get("first") in ("_Color", "_MainColor"):
                        c = kv.get("second", {})
                        rgb = (float(c.get("r", 1)), float(c.get("g", 1)), float(c.get("b", 1)))
                        if sum(rgb) < 2.85:   # not ~white -> a real authored colour
                            return rgb
            except Exception:
                pass
    return None


def walk(by_id, gtt, off, rot, path, meshes, colors, depth=0):
    if depth > 9:
        return
    for name, cgt, ctt in children_of(by_id, gtt):
        lp, lr = local_offset(ctt), local_rot(ctt)
        coff = tuple(o + c for o, c in zip(off, quat_apply(rot, lp)))
        crot = quat_mul(rot, lr)
        cpath = path + "/" + name if path else name
        cs = comps_of(by_id, cgt)
        if "MeshFilter" in cs:
            g = group_for(name, cpath)
            if g:
                mf = cs["MeshFilter"][0]
                mp = tt(mf).get("m_Mesh", {}).get("m_PathID")
                if mp and mp in by_id:
                    meshes.setdefault(g, []).append((by_id[mp], coff, crot))
                    if g not in colors:
                        col = read_color(by_id, cgt)
                        if col:
                            colors[g] = col
        if name not in ("Rotors", "Wheels", "Tires", "Model_0", "Model_1"):
            walk(by_id, cgt, coff, crot, cpath, meshes, colors, depth + 1)


def seat_points(by_id, root_gtt):
    """Seats/Seat_* empty transforms -> [(name, (x,y,-z))] root-relative, Z-negated to Godot."""
    pts = []
    for name, gtt, ctt in children_of(by_id, root_gtt):
        if name == "Seats":
            for sn, sgt, sct in children_of(by_id, gtt):
                p = local_offset(sct)
                pts.append((sn, (round(p[0], 3), round(p[1], 3), round(-p[2], 3))))
    return pts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    args = ap.parse_args()
    env = UnityPy.load(args.bundle)
    by_id = build_index(env)
    manifest = {}
    for prefab, prefix in HELIS.items():
        pref = f"assets/coremasterbundle/vehicles/{prefab}/vehicle.prefab"
        if pref not in env.container:
            print(f"!! {pref} missing", file=sys.stderr); continue
        root = tt(env.container[pref])
        meshes, colors = {}, {}
        walk(by_id, root, (0., 0., 0.), (0., 0., 0., 1.), "", meshes, colors)
        pts = seat_points(by_id, root)
        print(f"== {prefix} ==")
        entry = {"parts": {}, "seat_points": pts}
        for g, mos in sorted(meshes.items()):
            obj = mesh_to_obj(mos, f"{prefix}_{g}")   # positioned (offset kept), Z-negate + winding-reverse
            out = os.path.abspath(os.path.join(args.outdir, f"{prefix}_{g}.txt"))
            with open(out, "w") as f:
                f.write(obj)
            col = colors.get(g) or DEFAULT[g]
            src = "material" if g in colors else "default"
            nv, nf = obj.count("\nv "), obj.count("\nf ")
            print(f"  {prefix}_{g:11s} {nv:4d}v {nf:4d}tri  colour=({col[0]:.2f},{col[1]:.2f},{col[2]:.2f}) [{src}]")
            entry["parts"][g] = {"txt": f"{prefix}_{g}.txt", "color": [round(c, 3) for c in col], "color_src": src}
        if pts:
            print(f"  seat points: {pts}")
        manifest[prefix] = entry
    mpath = os.path.join(os.path.dirname(os.path.abspath(__file__)), "heli_parts_manifest.json")
    with open(mpath, "w") as f:
        json.dump(manifest, f, indent=2)
    print("manifest ->", mpath)


if __name__ == "__main__":
    main()
