# Numeric Godot4.6 oracle for Deployable.BuildMesh/Spawn and Barricade seating.
# Run with --headless --script THIS -- CATALOG_JSON CONTENT_ROOT OUTPUT_JSON.
# No renderer, screenshots or visual goldens are involved.
extends SceneTree
func v(d):
    return Vector3(d.x, d.y, d.z)
func arr(p):
    return [p.x, p.y, p.z]
func _initialize():
    var args = OS.get_cmdline_user_args()
    assert(args.size() == 3, 'Expected CATALOG_JSON CONTENT_ROOT OUTPUT_JSON')
    var defs = JSON.parse_string(FileAccess.get_file_as_string(args[0]))
    var rows = []
    for d in defs:
        if d.fixture != 'None' or d.get('fluid') != null or d.isStorage or d.isWindTurbine:
            continue
        var ab: AABB
        if d.procBox:
            var size = v(d.size)
            ab = AABB(-size * 0.5, size)
        else:
            var file = FileAccess.open(args[1] + '/objects/' + d.model + '.obj', FileAccess.READ)
            var lo = Vector3(INF,INF,INF)
            var hi = Vector3(-INF,-INF,-INF)
            while not file.eof_reached():
                var line = file.get_line()
                if not line.begins_with('v '): continue
                var xyz = line.split(' ', false)
                var p = Vector3(float(xyz[1]),float(xyz[2]),float(xyz[3]))
                lo = lo.min(p)
                hi = hi.max(p)
            ab = AABB(lo,hi-lo)
        var mr = Basis.from_euler(v(d.meshEuler) * PI / 180.0)
        ab = Transform3D(mr,Vector3.ZERO) * ab
        for yaw in [0.0,37.0,120.0]:
            for up in ([false,true] if d.mount == 'Container' else [false]):
                var point = Vector3(10,20,30)
                var stand = Basis(Vector3.RIGHT,PI*0.5)
                var basis = Basis(Vector3.UP,deg_to_rad(yaw)) * (Basis.IDENTITY if d.upright else stand)
                var normal = Vector3.UP
                if d.mount == 'Ceiling': normal = Vector3.DOWN
                elif d.mount in ['Wall','Container'] and not up:
                    normal = Vector3(sin(deg_to_rad(yaw)),0,cos(deg_to_rad(yaw)))
                var origin = point
                if d.mount in ['Ceiling','Wall','Container']:
                    var standoff = 0.005 if d.mount == 'Ceiling' else (d.size.y*0.5+0.01 if d.mount == 'Container' else min(d.offset,0.1))
                    origin += normal * standoff
                else:
                    var min_y = INF
                    for i in range(8):
                        var corner = ab.position + ab.size * Vector3(i & 1,(i >> 1) & 1,(i >> 2) & 1)
                        min_y = min(min_y,(stand*corner).y)
                    origin.y += -ab.position.y if d.upright else -min_y
                if d.mount == 'Container' and up:
                    var f = Vector3(sin(deg_to_rad(yaw)),0,cos(deg_to_rad(yaw)))
                    basis = Basis(f.cross(Vector3.UP).normalized(),PI*0.5) * basis
                rows.append({'id':int(d.id),'yaw':yaw,'mountUp':up,'min':arr(ab.position),'max':arr(ab.end),'position':arr(origin),'basis':[arr(basis.x),arr(basis.y),arr(basis.z)]})
    var file = FileAccess.open(args[2],FileAccess.WRITE)
    file.store_string('[\n')
    for i in range(rows.size()):
        file.store_string(JSON.stringify(rows[i]) + (',\n' if i + 1 < rows.size() else '\n'))
    file.store_string(']\n')
    print('SOURCE_DEPLOYABLE_REFERENCE_ROWS=',rows.size())
    quit()
