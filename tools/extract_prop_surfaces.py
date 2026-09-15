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
writes  <prop name (lowercase)> <TAB> <physic material>   one line per object prefab that has a collider,
and beside it <OUT_TSV stem>_boxes.tsv, the MINORITY box colliders of mixed props (see below).

A prop whose colliders disagree takes the material of the BIGGEST of them -- the surface most of its hull is
actually made of.

⚠ IT USED TO COUNT COLLIDERS, not measure them, which is not the same claim and got the answer backwards on
the exact prop that reported it. Dock_1, the big concrete pier, is three boxes on one GameObject: a
36 x 36 x 40.5 Concrete slab and two 1.5 x 1.5 x 41 Metal railings along its top edges. Two metal against one
concrete, so "most of them" said METAL -- and the whole pier rang like metal underfoot, 0.2% of its volume
outvoting the other 99.8% (strawberry 2026-09-15: "the large Dock prop is making a metal footstep sound.
should be concrete, with the brown metal pillars making metal footstep sounds"). Volume is what the docstring
always meant by "most of its hull"; counting was just easier to write.

...AND THE MINORITY SURVIVES. A prop's dominant material is one SurfMeta on one body, but a BoxCollider whose
material differs is exactly specified -- size and centre, in the prefab's own frame, which is the frame our
ripped OBJ is already in (verified against Dock_1: the retail boxes reproduce the OBJ's bounding box to the
centimetre, railings included). So those boxes are emitted too and WorldBuilder gives each one its own small
body with its own surface. Mesh colliders cannot be split this way and are not tried.
"""
import UnityPy, collections, os, sys

BUND = sys.argv[1]
OUT  = sys.argv[2]

env   = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}
cont  = {p.lower(): o for p, o in env.container.items()}

def materials_of(prefab):
    """Every collider under this prefab as (material, volume, box) -- box is (size, centre) for a BoxCollider
    and None for anything else. volume is the box's, or None when it cannot be measured."""
    found, stack = [], [prefab]
    while stack:
        go = stack.pop()
        if not go: continue
        tt = go.read_typetree()
        for comp in tt.get("m_Component", []):
            co = by_id.get(comp.get("component", comp).get("m_PathID"))
            if not co: continue
            if "Collider" in co.type.name:
                ct = co.read_typetree()
                ref = ct.get("m_Material", {}) or {}
                mo = by_id.get(ref.get("m_PathID")) if ref.get("m_PathID") else None
                if mo is not None and mo.type.name == "PhysicMaterial":
                    try: nm = mo.read().m_Name
                    except Exception: nm = None
                    if nm:
                        box = None
                        if "m_Size" in ct:
                            sz, ctr = ct["m_Size"], ct.get("m_Center", {}) or {}
                            box = ((sz["x"], sz["y"], sz["z"]),
                                   (ctr.get("x", 0.0), ctr.get("y", 0.0), ctr.get("z", 0.0)))
                        vol = abs(box[0][0] * box[0][1] * box[0][2]) if box else None
                        found.append((nm, vol, box))
            if co.type.name == "Transform":
                for ch in co.read_typetree().get("m_Children", []):
                    ct = by_id.get(ch.get("m_PathID"))
                    if ct: stack.append(by_id.get(ct.read_typetree().get("m_GameObject", {}).get("m_PathID")))
    return found

def surf_of(mat):
    """The port's Surf bucket for a physic material -- PropSurfaces.SurfForMaterial, mirrored. Two materials in
    the same bucket are the same SOUND, so the extractor must not emit an override body to switch between them."""
    m = (mat or "").lower()
    for pre, surf in (("metal", "Metal"), ("wood", "Wood"), ("gravel", "Gravel"), ("foliage", "Grass"),
                      ("snow", "Snow"), ("ice", "Ice"), ("water", "Water"), ("tile", "Concrete"), ("cloth", "Dirt")):
        if m.startswith(pre): return surf
    return "Concrete"

rows, boxes, mixed, nomat, unmeasured = {}, [], 0, 0, 0
prefabs = [p for p in cont if p.startswith("assets/coremasterbundle/objects/") and p.endswith("/object.prefab")]
for p in sorted(prefabs):
    name = p.split("/")[-2]
    cols = materials_of(cont[p])
    if not cols: nomat += 1; continue
    names = [c[0] for c in cols]
    if len(set(names)) > 1:
        mixed += 1
        # BIGGEST, not most numerous -- but ONLY when every collider on the prop can be measured.
        #
        # ⚠ THIS GATE IS THE WHOLE RULE. A MeshCollider has no size here, and comparing a box's volume against
        # an unmeasured mesh is not a comparison -- it is a box beating a blank. Ungated, that one mistake
        # re-materialed nine props on its own: greenhouse_0 and mill_0 became FOLIAGE because a planting volume
        # outvoted a wooden floor that measured as nothing, booth_1 became CLOTH, monolith became CONCRETE,
        # statue_0..3 became METAL. Every one of them had volume 0.0 on the side that was actually winning.
        # With the gate, volume decides exactly the props where it is entitled to: dock_1 (52488 concrete
        # against 184 metal), statue_4, fence_metal_broken_0. The rest keep the count they always had.
        if all(v is not None for _, v, _ in cols):
            by_vol = collections.defaultdict(float)
            for nm, vol, _ in cols: by_vol[nm] += vol
            dom = max(by_vol.items(), key=lambda kv: kv[1])[0]
        else:
            unmeasured += 1
            dom = collections.Counter(names).most_common(1)[0][0]
        # ...and every box that is NOT the dominant material keeps its own surface -- but only where that
        # actually SOUNDS different. Metal_Dynamic beside Metal_Static is the same bank, so a body for it would
        # be collision geometry that changes nothing you can hear.
        if all(v is not None for _, v, _ in cols):
            for nm, _, box in cols:
                if not box or surf_of(nm) == surf_of(dom): continue
                (sx, sy, sz), (cx, cy, cz) = box
                boxes.append((name, nm, sx, sy, sz, cx, cy, cz))
    else:
        dom = names[0]
    rows[name] = dom

with open(OUT, "w") as f:
    for k in sorted(rows): f.write(f"{k}\t{rows[k]}\n")

OUT_BOXES = OUT[:-4] + "_boxes.tsv" if OUT.endswith(".tsv") else OUT + "_boxes.tsv"
with open(OUT_BOXES, "w") as f:
    for r in sorted(boxes):
        f.write("%s\t%s\t%.4f\t%.4f\t%.4f\t%.4f\t%.4f\t%.4f\n" % r)
print(f"[surf] {len(boxes)} minority boxes on mixed props -> {OUT_BOXES}")

print(f"[surf] {len(prefabs)} object prefabs -> {len(rows)} with a physic material ({nomat} with none, {mixed} mixed -> biggest by volume, {unmeasured} of those unmeasurable -> most common)")
for m, n in collections.Counter(rows.values()).most_common():
    print(f"[surf]   {m:20s} {n}")
print(f"[surf] wrote {OUT}")
