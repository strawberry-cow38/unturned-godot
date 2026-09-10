#!/usr/bin/env python3
"""Extract each placed OBJECT's real physic material -- what it is actually made of.

Retail decides footstep, bullet-impact and melee audio from the PHYSIC MATERIAL NAME on the collider you
hit (PhysicMaterialCustomData.GetAudioDef(materialName, "BulletImpact"/"FootstepWalk"/...)). The port has a
Surf enum per collider instead, and WorldBuilder was setting it from ONE ternary --
`fmesh != null ? Wood : Concrete`, "trees have foliage so they are wood, everything else is concrete" --
so every metal fence, wooden crate and tiled floor in the world answered as pavement. The bundles carry the
real answer: 21 PhysicMaterials (Concrete/Metal/Wood/Gravel/Foliage/Tile/Cloth _Static|_Dynamic, plus Ice,
Snow, Water, Metal_Slip and the _Silent variants) and every object prefab's collider names one.

    python extract_prop_surfaces.py <BUNDLES_DIR> <OUT_TSV>
writes  <prop name (lowercase)> <TAB> <physic material>   one line per object prefab that has a collider.

A prop whose colliders disagree takes the material carried by the MOST of them, which is the surface most of
its hull is made of -- our SurfMeta is per-body and cannot say "the roof is tile and the walls are wood".
That collapse is the port's limit, not retail's, so the count is printed for the ones it applies to.
"""
import UnityPy, collections, os, sys

BUND = sys.argv[1]
OUT  = sys.argv[2]

env   = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
cont  = {p.lower(): o for p, o in env.container.items()}

def materials_of(prefab):
    """Every collider physic-material name under this prefab, in tree order."""
    found, stack = [], [prefab]
    while stack:
        go = stack.pop()
        if not go: continue
        tt = go.read_typetree()
        for comp in tt.get("m_Component", []):
            co = by_id.get(comp.get("component", comp).get("m_PathID"))
            if not co: continue
            if "Collider" in co.type.name:
                ref = co.read_typetree().get("m_Material", {}) or {}
                mo = by_id.get(ref.get("m_PathID")) if ref.get("m_PathID") else None
                if mo is not None and mo.type.name == "PhysicMaterial":
                    try: found.append(mo.read().m_Name)
                    except Exception: pass
            if co.type.name == "Transform":
                for ch in co.read_typetree().get("m_Children", []):
                    ct = by_id.get(ch.get("m_PathID"))
                    if ct: stack.append(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")))
    return found

rows, mixed, nomat = {}, 0, 0
prefabs = [p for p in cont if p.startswith("assets/coremasterbundle/objects/") and p.endswith("/object.prefab")]
for p in sorted(prefabs):
    name = p.split("/")[-2]
    mats = materials_of(cont[p])
    if not mats: nomat += 1; continue
    c = collections.Counter(mats)
    if len(c) > 1: mixed += 1
    rows[name] = c.most_common(1)[0][0]

with open(OUT, "w") as f:
    for k in sorted(rows): f.write(f"{k}\t{rows[k]}\n")

print(f"[surf] {len(prefabs)} object prefabs -> {len(rows)} with a physic material ({nomat} with none, {mixed} mixed -> most common)")
for m, n in collections.Counter(rows.values()).most_common():
    print(f"[surf]   {m:20s} {n}")
print(f"[surf] wrote {OUT}")
