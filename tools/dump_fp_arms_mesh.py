import bpy, json
FBX = r"C:\claude-workspace\archive\U3-SDK\Assets\Game\Sources\Models\Characters\Viewmodel\Model_0.fbx"
OUT = r"C:\claude-workspace\fp_arms_mesh.json"
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=FBX)
mo = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
me = mo.data
# our rig.json skin-bind SLOT index for each bone (from the existing rig's skin list)
SLOT = {"Spine": 0, "Left_Shoulder": 1, "Left_Arm": 2, "Left_Hand": 3, "Left_Hook": 4,
        "Right_Shoulder": 5, "Right_Arm": 6, "Right_Hand": 7, "Right_Hook": 8, "Skull": 9}
vgname = {g.index: g.name for g in mo.vertex_groups}
# ⚠ REAL UVs, not a placeholder (strawberry 2026-09-10: "the viewmodel arms are just getting the colors of
# clothing, not the actual textures"). This tool used to write [0.5, 0.5] for every vertex with the note "arms are
# a solid skin tint (no texture) -> uv irrelevant". That was TRUE when it was written and stopped being true the
# day clothes.gdshader started painting shirt/pants onto the arms: the shader samples texture(shirt_albedo, UV),
# every arm vertex asked for the same single texel, and the arms came out the flat average colour of the garment.
#
# Blender keeps UVs per LOOP (polygon corner), not per vertex, and this mesh is written per vertex -- so take each
# vertex's first loop. V is flipped to match rig_extract.py:120 (`uvs.append([u, 1.0-uv])`): Blender's UV origin is
# bottom-left like Unity's, and the body half of this same rig is already converted that way. The two meshes are
# painted by ONE shirt texture, so a mismatch here would put the arms in the wrong half of the atlas.
uvl = me.uv_layers.active
vert_uv = [None] * len(me.vertices)
if uvl is not None:
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            if vert_uv[vi] is None:
                uu, vv = uvl.data[li].uv
                vert_uv[vi] = [float(uu), 1.0 - float(vv)]
else:
    print("WARNING: no UV layer on the viewmodel mesh -- arms will stay untextured")
pos = []; nrm = []; uvs = []; si = []; sw = []
for v in me.vertices:
    co = mo.matrix_world @ v.co
    pos.append([-co.x, co.y, -co.z])                   # FBX(right-handed)->our rig: 180deg about Y (handedness-preserving, matches bbox)
    n = v.normal; nl = (n.x*n.x + n.y*n.y + n.z*n.z) ** 0.5 or 1.0
    nrm.append([-n.x/nl, n.y/nl, -n.z/nl])
    gs = sorted(v.groups, key=lambda g: -g.weight)[:2]
    a = [0, 0]; b = [0.0, 0.0]
    for k, g in enumerate(gs):
        a[k] = SLOT.get(vgname.get(g.group), 0); b[k] = g.weight
    s = b[0] + b[1]
    b = [b[0]/s, b[1]/s] if s > 1e-6 else [1.0, 0.0]
    si.append(a); sw.append(b)
    uvs.append(vert_uv[v.index] or [0.5, 0.5])         # real atlas UV; the 0.5 fallback only for a vertex no polygon uses
fc = []
for p in me.polygons:
    vs = list(p.vertices)
    for i in range(1, len(vs) - 1):
        fc += [vs[0], vs[i], vs[i + 1]]                # fan-triangulate, keep FBX winding (rig_extract doesn't reverse; shader is cull_front)
arms = {"vcount": len(pos), "positions": pos, "normals": nrm, "uvs": uvs,
        "skin_index": si, "skin_weight": sw, "faces": fc}
json.dump(arms, open(OUT, "w"))
print("EXTRACTED", len(pos), "verts", len(fc) // 3, "faces")
