#!/usr/bin/env python3
"""extract_sightview_hooks.py -- emit content/guns_sighthook.tsv: each gun's SIGHT hook (the attach MOUNT point
where optics sit) + VIEW hook (the iron-sight ADS EYE-POINT), read from its item.prefab. Guns without a default
sight attachment (pistols, shotguns, bows) fall back to the EAGLEFIRE's hooks otherwise, which parks their optics
+ ADS at the wrong spot; this table lets the viewmodel place each gun's mount/eye per-gun. Port coords: the mount
negates z (matches the sights.tsv convention); the view/eye negates x AND z (matches the sight-aim convention).
Additive -- writes ONLY guns_sighthook.tsv; touches no existing row."""
import UnityPy, os
MB = r"C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\core.masterbundle"
content = r"C:\claude-workspace\unturned-godot\game\content"
env = UnityPy.load(MB)
by_id = {o.path_id: o for o in env.objects}
cont = env.container

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def child_local(gun, child):
    p = next((o for pa, o in cont.items() if f"/guns/{gun}/item.prefab" in pa.lower() and o.type.name == "GameObject"), None)
    if not p:
        return None
    tr = comp_of(p.read_typetree(), ("Transform",))
    for ch in tr.read_typetree().get("m_Children", []):
        ct = by_id.get(ch.get("m_PathID"))
        if not ct:
            continue
        ctt = ct.read_typetree()
        cgo = by_id.get(ctt.get("m_GameObject", {}).get("m_PathID"))
        if cgo and cgo.read_typetree().get("m_Name") == child:
            lp = ctt["m_LocalPosition"]
            return (lp["x"], lp["y"], lp["z"])
    return None

guns = [l.strip().split("\t")[0] for l in open(content + r"\guns_visual.tsv") if l.strip()]
lines, nS, nV = [], 0, 0
for gun in guns:
    sh = child_local(gun, "Sight")   # attach MOUNT
    vh = child_local(gun, "View")    # iron-sight EYE-POINT
    shp = f"{sh[0]:.4f},{sh[1]:.4f},{-sh[2]:.4f}" if sh else ""    # mount: z-neg (sights.tsv convention)
    vhp = f"{-vh[0]:.4f},{vh[1]:.4f},{-vh[2]:.4f}" if vh else ""   # eye: x,z-neg (sight-aim convention)
    if shp:
        nS += 1
    if vhp:
        nV += 1
    lines.append(f"{gun}\t{shp}\t{vhp}")
    print(f"{gun}: sight={shp or 'NONE'}  view={vhp or 'NONE'}")
open(content + r"\guns_sighthook.tsv", "w").write("\n".join(lines) + "\n")
print(f"guns_sighthook.tsv: {len(lines)} guns  ({nS} with a Sight hook, {nV} with a View hook)")
