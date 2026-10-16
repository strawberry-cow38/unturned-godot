#!/usr/bin/env python3
"""Observe where Godot 4.6 (Jolt) leaves a VehicleWheel3D node and what the
original Vehicle.WheelExtension ray reads from there.

Vehicle.cs measures anti-roll extension from `w.GlobalPosition`. Godot's
VehicleBody3D moves each wheel node to hard point + direction x suspension
length every step, so the reading is an air gap, not a compression. This runs
the same ray (bit0 mask, own RID excluded, radius+rest+travel reach) on a real
VehicleBody3D: level on a floor, and with its right-front wheel hanging over a
pit whose floor is 0.3 m (ground in reach) or 0.8 m (none in reach) lower.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()

SCRIPT = r'''extends Node3D
var cases := []
var car: VehicleBody3D
var frames := 0
var index := 0
var rows := []
func _ready():
	cases = [{"name": "level", "pit": false}, {"name": "right_front_over_pit", "pit": true, "floor": -0.3},
		{"name": "right_front_over_void", "pit": true, "floor": -0.8}]
	build()
func slab(size: Vector3, at: Vector3):
	var body := StaticBody3D.new()
	var shape := CollisionShape3D.new(); var box := BoxShape3D.new(); box.size = size
	shape.shape = box; body.add_child(shape); body.position = at; add_child(body)
func build():
	for c in get_children(): c.queue_free()
	if not cases[index]["pit"]:
		slab(Vector3(50, 1, 50), Vector3(0, -0.5, 0))
	else:
		# Upper floor everywhere except x>0.5, z<0 (under the right-front wheel),
		# with a lower floor (0.3 m down: hangs with ground in reach; 0.8 m: none in reach).
		slab(Vector3(25, 1, 50), Vector3(-12, -0.5, 0))
		slab(Vector3(25, 1, 25), Vector3(13, -0.5, 12.5))
		slab(Vector3(50, 1, 50), Vector3(0, cases[index]["floor"] - 0.5, 0))
	car = VehicleBody3D.new(); car.mass = 1000
	# Rear-left CoM keeps the pit case standing on its three supported wheels.
	car.center_of_mass_mode = RigidBody3D.CENTER_OF_MASS_MODE_CUSTOM
	car.center_of_mass = Vector3(-0.4, 0, 0.6) if cases[index]["pit"] else Vector3.ZERO
	var hull := CollisionShape3D.new(); var hb := BoxShape3D.new(); hb.size = Vector3(0.3, 0.3, 4)
	hull.shape = hb; hull.position = Vector3(-0.9, 0.9, 0); car.add_child(hull)
	for p in [Vector3(-1, 0.5, -1.5), Vector3(1, 0.5, -1.5), Vector3(-1, 0.5, 1.5), Vector3(1, 0.5, 1.5)]:
		var w := VehicleWheel3D.new(); w.position = p
		w.wheel_radius = 0.4; w.wheel_rest_length = 0.25; w.suspension_travel = 0.25
		w.suspension_stiffness = 40; w.suspension_max_force = 30000
		car.add_child(w)
	car.position = Vector3(0, 0.8, 0)
	add_child(car)
	frames = 0
func extension(w: VehicleWheel3D) -> Array:
	var rest = max(0.01, w.wheel_rest_length)
	var from = w.global_position
	var to = from - car.global_transform.basis.y * (w.wheel_radius + rest + w.suspension_travel)
	var q = PhysicsRayQueryParameters3D.create(from, to, 1)
	q.exclude = [car.get_rid()]
	var hit = get_world_3d().direct_space_state.intersect_ray(q)
	if hit.is_empty(): return [null, 1.0]
	var d = from.distance_to(hit["position"])
	return [d, clamp((d - w.wheel_radius) / rest, 0.0, 1.0)]
func _physics_process(_d):
	frames += 1
	if frames < 100: return
	var wheels := []
	for w in car.get_children():
		if w is VehicleWheel3D:
			var e = extension(w)
			# Godot's own suspension ray: hard point (the node's enter-tree origin
			# under the current body pose) to its reported contact point.
			var connection = car.global_transform * Vector3(sign(w.position.x), 0.5, sign(w.position.z) * 1.5)
			var contact = w.get_contact_point() if w.is_in_contact() else null
			wheels.append({"local": [w.position.x, w.position.y, w.position.z], "contact": w.is_in_contact(),
				"hardPointToContact": connection.distance_to(contact) if contact != null else null,
				"radius": w.wheel_radius, "rest": w.wheel_rest_length, "travel": w.suspension_travel,
				"groundDistance": e[0], "extension": e[1]})
	rows.append({"case": cases[index]["name"], "connections": [[-1, 0.5, -1.5], [1, 0.5, -1.5], [-1, 0.5, 1.5], [1, 0.5, 1.5]], "wheels": wheels})
	index += 1
	if index >= cases.size():
		var f = FileAccess.open(OS.get_environment("UG_WHEEL_REFERENCE"), FileAccess.WRITE)
		f.store_string(JSON.stringify(rows)); f.close()
		print("SOURCE_WHEEL_CASES=", rows.size()); get_tree().quit()
	else:
		build()
'''

with tempfile.TemporaryDirectory(prefix='wheel-extension-godot-') as temp:
    root = Path(temp)
    (root / 'project.godot').write_text(
        'config_version=5\n[application]\nconfig/name="Source wheel extension"\n'
        'run/main_scene="res://main.tscn"\n[physics]\n3d/physics_engine="JoltPhysics3D"\n'
        'common/physics_ticks_per_second=50\n')
    (root / 'main.tscn').write_text(
        '[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://oracle.gd" id="1"]\n'
        '[node name="Oracle" type="Node3D"]\nscript=ExtResource("1")\n')
    (root / 'oracle.gd').write_text(SCRIPT)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    env = {'UG_WHEEL_REFERENCE': str(output), 'PATH': '/usr/bin:/bin'}
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)],
                   cwd=root, env=env, check=True, timeout=60)
for row in json.loads(output.read_text()):
    print(row['case'], [(round(w['local'][1], 4), w['contact'], w['hardPointToContact'] and round(w['hardPointToContact'], 4), w['groundDistance'] and round(w['groundDistance'], 4), round(w['extension'], 4)) for w in row['wheels']])
