#!/usr/bin/env python3
"""Extract unchanged data declarations; reject source drift instead of guessing.

The source's DeployableDef mixes a data table and Godot mesh/material methods.
Keep the table verbatim, excluding only those rendering methods. Related enum and
constant declarations are likewise copied from their original owners at build time.
"""
from pathlib import Path
import re
import sys

repo = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

def read(name):
    return (repo / name).read_text()

def match(pattern, source):
    m = re.search(pattern, source, re.S)
    if not m:
        raise RuntimeError(f'Source declaration moved: {pattern}')
    return m.group(0)

source = read('game/DeployableDef.cs')
marker = '        // The mesh + a nearest-filtered palette material'
assert source.count(marker) == 1
source = source.split(marker)[0] + '        public Vector3 MeshEuler;\n    }\n}\n'
(out / 'DeployableData.g.cs').write_text('// Generated from game/DeployableDef.cs; do not edit.\n' + source)
parts = ['using Godot; namespace UnturnedGodot {']
for filename, name in [('game/FluidTank.cs', 'FluidType'), ('game/FluidTank.cs', 'WaterQuality'),
                       ('game/FluidContainer.cs', 'FluidRole'), ('game/BarricadePlacer.cs', 'BarricadeMount'),
                       ('game/WindowBarricadeMesh.cs', 'WindowBarricadeStyle')]:
    parts.append(match(r'public enum ' + name + r'\s*\{[^}]+\}', read(filename)))
parts.append('public static class LampLight {' + match(r'public enum Kind\s*\{[^}]+\}', read('game/LampLight.cs')) + '}')
parts.append('public static class Refrigerator {' + match(r'public const byte GridW[^;]+;', read('game/inventory/StorageCrate.cs')) + match(r'public const float Watts[^;]+;', read('game/inventory/StorageCrate.cs')) + '}')
for file, name, power in [('game/GridPowerSource.cs', 'GridPowerSource', 'DefaultWatts'), ('game/GasPump.cs', 'GasPump', 'Watts')]:
    parts.append('public static class ' + name + '{' + match(r'public const float ' + power + r'[^;]+;', read(file)) + match(r'public static readonly Vector3 PortLocal[^;]+;', read(file)) + '}')
parts.append('public static class LootTables {')
for name in ['Crockery', 'Cutlery', 'Cookware']:
    parts.append(match(r'public static readonly ushort\[\] ' + name + r'\s*=\s*\{[^}]+\};', read('game/inventory/LootTables.cs')))
parts.append('}}')
(out / 'CatalogEnums.g.cs').write_text('// Generated original data declarations; do not edit.\n' + '\n'.join(parts))

fluid = read('game/FluidArt.cs')
spec = fluid[fluid.index('        public sealed class Spec'):fluid.index('        /// <summary>The ripped')]
catalog = fluid[fluid.index('        static readonly Lazy<'):fluid.index('        static readonly Dictionary<ushort, StandardMaterial3D>')]
configure = fluid[fluid.index('        public static void Configure'):fluid.index('        static ArrayMesh Load')]
(out / 'FluidArtData.g.cs').write_text('using Godot; using System.Text.Json; namespace UnturnedGodot { public static class FluidArt {\n' + spec + catalog + 'static Vector3 Vec(float[] v) => new(v[0],v[1],v[2]);\n' + configure + '}}')

loot = read('game/inventory/LootField.cs')
body = loot[loot.index('        struct Pt'):loot.index('        // Proximity is ANY')]
body = '\n'.join(line for line in body.splitlines() if 'readonly Dictionary<int, WorldItem> _live' not in line and 'readonly HashSet<int> _taken' not in line)
body += "\n public IEnumerable<(int Index, ushort ItemId, byte Table, float X, float Y, float Z)> Points() { for(int i=0;i<_pts.Count;i++) {var p=_pts[i]; int id=Roll(i,p.Type); if(id>=0) yield return(i,(ushort)id,p.Type,p.X,p.Y,p.Z); } }\n"
(out / 'RetailLootData.g.cs').write_text('using Godot; using System.Collections.Generic; namespace UnturnedGodot { public sealed class RetailLootData {\n' + body + '}}')

day = read('game/DayNightCycle.cs')
weather = read('game/WeatherManager.cs')
parts = ['namespace UnturnedBevy { public static class WorldDefaults {',
         match(r'public const float DefaultDayLength[^;]+;', day),
         match(r'public float Time[^;]+;', day).replace('public float Time', 'public const float StartTime')]
for name in ['SnowLayDays', 'SnowMeltDays', 'MeltRefC']:
    parts.append(match(r'public const float ' + name + r'[^;]+;', weather))
for name in ['FrequencyMultiplier', 'DurationMultiplier']:
    parts.append(match(r'public static float ' + name + r'[^;]+;', weather))
parts.append('}}')
(out / 'WorldDefaults.g.cs').write_text('// Generated original environment defaults; do not edit.\n' + '\n'.join(parts))
