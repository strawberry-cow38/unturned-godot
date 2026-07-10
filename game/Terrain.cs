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
        const float TILE_SIZE = 1024f, TILE_HEIGHT = 2048f, UNIT = 4f;

        // Build one landscape tile's mesh + trimesh collider from its .heightmap file, placed at its coord.
        public static Node3D LoadTile(string heightmapPath, int coordX, int coordY)
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
                    st.AddVertex(new Vector3(coordX * TILE_SIZE + x * UNIT, h[x, y] * TILE_HEIGHT - TILE_HEIGHT / 2f, -(coordY * TILE_SIZE + y * UNIT)));
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
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
            node.AddChild(body);
            return node;
        }

        // Load every Tile_*_Source.heightmap in a map's Landscape/Heightmaps folder into one Terrain node (the whole island).
        public static Terrain LoadMap(string heightmapsDir)
        {
            var t = new Terrain { Name = "Terrain" };
            foreach (var path in Directory.GetFiles(heightmapsDir, "Tile_*_Source.heightmap"))
            {
                // "Tile_<cx>_<cy>_Source.heightmap"
                string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int cx) && int.TryParse(parts[2], out int cy))
                    t.AddChild(LoadTile(path, cx, cy));
            }
            return t;
        }
    }
}
