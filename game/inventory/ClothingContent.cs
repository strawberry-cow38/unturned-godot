using Godot;
using System.Collections.Generic;

// Clothing content loader (P3a). Reads game/content/clothing_content.tsv -- the id->content manifest emitted by
// tools/extract_clothing_tex.py (P2) -- and resolves an item id to its ripped shirt/pants textures, mirroring how
// the gun arsenal loads content/guns_visual.tsv (Viewmodel.LoadExtraVisuals) and how ImageTexture is built from a
// runtime PNG (Viewmodel.LoadTex). P4's equip wiring calls Get()/LoadTextures(id) and hands the result to
// RiggedCharacter.SetShirt/SetPants.
//
// TSV columns (tab-separated, one header row): id  slot  guid  albedo  emission  metallic  mesh
// The albedo/emission/metallic/mesh cells are paths RELATIVE to res://content (e.g. "clothing/hoodie_orange_shirt.png").
namespace UnturnedGodot
{
    public static class ClothingContent
    {
        public class Entry
        {
            public int Id;
            public string Slot;      // "shirt" | "pants" | "hat" | "vest" | ...
            public string Guid;
            public string Albedo;    // res://content-relative PNG path, or "" if none
            public string Emission;
            public string Metallic;
            public string Mesh;      // gear (.obj) path for bone-attached slots; unused by P3a shirt/pants
        }

        static Dictionary<int, Entry> _byId;

        static Dictionary<int, Entry> Load()
        {
            var d = new Dictionary<int, Entry>();
            string path = ProjectSettings.GlobalizePath("res://content/clothing_content.tsv");
            if (!System.IO.File.Exists(path)) return d;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var c = line.Split('\t');
                if (c.Length < 3 || !int.TryParse(c[0], out int id)) continue;   // skips the header row (col0 = "id")
                d[id] = new Entry
                {
                    Id = id,
                    Slot = c[1],
                    Guid = c[2],
                    Albedo = c.Length > 3 ? c[3] : "",
                    Emission = c.Length > 4 ? c[4] : "",
                    Metallic = c.Length > 5 ? c[5] : "",
                    Mesh = c.Length > 6 ? c[6] : "",
                };
            }
            return d;
        }

        public static Entry Get(int id)
        {
            _byId ??= Load();
            return _byId.TryGetValue(id, out var e) ? e : null;
        }

        // Load a res://content-relative PNG as a runtime ImageTexture (no mipmaps -> the clothes shader samples
        // filter_nearest for blocky Unturned pixels). Blank cell or missing file -> null (reads as transparent on-body).
        public static Texture2D LoadTex(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            string p = ProjectSettings.GlobalizePath("res://content/" + rel);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        public struct Loaded { public Texture2D Albedo, Emission, Metallic; }

        // Resolve an item id -> its (albedo, emission, metallic) textures. Missing id or blank cells -> nulls.
        public static Loaded LoadTextures(int id)
        {
            var e = Get(id);
            if (e == null) return default;
            return new Loaded { Albedo = LoadTex(e.Albedo), Emission = LoadTex(e.Emission), Metallic = LoadTex(e.Metallic) };
        }
    }
}
