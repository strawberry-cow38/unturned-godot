#!/usr/bin/env python3
"""Take the welded lid off the wheelie bins' LOD1 meshes.

Dumpster_3/_4 now carry a hinged lid (content/objects/doors.txt) built from their own Dumpster_N_Ragdoll_0.obj.
Their LOD1 body was still modelled WITH the lid on: it reaches z 1.473, the lid's top, while the full-detail body
stops at 1.373. So past the LOD band an open bin still showed a shut lid (tinyclaw, 2026-09-07).

⚠ THERE IS NO SEAM TO CUT, which is why the obvious fix is wrong. The LOD is coarse -- its z planes are
-0.076 / 0.0 / 0.043 / 0.281 / 0.4 / 1.473 and there is NO ring at the 1.373 body line at all. The upper wall is
one span from 0.4 straight to the lid's top, so deleting the faces that touch the lid deletes the entire top half
of the bin and leaves a stub. (Tried it: 52 faces -> 44, capped at z=0.400.)

So the lid comes off by SHORTENING instead: every vertex sitting at the lid's top drops to the real body height.
Topology untouched, the LOD becomes a lidless tub of the right height, and the lid is only ever the hinged leaf.
"""
import sys, os

LID_TOP  = 1.473   # where the LOD's welded lid ends
BODY_TOP = 1.373   # where the full-detail body actually stops (Dumpster_3.obj)

def cut(path):
    out, moved = [], 0
    for line in open(path):
        if line.startswith("v "):
            x, y, z = (float(v) for v in line.split()[1:4])
            if abs(z - LID_TOP) < 1e-3: z = BODY_TOP; moved += 1
            out.append("v %.6f %.6f %.6f\n" % (x, y, z))
        else:
            out.append(line)
    if moved == 0: return 0
    out.insert(1, "# LID REMOVED by tools/cut_bin_lod_lid.py -- shortened to the body line; the lid is the hinged leaf.\n")
    open(path, "w").writelines(out)
    return moved

for name in (sys.argv[1:] or ["game/content/objects/Dumpster_3_lod1.obj", "game/content/objects/Dumpster_4_lod1.obj"]):
    n = cut(name)
    print("  %-30s %s" % (os.path.basename(name), ("%d verts lowered %.3f -> %.3f" % (n, LID_TOP, BODY_TOP)) if n else "already at the body line"))
