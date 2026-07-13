import struct
data = open(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Environment\Nodes.dat", "rb").read()
p = 0
def rb():
    global p; v = data[p]; p += 1; return v
def rf():
    global p; v = struct.unpack_from("<f", data, p)[0]; p += 4; return v
def rv3():
    return (rf(), rf(), rf())
def ru16():
    global p; v = struct.unpack_from("<H", data, p)[0]; p += 2; return v
def ru32():
    global p; v = struct.unpack_from("<I", data, p)[0]; p += 4; return v
def rstr():
    global p; n = data[p]; p += 1; s = data[p:p + n].decode("utf-8", "replace"); p += n; return s
# ENodeType order (LevelNodes.load check order)
LOCATION, SAFEZONE, PURCHASE, ARENA, DEADZONE, AIRDROP, EFFECT = 0, 1, 2, 3, 4, 5, 6
version = rb(); count = rb()
print(f"version={version} count={count}")
locs = []
for i in range(count):
    pt = rv3(); t = rb()
    if t == LOCATION:
        locs.append((rstr(), pt))
    elif t == SAFEZONE:
        rf()
        if version > 1: rb()
        if version > 4: rb()
        if version > 4: rb()
    elif t == PURCHASE:
        rf(); ru16(); ru32()
    elif t == ARENA:
        rf()
    elif t == DEADZONE:
        rf()
        if version > 6: rb()
    elif t == AIRDROP:
        ru16()
    else:
        print(f"!! unknown/unhandled type {t} at node {i}, byte {p}"); break
for name, pt in locs:
    print(f"  \"{name}\"  ({pt[0]:.1f}, {pt[1]:.1f}, {pt[2]:.1f})")
open(r"C:\claude-workspace\unturned-godot\game\content\nodes.tsv", "w", encoding="utf-8").write(
    "\n".join(f"{name}\t{pt[0]:.2f},{pt[1]:.2f},{-pt[2]:.2f}" for name, pt in locs) + "\n")   # Godot space: (x, y, -z)
print(f"{len(locs)} LOCATION nodes; consumed {p}/{len(data)} bytes; wrote content/nodes.tsv")
