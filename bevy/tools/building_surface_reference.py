#!/usr/bin/env python3
"""Run original WallSurface geometry in Godot; numeric oracle, no pixel goldens.

Only the three independent door/glass/barricade scene calls are removed. The
wall/trim/back mesh, materials and collision run on real engine resources.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', type=Path, required=True)
parser.add_argument('--sim-assembly', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
source = (repo / 'game/WallSurface.cs').read_text()
wall = source[:source.index('        // ---- glazing')]
for call in ['RebuildGlass', 'RebuildDoors', 'RebuildBarricades']:
    old = '            ' + call + '();'
    assert wall.count(old) == 1
    wall = wall.replace(old, '')
# Observe the retained SurfaceTool arrays after committing each nonempty mesh.
# This avoids ArrayMesh's packed-normal readback, without calling CommitToArrays
# on an empty tool (which logs an engine error). Source values are unchanged.
for node, tool in [('_mesh', 'st'), ('_backMesh', 'back'), ('_trimMesh', 'tt')]:
    old = f'            {node}.Mesh = {tool}.Commit();'
    assert wall.count(old) == 1
    name = {'_mesh': 'Mesh', '_backMesh': 'BackMesh', '_trimMesh': 'TrimMesh'}[node]
    wall = wall.replace(old, old + f'\n            if ({node}.Mesh != null && {node}.Mesh.GetSurfaceCount()>0) Oracle.Raw[(this,"{name}")] = {tool}.CommitToArrays();')
wall += source[source.index('        void AddGableCap('):source.index('        // ---- wall space')]
wall += '\n    }\n}\n'

# Original WallSave input, including asymmetric and empty cases. These exercise
# resource assembly independently of the adapter's generated class and sinks.
def row(name, wall, *openings):
    return dict(name=name, text='wall ' + wall + '\n' + ''.join('  open ' + o + '\n' for o in openings))
rows = [
    row('plain', '0 0 0 0 6 .7 0 3.5 0 Wall'),
    row('door', '0 0 0 0 7 .7 4 3.5 0 Wall', '1.1 0 2.3 3.1 999 0'),
    row('window', '0 0 0 37 6 .5 -3 3.5 0 Wall', '1.4 1 2.2 1.4 999 1'),
    row('overlap', '0 0 0 0 6 .7 0 3.5 0 Wall', '.5 .7 2 1.3 999 1', '2 1 2 1.5 999 1'),
    row('floor', '0 0 0 90 9 .3 2 8 -90 Floor', '2 2 3 4 999 0'),
    row('plumb_taper', '0 0 0 37 9 .3 3 6 -72 Roof 0 -1 0 2 1 .5'),
    row('plumb_mirror', '0 0 0 -119 9 .3 3 6 72 Roof 0 -1 1 .5 0 2'),
    row('taper_opening', '0 0 0 17 9 .5 4 6 0 Wall 0 -1 -.5 3 1 .25', '2 1 3 2 999 1'),
    row('gable', '0 0 0 0 6 .7 0 3.5 0 Wall 1.25'),
    row('two_sided', '0 0 0 0 7 .7 4 3.5 0 Wall 0 3 0 0 0 0 5 1', '1.1 0 2.3 3.1 999 0'),
    row('empty', '0 0 0 0 0 .7 0 3.5 0 Wall'),
    row('all_hole', '0 0 0 0 6 .7 0 3.5 0 Floor', '0 0 6 3.5 999 0'),
]
runner = r'''using Godot;
using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using UnturnedSim;
using UnturnedGodot;
public partial class Oracle : Node {
public static readonly Dictionary<(Node,string),Godot.Collections.Array> Raw = new();
static float[] A(Vector3 p) => new[]{p.X,p.Y,p.Z};
static float[] C(Color p) => new[]{p.R,p.G,p.B,p.A};
static object MeshData(Node parent,string name) {
 var mi=parent.GetNode<MeshInstance3D>(name);
 var m=mi.Mesh;
 if(m==null || m.GetSurfaceCount()==0) return new {vertices=Array.Empty<float[]>(),normals=Array.Empty<float[]>(),color=Array.Empty<float>()};
 var arr=Raw[(parent,name)];
 var vertices=(Vector3[])arr[(int)Mesh.ArrayType.Vertex];
 var normals=(Vector3[])arr[(int)Mesh.ArrayType.Normal];
 var index=(int[])arr[(int)Mesh.ArrayType.Index];
 var order=index.Length>0?index:Enumerable.Range(0,vertices.Length).ToArray();
 return new {vertices=order.Select(i=>A(vertices[i])).ToArray(),normals=order.Select(i=>A(normals[i])).ToArray(),color=C(((StandardMaterial3D)mi.MaterialOverride).AlbedoColor)};
}
static object[] Shapes(Node parent,string name) => parent.GetNode(name).GetChildren().OfType<CollisionShape3D>().Select(c=>new {
 position=A(c.Position),box=c.Shape is BoxShape3D b?A(b.Size):null,
 hull=c.Shape is ConvexPolygonShape3D h?h.Points.Select(A).ToArray():null}).ToArray();
public override void _Ready() {
 var input=JsonDocument.Parse(System.IO.File.ReadAllText("input.json"));
 var result=new List<object>();
 foreach(var row in input.RootElement.EnumerateArray()) {
  var pl=WallSave.Read(row.GetProperty("text").GetString().Split('\n')).Single();
  var wall=new WallSurface {Length=pl.Length,Height=pl.Height,Thickness=pl.Thickness,MaterialId=pl.Material,Kind=pl.Kind,Texel=pl.Texel,GableRise=pl.GableRise,
   InsetL0=pl.InsetL0,InsetL1=pl.InsetL1,InsetR0=pl.InsetR0,InsetR1=pl.InsetR1,MaterialIdBack=pl.MaterialBack,TexelBack=pl.TexelBack,
   RotationDegrees=new Vector3(pl.Pitch,pl.Yaw,0)};
  wall.Openings.AddRange(pl.Openings);AddChild(wall);
  result.Add(new {name=row.GetProperty("name").GetString(),text=row.GetProperty("text").GetString(),
   front=MeshData(wall,"Mesh"),back=MeshData(wall,"BackMesh"),trim=MeshData(wall,"TrimMesh"),
   solids=Shapes(wall,"Solids"),trimSolids=Shapes(wall,"Trim"),
   basis=new[]{A(wall.Basis.X),A(wall.Basis.Y),A(wall.Basis.Z)},inverse=new[]{A(wall.Basis.Inverse().X),A(wall.Basis.Inverse().Y),A(wall.Basis.Inverse().Z)}});
  foreach(var key in Raw.Keys.Where(k=>k.Item1==wall).ToArray())Raw.Remove(key);
  RemoveChild(wall);wall.Free();
 }
 System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_BUILDING_ORACLE"),JsonSerializer.Serialize(result));
 GD.Print("SOURCE_BUILDING_SURFACE_ROWS=",result.Count);GetTree().Quit();
}
}
namespace UnturnedGodot { public static class Log {public static void Err(string s)=>GD.PushWarning(s);} }
'''
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='building-surface-godot-') as temp:
    root = Path(temp)
    (root / 'Oracle.cs').write_text(runner)
    (root / 'WallSurface.cs').write_text(wall)
    shutil.copyfile(repo / 'game/WallMaterials.cs', root / 'WallMaterials.cs')
    (root / 'content').symlink_to(repo / 'game/content', target_is_directory=True)
    (root / 'Oracle.csproj').write_text(
        '<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
        '<EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup><ItemGroup>'
        f'<Reference Include="UnturnedSim"><HintPath>{args.sim_assembly.resolve()}</HintPath></Reference>'
        '</ItemGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Building surface source oracle"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    (root / 'input.json').write_text(json.dumps(rows))
    dotnet = shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet')
    subprocess.run([dotnet, 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, check=True)
    env = os.environ.copy()
    env['UG_BUILDING_ORACLE'] = str(output)
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)], cwd=root, env=env, check=True, timeout=40)
