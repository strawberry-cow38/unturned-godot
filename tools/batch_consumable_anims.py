#!/usr/bin/env python3
"""Extract the DISTINCT consumable Equip/Use FP anim clips (36 each, shared archetypes) into rig.json, and write
content/consumable_anims.tsv (mesh<TAB>equipClip<TAB>useClip<TAB>useLen). Source: UseableConsumeable plays each
item's own "Equip"/"Use" (useTime = Use clip length). Reuses extract_consumable_anims.py's Unity->Godot convert()."""
import UnityPy, json, os, re
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CANIMS = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.json"
TSV = r"C:\claude-workspace\unturned-godot\game\content\consumable_anims.tsv"
APARTS = r"C:\claude-workspace\unturned-godot\game\content\consumable_animparts.tsv"
env = UnityPy.load(MB)
cont = dict(env.container)

def qinv(q): x, y, z, w = q; return [-x, -y, -z, w]
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz]
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz-z*vy); ty = 2*(z*vx-x*vz); tz = 2*(x*vy-y*vx)
    return [vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx)]
# ⚠ THE HIERARCHY IS THE ANIMATION (strawberry 2026-09-10: "now its.. invsibile?").
# A retail curve path is
#     Skeleton/Spine/Right_Shoulder/Right_Arm/Right_Hand/Right_Hook/13/Bone_0
# where "13" is the ITEM ID: Unturned instances the equipable prefab under the hand hook and renames it to the
# item's id, and THAT node carries its own Pos/Rot/Scale curves. It is what places the whole item assembly
# relative to the hand -- the bag your other hand reaches into does not travel with the fist that holds it.
#
# This tool used to do `str(cur.path).split("/")[-1]`, which kept only the leaf. Three bugs came out of that one
# line: the item-root track survived as a track literally named "13" pointing at a node nobody built (dropped);
# Bone_n keys, which are LOCAL TO THAT ROOT, got applied straight to the hand so the hand's motion was counted
# twice; and the hook SIDE was lost, so bag_chips -- a LEFT hook item -- was hung off the right hand.
#
# So: canonicalise the id segment to "Item_Root" and keep the leaf below it, which makes the clip's names match a
# hierarchy the game can actually build (GunAttach/Item_Root/<part>), and report which hook it hangs from.
HOOKRE = re.compile(r"/(Left|Right)_Hook/([^/]+)(/.*)?$")
def tname(p):
    """The track name is the item-relative PATH, not the leaf. Godot builds one Animation per clip and shares it
    across every character, so the path cannot depend on which item is held -- but it does not have to: a clip
    archetype is one prefab SHAPE, so a relative path means the same thing to every item sharing it -- emitting
    "Item_Root/Bone_5/Bone_6" stays correct for all of them, while a leaf "Bone_6" would need a per-item lookup
    that a shared resource cannot have.

    ⚠ The precise version, because the loose one ("a clip IS one prefab") is FALSE and I shipped it as the
    justification: sharers carry different SUBSETS. CU_30 drives a Model_1 that none of its 20 sandwiches has, and
    canned_pasta lacks the Stat_Tracker that canned_beans has on the same clip -- 23 such dead tracks across 75
    items, which resolve to nothing and do nothing. What actually holds, and what the scheme needs, is weaker and
    true: wherever two sharers both HAVE a part, they agree on its parent. Verified across all 14 shared clips --
    zero conflicts -- and asserted by viewmodel.held_part_hierarchy so it stays that way."""
    m = HOOKRE.search(str(p))
    if not m: return str(p).split("/")[-1]
    return "Item_Root" if not m.group(3) else "Item_Root/" + m.group(3).strip("/")
def hook_of(cl):
    for a in ("m_RotationCurves", "m_PositionCurves", "m_ScaleCurves"):
        for cur in (getattr(cl, a, None) or []):
            m = HOOKRE.search(str(cur.path))
            if m: return m.group(1)
    return ""
def deep_warn(cl):
    """A part nested TWO levels under the item root would need its own parent chain; none exist today, but say so
    loudly rather than silently flattening it the way the old code did."""
    out = set()
    for a in ("m_RotationCurves", "m_PositionCurves", "m_ScaleCurves"):
        for cur in (getattr(cl, a, None) or []):
            m = HOOKRE.search(str(cur.path))
            if m and m.group(3) and len(m.group(3).strip("/").split("/")) > 1: out.add(str(cur.path))
    return out

def xyzw(v):
    if hasattr(v, "x"): return float(v.x), float(v.y), float(v.z), float(getattr(v, "w", 0.0))
    return float(v["x"]), float(v["y"]), float(v["z"]), float(v.get("w", 0.0))
def keyframes(cur):
    curve = getattr(cur, "curve", None) or getattr(cur, "m_Curve", None)
    kfs = getattr(curve, "m_Curve", None) if curve is not None else None
    return kfs or []
def convert(cl):
    fps = float(getattr(cl, "m_SampleRate", 30.0) or 30.0)
    tracks = {}
    for cur in (getattr(cl, "m_RotationCurves", None) or []):
        bn = tname(cur.path)
        keys = [[float(kf.time)] + [(-x), (-y), z, w] for kf in keyframes(cur) for (x, y, z, w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["rot"] = keys
    for cur in (getattr(cl, "m_PositionCurves", None) or []):
        bn = tname(cur.path)
        keys = [[float(kf.time), x, y, -z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["pos"] = keys
    # ⚠ SCALE CURVES, which this tool never read (strawberry 2026-09-10: "the animation is playing but its still
    # wrong"). Retail hides a sub-piece by parking it at ~0.001 rest scale and SCALES IT UP in the Use clip when it
    # should appear -- the handful of chips, the bag flaps. Dropping these curves left those parts 1000x too small
    # and permanently invisible, so the clip played with half its content missing and nothing looked broken.
    # Scale needs no axis fix: the X/Z negation is a frame change, and per-axis magnitudes do not flip under one.
    for cur in (getattr(cl, "m_ScaleCurves", None) or []):
        bn = tname(cur.path)
        keys = [[float(kf.time), x, y, z] for kf in keyframes(cur) for (x, y, z, _w) in [xyzw(kf.value)]]
        if keys: tracks.setdefault(bn, {})["scale"] = keys
    sk = tracks.get("Skeleton")
    if sk and sk.get("rot"):
        K = qinv(sk["rot"][0][1:5])
        sk["rot"] = [[k[0]] + qmul(K, k[1:5]) for k in sk["rot"]]
        if sk.get("pos"): sk["pos"] = [[k[0]] + qrot(K, k[1:4]) for k in sk["pos"]]
    if sk and sk.get("pos"): sk["pos"] = [[k[0], 0.0, 0.0, 0.0] for k in sk["pos"]]
    length = 0.0
    for d in tracks.values():
        for arr in d.values():
            if arr: length = max(length, arr[-1][0])
    return {"fps": fps, "length": length, "tracks": tracks, "loop": False}

def item_clips(go):
    out = {}
    for entry in (getattr(go, "m_Component", None) or []):
        pptr = getattr(entry, "component", None)
        if pptr is None: continue
        comp = pptr.read()
        tn = comp.object_reader.type.name if hasattr(comp, "object_reader") else ""
        if "Animation" not in tn: continue
        for cp in (getattr(comp, "m_Animations", None) or []):
            cl = cp.read(); out[cl.m_Name] = cl
    return out

# gather each item's Equip/Use clip objects (keyed by clip path_id for dedupe)
items = {}
for path, obj in cont.items():
    m = re.search(r"items/(food|medical|water|refills)/([^/]+)/animations\.prefab$", str(path).lower())
    if m and obj.type.name == "GameObject":
        items[m.group(2)] = item_clips(obj.read())

canims = {}
use_idx = {}; eq_idx = {}; rows = []
def keyfor(cl, table, prefix):
    pid = cl.object_reader.path_id
    if pid not in table:
        idx = len(table); table[pid] = (idx, convert(cl))
    return f"{prefix}{table[pid][0]}"
aparts = []
deep = set()
for nm, cl in sorted(items.items()):
    ek = keyfor(cl["Equip"], eq_idx, "CE_") if "Equip" in cl else ""
    uk = keyfor(cl["Use"], use_idx, "CU_") if "Use" in cl else ""
    ulen = use_idx[cl["Use"].object_reader.path_id][1]["length"] if "Use" in cl else 0.0
    # Which hook, and which parts the clips actually drive. Both clips must agree on the hook; they are the same
    # equipable prefab, so a disagreement means the assumption is wrong and I want to see it rather than pick one.
    hooks = {hook_of(c) for c in cl.values() if hook_of(c)}
    hook = sorted(hooks)[0] if hooks else ""
    if len(hooks) > 1: print(f"  !! {nm}: clips disagree on hook {sorted(hooks)}")
    for c in cl.values(): deep |= deep_warn(c)
    # The animated set is what decides LOD-vs-part in the game: a Model_n the clips drive is a PIECE (canned_beans
    # animates Model_2 and Model_3), a Model_n they never mention is a real LOD. Guessing by index was wrong.
    names = set()
    for c in cl.values():
        for a in ("m_RotationCurves", "m_PositionCurves", "m_ScaleCurves"):
            for cur in (getattr(c, a, None) or []):
                if HOOKRE.search(str(cur.path)): names.add(tname(cur.path).split("/")[-1])
    rows.append((nm, ek, uk, round(ulen, 3), hook))
    if names: aparts.append((nm, ",".join(sorted(names))))
for pid, (idx, data) in use_idx.items(): canims[f"CU_{idx}"] = data
for pid, (idx, data) in eq_idx.items(): canims[f"CE_{idx}"] = data
def rnd(o):
    if isinstance(o, float): return round(o, 5)
    if isinstance(o, list): return [rnd(x) for x in o]
    if isinstance(o, dict): return {k: rnd(v) for k, v in o.items()}
    return o
json.dump(rnd(canims), open(CANIMS, "w"), separators=(",", ":"))
with open(TSV, "w") as f:
    for nm, ek, uk, ul, hk in rows: f.write(f"{nm}\t{ek}\t{uk}\t{ul}\t{hk}\n")
with open(APARTS, "w") as f:
    for nm, ns in aparts: f.write(f"{nm}\t{ns}\n")
if deep:
    print(f"  !! {len(deep)} curve(s) nest deeper than one level under the item root -- flattening them WOULD be a bug:")
    for d in sorted(deep)[:6]: print("     ", d)
print(f"hooks: L={sum(1 for r in rows if r[4]=='Left')} R={sum(1 for r in rows if r[4]=='Right')} none={sum(1 for r in rows if not r[4])} | animparts rows={len(aparts)}")
print(f"distinct Use={len(use_idx)} Equip={len(eq_idx)} | {len(rows)} items mapped | consumable_anims.json bytes={os.path.getsize(CANIMS)}")
print("sample:", rows[:2], "| e.g. bottled_water:", next((r for r in rows if r[0]=='bottled_water'), None), "| medkit:", next((r for r in rows if r[0]=='medkit'), None))
