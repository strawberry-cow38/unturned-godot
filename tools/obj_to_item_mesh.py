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
import argparse, math, os, sys

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

def load_obj(path):
    vs, vns, tris = [], [], []
    for line in open(path, errors='replace'):
        q = line.split()
        if not q: continue
        if q[0] == 'v': vs.append(tuple(float(x) for x in q[1:4]))
        elif q[0] == 'vn': vns.append(tuple(float(x) for x in q[1:4]))
        elif q[0] == 'f':
            c = []
            for t in q[1:]:
                b = (t.split('/') + ['', ''])[:3]
                c.append(tuple(int(x) if x else 0 for x in b))
            for i in range(1, len(c) - 1): tris.append([c[0], c[i], c[i+1]])
    return vs, vns, tris

def winding_fraction(vs, vns, tris):
    """What fraction of triangles run COUNTER-clockwise around their own stated normals. ~1.0 and ~0.0 are the
    two conventions; anything in between means the mesh is not consistently wound and the caller should look at
    it rather than trust a flip."""
    if not vns: return None
    agree = total = 0
    for t in tris:
        try: p0, p1, p2 = [vs[i[0]-1] for i in t]
        except IndexError: continue
        u = (p1[0]-p0[0], p1[1]-p0[1], p1[2]-p0[2])
        w = (p2[0]-p0[0], p2[1]-p0[1], p2[2]-p0[2])
        g = cross(u, w)
        ns = [vns[i[2]-1] for i in t if i[2] and i[2] <= len(vns)]
        if not ns: continue
        avg = [sum(x[k] for x in ns)/len(ns) for k in range(3)]
        total += 1
        if g[0]*avg[0] + g[1]*avg[1] + g[2]*avg[2] > 0: agree += 1
    return agree/total if total else None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('obj'); ap.add_argument('--id', required=True)
    # ⚠⚠ REQUIRED, AND IT USED TO DEFAULT TO 'z'. Blender's default export is Z-up, so that looked like the
    # sensible default -- but every model this port has actually been handed is Y-up (astraclaw's stated export
    # convention), so the default was wrong for 10 conversions out of 10 and silently rotated the mesh 90 deg
    # about X. It is silent because --recentre then sits the tipped model's new base at Y=0, so it still looks
    # placed; a pile of ice cubes merely stopped being a pile. Master caught it by eye -- "u got rotation
    # right?" -- which is not a check. A default that is wrong every time is not a default.
    ap.add_argument('--up', required=True, choices=list(AXES),
                    help="which axis of the SOURCE points up. y for a Blender/glTF Y-up export (what we get); "
                         "z for Blender's own Z-up scene convention. No default on purpose.")
    ap.add_argument('--forward', default='-z', choices=list(AXES))
    ap.add_argument('--scale', type=float, default=1.0)
    ap.add_argument('--source-handedness', default='right', choices=['right','left'],
                    help='left for an OBJ out of Unity/Max -- mirrors X and reverses the winding')
    ap.add_argument('--recentre', action='store_true', help='centre X/Z and sit the base at Y=0')
    ap.add_argument('--out-dir', default='game/content/items')
    ap.add_argument('--match-winding', metavar='REF.txt',
                    help='an already-shipped item mesh; reverse the triangles iff the input disagrees with it '
                         'about whether vertex order runs CCW around the stated normals')
    ap.add_argument('--smooth', type=float, default=0.0, metavar='DEG',
                    help='recompute vertex normals, averaging faces that meet at under DEG (0 = keep as exported). '
                         '60 is a good default: curves smooth, rims and handle joins stay crisp.')
    ap.add_argument('--manifest', default='game/content/items/items_manifest.json',
                    help='the index WorldItem actually reads; "" to skip')
    ap.add_argument('--catalog', default='game/content/items_catalog.tsv',
                    help='where the name/type for the manifest entry come from')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()

    basis = build_basis(a.up, a.forward, a.source_handedness)
    d = det3(basis)
    flip = d < 0
    if abs(abs(d) - 1.0) > 1e-6:
        sys.exit(f"axis change is not a pure rotation/reflection (det={d:.4f}) -- check --up/--forward")

    vs, vns, faces = load_obj(a.obj)
    vts = [tuple(float(x) for x in l.split()[1:3]) for l in open(a.obj, errors='replace') if l.startswith('vt ')]
    if not vs or not faces:
        sys.exit(f"{a.obj}: no geometry (v={len(vs)} f={len(faces)})")

    vs = [tuple(c * a.scale for c in apply(basis, v)) for v in vs]
    vns = [apply(basis, n) for n in vns]
    if a.recentre:
        xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
        cx = (min(xs)+max(xs))/2; cz = (min(zs)+max(zs))/2; fy = min(ys)
        vs = [(v[0]-cx, v[1]-fy, v[2]-cz) for v in vs]
    # ⭐⭐ MEASURED, NOT ASSUMED. The axis change above can only tell you about ROTATION; it cannot know that
    # two pipelines disagree about which way a triangle runs around its own normal. Ours do: every shipped item
    # mesh winds CW with respect to its normals (0 of 44, 0 of 64, 0 of 52 triangles CCW across three of them --
    # unanimous), and a hand-authored OBJ out of Blender winds CCW (92 of 92, 200 of 200). Opposite. Passing that
    # correction in as a flag is how you get it backwards once and ship an inside-out mesh, so instead the tool
    # measures BOTH files the same way and flips only on a disagreement, and prints what it found.
    if a.match_winding:
        ref = winding_fraction(*load_obj(a.match_winding))
        mine = winding_fraction(vs, vns, faces)
        if ref is None or mine is None:
            print("[obj->item] WARNING: cannot compare winding (a file has no normals) -- left untouched")
        else:
            print(f"[obj->item] winding: reference {ref*100:.0f}% CCW, input {mine*100:.0f}% CCW", end='')
            if (ref > 0.5) != (mine > 0.5):
                faces = [[t[0], t[2], t[1]] for t in faces]
                flip = not flip
                print(" -> DISAGREE, triangles reversed")
            else:
                print(" -> agree, kept")
    if flip:
        faces = [[t[0], t[2], t[1]] for t in faces]

    if a.smooth > 0.0:
        # ⭐ ANGLE-WEIGHTED SMOOTHING, NOT "average everything". A flat-shaded export gives every face its own
        # normal, which is what makes a turned object read as a stack of facets. Averaging ALL faces that touch a
        # position would equally destroy the edges that SHOULD be sharp -- a plate's rim, where the mug's handle
        # meets the body, the lip of a bowl -- and turn them into soft smears. So faces are only averaged into
        # each other when they actually meet at a shallow angle; anything steeper stays its own surface.
        # ⚠ Grouped by POSITION, rounded to 0.1 mm: a flat export duplicates the vertex per face, so the shared
        # corner is several entries that are equal rather than one that is shared.
        import collections
        faces_n = []
        for t in faces:
            p0, p1, p2 = [vs[c[0]-1] for c in t]
            u = (p1[0]-p0[0], p1[1]-p0[1], p1[2]-p0[2]); w = (p2[0]-p0[0], p2[1]-p0[1], p2[2]-p0[2])
            n = cross(u, w); m = sum(k*k for k in n) ** 0.5
            faces_n.append(tuple(k/m for k in n) if m > 1e-12 else (0.0, 1.0, 0.0))
        at = collections.defaultdict(list)
        key = lambda v: (round(v[0], 4), round(v[1], 4), round(v[2], 4))
        for fi, t in enumerate(faces):
            for c in t: at[key(vs[c[0]-1])].append(fi)
        cosmin = math.cos(math.radians(a.smooth))
        new_vn, new_faces = [], []
        for fi, t in enumerate(faces):
            fn = faces_n[fi]; corners = []
            for c in t:
                acc = [0.0, 0.0, 0.0]
                for oj in at[key(vs[c[0]-1])]:
                    on = faces_n[oj]
                    if fn[0]*on[0] + fn[1]*on[1] + fn[2]*on[2] >= cosmin:
                        acc[0] += on[0]; acc[1] += on[1]; acc[2] += on[2]
                m = sum(k*k for k in acc) ** 0.5
                nn = tuple(k/m for k in acc) if m > 1e-12 else fn
                new_vn.append(nn); corners.append((c[0], c[1], len(new_vn)))
            new_faces.append(corners)
        vns, faces = new_vn, new_faces
        print(f"[obj->item] smoothed at {a.smooth:.0f} deg: {len(new_vn)} normals "
              f"({sum(1 for k, v in at.items() if len(v) > 1)} shared positions)")

    xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
    size = (max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))
    # ⭐ The size is PRINTED because it is the one thing the geometry cannot tell you is wrong: a pencil that
    # came out 19 metres long loads, renders and looks like nothing at all in the grid.
    # ⚠ The determinant and the winding are now TWO separate facts and the label must not conflate them: after a
    # --match-winding reversal `flip` is true while det is still +1, and printing "MIRROR" there would describe a
    # reflection that never happened. Report what each one actually was.
    print(f"[obj->item] {os.path.basename(a.obj)} -> id {a.id}: {len(vs)} verts, {len(faces)} tris, "
          f"det={d:+.0f} ({'reflection' if d < 0 else 'rotation'}), "
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

    # ⭐⭐ AND REGISTER IT, because writing the mesh is only half of wiring an item and the other half fails
    # SILENTLY. WorldItem.GetModel looks the id up in items_manifest.json and falls back to a 0.24 m grey cube
    # when it is missing -- it does not go looking for <id>.txt on disk. I shipped eight correct meshes with no
    # manifest rows and rendered eight identical little boxes, which looks exactly like "the converter is
    # broken" and is not. A tool that converts but does not register is a tool that lies about being done.
    if a.manifest:
        import json
        man = json.load(open(a.manifest)) if os.path.exists(a.manifest) else {}
        name, typ = f"Item {a.id}", "Generic"
        if a.catalog and os.path.exists(a.catalog):
            for line in open(a.catalog, errors='replace'):
                c = line.rstrip('\n').split('\t')
                if len(c) >= 3 and c[0] == str(a.id): name, typ = c[1], c[2]; break
        tex = f"{a.id}.png"
        if not os.path.exists(os.path.join(a.out_dir, tex)): tex = None
        centre = [round((min(xs)+max(xs))/2, 4), round((min(ys)+max(ys))/2, 4), round((min(zs)+max(zs))/2, 4)]
        # ⚠⚠ RE-REGISTERING MUST NOT DROP HAND-SET KEYS. This used to assign a fresh dict, so every key the
        # manifest carries that the converter does not generate -- "metal", "translucent", "rounds" -- was
        # silently deleted the next time the mesh was re-exported. It cost exactly that: the spoon was marked
        # metal, then master asked for its yaw turned 180, and the re-run wiped the flag. The spoon shipped as
        # grey plastic next to a steel fork, and nothing anywhere said so. Geometry keys are overwritten
        # because they are derived from THIS mesh; everything else is the manifest's own and is kept.
        entry = man.get(str(a.id)) or {}
        entry.update({"name": name, "type": typ, "obj": f"{a.id}.txt", "tex": tex, "color": entry.get("color"),
                      "box": [round(c, 4) for c in size], "center": centre, "parts": entry.get("parts", 1)})
        man[str(a.id)] = entry
        kept = sorted(k for k in entry if k not in
                      {"name", "type", "obj", "tex", "color", "box", "center", "parts"})
        with open(a.manifest, 'w') as fh: json.dump(man, fh, indent=0, sort_keys=True)
        print(f"[obj->item] registered {a.id} \"{name}\" ({typ}) in {os.path.basename(a.manifest)}"
              + (f"  [kept: {', '.join(kept)}]" if kept else "")
              + ("" if tex else "  ⚠ no texture, will render untextured"))

if __name__ == '__main__':
    main()
