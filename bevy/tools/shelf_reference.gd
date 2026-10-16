extends Node
# Godot oracle for the StoreShelf display (bevy/docs/STORE_SHELVES_PORT.md).
# Dumps every display item's shelf-local basis/origin, grid cell, name-tag
# position/scale/modulate, look hitbox and mesh AABB from the actual C#
# StoreShelf, two frames into Main.cs's UG_SHELFDEMO scene. To run it, copy
# this file to game/shelf_dump.gd and register it for one run with an
# untracked game/override.cfg:
#   [autoload]
#   ShelfDump="*res://shelf_dump.gd"
# then: UG_SHELFDEMO=1 UG_SHELF_DUMP=/abs/out.json <godot> --path game
#   --rendering-driver vulkan -- --invcrate --shot=/abs/out.png
# (xvfb + Vulkan, never --headless). Remove both files afterwards.
var frames := 0
func _process(_d):
	frames += 1
	if frames != 2:
		return
	var out := []
	for vis in _all(get_tree().root):
		if not (vis is MeshInstance3D):
			continue
		var label: Label3D = null
		var body = null
		for c in vis.get_children():
			if c is Label3D: label = c
			elif c is StaticBody3D: body = c
		if label == null or body == null:
			continue
		var shelf = vis.get_parent()
		var b: Basis = vis.transform.basis
		var cell = body.get("CellKey")
		var shape: CollisionShape3D = body.get_child(0)
		var a: AABB = vis.mesh.get_aabb()
		var bs: BoxShape3D = shape.shape
		out.append({"name": label.text, "cell": cell, "shelfYaw": shelf.rotation_degrees.y,
			"bx": [b.x.x, b.x.y, b.x.z], "by": [b.y.x, b.y.y, b.y.z], "bz": [b.z.x, b.z.y, b.z.z],
			"origin": [vis.position.x, vis.position.y, vis.position.z],
			"tag": [label.position.x, label.position.y, label.position.z],
			"tagScale": label.scale.x,
			"aabb": [a.position.x, a.position.y, a.position.z, a.size.x, a.size.y, a.size.z],
			"box": [shape.position.x, shape.position.y, shape.position.z, bs.size.x, bs.size.y, bs.size.z],
			"modulate": [label.modulate.r, label.modulate.g, label.modulate.b, label.modulate.a],
			"pixel": label.pixel_size, "font": label.font_size, "outline": label.outline_size, "nodepth": label.no_depth_test})
	var f := FileAccess.open(OS.get_environment("UG_SHELF_DUMP"), FileAccess.WRITE)
	f.store_string(JSON.stringify(out, "  "))
	f.close()
	print("[shelf-dump] wrote %d items" % out.size())
func _all(n: Node) -> Array:
	var r := [n]
	for c in n.get_children():
		r.append_array(_all(c))
	return r
