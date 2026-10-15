"""Original finite prop/groundwork/spawn consumers with a value-only Place sink."""
import re


def extract(read, out, match, method):
    source = read('game/ProcIslandSpawn.cs')

    def get(name):
        signatures = re.findall(r'^        [A-Za-z][^;{}]*?\b' + re.escape(name) +
                                r'\([^;{}]*?\)\s*(?=\{)', source, re.M)
        assert len(signatures) == 1, (name, len(signatures))
        return method(source, signatures[0].strip())

    pieces = []
    for pattern in (
        r'        public static Basis RotFor\([^\n]+',
        r'        public const float PavementTop = [^\n]+',
        r'        public const int DirtLayer = [^\n]+',
        r'        public const int RoadLayer = [^\n]+',
        r'        public const float SteepRise = [^\n]+',
        r'        const float TileHalfSpan = [^\n]+',
        r'        const byte CivilianTable = [^\n]+',
        r'        static int RoadsideOnRoad;[^\n]*',
        r'        public static readonly [^\n]+ FenceMarks = [^\n]+',
        r'        static readonly \(string Name, float Radius, float Bottom, float Height\)\[\] BoulderProps =\s*\{.*?\n        \};',
        r'        static readonly [^\n]+ BoulderMarks = [^\n]+',
        r'        const float CorridorCell = [^\n]+',
        r'        static \(int, int\) CorridorKey\([^\n]+',
        r'        const float TentHalfDepth = [^\n]+',
        r'        const float TentHalfWide = [^\n]+',
    ):
        pieces.append(match(pattern, source))
    for name in ('ArmDir', 'Spawn', 'ScatterBoulders', 'RouteCorridorCells',
                 'PaintGroundwork', 'PaintFootprint', 'Flatten',
                 'SpawnTownFurniture', 'ScatterTownFurniture', 'PavementPos', 'TownPropOk',
                 'SpawnRoadside', 'ScatterRoadside', 'RoadsideOk',
                 'SpawnCamps', 'SpawnLandmarks', 'ChoosePlayerSpawns', 'Near',
                 'GenerateSpawnTables', 'SpawnGroundOk'):
        body = get(name)
        if name == 'Spawn':
            # Diagnostic-only source reports have no influence on generated data.
            for line in ('            ReportPieces(terr);', '            ReportTowns(terr);'):
                assert body.count(line) == 1
                body = body.replace(line, '')
        # The original scene has one static Terrain water setting. The data
        # holder carries that same setting per generation, avoiding global state.
        body = body.replace('Terrain.HasWater', 'terr.HasWater').replace('Terrain.SeaLevelY', 'terr.SeaLevelY')
        pieces.append(body)
    (out / 'FiniteIslandPlacement.g.cs').write_text(
        '// Generated original placement, paint and spawn algorithms; no scene nodes.\n'
        'using Godot; using UnturnedGodot;\n'
        'using Terrain = UnturnedBevy.FiniteIslandSurface;\n'
        'using EditorObjects = UnturnedBevy.FiniteIslandObjects;\n'
        'namespace UnturnedBevy { public static partial class FiniteIslandPaths {\n' +
        '\n'.join(pieces) + '\n}}\n')
    editor = read('game/EditorObjects.cs')
    euler = match(r'        public static Basis FromEuler\(float ex, float ey, float ez\) =>[^;]+;', editor)
    (out / 'FiniteIslandObjects.g.cs').write_text(
        '// Original EditorObjects retail placement convention.\nusing Godot;\n'
        'namespace UnturnedBevy { public sealed partial class FiniteIslandObjects {\n' + euler + '\n}}\n')
