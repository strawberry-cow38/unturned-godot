import bpy, json, math
from pathlib import Path
from mathutils import Vector, Quaternion
ROOT=Path('/workspace/plate_glass_doors_20261019'); OUT=ROOT/'export'; OUT.mkdir(exist_ok=True)
for ob in list(bpy.context.scene.objects): bpy.data.objects.remove(ob,do_unlink=True)
def mat(name,color):
    m=bpy.data.materials.new(name); m.diffuse_color=(*color,1); m.use_nodes=True
    bs=m.node_tree.nodes.get('Principled BSDF'); bs.inputs['Base Color'].default_value=(*color,1); bs.inputs['Roughness'].default_value=.75
    return m
def model(name,colors,offset):
    # Match the native door kit's authored X-width / Z-height; DoorDeploy stands it up, without a scaled hinge.
    verts=[]; faces=[]; slots=[]; boxes=[]
    def box(label,lo,hi,slot=0):
        boxes.append({'name':label,'min':lo,'max':hi}); x,y,z=lo; X,Y,Z=hi; base=len(verts)
        verts.extend([(x,y,z),(X,y,z),(X,Y,z),(x,Y,z),(x,y,Z),(X,y,Z),(X,Y,Z),(x,Y,Z)])
        for f in [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]: faces.append([base+i for i in f]); slots.append(slot)
    box('latch_stile',(-1.225,-.09,0),(-1.065,.09,2.8))
    box('hinge_stile',(1.065,-.09,0),(1.225,.09,2.8))
    box('top_rail',(-1.065,-.09,2.64),(1.065,.09,2.8))
    box('kick_rail',(-1.065,-.09,0),(1.065,.09,.32),1)
    # Square pull and push bars, with substantial mounts, not tiny CAD screws or rounded jewellery.
    for z in [1.12,1.72]: box('pull_mount',(-1.065,-.25,z-.06),(-.89,-.09,z+.06),2)
    box('pull_handle',(-1.04,-.31,1.06),(-.91,-.19,1.78),2)
    for x in [-.98,.98]: box('push_mount',(x-.07,.09,1.32),(x+.07,.23,1.46),2)
    box('push_bar',(-1.05,.18,1.32),(1.05,.32,1.46),2)
    for z in [.42,2.32]: box('hinge',(1.065,-.12,z-.12),(1.225,.12,z+.12),1)
    mesh=bpy.data.meshes.new(name); mesh.from_pydata(verts,[],faces); mesh.update(); uv=mesh.uv_layers.new()
    # Tiny flat palette texture, nearest sampling, no painted reflections or fake glass.
    image=bpy.data.images.new(name+'_palette',width=4,height=1,alpha=True)
    image.pixels=[v for c in colors for v in (*c,1)]; image.filepath_raw=str(OUT/(name+'_tex.png')); image.file_format='PNG'; image.save()
    material=mat(name+'_palette',(1,1,1)); tex=material.node_tree.nodes.new('ShaderNodeTexImage'); tex.image=image; tex.interpolation='Closest'
    material.node_tree.links.new(tex.outputs['Color'],material.node_tree.nodes.get('Principled BSDF').inputs['Base Color']); mesh.materials.append(material)
    for p,slot in zip(mesh.polygons,slots):
        for li in p.loop_indices: uv.data[li].uv=((slot+.5)/4,.5)
    ob=bpy.data.objects.new(name,mesh); bpy.context.collection.objects.link(ob); ob.location.x=offset
    mesh.calc_loop_triangles(); lines=['# Plate-glass door FRAME ONLY. Native Z-up. Glass is a runtime GlassPane.','o '+name]
    lines+=['v '+' '.join(f'{x:.7f}' for x in v.co) for v in mesh.vertices]
    lines+=['vt '+' '.join(f'{x:.7f}' for x in uv.data[l.index].uv) for l in mesh.loops]
    for p in mesh.polygons:
        for li in p.loop_indices: lines.append('vn '+' '.join(f'{x:.7f}' for x in p.normal))
    lines+=['f '+' '.join(f'{mesh.loops[li].vertex_index+1}/{li+1}/{li+1}' for li in t.loops) for t in mesh.loop_triangles]
    (OUT/(name+'.obj')).write_text('\n'.join(lines)+'\n')
    (OUT/(name+'_colliders.json')).write_text(json.dumps(boxes,indent=2))
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active=ob
    old=ob.location.copy(); ob.location=(0,0,0)
    bpy.ops.export_scene.gltf(filepath=str(OUT/(name+'.glb')),export_format='GLB',use_selection=True,export_yup=True)
    ob.location=old
    return ob
silver=[(.57,.59,.59),(.42,.44,.44),(.25,.27,.27),(.65,.69,.72)]
bronze=[(.35,.28,.19),(.26,.21,.15),(.20,.18,.14),(.65,.69,.72)]
objects=[model('Door_PlateGlass',silver,-1.6),model('Door_PlateGlass_Bronze',bronze,1.6)]
# Viewport-only sample panes: clearly labelled and never exported as a prop. The engine supplies actual breakable glass.
glass=mat('PREVIEW_ONLY_runtime_glass_placeholder',(.50,.66,.73)); glass.diffuse_color=(.50,.66,.73,.23)
bs=glass.node_tree.nodes.get('Principled BSDF'); bs.inputs['Alpha'].default_value=.23; glass.surface_render_method='DITHERED'
for ob in objects:
    bpy.ops.mesh.primitive_cube_add(size=1,location=(ob.location.x,0,1.48)); p=bpy.context.object; p.name='PREVIEW_ONLY_'+ob.name+'_pane'; p.dimensions=(2.13,.04,2.32); p.data.materials.append(glass)
# Unchanged native door reference, not exported.
vs=[]; fs=[]
for line in (ROOT/'reference/Door_Metal.obj').read_text().splitlines():
    p=line.split()
    if p and p[0]=='v': vs.append(tuple(map(float,p[1:4])))
    elif p and p[0]=='f': fs.append([int(t.split('/')[0])-1 for t in p[1:]])
mesh=bpy.data.meshes.new('Native_Metal_Reference'); mesh.from_pydata(vs,[],fs); mesh.update(); mesh.materials.append(mat('Reference_metal',(.45,.47,.47)))
ref=bpy.data.objects.new('REFERENCE_Vanilla_Door_Metal',mesh); bpy.context.collection.objects.link(ref); ref.location=(5.5,0,1.4)
for a in bpy.context.screen.areas:
    if a.type=='VIEW_3D':
        a.spaces.active.shading.type='MATERIAL'; a.spaces.active.region_3d.view_distance=11
        a.spaces.active.region_3d.view_location=Vector((.4,0,1.45))
        direction=Vector((.35,-1,.15)); a.spaces.active.region_3d.view_rotation=direction.to_track_quat('Z','Y')
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'Plate_Glass_Doors.blend'))
print(json.dumps({'models':[o.name for o in objects],'pane_native':{'centre':[0,0,1.48],'size':[2.13,2.32]},'pivot_native':[1.145,0,0]}))