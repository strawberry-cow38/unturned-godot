#!/usr/bin/env python3
"""Observe unmodified ConnectionPort and AxisBillboard on actual Godot objects."""
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
ports = [dict(kind=kind, role=role, position=position) for kind, role, position in [
    ('Output', 'None', [.4, .6, .05]),
    ('Consumer', 'None', [-.13, 0, .65]),
    ('Passthrough', 'None', [.13, 0, .65]),
    ('Consumer', 'TurnOn', [-.5, .4, -.2]),
    ('Consumer', 'TurnOff', [-.5, .4, .3]),
    ('DataOut', 'None', [.35, .25, 0]),
    ('DataIn', 'None', [-.35, .25, 0]),
    ('Output', 'None', [0, 0, -7]),
    ('Consumer', 'None', [0, -.001, 7]),
    ('Output', 'None', [-.6, .6, -1]),
]]
runner = r'''using Godot;using System;using System.Linq;using System.Collections.Generic;using System.Reflection;using System.Text.Json;
using UnturnedGodot;
public partial class Oracle:Node {
static float[] A(Vector3 p)=>new[]{p.X,p.Y,p.Z};static float[] C(Color c)=>new[]{c.R,c.G,c.B,c.A};
public override void _Ready(){
 var camera=new Camera3D {Current=true};AddChild(camera);camera.MakeCurrent();
 var rows=new List<object>();byte[] texture=null;
 foreach(var row in JsonDocument.Parse(System.IO.File.ReadAllText("input.json")).RootElement.EnumerateArray()) {
  var input=row.GetProperty("position");var position=new Vector3(input[0].GetSingle(),input[1].GetSingle(),input[2].GetSingle());
  var definition=new DeployableDef.Port {Kind=Enum.Parse<DeployableDef.PortKind>(row.GetProperty("kind").GetString()),
   Role=Enum.Parse<DeployableDef.SwitchRole>(row.GetProperty("role").GetString()),Pos=position,Watts=250};
  var owner=new Deployable {Position=new Vector3(1,5,-2),RotationDegrees=new Vector3(90,37,0)};AddChild(owner);
  var port=ConnectionPort.Create(owner,definition,"Fixture");owner.AddChild(port);
  var cube=port.GetChild<MeshInstance3D>(0);var arrow=port.GetChild<Node3D>(2);
  var axis=(AxisBillboard)arrow.GetChild(0);var quad=axis.GetChild<MeshInstance3D>(0);
  var cm=(StandardMaterial3D)cube.MaterialOverride;var am=(StandardMaterial3D)quad.MaterialOverride;
  texture ??=am.AlbedoTexture.GetImage().GetData();
  var states=new List<object>();
  foreach(bool occupied in new[]{false,true}) foreach(bool dataLive in new[]{false,true})
   foreach(var hi in Enum.GetValues<ConnectionPort.PortHi>()) {
    port.Occupied=occupied;port.DataLive=dataLive;port.SetHighlight(hi);port.SetArrowState(true,!occupied);
    states.Add(new{occupied,dataLive,highlight=hi.ToString(),cube=C(cm.AlbedoColor),emission=C(cm.Emission),enabled=cm.EmissionEnabled,
     energy=cm.EmissionEnergyMultiplier,arrow=C(am.AlbedoColor),alpha=cm.Transparency.ToString(),roughness=cm.Roughness});
   }
  var spins=new List<object>();
  foreach(var local in new[]{new Vector3(.7f,.6f,1.1f),new Vector3(0,2,0),new Vector3(-.2f,.3f,-1)}) {
   camera.GlobalPosition=axis.ToGlobal(local);
   typeof(AxisBillboard).GetMethod("Tick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(axis,new object[]{0d});
   spins.Add(new{camera=A(local),basis=new[]{A(quad.Basis.X),A(quad.Basis.Y),A(quad.Basis.Z)}});
  }
  rows.Add(new{input=new{kind=definition.Kind.ToString(),role=definition.Role.ToString(),position=A(position)},
   cubeSize=A(((BoxMesh)cube.Mesh).Size),arrowPosition=A(arrow.Position),arrowBasis=new[]{A(arrow.Basis.X),A(arrow.Basis.Y),A(arrow.Basis.Z)},
   quadSize=new[]{((QuadMesh)quad.Mesh).Size.X,((QuadMesh)quad.Mesh).Size.Y},states,spins});
  owner.Free();
 }
 System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("UG_PORT_REFERENCE"),JsonSerializer.Serialize(new{
  rows,alpha=Enumerable.Range(0,64*64).Select(i=>(int)texture[i*4+3])}));
 GD.Print("SOURCE_PORT_CASES=",rows.Count);GetTree().Quit();
}}
namespace UnturnedGodot {
public class DeployableDef {public enum PortKind{Output,Consumer,Passthrough,DataOut,DataIn}public enum SwitchRole{None,TurnOn,TurnOff}
 public struct Port {public PortKind Kind;public SwitchRole Role;public Vector3 Pos;public float Watts;}
 public bool IsDataRadio;public static bool IsDataPort(PortKind k)=>k==PortKind.DataOut||k==PortKind.DataIn;}
public partial class Deployable:Node3D,IPowerDevice {public DeployableDef Def=new();public int DataCode;
 public bool PowerProducing=>false;public bool PowerOnFire=>false;public uint PowerNetId=>0;
 public IReadOnlyList<ConnectionPort> PowerPorts=>Array.Empty<ConnectionPort>();}
public static class TickHub {public static void AddProcess(Node o,Action<double> a){}public static void RemoveProcess(Node o){} }
}
'''
with tempfile.TemporaryDirectory(prefix='port-source-godot-') as temp:
    root = Path(temp)
    for name in ['ConnectionPort.cs', 'AxisBillboard.cs']:
        shutil.copyfile(repo / 'game' / name, root / name)
    (root / 'Oracle.cs').write_text(runner)
    (root / 'input.json').write_text(json.dumps(ports))
    (root / 'Oracle.csproj').write_text('<Project Sdk="Godot.NET.Sdk/4.6.2"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>')
    (root / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Source port geometry"\nrun/main_scene="res://main.tscn"\n[dotnet]\nproject/assembly_name="Oracle"\n')
    (root / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://Oracle.cs" id="1"]\n[node name="Oracle" type="Node"]\nscript=ExtResource("1")\n')
    env = os.environ.copy()
    env['DOTNET_ROOT'] = str(Path.home() / '.dotnet')
    env['PATH'] = env['DOTNET_ROOT'] + os.pathsep + env.get('PATH', '')
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    env['UG_PORT_REFERENCE'] = str(output)
    subprocess.run([shutil.which('dotnet', path=env['PATH']), 'build', 'Oracle.csproj', '--nologo', '-v:q'], cwd=root, env=env, check=True)
    subprocess.run([str(args.godot.resolve()), '--headless', '--path', str(root)], cwd=root, env=env, check=True, timeout=60)
