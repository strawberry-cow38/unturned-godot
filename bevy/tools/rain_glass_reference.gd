extends Node3D
var mat: ShaderMaterial
var frame := 0
func _ready():
    RenderingServer.global_shader_parameter_add("rain_wetness", RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0)
    RenderingServer.global_shader_parameter_add("rain_intensity", RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0)
    var data = JSON.parse_string(FileAccess.get_file_as_string("res://fixture.json"))
    var vertices: Array[Vector3] = []
    for a in data.vertices: vertices.append(Vector3(a[0],a[1],a[2]))
    var lo := vertices[0]
    var hi := lo
    for v in vertices:
        lo = lo.min(v)
        hi = hi.max(v)
    var center := (lo+hi)*0.5
    var axis := (vertices[1]-vertices[0]).cross(vertices[2]-vertices[0]).normalized()
    if axis.z < 0: axis = -axis
    var normal := -axis
    var mesh = SurfaceTool.new()
    mesh.begin(Mesh.PRIMITIVE_TRIANGLES)
    # The original file has no vn; ContentProvider also adds these vertices
    # without manufacturing vertex normals.
    for v in vertices: mesh.add_vertex(v)
    var pane = MeshInstance3D.new()
    pane.mesh = mesh.commit()
    mat = ShaderMaterial.new()
    mat.shader = load("res://rain.gdshader")
    var c = data.tint
    mat.set_shader_parameter("tint",Color(c[0],c[1],c[2],c[3]))
    mat.set_shader_parameter("metallic",0.35)
    mat.set_shader_parameter("roughness",0.1)
    pane.material_override = mat
    add_child(pane)
    pane.set_instance_shader_parameter("pane_axis",axis)
    pane.set_instance_shader_parameter("covered",1.0)
    pane.name = "OriginalPane"
    var camera = Camera3D.new()
    camera.fov = 45
    add_child(camera)
    camera.position = center + normal*1.4
    camera.look_at(center,Vector3.UP)
    var env = WorldEnvironment.new()
    env.environment = Environment.new()
    env.environment.background_mode = Environment.BG_COLOR
    env.environment.background_color = Color(.15,.18,.22)
    env.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
    env.environment.ambient_light_color = Color.WHITE
    env.environment.ambient_light_energy = 0.22
    add_child(env)
    var light = DirectionalLight3D.new()
    light.light_energy = 1.0
    add_child(light)
    light.position = Vector3(-3,7,-5)
    light.look_at(center,Vector3.UP)
    var back = Transform3D(Basis.IDENTITY,center-normal*.6).looking_at(center-normal*1.6,Vector3.UP)
    var colors = [Color(.16,.20,.26),Color(.75,.65,.45)]
    for y in range(-10,11):
        for x in range(-15,16):
            var tile = MeshInstance3D.new()
            tile.mesh = QuadMesh.new()
            tile.mesh.size = Vector2(.18,.18)
            var m = StandardMaterial3D.new()
            m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
            m.cull_mode = BaseMaterial3D.CULL_DISABLED
            m.albedo_color = colors[(x+y+40)%2]
            tile.material_override = m
            tile.transform = Transform3D(back.basis,back*Vector3(x*.18,y*.18,0))
            add_child(tile)
func _process(_dt):
    frame += 1
    $OriginalPane.set_instance_shader_parameter("covered",1.0 if frame<50 or frame>=110 else 0.0)
    mat.set_shader_parameter("fixture_time",1.2 if frame<80 else 2.4)
    var label = {40:"dry",70:"wet",100:"later",130:"covered"}.get(frame,"")
    if label != "":
        await RenderingServer.frame_post_draw
        get_viewport().get_texture().get_image().save_png(OS.get_environment("UG_RAIN_GLASS_REFERENCE")+"-"+label+".png")
    if frame>140: get_tree().quit()
