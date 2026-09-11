#!/usr/bin/env python3
"""extract_gesture_anims.py -- rip the NINE 3rd-person Gesture_* clips into rig.json's `anims`.

WHY THEY WERE MISSING. rig.json ships 575 clips and not one gesture: every one of them is a weapon clip
(per-gun Equip/Reload/Aim/...) plus Attack_n. The gesture clips are not in core.masterbundle at all -- they
live in Unturned_Data/resources.assets, which is where rig_extract.py's clip dump comes from, so they were
simply never pulled. Probed both sources before assuming: core.masterbundle has 717 AnimationClips and zero
matching, resources.assets has 85 and all nine.

    Gesture_Arrest  Gesture_Facepalm  Gesture_Inventory  Gesture_Pickup
    Gesture_Point   Gesture_Rest      Gesture_Salute     Gesture_Surrender  Gesture_Wave

There is NO Gesture_T_Pose: EPlayerGesture.T_POSE_START is the bind pose itself, and PUNCH_LEFT/RIGHT reuse
the Attack clips rig.json already has. So nine files is the whole set, not a sample of it.

⚠ MERGES, never regenerates. rig_extract.py rebuilds the entire rig -- mesh, skin, ragdoll, anims -- and the
FP arms in there were merged in separately by merge_fp_arms.py. Re-running it to gain nine clips would throw
that away. Same rule as gen_item_catalog.py.

CONVENTION. Copied from rig_extract.py:parse_clip, NOT from the throwable/consumable converter. They differ in
one place that matters: the arms path zeroes the Skeleton's POSITION track (it is arms-only and the body root
is meaningless there), while the body rig keeps it, rotated by the same root correction. Using the arms
version here would pin every gesture to the origin.

Run on the box:  python tools/extract_gesture_anims.py
"""
import UnityPy, json, os

RES = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Unturned_Data\resources.assets"
RIG = r"C:\claude-workspace\unturned-godot\game\content\rig.json"

# Retail play(name, loop) -- UseableX and PlayerAnimator.ReceiveGesture agree on which of these are STATES you
# stay in until a STOP arrives, and which are one-shots that end themselves.
LOOPING = {"Gesture_Inventory", "Gesture_Surrender", "Gesture_Arrest", "Gesture_Rest"}
ONESHOT = {"Gesture_Pickup", "Gesture_Point", "Gesture_Wave", "Gesture_Salute", "Gesture_Facepalm"}
WANT = LOOPING | ONESHOT

def qinv(q): x, y, z, w = q; return [-x, -y, -z, w]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz-z*vy); ty = 2*(z*vx-x*vz); tz = 2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
def xyzw(v):
    if hasattr(v, "x"): return float(v.x), float(v.y), float(v.z), float(getattr(v, "w", 0.0))
    return float(v["x"]), float(v["y"]), float(v["z"]), float(v.get("w", 0.0))
def keyframes(cur):
    curve = getattr(cur, "curve", None) or getattr(cur, "m_Curve", None)
    return (getattr(curve, "m_Curve", None) if curve is not None else None) or []

def convert(cl, loop):
    fps = float(getattr(cl, "m_SampleRate", 30.0) or 30.0)
    tracks = {}
    # Bone NAME, not path. rig.json's bones are uniquely named (Left_Hand and Right_Hand are distinct nodes),
    # and rig_extract.py keys its tracks the same way -- so a clip merged here addresses the same skeleton the
    # 575 existing ones do. Keeping the path would make these nine the odd ones out.
    for cur in (getattr(cl, "m_RotationCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time), -x, -y, z, w] for kf in keyframes(cur) for (x, y, z, w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["rot"] = keys
    for cur in (getattr(cl, "m_PositionCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time), x, y, -z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["pos"] = keys
    for cur in (getattr(cl, "m_ScaleCurves", None) or []):
        bn = str(cur.path).split("/")[-1]
        keys = [[float(kf.time), x, y, z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["scale"] = keys
    # Root-convention fix, rig_extract.py's version: the bind poses are baked with Skeleton=identity but the
    # clips are authored with the root tipped -90 X. Pre-multiplying by inverse(frame 0) un-tips the whole rig;
    # children inherit it and keep their motion. The POSITION track is rotated by the same correction and KEPT.
    sk = tracks.get("Skeleton")
    if sk and sk.get("rot"):
        K = qinv(sk["rot"][0][1:5])
        sk["rot"] = [[k[0]] + qmul(K, k[1:5]) for k in sk["rot"]]
        if sk.get("pos"): sk["pos"] = [[k[0]] + qrot(K, k[1:4]) for k in sk["pos"]]
    length = 0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length = max(length, arr[-1][0])
    return {"fps": fps, "length": length, "tracks": tracks, "loop": loop}

env = UnityPy.load(RES)
found = {}
for o in env.objects:
    if o.type.name != "AnimationClip": continue
    cl = o.read()
    nm = getattr(cl, "m_Name", None)
    if nm in WANT and nm not in found: found[nm] = cl

missing = sorted(WANT - set(found))
if missing: raise SystemExit(f"NOT FOUND in resources.assets: {missing} -- refusing to merge a partial set")

rig = json.load(open(RIG))
anims = rig["anims"]
before = len(anims)
def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o
for nm in sorted(found):
    c = convert(found[nm], nm in LOOPING)
    # Every one of these has to move the SPINE -- they are upper-body poses. A clip that does not is not the
    # human gesture we think it is, and rig_extract.py drops such clips for the same reason.
    if "rot" not in c["tracks"].get("Spine", {}):
        raise SystemExit(f"{nm} has no Spine rotation -- that is not a human gesture clip, refusing to merge")
    anims[nm] = rnd(c)
    print(f"  {nm:20s} {c['length']:.3f}s @ {c['fps']:.0f}fps  loop={c['loop']}  bones={len(c['tracks'])}")
# DEFAULT separators, matching the formatting rig_extract.py wrote. Dumping compact changed every one of
# 22 MB of bytes to add nine clips: an unreadable diff and a blob git has to store whole again, for a
# saving nobody asked for. The values were identical either way -- which is how I know it was only that.
json.dump(rig, open(RIG, "w"))
print(f"rig.json anims {before} -> {len(anims)} ({os.path.getsize(RIG)} bytes)")
