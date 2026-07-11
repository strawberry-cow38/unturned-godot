import struct
NAMES = ["Highway_0", "Highway_1", "Racetrack", "Road", "Tracks", "Trail", "White_0", "White_1", "Yellow_0", "Yellow_1"]
d = open("/tmp/pei_paths.dat", "rb").read()
st = [0]
def u8(): v = d[st[0]]; st[0] += 1; return v
def u16(): v = struct.unpack_from("<H", d, st[0])[0]; st[0] += 2; return v
def f32(): v = struct.unpack_from("<f", d, st[0])[0]; st[0] += 4; return v
def vec3(): return (f32(), f32(), f32())
version = u8()
count = u16()
STRAT = (-72.0, 534.0)   # unity xz near the Stratford render spawn (godot -72,-534)
roads = []
for r in range(count):
    length = u16(); material = u8()
    if version > 2: u8()   # isLoop
    if version >= 6: gl = u16(); st[0] += gl
    joints = []
    for j in range(length):
        p = vec3()
        if version > 2: st[0] += 24
        if version > 2: u8()
        if version > 4: f32()
        if version > 3: u8()
        joints.append(p)
    roads.append((r, material, joints))
by_mat = {}
for r, material, joints in roads:
    by_mat[material] = by_mat.get(material, 0) + 1
    if not joints:
        continue
    cx = sum(j[0] for j in joints) / len(joints)
    cz = sum(j[2] for j in joints) / len(joints)
    dist = ((cx - STRAT[0])**2 + (cz - STRAT[1])**2) ** 0.5
    nm = NAMES[material] if material < len(NAMES) else "?"
    print(f"road {r:2}: mat {material} ({nm:9}), {len(joints):2} joints, center=({cx:6.0f},{cz:6.0f}), dStratford={dist:.0f}")
print("material usage:", {NAMES[m] if m < len(NAMES) else m: c for m, c in sorted(by_mat.items())})
near = min((r for r in roads if r[2]), key=lambda r: ((sum(j[0] for j in r[2]) / len(r[2]) - STRAT[0])**2 + (sum(j[2] for j in r[2]) / len(r[2]) - STRAT[1])**2))
print(f"NEAREST to Stratford render spawn: road {near[0]}, mat {near[1]} = {NAMES[near[1]] if near[1] < len(NAMES) else '?'}")
for want, label in [(None, "ANY"), (0, "HIGHWAY_0")]:
    best = None
    for r, material, joints in roads:
        if want is not None and material != want:
            continue
        for i in range(len(joints) - 1):
            a = joints[i]; b = joints[i + 1]
            hd = ((a[0] - b[0])**2 + (a[2] - b[2])**2) ** 0.5
            if hd > 3:
                slope = abs(a[1] - b[1]) / hd
                if best is None or slope > best[0]:
                    best = (slope, r, (a[0] + b[0]) / 2, (a[2] + b[2]) / 2, a[1], b[1], NAMES[material] if material < len(NAMES) else material)
    if best:
        print(f"STEEPEST {label}: road {best[1]} ({best[6]}), slope {best[0]:.2f}, GODOT spawn ({best[2]:.0f},{-best[3]:.0f}), Y {best[4]:.0f}->{best[5]:.0f}")
print("--- endpoints (godot xz) ---")
for r, material, joints in roads:
    if len(joints) < 2:
        continue
    a = joints[0]; b = joints[-1]
    looped = (abs(a[0] - b[0]) < 1 and abs(a[2] - b[2]) < 1)
    print(f"road {r:2} ({NAMES[material] if material < len(NAMES) else material:9}): start ({a[0]:.0f},{-a[2]:.0f}) Y{a[1]:.0f} | end ({b[0]:.0f},{-b[2]:.0f}) Y{b[1]:.0f}{'  [loop]' if looped else ''}")
