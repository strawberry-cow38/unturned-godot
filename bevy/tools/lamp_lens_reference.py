#!/usr/bin/env python3
"""Observe unmodified ObjMesh.Load/SplitByUvAndPos carving the source lamp lens.

The predicate is Deployable.SplitLampLens's, transcribed with its constants
(UV centroid u>0.5 && v>0.5, position centroid within BulbReach 0.42 of one of
the def's DeployLight positions); everything else is the original loader.
"""
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
# DeployableDef.Spotlight / Cagelight Lights[].Pos (game/DeployableDef.cs).
cases = [
    dict(id=459, model='Spotlight_deploy', lamps=[[-0.490, -0.400, -1.353], [0.490, -0.400, -1.353]]),
    dict(id=1222, model='Cagelight_0', lamps=[[0, 0.131, 0]]),
]
runner = r'''using Godot;using System;using System.Linq;using System.Collections.Generic;using System.Text.Json;
using UnturnedGodot;
public partial class Oracle:Node {
static float[] A(Vector3 p)=>new[]{p.X,p.Y,p.Z};
public override void _Ready(){
 var rows=new List<object>();
 foreach(var c in JsonDocument.Parse(System.IO.File.ReadAllText("input.json")).RootElement.EnumerateArray()) {
  var lamps=c.GetProperty("lamps").EnumerateArray().Select(l=>new Vector3(l[0].GetSingle(),l[1].GetSingle(),l[2].GetSingle())).ToList();
  var src=ObjMesh.Load(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("UG_CONTENT"),"objects",c.GetProperty("model").GetString()+".obj"));
  const float BulbReach=0.42f;
  var (body,lens)=ObjMesh.SplitByUvAndPos(src,uv=>uv.X>0.5f&&uv.Y>0.5f,pos=>{foreach(var lp in lamps) if(pos.DistanceTo(lp)<=BulbReach) return true;return false;});
  int total=src.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length/3;
  var centroids=new List<float[]>();
  if(lens!=null){var v=lens.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
   for(int i=0;i+2<v.Length;i+=3) centroids.Add(A((v[i]+v[i+1]+v[i+2])/3f));}
  rows.Add(new{id=c.GetProperty("id").GetInt32(),model=c.GetProperty("model").GetString(),triangles=total,lens=centroids});
 }
 System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_LENS_REFERENCE"),JsonSerializer.Serialize(rows));
 GD.Print("SOURCE_LENS_CASES=",rows.Count);GetTree().Quit();
}}
'''
with tempfile.TemporaryDirectory(prefix='lamp-lens-godot-') as temp:
    root = Path(temp)
    shutil.copyfile(repo / 'game' / 'ObjMesh.cs', root / 'ObjMesh.cs')
    (root / 'Oracle.cs').write_text(runner)
    (root / 'input.json').write_text(json.dumps(cases))
    (root / 'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Source lamp lens"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    env = os.environ.copy()
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    env['UG_CONTENT'] = str(repo / 'game' / 'content')
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    env['UG_LENS_REFERENCE'] = str(output)
    subprocess.run([shutil.which('dotnet', path=env['PATH']), 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, env=env, check=True)
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)], cwd=root, env=env, check=True, timeout=120)
    print(output.read_text()[:400])
