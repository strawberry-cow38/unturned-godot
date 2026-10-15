"""Original finite chart raster and town labels; return data instead of UI globals."""


def extract(read, out, match, method):
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)
    source = read('game/ProcIslandMap.cs')
    rebind = method(source, 'public static bool Rebind(Terrain terr, int seed)')
    rebind = once(rebind, 'public static bool Rebind(Terrain terr, int seed)',
                  'public static ChartBinding Rebind(Terrain terr, int seed, string contentDirectory)')
    rebind = rebind.replace('return false;', 'return null;')
    rebind = once(rebind, 'ProjectSettings.GlobalizePath("res://content/" + name)',
                  'System.IO.Path.Combine(contentDirectory, name)')
    rebind = once(rebind, '            MapUI.IslandImage = name;')
    rebind = once(rebind, 'MapUI.IslandSize =', 'float size =')
    rebind = once(rebind, 'MapUI.MapCentre =', 'var centre =')
    start = rebind.index('            if (System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/" + nodes)))')
    # Preserve the source optional-nodes handoff without changing caller globals.
    rebind = rebind[:start] + '''            return new ChartBinding(centre, size, name,
                System.IO.File.Exists(System.IO.Path.Combine(contentDirectory, nodes)) ? nodes : null);
        }'''
    bake = method(source, 'public static string Bake(Terrain terr, int seed)')
    bake = once(bake, 'public static string Bake(Terrain terr, int seed)',
                'public static ChartData Bake(Terrain terr, int seed)')
    bake = once(bake, 'Image.CreateEmpty(Res, Res, false, Image.Format.Rgb8)', 'new ChartPixels(Res, Res)')
    bake = bake.replace('Terrain.SeaLevelY', 'terr.SeaLevelY').replace('Terrain.HasWater', 'terr.HasWater')
    # All raster work above this original persistence/UI handoff stays unchanged.
    start = bake.index('            BakeNodes(terr, seed, cx, cz);')
    bake = bake[:start] + '''            return new ChartData(Res, Res, img.Bytes, BakeNodes(terr, seed, cx, cz),
                new Vector2(cx, cz), size, $"island_{seed}_map.png", $"nodes_island_{seed}.tsv");
        }'''
    nodes = method(source, 'static void BakeNodes(Terrain terr, int seed, float cx, float cz)')
    nodes = once(nodes, 'static void BakeNodes', 'static string BakeNodes')
    nodes = once(nodes, 'if (terr?.IslandTiles == null) return;', 'if (terr?.IslandTiles == null) return "";')
    start = nodes.index('            string file = $"nodes_island_{seed}.tsv";')
    nodes = nodes[:start] + '            return sb.ToString();\n        }'
    fruits = match(r'        static readonly string\[\] Fruits =\s*\{.*?\n        \};', source)
    (out / 'FiniteIslandChart.g.cs').write_text(
        '// Original ProcIslandMap pixel/label loops; PNG encoding and UI routing are native responsibilities.\n'
        'using Godot; using UnturnedGodot;\n'
        'using Terrain = UnturnedBevy.FiniteIslandSurface; using ProcIslandSpawn = UnturnedBevy.FiniteIslandPaths;\n'
        'namespace UnturnedBevy { public static partial class FiniteIslandChart {\n'
        'public const int Res=1024;\n' + rebind + '\n' + bake + '\n' + fruits + '\n' + nodes + '\n}}\n')
    layer = match(r'        public static Color LayerColor\(byte l\) => l switch\s*\{.*?\n        \};', read('game/Terrain.cs'))
    (out / 'FiniteIslandLayerColor.g.cs').write_text(
        '// Original terrain chart colors, including the source layer5 blue.\n'
        'using Godot; namespace UnturnedBevy { public sealed partial class FiniteIslandSurface {\n' + layer + '\n}}\n')
