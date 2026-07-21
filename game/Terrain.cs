using Godot;
using System.IO;

namespace UnturnedGodot
{
    // Loads a real Unturned map's terrain from its Landscape tile heightmaps (source Framework/Landscapes/LandscapeTile.cs).
    // Each Tile_X_Y_Source.heightmap = HEIGHTMAP_RESOLUTION^2 (257x257) BIG-ENDIAN ushorts, x outer / y inner; a sample =
    // raw/65535 (normalized 0..1). Tile = TILE_SIZE 1024 m, samples HEIGHTMAP_WORLD_UNIT 4 m apart; TILE_HEIGHT 2048 m, so
    // world height = h*2048 - 1024 (0.5 = sea level 0). Tile at landscape coord (cx,cy) spans world x[cx*1024 .. +1024],
    // z[cy*1024 .. +1024]. Unity->Godot: negate Z (the port's convention).
    public partial class Terrain : Node3D
    {
        const int RES = 257;
        const int SRES = 256, SLAYERS = 8;   // Landscape SPLATMAP_RESOLUTION + SPLATMAP_LAYERS (per-texel layer weights, 1 byte each)
        const float TILE_SIZE = 1024f, TILE_HEIGHT = 2048f, UNIT = 4f;
        const float BRUSH_FALLOFF = 0.5f;   // source Devkit brushFalloff: full strength inside this radius fraction, then linear to 0 at the edge
        static float BrushAlpha(float normDist) => normDist <= BRUSH_FALLOFF ? 1f : (1f - normDist) / (1f - BRUSH_FALLOFF);   // source TerrainEditor.getBrushAlpha

        // The 8 shared terrain material layers, colored as a stand-in until real splatmap texture blending. Inferred from
        // the PEI splatmap layout (layer 5 = ocean/water dominant, 2 = grass/ground, 3 = the road network, 0/7 = forest).
        static Color LayerColor(byte l) => l switch
        {
            // source-accurate: avg colour of each layer's REAL albedo (extracted from core.masterbundle via UnityPy).
            // Layer->material mapping read from PEI Level.hierarchy (see reference_unturned_world memory).
            0 => new Color(0.545f, 0.325f, 0.224f),   // PEI_Dirt_01
            1 => new Color(0.690f, 0.627f, 0.404f),   // PEI_Farm_Wheat_00 (crop field)
            2 => new Color(0.220f, 0.443f, 0.224f),   // PEI_Grass_00
            3 => new Color(0.494f, 0.314f, 0.247f),   // PEI_Gravel_00
            4 => new Color(0.290f, 0.294f, 0.290f),   // Russia_Road_00 (paved, shared)
            5 => new Color(0.170f, 0.310f, 0.470f),   // PEI_Sand_01 (real avg sand=0.69,0.55,0.36) but shown OCEAN BLUE: layer 5 is mostly underwater seabed; real water plane at seaLevel*256 is TODO, then this reverts to sand
            6 => new Color(0.714f, 0.714f, 0.714f),   // Yukon_Snow_00 (shared)
            _ => new Color(0.553f, 0.306f, 0.184f),   // 7: PEI_Stone_01
        };

        // Real per-layer albedo textures (extracted from core.masterbundle), dominant-layer selected + world-tiled.
        // UV samples the dominant-layer index map; the chosen albedo tiles by world XZ at the source scale (texW*0.25 = 16u).
        // Layer 5 (sand seabed) -> ocean blue until a real water plane exists.
        const string TERRAIN_SHADER = @"
shader_type spatial;
uniform sampler2DArray albedos : source_color, filter_nearest_mipmap, repeat_enable;
uniform sampler2D splat0 : filter_linear;
uniform sampler2D splat1 : filter_linear;
uniform float tileWorld = 16.0;
varying vec3 wpos;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
    vec4 w0 = texture(splat0, UV);
    vec4 w1 = texture(splat1, UV);
    vec2 tuv = wpos.xz / tileWorld;
    // winner-take-all: dominant splat layer per pixel = hard-edged distinct regions. Per master (+ ref shots)
    // this matches the real game look. Splat sampled bilinear so borders follow the smooth contour (not blocky).
    float ws[8];
    ws[0] = w0.r; ws[1] = w0.g; ws[2] = w0.b; ws[3] = w0.a;
    ws[4] = w1.r; ws[5] = w1.g; ws[6] = w1.b; ws[7] = w1.a;
    int best = 0; float bw = ws[0];
    for (int i = 1; i < 8; i++) { if (ws[i] > bw) { bw = ws[i]; best = i; } }
    ALBEDO = texture(albedos, vec3(tuv, float(best))).rgb;
    ROUGHNESS = 1.0;
}
";

        static ShaderMaterial BuildTerrainMaterial(Texture2D splat0, Texture2D splat1)
        {
            var imgs = new Godot.Collections.Array<Image>();
            for (int l = 0; l < SLAYERS; l++)
            {
                var img = new Image();
                if (img.Load(ProjectSettings.GlobalizePath($"res://content/terrain/layer{l}.png")) != Error.Ok) { GD.Print($"[TERRAIN] texture load FAILED: layer{l}"); return null; }
                img.Convert(Image.Format.Rgba8);
                img.GenerateMipmaps();
                imgs.Add(img);
            }
            var arr = new Texture2DArray();
            if (arr.CreateFromImages(imgs) != Error.Ok) return null;

            var mat = new ShaderMaterial { Shader = new Shader { Code = TERRAIN_SHADER } };
            mat.SetShaderParameter("albedos", arr);
            mat.SetShaderParameter("splat0", splat0);
            mat.SetShaderParameter("splat1", splat1);
            mat.SetShaderParameter("tileWorld", 16f);
            return mat;
        }

        // Merged-map height grid + placement, stashed so gameplay can sample the ground height at a world XZ (spawns etc.).
        float[,] _grid; int _gw, _gh; float _bx, _bz;
        byte[,] _dom; int _dw, _dh;   // dominant splatmap layer per texel -> SampleDominantLayer (grassy-spawn picking)
        MeshInstance3D[,] _chunkMi; StaticBody3D[,] _chunkBody; int _chunksX, _chunksY; Material _terrMat; bool _withCollider;   // editor sculpt: per-chunk meshes so a stroke rebuilds ONLY the touched chunks
        const int CHUNK = 48;   // grid cells per chunk side (chunks share edge verts, so no seams)
        Image _s0Img, _s1Img; ImageTexture _s0Tex, _s1Tex;   // editor splat paint: the live 8-layer weight textures (splat0=layers 0-3, splat1=4-7)

        // Paint the splat map: set every texel in a world-radius brush to a single dominant layer, then re-upload the
        // splat textures (the shader is winner-take-all, so one layer at 1.0 = that material shows). Also updates _dom
        // (gameplay's SampleDominantLayer). Layer 0 Dirt / 1 Wheat / 2 Grass / 3 Gravel / 4 Road / 5 Sand / 6 Snow / 7 Stone.
        public void PaintSplat(float worldX, float worldZ, float radiusWorld, int layer)
        {
            if (_dom == null || _s0Img == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            int cgx = Mathf.RoundToInt(cx), cgy = Mathf.RoundToInt(cy);
            var c0 = new Color(layer == 0 ? 1 : 0, layer == 1 ? 1 : 0, layer == 2 ? 1 : 0, layer == 3 ? 1 : 0);
            var c1 = new Color(layer == 4 ? 1 : 0, layer == 5 ? 1 : 0, layer == 6 ? 1 : 0, layer == 7 ? 1 : 0);
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_dw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_dh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT;
                    if (Mathf.Sqrt(dx * dx + dy * dy) > radiusWorld) continue;
                    _dom[gx, gy] = (byte)layer;
                    _s0Img.SetPixel(gx, gy, c0); _s1Img.SetPixel(gx, gy, c1);
                }
            _s0Tex.Update(_s0Img); _s1Tex.Update(_s1Img);
        }

        // --- live heightmap sculpt (map editor Terrain tab) ---
        // Raise/lower _grid samples inside a world-radius brush (radial falloff), then rebuild the mesh + collider.
        public void EditHeight(float worldX, float worldZ, float radiusWorld, float deltaWorldY)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;   // brush centre in grid space (world Z negated, matching SampleHeight)
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            int cgx = Mathf.RoundToInt(cx), cgy = Mathf.RoundToInt(cy);
            float dNorm = deltaWorldY / TILE_HEIGHT;   // world Y delta -> normalized grid delta
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_gw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_gh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float falloff = BrushAlpha(dist / radiusWorld);   // source linear falloff (getBrushAlpha)
                    _grid[gx, gy] = Mathf.Clamp(_grid[gx, gy] + dNorm * falloff, 0f, 1f);
                }
            _dirty = true;
            RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        bool _dirty;
        public bool Dirty => _dirty;

        public void SaveHeightmap(string path)   // the edited merged grid (port translator; writing the retail .heightmap tiles would clobber the install)
        {
            if (_grid == null) return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write(_gw); w.Write(_gh);
            for (int x = 0; x < _gw; x++) for (int y = 0; y < _gh; y++) w.Write(_grid[x, y]);
        }

        public bool LoadHeightmap(string path)   // apply a saved sculpt over the freshly-built retail terrain (dims must match)
        {
            if (_grid == null || !System.IO.File.Exists(path)) return false;
            using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            if (r.ReadInt32() != _gw || r.ReadInt32() != _gh) return false;
            for (int x = 0; x < _gw; x++) for (int y = 0; y < _gh; y++) _grid[x, y] = r.ReadSingle();
            RebuildAll();
            return true;
        }

        public void EditFlatten(float worldX, float worldZ, float radiusWorld, float strength)   // pull heights toward the brush centre's height (Devkit FLATTEN)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int cgx = Mathf.Clamp(Mathf.RoundToInt(cx), 0, _gw - 1), cgy = Mathf.Clamp(Mathf.RoundToInt(cy), 0, _gh - 1);
            float target = _grid[cgx, cgy];
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            for (int gx = System.Math.Max(0, cgx - rg); gx <= System.Math.Min(_gw - 1, cgx + rg); gx++)
                for (int gy = System.Math.Max(0, cgy - rg); gy <= System.Math.Min(_gh - 1, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT; float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float f = BrushAlpha(dist / radiusWorld);   // source linear falloff
                    _grid[gx, gy] = Mathf.Lerp(_grid[gx, gy], target, Mathf.Clamp(strength * f, 0f, 1f));
                }
            _dirty = true; RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        public void EditSmooth(float worldX, float worldZ, float radiusWorld, float strength)   // average each sample with its 4 neighbours (Devkit SMOOTH)
        {
            if (_grid == null) return;
            float cx = (worldX - _bx) / UNIT, cy = (-worldZ - _bz) / UNIT;
            int cgx = Mathf.Clamp(Mathf.RoundToInt(cx), 0, _gw - 1), cgy = Mathf.Clamp(Mathf.RoundToInt(cy), 0, _gh - 1);
            int rg = Mathf.CeilToInt(radiusWorld / UNIT) + 1;
            var next = new System.Collections.Generic.List<(int, int, float)>();
            for (int gx = System.Math.Max(1, cgx - rg); gx <= System.Math.Min(_gw - 2, cgx + rg); gx++)
                for (int gy = System.Math.Max(1, cgy - rg); gy <= System.Math.Min(_gh - 2, cgy + rg); gy++)
                {
                    float dx = (gx - cx) * UNIT, dy = (gy - cy) * UNIT; float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusWorld) continue;
                    float f = BrushAlpha(dist / radiusWorld);   // source linear falloff
                    float avg = (_grid[gx - 1, gy] + _grid[gx + 1, gy] + _grid[gx, gy - 1] + _grid[gx, gy + 1]) * 0.25f;
                    next.Add((gx, gy, Mathf.Lerp(_grid[gx, gy], avg, Mathf.Clamp(strength * f, 0f, 1f))));
                }
            foreach (var (gx, gy, nv) in next) _grid[gx, gy] = nv;
            _dirty = true; RebuildChunksIn(cgx - rg, cgx + rg, cgy - rg, cgy + rg);
        }

        // Devkit RAMP (source handleHeightmapWriteRamp): a linear height grade between two clicked world points, in a
        // corridor of half-width radiusWorld (falloff on the cross axis). One-shot -- begin.Y..end.Y are the target heights.
        public void EditRamp(Vector3 begin, Vector3 end, float radiusWorld)
        {
            if (_grid == null) return;
            var rampOffset = new Vector2(end.X - begin.X, end.Z - begin.Z);
            float rampMag = rampOffset.Length();
            if (rampMag < 1f) return;
            var rampDir = rampOffset / rampMag;
            var rampCross = new Vector2(-rampDir.Y, rampDir.X);
            float beginH = (begin.Y + TILE_HEIGHT / 2f) / TILE_HEIGHT, endH = (end.Y + TILE_HEIGHT / 2f) / TILE_HEIGHT;
            float minX = Mathf.Min(begin.X, end.X) - radiusWorld, maxX = Mathf.Max(begin.X, end.X) + radiusWorld;
            float minZ = Mathf.Min(begin.Z, end.Z) - radiusWorld, maxZ = Mathf.Max(begin.Z, end.Z) + radiusWorld;
            int gx0 = Mathf.Clamp(Mathf.FloorToInt((minX - _bx) / UNIT), 0, _gw - 1), gx1 = Mathf.Clamp(Mathf.CeilToInt((maxX - _bx) / UNIT), 0, _gw - 1);
            int gy0 = Mathf.Clamp(Mathf.FloorToInt((-maxZ - _bz) / UNIT), 0, _gh - 1), gy1 = Mathf.Clamp(Mathf.CeilToInt((-minZ - _bz) / UNIT), 0, _gh - 1);
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    float wx = _bx + gx * UNIT, wz = -(_bz + gy * UNIT);
                    var wo = new Vector2(wx - begin.X, wz - begin.Z);
                    float wMag = wo.Length();
                    if (wMag < 0.001f) { _grid[gx, gy] = Mathf.Clamp(Mathf.Lerp(_grid[gx, gy], beginH, 1f), 0f, 1f); continue; }
                    var wDir = wo / wMag;
                    float alongAlign = wDir.Dot(rampDir);
                    if (alongAlign < 0f) continue;                                   // behind the ramp begin
                    float alongDist = wMag * alongAlign / rampMag;
                    if (alongDist > 1f) continue;                                    // past the ramp end
                    float crossDist = Mathf.Abs(wMag * wDir.Dot(rampCross) / radiusWorld);
                    if (crossDist > 1f) continue;                                    // outside the corridor
                    float alpha = BrushAlpha(crossDist);
                    float target = Mathf.Lerp(beginH, endH, alongDist);
                    _grid[gx, gy] = Mathf.Clamp(Mathf.Lerp(_grid[gx, gy], target, alpha), 0f, 1f);
                }
            _dirty = true; RebuildChunksIn(gx0, gx1, gy0, gy1); FlushColliders();
        }

        readonly System.Collections.Generic.HashSet<(int, int)> _dirtyChunks = new();   // chunks whose collider went stale mid-stroke (flushed on mouse-up)

        // Rebuild ONE chunk's mesh (+ optional trimesh collider) from the (global) _grid. Reads neighbour cells for edge normals.
        public void RebuildChunk(int cxi, int cyi, bool withCollider = true)
        {
            if (_grid == null || _chunkMi == null || cxi < 0 || cyi < 0 || cxi >= _chunksX || cyi >= _chunksY) return;
            int x0 = cxi * CHUNK, y0 = cyi * CHUNK;
            int x1 = System.Math.Min(x0 + CHUNK, _gw - 1), y1 = System.Math.Min(y0 + CHUNK, _gh - 1);
            int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
            if (nx < 2 || ny < 2) return;
            int nv = nx * ny;
            var verts = new Vector3[nv]; var norms = new Vector3[nv]; var uvs = new Vector2[nv]; var cols = new Color[nv];
            for (int lx = 0; lx < nx; lx++)
                for (int ly = 0; ly < ny; ly++)
                {
                    int gx = x0 + lx, gy = y0 + ly; int i = lx * ny + ly;
                    verts[i] = new Vector3(_bx + gx * UNIT, _grid[gx, gy] * TILE_HEIGHT - TILE_HEIGHT / 2f, -(_bz + gy * UNIT));
                    uvs[i] = new Vector2(gx / (float)(_gw - 1), gy / (float)(_gh - 1));
                    cols[i] = _dom != null ? LayerColor(_dom[System.Math.Min(gx, _dw - 1), System.Math.Min(gy, _dh - 1)]) : new Color(0.34f, 0.42f, 0.26f);
                    float hl = _grid[System.Math.Max(0, gx - 1), gy], hr = _grid[System.Math.Min(_gw - 1, gx + 1), gy];
                    float hd = _grid[gx, System.Math.Max(0, gy - 1)], hu = _grid[gx, System.Math.Min(_gh - 1, gy + 1)];
                    norms[i] = new Vector3(-(hr - hl) * TILE_HEIGHT, 2f * UNIT, (hu - hd) * TILE_HEIGHT).Normalized();
                }
            var idx = new int[(nx - 1) * (ny - 1) * 6]; int t = 0;
            for (int lx = 0; lx < nx - 1; lx++)
                for (int ly = 0; ly < ny - 1; ly++)
                {
                    int i00 = lx * ny + ly, i10 = (lx + 1) * ny + ly, i01 = lx * ny + (ly + 1), i11 = (lx + 1) * ny + (ly + 1);
                    idx[t++] = i00; idx[t++] = i01; idx[t++] = i10; idx[t++] = i10; idx[t++] = i01; idx[t++] = i11;
                }
            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts; arr[(int)Mesh.ArrayType.Normal] = norms;
            arr[(int)Mesh.ArrayType.TexUV] = uvs; arr[(int)Mesh.ArrayType.Color] = cols; arr[(int)Mesh.ArrayType.Index] = idx;
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var mi = _chunkMi[cxi, cyi];
            if (mi == null) { mi = new MeshInstance3D { MaterialOverride = _terrMat }; _chunkMi[cxi, cyi] = mi; AddChild(mi); }
            mi.Mesh = mesh;
            if (_withCollider && withCollider)
            {
                var body = _chunkBody[cxi, cyi];
                if (body == null) { body = new StaticBody3D { CollisionLayer = 1u << 0 }; body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass); body.AddToGroup("terrain"); body.AddChild(new CollisionShape3D()); _chunkBody[cxi, cyi] = body; AddChild(body); }
                foreach (var c in body.GetChildren()) if (c is CollisionShape3D cs) cs.Shape = mesh.CreateTrimeshShape();
            }
        }

        // Rebuild every chunk overlapping a grid cell range (a brush edit) -- 1-chunk margin so shared edges/normals update.
        // withCollider=false (default for strokes): MESH ONLY (fast) + mark the chunk dirty; FlushColliders rebuilds the heavy trimesh on mouse-up.
        void RebuildChunksIn(int gx0, int gx1, int gy0, int gy1, bool withCollider = false)
        {
            int cx0 = System.Math.Max(0, gx0 / CHUNK - 1), cx1 = System.Math.Min(_chunksX - 1, gx1 / CHUNK);
            int cy0 = System.Math.Max(0, gy0 / CHUNK - 1), cy1 = System.Math.Min(_chunksY - 1, gy1 / CHUNK);
            for (int cx = cx0; cx <= cx1; cx++) for (int cy = cy0; cy <= cy1; cy++) { RebuildChunk(cx, cy, withCollider); if (!withCollider) _dirtyChunks.Add((cx, cy)); }
        }

        public void RebuildAll() { if (_chunkMi != null) for (int cx = 0; cx < _chunksX; cx++) for (int cy = 0; cy < _chunksY; cy++) RebuildChunk(cx, cy, true); }   // full build (mesh + collider)

        public void FlushColliders()   // stroke end (mouse-up): rebuild trimesh colliders only for the chunks the drag touched
        {
            if (_withCollider)
                foreach (var (cx, cy) in _dirtyChunks)
                {
                    var body = _chunkBody[cx, cy];
                    if (_chunkMi[cx, cy]?.Mesh is ArrayMesh am && body != null)
                        foreach (var c in body.GetChildren()) if (c is CollisionShape3D cs) cs.Shape = am.CreateTrimeshShape();
                }
            _dirtyChunks.Clear();
        }
        public float SampleHeight(float worldX, float worldZ)
        {
            if (_grid == null) return 0f;
            // bilinear across the 4 surrounding grid verts so callers (roads etc.) follow the SMOOTH terrain
            // instead of a nearest-neighbour stepped height -- that RoundToInt stepping WAS the road's jagged edges.
            float fx = (worldX - _bx) / UNIT;
            float fy = (-worldZ - _bz) / UNIT;   // world Z is negated
            int xi = Mathf.FloorToInt(fx), yi = Mathf.FloorToInt(fy);
            float tx = fx - xi, ty = fy - yi;
            int x0 = Mathf.Clamp(xi, 0, _gw - 1), x1 = Mathf.Clamp(xi + 1, 0, _gw - 1);
            int y0 = Mathf.Clamp(yi, 0, _gh - 1), y1 = Mathf.Clamp(yi + 1, 0, _gh - 1);
            float h0 = Mathf.Lerp(_grid[x0, y0], _grid[x1, y0], tx);
            float h1 = Mathf.Lerp(_grid[x0, y1], _grid[x1, y1], tx);
            return Mathf.Lerp(h0, h1, ty) * TILE_HEIGHT - TILE_HEIGHT / 2f;
        }
        // dominant splatmap layer at a world point (2=grass, 0/7=forest, 1=sand, 3=road, 4=rock, 5=water, 6=dirt); 255 = no splats
        public byte SampleDominantLayer(float worldX, float worldZ)
        {
            if (_dom == null) return 255;
            int gx = Mathf.Clamp(Mathf.RoundToInt((worldX - _bx) / UNIT), 0, _dw - 1);
            int gy = Mathf.Clamp(Mathf.RoundToInt((-worldZ - _bz) / UNIT), 0, _dh - 1);
            return _dom[gx, gy];
        }
        public static bool IsWater(byte layer) => layer == 5;   // splat layer 5 = ocean; every other layer is drivable land

        public static Terrain Active;   // most-recently-built terrain -> bullet impacts sample the ground material off its splatmap

        // Ocean surface world-Y (PEI seaLevel * 256 = 25.6). Set when the water plane is built; fishing casts a
        // bobber and treats it as "in water" once it drops below this. HasWater is false when UG_NOWATER skips it.
        public static float WaterSurfaceY = 25.6f;
        public static bool HasWater;
        public const float MinFishDepth = 4f;   // retail UseableFisher minimumDepth: a bobber needs >=4m of water below the surface
        // The bullet-impact surface material at a world point, from the dominant splat layer (so shooting sand kicks up sand,
        // road/rock = concrete chips, dirt = dirt, grass/forest = foliage -- instead of one flat guess for the whole island).
        public PlayerController.Surf SurfAt(float worldX, float worldZ) => SampleDominantLayer(worldX, worldZ) switch
        {
            1 => PlayerController.Surf.Sand,      // PEI_Sand
            3 => PlayerController.Surf.Concrete,  // road network
            4 => PlayerController.Surf.Concrete,  // rock / cliff
            5 => PlayerController.Surf.Sand,      // underwater seabed (sand)
            6 => PlayerController.Surf.Dirt,      // dirt
            _ => PlayerController.Surf.Grass,     // 2 grass, 0/7 forest, 255 none
        };

        // Build one landscape tile's mesh (+ optional trimesh collider) from its .heightmap file, placed at its coord.
        public static Node3D LoadTile(string heightmapPath, int coordX, int coordY, bool withCollider = true)
        {
            byte[] data = File.ReadAllBytes(heightmapPath);
            var h = new float[RES, RES];
            int i = 0;
            for (int x = 0; x < RES; x++)
                for (int y = 0; y < RES; y++)
                {
                    ushort raw = (ushort)((data[i] << 8) | data[i + 1]); i += 2;   // big-endian, source SHA1Stream.ReadByte pairs
                    h[x, y] = raw / (float)ushort.MaxValue;
                }

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int x = 0; x < RES; x++)
                for (int y = 0; y < RES; y++)
                {
                    st.SetUV(new Vector2(x / (float)(RES - 1), y / (float)(RES - 1)));
                    st.AddVertex(new Vector3(coordX * TILE_SIZE + y * UNIT, h[x, y] * TILE_HEIGHT - TILE_HEIGHT / 2f, -(coordY * TILE_SIZE + x * UNIT)));   // y-index = world X, x-index = world Z
                }
            for (int x = 0; x < RES - 1; x++)
                for (int y = 0; y < RES - 1; y++)
                {
                    int i00 = x * RES + y, i10 = (x + 1) * RES + y, i01 = x * RES + (y + 1), i11 = (x + 1) * RES + (y + 1);
                    st.AddIndex(i00); st.AddIndex(i01); st.AddIndex(i10);   // winding reversed to compensate the Z-flip
                    st.AddIndex(i10); st.AddIndex(i01); st.AddIndex(i11);
                }
            st.GenerateNormals();
            var mesh = st.Commit();

            var node = new Node3D { Name = $"Tile_{coordX}_{coordY}" };
            node.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.40f, 0.28f), Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
            if (withCollider)
            {
                var body = new StaticBody3D { CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass);   // bullet impacts on the ground kick up grass/dirt
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                node.AddChild(body);
            }
            return node;
        }

        // NEW MAP: a flat, all-grass, sculptable terrain (no heightmap file). Same chunked mesh + splat material as a
        // loaded map, so every editor tool (sculpt/ramp/paint) works on it. tiles = size in 1024-unit Landscape tiles.
        public static Terrain CreateFlat(int tilesX = 3, int tilesZ = 3, bool withCollider = true)
        {
            var terr = new Terrain { Name = "Terrain" };
            Active = terr;
            int GW = tilesX * 256 + 1, GH = tilesZ * 256 + 1, GWs = tilesX * SRES, GHs = tilesZ * SRES;
            float flat = (30f + TILE_HEIGHT / 2f) / TILE_HEIGHT;   // flat land ~Y30 (above the 25.6 sea level)
            var g = new float[GW, GH];
            for (int x = 0; x < GW; x++) for (int y = 0; y < GH; y++) g[x, y] = flat;
            var dom = new byte[GWs, GHs];
            for (int x = 0; x < GWs; x++) for (int y = 0; y < GHs; y++) dom[x, y] = 2;   // layer 2 = grass
            var sbuf0 = new byte[GWs * GHs * 4]; var sbuf1 = new byte[GWs * GHs * 4];
            for (int i = 0; i < GWs * GHs; i++) sbuf0[i * 4 + 2] = 255;   // splat0 B channel = layer 2 (grass) weight 1
            var splat0Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf0);
            var splat1Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf1);
            var s0t = ImageTexture.CreateFromImage(splat0Img); var s1t = ImageTexture.CreateFromImage(splat1Img);
            var texMat = BuildTerrainMaterial(s0t, s1t);
            terr._grid = g; terr._gw = GW; terr._gh = GH; terr._bx = 0f; terr._bz = 0f;
            terr._dom = dom; terr._dw = GWs; terr._dh = GHs;
            terr._s0Img = splat0Img; terr._s1Img = splat1Img; terr._s0Tex = s0t; terr._s1Tex = s1t;
            terr._terrMat = texMat != null ? (Material)texMat : new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
            terr._withCollider = withCollider;
            terr._chunksX = (GW - 2) / CHUNK + 1; terr._chunksY = (GH - 2) / CHUNK + 1;
            terr._chunkMi = new MeshInstance3D[terr._chunksX, terr._chunksY];
            terr._chunkBody = new StaticBody3D[terr._chunksX, terr._chunksY];
            terr.RebuildAll();
            GD.Print($"[terrain] flat NEW map {tilesX}x{tilesZ} tiles ({GW}x{GH} verts)");
            return terr;
        }

        // Load every Tile_*_Source.heightmap in a map's Landscape/Heightmaps folder into one Terrain node (the whole island).
        public static Terrain LoadMap(string heightmapsDir, bool withCollider = true)
        {
            var t = new Terrain { Name = "Terrain" };
            Active = t;
            foreach (var path in Directory.GetFiles(heightmapsDir, "Tile_*_Source.heightmap"))
            {
                // "Tile_<cx>_<cy>_Source.heightmap"
                string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int cx) && int.TryParse(parts[2], out int cy))
                    t.AddChild(LoadTile(path, cx, cy, withCollider));
            }
            return t;
        }

        // Whole-map terrain as ONE SEAMLESS mesh: stitch all tiles into a global (tw*256+1)x(th*256+1) height grid so
        // adjacent tiles SHARE their edge vertices (no per-tile-mesh seams), then one ArrayMesh via bulk arrays (fast --
        // SurfaceTool per-vertex would be far too slow at ~1M verts) with heightfield-gradient normals.
        public static Terrain LoadMapMerged(string heightmapsDir, bool withCollider = true)
        {
            if (!Directory.Exists(heightmapsDir))   // real map terrain is read live from a local Unturned install (not shipped in-repo)
            {
                GD.PrintErr($"[map] Unturned map terrain not found at '{heightmapsDir}'. Install Unturned via Steam, or set the UG_UNTURNED_DIR env var to your Unturned folder if it's in a non-default location.");
                return null;
            }
            var tiles = new System.Collections.Generic.Dictionary<(int, int), float[,]>();
            var splats = new System.Collections.Generic.Dictionary<(int, int), byte[,]>();   // dominant splatmap layer per 256x256 texel
            var splatRaw = new System.Collections.Generic.Dictionary<(int, int), byte[]>();   // raw 256x256x8 layer weights per tile, for the blend shader
            string splatDir = Path.Combine(Path.GetDirectoryName(heightmapsDir), "Splatmaps");
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var path in Directory.GetFiles(heightmapsDir, "Tile_*_Source.heightmap"))
            {
                string[] p = Path.GetFileNameWithoutExtension(path).Split('_');
                if (p.Length < 3 || !int.TryParse(p[1], out int cx) || !int.TryParse(p[2], out int cy)) continue;
                byte[] d = File.ReadAllBytes(path);
                var hh = new float[RES, RES]; int k = 0;
                for (int x = 0; x < RES; x++) for (int y = 0; y < RES; y++) { hh[x, y] = (ushort)((d[k] << 8) | d[k + 1]) / (float)ushort.MaxValue; k += 2; }
                tiles[(cx, cy)] = hh;
                minX = System.Math.Min(minX, cx); minY = System.Math.Min(minY, cy); maxX = System.Math.Max(maxX, cx); maxY = System.Math.Max(maxY, cy);
                // matching splatmap -> the winning (dominant) layer per texel (256x256x8 bytes, weight = raw/255; source readSplatmap)
                string sp = Path.Combine(splatDir, $"Tile_{cx}_{cy}_Source.splatmap");
                if (File.Exists(sp))
                {
                    byte[] sd = File.ReadAllBytes(sp); var dm = new byte[SRES, SRES]; int sk = 0;
                    for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++) { byte bl = 0, bv = 0; for (byte L = 0; L < SLAYERS; L++) { byte w = sd[sk++]; if (w > bv) { bv = w; bl = L; } } dm[sx, sy] = bl; }
                    splats[(cx, cy)] = dm; splatRaw[(cx, cy)] = sd;
                }
            }
            var terr = new Terrain { Name = "Terrain" };
            Active = terr;
            if (tiles.Count == 0) return terr;

            int GW = (maxX - minX + 1) * 256 + 1, GH = (maxY - minY + 1) * 256 + 1;
            var g = new float[GW, GH];
            foreach (var kv in tiles)
            {
                int ox = (kv.Key.Item1 - minX) * 256, oy = (kv.Key.Item2 - minY) * 256;
                for (int x = 0; x < RES; x++) for (int y = 0; y < RES; y++) g[ox + y, oy + x] = kv.Value[x, y];   // heightmap y-index = world X, x-index = world Z (verified: adjacent tiles' edges only match swapped) -> shared edges coincide, seamless
            }

            int GWs = (maxX - minX + 1) * SRES, GHs = (maxY - minY + 1) * SRES;   // global splatmap grid (256/tile, no shared edge)
            var dom = new byte[GWs, GHs];
            foreach (var kv in splats)
            {
                int ox = (kv.Key.Item1 - minX) * SRES, oy = (kv.Key.Item2 - minY) * SRES;
                for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++) dom[ox + sy, oy + sx] = kv.Value[sx, sy];   // same y->worldX, x->worldZ transpose
            }

            // bake the 8 raw layer weights into 2 RGBA8 textures (splat0 = layers 0-3, splat1 = 4-7) for the blend shader
            byte[] sbuf0 = new byte[GWs * GHs * 4], sbuf1 = new byte[GWs * GHs * 4];
            foreach (var kv in splatRaw)
            {
                int ox = (kv.Key.Item1 - minX) * SRES, oy = (kv.Key.Item2 - minY) * SRES; byte[] sd = kv.Value;
                for (int sx = 0; sx < SRES; sx++) for (int sy = 0; sy < SRES; sy++)
                {
                    int di = ((oy + sx) * GWs + (ox + sy)) * 4, b = (sx * SRES + sy) * SLAYERS;   // merged pos, same y->X/x->Z transpose as dom
                    sbuf0[di] = sd[b]; sbuf0[di + 1] = sd[b + 1]; sbuf0[di + 2] = sd[b + 2]; sbuf0[di + 3] = sd[b + 3];
                    sbuf1[di] = sd[b + 4]; sbuf1[di + 1] = sd[b + 5]; sbuf1[di + 2] = sd[b + 6]; sbuf1[di + 3] = sd[b + 7];
                }
            }
            var splat0Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf0);
            var splat1Img = Image.CreateFromData(GWs, GHs, false, Image.Format.Rgba8, sbuf1);

            float baseX = minX * TILE_SIZE, baseZ = minY * TILE_SIZE;
            ImageTexture s0t = splats.Count > 0 ? ImageTexture.CreateFromImage(splat0Img) : null;
            ImageTexture s1t = splats.Count > 0 ? ImageTexture.CreateFromImage(splat1Img) : null;
            var texMat = splats.Count > 0 ? BuildTerrainMaterial(s0t, s1t) : null;   // real per-layer albedos, blended by per-texel splat weights
            GD.Print(texMat != null ? "[TERRAIN] weight-blended albedo shader ACTIVE" : "[TERRAIN] vertex-colour fallback");
            terr._grid = g; terr._gw = GW; terr._gh = GH; terr._bx = baseX; terr._bz = baseZ;   // SampleHeight (spawns) + chunk sculpt
            terr._dom = dom; terr._dw = GWs; terr._dh = GHs;   // SampleDominantLayer + chunk vertex colours
            terr._s0Img = splat0Img; terr._s1Img = splat1Img; terr._s0Tex = s0t; terr._s1Tex = s1t;   // live splat paint
            terr._terrMat = texMat != null ? (Material)texMat : new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
            terr._withCollider = withCollider;
            // CHUNKED mesh: one MeshInstance per chunk so a sculpt stroke rebuilds ONLY the touched chunks (smooth held-drag).
            terr._chunksX = (GW - 2) / CHUNK + 1; terr._chunksY = (GH - 2) / CHUNK + 1;
            terr._chunkMi = new MeshInstance3D[terr._chunksX, terr._chunksY];
            terr._chunkBody = new StaticBody3D[terr._chunksX, terr._chunksY];
            terr.RebuildAll();   // builds every chunk's mesh + collider from _grid

            // translucent ocean surface at PEI's REAL sea level (source: Environment/Lighting.dat seaLevel float @+18, v12 = 0.1)
            // UG_NOWATER=1 skips the water plane -> see a map's raw terrain/textures from above (esp. flat custom maps below sea level)
            if (System.Environment.GetEnvironmentVariable("UG_NOWATER") != "1")
            {
                float waterY = 0.1f * 256f;   // = 25.6 world-Y; Unturned water surface = seaLevel * Level.TERRAIN(256), Use_Legacy_Water path
                WaterSurfaceY = waterY; HasWater = true;   // expose the sea surface for fishing (bobber water-contact test)
                var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2((maxX - minX + 1) * TILE_SIZE + 400f, (maxY - minY + 1) * TILE_SIZE + 400f) } };
                water.Position = new Vector3(baseX + GW * UNIT * 0.5f, waterY, -(baseZ + GH * UNIT * 0.5f));
                water.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.13f, 0.29f, 0.44f, 0.74f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Roughness = 0.12f, Metallic = 0.15f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                terr.AddChild(water);
                // Bullets-only splash collider on a dedicated layer (bit9): the bullet raycast checks it, but player/
                // vehicles don't mask bit9 so it never blocks movement/swimming. Shooting the ocean -> Water_Static splash.
                var wbody = new StaticBody3D { CollisionLayer = 1u << 9, Position = water.Position };
                wbody.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Water);
                var wsize = ((PlaneMesh)water.Mesh).Size;
                wbody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(wsize.X, 0.2f, wsize.Y) } });
                terr.AddChild(wbody);
            }
            return terr;   // (grid/dom/material/chunks all stored above, before RebuildAll)
        }
    }
}
