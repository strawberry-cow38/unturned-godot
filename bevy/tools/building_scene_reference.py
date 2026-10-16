#!/usr/bin/env python3
"""Observe original EditorBuildings assembly on real Godot nodes/resources.

UI/undo, furniture rendering and independent opening decorations are excluded;
the generator, wall transforms, stair bodies, floor cuts, corner solving and
saved room/furniture metadata remain original source. No adapter code is used.
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
parser.add_argument('--render-dir', type=Path, help='also capture original full/cutaway surfaces with Vulkan (requires a display)')
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
source = (repo / 'game/EditorBuildings.cs').read_text()


def method(text, signature):
    start = text.index(signature)
    end = text.index('\n        }', text.index('{', start)) + len('\n        }')
    return text[start:end]


# Compile original methods against actual Godot types. Only UI/history and
# observation side effects are removed, with anchored source boundaries.
generate = method(source, 'public BuildingGenResult Generate(')
generate = ('public BuildingGenResult Generate(BuildingGenSpec spec) {\n'
            + generate[generate.index('            var res ='):generate.index('            var before =')]
            + generate[generate.index('            foreach (var pl in res.Walls)'):generate.index('            var solved =')]
            + '            SolveCorners();\n'
            + generate[generate.index('            DesignatedKind = res.Kind;'):generate.index('            ShowFurniture();')]
            + '            ActiveMaterial = mat; return res;\n}')
plan = method(source, 'WallPlan PlanOf(WallSurface w)')
plan = plan[:plan.index('            // Overwrite the four colour fields')] + '            return pl;\n}'
members = [generate, plan]
for signature in ['WallSurface SpawnWall(', 'static Aabb SurfaceAabb(',
                  'public bool PunchHole(', 'public List<(WallSurface W, float Len, Vector3 Pos)> SolveCorners()',
                  'static bool CrossOnPlan(', 'public static IEnumerable<Vector3> StairTreadOrigins(',
                  'public int FitStairsToStructure(']:
    members.append(method(source, signature))
members.append(method(source, 'public void HideAbove('))
for pattern in [r'public static readonly Vector3 StageOrigin[^;]+;', r'public const float GroundClearance[^;]+;',
                r'public static float StoreyHeight[^;]+;']:
    members.append(re.search(pattern, source).group(0))
assembly = '''using Godot; using System; using System.Collections.Generic; using System.Linq; using UnturnedSim;
namespace UnturnedGodot { public partial class SourceAssembly : Node3D {
public int ActiveMaterial; public BuildingKind? DesignatedKind;
public List<RoomDesignation> Designations=new(); public List<DoorDesignation> DoorMarks=new(); public List<PropDesignation> Furniture=new();
readonly List<WallSurface> _walls=new(); public readonly List<StaticBody3D> _stairRamps=new();
readonly List<Node3D> _furnitureNodes=new();
readonly Dictionary<Rid,WallSurface> _pickToWall=new();
void RemoveWall(WallSurface w) {_walls.Remove(w);RemoveChild(w);w.Free();}
public WallPlan[] Plans()=>_walls.Select(PlanOf).ToArray();
public string SaveText()=>WallSave.Write(Plans(),DesignatedKind,Designations,DoorMarks,Furniture);
''' + '\n'.join(members) + '\n}}'
wall = (repo / 'game/WallSurface.cs').read_text()
head = wall[:wall.index('        // ---- glazing')]
for call in ['RebuildGlass', 'RebuildDoors', 'RebuildBarricades']:
    head = head.replace('            ' + call + '();', '')
head += wall[wall.index('        void AddGableCap('):wall.index('        // ---- wall space')]
head += re.search(r'public Vector3 UVToWorld\([^;]+;', wall).group(0) + '\n'
head += method(wall, 'public bool WorldToUV(') + '\n}}'

rows = [dict(seed=seed, kind=kind, floors=floors) for seed, kind, floors in [
    (2, 'Residential', 1), (17, 'Residential', 2), (3, 'Commercial', 1), (31, 'Commercial', 2),
    (6, 'Industrial', 2), (19, 'Misc', 1), (7, 'Apartment', 2), (12, 'Apartment', 3)]]
runner = r'''using Godot; using System; using System.Linq; using System.Text.Json; using System.Collections.Generic;
using UnturnedSim; using UnturnedGodot;
public partial class Oracle : Node {
SourceAssembly _visual; int _frame; string _renderDir;
static float[] A(Vector3 p)=>new[]{p.X,p.Y,p.Z};
public override void _Ready() {
 var output=new List<object>();
 foreach(var row in JsonDocument.Parse(System.IO.File.ReadAllText("input.json")).RootElement.EnumerateArray()) {
  var kind=Enum.Parse<BuildingKind>(row.GetProperty("kind").GetString());
  int seed=row.GetProperty("seed").GetInt32(), floors=row.GetProperty("floors").GetInt32();
  var scene=new SourceAssembly(); AddChild(scene);
  var result=scene.Generate(new BuildingGenSpec {Seed=seed,Kind=kind,MinFloors=floors,MaxFloors=floors});
  output.Add(new {seed,kind=kind.ToString(),floors,palette=result.Palette,text=scene.SaveText(),plans=scene.Plans(),
   ramps=scene._stairRamps.Select(r=>new {position=A(r.Position),basis=new[]{A(r.Basis.X),A(r.Basis.Y),A(r.Basis.Z)},
    size=A(((BoxShape3D)r.GetChild<CollisionShape3D>(0).Shape).Size)}).ToArray()});
  RemoveChild(scene);scene.Free();
 }
 System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_BUILDING_ORACLE"),
  JsonSerializer.Serialize(output,new JsonSerializerOptions {IncludeFields=true}));
 GD.Print("SOURCE_BUILDING_SCENE_ROWS=",output.Count);
 _renderDir=System.Environment.GetEnvironmentVariable("UG_BUILDING_RENDER");
 if(string.IsNullOrEmpty(_renderDir)) {GetTree().Quit();return;}
 _visual=new SourceAssembly();AddChild(_visual);
 _visual.Generate(new BuildingGenSpec {Seed=17,Kind=BuildingKind.Residential,MinFloors=2,MaxFloors=2});
 var env=new Godot.Environment {BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color(.24f,.27f,.30f),
  AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=.5f,
  TonemapMode=Godot.Environment.ToneMapper.Aces};
 AddChild(new WorldEnvironment {Environment=env});
 var ground=new MeshInstance3D {Mesh=new BoxMesh {Size=new Vector3(80,.3f,80)},Position=new Vector3(0,1999.85f,0),
  MaterialOverride=new StandardMaterial3D {AlbedoColor=new Color(.43f,.46f,.47f),Roughness=1}};AddChild(ground);
 var sun=new DirectionalLight3D {LightEnergy=1,ShadowEnabled=true,Position=new Vector3(-30,2040,20)};AddChild(sun);sun.LookAt(new Vector3(0,2000,0),Vector3.Up);
 var cam=new Camera3D {Position=new Vector3(44,2034,44),Fov=60,Current=true};AddChild(cam);cam.LookAt(new Vector3(1.5f,2004,1.5f),Vector3.Up);
 GetViewport().Msaa3D=Viewport.Msaa.Msaa4X;
}
public override void _Process(double delta) {
 if(_visual==null)return;
 ++_frame;
 if(_frame==60)Save("full");
 if(_frame==80)_visual.HideAbove(3);
 if(_frame==100) {Save("cutaway");GetTree().Quit();}
}
void Save(string stage) {var image=GetViewport().GetTexture().GetImage();image.SavePng(System.IO.Path.Combine(_renderDir,"godot-building-"+stage+".png"));}
}
namespace UnturnedGodot {public static class Log {public static void Err(string s)=>GD.PushWarning(s);}}
'''
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='building-scene-godot-') as temp:
    root = Path(temp)
    (root / 'Oracle.cs').write_text(runner)
    (root / 'SourceAssembly.cs').write_text(assembly)
    (root / 'WallSurface.cs').write_text(head)
    shutil.copyfile(repo / 'game/WallMaterials.cs', root / 'WallMaterials.cs')
    (root / 'content').symlink_to(repo / 'game/content', target_is_directory=True)
    (root / 'Oracle.csproj').write_text(
        '<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework>'
        '<EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup><ItemGroup>'
        f'<Reference Include="UnturnedSim"><HintPath>{args.sim_assembly.resolve()}</HintPath></Reference>'
        '</ItemGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Building assembly source oracle"\nrun/main_scene="res://main.tscn"\n[display]\nwindow/size/viewport_width=1280\nwindow/size/viewport_height=800\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    (root / 'input.json').write_text(json.dumps(rows))
    dotnet = shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet')
    subprocess.run([dotnet, 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, check=True)
    env = os.environ.copy()
    env['UG_BUILDING_ORACLE'] = str(output)
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    command = [str(args.godot.resolve()), '--path', str(root)]
    if args.render_dir:
        render_dir = args.render_dir.resolve()
        render_dir.mkdir(parents=True, exist_ok=True)
        env['UG_BUILDING_RENDER'] = str(render_dir)
        command += ['--rendering-driver', 'vulkan', '--write-movie', str(root / 'movie.avi'), '--fixed-fps', '30']
    else:
        command += ['--headless']
    subprocess.run(command, cwd=root, env=env, check=True, timeout=120)
