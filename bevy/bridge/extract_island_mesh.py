"""Original finite Terrain chunk geometry before the engine mesh/collider handoff."""


def extract(read, out, match, method):
    source = read('game/Terrain.cs')
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)
    chunk = method(source, 'public void RebuildChunk(int cxi, int cyi, bool withCollider = true)')
    chunk = once(chunk, 'public void RebuildChunk(int cxi, int cyi, bool withCollider = true)',
                 'public ChunkData BuildChunk(int cxi, int cyi)')
    chunk = once(chunk, 'if (_grid == null || _chunkMi == null || cxi < 0 || cyi < 0 || cxi >= _chunksX || cyi >= _chunksY) return;',
                 'if (cxi < 0 || cyi < 0 || cxi >= ChunkCountX || cyi >= ChunkCountZ) return null;')
    chunk = once(chunk, 'if (nx < 2 || ny < 2) return;', 'if (nx < 2 || ny < 2) return null;')
    end = chunk.index('            if (t == 0)')
    chunk = chunk[:end] + '            return new ChunkData(cxi, cyi, verts, norms, uvs, cols, idx);\n        }'
    pieces = [
        match(r'        const int CHUNK = [^\n]+', source),
        match(r'        const int RiverSubdiv = [^\n]+', source),
        match(r'        float RiverCutAt\([^\n]+\n[^\n]+', source),
        match(r'        bool QuadHasRiver\([^\n]+\n[^\n]+\n[^\n]+', source),
        match(r'        public float SurfaceHeightWorld\([^\n]+\n[^\n]+', source),
        method(source, 'float BaseHeightWorld(float worldX, float worldZ)'),
        chunk,
    ]
    (out / 'FiniteIslandSurfaceMesh.g.cs').write_text(
        '// Original Terrain.RebuildChunk preparation; native renderer owns scene and collider creation.\n'
        'using Godot; namespace UnturnedBevy { public sealed partial class FiniteIslandSurface {\n'
        + '\n'.join(pieces) + '\n}}\n')
