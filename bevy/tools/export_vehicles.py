#!/usr/bin/env python3
"""Evaluate the original fleet's data declarations without loading Godot.

Only source declarations and pure constructors are copied into a temporary .NET
program. No vehicle dimensions, masses, meshes or flight constants are inferred.
The committed JSON is runtime data; neither Bevy nor its bridge needs Godot.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'game/Vehicle.cs'

def mask_source(source):
    pattern = r'//[^\n]*|/\*[\s\S]*?\*/|\$?@?"(?:\\.|""|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    return re.sub(pattern, lambda m: ''.join('\n' if c == '\n' else ' ' for c in m[0]), source)

def declaration(source, marker, block=False):
    start = source.index(marker)
    mask = mask_source(source)
    if block:
        body = mask.index('{', start)
        depth = 1
        for i in range(body + 1, len(mask)):
            depth += (mask[i] == '{') - (mask[i] == '}')
            if depth == 0:
                return source[start:i+1]
    else:
        stack = 0
        for i in range(start, len(mask)):
            stack += (mask[i] in '({[') - (mask[i] in ')}]')
            if mask[i] == ';' and stack == 0:
                return source[start:i+1]
    raise ValueError(marker)

def generate(source):
    snippets = []
    for marker in ['public enum WaterMode', 'public enum HeliFrame', 'public enum AccessKind', 'struct Spec', 'public sealed class TurretDef']:
        snippets.append(declaration(source, marker, True))
    markers = [m[0] for m in re.finditer(r'static readonly Spec \w+\s*=', source)]
    markers += ['static readonly TurretDef[] HindTurret', 'static readonly System.Collections.Generic.Dictionary<string, Vector3[]> SeatTable',
                'static readonly Vector3 TankGunnerSeatDown', 'public static readonly Vector3 TankPeriscopeEye', 'static readonly Vector3 GenericSeatRise', 'public readonly record struct AccessZone', 'const float DoorZoneWidth', 'const float MinCompartmentRun', 'const float CompartmentHeight', 'public static readonly string[] SpecNames']
    markers += ['static readonly (Vector3 min, Vector3 max) '+name for name in ['TankMgBox','TankMgBarrel','TankTopHatch','TankDriverHatch']]
    markers += ['static readonly Vector3 '+name for name in ['TankTopHatchHinge','TankDriverHatchHinge','TankGlacisDown','TankTurretColSize','TankBarrelColSize','TankSightEye','TankCupolaEye']]
    markers += ['const float '+name for name in ['TankTopHatchClosedDeg','TankDriverHatchLift','TankDriverHatchSlide','TankDriverHatchClosedDeg','TankHatchSwingSeconds','RearArcDepressionScale','TankGunnerRise']]
    # Original declaration order is required for shared turret arrays.
    for marker in sorted(markers, key=source.index):
        snippets.append(declaration(source, marker))
    for marker in ['static (string, Color)[] HeliParts', 'static (Vector3, Vector3)[] Skids',
                   'static Spec WithTurrets', 'static Spec WithBeaconLens', 'static Spec WithSling', 'static TurretDef[] DoorGuns(', 'static Color SpawnPaint', 'static AccessZone[] BuildAccessZones']:
        snippets.append(declaration(source, marker, True))
    for marker in ['static TurretDef[] TankCannon', 'static TurretDef[] HueyDoorGuns()', 'static TurretDef[] OrcaDoorGuns()', 'static bool HandTunedSeatOf', 'static Spec HeliBase', 'static Vector3 SeatOf', 'static (Vector3 size, Vector3 center)? RoofBox']:
        snippets.append(declaration(source, marker))
    builds = dict(re.findall(r'public static Vehicle (Build\w+)\(int variant = 0\)\s*=>\s*Build\((\w+),', source))
    dispatch = declaration(source, 'public static Vehicle BuildByName')
    mappings = dict(re.findall(r'"([a-z_]+)"\s*=>\s*(Build\w+)\(variant\)', dispatch))
    mappings['jeep'] = 'BuildJeep'
    resolves = ',\n'.join(f'"{key}" => {builds[build]}' for key,build in mappings.items())
    header = '''using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
record struct Vector3(float X, float Y, float Z) {
    public static Vector3 Zero => new(0,0,0);
    public static Vector3 One => new(1,1,1);
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3 operator *(Vector3 a, float b) => new(a.X*b,a.Y*b,a.Z*b);
}
record struct Color(float R, float G, float B, float A=1) {
    public Color(string hex) : this(1,1,1,1) {
        uint rgb=System.Convert.ToUInt32(hex.TrimStart('#'),16);
        R=((rgb>>16)&255)/255f; G=((rgb>>8)&255)/255f; B=(rgb&255)/255f;
    }
    public static Color FromHsv(float h,float s,float v) {
        float hue=h*6; int i=(int)MathF.Floor(hue); float f=hue-i;
        float p=v*(1-s), q=v*(1-s*f), t=v*(1-s*(1-f));
        return (i%6) switch { 0=>new(v,t,p),1=>new(q,v,p),2=>new(p,v,t),3=>new(p,q,v),4=>new(t,p,v),_=>new(v,p,q) };
    }
}
static class Colors { public static Color White => new(1,1,1,1); }
static class Mathf {
    public static float Abs(float f)=>MathF.Abs(f);
    public static float Min(float a,float b)=>MathF.Min(a,b);
    public static float Max(float a,float b)=>MathF.Max(a,b);
    public static int Sign(float f)=>MathF.Sign(f);
}
enum EItemRarity { COMMON, UNCOMMON, RARE, EPIC, LEGENDARY, MYTHICAL }
static class Fleet {
'''
    footer = '''
static Spec Resolve(string key) => key switch { RESOLVES, _ => throw new ArgumentException(key) };
static object ConvertValue(object v) {
    if (v == null) return null;
    if (v is Vector3 p) return new[] {p.X,p.Y,p.Z};
    if (v is Color c) return new[] {c.R,c.G,c.B,c.A};
    if (v is Enum) return v.ToString();
    if (v is string || v.GetType().IsPrimitive) return v;
    if (v is ITuple tuple) return Enumerable.Range(0,tuple.Length).Select(i => ConvertValue(tuple[i])).ToArray();
    if (v is IEnumerable values) return values.Cast<object>().Select(ConvertValue).ToArray();
    var fields=v.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public);
    if(fields.Length>0) return fields.ToDictionary(f=>f.Name, f=>ConvertValue(f.GetValue(v)));
    return v.GetType().GetProperties(BindingFlags.Instance|BindingFlags.Public).ToDictionary(p=>p.Name,p=>ConvertValue(p.GetValue(v)));
}
public static void Main() {
    var vehicles = SpecNames.Select((key,index) => {
        Spec spec = Resolve(key);
        var seats=spec.Seats ?? (SeatTable.TryGetValue(key,out var st) ? st : new[] {SeatOf(spec.Name)});
        var offset=HandTunedSeatOf(spec.Name) ? SeatOf(spec.Name) : seats[0] + (spec.SeatBodyRise>0 ? new Vector3(0,spec.SeatBodyRise,0) : GenericSeatRise);
        return new { typeId=index, key, spec=ConvertValue(spec),
            seats=ConvertValue(seats), bodySeats=ConvertValue(seats.Select(s=>s+offset-seats[0]).ToArray()),
            access=ConvertValue(BuildAccessZones(spec,seats,spec.BoxCenter,spec.BoxSize)),
            paints=Enumerable.Range(0,256).Select(v=>ConvertValue(SpawnPaint(spec,v))).ToArray(),
            roof=ConvertValue(RoofBox(spec.Name)) };
    }).ToArray();
    var tank = new {
        mgBox=ConvertValue(TankMgBox), mgBarrel=ConvertValue(TankMgBarrel), topHatch=ConvertValue(TankTopHatch), driverHatch=ConvertValue(TankDriverHatch),
        topHinge=ConvertValue(TankTopHatchHinge), driverHinge=ConvertValue(TankDriverHatchHinge), glacisDown=ConvertValue(TankGlacisDown),
        topClosed=TankTopHatchClosedDeg, driverClosed=TankDriverHatchClosedDeg, driverLift=TankDriverHatchLift, driverSlide=TankDriverHatchSlide,
        swingSeconds=TankHatchSwingSeconds, rearDepressionScale=RearArcDepressionScale,
        turretSize=ConvertValue(TankTurretColSize), turretCenter=ConvertValue(TankTurretColCenter), barrelSize=ConvertValue(TankBarrelColSize), barrelCenter=ConvertValue(TankBarrelColCenter),
        periscopeEye=ConvertValue(TankPeriscopeEye), sightEye=ConvertValue(TankSightEye), cupolaEye=ConvertValue(TankCupolaEye), gunnerSeat=ConvertValue(TankGunnerSeatDown), gunnerRise=TankGunnerRise,
    };
    Console.WriteLine(JsonSerializer.Serialize(new {vehicles,tank}));
}
}
'''.replace('RESOLVES',resolves)
    return header + '\n'.join(snippets) + footer

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT/'bevy/data/vehicles.json')
    args=parser.parse_args()
    source=SOURCE.read_text()
    with tempfile.TemporaryDirectory(prefix='unturned-vehicle-data-') as d:
        work=Path(d)
        (work/'Export.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
        (work/'Program.cs').write_text(generate(source))
        dotnet=os.environ.get('DOTNET') or shutil.which('dotnet') or str(Path.home()/'.dotnet/dotnet')
        result=subprocess.run([dotnet,'run','--project',str(work/'Export.csproj'),'-v','q'],capture_output=True,text=True)
        if result.returncode:
            print(result.stdout, result.stderr)
            Path('/tmp/vehicle-export-failed.cs').write_text((work/'Program.cs').read_text())
            raise SystemExit(result.returncode)
        exported=json.loads(result.stdout.strip().splitlines()[-1])
        data=exported['vehicles']
    assert len(data)==len({v['key'] for v in data}), 'Duplicate vehicle keys'
    # Plane keys historically fell through SpecFor to jeep. BuildByName is the
    # actual spawn path and the source of truth for this exporter.
    assert next(v for v in data if v['key']=='otter')['spec']['Plane']
    assert next(v for v in data if v['key']=='fighterjet')['spec']['Plane']
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps({'source':'game/Vehicle.cs','sourceSha256':hashlib.sha256(source.encode()).hexdigest(),**exported},indent=2)+'\n')
    print(f'Exported {len(data)} original vehicle type IDs to {args.output}')

if __name__=='__main__': main()
