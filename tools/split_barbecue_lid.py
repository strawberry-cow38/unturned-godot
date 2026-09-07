#!/usr/bin/env python3
"""Split Barbecue_1 into a body and an opening lid (master 2026-09-07).

The red-black barbecue ships as ONE mesh: four grey legs, a fused bowl+lid shell, and a black
handle sitting on top. The lid is the red half of that fused shell, and it separates on a clean
square ring -- so this cuts there, caps both new openings, and writes the lid out as a door mesh
the existing doors.txt machinery can hinge (the same path the dumpster lids use).

MEASURED, not assumed -- and asserted below so this fails loudly rather than writing nonsense if
the asset ever changes:
  * the 2x2 texture is a PALETTE: black (53,53,53), grey (150,150,150), red (133,35,35)
  * red faces span z 1.222..1.524, and share exactly FOUR vertices with the black faces
  * those four are a square ring at z=1.2216, x/y = +/-0.6053 -- the seam
  * the handle is its own shell at z 1.475..1.584, entirely above the seam, so it rides the lid

The originals are not lost: Barbecue_1_Ragdoll_0.obj is a vertex-identical copy of the whole
barbecue (140 verts, same bounds), which is what the break/debris path already uses.

    python3 tools/split_barbecue_lid.py [--check | --lod]

--check verifies the geometry and writes nothing; --lod redoes only the LOD1 cut.
"""
import sys, os
from collections import defaultdict

D = os.path.join(os.path.dirname(__file__), "..", "game", "content", "objects")
SRC = os.path.join(D, "Barbecue_1.obj")
LID = os.path.join(D, "Barbecue_1_lid.obj")
LOD = os.path.join(D, "Barbecue_1_lod1.obj")

RED, BLK, GREY = (133, 35, 35), (53, 53, 53), (150, 150, 150)
SEAM_Z = 1.2216
BLACK_UV = (0.25, 0.75)          # the palette cell that samples black -- the cut face is interior
TOL = 1e-3


def load(path):
    V, VT, VN, F, grp = [], [], [], [], None
    for line in open(path):
        t = line.split()
        if not t:
            continue
        if t[0] == "v":
            V.append(tuple(float(x) for x in t[1:4]))
        elif t[0] == "vt":
            VT.append(tuple(float(x) for x in t[1:3]))
        elif t[0] == "vn":
            VN.append(tuple(float(x) for x in t[1:4]))
        elif t[0] == "g":
            grp = t[1]
        elif t[0] == "f":
            idx = []
            for p in t[1:]:
                a = (p.split("/") + ["", ""])[:3]
                idx.append((int(a[0]) - 1,
                            int(a[1]) - 1 if a[1] else None,
                            int(a[2]) - 1 if a[2] else None))
            F.append((grp, idx))
    return V, VT, VN, F


def main():
    if "--lod" in sys.argv:      # the full-res split consumes its own input, so it is one-shot;
        split_lod(); return      # this re-runs only the LOD half against the seam it measured
    check = "--check" in sys.argv
    from PIL import Image
    im = Image.open(os.path.join(D, "Barbecue_1_tex.png")).convert("RGB")
    W, H = im.size
    V, VT, VN, F = load(SRC)

    def texel(ti):
        u, v = VT[ti]
        return im.getpixel((min(W - 1, int(u * W)), min(H - 1, int((1 - v) * H))))

    def colour(idx):
        c = [texel(ti) for _, ti, _ in idx if ti is not None]
        return max(set(c), key=c.count) if c else None

    # ---- connected shells, welded by rounded position (UV seams split verts that share a corner)
    par = {}
    def find(a):
        while par.setdefault(a, a) != a:
            par[a] = par[par[a]]; a = par[a]
        return a
    def uni(a, b):
        ra, rb = find(a), find(b)
        if ra != rb: par[ra] = rb
    key = lambda vi: tuple(round(c, 4) for c in V[vi])
    for _, idx in F:
        ks = [key(vi) for vi, _, _ in idx]
        for k in ks[1:]: uni(ks[0], k)
    shell = {}
    for fi, (_, idx) in enumerate(F):
        shell[fi] = find(key(idx[0][0]))

    reds = [fi for fi, (_, idx) in enumerate(F) if colour(idx) == RED]
    assert reds, "no red faces -- the palette or the asset changed"
    zr = [V[vi][2] for fi in reds for vi, _, _ in F[fi][1]]
    assert abs(min(zr) - SEAM_Z) < 1e-2, f"red starts at {min(zr):.4f}, expected the seam {SEAM_Z}"

    # the seam: vertices shared by a red face and a non-red one
    rv = {key(vi) for fi in reds for vi, _, _ in F[fi][1]}
    ov = {key(vi) for fi, (_, idx) in enumerate(F) if fi not in reds for vi, _, _ in idx}
    seam = sorted(rv & ov, key=lambda p: (p[0], p[1]))
    assert len(seam) == 4, f"expected a 4-vertex seam ring, got {len(seam)}"
    assert all(abs(p[2] - SEAM_Z) < TOL for p in seam), f"seam is not planar: {seam}"

    # the handle: any shell sitting entirely at or above the seam, and not the red lid itself
    redshells = {shell[fi] for fi in reds}
    handle = [fi for fi, (_, idx) in enumerate(F)
              if shell[fi] not in redshells
              and min(V[vi][2] for vi, _, _ in idx) >= SEAM_Z - TOL]
    lidset = set(reds) | set(handle)
    body = [fi for fi in range(len(F)) if fi not in lidset]

    print(f"seam ring z={SEAM_Z} at x/y +/-{abs(seam[0][0]):.4f}")
    print(f"lid   : {len(reds)} red + {len(handle)} handle = {len(lidset)} faces")
    print(f"body  : {len(body)} faces (bowl + legs)")
    zb = [V[vi][2] for fi in body for vi, _, _ in F[fi][1]]
    print(f"body z {min(zb):.3f}..{max(zb):.3f}   lid z "
          f"{min(V[vi][2] for fi in lidset for vi,_,_ in F[fi][1]):.3f}.."
          f"{max(V[vi][2] for fi in lidset for vi,_,_ in F[fi][1]):.3f}")
    assert max(zb) <= SEAM_Z + TOL, f"body reaches {max(zb):.4f}, above the seam -- the cut is wrong"
    if check:
        print("--check: geometry verified, nothing written")
        return

    ring = ring_of(seam)

    write_obj(LID,  V, VT, VN, F, sorted(lidset), ring, cap_up=False,
              note="lid (red cover + handle)")
    write_obj(SRC,  V, VT, VN, F, sorted(body),   ring, cap_up=True,
              note="body (bowl + legs)")
    split_lod()


def write_obj(path, V, VT, VN, F, faces, ring, cap_up, note):
    """Write `faces` out as their own .obj, capping the cut with a quad on `ring`."""
    vmap, tmap, nmap = {}, {}, {}
    vs, ts, ns, out = [], [], [], []
    def vi_(i):
        if i not in vmap: vmap[i] = len(vs) + 1; vs.append(V[i])
        return vmap[i]
    def ti_(i):
        if i not in tmap: tmap[i] = len(ts) + 1; ts.append(VT[i])
        return tmap[i]
    def ni_(i):
        if i not in nmap: nmap[i] = len(ns) + 1; ns.append(VN[i])
        return nmap[i]
    for fi in faces:
        out.append([(vi_(v), ti_(t) if t is not None else None,
                     ni_(n) if n is not None else None) for v, t, n in F[fi][1]])
    # THE MISSING FACES: cap the opening the cut just made. Up on the body (you look down into
    # the bowl when the lid is open), down on the lid (its underside). Black, since it is interior.
    ts.append(BLACK_UV); tcap = len(ts)
    ns.append((0.0, 0.0, 1.0 if cap_up else -1.0)); ncap = len(ns)
    quad = ring if cap_up else list(reversed(ring))
    cap = []
    for p in quad:
        vs.append(p); cap.append((len(vs), tcap, ncap))
    out.append(cap)
    with open(path, "w") as f:
        f.write("# generated by tools/split_barbecue_lid.py from Barbecue_1.obj\n")
        f.write(f"# {note}, cut at the red/black seam z={SEAM_Z}\n")
        f.write("g Model_0\n")
        for p in vs: f.write("v %.6f %.6f %.6f\n" % p)
        for p in ts: f.write("vt %.6f %.6f\n" % p)
        for p in ns: f.write("vn %.6f %.6f %.6f\n" % p)
        for fc in out:
            f.write("f " + " ".join(
                f"{v}/{t if t else ''}/{n if n else ''}" for v, t, n in fc) + "\n")
    print(f"wrote {path}: {len(vs)} verts, {len(out)} faces")


def ring_of(pts):
    """Order a seam ring by angle about its own centre, so the cap quad is not a bowtie."""
    import math
    cx = sum(p[0] for p in pts) / len(pts)
    cy = sum(p[1] for p in pts) / len(pts)
    return sorted(pts, key=lambda p: math.atan2(p[1] - cy, p[0] - cx))


def split_lod():
    """The same cut on the LOD1 mesh.

    Barbecue_1 IS in lods.txt (MEDIUM, size 1.765), so past the LOD0 band the body swaps to
    Barbecue_1_lod1.obj -- which still had the whole closed lid baked in. The door leaf keeps
    drawing to its own cull distance, so an OPEN barbecue would have shown its lid twice at
    range: once hinged open, once welded shut on the LOD. Cut the lid out of the LOD body and
    the leaf covers it at every distance.

    (Dumpster_3/Dumpster_4 have exactly this problem already -- their lod1 reaches z 1.473,
    over a lid that lives at 1.370..1.470. Not fixed here, but it is the same one line of work.)"""
    V, VT, VN, F = load(LOD)
    lid  = [fi for fi, (_, idx) in enumerate(F) if min(V[vi][2] for vi, _, _ in idx) >= SEAM_Z - TOL]
    body = [fi for fi, (_, idx) in enumerate(F) if max(V[vi][2] for vi, _, _ in idx) <= SEAM_Z + TOL]
    assert len(lid) + len(body) == len(F), "a LOD face straddles the seam -- the cut is not clean there"
    assert lid, "the LOD mesh has no lid above the seam"
    seam = {tuple(round(c, 4) for c in V[vi])
            for fi in lid for vi, _, _ in F[fi][1] if abs(V[vi][2] - SEAM_Z) < TOL}
    assert len(seam) == 4, f"expected a 4-vertex LOD seam ring, got {len(seam)}"
    print(f"lod1  : {len(lid)} lid faces cut, {len(body)} kept")
    write_obj(LOD, V, VT, VN, F, body, ring_of(seam), cap_up=True, note="LOD1 body (bowl + legs)")


if __name__ == "__main__":
    main()
