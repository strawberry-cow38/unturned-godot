using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Maps a consumable item id -> its held mesh name (content/<mesh>.txt), from content/consumables.tsv
    // (tools/consumables_ids.py). Used to equip an inventory consumable to the hands. Lazy-loaded.
    public static class ConsumableRegistry
    {
        static readonly Dictionary<ushort, string> _byId = new();
        static bool _loaded;

        public static void Load()
        {
            _byId.Clear();
            _loaded = true;
            string p = ProjectSettings.GlobalizePath("res://content/consumables.tsv");
            if (!System.IO.File.Exists(p)) { GD.Print("[consumables] no consumables.tsv"); return; }
            foreach (var ln in System.IO.File.ReadAllLines(p))
            {
                var c = ln.Split('\t');
                if (c.Length >= 2 && ushort.TryParse(c[0], out var id)) _byId[id] = c[1].Trim();
            }
            GD.Print($"[consumables] loaded {_byId.Count} consumable meshes");
        }

        public static string Mesh(ushort id)
        {
            if (!_loaded) Load();
            return _byId.TryGetValue(id, out var m) ? m : null;
        }
        public static bool Has(ushort id) => Mesh(id) != null;
    }
}
