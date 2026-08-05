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
    uvs.append([0.5, 0.5])                             # arms are a solid skin tint (no texture) -> uv irrelevant
fc = []
for p in me.polygons:
    vs = list(p.vertices)
    for i in range(1, len(vs) - 1):
        fc += [vs[0], vs[i], vs[i + 1]]                # fan-triangulate, keep FBX winding (rig_extract doesn't reverse; shader is cull_front)
arms = {"vcount": len(pos), "positions": pos, "normals": nrm, "uvs": uvs,
        "skin_index": si, "skin_weight": sw, "faces": fc}
json.dump(arms, open(OUT, "w"))
print("EXTRACTED", len(pos), "verts", len(fc) // 3, "faces")
