#!/usr/bin/env python3
"""Convert a hand-authored OBJ into this port's item-mesh format (content/items/<id>.txt).

Every other extractor here reads a Unity bundle. This one takes an OBJ somebody MODELLED -- astraclaw
authors props on another machine, cannot push, and hands them over as an archive (see the memory
reference_astraclaw_collab), so there was no path from "a nice OBJ" to "an item in the game".

⚠⚠ WHY THIS DOES NOT JUST COPY extract_items.py's TRANSFORM. Those scripts negate X AND Z and reverse the
winding, and that is correct FOR A UNITY SOURCE: Unity is left-handed, so the rip needs a handedness flip,
and the double negate is the 180 deg yaw that puts the model the right way round. An OBJ out of Blender is
already right-handed -- applying that same correction would mirror it. The port has shipped a mirrored mesh
twice this way (guns ejecting on the wrong side; every vehicle). So:

  ⭐ THE WINDING FLIP IS DERIVED FROM THE MATRIX, NOT PASSED IN. We build the axis change the caller asks
  for, take its DETERMINANT, and reverse the triangles only when it is negative (a reflection). A rotation
  keeps the winding; a mirror has to have it reversed or every face points inward. Nobody has to remember
  which case they are in, and "I negated one axis and it looked inside out" cannot happen.

Usage:
  obj_to_item_mesh.py IN.obj --id 9320 [--up z] [--forward -y] [--scale 1.0] [--recentre] [--dry-run]

--up    which axis of the SOURCE points up   (z = Blender default, y = already Y-up)
⚠ a NEGATIVE axis needs an equals sign -- argparse reads a bare "-z" as a flag: --forward=-z
--forward  which axis of the SOURCE points "front"; used only to pick the yaw (default -z, Godot's forward)
"""
import argparse, os, sys

AXES = {'x': (1,0,0), 'y': (0,1,0), 'z': (0,0,1),
        '-x': (-1,0,0), '-y': (0,-1,0), '-z': (0,0,-1)}

def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])

def det3(m):
    (a,b,c),(d,e,f),(g,h,i) = m
    return a*(e*i-f*h) - b*(d*i-f*g) + c*(d*h-e*g)

def build_basis(up, fwd, handedness):
    """Rows map a SOURCE vector onto game axes: game.x = right, game.y = up, game.z = back (RH, Y-up).

    ⚠ right = FORWARD x UP, in that order. Written the other way round first, which silently produced a
    determinant of -1 for the identity case (up=y forward=-z) -- so the tool reversed the winding of a mesh
    it had not rotated at all, and every face would have pointed inward. Caught by the round-trip control
    below, not by reading it: both orders look equally plausible on the page."""
    u = AXES[up]; f = AXES[fwd]
    if abs(u[0]*f[0] + u[1]*f[1] + u[2]*f[2]) > 1e-6:
        sys.exit(f"--up {up} and --forward {fwd} are not perpendicular")
    # Godot: +X right, +Y up, +Z toward the viewer. The model's forward should end up at -Z.
    right = cross(f, u)
    rows = [list(right), list(u), [-f[0], -f[1], -f[2]]]
    # ⭐ AND THIS IS THE ONLY WAY THE WINDING FLIP CAN EVER FIRE. Any perpendicular (up, forward) pair of
    # signed axes yields a right-handed basis, so an axis permutation ALONE is always a rotation, det +1 --
    # the flip below would be dead code. What actually needs it is a source that is left-handed to begin
    # with: an OBJ exported from Unity or Max is mirrored and NO permutation fixes that. This port has
    # shipped that exact bug twice (guns with the ejection port on the wrong side, then every vehicle), so
    # the case is real rather than hypothetical, and it is the caller stating a fact about their exporter.
    if handedness == 'left':
        rows[0] = [-c for c in rows[0]]
    return tuple(tuple(r) for r in rows)

def apply(m, v):
    return (m[0][0]*v[0] + m[0][1]*v[1] + m[0][2]*v[2],
            m[1][0]*v[0] + m[1][1]*v[1] + m[1][2]*v[2],
            m[2][0]*v[0] + m[2][1]*v[1] + m[2][2]*v[2])

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('obj'); ap.add_argument('--id', required=True)
    ap.add_argument('--up', default='z', choices=list(AXES))
    ap.add_argument('--forward', default='-y', choices=list(AXES))
    ap.add_argument('--scale', type=float, default=1.0)
    ap.add_argument('--source-handedness', default='right', choices=['right','left'],
                    help='left for an OBJ out of Unity/Max -- mirrors X and reverses the winding')
    ap.add_argument('--recentre', action='store_true', help='centre X/Z and sit the base at Y=0')
    ap.add_argument('--out-dir', default='game/content/items')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    basis = build_basis(a.up, a.forward, a.source_handedness)
    d = det3(basis)
    flip = d < 0
    if abs(abs(d) - 1.0) > 1e-6:
        sys.exit(f"axis change is not a pure rotation/reflection (det={d:.4f}) -- check --up/--forward")

    vs, vns, vts, faces = [], [], [], []
    for line in open(a.obj, 'r', errors='replace'):
        p = line.split()
        if not p: continue
        if p[0] == 'v':  vs.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == 'vn': vns.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == 'vt': vts.append(tuple(float(x) for x in p[1:3]))
        elif p[0] == 'f':
            # a | a/b | a//c | a/b/c, and fan-triangulate anything with >3 corners
            corners = []
            for tok in p[1:]:
                bits = (tok.split('/') + ['', ''])[:3]
                corners.append(tuple(int(b) if b else 0 for b in bits))
            for i in range(1, len(corners) - 1):
                faces.append([corners[0], corners[i], corners[i+1]])
    if not vs or not faces:
        sys.exit(f"{a.obj}: no geometry (v={len(vs)} f={len(faces)})")

    vs = [tuple(c * a.scale for c in apply(basis, v)) for v in vs]
    vns = [apply(basis, n) for n in vns]
    if a.recentre:
        xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
        cx = (min(xs)+max(xs))/2; cz = (min(zs)+max(zs))/2; fy = min(ys)
        vs = [(v[0]-cx, v[1]-fy, v[2]-cz) for v in vs]
    if flip:
        faces = [[t[0], t[2], t[1]] for t in faces]

    xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
    size = (max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))
    # ⭐ The size is PRINTED because it is the one thing the geometry cannot tell you is wrong: a pencil that
    # came out 19 metres long loads, renders and looks like nothing at all in the grid.
    print(f"[obj->item] {os.path.basename(a.obj)} -> id {a.id}: {len(vs)} verts, {len(faces)} tris, "
          f"det={d:+.0f} ({'MIRROR -> winding reversed' if flip else 'rotation -> winding kept'}), "
          f"size {size[0]:.3f} x {size[1]:.3f} x {size[2]:.3f} m")

    out = [f"# hand-authored OBJ -> Godot item mesh (up={a.up} fwd={a.forward} scale={a.scale} "
           f"det={d:+.0f}{' winding-reversed' if flip else ''}, RH Y-up)"]
    out += [f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}" for v in vs]
    out += [f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}" for n in vns]
    out += [f"vt {t[0]:.9f} {t[1]:.9f}" for t in vts]
    for t in faces:
        out.append("f " + " ".join(
            f"{c[0]}/{c[1] or c[0]}/{c[2] or c[0]}" if vts and vns else
            (f"{c[0]}//{c[2] or c[0]}" if vns else str(c[0])) for c in t))

    path = os.path.join(a.out_dir, f"{a.id}.txt")
    if a.dry_run:
        print(f"[obj->item] dry run, would write {path}")
        return
    with open(path, 'w') as fh: fh.write("\n".join(out) + "\n")
    print(f"[obj->item] wrote {path}")

if __name__ == '__main__':
    main()
