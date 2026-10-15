"""Original finite terrain sidecars and river sampling, without scene ownership."""
import re


def extract(terrain, out, match, get):
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)

    pieces = [
        'bool[,] _holes; bool _anyHoles; float[,] _riverCut;',
        match(r'        public sealed class River\s*\{.*?\n        \}', terrain),
        match(r'        readonly System.Collections.Generic.List<River> _rivers = [^\n]+', terrain),
        match(r'        public int RiverCount => [^\n]+', terrain),
        match(r'        public System.Collections.Generic.IReadOnlyList<River> Rivers => [^\n]+', terrain),
        match(r'        public int HoleCount\s*\{.*?\n        \}', terrain),
        match(r'        public bool IsHole\([^\n]+\n[^\n]+', terrain),
    ]
    for name in ('HolesPathFor', 'RiversPathFor', 'SplatPathFor'):
        pieces.append(match(r'        static string ' + name + r'\([^\n]+', terrain))
    for name in ('RiverBlendFactor', 'RiverBlendDrop'):
        pieces.append(match(r'        const float ' + name + r' = [^\n]+', terrain))
    for name in ('SaveHeightmap', 'LoadHeightmap', 'SaveSplat', 'LoadSplat',
                 'SetHole', 'SaveHoles', 'LoadHoles', 'SaveRivers', 'LoadRivers',
                 'RiverPolylines', 'RiverPathPoints', 'RiverProfile',
                 'RiverBlendProfile', 'RebuildRiverField', 'RiverCutWorld'):
        # Heightmap signatures carry trailing comments in the original source.
        if name in ('SaveHeightmap', 'LoadHeightmap'):
            body = match(r'        public (?:void|bool) ' + name + r'\([^\n]+\n        \{.*?\n        \}', terrain)
        else:
            body = get(terrain, name)
        if name == 'LoadHeightmap':
            body = once(body, '            RebuildAll();')
        if name == 'SetHole':
            body = once(body, '            JournalHole(gx, gy);')
        if name == 'LoadSplat':
            body = once(body, ' || _s0Img == null || _s1Img == null')
            for prefix in ('_s0Img.SetPixel(', '_s1Img.SetPixel(', 'UpdateSplat('):
                body, count = re.subn(r'^\s*' + re.escape(prefix) + r'[^\n]*\n', '\n', body, flags=re.M)
                assert count == 1, (name, prefix)
        if name == 'LoadRivers':
            body = once(body, '            if (_riverBeds != null) { _riverBeds.QueueFree(); _riverBeds = null; }   // drop the previous map\'s beds')
            body = once(body, '            RebuildRiverWater();')
        # Warnings are diagnostics; the original ignore/reset/exception paths
        # stay intact. No Godot native logging call is required for plain data.
        body = body.replace('GD.PushWarning(', 'Log.Err(')
        pieces.append(body)
    (out / 'FiniteIslandSurfaceSaved.g.cs').write_text(
        '// Original saved grid/paint/hole/river formats and analytical river cuts.\n'
        '// Scene mesh/water rebuild and edit journals belong to native consumers.\n'
        'using Godot; using UnturnedGodot; namespace UnturnedBevy {\n'
        'public sealed partial class FiniteIslandSurface {\n' + '\n'.join(pieces) + '\n}}\n')
