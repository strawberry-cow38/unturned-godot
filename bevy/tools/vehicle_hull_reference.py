#!/usr/bin/env python3
"""Evaluate unchanged Vehicle.ConvexBand/Hull2D in Godot; numeric oracle, no golden images."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
from export_vehicles import declaration

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
source = (repo / 'game/Vehicle.cs').read_text()
methods = '\n'.join(declaration(source, marker, True) for marker in [
    'static ConvexPolygonShape3D ConvexBand(',
    'static System.Collections.Generic.List<Vector2> Hull2D(', 'static Mesh MeshRegion('])
methods += '\n'+declaration(source, 'static bool In(', False)
rows = []
for d in json.loads((repo / 'bevy/data/vehicles.json').read_text())['vehicles']:
    s = d['spec']
    if not s['HullBands']:
        continue
    vertices, faces = [], []
    for line in (repo / 'game/content' / s['Body']).read_text().splitlines():
        f = line.split()
        if f and f[0] == 'v': vertices.append([float(x) for x in f[1:4]])
        elif f and f[0] == 'f': faces.extend(vertices[int(v.split('/')[0])-1] for v in f[1:4])
    rows.append({'name':d['key'], 'vertices':faces, 'bands':s['HullBands'], 'region':s['HullTrimesh']})
# Exercise source rounding and exact distinct points independently of today's ship mesh.
rows.append({'name':'rounding', 'vertices':[[x,y,z] for y in [-.0015,.0025] for x,z in [(-1,-1),(1,-1),(1,1),(-1,1),(0,0),(0,0)]], 'bands':[[[-1,-1,-1],[1,1,1]]]})
rows.append({'name':'snap', 'vertices':[[-.00015,-.00005,.00005],[.00015,9.999994,11.000007],[1.00004,1.00006,-1.00004]], 'bands':[]})
rows.append({'name':'region_boundary', 'vertices':[[-.00004,0,0],[1.00004,.5,0],[0,.5,.5],[-.00004,0,0],[1.00006,.5,0],[0,.5,.5]], 'bands':[], 'region':[[0,-1,-1],[1,1,1]]})
runner = '''using Godot;
using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
public partial class Oracle : Node {
static Vector3 V(JsonElement v) => new(v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle());
static float[] A(Vector3 p) => new[]{p.X,p.Y,p.Z};
public override void _Ready() {
var input=JsonDocument.Parse(System.IO.File.ReadAllText("res_input.json"));
var result=new List<object>();
foreach(var row in input.RootElement.EnumerateArray()) {
 var mesh=new ArrayMesh(); var arrays=new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
 arrays[(int)Mesh.ArrayType.Vertex]=row.GetProperty("vertices").EnumerateArray().Select(V).ToArray();
 mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
 var bands=row.GetProperty("bands").EnumerateArray().ToArray();
 var shape=bands.Length>0 ? ConvexBand(mesh,V(bands[0][0]),V(bands[0][1])) : null;
 var region=row.TryGetProperty("region",out var bounds) ? MeshRegion(mesh,V(bounds[0]),V(bounds[1])) : null;
 result.Add(new {name=row.GetProperty("name").GetString(),points=shape?.Points.Select(A).ToArray(),faces=mesh.GetFaces().Select(A).ToArray(),region=region?.GetFaces().Select(A).ToArray(),vertices=row.GetProperty("vertices")});
}
System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_HULL_ORACLE"),JsonSerializer.Serialize(result));
GD.Print("SOURCE_HULL_ORACLE_ROWS=",result.Count); GetTree().Quit();
}
'''+methods+'\n}\n'
output=args.output.resolve();output.parent.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='vehicle-hull-godot-') as tmp:
    root=Path(tmp)
    (root/'Oracle.cs').write_text(runner)
    (root/'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>')
    (root/'project.godot').write_text('config_version=5\n[application]\nconfig/name="Vehicle source hull oracle"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root/'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    (root/'res_input.json').write_text(json.dumps(rows))
    subprocess.run([shutil.which('dotnet') or str(Path.home()/'.dotnet/dotnet'),'build','Oracle.csproj','--nologo','-v:q'],cwd=root,check=True)
    env=os.environ.copy();env['UG_HULL_ORACLE']=str(output)
    env['DOTNET_ROOT']=str(Path.home()/'.dotnet')
    env['PATH']=env['DOTNET_ROOT']+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.godot.resolve()),'--headless','--path',str(root)],cwd=root,env=env,check=True,timeout=40)
