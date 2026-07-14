using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Maps a consumable item id -> its held mesh name (content/<mesh>.txt), from content/consumables.tsv
    // (tools/consumables_ids.py). Used to equip an inventory consumable to the hands. Lazy-loaded.
    public static class ConsumableRegistry
    {
        static readonly Dictionary<ushort, string> _byId = new();
        // mesh -> its own eat/drink archetype clips + source useTime (Use-clip length). From content/consumable_anims.tsv.
        public readonly struct AnimSet { public readonly string Equip, Use; public readonly float UseLen; public AnimSet(string e, string u, float l) { Equip = e; Use = u; UseLen = l; } }
        static readonly Dictionary<string, AnimSet> _animsByMesh = new();
        static readonly Dictionary<ushort, string> _soundById = new();   // id -> use-sound file stem (content/sounds/<x>.wav)
        static bool _loaded;

        public static void Load()
        {
            _byId.Clear(); _animsByMesh.Clear();
            _loaded = true;
            string p = ProjectSettings.GlobalizePath("res://content/consumables.tsv");
            if (System.IO.File.Exists(p))
                foreach (var ln in System.IO.File.ReadAllLines(p))
                {
                    var c = ln.Split('\t');
                    if (c.Length >= 2 && ushort.TryParse(c[0], out var id)) _byId[id] = c[1].Trim();
                }
            else GD.Print("[consumables] no consumables.tsv");
            string ap = ProjectSettings.GlobalizePath("res://content/consumable_anims.tsv");
            if (System.IO.File.Exists(ap))
                foreach (var ln in System.IO.File.ReadAllLines(ap))
                {
                    var c = ln.Split('\t');   // mesh, equipClip, useClip, useLen
                    if (c.Length >= 4) { float.TryParse(c[3], out var ul); _animsByMesh[c[0].Trim()] = new AnimSet(c[1].Trim(), c[2].Trim(), ul); }
                }
            string sp = ProjectSettings.GlobalizePath("res://content/consumable_sounds.tsv");
            if (System.IO.File.Exists(sp))
                foreach (var ln in System.IO.File.ReadAllLines(sp))
                {
                    var c = ln.Split('\t');   // id, soundStem
                    if (c.Length >= 2 && ushort.TryParse(c[0], out var id)) _soundById[id] = c[1].Trim();
                }
            GD.Print($"[consumables] loaded {_byId.Count} meshes, {_animsByMesh.Count} anim sets, {_soundById.Count} sounds");
        }

        // this item's use/eat/drink sound stem (content/sounds/<x>.wav), or null (source ItemConsumeableAsset.use)
        public static string Sound(ushort id)
        {
            if (!_loaded) Load();
            return _soundById.TryGetValue(id, out var s) ? s : null;
        }

        public static string Mesh(ushort id)
        {
            if (!_loaded) Load();
            return _byId.TryGetValue(id, out var m) ? m : null;
        }
        public static bool Has(ushort id) => Mesh(id) != null;
        // reverse lookup (render harness UG_HOLD/UG_EAT): first id whose held mesh matches this name
        public static ushort IdForMesh(string mesh)
        {
            if (!_loaded) Load();
            foreach (var kv in _byId) if (kv.Value == mesh) return kv.Key;
            return 0;
        }

        // this mesh's own Equip/Use clips + useTime; default (generic + 2.2s) when the mesh has no mapped set.
        public static AnimSet Anims(string mesh)
        {
            if (!_loaded) Load();
            return mesh != null && _animsByMesh.TryGetValue(mesh, out var a) ? a : new AnimSet("", "", 2.2f);
        }
    }
}
