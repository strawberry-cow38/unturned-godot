#!/usr/bin/env python3
"""Observe unmodified Wire.SetPoints on real Godot meshes and transforms."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
routes = [
    [[3, -2, 7], [4, -2, 7]],
    [[3, -2, 7], [3, -1, 7]],
    [[3, -2, 7], [3, -3, 7]],
    [[3, -2, 7], [0, 2, 2]],
    [[0, 0, 0], [.001, 1, 0]],
    [[0, 0, 0], [.001, -1, 0]],
    [[0, 0, 0], [0, .00001, 0]],
    [[-3, 1, -2], [2, 1, 3], [1, 4, -1], [1, 4, -1]],
    [[0, 0, 0], [0, 2, 0]],
    [],
]
runner = r'''using Godot;using System;using System.Linq;using System.Collections.Generic;using System.Text.Json;
using UnturnedGodot;
public partial class Oracle:Node {
static float[] A(Vector3 p)=>new[]{p.X,p.Y,p.Z};
public override void _Ready(){
 var wire=new Wire();AddChild(wire);var rows=new List<object>();
 foreach(var route in JsonDocument.Parse(System.IO.File.ReadAllText("input.json")).RootElement.EnumerateArray()) {
  var points=route.EnumerateArray().Select(p=>new Vector3(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle())).ToList();
  wire.SetPoints(points,true);
  rows.Add(new{points=points.Select(A),length=wire.TotalLength(),segments=wire.GetChildren().OfType<MeshInstance3D>().Select(m=>new{
   visible=m.Visible,position=A(m.GlobalPosition),basis=new[]{A(m.GlobalBasis.X),A(m.GlobalBasis.Y),A(m.GlobalBasis.Z)},
   radius=((CylinderMesh)m.Mesh).TopRadius,height=((CylinderMesh)m.Mesh).Height,radialSegments=((CylinderMesh)m.Mesh).RadialSegments,
   metallic=((StandardMaterial3D)m.MaterialOverride).Metallic,roughness=((StandardMaterial3D)m.MaterialOverride).Roughness}).ToArray()});
 }
 System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_WIRE_REFERENCE"),JsonSerializer.Serialize(rows));
 GD.Print("SOURCE_WIRE_CASES=",rows.Count);GetTree().Quit();
}}
'''
with tempfile.TemporaryDirectory(prefix='wire-source-godot-') as temp:
    root = Path(temp)
    shutil.copyfile(repo / 'game/Wire.cs', root / 'Wire.cs')
    # Wire's topology types are declared but not read by SetPoints. Keep those
    # names only; the geometry, engine objects and transforms are original.
    (root / 'Ports.cs').write_text('namespace UnturnedGodot {public class ConnectionPort {}}')
    (root / 'Oracle.cs').write_text(runner)
    (root / 'input.json').write_text(json.dumps(routes))
    (root / 'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Source wire geometry"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    env = os.environ.copy()
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    env['UG_WIRE_REFERENCE'] = str(output)
    subprocess.run([shutil.which('dotnet', path=env['PATH']), 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, env=env, check=True)
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)], cwd=root, env=env, check=True, timeout=60)
