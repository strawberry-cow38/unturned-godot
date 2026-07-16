#!/usr/bin/env python3
"""extract_punch_anims.py -- port the unarmed PUNCH animations (Punch_Left / Punch_Right) from the U3-SDK
source FBX into content/rig.json, so the bare-fists melee state plays the REAL src jab instead of the generic
melee swing.

Source: U3-SDK/Assets/Game/Sources/Animations/Characters/Human/Basic/Punch/Animation.fbx -- an FBX 6.x ASCII
file with two Takes (Punch_Left, Punch_Right, 20 frames each). The bone names (Spine, Left_Arm, Right_Arm, ...)
match our rig 1:1, so no bone remap is needed.

FBX->rig coordinate conversion (CALIBRATED, don't guess): the rig's existing clips came from resources.assets
(Unity-imported) -> rig_extract.py, so to match THAT convention exactly I converted the same Idle takes from
this FBX and diffed against rig.json. Result (verified to 0.0 quaternion error on Idle_Prone/Crouch/Reclined,
which have non-trivial X/Y/Z euler):
  rotation:  euler(deg, XYZ order) -> quat, then NEGATE qz  ->  (qx, qy, -qz, qw)
  position:  NEGATE x               ->  (-x, y, z)
Then the same root-tip fix rig_extract uses: neutralise the Skeleton track (pre-multiply by inverse(frame0))
so the rig un-tips into the mesh's bind convention.

Verified: --rig=DIR --anim=Punch_Left|Punch_Right renders the character throwing the correct (mirrored) jab.
"""
import re, json, math, os

FBXTC = 46186158000.0   # FBX time ticks per second
FBX = os.environ.get("PUNCH_FBX",
    "/home/ec2-user/projects/U3-SDK/Assets/Game/Sources/Animations/Characters/Human/Basic/Punch/Animation.fbx")
RIG = os.environ.get("RIG_JSON",
    "/home/ec2-user/projects/unturned-godot/game/content/rig.json")

lines = open(FBX).read().splitlines()

def take_block(name):
    out = []; depth = 0; inside = False
    for ln in lines:
        if not inside:
            if re.search(r'Take:\s*"%s"\s*\{' % re.escape(name), ln): inside = True; depth = 1; continue
        else:
            depth += ln.count('{') - ln.count('}')
            if depth <= 0: break
            out.append(ln)
    return out

def parse_take(name):
    blk = take_block(name); models = {}
    cm = ct = ca = None
    def store(m, t, a, keys): models.setdefault(m, {}).setdefault(t, {})[a] = keys
    i = 0
    while i < len(blk):
        s = blk[i].strip()
        m = re.match(r'Model:\s*"Model::([^"]+)"', s)
        if m: cm = m.group(1); ct = ca = None
        elif re.match(r'Channel:\s*"(T|R|S)"', s): ct = re.match(r'Channel:\s*"(T|R|S)"', s).group(1); ca = None
        elif re.match(r'Channel:\s*"(X|Y|Z)"', s): ca = re.match(r'Channel:\s*"(X|Y|Z)"', s).group(1)
        elif s.startswith('Default:') and cm and ct and ca:
            store(cm, ct, ca, [(0.0, float(s.split(':')[1].strip()))])
        elif s.startswith('Key:') and cm and ct and ca:
            keys = []; j = i + 1
            while j < len(blk):
                ks = blk[j].strip().rstrip(',')
                if not ks or ks.startswith(('Color:', 'LayerType', 'Channel', '}', 'KeyVer', 'KeyCount')): break
                p = ks.split(',')
                if len(p) >= 2:
                    try: keys.append((float(p[0]), float(p[1])))
                    except ValueError: pass
                j += 1
            store(cm, ct, ca, keys); i = j - 1
        i += 1
    return models

def sample(ks, t):
    if not ks: return 0.0
    if len(ks) == 1: return ks[0][1]
    if t <= ks[0][0]: return ks[0][1]
    if t >= ks[-1][0]: return ks[-1][1]
    for k in range(len(ks) - 1):
        t0, v0 = ks[k]; t1, v1 = ks[k + 1]
        if t0 <= t <= t1: f = (t - t0) / (t1 - t0) if t1 > t0 else 0; return v0 + (v1 - v0) * f
    return ks[-1][1]

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qinv(q): return [-q[0], -q[1], -q[2], q[3]]
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx, ty, tz = 2*(y*vz-z*vy), 2*(z*vx-x*vz), 2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
def euler_xyz(rx, ry, rz):
    hx, hy, hz = math.radians(rx)/2, math.radians(ry)/2, math.radians(rz)/2
    qx = (math.sin(hx), 0, 0, math.cos(hx)); qy = (0, math.sin(hy), 0, math.cos(hy)); qz = (0, 0, math.sin(hz), math.cos(hz))
    return qmul(qmul(qx, qy), qz)

def convert(take):
    models = parse_take(take); tracks = {}; length = 0.0
    for bone, ch in models.items():
        R = ch.get('R', {}); T = ch.get('T', {})
        times = sorted({tt for a in 'XYZ' for (tt, _) in R.get(a, []) + T.get(a, [])}) or [0.0]
        rot = []; pos = []
        for tt in times:
            q = euler_xyz(sample(R.get('X', []), tt), sample(R.get('Y', []), tt), sample(R.get('Z', []), tt))
            sec = tt / FBXTC
            rot.append([sec, q[0], q[1], -q[2], q[3]])   # calibrated: negate qz
            pos.append([sec, -sample(T.get('X', []), tt), sample(T.get('Y', []), tt), sample(T.get('Z', []), tt)])  # negate x
            length = max(length, sec)
        tracks[bone] = {'rot': rot, 'pos': pos}
    sk = tracks.get('Skeleton')   # root-tip fix (same as rig_extract): frame0 -> identity
    if sk and sk.get('rot'):
        K = qinv(sk['rot'][0][1:5])
        sk['rot'] = [[k[0]] + qmul(K, k[1:5]) for k in sk['rot']]
        if sk.get('pos'): sk['pos'] = [[k[0]] + qrot(K, k[1:4]) for k in sk['pos']]
    return {'fps': 30.0, 'length': length, 'loop': False, 'tracks': tracks}

if __name__ == "__main__":
    rig = json.load(open(RIG))
    for nm in ("Punch_Left", "Punch_Right"):
        rig['anims'][nm] = convert(nm)
        print(f"  {nm}: len={rig['anims'][nm]['length']:.3f}s bones={len(rig['anims'][nm]['tracks'])}")
    json.dump(rig, open(RIG, "w"))
    print(f"merged Punch_Left/Right into {RIG} ({len(rig['anims'])} clips)")
