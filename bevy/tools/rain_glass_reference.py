#!/usr/bin/env python3
"""Render the unchanged original RainGlass math on its actual police pane.

Only TIME becomes a uniform at1.2/2.4s for repeatable source/native comparison.
This is an opt-in rendered reference, not a stored visual-golden gate.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--godot", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True, help="PNG prefix")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
shader = repo / "game/content/rain_glass.gdshader"
pane = repo / "game/content/police_glass_windshield.txt"
rows = json.loads((repo / "bevy/data/vehicles.json").read_text())["vehicles"]
spec = next(row["spec"] for row in rows if row["key"] == "police")
vertices, faces = [], []
for line in pane.read_text().splitlines():
    fields = line.split()
    if fields and fields[0] == "v":
        vertices.append([float(v) for v in fields[1:4]])
    if fields and fields[0] == "f":
        faces.extend(vertices[int(v.split("/")[0]) - 1] for v in fields[1:4])
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="rain-glass-godot-") as tmp:
    root = Path(tmp)
    source = shader.read_text().replace("TIME", "fixture_time").replace(
        "shader_type spatial;", "shader_type spatial;\nuniform float fixture_time = 1.2;"
    )
    (root / "rain.gdshader").write_text(source)
    (root / "fixture.json").write_text(json.dumps({"vertices": faces, "tint": spec.get("GlassTint") or [.78, .62, .30, .4]}))
    (root / "main.gd").write_text(Path(__file__).with_suffix(".gd").read_text())
    (root / "main.tscn").write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://main.gd" id="1"]\n[node name="Scene" type="Node3D"]\nscript = ExtResource("1")\n')
    (root / "project.godot").write_text('config_version=5\n[application]\nconfig/name="Original RainGlass reference"\nrun/main_scene="res://main.tscn"\n[display]\nwindow/size/viewport_width=1100\nwindow/size/viewport_height=720\n')
    env = os.environ.copy()
    env.pop("WAYLAND_DISPLAY", None)
    env["UG_RAIN_GLASS_REFERENCE"] = str(output)
    subprocess.run(["xvfb-run", "-a", str(args.godot.resolve()), "--path", str(root), "--rendering-driver", "vulkan", "--fixed-fps", "50", "--write-movie", str(root / "reference.avi")], env=env, check=True)
    output.with_suffix(".json").write_text(json.dumps({"source": str(shader.relative_to(repo)), "shaderSha256": hashlib.sha256(shader.read_bytes()).hexdigest(), "mesh": str(pane.relative_to(repo)), "times": [1.2,2.4], "weather": [1,1], "adaptation": "TIME uniform only; original mesh has no authored normals; controlled material fixture, not fullgame driving"}, indent=2))
