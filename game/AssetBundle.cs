using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace UnturnedGodot
{
    // A composed, hand-authored game asset produced by the Asset Factory: stacked mesh
    // Parts + hand-placed Colliders / Volumes / named Points, plus a freeform Params bag.
    // One loader (AssetBundleLoader) turns it into the right in-game node tree per Type.
    // Everything is authored in the PORT's own coordinate space (rotations = Euler degrees),
    // so the file is WYSIWYG — no z-flip / hook-math guessing. Stored as content/assets/<name>.assetbundle (JSON).
    public class AssetBundle
    {
        public string Name { get; set; } = "asset";
        public string Type { get; set; } = "prop";      // prop | deployable | vehicle | gun
        public List<Part> Parts { get; set; } = new();
        public List<Collider> Colliders { get; set; } = new();
        public List<Volume> Volumes { get; set; } = new();
        public List<Point> Points { get; set; } = new();
        public Dictionary<string, JsonElement> Params { get; set; } = new();   // type-specific, freeform

        public class Part
        {
            public string Mesh { get; set; }             // content/<mesh>.txt (ContentProvider.ParseObj)
            public string Albedo { get; set; }           // content/<albedo>.png (optional)
            public float[] Color { get; set; }           // flat rgb(a) fallback when no albedo (optional)
            public float[] Pos { get; set; } = { 0, 0, 0 };
            public float[] Rot { get; set; } = { 0, 0, 0 };   // Euler degrees
            public float[] Scale { get; set; } = { 1, 1, 1 };
        }

        public class Collider
        {
            public string Shape { get; set; } = "box";   // box | sphere | capsule | convex
            public float[] Pos { get; set; } = { 0, 0, 0 };
            public float[] Rot { get; set; } = { 0, 0, 0 };
            // box: full extents (x,y,z); sphere: [radius]; capsule: [radius,height]; convex: [partIndex]
            public float[] Size { get; set; } = { 1, 1, 1 };
        }

        public class Volume   // named trigger box -> Area3D
        {
            public string Name { get; set; } = "volume";
            public float[] Pos { get; set; } = { 0, 0, 0 };
            public float[] Rot { get; set; } = { 0, 0, 0 };
            public float[] Size { get; set; } = { 1, 1, 1 };   // full extents
        }

        public class Point    // named empty transform -> the hooks the game queries by name
        {
            public string Name { get; set; } = "point";
            public float[] Pos { get; set; } = { 0, 0, 0 };
            public float[] Rot { get; set; } = { 0, 0, 0 };   // Euler degrees
        }

        static readonly JsonSerializerOptions Opts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        // --- IO ---------------------------------------------------------------
        // Load handles res:// | user:// (Godot.FileAccess) so bundles work in exported builds.
        public static AssetBundle Load(string path)
        {
            string text;
            using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
            {
                if (f == null) { GD.PushError($"[AssetBundle] cannot open {path}"); return null; }
                text = f.GetAsText();
            }
            try { return JsonSerializer.Deserialize<AssetBundle>(text, Opts); }
            catch (System.Exception e) { GD.PushError($"[AssetBundle] parse {path}: {e.Message}"); return null; }
        }

        // Save writes the real file (GlobalizePath) so the editor can author in dev.
        public void Save(string path)
        {
            string text = JsonSerializer.Serialize(this, Opts);
            string real = ProjectSettings.GlobalizePath(path);
            System.IO.File.WriteAllText(real, text);
            GD.Print($"[AssetBundle] saved {path} ({Parts.Count}p/{Colliders.Count}c/{Volumes.Count}v/{Points.Count}pt)");
        }

        // --- accessors --------------------------------------------------------
        public static Vector3 V3(float[] a, Vector3 def = default)
            => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : def;

        // Euler-degrees -> Basis (Godot Y-up, matches how the editor authors rotations).
        public static Basis EulerDegBasis(float[] rot)
            => rot == null || rot.Length < 3 ? Basis.Identity
             : Basis.FromEuler(new Vector3(Mathf.DegToRad(rot[0]), Mathf.DegToRad(rot[1]), Mathf.DegToRad(rot[2])));

        public Point FindPoint(string name) => Points.Find(p => p.Name == name);

        // Best-matching albedo texture for a mesh file so composed parts render TEXTURED by default.
        // Content naming varies: "adrenaline.txt"->"adrenaline_albedo.png"; "ace_gun.txt"->"ace_albedo.png";
        // "jeep_body.txt"->"jeep_palette.png". Returns the content-relative png name, or null (flat fallback).
        public static string ResolveAlbedo(string meshFile)
        {
            if (string.IsNullOrEmpty(meshFile)) return null;
            string b = (meshFile.EndsWith(".txt") || meshFile.EndsWith(".obj")) ? meshFile[..^4] : meshFile;   // strip .txt / .obj (objects/*.obj deployable meshes)
            string stripped = b;
            foreach (var suf in new[] { "_gun", "_body", "_0", "_1" }) if (b.EndsWith(suf)) { stripped = b[..^suf.Length]; break; }
            foreach (var cand in new[] { b + "_albedo.png", b + ".png", b + "_tex.png", stripped + "_albedo.png", stripped + "_palette.png", b + "_palette.png", stripped + "_tex.png" })   // _tex.png = the objects/ palette naming (Gas_Pump_0_tex.png)
                if (System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/" + cand))) return cand;
            return null;
        }

        public float ParamFloat(string key, float def = 0f)
            => Params != null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : def;
        public string ParamString(string key, string def = null)
            => Params != null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : def;
        public bool ParamBool(string key, bool def = false)
            => Params != null && Params.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;

        // editor-side setters (the behaviours panel writes params); passing an empty string removes the key
        public void SetParam(string key, float v) { Params ??= new(); Params[key] = JsonSerializer.SerializeToElement(v); }
        public void SetParam(string key, bool v) { Params ??= new(); Params[key] = JsonSerializer.SerializeToElement(v); }
        public void SetParam(string key, string v) { Params ??= new(); if (string.IsNullOrEmpty(v)) Params.Remove(key); else Params[key] = JsonSerializer.SerializeToElement(v); }
    }
}
