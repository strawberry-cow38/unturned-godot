#!/usr/bin/env python3
"""extract_mag_tex.py -- each magazine's _MainTex (Albedo) out of magazine.prefab's material.

Writes content/mag_<folder>_albedo.png, one per magazine, and reports what each texture actually LOOKS like so
the wiring can tell a real painted texture from Unturned's white=paintable / black=metal mask convention (which
the gun albedos use and which renders flat black if applied raw).
"""
import UnityPy, os, sys, collections
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
CONTENT = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB); by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names: return co
    return None

def pairs(seq):
    out = []
    for e in seq or []:
        if isinstance(e, dict): out.append((e.get("first"), e.get("second")))
        elif isinstance(e, (list, tuple)) and len(e) >= 2: out.append((e[0], e[1]))
    return out

def main_tex(sub):
    p = next((o for pa, o in env.container.items() if pa.lower().endswith(sub) and o.type.name == "GameObject"), None)
    if not p: return None
    found = []
    def walk(pid):
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree()
        mr = comp_of(tt, ("MeshRenderer", "SkinnedMeshRenderer"))
        if mr:
            for m in mr.read_typetree().get("m_Materials", []):
                mo = by_id.get(m.get("m_PathID"))
                if not mo: continue
                sp = mo.read_typetree().get("m_SavedProperties", {})
                for k, v in pairs(sp.get("m_TexEnvs", [])):
                    if k == "_MainTex" and isinstance(v, dict):
                        pid2 = v.get("m_Texture", {}).get("m_PathID")
                        if pid2 and pid2 in by_id: found.append(by_id[pid2])
        tr = comp_of(tt, ("Transform",))
        if tr:
            for ch in tr.read_typetree().get("m_Children", []):
                ct = by_id.get(ch.get("m_PathID"))
                if ct: walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
    walk(p.path_id)
    return found[0] if found else None

folders = sorted({f[4:-4] for f in os.listdir(CONTENT) if f.startswith("mag_") and f.endswith(".txt")})
print("magazines with a model: %d" % len(folders))
ok = 0
for f in folders:
    t = main_tex("magazines/%s/magazine.prefab" % f)
    if t is None:
        print("  %-22s NO _MainTex" % f); continue
    img = t.read().image.convert("RGB")
    out = os.path.join(CONTENT, "mag_%s_albedo.png" % f)
    img.save(out); ok += 1
    px = img.load(); w, h = img.size
    n = 0; dark = 0; light = 0; sat = 0
    for y in range(0, h, max(1, h // 48)):
        for x in range(0, w, max(1, w // 48)):
            r, g, b = px[x, y]; n += 1
            mx, mn = max(r, g, b), min(r, g, b)
            if mx < 40: dark += 1
            elif mn > 215: light += 1
            if mx - mn > 24: sat += 1
    print("  %-22s %4dx%-4d  near-black %2d%%  near-white %2d%%  coloured %2d%%" %
          (f, w, h, 100*dark//n, 100*light//n, 100*sat//n))
print("wrote %d albedos" % ok)
