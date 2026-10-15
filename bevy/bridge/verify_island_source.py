#!/usr/bin/env python3
"""Compare the bridge's finite layout against unchanged source + real GodotSharp.

This is a numerical source comparison, not a visual golden. Both complete grids
and every layout record are compared; no engine/render process is needed because
ProcIsland uses only managed Godot math. Outputs stay in the requested work dir.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from xml.sax.saxutils import escape

REPO = Path(__file__).resolve().parents[2]
PROGRAM = r'''
using System.Security.Cryptography;
using System.Text.Json;
using UnturnedBevy;
using Godot;

var options = new JsonSerializerOptions { IncludeFields = true };
Directory.CreateDirectory(args[0]);
// Compare exact result bits at the Godot float epsilon and its scaled threshold,
// including reversed/equal/negative edges. Generator samples alone seldom land
// at these branches. Keep this runnable separately from the slower whole island.
var pairs = new[] { (0f, 0f), (0f, MathF.BitDecrement(1e-6f)), (0f, 1e-6f),
    (0f, 1e-5f), (100f, 100.00005f), (100f, 100.00011f),
    (-100f, -100.00005f), (-100f, -100.00011f), (-2f, -2f),
    (-2f, 2f), (2f, -2f), (2f, MathF.BitIncrement(2f)), (10f, 20f) };
var math = pairs.Select(p => new { from=p.Item1, to=p.Item2,
    equal=Mathf.IsEqualApprox(p.Item1,p.Item2),
    smooth=new[] { -3f, 0f, .5f, 3f, 15f }.Select(w =>
        BitConverter.SingleToInt32Bits(Mathf.SmoothStep(p.Item1,p.Item2,w))).ToArray() }).ToArray();
string nanSign;
try { nanSign=Mathf.Sign(float.NaN).ToString(); }
catch (Exception e) { nanSign=e.GetType().Name; }
File.WriteAllText(Path.Combine(args[0], "math.json"), JsonSerializer.Serialize(new {
    math, nanSign, sign=new[] { -2f, -0f, 0f, 2f }.Select(Mathf.Sign).ToArray(),
    angles=new[] { -MathF.PI, -MathF.PI/4f, 0f, 1f, MathF.PI/2f, MathF.PI }
        .Select(v => BitConverter.SingleToInt32Bits(Mathf.RadToDeg(v))).ToArray() }, options));
if (args.Length > 1 && args[1] == "--math-only") return;
var cases = new[] { (1, 1, 1234, false), (2, 1, 9876, true),
                    (3, 3, 771177, false), (1, 1, 1234, false) };
int index = 0;
foreach (var (x, z, seed, lakes) in cases)
{
    var layout = new FiniteIslandLayout(x, z);
    var pois = layout.GenerateIsland(seed, lakes);
    var path = Path.Combine(args[0], $"case{index++}");
    using (var writer = new BinaryWriter(File.Create(path + ".bin")))
    {
        writer.Write(layout.Width); writer.Write(layout.Height);
        for (int gx=0; gx<layout.Width; gx++)
            for (int gz=0; gz<layout.Height; gz++) writer.Write(layout.Grid[gx,gz]);
    }
    var data = new { x, z, seed, lakes, pois, layout.Links, layout.Connectors,
        layout.Tiles, layout.Buildings, layout.Routes, layout.Trails,
        layout.Camps, layout.Landmarks };
    File.WriteAllText(path + ".json", JsonSerializer.Serialize(data, options));
    // A second source boundary: final road curves/conform, still before prop
    // painting/scatter. Both sides run the extracted original operations.
    if (args.Length > 2 && args[1] == "--paths")
    {
        var surface = new FiniteIslandSurface(layout);
        surface.PaintBelowHeight(29.6f, 5);
        surface.PaintSteeperThan(.58f, 0);
        var roads = new FiniteIslandRoads(surface, args[2]);
        int roadCount = FiniteIslandPaths.SpawnRoutes(surface, roads);
        int trails = FiniteIslandPaths.SpawnTrails(surface, roads);
        roads.SavePaths(path + "-paths.dat");
        using (var writer = new BinaryWriter(File.Create(path + "-surface.bin")))
        {
            writer.Write(surface.Width); writer.Write(surface.Height);
            for (int gx=0; gx<surface.Width; gx++)
                for (int gz=0; gz<surface.Height; gz++) writer.Write(surface.Grid[gx,gz]);
            for (int gx=0; gx<surface.Width-1; gx++)
                for (int gz=0; gz<surface.Height-1; gz++) writer.Write(surface.Layers[gx,gz]);
        }
        File.WriteAllText(path + "-curves.json", JsonSerializer.Serialize(new {
            roadCount, trails, surface.IslandRoutes, FiniteIslandPaths.DebugCurves,
            FiniteIslandPaths.DebugKinds, FiniteIslandPaths.DebugRoutes }, options));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { x, z, seed, lakes,
        heights=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path+".bin"))),
        graph=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path+".json"))),
        pois=pois.Count, tiles=layout.Tiles.Count, buildings=layout.Buildings.Count,
        routes=layout.Routes.Count, trails=layout.Trails.Count }));
}
'''


def first_difference(a, b, at='$'):
    if type(a) is not type(b):
        return f'{at}: {a!r} != {b!r}'
    if isinstance(a, dict):
        if a.keys() != b.keys():
            return f'{at}: different keys'
        for key in a:
            found = first_difference(a[key], b[key], f'{at}.{key}')
            if found:
                return found
    elif isinstance(a, list):
        if len(a) != len(b):
            return f'{at}: count {len(a)} != {len(b)}'
        for i, (av, bv) in enumerate(zip(a, b)):
            found = first_difference(av, bv, f'{at}[{i}]')
            if found:
                return found
    elif a != b:
        return f'{at}: {a!r} != {b!r}'
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bridge', type=Path, required=True,
                        help='Immutable bridge DLL containing the layout adapter')
    parser.add_argument('--godot-sharp', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--math-only', action='store_true')
    parser.add_argument('--paths-environment', type=Path,
                        help='Also compare original road/conform passes using this PEI Environment directory')
    parser.add_argument('--placements-content', type=Path,
                        help='Also compare original object/paint/spawn consumers using this content directory')
    parser.add_argument('--dotnet', default=shutil.which('dotnet'))
    args = parser.parse_args()
    assert args.dotnet, 'Pass --dotnet or add dotnet to PATH'
    assert not args.placements_content or args.paths_environment, 'Placements require --paths-environment'
    args.work.mkdir(parents=True, exist_ok=True)
    # Extract only the original GenerateIsland orchestration with the same strict
    # source marker as the bridge build. No bridge shim is part of the reference.
    from extract_island import extract
    import re
    def match(pattern, source):
        result = re.search(pattern, source, re.S)
        assert result, pattern
        return result.group(0)
    def method(source, signature):
        return match(re.escape(signature) + r'\s*\{.*?\n        \}', source)
    for mode in ('source', 'bridge'):
        folder = args.work / mode
        folder.mkdir(parents=True, exist_ok=True)
        (folder / 'Program.cs').write_text(PROGRAM)
        if mode == 'source':
            extract(lambda p: (REPO / p).read_text(), folder, match, method)
            source_files = ['game/ProcIsland.cs', 'bevy/bridge/FiniteIslandLayout.cs']
            if args.paths_environment:
                from extract_island_paths import extract as extract_paths
                extract_paths(lambda p: (REPO / p).read_text(), folder, match, method)
                source_files += ['bevy/bridge/FiniteIslandSurface.cs', 'bevy/bridge/FiniteIslandRoads.cs']
            if args.placements_content:
                from extract_island_placement import extract as extract_placement
                extract_placement(lambda p: (REPO / p).read_text(), folder, match, method)
                source_files += ['bevy/bridge/FiniteIslandObjects.cs']
            (folder / 'Log.cs').write_text('namespace UnturnedGodot; public static class Log { public static void Print(string s) {} public static void Err(string s) {} }')
            includes = ''.join(f'<Compile Include="{escape(str(REPO / p))}" />' for p in source_files)
            dependency, name = args.godot_sharp.resolve(), 'GodotSharp'
        else:
            includes = ''
            dependency, name = args.bridge.resolve(), 'UnturnedBevy.Bridge'
        program = PROGRAM
        if not args.paths_environment:
            # Keep the first-stage verifier usable with its earlier immutable
            # bridge DLL, which deliberately has no road/surface classes.
            start = program.index('    // A second source boundary:')
            end = program.index('    Console.WriteLine(', start)
            program = program[:start] + program[end:]
        if args.placements_content:
            program = program.replace('        surface.PaintBelowHeight', '''        var objects = new FiniteIslandObjects(args[3]);
        var spawned = FiniteIslandPaths.Spawn(surface, objects);
        surface.PaintBelowHeight''')
            program = program.replace('        var roads =', '''        FiniteIslandPaths.PaintGroundwork(surface);
        var players = FiniteIslandPaths.ChoosePlayerSpawns(surface, seed);
        FiniteIslandPaths.SpawnTownFurniture(surface, objects);
        var roads =''')
            program = program.replace('        int trails =', '''        FiniteIslandPaths.SpawnRoadside(surface, objects);
        int trails =''')
            program = program.replace('        roads.SavePaths', '''        FiniteIslandPaths.SpawnCamps(surface, objects);
        FiniteIslandPaths.SpawnLandmarks(surface, objects);
        var spawns = FiniteIslandPaths.GenerateSpawnTables(surface, seed);
        File.WriteAllText(path + "-placements.json", JsonSerializer.Serialize(new {
            spawned, players, spawns,
            objects = objects.Placements.Select(o => new { o.Name, o.Position, o.Material,
                X=o.Basis.X, Y=o.Basis.Y, Z=o.Basis.Z }).ToArray()
        }, options));
        roads.SavePaths''')
        (folder / 'Program.cs').write_text(program)
        project = ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                   '<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
                   '</PropertyGroup><ItemGroup>' + includes +
                   f'<Reference Include="{name}"><HintPath>{escape(str(dependency))}</HintPath></Reference>'
                   '</ItemGroup></Project>')
        (folder / 'Compare.csproj').write_text(project)
        with (folder / 'build.log').open('w') as log:
            subprocess.run([args.dotnet, 'build', str(folder / 'Compare.csproj'), '--nologo'],
                           stdout=log, stderr=subprocess.STDOUT, check=True)
        with (folder / 'run.log').open('w') as log:
            command = [args.dotnet, str(folder / 'bin/Debug/net8.0/Compare.dll'), str(folder)]
            if args.math_only:
                command.append('--math-only')
            elif args.paths_environment:
                command += ['--paths', str(args.paths_environment.resolve())]
                if args.placements_content:
                    command.append(str(args.placements_content.resolve()))
            result = subprocess.run(command,
                                    stdout=subprocess.PIPE, stderr=log, text=True, check=True)
        (folder / 'summary.jsonl').write_text(result.stdout)
    errors = []
    sources = [args.work / 'source/math.json']
    if not args.math_only:
        sources += sorted((args.work / 'source').glob('case*.*'))
    for source in sources:
        bridge = args.work / 'bridge' / source.name
        a, b = source.read_bytes(), bridge.read_bytes()
        if a == b:
            print(f'PASS {source.name}: {len(a)} bytes {hashlib.sha256(a).hexdigest()}')
        elif source.suffix == '.json':
            errors.append(f'{source.name}: {first_difference(json.loads(a), json.loads(b))}')
        else:
            differences = sum(x != y for x, y in zip(a, b)) + abs(len(a)-len(b))
            errors.append(f'{source.name}: {differences} differing bytes')
    if errors:
        raise AssertionError('\n'.join(errors))


if __name__ == '__main__':
    main()
