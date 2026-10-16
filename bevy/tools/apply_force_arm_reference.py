#!/usr/bin/env python3
"""Observe the lever arm Godot 4.6 (Jolt) gives RigidBody3D.ApplyForce.

Vehicle.UpdateTow calls `ApplyForce(f, point - ToGlobal(CenterOfMass))`. This
measures what Godot does with that position: each case is a zero-gravity,
undamped body with a custom centre of mass and inertia, given one tick of
force at a position offset, then its angular velocity is read back. The
recorded rows let the native port check its torque against the engine the
source ran on rather than against the API's prose.
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
const CASES = [
	{"yaw": 0.0, "com": [0, -0.5, 0], "offset": [0, 0, 0], "force": [0, 0, 1000]},
	{"yaw": 0.0, "com": [0, -0.5, 0], "offset": [0, -0.5, 0], "force": [0, 0, 1000]},
	{"yaw": 0.0, "com": [0, -0.5, 0], "offset": [0, 0.5, 0], "force": [0, 0, 1000]},
	{"yaw": 90.0, "com": [0.3, -0.4, 0.6], "offset": [0.2, 0.1, -1.0], "force": [300, 0, 800]},
]
var bodies := []
var frames := 0
func _ready():
	for i in CASES.size():
		var c = CASES[i]
		var b := RigidBody3D.new(); b.mass = 1000; b.gravity_scale = 0
		b.linear_damp_mode = RigidBody3D.DAMP_MODE_REPLACE; b.angular_damp_mode = RigidBody3D.DAMP_MODE_REPLACE
		b.linear_damp = 0; b.angular_damp = 0
		b.center_of_mass_mode = RigidBody3D.CENTER_OF_MASS_MODE_CUSTOM
		b.center_of_mass = Vector3(c["com"][0], c["com"][1], c["com"][2])
		b.inertia = Vector3(1000, 1000, 1000)
		var s := CollisionShape3D.new(); var bx := BoxShape3D.new(); bx.size = Vector3.ONE; s.shape = bx; b.add_child(s)
		b.position = Vector3(i * 10, 0, 0); b.rotation_degrees = Vector3(0, c["yaw"], 0)
		add_child(b); bodies.append(b)
func _physics_process(_d):
	frames += 1
	if frames == 3:
		for i in CASES.size():
			var c = CASES[i]
			bodies[i].apply_force(Vector3(c["force"][0], c["force"][1], c["force"][2]), Vector3(c["offset"][0], c["offset"][1], c["offset"][2]))
	if frames == 5:
		var rows := []
		for i in CASES.size():
			var b: RigidBody3D = bodies[i]; var w = b.angular_velocity
			var row = CASES[i].duplicate(); row["angularVelocity"] = [w.x, w.y, w.z]
			row["inertia"] = 1000.0; row["dt"] = 1.0 / Engine.physics_ticks_per_second
			var g = b.global_transform.basis * b.center_of_mass
			row["comGlobalOffset"] = [g.x, g.y, g.z]
			rows.append(row)
		var f = FileAccess.open(OS.get_environment("UG_ARM_REFERENCE"), FileAccess.WRITE)
		f.store_string(JSON.stringify(rows)); f.close()
		print("APPLY_FORCE_CASES=", rows.size()); get_tree().quit()
'''

with tempfile.TemporaryDirectory(prefix='apply-force-godot-') as temp:
    root = Path(temp)
    (root / 'project.godot').write_text(
        'config_version=5\n[application]\nconfig/name="Source ApplyForce arm"\n'
        'run/main_scene="res://main.tscn"\n[physics]\n3d/physics_engine="JoltPhysics3D"\n'
        'common/physics_ticks_per_second=50\n')
    (root / 'main.tscn').write_text(
        '[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://oracle.gd" id="1"]\n'
        '[node name="Oracle" type="Node3D"]\nscript=ExtResource("1")\n')
    (root / 'oracle.gd').write_text(SCRIPT)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    env = {'UG_ARM_REFERENCE': str(output), 'PATH': '/usr/bin:/bin'}
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)],
                   cwd=root, env=env, check=True, timeout=60)
for row in json.loads(output.read_text()):
    print(row['yaw'], row['com'], row['offset'], [round(v, 6) for v in row['angularVelocity']])
