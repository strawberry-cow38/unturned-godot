#!/usr/bin/env python3
"""Local (arm64/tinyclaw) albedo pull for semi_1 (trailer). Points at the LOCAL extracted
Bundles instead of catboy's Windows Steam path. Navigates each prefab to its Model_0 renderer's
material _MainTex; if semi_0 and semi_1 share the same texture (one authoring set), the trailer
can reuse semi_0_albedo.png. Otherwise exports semi_1's _MainTex -> trailer_0_albedo.png.
Usage: python extract_semi1_albedo_local.py
"""
import UnityPy, sys, os

BUNDLE = os.path.expanduser("~/unturned-bundles/Bundles/core.masterbundle")
OUT = os.path.join(os.path.dirname(__file__), "..", "game", "content", "trailer_0_albedo.png")

env = UnityPy.load(BUNDLE)
by_id = {o.path_id: o for o in env.objects}

def comp_of(tt, names):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID") if isinstance(c, dict) else None)
        if co and co.type.name in names:
            return co
    return None

def find_maintex(prefab_sub):
    """walk the prefab tree, find the largest MeshRenderer's material _MainTex texture obj."""
    prefab = next(o for p, o in env.container.items()
                  if p.lower().endswith(prefab_sub) and o.type.name == "GameObject")
    best = None  # (vcount, tex_obj)
    def walk(pid):
        nonlocal best
        go = by_id.get(pid)
        if not go: return
        tt = go.read_typetree()
        mr = comp_of(tt, ("MeshRenderer", "SkinnedMeshRenderer"))
        mf = comp_of(tt, ("MeshFilter",))
        if mr:
            mrt = mr.read_typetree()
            mats = mrt.get("m_Materials", [])
            vc = 0
            if mf:
                mp = mf.read_typetree().get("m_Mesh", {}).get("m_PathID")
                if mp in by_id:
                    vc = by_id[mp].read_typetree().get("m_VertexData", {}).get("m_VertexCount", 0)
            for m in mats:
                mo = by_id.get(m.get("m_PathID"))
                if not mo or mo.type.name != "Material": continue
                for te in mo.read_typetree().get("m_SavedProperties", {}).get("m_TexEnvs", []):
                    name = te[0] if isinstance(te, (list, tuple)) else te.get("first")
                    env_ = te[1] if isinstance(te, (list, tuple)) else te.get("second")
                    if name == "_MainTex":
                        tid = (env_.get("m_Texture") or {}).get("m_PathID")
                        to = by_id.get(tid)
                        if to and (best is None or vc > best[0]):
                            best = (vc, to)
        tr = comp_of(tt, ("Transform",))
        if tr:
            for ch in tr.read_typetree().get("m_Children", []):
                ct = by_id.get(ch.get("m_PathID"))
                if ct:
                    walk(ct.read_typetree().get("m_GameObject", {}).get("m_PathID"))
    walk(prefab.read_typetree().get("m_GameObject", prefab.path_id) if False else prefab.path_id)
    return best[1] if best else None

t0 = find_maintex("semi_0/object.prefab")
t1 = find_maintex("semi_1/object.prefab")
n0 = t0.read_typetree().get("m_Name") if t0 else None
n1 = t1.read_typetree().get("m_Name") if t1 else None
print(f"semi_0 _MainTex: {n0} (pathid {t0.path_id if t0 else None})")
print(f"semi_1 _MainTex: {n1} (pathid {t1.path_id if t1 else None})")

if t0 and t1 and t0.path_id == t1.path_id:
    print("SHARED texture -> trailer reuses semi_0_albedo.png (Palette = \"semi_0_albedo.png\"), no export needed")
elif t1:
    img = t1.read().image
    img.save(os.path.abspath(OUT))
    print(f"EXPORTED semi_1 _MainTex -> {os.path.abspath(OUT)} ({img.size})")
else:
    print("could not resolve semi_1 _MainTex")
