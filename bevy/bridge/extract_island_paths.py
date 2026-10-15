"""Extract finite surface/road data math; omit only explicitly listed scene work."""
import re


def extract(read, out, match, method):
    def get(source, name):
        # Source top-level method indentation is stable and excludes local
        # helpers. Keep multiline signatures and every arithmetic expression.
        signatures = re.findall(r'^        [A-Za-z][^\n]*\b' + re.escape(name) +
                                r'\([^;]*?\)\s*(?=\{)', source, re.M)
        assert len(signatures) == 1, (name, len(signatures))
        return method(source, signatures[0].strip())

    def replace_once(source, before, after=''):
        assert source.count(before) == 1, before
        return source.replace(before, after)

    terrain = read('game/Terrain.cs')
    pieces = []
    for name in ('SampleHeight', 'SampleDominantLayer', 'SlopeAt', 'NormalAt',
                 'ConformToPolylines', 'GatherConform', 'PaintSplat',
                 'PaintBelowHeight', 'PaintSteeperThan'):
        body = get(terrain, name)
        if name == 'SampleHeight':
            # This class is the fresh generator, before any editor river exists.
            body = replace_once(body, ' - RiverCutWorld(worldX, worldZ)')
        if name == 'ConformToPolylines':
            body = replace_once(body, '                JournalH(gx, gy);')
            body = replace_once(body, '            _dirty = true;')
            body = replace_once(body, '            RebuildChunksIn(minX, maxX, minY, maxY, withCollider: true);')
        if name.startswith('Paint'):
            body = replace_once(body, '_dom == null || _s0Img == null', '_dom == null')
            # Winner-take-all _dom IS the source editor splat; GPU image mirrors
            # and texture uploads are not a second data state in this adapter.
            for prefix in ('var c0 = new Color(', 'var c1 = new Color(',
                           '_s0Img.SetPixel(', 'UpdateSplat('):
                pattern = r'^\s*' + re.escape(prefix) + r'[^\n]*\n'
                body, count = re.subn(pattern, '\n', body, flags=re.M)
                assert count == 1, (name, prefix, count)
            # Reporting does not participate in the paint result. Avoid importing
            # the render material name table solely for its diagnostic label.
            body = re.sub(r'^\s*Log.Print\([^\n]*\n', '\n', body, flags=re.M)
        pieces.append(body)
    (out / 'FiniteIslandSurface.g.cs').write_text(
        '// Generated from Terrain.cs: fresh-grid math, no journal/scene/GPU side effects.\n'
        'using Godot; using UnturnedGodot; namespace UnturnedBevy {\n'
        'public sealed partial class FiniteIslandSurface {\n' + '\n'.join(pieces) + '\n}}\n')

    roads = read('game/RoadField.cs')
    pieces = [match(r'        struct RoadMat \{[^\n]+', roads)]
    pieces.append(match(r'        class Joint\s*//[^\n]*\n        \{.*?\n        \}', roads))
    pieces.append('class RoadData { public int Material; public bool IsLoop; public List<Joint> Joints = new(); public byte[] GuidBytes; }')
    for name in ('AddRoadFromPolyline', 'RetangentRoad', 'RemoveRoad',
                 'SampleCentreline', 'SampleWalk', 'RoadFrame', 'Bezier',
                 'SplinePos', 'SegLength', 'SampleNormal', 'ParseRoadsDat', 'SavePaths'):
        body = get(roads, name)
        if name == 'AddRoadFromPolyline':
            body = replace_once(body, '            if (r.Joints.Count >= 2 && r.Material >= 0 && r.Material < _mats.Count) BuildRoadNode(r);')
        if name == 'RemoveRoad':
            body = replace_once(body, '            r.Mi?.QueueFree(); r.Body?.QueueFree();')
        pieces.append(body)
    (out / 'FiniteIslandRoads.g.cs').write_text(
        '// Generated original RoadField data/curve/save operations; no scene nodes.\n'
        'using Godot; using System.Collections.Generic; using System.IO; namespace UnturnedBevy {\n'
        'public sealed partial class FiniteIslandRoads {\n' + '\n'.join(pieces) + '\n}}\n')

    source = read('game/ProcIslandSpawn.cs')
    pieces = []
    for pattern in (r'        const int SmoothWin = [^\n]+',
                    r'        static readonly float FloorSlack =.*?;',
                    r'        public static readonly int RouteJointStride =.*?;',
                    r'        public const float RoadPropLift = [^\n]+',
                    r'        public const float BuildingLift = [^\n]+',
                    r'        public const int GrassLayer = [^\n]+',
                    r'        public const int TrailMaterial = [^\n]+',
                    r'        public static readonly [^\n]+ DebugCurves = [^\n]+',
                    r'        public static readonly [^\n]+ DebugKinds = [^\n]+',
                    r'        public static readonly [^\n]+ DebugRoutes = [^\n]+'):
        pieces.append(match(pattern, source))
    for name in ('SmoothProfile', 'JointPosFor', 'JointClearanceFor', 'JointPosAlong',
                 'BuildingPosFor', 'TilePosFor', 'PosFor', 'SpawnRoutes', 'SpawnTrails'):
        body = get(source, name)
        if name == 'SpawnRoutes':
            body = replace_once(body, '            ReportCapJoins(terr);')
        pieces.append(body)
    (out / 'FiniteIslandPaths.g.cs').write_text(
        '// Generated original island route consumer; curve/conform ordering unchanged.\n'
        'using Godot; using UnturnedGodot;\n'
        'using Terrain = UnturnedBevy.FiniteIslandSurface;\n'
        'using RoadField = UnturnedBevy.FiniteIslandRoads;\n'
        'namespace UnturnedBevy { public static partial class FiniteIslandPaths {\n' +
        '\n'.join(pieces) + '\n}}\n')
