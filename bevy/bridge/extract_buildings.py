"""Original wall meshes/collision as values; no replacement building generator.

WallOpenings, RoofShapes, ConvexHull and BuildingGen already live in UnturnedSim.
Keep WallSurface's geometry methods verbatim, replacing only engine resource sinks.
"""
import re


def extract(read, out, match, method):
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)

    # Source palette loading and role selection, with one cache per content root
    # instead of a process-global Godot resource cache.
    palette = read('game/WallMaterials.cs')
    palette = once(palette, 'namespace UnturnedGodot', 'namespace UnturnedBevy')
    palette = once(palette, 'public static class WallMaterials', 'public sealed partial class BuildingPalettes')
    palette = palette.replace('static ', '')
    palette = once(palette, 'ProjectSettings.GlobalizePath("res://content/wall_palettes.tsv")',
                   'System.IO.Path.Combine(contentRoot, "wall_palettes.tsv")')
    palette = palette.replace('Log.Err(', 'UnturnedGodot.Log.Err(')
    (out / 'BuildingPalettes.g.cs').write_text('// Original WallMaterials data and lookup; instance cache only.\n' + palette)

    source = read('game/WallSurface.cs')
    fields = source[source.index('        public float Length'):source.index('        MeshInstance3D _mesh')]
    rebuilt = method(source, 'public void Rebuild()')
    # Vertex writers stay unchanged. Native rendering receives the unindexed
    # triangle stream and generates the same flat normals before any indexing.
    mesh = rebuilt[rebuilt.index('            var solids ='):rebuilt.index('            st.GenerateNormals();')]
    mesh = re.sub(r'^\s*(?:st|back)\.(?:Begin|SetSmoothGroup)\([^\n]+\n', '\n', mesh, flags=re.M)
    trim = rebuilt[rebuilt.index('            if (Framed && Openings.Count > 0)'):rebuilt.index('            // collision:')]
    trim = trim[:trim.index('                tt.GenerateNormals();')] + '\n            }\n'
    trim = trim.replace('var tt = new SurfaceTool();', 'tt = new SurfaceTool();')
    trim = re.sub(r'^\s*tt\.(?:Begin|SetSmoothGroup)\([^\n]+\n', '\n', trim, flags=re.M)
    collision = rebuilt[rebuilt.index('            int want ='):rebuilt.index('            RebuildGlass();')]
    start = collision.index('            while (_shapes.Count > want)')
    end = collision.index('            for (int i = 0; i < solids.Count; i++)')
    collision = collision[:start] + '            var _shapes = Enumerable.Range(0, want).Select(_ => new BuildingCollision()).ToArray();\n' + collision[end:]
    collision = once(collision, '            RebuildTrimCollision();', '            var trimShapes = BuildTrimCollision();')
    collision = collision.replace('BoxShape3D', 'BuildingBox')
    body = 'public BuildingGeometry Build() {\n' + mesh + '\n SurfaceTool tt = null;\n' + trim + collision
    body += '\n return new(st.Vertices.ToArray(), back?.Vertices.ToArray() ?? [], tt?.Vertices.ToArray() ?? [], _shapes, trimShapes);\n}\n'

    trim_collision = method(source, 'void RebuildTrimCollision()')
    trim_collision = trim_collision[:trim_collision.index('            while (_trimShapes.Count > boxes.Count)')]
    trim_collision = trim_collision.replace('void RebuildTrimCollision()', 'BuildingCollision[] BuildTrimCollision()')
    trim_collision += '            return boxes.Select(b => new BuildingCollision { Position=(b.Min+b.Max)*0.5f, Shape=new BuildingBox { Size=b.Max-b.Min } }).ToArray();\n        }'

    methods = []
    for signature in [
        'public bool WorldToUV(Vector3 world, out float u, out float v)',
        'void AddGableCap(SurfaceTool st, float t)',
        'void AddTrim(SurfaceTool st, WallOpening o)',
        'public static Vector2 ClearOpeningSize(WallOpening o)',
        'void AddTaperedSolid(SurfaceTool st, WallSolid s, float t, SurfaceTool backTool = null)',
        'bool PlumbShift(out float su, out float sv)',
        'Vector3[] PlumbSolid(WallSolid s, float t, float su, float sv)',
        'void AddHull(SurfaceTool st, Vector3[] pts, SurfaceTool backTool)',
        'static void Facing(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 want)',
        'static void SetHull(CollisionShape3D cs, Vector3[] pts)',
        'List<Vector2> ClipToTaper(WallSolid s)',
        'static void AddWallBox(SurfaceTool st, WallSolid s, List<WallSolid> all, float t,\n                               SurfaceTool backTool = null)',
        'static bool Abuts(List<WallSolid> all, WallSolid s, int du, int dv)',
        'static void AddBoxFaces(SurfaceTool st, Vector3 a, Vector3 b,\n                                bool front, bool back, bool minU, bool maxU, bool minV, bool maxV,\n                                SurfaceTool backTool = null)',
    ]:
        methods.append(method(source, signature))
    for signature in ['static void Tri', 'static void Quad']:
        methods.append(match(re.escape(signature) + r'\([^\n]+\n        \{[^\n]+', source))
    methods.append(match(r'static void AddBox\([^\n]+\n[^;]+;', source))
    methods.append(match(r'public Vector3 UVToWorld\([^;]+;', source))
    output = fields + body + trim_collision + '\n'.join(methods)
    output = output.replace('SurfaceTool', 'BuildingTriangles').replace('CollisionShape3D', 'BuildingCollision').replace('ConvexPolygonShape3D', 'BuildingHull')
    (out / 'BuildingSurface.g.cs').write_text(
        '// Original WallSurface geometry. Engine resource allocation replaced by value sinks.\n'
        'using Godot; using System.Collections.Generic; using UnturnedSim; namespace UnturnedBevy {\n'
        'public sealed partial class BuildingSurface {\n' + output + '\n}}\n')

    # EditorBuildings' data assembly, preserving its order: generated surfaces,
    # stairs/ramps and floor cuts, corner solving, then translated designations.
    # UI/undo and the render-harness-only open-door switch are not data inputs.
    editor = read('game/EditorBuildings.cs')
    def editor_method(signature):
        start = editor.index(signature)
        return method(editor, editor[start:editor.index('{', start)].rstrip())

    generate = editor_method('public BuildingGenResult Generate(BuildingGenSpec spec)')
    generate = ('public BuildingGenResult Generate(BuildingGenSpec spec) {\n'
                + generate[generate.index('            var res ='):generate.index('            var before =')]
                .replace('            BuildingGen.FurnishTrace = null;\n', '')
                + '            _stairRamps.Clear(); _walls.Clear();\n'
                + generate[generate.index('            foreach (var pl in res.Walls)'):generate.index('            var solved =')]
                + '            SolveCorners();\n'
                + generate[generate.index('            DesignatedKind = res.Kind;'):generate.index('            ShowFurniture();')]
                + '            ActiveMaterial = mat; return res;\n}')
    spawn = editor_method('WallSurface SpawnWall(')
    spawn = once(spawn, 'new WallSurface\n', 'new BuildingSurface(WallMaterials)\n')
    spawn = once(spawn, '            AddChild(w);\n')
    spawn = once(spawn, '            _pickToWall[w.BodyRid] = w;\n')
    plan = editor_method('WallPlan PlanOf(WallSurface w)')
    plan = plan[:plan.index('            // Overwrite the four colour fields')] + '            return pl;\n}'
    stairs = editor_method('public int FitStairsToStructure(')
    start = stairs.index('            var ramp = new StaticBody3D')
    end = stairs.index('            if (!cutFloor)')
    stairs = (stairs[:start]
              + '            _stairRamps.Add(new BuildingRamp(foot, new Vector3(Mathf.Atan2(rise, run), yaw, 0f),\n'
              + '                new Vector3(width, WallOpenings.StairTreadThickness, slope)));\n'
              + stairs[end:])
    save = editor_method('public int Save()')
    save = ('public string SaveText() {\n'
            + save[save.index('            var plans ='):save.index('            // An empty layout')]
            + '            return WallSave.Write(plans, DesignatedKind, Designations, DoorMarks, Furniture);\n}')
    load = editor_method('public int Load()')
    load = ('public int LoadText(string text) {\n'
            + '            var plans = WallSave.Read(text.Split(\'\\n\'), out var kind, out var rooms, out var marks, out var props);\n'
            + load[load.index('            Designations.Clear();'):load.index('            Log.Print(')]
            + '            return plans.Count;\n}')
    load = once(load, ' ShowFurniture();')
    parts = [generate, spawn, plan, stairs, save, load]
    for signature in ['static Aabb SurfaceAabb(WallSurface w)',
                      'public bool PunchHole(WallSurface w, float u0, float v0, float u1, float v1)',
                      'public List<(WallSurface W, float Len, Vector3 Pos)> SolveCorners()',
                      'static bool CrossOnPlan(', 'public static IEnumerable<Vector3> StairTreadOrigins(']:
        parts.append(editor_method(signature))
    parts.extend(match(pattern, editor) for pattern in [
        r'public static readonly Vector3 StageOrigin[^;]+;',
        r'public const float GroundClearance[^;]+;',
        r'public static float StoreyHeight[^;]+;'])
    output = '\n'.join(parts).replace('WallSurface', 'BuildingSurface').replace('Aabb', 'BuildingAabb')
    output = output.replace(' w.Rebuild();', '').replace('                w.Rebuild();', '')
    (out / 'BuildingScene.g.cs').write_text(
        '// Original EditorBuildings scene assembly; engine nodes/UI/undo replaced by values.\n'
        'using Godot; using UnturnedSim; namespace UnturnedBevy {\n'
        'public sealed partial class BuildingScene {\n' + output + '\n}}\n')

    # The fitted door path reuses original DoorDeploy catalog/wooden fallback,
    # fitting and WallSurface alignment. Node/material allocation becomes values;
    # the standalone ground lift is overwritten by PlaceDoor and is not retained.
    door = read('game/DoorDeploy.cs')
    wooden = method(door, 'static List<WorldBuilder.DoorCatalogEntry> WoodenLeaves(string prop, string dir)')
    wooden = wooden.replace('static List<', 'List<', 1).replace('ObjMesh.Load(', 'Mesh(')
    spawn = method(door, 'public static Node3D SpawnProp(string prop, Node parent, Vector3 pos, float yawDeg, Vector2? fit = null, bool reverse = false)')
    catalog = spawn[spawn.index('            string dir ='):spawn.index('            var host =')]
    catalog = catalog.replace('ProjectSettings.GlobalizePath("res://content/objects/")',
                              'System.IO.Path.Combine(contentRoot, "objects") + System.IO.Path.DirectorySeparatorChar')
    catalog = catalog.replace('def.DoorProp', 'prop')
    leaves = spawn[spawn.index('            var xform ='):spawn.index('            if (made.Count == 0)')]
    leaves = once(leaves, '            var mat = MatFor(def.DoorProp, dir);\n')
    leaves = leaves.replace('Transform3D', 'BuildingTransform').replace('new List<ObjectDoor>()', 'new List<BuildingDoorLeaf>()')
    leaves = leaves.replace('ObjMesh.Load(', 'Mesh(').replace('def.DoorProp', 'prop')
    leaves = leaves.replace('pivotL *= sc;', 'pivotL = new Vector3(pivotL.X * sc.X, pivotL.Y * sc.Y, pivotL.Z * sc.Z);')
    a = leaves.index('                made.Add(ObjectDoor.Spawn(')
    b = leaves.index('                    soundName: e.Sound));', a) + len('                    soundName: e.Sound));')
    leaves = leaves[:a] + '''                made.Add(BuildingDoorLeaf.Create(e.MeshFile, leaf, pivotL, e.Axis,
                    reverse ? -e.AngleDeg : e.AngleDeg, e.DurationSec, xform,
                    WorldBuilder.LoadDoorCurve(dir, curveBase, "open"),
                    WorldBuilder.LoadDoorCurve(dir, curveBase, "close"), e.Sound));''' + leaves[b:]
    spawn = ('BuildingDoorHost SpawnProp(string prop, Vector2? fit, bool reverse) {\n'
             + 'if(string.IsNullOrEmpty(prop)) return null;\n' + catalog + leaves
             + '\n return made.Count == 0 ? null : new BuildingDoorHost(prop, made.ToArray());\n}')
    place = method(source, 'void PlaceDoor(Node3D host, WallOpening o)')
    place = place.replace('void PlaceDoor(Node3D host, WallOpening o)',
                          'static void PlaceDoor(BuildingSurface wall, BuildingDoorHost host, WallOpening o)')
    place = place.replace('host == null || !IsInstanceValid(host)', 'host == null').replace('Transform3D', 'BuildingTransform')
    a = place.index('            foreach (var d in host.GetChildren())')
    b = place.index('            // Land it on the TRIM', a)
    measured = '''            foreach(var leaf in host.Leaves) {
                var ab = leaf.Mesh.GetAabb();
                var meshWorld = leaf.ClosedMesh(wall.GlobalTransform * host.Transform);
                for(int i=0;i<8;i++) {
                    var q = wall.ToLocal(meshWorld * ab.GetEndpoint(i));
                    lo.X = Mathf.Min(lo.X,q.X);hi.X = Mathf.Max(hi.X,q.X);
                    lo.Y = Mathf.Min(lo.Y,q.Y);hi.Y = Mathf.Max(hi.Y,q.Y);
                }
            }
'''
    place = place[:a] + measured + place[b:]
    place = place.replace('TrimProfile', 'BuildingSurface.TrimProfile')
    output = '\n'.join([wooden, spawn, place]).replace('Log.Err(', 'UnturnedGodot.Log.Err(')
    (out / 'BuildingOpenings.g.cs').write_text(
        '// Original DoorDeploy/WallSurface fitted leaf construction, values only.\n'
        'using Godot;using UnturnedGodot;using UnturnedSim;namespace UnturnedBevy {\n'
        'public sealed partial class BuildingOpenings {\n' + output + '\n}}\n')

    # Preserve source face indexing/fan order; discard only unused attributes.
    mesh = method(read('game/ObjMesh.cs'), 'public static ArrayMesh Load(string globalPath)')
    mesh = mesh[mesh.index('            var pos ='):mesh.index('            var arr =')]
    mesh = re.sub(r'^\s*var (?:col|nrm|uv|outC|outN|outU) =[^\n]+\n', '\n', mesh, flags=re.M)
    mesh = re.sub(r'^\s*(?:col|outC|outN|outU)\.Add\([^\n]+\n', '\n', mesh, flags=re.M)
    a = mesh.index("                else if (raw[0] == 'v' && raw[1] == 'n')")
    b = mesh.index("                else if (raw[0] == 'f' && raw[1] == ' ')",a)
    mesh = mesh[:a] + mesh[b:]
    (out / 'BuildingDoorMesh.g.cs').write_text(
        '// Original ObjMesh referenced-vertex parser; no mesh/resource storage.\n'
        'using Godot;namespace UnturnedBevy {public sealed partial class BuildingDoorMesh {\n'
        'public static BuildingDoorMesh Load(string globalPath) {\n' + mesh
        + '\n return new BuildingDoorMesh(outV.ToArray(),new Vector3(1,1,1));\n}}}\n')
