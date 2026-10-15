"""Preserve Terrain.GenerateIsland's actual order without loading a scene node."""


def extract(read, out, match, method):
    source = read('game/Terrain.cs')
    body = method(source, 'public System.Collections.Generic.List<ProcIsland.Poi> GenerateIsland(int seed, bool lakes = false)')
    # RebuildAll is the scene mesh/collider side effect. The unchanged height and
    # layout outputs are consumed by the native materializer after later source
    # ProcIslandSpawn passes have finished conforming the roads.
    assert body.count('            RebuildAll();') == 1
    body = body.replace('            RebuildAll();', '')
    (out / 'FiniteIslandLayout.g.cs').write_text(
        '// Generated from game/Terrain.cs. Only RebuildAll is omitted.\n'
        'using Godot; using UnturnedGodot; namespace UnturnedBevy {\n'
        'public sealed partial class FiniteIslandLayout {\n' + body + '\n}}\n')
