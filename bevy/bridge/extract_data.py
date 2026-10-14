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
parts.append('}')
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

crop = read('game/CropNode.cs').split('    // A planted farm crop in the WORLD:')[0] + '}\n'
(out / 'CropData.g.cs').write_text(crop)
fluid = read('game/FluidTank.cs')
tank = fluid[fluid.index('    public class FluidTank'):]
(out / 'FluidTankData.g.cs').write_text('using Godot; namespace UnturnedGodot {\n' + tank)
food = read('game/FoodSpoil.cs').split('        // Spoil one day across every PLACED')[0] + '}}\n'
(out / 'FoodSpoilData.g.cs').write_text(food)
shelf = read('game/inventory/StoreShelf.cs')
shelf = shelf[shelf.index('        class Profile'):shelf.index('        static readonly Dictionary<string, ArrayMesh>')]
(out / 'ShelfData.g.cs').write_text('using Godot; using System.Collections.Generic; using SDG.Unturned; namespace UnturnedGodot { public static class StoreShelf {\n' + shelf + '}}\n')
builder = read('game/WorldBuilder.cs')
container_table = match(r'static readonly System.Collections.Generic.Dictionary<string, \(string mesh, int table, bool display, string label\)> ContainerShelf = new\(\)\s*\{.*?\n        \};', builder)
container_kinds = builder[builder.index('        public static System.Collections.Generic.List<(string mesh, bool display, string label)> ContainerKinds()'):builder.index('        // Openable PROP DOORS')]
(out / 'ContainerKinds.g.cs').write_text('namespace UnturnedGodot { public static class WorldBuilder {\n' + container_table.replace('static readonly', 'public static readonly', 1) + container_kinds + '}}\n')
sync = read('game/ContainerNetSync.cs')
sync = sync.replace('        readonly Node _host;', '        // No scene node is needed by the original container lifecycle.')
sync = sync.replace('NetWorldServer server, Node host,', 'NetWorldServer server,')
sync = sync.replace('            _host = host;\n', '')
sync = sync.replace('        void RegisterAll(', '        public void RegisterAll(')
(out / 'ContainerNetSync.g.cs').write_text(sync)
main = read('game/Main.cs')
holiday = main[main.index('        public static string ActiveHolidayNow()'):main.index('        // --objects: PEI')]
(out / 'HolidayData.g.cs').write_text('namespace UnturnedGodot { public static class Main {\n' + holiday + '}}\n')
drop = read('game/WorldNetSync.cs')
drop = drop[drop.index('        void DropLoot(int index)'):]
drop = drop.replace('_field.TryGetDrop', 'TryGetDrop').replace('_field.TryGetWorldPos', 'TryGetWorldPos')
(out / 'DestructibleDrops.g.cs').write_text('using Godot; namespace UnturnedBevy { public sealed partial class WorldServices {\n' + drop)

# Catalog and map helpers have no scene dependencies. Extract the original bodies,
# rather than maintaining a second list of rewards, overrides or authored volumes.
rubble = read('game/DestructibleField.cs')
body = rubble[rubble.index('        public readonly struct Rubble'):]
drops = match(r'public static readonly System.Collections.Generic.Dictionary<string, \(ushort Item, byte Min, byte Max\)> Drops = new\(\)\s*\{.*?\n        \};', rubble)
(out / 'RubbleData.g.cs').write_text('using Godot; using System.IO; using System.Collections.Generic; namespace UnturnedGodot { public static class DestructibleField {\n' + drops + body)
deadzone = read('game/DeadzoneMap.cs').split('        /// <summary>Load the current map')[0] + '}}\n'
(out / 'DeadzoneData.g.cs').write_text(deadzone)
resource = read('game/ResourceField.cs')
reward = resource[resource.index('        public const float ForageResetSeconds'):resource.index('        /// <summary>Trunk radius')]
(out / 'ResourceData.g.cs').write_text('namespace UnturnedGodot { public static class ResourceField {\n' + reward + match(r'internal const float TreeSink[^;]+;', resource) + '}}\n')
