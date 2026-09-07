#!/usr/bin/env python3
"""Put the crossing boom's hinge block ON the post (master 2026-09-07: "move the block pivot point to be
attached to the post on the crossing props").

WHAT WAS WRONG. tools/extract_crossing_arm.py pulls the boom out of the Hinge_0 SkinnedMeshRenderer with an
IDENTITY matrix, deliberately -- baking the renderer node's full transform put the beam at ground level and
turned it sideways, so the node transform was dropped whole. Dropping it whole also dropped its 2.5 m
translation, and the boom landed 2.5 m off along its own length. What that looks like in the shipped asset:

    post  (Crossing_1.obj)          x -2.902 .. -2.598      z 0.000 .. 1.500
    block (the beam's end box)      x -5.460 .. -5.024      z 1.112 .. 1.548
    beam  (a plain 0.27 x 0.11 bar) x -5.110 ..  5.159      z 1.190 .. 1.460

The post's top 0.31 m passes straight THROUGH the middle of a plain bar, and the block -- a 0.44 m box
capping the beam's end, which is the hinge housing -- hangs 2.1 m clear of anything, in mid-air.

THE FIX is a pure +2.5 m translation along X, restoring the magnitude the extractor dropped. It lands the
block centre at -2.742 against the post centre at -2.750: 8 mm apart, with the post's top 0.388 m inside the
housing, which is how a boom is actually mounted. Nothing else about the mesh changes.

The DIRECTION (+X) was fitted, not derived -- the Unity axis mapping is not checkable from this box, and
extract_crossing_arm.py names the offset two different ways in the same file ((0,0,2.5) in its docstring,
(0,-2.5,0) in the code comment). What is not fitted is the MAGNITUDE: 2.5 is the number Unity carries and
the number that puts the housing on the post, and no other axis or sign lands anywhere near it.

NOT USED AS EVIDENCE: the lods.txt size (11.611). It decodes to an x-span of 11.50 m, which neither the
shifted nor the unshifted arrangement gives (both are 10.62), so the LODGroup is evidently bounding
something else as well and the number cannot arbitrate between them. Recording that here so nobody re-derives
it and thinks it means something.

    python3 tools/attach_crossing_arm.py [--check]
"""
import os, sys

D = os.path.join(os.path.dirname(__file__), "..", "game", "content", "objects")
SHIFT_X = 2.5
ARMS = ["Crossing_1_Hinge_0_door.obj", "Crossing_2_Hinge_0_door.obj"]
POSTS = {"Crossing_1_Hinge_0_door.obj": "Crossing_1.obj",
         "Crossing_2_Hinge_0_door.obj": "Crossing_2.obj"}
TOL = 0.05   # the block and the post are 8 mm apart when this is right; 50 mm is a loose gate on a 0.30 m post


def verts(path):
    return [tuple(float(x) for x in l.split()[1:4]) for l in open(path) if l.startswith("v ")]


def block_centre(V):
    """The hinge housing: the box capping the beam's low-X end. Picked by HEIGHT, not by x -- it is the only
    part of the arm that reaches outside the beam's own 1.190..1.460 band, so selecting on z finds its faces
    exactly. (Selecting on x instead sweeps in the beam verts that start inside the housing and drags the
    centre 33 mm off, which is most of the number this script is checking.)"""
    zlo = min(p[2] for p in V) + 1e-3
    zhi = max(p[2] for p in V) - 1e-3
    blk = [p for p in V if p[2] <= zlo or p[2] >= zhi]
    return (min(p[0] for p in blk) + max(p[0] for p in blk)) / 2.0


def main():
    check = "--check" in sys.argv
    for arm in ARMS:
        ap, pp = os.path.join(D, arm), os.path.join(D, POSTS[arm])
        V, P = verts(ap), verts(pp)
        post_c = (min(p[0] for p in P) + max(p[0] for p in P)) / 2.0
        before = block_centre(V)
        after = before + SHIFT_X
        gap = abs(after - post_c)
        # ALREADY DONE. This script is not idempotent -- running it twice would push the boom another 2.5 m --
        # so say that plainly rather than failing with "the shift does not fit", which is true but misleading.
        if abs(before - post_c) < TOL:
            print(f"{arm}: block is ALREADY on the post ({before:.3f} vs {post_c:.3f}) -- nothing to do")
            continue
        print(f"{arm}: block {before:.3f} -> {after:.3f}, post {post_c:.3f}, gap {gap:.3f} m")
        assert gap < TOL, f"a +{SHIFT_X} shift does NOT put the block on the post (gap {gap:.3f} m) -- stop"
        if check:
            continue
        out = []
        for l in open(ap):
            if l.startswith("v "):
                t = l.split()
                out.append("v %.6f %s %s\n" % (float(t[1]) + SHIFT_X, t[2], t[3]))
            else:
                out.append(l)
        open(ap, "w").writelines(out)
        print(f"  wrote {arm}")
    if check:
        print("--check: geometry verified, nothing written")
    else:
        print(f"\nNow update doors.txt: the pivot X moves by the same {SHIFT_X} "
              f"(-5.242036 -> {-5.242036 + SHIFT_X:.6f}). The Y and Z are the block's own centre and do not move.")


if __name__ == "__main__":
    main()
