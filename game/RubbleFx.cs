using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // The ACTUAL retail Rubble_Effect particle systems, extracted from core.masterbundle by
    // tools/extract_rubble_effects.py into content/effects/rubble_fx.json + rubble/<id>.png. Each destructible
    // prop's `Rubble_Effect <id>` names one of these (Metal_5, Glass_0, Wheat_0, Water_0's 128-droplet splash, ...);
    // DestructibleField.PlayBreakEffect plays the prop's authored effect -- the real sprite (a horizontal flipbook),
    // burst count, cone/box shape, start speed/size/lifetime, gravity + tumble -- reproduced on a Godot CpuParticles3D.
    // Loaded + cached once on first break. A prop with no effect id (0) falls back to a generic material-tint debris puff.
    public static class RubbleFx
    {
        public sealed class FxDef
        {
            public int Count; public string Shape; public float ConeAngle, Radius, Gravity;
            public float LifeMin, LifeMax, SpeedMin, SpeedMax, SizeMin, SizeMax;
            public bool Tumble, Shrink; public int HFrames; public ImageTexture Tex;
        }

        static Dictionary<int, FxDef> _byId;

        public static bool TryGet(int id, out FxDef fx)
        {
            EnsureLoaded();
            return _byId.TryGetValue(id, out fx);
        }

        static void EnsureLoaded()
        {
            if (_byId != null) return;
            _byId = new Dictionary<int, FxDef>();
            string path = ProjectSettings.GlobalizePath("res://content/effects/rubble_fx.json");
            if (!File.Exists(path)) { GD.Print("[rubblefx] no rubble_fx.json -- generic break VFX"); return; }
            var parsed = Json.ParseString(File.ReadAllText(path));
            if (parsed.VariantType != Variant.Type.Dictionary) return;
            var dict = parsed.AsGodotDictionary();
            string texDir = ProjectSettings.GlobalizePath("res://content/effects/rubble/");
            foreach (var key in dict.Keys)
            {
                if (!int.TryParse(key.AsString(), out int id)) continue;
                var d = dict[key].AsGodotDictionary();
                var life = Pair(d, "life", 1f, 1f); var speed = Pair(d, "speed", 5f, 8f); var size = Pair(d, "size", 0.5f, 0.75f);
                var fx = new FxDef
                {
                    Count = Num(d, "count", 8), Shape = d.ContainsKey("shape") ? d["shape"].AsString() : "cone",
                    ConeAngle = Numf(d, "cone_angle", 45f), Radius = Numf(d, "radius", 1f), Gravity = Numf(d, "gravity", 1f),
                    LifeMin = life[0], LifeMax = life[1], SpeedMin = speed[0], SpeedMax = speed[1], SizeMin = size[0], SizeMax = size[1],
                    Tumble = d.ContainsKey("tumble") && d["tumble"].AsBool(), Shrink = d.ContainsKey("shrink") && d["shrink"].AsBool(),
                    HFrames = Num(d, "hframes", 1),
                };
                if (d.ContainsKey("tex"))
                {
                    string tp = texDir + d["tex"].AsString();
                    if (File.Exists(tp)) { var img = Image.LoadFromFile(tp); if (img != null) { img.GenerateMipmaps(); fx.Tex = ImageTexture.CreateFromImage(img); } }
                }
                _byId[id] = fx;
            }
            GD.Print($"[rubblefx] loaded {_byId.Count} retail break effects");
        }

        static int Num(Godot.Collections.Dictionary d, string k, int def) => d.ContainsKey(k) ? (int)d[k].AsDouble() : def;
        static float Numf(Godot.Collections.Dictionary d, string k, float def) => d.ContainsKey(k) ? (float)d[k].AsDouble() : def;
        static float[] Pair(Godot.Collections.Dictionary d, string k, float a, float b)
        {
            if (d.ContainsKey(k)) { var arr = d[k].AsGodotArray(); if (arr.Count >= 2) return new[] { (float)arr[0].AsDouble(), (float)arr[1].AsDouble() }; }
            return new[] { a, b };
        }
    }
}
