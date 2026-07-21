#!/usr/bin/env python3
"""Extract the ACTUAL retail Rubble_Effect particle systems for the destructible props placed on PEI.

Each object's `Rubble_Effect <id>` names an EffectAsset whose prefab is a Unity Shuriken ParticleSystem in
core.masterbundle (burst of debris-chip / dust / spark sprites in a cone, tumbling under gravity, shrinking
over life). We can't run a Unity ParticleSystem in Godot, but we CAN read its parameters + its actual sprite
and reproduce the break faithfully with a CpuParticles3D. This pulls, per effect id used by a placed rubble
prop: the burst count, cone shape (angle/radius), start lifetime/speed/size ranges, gravity, tumble, and the
real sprite (a horizontal flipbook -> hframes), into:
    game/content/effects/rubble_fx.json     -- { "<id>": { params... } }
    game/content/effects/rubble/<id>.png     -- the particle sprite (RGBA)

Runtime: DestructibleField.PlayBreakEffect looks up the prop's effect id (carried in rubble.txt) and plays it.
"""
import UnityPy, json, os, re, glob

BUND = "/home/ec2-user/unturned-bundles/Bundles"
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MB   = os.path.join(BUND, "core.masterbundle")
OUTJSON = os.path.join(REPO, "game/content/effects/rubble_fx.json")
OUTTEX  = os.path.join(REPO, "game/content/effects/rubble")

def kv(txt, k):
    m = re.search(r"(?im)^\s*" + re.escape(k) + r"\s+(\S+)", txt)
    return m.group(1) if m else None

def mm(v):
    """Unwrap a Unity MinMaxCurve to (min, max). state 3 = random between two constants [minScalar, scalar];
    0 = constant (scalar); 1/2 = curve (approximate with the scalar)."""
    if not isinstance(v, dict): return (float(v), float(v))
    s = v.get("scalar", 0.0) or 0.0
    if v.get("minMaxState") == 3:
        return (float(v.get("minScalar", s)), float(s))
    return (float(s), float(s))

# 1) effect id -> "category/name" (relative to Bundles/Effects) from the effect .dats
id2eff = {}
for datp in glob.glob(os.path.join(BUND, "Effects", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    if (kv(txt, "Type") or "").lower() != "effect": continue
    eid = kv(txt, "ID")
    if eid and eid.isdigit():
        id2eff[int(eid)] = os.path.relpath(os.path.dirname(datp), os.path.join(BUND, "Effects")).replace("\\", "/").lower()

# 2) which effect ids the PLACED rubble props actually use (rubble.txt guid -> prop .dat -> Rubble_Effect)
guid2dat = {}
for datp in glob.glob(os.path.join(BUND, "Objects", "**", "*.dat"), recursive=True):
    txt = open(datp, encoding="utf-8-sig", errors="ignore").read()
    g = kv(txt, "GUID")
    if g: guid2dat[g.lower()] = txt
used = {}
for line in open(os.path.join(REPO, "game/content/objects/rubble.txt")):
    p = line.split()
    if p and p[0] in guid2dat:
        e = kv(guid2dat[p[0]], "Rubble_Effect")
        if e and e.isdigit(): used[p[0]] = int(e)
used_ids = sorted(set(used.values()))
print(f"[fx] {len(used_ids)} effect ids used by placed rubble props")

# 3) extract each used effect's ParticleSystem params + sprite from the masterbundle
os.makedirs(OUTTEX, exist_ok=True)
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = {p.lower(): o for p, o in env.container.items()}

def main_ps(prefab):
    """the effect's primary ParticleSystem (walk the prefab; take the first PS found)."""
    stack = [prefab]
    while stack:
        go = stack.pop()
        if not go: continue
        tt = go.read_typetree()
        for comp in tt.get("m_Component", []):
            co = by_id.get(comp.get("component", comp).get("m_PathID"))
            if not co: continue
            if co.type.name == "ParticleSystem":
                return co
            if co.type.name == "Transform":
                for ch in co.read_typetree().get("m_Children", []):
                    ct = by_id.get(ch.get("m_PathID"))
                    if ct: stack.append(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")))
    return None

catalog = {}
for eid in used_ids:
    effpath = id2eff.get(eid)
    base = f"assets/coremasterbundle/effects/{effpath}"
    prefab = cont.get(base + "/effect.prefab")
    if not prefab:
        print(f"[fx] {eid} ({effpath}): no prefab, skip"); continue
    ps = main_ps(prefab)
    if not ps:
        print(f"[fx] {eid} ({effpath}): no ParticleSystem, skip"); continue
    d = ps.read_typetree()
    im = d.get("InitialModule", {})
    bursts = d.get("EmissionModule", {}).get("m_Bursts", [])
    count = int(bursts[0].get("countCurve", {}).get("scalar", 8)) if bursts else int(d.get("EmissionModule", {}).get("rateOverTime", {}).get("scalar", 8))
    sh = d.get("ShapeModule", {})
    shape_type = sh.get("type")   # 4=cone, 0=sphere, 6=box(ish)
    life = mm(im.get("startLifetime", 1.0)); spd = mm(im.get("startSpeed", 5.0)); sz = mm(im.get("startSize", 0.5))
    grav = mm(im.get("gravityModifier", 1.0))[1]
    rot_enabled = bool(d.get("RotationModule", {}).get("enabled"))
    size_over_life = bool(d.get("SizeModule", {}).get("enabled"))
    # sprite: the effect's texture.png (a horizontal flipbook -> hframes)
    hframes = 1; texname = None
    tex = cont.get(base + "/texture.png")
    if tex:
        img = tex.read()
        w, h = img.m_Width, img.m_Height
        hframes = max(1, round(w / h)) if h and w >= h else 1
        texname = f"{eid}.png"
        img.image.save(os.path.join(OUTTEX, texname))
    catalog[str(eid)] = {
        "effect": effpath, "count": max(1, min(count, 64)),
        "shape": "cone" if shape_type == 4 else ("sphere" if shape_type in (0, 1) else "box"),
        "cone_angle": float(sh.get("angle", 45.0)), "radius": float(sh.get("radius", {}).get("value", 1.0) if isinstance(sh.get("radius"), dict) else sh.get("radius", 1.0)),
        "life": [round(life[0], 3), round(life[1], 3)], "speed": [round(spd[0], 3), round(spd[1], 3)],
        "size": [round(sz[0], 4), round(sz[1], 4)], "gravity": round(grav, 3),
        "tumble": rot_enabled, "shrink": size_over_life, "hframes": hframes, "tex": texname,
    }
    print(f"[fx] {eid:4d} {effpath:28s} count={count} shape={catalog[str(eid)]['shape']} life={life} spd={spd} sz={sz} grav={grav} hf={hframes}")

json.dump(catalog, open(OUTJSON, "w"), indent=1, sort_keys=True)
print(f"[fx] wrote {len(catalog)} effects -> {OUTJSON} + sprites in {OUTTEX}/")
