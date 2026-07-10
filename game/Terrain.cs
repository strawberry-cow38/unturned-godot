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

        // Merged-map height grid + placement, stashed so gameplay can sample the ground height at a world XZ (spawns etc.).
        float[,] _grid; int _gw, _gh; float _bx, _bz;
        byte[,] _dom; int _dw, _dh;   // dominant splatmap layer per texel -> SampleDominantLayer (grassy-spawn picking)
        public float SampleHeight(float worldX, float worldZ)
        {
            if (_grid == null) return 0f;
            int gx = Mathf.Clamp(Mathf.RoundToInt((worldX - _bx) / UNIT), 0, _gw - 1);
            int gy = Mathf.Clamp(Mathf.RoundToInt((-worldZ - _bz) / UNIT), 0, _gh - 1);   // world Z is negated
            return _grid[gx, gy] * TILE_HEIGHT - TILE_HEIGHT / 2f;
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
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                node.AddChild(body);
            }
            return node;
        }

        // Load every Tile_*_Source.heightmap in a map's Landscape/Heightmaps folder into one Terrain node (the whole island).
        public static Terrain LoadMap(string heightmapsDir, bool withCollider = true)
        {
            var t = new Terrain { Name = "Terrain" };
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
            var tiles = new System.Collections.Generic.Dictionary<(int, int), float[,]>();
            var splats = new System.Collections.Generic.Dictionary<(int, int), byte[,]>();   // dominant splatmap layer per 256x256 texel
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
                    splats[(cx, cy)] = dm;
                }
            }
            var terr = new Terrain { Name = "Terrain" };
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

            int nv = GW * GH;
            var verts = new Vector3[nv]; var norms = new Vector3[nv]; var uvs = new Vector2[nv]; var cols = new Color[nv];
            float baseX = minX * TILE_SIZE, baseZ = minY * TILE_SIZE;
            for (int x = 0; x < GW; x++)
                for (int y = 0; y < GH; y++)
                {
                    int i = x * GH + y;
                    float wy = g[x, y] * TILE_HEIGHT - TILE_HEIGHT / 2f;
                    verts[i] = new Vector3(baseX + x * UNIT, wy, -(baseZ + y * UNIT));
                    uvs[i] = new Vector2(x / (float)(GW - 1), y / (float)(GH - 1));
                    cols[i] = splats.Count > 0 ? LayerColor(dom[System.Math.Min(x, GWs - 1), System.Math.Min(y, GHs - 1)])   // real splatmap material layout (grass/dirt/sand/forest)
                                               : (wy < 0f ? new Color(0.20f, 0.36f, 0.55f) : (wy < 30f ? new Color(0.74f, 0.68f, 0.48f) : new Color(0.34f, 0.42f, 0.26f)));   // height fallback
                    float hl = g[System.Math.Max(0, x - 1), y], hr = g[System.Math.Min(GW - 1, x + 1), y];
                    float hd = g[x, System.Math.Max(0, y - 1)], hu = g[x, System.Math.Min(GH - 1, y + 1)];
                    norms[i] = new Vector3(-(hr - hl) * TILE_HEIGHT, 2f * UNIT, (hu - hd) * TILE_HEIGHT).Normalized();
                }
            var idx = new int[(GW - 1) * (GH - 1) * 6]; int t = 0;
            for (int x = 0; x < GW - 1; x++)
                for (int y = 0; y < GH - 1; y++)
                {
                    int i00 = x * GH + y, i10 = (x + 1) * GH + y, i01 = x * GH + (y + 1), i11 = (x + 1) * GH + (y + 1);
                    idx[t++] = i00; idx[t++] = i01; idx[t++] = i10;
                    idx[t++] = i10; idx[t++] = i01; idx[t++] = i11;
                }

            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts; arr[(int)Mesh.ArrayType.Normal] = norms;
            arr[(int)Mesh.ArrayType.TexUV] = uvs; arr[(int)Mesh.ArrayType.Color] = cols; arr[(int)Mesh.ArrayType.Index] = idx;
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);

            terr.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f } });
            if (withCollider)
            {
                var body = new StaticBody3D { CollisionLayer = 1u << 0 };
                body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
                terr.AddChild(body);
            }
            terr._grid = g; terr._gw = GW; terr._gh = GH; terr._bx = baseX; terr._bz = baseZ;   // for SampleHeight (spawns)
            terr._dom = dom; terr._dw = GWs; terr._dh = GHs;   // for SampleDominantLayer (grassy-spawn picking)
            return terr;
        }
    }
}
