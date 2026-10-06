extends Node
var vp: SubViewport
var rig: Node3D
var cam: Camera3D
func xyz(v:Vector3)->Vector3: return Vector3(-v.x,-v.z,-v.y)
func mesh_file(stem:String,texname:String,pos:Vector3=Vector3.ZERO)->MeshInstance3D:
	var vertices:Array[Vector3]=[]
	var ns:Array[Vector3]=[]
	var uv:Array[Vector2]=[]
	var coords=PackedVector3Array()
	var normals=PackedVector3Array()
	var tex=PackedVector2Array()
	for raw in FileAccess.get_file_as_string("res://content/"+stem+".txt").split("\n"):
		var t=raw.split(" ",false)
		if t.is_empty(): continue
		if t[0]=="v": vertices.append(xyz(Vector3(float(t[1]),float(t[2]),float(t[3]))))
		elif t[0]=="vn": ns.append(xyz(Vector3(float(t[1]),float(t[2]),float(t[3]))))
		elif t[0]=="vt": uv.append(Vector2(float(t[1]),1-float(t[2])))
		elif t[0]=="f":
			var f:Array=[]
			for i in range(1,4):
				var e=t[i].split("/");f.append([int(e[0])-1,int(e[1])-1,int(e[2])-1])
			if (vertices[f[1][0]]-vertices[f[0][0]]).cross(vertices[f[2][0]]-vertices[f[0][0]]).dot(ns[f[0][2]])>0:
				var tmp=f[1];f[1]=f[2];f[2]=tmp
			for e in f: coords.append(vertices[e[0]]);normals.append(ns[e[2]]);tex.append(uv[e[1]])
	var a:Array=[];a.resize(Mesh.ARRAY_MAX);a[Mesh.ARRAY_VERTEX]=coords;a[Mesh.ARRAY_NORMAL]=normals;a[Mesh.ARRAY_TEX_UV]=tex
	var m=ArrayMesh.new();m.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES,a)
	var mat=StandardMaterial3D.new();mat.albedo_texture=ImageTexture.create_from_image(Image.load_from_file(ProjectSettings.globalize_path("res://content/"+texname)));mat.texture_filter=BaseMaterial3D.TEXTURE_FILTER_NEAREST;mat.metallic_specular=0;mat.roughness=1;mat.cull_mode=BaseMaterial3D.CULL_BACK
	var obj=MeshInstance3D.new();obj.mesh=m;obj.material_override=mat;obj.position=xyz(pos);return obj
func _ready()->void:
	RenderingServer.set_default_clear_color(Color(0,0,0,0))
	vp=SubViewport.new();vp.size=Vector2i(256,256);vp.own_world_3d=true;vp.transparent_bg=true;vp.render_target_update_mode=SubViewport.UPDATE_ALWAYS;vp.msaa_3d=Viewport.MSAA_2X;add_child(vp)
	var stage=Node3D.new();vp.add_child(stage)
	var env=Environment.new();env.background_mode=Environment.BG_CLEAR_COLOR;env.ambient_light_source=Environment.AMBIENT_SOURCE_COLOR;env.ambient_light_color=Color.WHITE;env.ambient_light_energy=.9;env.tonemap_mode=Environment.TONE_MAPPER_LINEAR
	var w=WorldEnvironment.new();w.environment=env;stage.add_child(w)
	var light=DirectionalLight3D.new();light.light_energy=.65;light.rotation_degrees=Vector3(-40,-25,0);stage.add_child(light)
	rig=Node3D.new();stage.add_child(rig)
	cam=Camera3D.new();cam.projection=Camera3D.PROJECTION_ORTHOGONAL;cam.current=true;cam.near=.001;stage.add_child(cam)
	for id in [9145,9146,9147]:
		var objs:Array[MeshInstance3D]=[]
		if id==9145:
			objs=[mesh_file("mac10_gun","mac10_albedo.png"),mesh_file("mac10_sight","mac10_sight_albedo.png",Vector3(0,-.197,-.188)),mesh_file("mag_mac10","mag_mac10_albedo.png",Vector3(0,-.027797834,.17472924))]
		elif id==9146: objs=[mesh_file("mag_mac10","mag_mac10_albedo.png")]
		else: objs=[mesh_file("mac10_sight","mac10_sight_albedo.png")]
		var bb:AABB=objs[0].get_aabb()
		for obj in objs: rig.add_child(obj);bb=bb.merge(AABB(obj.get_aabb().position+obj.position,obj.get_aabb().size))
		rig.position=-bb.get_center();cam.size=max(bb.size.length(),.1)*1.18;cam.position=Vector3(3,0,0) # straight author-right / ejection-port-side profile
		cam.look_at(Vector3.ZERO,Vector3.UP)
		for i in range(5): await get_tree().process_frame
		await RenderingServer.frame_post_draw
		var img=vp.get_texture().get_image();assert(img.save_png(ProjectSettings.globalize_path("res://content/items/icons/"+str(id)+".png"))==OK)
		print("ICON_SAVED ",id)
		for obj in objs: rig.remove_child(obj);obj.queue_free()
		rig.position=Vector3.ZERO
	get_tree().quit(0)
