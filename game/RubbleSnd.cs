using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // The ACTUAL retail break SOUND for each destructible prop's Rubble_Effect, extracted from core.masterbundle by
    // tools/extract_rubble_sfx.py into content/effects/rubble_snd/<id>.wav + rubble_snd.json. Same effect-id key as
    // RubbleFx (the break VFX): a prop's `Rubble_Effect <id>` names BOTH its debris sprite and its break sound, and
    // DestructibleField.PlayBreakEffect fires them together at the break point. The sounds are MATERIAL-keyed, not
    // per-prop -- retail reuses one clip across a material (all wood props share Wood, all presents share Present,
    // Metal_0/1/3/4/5 + computer share Metal, ...), so ~17 unique clips cover the 35 effect ids, exactly as authored.
    // Loaded + cached once on first break. A prop whose effect has no extracted sound (or effect id 0) breaks silent.
    public static class RubbleSnd
    {
        static Dictionary<int, AudioStream> _byId;

        public static bool TryGet(int id, out AudioStream stream)
        {
            EnsureLoaded();
            return _byId.TryGetValue(id, out stream) && stream != null;
        }

        /// <summary>Front-load the JSON + all break WAVs during the loading screen so the first break doesn't (master).</summary>
        public static void Warm() => EnsureLoaded();

        /// <summary>Drop the cache (AudioStreams) for ResourceCaches.ClearAll; re-warmed by Warmup.Begin on entry.</summary>
        public static void Clear() => _byId = null;

        static void EnsureLoaded()
        {
            if (_byId != null) return;
            _byId = new Dictionary<int, AudioStream>();
            string path = ProjectSettings.GlobalizePath("res://content/effects/rubble_snd.json");
            if (!File.Exists(path)) { GD.Print("[rubblesnd] no rubble_snd.json -- silent breaks"); return; }
            var parsed = Json.ParseString(File.ReadAllText(path));
            if (parsed.VariantType != Variant.Type.Dictionary) return;
            var dict = parsed.AsGodotDictionary();
            string sndDir = ProjectSettings.GlobalizePath("res://content/effects/rubble_snd/");
            foreach (var key in dict.Keys)
            {
                if (!int.TryParse(key.AsString(), out int id)) continue;
                var v = dict[key];
                if (v.VariantType != Variant.Type.Dictionary) continue;   // null entry = VFX-only effect, no sound
                var d = v.AsGodotDictionary();
                if (!d.ContainsKey("snd")) continue;
                var sv = d["snd"];
                if (sv.VariantType != Variant.Type.String) continue;      // "snd": null (external/undecodable clip)
                string sp = sndDir + sv.AsString();
                if (!File.Exists(sp)) continue;
                // Runtime WAV ingest (Godot 4.4+): these are loose content files, never editor-imported, so load the
                // 16-bit PCM straight into an AudioStreamWav. Empty options dict = defaults (no loop) -> one-shot break.
                var wav = AudioStreamWav.LoadFromFile(sp, new Godot.Collections.Dictionary());
                if (wav != null) _byId[id] = wav;
            }
            GD.Print($"[rubblesnd] loaded {_byId.Count} retail break sounds");
        }
    }
}
