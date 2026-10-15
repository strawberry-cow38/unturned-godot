# Actual original C# InfoBillboard, not a recreation of its widgets.
# Requires the source Mono assembly, Vulkan/display and --fixed-fps30.
# --path game --script ../bevy/tools/info_billboard_reference.gd -- OUTPUT_PREFIX
extends SceneTree
var frame := 0
var stage := 0
var panel
var wall: MeshInstance3D
var prefix := '/tmp/godot-info'
func _initialize():
    var args = OS.get_cmdline_user_args()
    if args.size() > 0: prefix = args[0]
    root.size = Vector2i(1000,760)
    var world = Node3D.new()
    root.add_child(world)
    var env = WorldEnvironment.new()
    env.environment = Environment.new()
    env.environment.background_mode = Environment.BG_COLOR
    env.environment.background_color = Color(0.15,0.19,0.23)
    env.environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
    world.add_child(env)
    var camera = Camera3D.new()
    camera.fov = 45.0
    world.add_child(camera)
    camera.position = Vector3(0,1,4)
    camera.current = true
    wall = MeshInstance3D.new()
    var mesh = BoxMesh.new()
    mesh.size = Vector3(4,3,0.1)
    wall.mesh = mesh
    var mat = StandardMaterial3D.new()
    mat.albedo_color = Color(0.25,0.28,0.31)
    mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
    wall.material_override = mat
    wall.position = Vector3(0,1,2)
    wall.visible = false
    world.add_child(wall)
    panel = load('res://InfoBillboard.cs').new()
    world.add_child(panel)
    panel.position = Vector3.UP
func configure():
    panel.SetName('Generator  · 45m',Color(0.82,0.83,0.90))
    panel.SetPrompt('[F] Turn Off · Hold [F]: pick up',Color(0.82,0.83,0.90))
    panel.SetBar(0,0.74,Color(191.0/255,31.0/255,31.0/255),true)
    panel.SetBar(1,0.42,Color(220.0/255,180.0/255,19.0/255),true)
    panel.SetBar(2,0.60,Color(53.0/255,192.0/255,224.0/255),true)
    panel.SetActive(true)
func _process(_delta):
    frame += 1
    if stage == 0 and frame == 1: configure()
    if frame < 24: return
    root.get_texture().get_image().save_png(prefix+'-'+str(stage)+'.png')
    panel.get_child(0).get_texture().get_image().save_png(prefix+'-canvas-'+str(stage)+'.png')
    if stage != 3:
        var ui = panel.get_child(0).get_child(0)
        for label in [ui.get_child(0),ui.get_child(6)]:
            var font = label.get_theme_font('font')
            var size = label.get_theme_font_size('font_size')
            print('SOURCE_INFO_FONT ',label.text,' size=',size,' ascent=',font.get_ascent(size),' width=',font.get_string_size(label.text,HORIZONTAL_ALIGNMENT_CENTER,-1,size).x)
    print('SOURCE_INFO_STAGE=',stage)
    stage += 1
    frame = 0
    if stage == 1: wall.visible = true
    elif stage == 2:
        panel.SetName('A deliberately long original generator name',Color(0.82,0.83,0.90))
        panel.SetPrompt('Picking up... 81%',Color(0.82,0.83,0.90))
    elif stage == 3: panel.SetActive(false)
    else: quit()
