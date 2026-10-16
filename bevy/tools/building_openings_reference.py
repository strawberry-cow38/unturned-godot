#!/usr/bin/env python3
"""Measure original DoorDeploy/ObjectDoor/WallSurface placement on Godot nodes.

Only independent audio, outline and occupied-volume queries are omitted. Source
OBJ parsing, fitted mesh/pivot, hierarchy, wall alignment, pocket/swing curves
and collider transforms execute their original methods with actual engine types.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', type=Path, required=True)
parser.add_argument('--sim-assembly', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--render-dir', type=Path, help='capture fitted opening rig with actual source materials')
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]


def method(text, signature):
    start = text.index(signature)
    end = text.index('\n        }', text.index('{', start)) + len('\n        }')
    return text[start:end]


door = (repo / 'game/ObjectDoor.cs').read_text()
ready = method(door, 'public override void _Ready()')
ready = ready[:ready.index('            if (!string.IsNullOrEmpty(_soundName))')] + '\n ApplySwing(0); }'
start = ready.index('                _leafOutline =')
end = ready.index('                // Box collider', start)
ready = ready[:start] + ready[end:]
spawn = method(door, 'public static ObjectDoor Spawn(')
source_door = '''using Godot; using System.Collections.Generic;
namespace UnturnedGodot { public partial class ObjectDoor : StaticBody3D {
Vector3 _pivotLocal,_axis=Vector3.Back,_slide,_leafColliderCenter;
float _angleDeg,_duration,_cull,_minThickness,_swing,_swingSign=1,_wantSign=1;
Mesh _leafMesh;Material _leafMaterial;bool _initialOpen,_doubleActing,_pendingSolid;
string _soundName;List<Vector2> _openCurve,_closeCurve;Node3D _pivot;
StandardMaterial3D _leafMatInstance;CollisionShape3D _leafCollider;
public const string Group="object_doors";public bool IsOpen {get;set;}
public Vector3 Pivot=>_pivotLocal;public Vector3 Axis=>_axis;public Vector3 Slide=>_slide;
public float Angle=>_angleDeg;public float Duration=>_duration;
public bool DoubleActing=>_doubleActing;
public Mesh Leaf=>_leafMesh;public CollisionShape3D Collider=>_leafCollider;
public void SetGroup(List<ObjectDoor> group) {}
public void MakeDoubleActing()=>_doubleActing=true;
void TrySolidify(){_leafCollider.Disabled=false;}
public void Observe(float t,bool reverse){IsOpen=true;_wantSign=_swingSign=reverse?-1:1;ApplySwing(t);}
''' + '\n'.join([spawn, ready, method(door, 'public void MakePocket('),
                  method(door, 'void ApplySwing('), method(door, 'float SampleEasing(')]) + '\n}}'

obj = (repo / 'game/ObjMesh.cs').read_text()
source_mesh = '''using Godot;using System.Collections.Generic;using System.Globalization;
namespace UnturnedGodot {public static class ObjMesh {
static readonly Dictionary<string,ArrayMesh> _cache=new();static readonly Dictionary<ArrayMesh,Vector3[]> _faces=new();
const int CONV=1;static float F(string s)=>float.Parse(s,CultureInfo.InvariantCulture);
static ArrayMesh TryLoadBin(string path,out Vector3[] faces){faces=null;return null;}
static void TrySaveBin(string path,Vector3[] v,Vector3[] n,Vector2[] uv,Color[] c){}
''' + method(obj, 'public static ArrayMesh Load(') + '\n}}'
builder = (repo / 'game/WorldBuilder.cs').read_text()
catalog = builder[builder.index('        public struct DoorCatalogEntry'):builder.index('        /// <summary>Where you can sit on a prop')]
catalog = 'using Godot;namespace UnturnedGodot {public static class WorldBuilder {\n' + catalog + '\n}}'
wall = (repo / 'game/WallSurface.cs').read_text()
source_wall = '''using Godot;using UnturnedSim;using System.Collections.Generic;
namespace UnturnedGodot {public partial class SourceOpeningWall:Node3D {
public const float TrimProfile=WallOpenings.TrimProfile;
public Node3D Hang(WallOpening o,float thickness) {
var made=DoorDeploy.SpawnProp(o.DoorProp,this,Vector3.Zero,0f,ClearOpeningSize(o),reverse:o.DoorHingeAtStart!=o.DoorOpensFront);
if(made==null)return null;
foreach(var c in made.GetChildren()) if(c is ObjectDoor od) {
if(o.DoorAction==DoorAction.DoubleSwing)od.MakeDoubleActing();
else if(o.DoorAction==DoorAction.Pocket)od.MakePocket(thickness+.02f);
}
PlaceDoor(made,o);return made;
}
''' + '\n'.join([method(wall, 'void PlaceDoor('), method(wall, 'public static Vector2 ClearOpeningSize(')]) + '\n}}'

glass = (repo / 'game/GlassPane.cs').read_text()
source_glass = '''using Godot;using System.Collections.Generic;
namespace UnturnedGodot {public partial class GlassPane:StaticBody3D {
public static readonly Color DefaultHue=new(.62f,.73f,.78f);public float Health,HealthMax;
public bool Indestructible;Vector3 _half;Color _hue;MeshInstance3D _mesh;static Shader _rainGlass;
public MeshInstance3D Art=>_mesh;
public bool Covered {set{_mesh.SetInstanceShaderParameter("covered",value?1f:0f);}}
static Material GlassMat(Color hue)=>RainGlassMat(new Color(hue.R,hue.G,hue.B,.26f),0f,.06f);
''' + '\n'.join([method(glass,'public static GlassPane Build('),method(glass,'public static Vector3 DominantFaceAxis('),
                       method(glass,'public static ShaderMaterial RainGlassMat(')]) + '\n}}'

rows = []
for action in [0, 1, 2]:
    for hinge in [False, True]:
        for front in [False, True]:
            rows.append(dict(prop='Door_Pine', action=action, hinge=hinge, front=front,
                             position=[7, 2000.25, -13], euler=[0, 37, 0], u=1.2, v=0, width=2.5, height=2.85, thickness=.7))
for prop in ['Door_Birch', 'Door_Maple', 'Gate_Pine', 'Hatch_Pine', 'Doubledoor_Pine', 'Fridge_0', 'Wardrobe_0']:
    rows.append(dict(prop=prop, action=0, hinge=True, front=False,
                     position=[-3, 2000.25, 5], euler=[18, -73, 0], u=.4, v=1.1, width=3.1, height=2.3, thickness=.5))
runner = r'''using Godot;using System;using System.Linq;using System.Collections.Generic;using System.Text.Json;
using UnturnedSim;using UnturnedGodot;
public partial class Oracle:Node {
List<ObjectDoor> _visual=new();int _frame;string _render;GlassPane _pane;

static float[] V(Vector3 p)=>new[]{p.X,p.Y,p.Z};
static float[][] B(Basis b)=>new[]{V(b.X),V(b.Y),V(b.Z)};
static object T(Transform3D t)=>new{position=V(t.Origin),basis=B(t.Basis)};
static Vector3 Read(JsonElement row,string key){var a=row.GetProperty(key).EnumerateArray().Select(x=>x.GetSingle()).ToArray();return new(a[0],a[1],a[2]);}
public override void _Ready(){
var output=new List<object>();
foreach(var row in JsonDocument.Parse(System.IO.File.ReadAllText("input.json")).RootElement.EnumerateArray()) {
 var wall=new SourceOpeningWall {Position=Read(row,"position"),RotationDegrees=Read(row,"euler")};AddChild(wall);
 var opening=new WallOpening {U=row.GetProperty("u").GetSingle(),V=row.GetProperty("v").GetSingle(),
 Width=row.GetProperty("width").GetSingle(),Height=row.GetProperty("height").GetSingle(),Depth=999,
 DoorProp=row.GetProperty("prop").GetString(),DoorHingeAtStart=row.GetProperty("hinge").GetBoolean(),
 DoorOpensFront=row.GetProperty("front").GetBoolean(),DoorAction=(DoorAction)row.GetProperty("action").GetInt32()};
 var host=wall.Hang(opening,row.GetProperty("thickness").GetSingle());
 var leaves=new List<object>();
 if(host!=null)foreach(var child in host.GetChildren())if(child is ObjectDoor d) {
  var poses=new List<object>();
  foreach(var state in new[]{(0f,false),(.5f,false),(1f,false),(1f,true)}) {
   d.Observe(state.Item1,state.Item2);
   var mesh=d.GetChild<Node3D>(0).GetChildren().OfType<MeshInstance3D>().Single();
   poses.Add(new{t=state.Item1,reverse=state.Item2,mesh=T(mesh.GlobalTransform),collider=T(d.Collider.GlobalTransform),disabled=d.Collider.Disabled});
  }
  var a=d.Leaf.GetAabb();
  leaves.Add(new{pivot=V(d.Pivot),axis=V(d.Axis),angle=d.Angle,duration=d.Duration,slide=V(d.Slide),doubleActing=d.DoubleActing,
   min=V(a.Position),max=V(a.End),colliderSize=V(((BoxShape3D)d.Collider.Shape).Size),body=T(d.Transform),poses});
 }
 output.Add(new{input=row,host=host==null?null:T(host.Transform),leaves});
 RemoveChild(wall);wall.Free();
}
System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_BUILDING_OPENINGS"),JsonSerializer.Serialize(output));
var panes=new List<object>();
foreach(var size in new[]{new Vector2(2.4f,2.6f),new Vector2(.02f,2.6f),new Vector2(2.4f,.02f),new Vector2(.04f,1f),new Vector2(1f,.04f),new Vector2(.04f,.04f),new Vector2(.03999f,1f),new Vector2(1f,.03999f),new Vector2(.02f,.02f),new Vector2(.00001f,.00001f),new Vector2(.0001f,.0001f)}) {
 var pane=GlassPane.Build(size);AddChild(pane);var mat=(ShaderMaterial)pane.Art.MaterialOverride;
 panes.Add(new{size=new[]{size.X,size.Y},axis=V(GlassPane.DominantFaceAxis(pane.Art.Mesh)),
  box=V(((BoxShape3D)pane.GetChild<CollisionShape3D>(1).Shape).Size),layer=pane.CollisionLayer,mask=pane.CollisionMask,
  color=new[]{GlassPane.DefaultHue.R,GlassPane.DefaultHue.G,GlassPane.DefaultHue.B,mat.GetShaderParameter("tint").AsColor().A},health=pane.Health});
 RemoveChild(pane);pane.Free();
}
System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_BUILDING_OPENINGS")+".glass.json",JsonSerializer.Serialize(panes));
GD.Print("SOURCE_BUILDING_OPENINGS_ROWS=",output.Count);
_render=System.Environment.GetEnvironmentVariable("UG_BUILDING_RENDER");
if(string.IsNullOrEmpty(_render)){GetTree().Quit();return;}
for(int i=0;i<3;i++) {
 var wall=new SourceOpeningWall {Position=new Vector3(-6+i*4,0,0)};AddChild(wall);
 var o=new WallOpening {U=.5f,V=0,Width=2.5f,Height=2.85f,Depth=999,DoorProp="Door_Pine",DoorAction=(DoorAction)i,DoorHingeAtStart=true,DoorOpensFront=true};
 var host=wall.Hang(o,.7f);_visual.AddRange(host.GetChildren().OfType<ObjectDoor>());
}
_pane=GlassPane.Build(new Vector2(2.4f,2.6f));AddChild(_pane);_pane.Position=new Vector3(8,1.8f,0);
var env=new Godot.Environment {BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color(.24f,.27f,.30f),
AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=.5f,TonemapMode=Godot.Environment.ToneMapper.Aces};
AddChild(new WorldEnvironment {Environment=env});
Box(new Vector3(2,-.15f,0),new Vector3(24,.3f,12),new Color(.43f,.46f,.47f));
for(int y=0;y<8;y++)for(int x=0;x<8;x++)Box(new Vector3(6.8f+x*.34f,.6f+y*.34f,-.5f),new Vector3(.34f,.34f,.1f),(x+y)%2==0?new Color(.8f,.72f,.2f):new Color(.15f,.3f,.6f));
var sun=new DirectionalLight3D {LightEnergy=1,ShadowEnabled=true,Position=new Vector3(-10,20,10)};AddChild(sun);sun.LookAt(Vector3.Zero,Vector3.Up);
var cam=new Camera3D {Position=new Vector3(12,6,16),Fov=50,Current=true};AddChild(cam);cam.LookAt(new Vector3(2,1.5f,0),Vector3.Up);GetViewport().Msaa3D=Viewport.Msaa.Msaa4X;
}
void Box(Vector3 p,Vector3 size,Color color){AddChild(new MeshInstance3D{Position=p,Mesh=new BoxMesh{Size=size},MaterialOverride=new StandardMaterial3D{AlbedoColor=color,Roughness=1}});}
public override void _Process(double delta){
if(_visual.Count==0)return;++_frame;
if(_frame==60)Save("closed");
if(_frame==70)foreach(var d in _visual)d.Observe(.5f,false);
if(_frame==85)Save("half");
if(_frame==90)foreach(var d in _visual)d.Observe(1f,false);
if(_frame==110)Save("open");
if(_frame==115){RenderingServer.GlobalShaderParameterSet("rain_wetness",1f);RenderingServer.GlobalShaderParameterSet("rain_intensity",1f);}
if(_frame==140){Save("wet");GetTree().Quit();}
}
void Save(string stage){GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_render,"godot-building-openings-"+stage+".png"));}
}
namespace UnturnedGodot {
public static class PlayerController {public const string SurfMeta="source_surface";public enum Surf{Concrete}}
public static class RainSystem3D {static bool _ready;public static void EnsureGlobals(){if(_ready)return;_ready=true;RenderingServer.GlobalShaderParameterAdd("rain_wetness",RenderingServer.GlobalShaderParameterType.Float,0f);RenderingServer.GlobalShaderParameterAdd("rain_intensity",RenderingServer.GlobalShaderParameterType.Float,0f);}}
public sealed class DeployableDef {public string DoorProp;}
public static class ContentProvider {public static bool LoadOk(Image i,string path)=>i.Load(path)==Error.Ok;}
public static class Log {public static void Err(string s)=>GD.PushWarning(s);}
}
'''
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='building-openings-godot-') as temp:
    root = Path(temp)
    for name, code in [('Oracle', runner), ('ObjectDoor', source_door), ('ObjMesh', source_mesh),
                       ('WorldBuilder', catalog), ('SourceOpeningWall', source_wall), ('GlassPane',source_glass)]:
        (root / (name + '.cs')).write_text(code)
    shutil.copyfile(repo / 'game/DoorDeploy.cs', root / 'DoorDeploy.cs')
    (root / 'content').symlink_to(repo / 'game/content', target_is_directory=True)
    (root / 'input.json').write_text(json.dumps(rows))
    (root / 'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup><ItemGroup>'
                                      f'<Reference Include="UnturnedSim"><HintPath>{args.sim_assembly.resolve()}</HintPath></Reference></ItemGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Building opening source oracle"\nrun/main_scene="res://main.tscn"\n[display]\nwindow/size/viewport_width=1280\nwindow/size/viewport_height=800\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    dotnet = shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet')
    subprocess.run([dotnet, 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, check=True)
    env = os.environ.copy()
    env['UG_BUILDING_OPENINGS'] = str(output)
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    command=[str(args.godot.resolve()),'--path',str(root)]
    if args.render_dir:
        args.render_dir.resolve().mkdir(parents=True,exist_ok=True)
        env['UG_BUILDING_RENDER']=str(args.render_dir.resolve())
        command+=['--rendering-driver','vulkan','--write-movie',str(root/'movie.avi'),'--fixed-fps','30']
    else: command+=['--headless']
    subprocess.run(command,cwd=root,env=env,check=True,timeout=120)
