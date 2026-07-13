#!/usr/bin/env python3
"""extract_gun.py <GunFolder> [OUTDIR] -- rip a gun's 1st-person viewmodel from core.masterbundle.
Outputs <gun>_gun.txt (Model_0 mesh, Unturned->Godot: Z-neg + winding reverse) + <gun>_albedo.png
(Model_0 material _MainTex, the COLORED albedo -- not the grayscale albedo_base mask) + prints the
hook local positions (Sight/Barrel/Magazine/Eject) for the Viewmodel GunVisual."""
import UnityPy, numpy as np, sys, os
env = UnityPy.load(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle")
by_id = {o.path_id: o for o in env.objects}
GUN = sys.argv[1]
OUTDIR = sys.argv[2] if len(sys.argv) > 2 else r"C:\claude-workspace\gunout"
os.makedirs(OUTDIR, exist_ok=True)
gl = GUN.lower()

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def pptr(v):
    return by_id.get(v.get("m_PathID")) if isinstance(v, dict) else None

# find the gun's item.prefab root ("Item")
prefab = next((o for p, o in env.container.items()
               if ("/guns/" + gl + "/item.prefab") in p.lower() and o.type.name == "GameObject"), None)
if not prefab:
    print("NO item.prefab for", GUN); sys.exit(1)
item_tt = prefab.read_typetree()
item_tr = comp_of(item_tt, ("Transform",))

hooks, model0_go = {}, None
for ch in item_tr.read_typetree().get("m_Children", []):
    ct = by_id.get(ch.get("m_PathID"))
    if not ct:
        continue
    ctt = ct.read_typetree()
    cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
    if not cgo:
        continue
    nm = cgo.read_typetree().get("m_Name")
    lp = ctt["m_LocalPosition"]
    hooks[nm] = (round(lp["x"], 4), round(lp["y"], 4), round(lp["z"], 4))
    if nm == "Model_0":
        model0_go = cgo

if not model0_go:
    print("NO Model_0 for", GUN); sys.exit(1)
m0tt = model0_go.read_typetree()

# --- mesh (Model_0 is at identity under Item; export raw local verts, Z-neg + winding reverse) ---
mf = comp_of(m0tt, ("MeshFilter",))
mesh = by_id.get(mf.read_typetree().get("m_Mesh", {}).get("m_PathID")) if mf else None
if not mesh:
    print("NO Model_0 mesh for", GUN); sys.exit(1)
txt = mesh.read().export()
Vs, Ns, Ts, Fs = [], [], [], []
for line in txt.splitlines():
    p = line.split()
    if not p:
        continue
    if p[0] == "v":
        Vs.append((-float(p[1]), float(p[2]), -float(p[3])))   # Unturned(Unity LH) -> Godot(RH): negate X AND Z (the originals do both; Z-only left the guns mirrored L/R -- ejection ports on the wrong side)
    elif p[0] == "vn":
        Ns.append((-float(p[1]), float(p[2]), -float(p[3])))
    elif p[0] == "vt":
        Ts.append((p[1], p[2]))
    elif p[0] == "f":
        idx = []
        for tok in p[1:]:
            q = tok.split("/")
            idx.append((int(q[0]), (int(q[1]) if len(q) > 1 and q[1] else None), (int(q[2]) if len(q) > 2 and q[2] else None)))
        Fs.append(list(reversed(idx)))
L = ["# Model_0 (%s rip -> Godot, Z negated + winding reversed)" % GUN]
L += ["v %.6f %.6f %.6f" % v for v in Vs]
L += ["vt %s %s" % t for t in Ts]
L += ["vn %.6f %.6f %.6f" % n for n in Ns]
for f in Fs:
    s = "f"
    for (vi, ti, ni) in f:
        s += (" %d/%d/%d" % (vi, ti, ni)) if (ti and ni) else ((" %d//%d" % (vi, ni)) if ni else ((" %d/%d" % (vi, ti)) if ti else " %d" % vi))
    L.append(s)
open(os.path.join(OUTDIR, gl + "_gun.txt"), "w").write("\n".join(L) + "\n")

# --- albedo: Model_0 MeshRenderer material _MainTex (colored) ---
alb = "NONE"
mr = comp_of(m0tt, ("MeshRenderer",))
if mr:
    mats = mr.read_typetree().get("m_Materials", [])
    mo = pptr(mats[0]) if mats else None
    if mo:
        for pair in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
            nm, val = (pair[0], pair[1]) if isinstance(pair, (list, tuple)) else (pair.get("first"), pair.get("second"))
            if nm == "_MainTex" and isinstance(val, dict):
                to = pptr(val.get("m_Texture", {}))
                if to:
                    to.read().image.convert("RGBA").save(os.path.join(OUTDIR, gl + "_albedo.png"))
                    alb = "%s_albedo.png" % gl

# per-gun sounds: shoot + reload AudioClips -> ogg (real gun audio instead of reusing eaglefire's)
import subprocess, glob, re as _re
FFMPEG = r"C:\claude-workspace\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe"
_cont = {p.lower(): o for p, o in env.container.items()}
snds = []

def _save_ogg(clip_path, suffix):   # container AudioClip -> <gl>_<suffix>.ogg (via wav->ffmpeg)
    _co = _cont.get(clip_path)
    if not (_co and _co.type.name == "AudioClip"):
        return False
    try:
        for _nm, _wav in _co.read().samples.items():
            _wp = os.path.join(OUTDIR, gl + "_" + suffix + ".wav")
            open(_wp, "wb").write(_wav)
            subprocess.run([FFMPEG, "-y", "-i", _wp, os.path.join(OUTDIR, gl + "_" + suffix + ".ogg")], capture_output=True)
            os.remove(_wp)
            return True
    except Exception as _e:
        print("SND ERR", gl, suffix, _e)
    return False

for _snd in ("shoot", "reload"):
    if _save_ogg("assets/coremasterbundle/items/guns/" + gl + "/" + _snd + ".mp3", _snd):
        snds.append(_snd)

# FALLBACK 0 (launcher): some guns name the fire clip fire.mp3 instead of shoot.mp3.
if "shoot" not in snds and _save_ogg("assets/coremasterbundle/items/guns/" + gl + "/fire.mp3", "shoot"):
    snds.append("shoot(fire.mp3)")

# FALLBACK 1 (bows/crossbow): no guns/<name>/shoot.mp3 -> the fire sound lives on the gun's DEFAULT Barrel.
# Source: the gun .dat's "Barrel <id>" (Bow_Compound -> Barrel 354 = Bow_Barrel) -> items/barrels/<barrel>/shoot.mp3 (the string twang, NOT a gunshot).
if "shoot" not in snds:
    _gundat = os.path.join(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Guns", GUN, GUN + ".dat")
    _bid = None
    if os.path.exists(_gundat):
        _m = _re.search(r"^\s*Barrel\s+(\d+)", open(_gundat, encoding="utf-8-sig", errors="ignore").read(), _re.M)
        if _m: _bid = _m.group(1)
    if _bid:
        for _bd in glob.glob(r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items\Barrels\*\*.dat"):
            if _re.search(r"^\s*ID\s+" + _bid + r"\b", open(_bd, encoding="utf-8-sig", errors="ignore").read(), _re.M):
                _bf = os.path.basename(os.path.dirname(_bd)).lower()
                if _save_ogg("assets/coremasterbundle/items/barrels/" + _bf + "/shoot.mp3", "shoot"):
                    snds.append("shoot(barrel:%s)" % _bf)
                break

# FALLBACK 2 (bows): no guns/<name>/reload.mp3 -> the bow's aim.mp3 is the string-DRAW sound (a bow's "reload").
if "reload" not in snds and _save_ogg("assets/coremasterbundle/items/guns/" + gl + "/aim.mp3", "reload"):
    snds.append("reload(aim/draw)")

print("GUN", GUN, "verts", len(Vs), "tris", len(Fs), "albedo", alb, "sounds", snds)
print("HOOKS", {k: hooks[k] for k in ("Model_0", "Sight", "Barrel", "Magazine", "Eject", "Grip", "Tactical") if k in hooks})

# emit the Viewmodel GunVisual data line: name \t muzzle(x,y,z) \t aim(x,y,z) \t ejects(1/0)
# muzzle = Barrel hook Z-negated; aim = a first-pass ADS offset from the Sight hook (fit to eaglefire, tune later)
sg = hooks.get("Sight", (0, 0, 0)); ba = hooks.get("Barrel", (0, 0, 0))
muzzle = (round(-ba[0], 4), round(ba[1], 4), round(-ba[2], 4))
aim = (round(-sg[0], 4), round(sg[1] - 0.229, 4), round(-sg[2] - 0.071, 4))
ejects = "0" if gl in ("grizzly", "schofield", "ace", "peacemaker", "desert_falcon", "luger", "masterkey", "quadbarrel", "sawed_off", "matamorez", "crossbow", "bow_maple", "bow_birch", "bow_pine", "bow_compound", "launcher_rocket") else "1"
vline = "%s\t%s,%s,%s\t%s,%s,%s\t%s" % (gl, muzzle[0], muzzle[1], muzzle[2], aim[0], aim[1], aim[2], ejects)
open(os.path.join(OUTDIR, "guns_visual.tsv"), "a").write(vline + "\n")
print("VISUAL", vline)
