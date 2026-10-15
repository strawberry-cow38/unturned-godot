"""Keep original finite foliage/resource scatters and file formats, redirect IO."""


def extract(read, out, match, method):
    source = read('game/ProcIslandFoliage.cs')
    # Preserve the complete original loops, manifest ordering and random draws.
    # Only the destination owner and filesystem roots differ from scene loading.
    def once(before, after):
        nonlocal source
        assert source.count(before) == 1, before
        source = source.replace(before, after)
    once('namespace UnturnedGodot', 'namespace UnturnedBevy')
    once('public static class ProcIslandFoliage', 'public static class FiniteIslandScatter')
    for name in ('Bake', 'BakeResources'):
        once(f'public static string {name}(Terrain terr, int seed, string mapKey)',
             f'public static string {name}(Terrain terr, int seed, string mapKey, string contentDirectory, string outputDirectory)')
    once('string src = ProjectSettings.GlobalizePath(SourceDir);',
         'string src = Path.Combine(contentDirectory, "foliage");')
    once('string src = ProjectSettings.GlobalizePath("res://content/resources/");',
         'string src = Path.Combine(contentDirectory, "resources");')
    before = 'string dst = ProjectSettings.GlobalizePath($"res://content/{dirName}/");'
    assert source.count(before) == 2
    source = source.replace(before, 'string dst = Path.Combine(outputDirectory, dirName);')
    source = source.replace('Terrain.HasWater', 'terr.HasWater').replace('Terrain.SeaLevelY', 'terr.SeaLevelY')
    (out / 'FiniteIslandScatter.g.cs').write_text(
        '// Generated original scatter algorithms and binary writers; private output root.\n'
        'using Terrain = UnturnedBevy.FiniteIslandSurface;\n'
        'using ProcIslandSpawn = UnturnedBevy.FiniteIslandPaths;\n'
        'using Log = UnturnedGodot.Log;\n' + source)
    surface = method(read('game/Terrain.cs'), 'public (ulong H, ulong S) GroundFingerprint()')
    (out / 'FiniteIslandSurfaceAssets.g.cs').write_text(
        '// Original ground fingerprint used by source scatters.\n'
        'namespace UnturnedBevy { public sealed partial class FiniteIslandSurface {\n' + surface + '\n}}\n')
