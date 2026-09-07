"""Close an open planar boundary loop in a ripped prop mesh.

    python cap_open_loops.py Lamp_1 [More_0 ...]        # names under content/objects, no .obj
    python cap_open_loops.py --dry Lamp_1               # report only

Retail models are open wherever the player could never look: a floor lamp's base has no underside because in
Unturned you cannot pick one up. Ours are DEPLOYABLES now (master 2026-09-07), so you can carry them, place them
on a slope and stand under them -- and with CullMode.Disabled on prop materials an uncapped base does not read as
a hole, it reads as the INSIDE of the base, which is worse.

Finding the hole is the whole job and it is not "count faces": an OBJ splits a vertex per UV/normal, so raw
indices never match across faces and every edge in the file looks unshared. WELD BY POSITION first, then a
boundary edge is one used by exactly one welded face -- Lamp_1 goes from 92 apparent boundary edges to 4 real
ones, which is the single missing quad.

Caps only PLANAR loops (all corners share one axis value), because that is the shape a modeller leaves open; a
non-planar hole is damage, not a deliberate omission, and silently fanning it would hide that. The new normal
points AWAY from the mesh centroid along that axis, and the winding is ordered to match it -- belt and braces,
since prop materials render both sides anyway (see feedback_godot_cull_disabled_normal_flip).

Each cap corner REUSES a vt index that an existing face already used for that exact position, so the new face
samples the same palette texel as the wall it closes. A fresh UV would have made the cap a different colour.
"""
import os, sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

TOL = 1e-4

def load(path):
    V, VT, VN, F, lines = [], [], [], [], []
    for ln in open(path):
        lines.append(ln.rstrip("\n"))
        p = ln.split()
        if not p: continue
        if p[0] == "v": V.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == "vt": VT.append(p[1:3])
        elif p[0] == "vn": VN.append(tuple(float(x) for x in p[1:4]))
        elif p[0] == "f":
            face = []
            for t in p[1:]:
                q = (t.split("/") + ["", ""])[:3]
                face.append((int(q[0]) - 1,
                             int(q[1]) - 1 if q[1] else None,
                             int(q[2]) - 1 if q[2] else None))
            F.append(face)
    return V, VT, VN, F, lines

def cap(name, dry=False):
    path = os.path.join(ug_paths.objects_out(), name + ".obj")
    if not os.path.isfile(path):
        print(f"  !! no such mesh: {path}"); return False
    V, VT, VN, F, lines = load(path)

    weld, rep = {}, []
    for i, v in enumerate(V):
        k = tuple(round(c, 5) for c in v)
        weld.setdefault(k, i)
        rep.append(weld[k])
    # one (v, vt) the file already uses for each welded position, so the cap keeps the palette texel
    sample = {}
    for f in F:
        for vi, ti, _ in f:
            sample.setdefault(rep[vi], (vi, ti))

    used = Counter()
    for f in F:
        for k in range(len(f)):
            a, b = rep[f[k][0]], rep[f[(k + 1) % len(f)][0]]
            used[(min(a, b), max(a, b))] += 1
    boundary = [e for e, c in used.items() if c == 1]
    print(f"  {name}: {len(V)} verts, {len(F)} faces, {len(boundary)} boundary edges (welded)")
    if not boundary: return False

    adj = defaultdict(list)
    for a, b in boundary:
        adj[a].append(b); adj[b].append(a)
    loops, seen = [], set()
    for start in adj:
        if start in seen: continue
        loop, cur, prev = [start], start, None
        seen.add(start)
        while True:
            nxt = next((n for n in adj[cur] if n != prev and n not in seen), None)
            if nxt is None: break
            loop.append(nxt); seen.add(nxt); prev, cur = cur, nxt
        loops.append(loop)

    centroid = tuple(sum(v[a] for v in V) / len(V) for a in range(3))
    added = []
    for loop in loops:
        if len(loop) < 3:
            print(f"    loop of {len(loop)} -- too small to cap, left open"); continue
        axis = next((a for a in range(3)
                     if max(V[i][a] for i in loop) - min(V[i][a] for i in loop) < TOL), None)
        if axis is None:
            print(f"    loop of {len(loop)} is NOT planar -- left open (damage, not an omission)"); continue
        plane = V[loop[0]][axis]
        sign = -1.0 if plane < centroid[axis] else 1.0
        nrm = [0.0, 0.0, 0.0]; nrm[axis] = sign
        # order the fan so its geometric normal agrees with `nrm`
        u, w = [a for a in range(3) if a != axis]
        area = sum(V[loop[k]][u] * V[loop[(k + 1) % len(loop)]][w]
                   - V[loop[(k + 1) % len(loop)]][u] * V[loop[k]][w] for k in range(len(loop)))
        # +area winds CCW in the (u,w) plane, whose normal is +axis for an even permutation
        ccw_is_plus = (axis == 1)   # (u,w) = (0,2) for axis 1 -> right-handed normal is -Y; see below
        want_ccw = (sign > 0) != ccw_is_plus
        if (area > 0) != want_ccw: loop = loop[::-1]
        added.append((loop, tuple(nrm)))
        ext = [f"{max(V[i][a] for i in loop) - min(V[i][a] for i in loop):.3f}" for a in range(3)]
        print(f"    capping loop of {len(loop)} on axis {'xyz'[axis]}={plane:.3f}, extent {'x'.join(ext)}, normal {tuple(nrm)}")
    if not added or dry: return False

    out = list(lines)
    nvn = len(VN)
    for loop, nrm in added:
        nvn += 1
        out.append("vn %.6f %.6f %.6f" % nrm)
        for k in range(1, len(loop) - 1):
            tri = [loop[0], loop[k], loop[k + 1]]
            toks = []
            for wi in tri:
                vi, ti = sample[wi]
                toks.append(f"{vi+1}/{ti+1}/{nvn}" if ti is not None else f"{vi+1}//{nvn}")
            out.append("f " + " ".join(toks))
    open(path, "w").write("\n".join(out) + "\n")
    print(f"    -> wrote {sum(len(l) - 2 for l, _ in added)} new triangles into {name}.obj")
    return True

args = [a for a in sys.argv[1:] if not a.startswith("--")]
dry = "--dry" in sys.argv
if not args:
    print(__doc__); raise SystemExit(2)
for n in args: cap(n, dry)
