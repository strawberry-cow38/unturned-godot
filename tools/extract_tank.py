#!/usr/bin/env python3
"""Extract the full retail TANK from core.masterbundle -- every visual part (master: FULLY, ALL PARTS).

From the prefab dump: hull Model_0 + skinned treads Model_2 (CrawlerTrack) at root; a rotating turret under
Turrets/Turret_1/Yaw (Model_0 + Model_1) with an elevating gun under .../Yaw/Pitch/Model_0; 8 road wheels; a
driver seat + a gunner seat (up in the turret); steering. Palette-painted parts (hull/turret/gun/treads) get
UVs so they sample the tank's MilitaryPaintable palette; the turret + gun are extracted in their PIVOT's local
frame (position dropped, own rotation baked) so the runtime rotates Yaw/Pitch to aim. Solid detail parts get
their real _Color, positioned root-relative. Also dumps the rig (turret/gun pivots, muzzle, wheel + seat
positions, Z-negated for Godot) to a manifest for the vehicle implementation.

Reuses extract_huey's traversal -- collect_meshes already walks SkinnedMeshRenderer, so the treads come through.
Usage: python3 tools/extract_tank.py [--bundle PATH] [--outdir DIR]
"""
import argparse, os, sys, json
import UnityPy


# --- prefab traversal (self-contained; same logic as extract_huey.py) ---
def tt(o):
    return o.read_typetree()

def build_index(env):
    return {o.path_id: o for o in env.objects}

def comps_of(by_id, go_tt):
    out = {}
    for comp in go_tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        pid = c.get("m_PathID") if isinstance(c, dict) else None
        o = by_id.get(pid)
        if o:
            out.setdefault(o.type.name, []).append(o)
    return out

def children_of(by_id, go_tt):
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
        out.append((tt(go).get("m_Name", ""), tt(go), ctt))
    return out

def find_path(by_id, root_tt, path):
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
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))

PREFAB = "assets/coremasterbundle/vehicles/tank/vehicle.prefab"
CANDS = [r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle",
         os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")]
# palette-painted, extracted at their pivot origin (own_rot, position dropped) -- UVs kept
PALETTE = [
    ("Model_0",                            "tank_hull",     True),
    ("Model_2",                            "tank_treads",   True),   # SKINNED CrawlerTrack (collect_meshes handles it)
    ("Turrets/Turret_1/Yaw/Model_0",       "tank_turret",   True),
    ("Turrets/Turret_1/Yaw/Model_1",       "tank_turret_1", True),
    ("Turrets/Turret_1/Yaw/Pitch/Model_0", "tank_gun",      True),
]
WHEEL = ("Wheels/Wheel_0", "tank_wheel")                 # one road wheel at origin (reused at 8 positions -> rig)
POSITIONED = [("Objects/Seat_0", "tank_seat_driver"), ("Objects/Steer", "tank_steer"),
              ("Turrets/Turret_1/Yaw/Objects/Seat_1", "tank_seat_gunner")]  # root-relative, solid (gunner rides the turret)


def _write_obj(Vs, Ts, Ns, Fs, group):
    L = [f"g {group}"]
    L += ["v %.6f %.6f %.6f" % v for v in Vs]
    L += ["vt %s %s" % t for t in Ts]
    L += ["vn %.6f %.6f %.6f" % n for n in Ns]
    for f in Fs:
        s = "f"
        for (vi, ti, ni) in f:
            if ti and ni: s += " %d/%d/%d" % (vi, ti, ni)
            elif ni:      s += " %d//%d" % (vi, ni)
            elif ti:      s += " %d/%d" % (vi, ti)
            else:         s += " %d" % vi
        L.append(s)
    return "\n".join(L) + "\n"


def mesh_to_obj(mesh_objs, group, keep_uv):
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
            elif p[0] == "vt" and keep_uv:
                Ts.append((p[1], p[2]))
            elif p[0] == "vn" and keep_uv:
                nx, ny, nz = quat_apply(rot, (float(p[1]), float(p[2]), float(p[3])))
                Ns.append((nx, ny, -nz))
            elif p[0] == "f":
                idx = []
                for tok in p[1:]:
                    q = tok.split("/")
                    vi = int(q[0]) + vb
                    ti = (int(q[1]) + tb) if keep_uv and len(q) > 1 and q[1] else None
                    ni = (int(q[2]) + nb) if keep_uv and len(q) > 2 and q[2] else None
                    idx.append((vi, ti, ni))
                Fs.append(list(reversed(idx)))
    return _write_obj(Vs, Ts, Ns, Fs, group)


def collect_any(by_id, go_tt, ctt, acc, xform=((0., 0., 0.), (0., 0., 0., 1.)), depth=0):
    """Like extract_huey.collect_meshes but also walks SkinnedMeshRenderer -- the treads are one, and that
    version only checks MeshFilter (which is why Model_2 came back empty)."""
    if depth > 9:
        return
    off, rot = xform
    if ctt is not None:
        lp, lr = local_offset(ctt), local_rot(ctt)
        off = tuple(o + c for o, c in zip(off, quat_apply(rot, lp)))
        rot = quat_mul(rot, lr)
    cs = comps_of(by_id, go_tt)
    for r in cs.get("MeshFilter", []) + cs.get("SkinnedMeshRenderer", []):
        mp = tt(r).get("m_Mesh", {}).get("m_PathID")
        mo = by_id.get(mp)
        if mo is not None:
            acc.append((mo, off, rot))
    for _n, gtt, cht in children_of(by_id, go_tt):
        collect_any(by_id, gtt, cht, acc, (off, rot), depth + 1)


def read_color(by_id, go_tt, depth=0):
    cs = comps_of(by_id, go_tt)
    for r in cs.get("MeshRenderer", []) + cs.get("SkinnedMeshRenderer", []):
        for mp in tt(r).get("m_Materials", []):
            mo = by_id.get(mp.get("m_PathID"))
            if not mo:
                continue
            for kv in tt(mo).get("m_SavedProperties", {}).get("m_Colors", []):
                key = kv[0] if isinstance(kv, (list, tuple)) else kv.get("first")
                val = kv[1] if isinstance(kv, (list, tuple)) else kv.get("second")
                if key == "_Color" and isinstance(val, dict):
                    return (round(val.get("r", 0.5), 3), round(val.get("g", 0.5), 3), round(val.get("b", 0.5), 3))
    if depth < 6:   # the material often lives on a child mesh node (Wheel_0/Model_0), not the group node
        for _n, gtt, _c in children_of(by_id, go_tt):
            c = read_color(by_id, gtt, depth + 1)
            if c:
                return c
    return None


def find_path_xf(by_id, root_gtt, path):
    """Walk a '/'-path accumulating root-relative (off, rot). Returns (gtt, off, rot) or (None, ...)."""
    cur, off, rot = root_gtt, (0., 0., 0.), (0., 0., 0., 1.)
    for part in path.split("/"):
        nxt = None
        for name, gtt, ctt in children_of(by_id, cur):
            if name == part:
                lp, lr = local_offset(ctt), local_rot(ctt)
                off = tuple(o + c for o, c in zip(off, quat_apply(rot, lp)))
                rot = quat_mul(rot, lr)
                cur, nxt = gtt, gtt
                break
        if nxt is None:
            return None, off, rot
    return cur, off, rot


def gpos(by_id, root, path):
    g, off, rot = find_path_xf(by_id, root, path)
    return None if g is None else [round(off[0], 3), round(off[1], 3), round(-off[2], 3)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(__file__), "..", "game", "content"))
    args = ap.parse_args()
    env = UnityPy.load(args.bundle)
    by_id = build_index(env)
    root = tt(env.container[PREFAB])
    man = {"parts": {}, "rig": {}}

    def emit(path, name, own_rot, keep_uv, positioned):
        if positioned:
            gtt, off, rot = find_path_xf(by_id, root, path)
            seed = (off, rot)
        else:
            gtt, ctt = find_path(by_id, root, path)
            seed = ((0., 0., 0.), local_rot(ctt) if own_rot else (0., 0., 0., 1.))
        if gtt is None:
            print(f"  !! {path}: not found", file=sys.stderr); return
        acc = []
        collect_any(by_id, gtt, None, acc, seed)
        if not acc:
            print(f"  !! {path}: no mesh", file=sys.stderr); return
        obj = mesh_to_obj(acc, name, keep_uv)
        open(os.path.join(args.outdir, f"{name}.txt"), "w").write(obj)
        col = read_color(by_id, gtt)
        nv, nf = obj.count("\nv "), obj.count("\nf ")
        vs = [tuple(map(float, l.split()[1:4])) for l in obj.splitlines() if l.startswith("v ")]
        bb = ""
        if vs:
            xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
            bb = f"bbox x[{min(xs):.2f},{max(xs):.2f}] y[{min(ys):.2f},{max(ys):.2f}] z[{min(zs):.2f},{max(zs):.2f}]"
        print(f"  {name:18s} {nv:5d}v {nf:5d}tri uv={'Y' if keep_uv else 'n'} colour={col}  {bb}")
        man["parts"][name] = {"txt": f"{name}.txt", "color": list(col) if col else None, "uv": keep_uv}

    print("== tank palette meshes (UVs, pivot-local) ==")
    for path, name, own_rot in PALETTE:
        emit(path, name, own_rot, True, False)
    print("== road wheel (one, at origin) ==")
    emit(WHEEL[0], WHEEL[1], True, False, False)
    print("== positioned solid parts ==")
    for path, name in POSITIONED:
        emit(path, name, True, False, True)

    # ---- rig: pivots + mount points + wheel/seat arrays (Z-negated) ----
    man["rig"]["turret_yaw_pivot"] = gpos(by_id, root, "Turrets/Turret_1/Yaw")
    man["rig"]["gun_pitch_pivot"] = gpos(by_id, root, "Turrets/Turret_1/Yaw/Pitch")
    man["rig"]["muzzle"] = gpos(by_id, root, "Turrets/Turret_1/Yaw/Pitch/Aim/Barrel")
    man["rig"]["driver_seat"] = gpos(by_id, root, "Seats/Seat_0")
    wheels = []
    for name, gtt, ctt in children_of(by_id, next(g for n, g, c in children_of(by_id, root) if n == "Wheels")):
        p = local_offset(ctt)
        wheels.append({"name": name, "pos": [round(p[0], 3), round(p[1], 3), round(-p[2], 3)]})
    man["rig"]["wheels"] = sorted(wheels, key=lambda w: w["name"])
    print("== rig ==")
    for k, v in man["rig"].items():
        print(f"  {k}: {v if not isinstance(v, list) else f'{len(v)} entries'}")
    mpath = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tank_manifest.json")
    json.dump(man, open(mpath, "w"), indent=2)
    print("manifest ->", mpath)


if __name__ == "__main__":
    main()
