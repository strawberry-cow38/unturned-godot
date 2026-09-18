using Godot;
using System.Collections.Generic;

// Clothing content loader (P3a). Reads game/content/clothing_content.tsv -- the id->content manifest emitted by
// tools/extract_clothing_tex.py (P2) -- and resolves an item id to its ripped shirt/pants textures, mirroring how
// the gun arsenal loads content/guns_visual.tsv (Viewmodel.LoadExtraVisuals) and how ImageTexture is built from a
// runtime PNG (Viewmodel.LoadTex). P4's equip wiring calls Get()/LoadTextures(id) and hands the result to
// RiggedCharacter.SetShirt/SetPants.
//
// TSV columns (tab-separated, one header row): id  slot  guid  albedo  emission  metallic  mesh  attach_off
// The albedo/emission/metallic/mesh cells are paths RELATIVE to res://content (e.g. "clothing/hoodie_orange_shirt.png").
// attach_off = "x,y,z" bone-LOCAL offset (Godot space) at which a gear mesh rides its bone (P3b/P4); blank for shirt/pants.
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
            public Vector3 Offset;   // gear bone-local attach offset (from the ripped prefab's Model_0 local pos); (0,0,0) if none
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
                    Offset = c.Length > 7 ? ParseOffset(c[7]) : Vector3.Zero,
                };
            }
            return d;
        }

        // "x,y,z" -> Vector3 (invariant floats); blank/malformed -> zero. The gear bone-local attach offset.
        static Vector3 ParseOffset(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Vector3.Zero;
            var p = s.Split(',');
            if (p.Length != 3) return Vector3.Zero;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out var x)
                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out var y)
                && float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out var z)
                ? new Vector3(x, y, z) : Vector3.Zero;
        }

        public static Entry Get(int id)
        {
            _byId ??= Load();
            return _byId.TryGetValue(id, out var e) ? e : null;
        }

        static readonly Dictionary<string, List<int>> _slotIds = new();

        /// <summary>Every item id the manifest lists for a slot ("shirt", "pants", "hat", ...). SORTED, because
        /// a Dictionary's order is an implementation detail and anything picking from this by a seed would
        /// otherwise choose differently on another machine or another run for the same seed.</summary>
        public static IReadOnlyList<int> IdsForSlot(string slot)
        {
            _byId ??= Load();
            if (_slotIds.TryGetValue(slot, out var cached)) return cached;
            var list = new List<int>();
            foreach (var kv in _byId)
                if (string.Equals(kv.Value.Slot, slot, System.StringComparison.Ordinal)) list.Add(kv.Key);
            list.Sort();
            _slotIds[slot] = list;
            return list;
        }

        // Load a res://content-relative PNG as a runtime ImageTexture (no mipmaps -> the clothes shader samples
        // filter_nearest for blocky Unturned pixels). Blank cell or missing file -> null (reads as transparent on-body).
        // LENS GLOW (strawberry 2026-09-10: "add emissive lenses to both nightvisions the headlamp and the
        // flashlight. should only glow when they are on, in 3p too").
        //
        // ⚠ OPT-IN BY ID, not by a rule. Every one of these albedos is a 2x2 palette: three housing greys
        // (40,40,40 / 50,50,50 / 71,71,71) and one lens cell. Deriving "which cell is the lens" is easy -- it is
        // the brightest -- but deciding "which ITEMS have a lens at all" is not, and a heuristic there would set
        // some hat's brightest patch glowing. Retail ships no emission map for these three (23 other garments do
        // have one, so the pipeline supports it; these simply are not authored that way), which is why the mask
        // is derived rather than loaded.
        static readonly System.Collections.Generic.HashSet<int> GlowLensIds = new() { 334, 1044, 1199 };
        static readonly Dictionary<int, ImageTexture> _lensMask = new();

        public static bool HasGlowLens(int id) => GlowLensIds.Contains(id);

        /// <summary>How hard a lens item's lens burns (strawberry 2026-09-13: "tone down the glow on both
        /// nvgs"). Per ITEM rather than one constant at the call site, because the three are not the same kind
        /// of device and were never meant to read alike:
        ///
        ///   HEADLAMP is a LAMP. It is supposed to look like a light source pointed at the world, so it keeps
        ///   the 3.2 everything used to share.
        ///
        ///   THE NIGHTVISIONS are things you look THROUGH. Their tubes should read as powered, not as
        ///   headlights -- and they were the two that looked wrong, which is consistent with the palettes: the
        ///   military lens is a fully saturated (0,255,0) and the civilian a near-white (200,200,200), so at a
        ///   shared energy they push further past the HDR bloom threshold (0.9) than the headlamp's warmer,
        ///   channel-spread cream does. Same number, brighter result, which is why one value could not serve.
        ///
        /// 1.4 is "a bit under half", chosen as a visible step rather than derived from anything -- there is no
        /// retail figure for this (no shipped emission map at all for these three). UG_NVGLOW overrides it at
        /// runtime so it can be tuned against a picture instead of guessed at twice.</summary>
        public static float LensEnergy(int id) => id switch
        {
            334 or 1044 => NvgLensEnergy,
            _ => DefaultLensEnergy,
        };

        public const float DefaultLensEnergy = 3.2f;
        static readonly float NvgLensEnergy =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_NVGLOW"), out float e) ? e : 1.4f;

        /// <summary>An emission mask for a lens item: the albedo with everything BUT the lens cell blacked out, so
        /// one material and one draw call give "only the lens glows". Null for anything not opted in.
        ///
        /// The lens is the BRIGHTEST cell, which is the only rule that works across all three -- the military
        /// tube is pure green (0,255,0) but the civilian one is a desaturated (200,200,200) and the headlamp a
        /// warm cream. A "most saturated" rule picks the military lens and misses the other two entirely.</summary>
        public static Texture2D LensMask(int id)
        {
            if (!GlowLensIds.Contains(id)) return null;
            if (_lensMask.TryGetValue(id, out var cached)) return cached;
            var e = Get(id);
            string rel = e?.Albedo;
            if (string.IsNullOrEmpty(rel) || rel[0] == '#') { _lensMask[id] = null; return null; }
            var built = EmissionMaskFrom(rel, id.ToString());
            _lensMask[id] = built as ImageTexture;
            return built;
        }

        static readonly Dictionary<string, ImageTexture> _maskByPath = new();

        /// <summary>The same lens-mask derivation for anything that is not a garment -- the handheld flashlight is
        /// a MELEE item and has no row in this table, but its albedo has exactly the same shape: a body colour and
        /// a bulb. Keyed by path so the two callers share one cache.</summary>
        public static Texture2D EmissionMaskFrom(string resRelPath, string label = null)
        {
            if (string.IsNullOrEmpty(resRelPath)) return null;
            if (_maskByPath.TryGetValue(resRelPath, out var hit)) return hit;
            string p = ProjectSettings.GlobalizePath("res://content/" + resRelPath);
            var img = System.IO.File.Exists(p) ? ContentProvider.LoadImage(p) : null;
            if (img == null || img.IsEmpty()) { _maskByPath[resRelPath] = null; return null; }
            int w = img.GetWidth(), h = img.GetHeight();
            float best = -1f; int bx = 0, by = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = img.GetPixel(x, y);
                    float lum = c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;   // perceptual, so a green tube beats a mid grey
                    if (lum > best) { best = lum; bx = x; by = y; }
                }
            // ⚠ KEEP EVERY PIXEL OF THE LENS, not just the brightest one. These three albedos are 2x2 palettes
            // where the lens is a single texel, so "black out all but the brightest pixel" looked right -- and it
            // is wrong the moment a texture is bigger than its palette. The handheld flashlight's albedo is
            // 128x128 with a 784-pixel bulb (plus a 28-pixel near-twin one unit off, from the rip), which that
            // version would have reduced to one lit texel.
            //
            // So match by COLOUR against the brightest one, with enough tolerance to hold a rip's near-duplicates
            // together and nowhere near enough to reach the housing greys -- the gap here is (247,224,148) versus
            // (73,73,73), which is not a close call.
            var lens = img.GetPixel(bx, by);
            var mask = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            mask.Fill(new Color(0f, 0f, 0f, 1f));
            int lit = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = img.GetPixel(x, y);
                    float dr = c.R - lens.R, dg = c.G - lens.G, db = c.B - lens.B;
                    if (dr * dr + dg * dg + db * db <= 0.02f * 0.02f * 3f) { mask.SetPixel(x, y, c); lit++; }
                }
            var tex = ImageTexture.CreateFromImage(mask);
            Log.Print($"[lens] {label ?? resRelPath}: {lit} of {w * h} texels lit, from {lens}");
            _maskByPath[resRelPath] = tex;
            return tex;
        }

        static readonly Dictionary<string, Texture2D> _texByPath = new();

        /// <summary>⚠ CACHED, and it has to be. For the player this runs a handful of times on equip, so the
        /// uncached version was fine. ZombieBody now dresses every zombie at spawn, and the chunk field streams a
        /// horde that can hold ~871 live bodies -- an uncached call is a File.Exists, a PNG decode and a texture
        /// upload EACH, per zombie, per spawn. Garments repeat heavily across a horde, and an ImageTexture is a
        /// Resource that is safe to share, so one instance per path serves everybody (the same reasoning that makes
        /// RiggedCharacter share its clip library). Bounded by the manifest: 717 rows, so the cache cannot grow
        /// without limit. Null results are cached too -- a missing file is just as expensive to re-discover.</summary>
        public static Texture2D LoadTex(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (_texByPath.TryGetValue(rel, out var hit)) return hit;
            var made = LoadTexUncached(rel);
            _texByPath[rel] = made;
            return made;
        }

        static Texture2D LoadTexUncached(string rel)
        {
            // FLAT-COLOUR gear (strawberry 2026-09-04 "hat/balaclava clothing items that are meant to be flat colored on
            // their model are completely white when worn"): the retail material has no _MainTex, only a _Color, which the
            // ripper writes as "#rrggbb" in the albedo cell. A 1x1 texture of that colour is the cheapest way to feed the
            // same material path every textured garment uses.
            if (rel[0] == '#')
            {
                var flat = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
                flat.Fill(Color.FromHtml(rel));
                return ImageTexture.CreateFromImage(flat);
            }
            string p = ProjectSettings.GlobalizePath("res://content/" + rel);
            if (System.IO.File.Exists(p)) { var img = ContentProvider.LoadImage(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // Load a gear item's worn MESH (.obj) as a runtime ArrayMesh, reusing ContentProvider.ParseObj -- the exact
        // runtime .obj loader the guns/vehicles/attachments use (Viewmodel gun mesh, Vehicle body). Blank cell or
        // missing file -> null (the slot then attaches nothing). Only gear slots (hat/vest/mask/glasses/backpack) carry a mesh.
        static readonly Dictionary<string, ArrayMesh> _meshByPath = new();

        /// <summary>⚠ CACHED for the same reason LoadTex is, and more urgently: this re-PARSES an .obj on every
        /// call. Hats and vests are attached per zombie at spawn now, and a horde repeats a handful of garments.</summary>
        public static ArrayMesh LoadMesh(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (_meshByPath.TryGetValue(rel, out var hit)) return hit;
            var made = ContentProvider.ParseObj("res://content/" + rel);
            _meshByPath[rel] = made;
            return made;
        }

        public static ArrayMesh LoadMesh(int id) { var e = Get(id); return e == null ? null : LoadMesh(e.Mesh); }

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
