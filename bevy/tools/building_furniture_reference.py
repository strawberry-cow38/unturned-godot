#!/usr/bin/env python3
"""Observe original ShowFurniture placement, slots and room binding on Godot nodes.

Original OBJ parser, generated decor, DoodadKitView and RoomLightSwitch execute
unchanged. Lamp/grid sinks retain their placement/ports/manual-light state without
running the independent global-power simulation. Stock item geometry and editor
material loading are not part of this value oracle.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--godot', type=Path, required=True)
p.add_argument('--sim-assembly', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
args = p.parse_args()
repo = Path(__file__).resolve().parents[2]
def method(text, signature):
    a = text.index(signature)
    b = text.index('\n        }', text.index('{', a)) + len('\n        }')
    return text[a:b]
editor = (repo / 'game/EditorBuildings.cs').read_text()
show = method(editor,'void ShowFurniture()').replace('var objects = _editor?.Objects;', 'var objects = Objects;')
signatures = ['RoomLightSwitch BuildSwitch(', 'GridPowerSource BuildOutlet(',
              'public static MeshInstance3D RuntimeDecorNode(', 'public static Transform3D RuntimeDecorTransform(']
parts = [show] + [method(editor,s) for s in signatures]
parts += [re.search(r'public static Color\? CurtainTint\(string name\) => name switch\s*\{[^}]+\};',editor,re.S)[0],
          re.search(r'static readonly Vector3 OutletPort =[^;]+;',editor,re.S)[0]]
wrapper = '''using Godot;using UnturnedSim;using System.Collections.Generic;
namespace UnturnedGodot {public partial class SourceFurniture:Node3D {
public EditorObjects Objects=new();public readonly List<PropDesignation> Furniture=new();public readonly List<RoomDesignation> Designations=new();
readonly List<Node3D> _furnitureNodes=new();readonly List<RoomLightSwitch> _switches=new();readonly List<LampLight> _ceilingLights=new();readonly List<GridPowerSource> _outlets=new();
public IReadOnlyList<Node3D> Nodes=>_furnitureNodes;public IReadOnlyList<LampLight> Lights=>_ceilingLights;public void Build()=>ShowFurniture();
'''+'\n'.join(parts)+'\n}}'
obj = (repo / 'game/ObjMesh.cs').read_text()
objcode = '''using Godot;using System.Collections.Generic;using System.Globalization;
namespace UnturnedGodot {public static class ObjMesh {
static readonly Dictionary<string,ArrayMesh> _cache=new();static readonly Dictionary<ArrayMesh,Vector3[]> _faces=new();
const int CONV=1;static float F(string s)=>float.Parse(s,CultureInfo.InvariantCulture);
static ArrayMesh TryLoadBin(string path,out Vector3[] faces){faces=null;return null;}static void TrySaveBin(string p,Vector3[] v,Vector3[] n,Vector2[] u,Color[] c){}
''' + method(obj,'public static ArrayMesh Load(') + '\n}}'
upright = re.search(r'public static Basis Upright\(float yawDeg\) =>[^;]+;',(repo/'game/EditorObjects.cs').read_text())[0]
jitter = method((repo/'game/GridLight.cs').read_text(),'protected void InitJitter(Vector3 pos)')
sinks = '''using Godot;using System.Collections.Generic;
namespace UnturnedGodot {
public static class Log {public static void Print(string x){}public static void Err(string x)=>GD.PrintErr(x);}
public static class OutlineOverlay {public const uint OutlineLayer=1u<<19;}
public static class ContentProvider {public static ArrayMesh ParseObj(string path){path=ProjectSettings.GlobalizePath(path);return System.IO.File.Exists(path)?ObjMesh.Load(path):null;}}
public class EditorObjects {
public HashSet<string> Missing=new();
public ArrayMesh PreviewMesh(string name)=>name==null||Missing.Contains(name)?null:ContentProvider.ParseObj("res://content/objects/"+name+".obj");
public StandardMaterial3D PreviewMaterial(string name)=>null;
''' + upright + '''}
public static class WorldItem {public static MeshInstance3D BuildReplicaVisual(ushort id,Color color)=>new();}
public partial class LampLight:Node3D {
public enum Kind {Generic}public bool Switched=true;public float _worn=1,_reactionFrac;
public static LampLight Make(Vector3 at,MeshInstance3D fixture,Kind kind){var l=new LampLight{Position=at,TopLevel=true};l.InitJitter(at);return l;}
public void SetSwitched(bool on)=>Switched=on;
''' + jitter + '''}
public partial class GridPowerSource:Node3D {
public float Watts;public List<Vector3> Ports=new();
public static GridPowerSource Attach(Node parent,Vector3 at,Basis basis,Vector3 port,float watts,string label,Mesh mesh){var g=new GridPowerSource{Transform=new(basis,at),Watts=watts};parent.AddChild(g);g.Ports.Add(port);return g;}
public void AddOutputPort(Vector3 p,string label)=>Ports.Add(p);
}}
'''
props = [
 dict(name='Light_Switch_0',at=[1,2001.5,3],yaw=37),
 dict(name='Ceiling_Lamp_missing_oracle',at=[3,2003,3],yaw=0),
 dict(name='Power_Outlet_0',at=[2,2000.5,4],yaw=-91),
 dict(name='Light_Switch_0',at=[5,2001.5,3],yaw=180),
 dict(name='Ceiling_Lamp_Pendant_0',at=[4,2003.4,5],yaw=15),
 dict(name='Knife_Block_0',at=[5,2001,6],yaw=62),
 dict(name='Mug_Pair_0',at=[7,2001,8],yaw=-27),
 dict(name='oracle_curtain',at=[10,2003.2,4],yaw=53,gen=dict(kind='Curtain',width=2.6,height=1.8,open=True,style='Sage')),
 dict(name='oracle_rug',at=[6,2000.25,5],yaw=-63,gen=dict(kind='Rug',width=3.5,height=2.4,open=False,style='Diamond')),
 dict(name='missing_oracle',at=[0,2000,0],yaw=0),
 dict(name='unknown_rug_style',at=[0,2000,0],yaw=0,gen=dict(kind='Rug',width=2,height=2,open=False,style='Future')),
 dict(name='Ceiling_Lamp_Flush_0',at=[8,2003.4,9],yaw=44),
]
rows = [dict(props=props,down=[True,False],missing=[]),dict(props=props,down=[True,True],missing=[]),
        dict(props=props,down=[False,True],missing=['Power_Outlet_0','Ceiling_Lamp_Pendant_0'])]
runner = r'''using Godot;using UnturnedGodot;using UnturnedSim;using System;using System.Linq;using System.Collections.Generic;using System.Text.Json;
public partial class Oracle:Node {
static float[] V(Vector3 v)=>new[]{v.X,v.Y,v.Z};static object T(Transform3D t)=>new{position=V(t.Origin),basis=new[]{V(t.Basis.X),V(t.Basis.Y),V(t.Basis.Z)}};
public override void _Ready(){var output=new List<object>();using var input=JsonDocument.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://input.json")));
foreach(var row in input.RootElement.EnumerateArray()){
var stage=new SourceFurniture();AddChild(stage);foreach(var name in row.GetProperty("missing").EnumerateArray())stage.Objects.Missing.Add(name.GetString());
foreach(var p in row.GetProperty("props").EnumerateArray()){var a=p.GetProperty("at");RuntimeDecor gen=null;if(p.TryGetProperty("gen",out var g))gen=new(Enum.Parse<RuntimeDecorKind>(g.GetProperty("kind").GetString()),1,g.GetProperty("width").GetSingle(),g.GetProperty("height").GetSingle(),g.GetProperty("open").GetBoolean(),g.GetProperty("style").GetString());stage.Furniture.Add(new(p.GetProperty("name").GetString(),a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle(),p.GetProperty("yaw").GetSingle(),gen));}
var e=new RoomElectrical();var down=row.GetProperty("down");e.Add(new(ElectricalKind.Switch,0,0,0,0,0),down[0].GetBoolean());e.Add(new(ElectricalKind.Switch,2,0,0,0,0),down[1].GetBoolean());e.Add(new(ElectricalKind.Outlet,1,0,0,0,0,1500));e.Lights.AddRange(new[]{0,1});stage.Designations.Add(new(RoomKind.Kitchen,0,0,0,null,e));
stage.Build();var nodes=new List<object>();foreach(var n in stage.Nodes){
var stock=n.GetChildren().OfType<MeshInstance3D>().Where(x=>x.Name.ToString().StartsWith("Stock_")).Select(x=>new{name=x.Name.ToString(),pose=T(x.Transform)}).ToArray();
object sw=null,light=null,outlet=null;if(n is RoomLightSwitch s)sw=new{down=s.Down,on=s.On,angle=s.RockerAngle,lamps=s.Lamps.Count,pivot=T(s.GetNode<Node3D>("RockerPivot").Transform)};
if(n is LampLight l)light=new{switched=l.Switched,wear=l._worn,reaction=l._reactionFrac};
if(n is GridPowerSource o){var box=o.GetNode<StaticBody3D>("OutletLook");var shape=box.GetChild<CollisionShape3D>(0);outlet=new{watts=o.Watts,ports=o.Ports.Select(V).ToArray(),lookPosition=V(box.Position),lookSize=V(((BoxShape3D)shape.Shape).Size),layer=box.CollisionLayer,mask=box.CollisionMask};}
nodes.Add(new{name=n.Name.ToString(),type=n.GetType().Name,pose=T(n.GlobalTransform),stock,sw,light,outlet});}
output.Add(new{input=row,nodes,lightSlots=stage.Lights.Select(l=>l==null?-1:stage.Nodes.ToList().IndexOf(l)).ToArray()});stage.QueueFree();}
System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_FURNITURE_REFERENCE"),JsonSerializer.Serialize(output));GD.Print("SOURCE_FURNITURE_CASES="+output.Count);GetTree().Quit();}}
'''
output=args.output.resolve();output.parent.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='building-furniture-godot-') as temp:
    root=Path(temp)
    for name,code in [('SourceFurniture',wrapper),('ObjMesh',objcode),('Sinks',sinks),('Oracle',runner)]:
        (root/(name+'.cs')).write_text(code)
    for name in ['RoomLightSwitch','RuntimeDecorGenerators','DoodadKitView']:
        shutil.copyfile(repo/'game'/f'{name}.cs',root/f'{name}.cs')
    (root/'content').symlink_to(repo/'game/content',target_is_directory=True)
    (root/'input.json').write_text(json.dumps(rows))
    (root/'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup><ItemGroup>'
                                    f'<Reference Include="UnturnedSim"><HintPath>{args.sim_assembly.resolve()}</HintPath></Reference></ItemGroup></Project>')
    (root/'project.godot').write_text('config_version=5\n[application]\nconfig/name="Original furniture oracle"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root/'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    dotnet=shutil.which('dotnet') or str(Path.home()/'.dotnet/dotnet')
    subprocess.run([dotnet,'build','Oracle.csproj','--nologo','-v:q'],cwd=root,check=True)
    env=os.environ.copy();env['UG_FURNITURE_REFERENCE']=str(output);env['DOTNET_ROOT']=str(Path.home()/'.dotnet');env['PATH']=env['DOTNET_ROOT']+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.godot.resolve()),'--path',str(root),'--headless'],cwd=root,env=env,check=True,timeout=60)
