using Godot;
using System.Collections.Generic;

namespace SDG.Unturned
{
    // A FARM crop -- a Type=Farm seed item (source ItemFarmAsset). Planting it grows for `Growth` seconds, then
    // harvesting yields item `Grow`. Loaded from content/farms.tsv (tools/gen_farms.py -> 20 crops: carrot/corn/...).
    public struct FarmDef
    {
        public ushort Id;        // the seed item id
        public uint Growth;      // grow time in seconds (source ItemFarmAsset.growth)
        public ushort Grow;      // harvest-yield item id (source ItemFarmAsset.grow)
        public bool IgnoreSoil;  // if true, can plant anywhere (else soil/dirt only)
    }

    public static class FarmRegistry
    {
        static readonly Dictionary<ushort, FarmDef> _bySeed = new();

        public static void Load()
        {
            _bySeed.Clear();
            const string path = "res://content/farms.tsv";
            if (!Godot.FileAccess.FileExists(path)) { GD.Print("[farms] no farms.tsv"); return; }
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            while (f != null && !f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line)) continue;
                var c = line.Split('\t');
                if (c.Length < 3 || !ushort.TryParse(c[0], out var id)) continue;
                uint.TryParse(c[1], out var growth);
                ushort.TryParse(c[2], out var grow);
                bool ignoreSoil = c.Length > 3 && c[3].Trim() == "1";
                _bySeed[id] = new FarmDef { Id = id, Growth = growth, Grow = grow, IgnoreSoil = ignoreSoil };
            }
            GD.Print($"[farms] loaded {_bySeed.Count} crop defs");
        }

        public static bool IsSeed(ushort id) => _bySeed.ContainsKey(id);
        public static bool TryGet(ushort id, out FarmDef def) => _bySeed.TryGetValue(id, out def);
        public static int Count => _bySeed.Count;
    }

    // A planted crop instance. Source InteractableFarm: IsFullyGrown when (now - planted) >= growth; harvest (use/E)
    // yields `grow`. The world-integration layer (a placed crop node + a Harvest interaction) builds on this.
    public class PlantedCrop
    {
        public FarmDef Def;
        public double PlantedAt;   // game-clock seconds when planted

        public bool IsFullyGrown(double now) => now - PlantedAt >= Def.Growth;

        // 0..1 growth progress -- for picking a visual growth stage (sprout -> grown).
        public float GrowthFraction(double now) => Def.Growth == 0 ? 1f : Mathf.Clamp((float)((now - PlantedAt) / Def.Growth), 0f, 1f);

        // Harvest: the yield item id if fully grown, else 0 (not ready yet).
        public ushort Harvest(double now) => IsFullyGrown(now) ? Def.Grow : (ushort)0;
    }
}
