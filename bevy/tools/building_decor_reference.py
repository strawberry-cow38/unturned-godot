#!/usr/bin/env python3
"""Observe original RuntimeDecorGenerators on actual Godot meshes/images.

The only instrumentation captures Builder arrays before engine normal/colour
packing. Factory/cache/layout/pixel loops remain unchanged; returned mesh AABBs
and actual RGBA8 Image bytes are also observed independently.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--godot', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
args = p.parse_args()
repo = Path(__file__).resolve().parents[2]
source = (repo / 'game/RuntimeDecorGenerators.cs').read_text()
needle = 'var mesh=new ArrayMesh();mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles,a);mesh.SurfaceSetMaterial(0,material);return mesh;'
assert source.count(needle) == 1
source = source.replace(needle, needle.replace('return mesh;', 'Observe(mesh,V.ToArray(),N.ToArray(),UV.ToArray(),Colors.ToArray());return mesh;'))
needle = '    public enum RugStyle'
source = source.replace(needle, '''    public static Action<ArrayMesh,Vector3[],Vector3[],Vector2[],Color[]> Observe;
''' + needle)
rows = []
for w,h in [(2.6,2.1),(.4,.65),(6.1,3.3),(1,1)]:
    for opened in [0,.5,1]:
        rows.append(dict(kind='curtain',width=w,height=h,open=opened,tint=None))
for tint in [[.60,.66,.54,1],[.50,.56,.63,1],[.66,.42,.32,1],[.3,.7,.2,.25]]:
    rows.append(dict(kind='curtain',width=3.2,height=1.8,open=.75,tint=tint))
for w,h in [(3.5,2.4),(.15,.4),(16,3.6),(80,130),(1,1)]:
    for style in ['Bordered','Diamond','Striped','Checker']:
        rows.append(dict(kind='rug',width=w,height=h,style=style))
runner = r'''using Godot;using UnturnedGodot;using System;using System.IO;using System.Linq;
using System.Collections.Generic;using System.Text.Json;using System.Security.Cryptography;
public partial class Oracle:Node {
static float[] V(Vector3 v)=>new[]{v.X,v.Y,v.Z};
static string Hash(float[] values){using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))foreach(var x in values)writer.Write(x);return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();}
public override void _Ready(){
var rows=JsonDocument.Parse(System.IO.File.ReadAllText("res://input.json".Replace("res://",ProjectSettings.GlobalizePath("res://"))));var output=new List<object>();
Vector3[] vertices=null,normals=null;Vector2[] uv=null;Color[] colors=null;
RuntimeDecorGenerators.Observe=(m,v,n,u,c)=>{vertices=v;normals=n;uv=u;colors=c;};
foreach(var input in rows.RootElement.EnumerateArray()){
RuntimeDecorGenerators.ClearCache();float w=input.GetProperty("width").GetSingle(),h=input.GetProperty("height").GetSingle();
bool curtain=input.GetProperty("kind").GetString()=="curtain";ArrayMesh mesh;
if(curtain){var tint=input.GetProperty("tint");Color? c=tint.ValueKind==JsonValueKind.Null?null:new Color(tint[0].GetSingle(),tint[1].GetSingle(),tint[2].GetSingle(),tint[3].GetSingle());mesh=RuntimeDecorGenerators.CreateCurtainMesh(w,h,input.GetProperty("open").GetSingle(),c);}
else mesh=RuntimeDecorGenerators.CreateRugMesh(w,h,Enum.Parse<RuntimeDecorGenerators.RugStyle>(input.GetProperty("style").GetString()));
var mat=(StandardMaterial3D)mesh.SurfaceGetMaterial(0);var img=mat.AlbedoTexture?.GetImage();var bounds=mesh.GetAabb();
var actual=mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
if(!actual.SequenceEqual(vertices))throw new Exception("engine vertex storage changed original builder");
output.Add(new{input,vertices=vertices.Length,vertexHash=Hash(vertices.SelectMany(V).ToArray()),normalHash=Hash(normals.SelectMany(V).ToArray()),
uvHash=Hash(uv.SelectMany(v=>new[]{v.X,v.Y}).ToArray()),colorHash=Hash(colors.SelectMany(c=>new[]{c.R,c.G,c.B,c.A}).ToArray()),
min=V(bounds.Position),size=V(bounds.Size),railY=curtain?RuntimeDecorGenerators.CurtainRailY(w,h):0,
clothTopY=curtain?RuntimeDecorGenerators.CurtainClothTopY(w,h):0,
texture=img==null?null:new{width=img.GetWidth(),height=img.GetHeight(),hash=Convert.ToHexString(SHA256.HashData(img.GetData())).ToLowerInvariant()},
vertexColors=mat.VertexColorUseAsAlbedo,roughness=mat.Roughness,doubleSided=mat.CullMode==BaseMaterial3D.CullModeEnum.Disabled});
}
System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_DECOR_REFERENCE"),JsonSerializer.Serialize(output));
GD.Print("SOURCE_DECOR_CASES="+output.Count);GetTree().Quit();}}
'''
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='building-decor-godot-') as temp:
    root = Path(temp)
    (root / 'RuntimeDecorGenerators.cs').write_text(source)
    (root / 'Oracle.cs').write_text(runner)
    (root / 'input.json').write_text(json.dumps(rows))
    (root / 'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Original runtime decor oracle"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    dotnet = shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet')
    subprocess.run([dotnet,'build','Oracle.csproj','--nologo','-v:q'],cwd=root,check=True)
    env = os.environ.copy()
    env['UG_DECOR_REFERENCE'] = str(output)
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH','')
    subprocess.run([str(args.godot.resolve()),'--path',str(root),'--headless'],cwd=root,env=env,check=True,timeout=60)
