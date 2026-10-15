# Godot4.6 renderer reference for examples/alpha_depth.rs (manual, no golden gate).
# xvfb-run godot --rendering-driver vulkan --write-movie /tmp/alpha.avi \
#   --fixed-fps 30 --script bevy/tools/alpha_depth_reference.gd -- /tmp/godot-alpha.png
extends SceneTree
var frames := 0
var output := "/tmp/godot-alpha.png"
func _initialize():
    root.size = Vector2i(1280, 720)
    if OS.get_cmdline_user_args().size() > 0:
        output = OS.get_cmdline_user_args()[0]
    var world := Node3D.new()
    root.add_child(world)
    var env := WorldEnvironment.new()
    env.environment = Environment.new()
    env.environment.background_mode = Environment.BG_COLOR
    env.environment.background_color = Color(0.12, 0.15, 0.19)
    env.environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
    world.add_child(env)
    var camera := Camera3D.new()
    camera.fov = 45.0
    camera.position = Vector3(0, 0, 8)
    world.add_child(camera)
    camera.current = true
    for i in range(6):
        var a: float = [13,25,26,252,253,255][i] / 255.0
        var image := Image.create(2, 1, false, Image.FORMAT_RGBA8)
        image.set_pixel(0,0,Color(0,1,0,a))
        image.set_pixel(1,0,Color(1,0,0,1))
        var tex := ImageTexture.create_from_image(image)
        for row in range(2):
            var material := StandardMaterial3D.new()
            material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
            material.cull_mode = BaseMaterial3D.CULL_DISABLED
            material.texture_filter = BaseMaterial3D.TEXTURE_FILTER_NEAREST
            material.albedo_texture = tex
            material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA if row == 0 else BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
            var vertices := PackedVector3Array()
            var normals := PackedVector3Array()
            var uvs := PackedVector2Array()
            for plane in range(2):
                for p in [Vector2(-0.4,-0.4),Vector2(0.4,-0.4),Vector2(0.4,0.4),Vector2(-0.4,-0.4),Vector2(0.4,0.4),Vector2(-0.4,0.4)]:
                    vertices.append(Vector3(p.x,p.y,-0.3*plane))
                    normals.append(Vector3(0,0,1))
                    uvs.append(Vector2(0.25 if plane == 0 else 0.75,0.5))
            var arrays := []
            arrays.resize(Mesh.ARRAY_MAX)
            arrays[Mesh.ARRAY_VERTEX] = vertices
            arrays[Mesh.ARRAY_NORMAL] = normals
            arrays[Mesh.ARRAY_TEX_UV] = uvs
            var mesh := ArrayMesh.new()
            mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES,arrays)
            var node := MeshInstance3D.new()
            node.mesh = mesh
            node.material_override = material
            node.position = Vector3((i-2.5)*1.05,0.7 if row == 0 else -0.7,0)
            world.add_child(node)
    process_frame.connect(capture)
func capture():
    frames += 1
    if frames != 60:
        return
    await RenderingServer.frame_post_draw
    root.get_texture().get_image().save_png(output)
    quit()
