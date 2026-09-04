#!/usr/bin/env python3
"""UN-MIRROR the ripped vehicle meshes (strawberry 2026-09-04: "every vehicle's body mesh is mirrored horizontally").

Root cause: UnityPy's Mesh.export() already writes OBJ in a right-handed frame (it negates X and reverses the
winding), and extract_vehicle_mesh.py / extract_vehicle_part.py / extract_huey.py / extract_tank.py then negated Z
on top "for Godot" -- two reflections = a 180-degree turn about Y, i.e. the car faces -Z as intended but with its
left and right swapped. The prefab-derived constants (wheels, seats, steer, lamps) went through ONE flip (Z) and are
correct, which is why the driver sits on the left of a body whose door is on the left too.

Fix the DATA: negate X (positions + normals) and reverse the face winding of every body-frame mesh -- bodies, the
glass panes generated from them, the heli/plane bodies, the tank hull. Parts (steer, seats, lamps) are NOT touched:
the part ripper baked each node's true translation, so mirroring about X=0 would move them to the wrong side, and
their own geometry is symmetric about their node. Wheels are not touched either: Vehicle.cs mirrors them per side.

Hull bakes (content/vehicle_hulls/<fnv1a(key)>.hulls) are keyed on the body file SIZE, so they are mirrored too and
re-saved under the key the new file size produces.

  python3 tools/mirror_vehicle_meshes.py          # dry run: report
  python3 tools/mirror_vehicle_meshes.py --apply  # rewrite meshes + hull bakes
"""
import os, re, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTENT = os.path.join(ROOT, "game", "content")
HULLS = os.path.join(CONTENT, "vehicle_hulls")
APPLY = "--apply" in sys.argv

vehicle_cs = open(os.path.join(ROOT, "game", "Vehicle.cs"), encoding="utf-8").read()

def spec_blocks():
    for m in re.finditer(r'static readonly Spec (_\w+) = new\(\)\s*\{', vehicle_cs):
        start = m.end(); depth = 1; i = start
        while depth > 0:
            if vehicle_cs[i] == '{': depth += 1
            elif vehicle_cs[i] == '}': depth -= 1
            i += 1
        yield m.group(1), vehicle_cs[start:i]

SKIP = {"ship_body.txt"}   # a different pipeline (convert_ship.py); not proven mirrored -- left alone, flagged
files = {}   # file -> why
specs = []   # (spec, body, name)
for spec, body in spec_blocks():
    name = re.search(r'Name = "([^"]+)"', body)
    b = re.search(r'\bBody = "([^"]+\.txt)"', body)
    if b and b.group(1) not in SKIP: files[b.group(1)] = f"Body of {spec}"
    if b: specs.append((spec, b.group(1), name.group(1) if name else ""))
    g = re.search(r'GlassMesh = "([^"]+\.txt)"', body)
    if g: files[g.group(1)] = f"GlassMesh of {spec}"
    hb = re.search(r'HeliBodyMeshes = new\[\]\s*\{([^}]*)\}', body)
    if hb:
        for f in re.findall(r'"([^"]+\.txt)"', hb.group(1)): files[f] = f"HeliBodyMeshes of {spec}"

def mirror_obj(text):
    out = []
    for line in text.splitlines():
        p = line.split()
        if not p: out.append(line); continue
        if p[0] == "v" or p[0] == "vn":
            x, y, z = float(p[1]), float(p[2]), float(p[3])
            out.append(f"{p[0]} {-x:.6f} {y:.6f} {z:.6f}" if p[0] == "v" else f"vn {-x:.6f} {y:.6f} {z:.6f}")
        elif p[0] == "f":
            out.append("f " + " ".join(reversed(p[1:])))
        else:
            out.append(line)
    return "\n".join(out) + "\n"

def fnv1a(key):
    h = 14695981039346656037
    for c in key:
        h ^= ord(c); h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return f"{h:016x}"

def hull_key(body, name, size): return f"body|{body}|{name}|||{size}"

def side_counts(text, zlo, zhi, ylo, yhi, xedge):
    L = R = 0
    for line in text.splitlines():
        if not line.startswith("v "): continue
        x, y, z = map(float, line.split()[1:4])
        if zlo < z < zhi and ylo < y < yhi:
            if x < -xedge: L += 1
            elif x > xedge: R += 1
    return L, R

# GLASS PANES: per-pane files <base>_<label>.txt generated from the (mirrored) bodies -> mirror them too. A pane
# with a side in its label (l_front / r_rear ...) swaps files with its opposite so the label still names the TRUE side.
pane_swaps = []   # (left file, right file)
for f in list(files):
    if "glass" not in f: continue
    base = f[:-4]
    for pf in os.listdir(CONTENT):
        if pf.startswith(base + "_") and pf.endswith(".txt"):
            files[pf] = f"glass pane of {files[f]}"
            if "_l_" in pf:
                rf = pf.replace("_l_", "_r_", 1)
                if os.path.exists(os.path.join(CONTENT, rf)): pane_swaps.append((pf, rf))
    files.pop(f, None)   # the base name itself is not a file

# ---- report
present = [f for f in files if os.path.exists(os.path.join(CONTENT, f))]
print(f"{len(pane_swaps)} left/right pane pairs will swap files")
missing = [f for f in files if f not in present]
print(f"{len(present)} mesh files to mirror, {len(missing)} missing: {missing}")
for f in sorted(present): print(f"  {f:28s} {files[f]}")

bus = open(os.path.join(CONTENT, "bus_body.txt")).read()
print("bus door band before (left, right):", side_counts(bus, -3.2, -1.6, 0.0, 2.2, 1.2), "-> after:", side_counts(mirror_obj(bus), -3.2, -1.6, 0.0, 2.2, 1.2))

hull_files = set(os.listdir(HULLS)) if os.path.isdir(HULLS) else set()
plan = []   # (old file, new file, body)
for spec, body, name in specs:
    fp = os.path.join(CONTENT, body)
    if not os.path.exists(fp) or body in SKIP: continue
    old = fnv1a(hull_key(body, name, os.path.getsize(fp))) + ".hulls"
    if old in hull_files:
        new_size = len(mirror_obj(open(fp).read()).encode("utf-8")) if body in files else os.path.getsize(fp)
        new = fnv1a(hull_key(body, name, new_size)) + ".hulls"
        plan.append((old, new, body)); print(f"  hull bake {old} -> {new}  ({name})")
matched = {o for o, _, _ in plan}
print(f"{len(plan)} hull bakes matched of {len(hull_files)}; unmatched: {sorted(hull_files - matched)}")

if not APPLY:
    print("dry run -- pass --apply to write"); sys.exit(0)

# ---- apply: meshes (mirror in memory first, then write -- pane pairs swap destinations)
mirrored = {f: mirror_obj(open(os.path.join(CONTENT, f)).read()) for f in present}
swap_to = {}
for l, r in pane_swaps: swap_to[l] = r; swap_to[r] = l
for f, text in mirrored.items():
    open(os.path.join(CONTENT, swap_to.get(f, f)), "w").write(text)
print(f"rewrote {len(present)} meshes ({len(pane_swaps)} l/r pane pairs swapped)")
# ---- apply: hull bakes (negate X of every point), saved under the new key; old file removed
for old, new, body in plan:
    src = os.path.join(HULLS, old); dst = os.path.join(HULLS, new)
    data = open(src, "rb").read()
    hulls, = struct.unpack_from("<i", data, 0); o = 4; out = bytearray(struct.pack("<i", hulls))
    for _ in range(hulls):
        n, = struct.unpack_from("<i", data, o); o += 4; out += struct.pack("<i", n)
        for _ in range(n):
            x, y, z = struct.unpack_from("<fff", data, o); o += 12
            out += struct.pack("<fff", -x, y, z)
    open(dst, "wb").write(out)
    if old != new: os.remove(src)
    # sanity: the new key must match the mirrored body's real size
    assert new == fnv1a(hull_key(body, [n for s, b, n in specs if b == body][0], os.path.getsize(os.path.join(CONTENT, body)))) + ".hulls", body
print(f"re-keyed {len(plan)} hull bakes")
