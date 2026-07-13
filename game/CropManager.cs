using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Tracks planted crops + ticks their growth, and does plant/harvest. Source InteractableFarm uses Provider.time
    // (server seconds); here a manager clock accumulates real delta * UG_FARMSPEED (default 1) so growth is testable
    // (carrot Growth=10800s = 3h real -> set UG_FARMSPEED=1000 to mature in ~11s; `plant <crop> grown` spawns ready).
    public partial class CropManager : Node3D
    {
        static CropManager _inst;
        double _clock;
        readonly List<CropNode> _crops = new();
        static float Speed => float.TryParse(System.Environment.GetEnvironmentVariable("UG_FARMSPEED"), out var s) ? s : 1f;

        public override void _Ready()
        {
            _inst = this;
            CropRegistry.Load();          // crops.tsv: seed id -> crop assets + dirt color
            FarmRegistry.Load();          // farms.tsv: seed id -> Growth secs + Grow yield (increment 1)
        }

        public override void _Process(double delta)
        {
            _clock += delta * Speed;
            for (int i = _crops.Count - 1; i >= 0; i--)
            {
                if (!IsInstanceValid(_crops[i])) { _crops.RemoveAt(i); continue; }
                _crops[i].UpdateGrowth(_clock);
            }
        }

        public static double Now => _inst?._clock ?? 0;
        public static bool Active => _inst != null;   // a CropManager exists in the scene

        // Plant a crop at pos. grown=true spawns it already mature (harvest testing). Returns the node (null if unknown crop).
        public static CropNode Plant(string cropName, Vector3 pos, bool grown = false)
        {
            if (_inst == null || !CropRegistry.TryByName(cropName.ToLowerInvariant(), out var cd)) return null;
            var crop = CropNode.Spawn(cd.Name);
            FarmDef def = default;
            if (cd.SeedId != 0) FarmRegistry.TryGet(cd.SeedId, out def);
            if (def.Grow == 0) def = new FarmDef { Id = cd.SeedId, Growth = 30, Grow = cd.SeedId };   // fallback so it still grows/harvests
            crop.Crop = new PlantedCrop { Def = def, PlantedAt = grown ? -1e9 : _inst._clock };
            crop.AddToGroup("crop");
            _inst.AddChild(crop);
            crop.GlobalPosition = pos;
            crop.SetGrown(grown);
            _inst._crops.Add(crop);
            return crop;
        }

        // Harvest a grown crop: drop its Grow yield item at the crop + remove it. Returns false if not ready.
        // AGRICULTURE skill effect (source InteractableFarm): a chance of a SECOND yield item = Random.value < mastery(AGRICULTURE),
        // so at AGRICULTURE max (mastery 1.0) every harvest doubles. (harvestRewardExperience XP award is a follow-up -- needs per-crop extraction.)
        public static bool Harvest(CropNode crop, PlayerController by)
        {
            if (crop?.Crop == null || !crop.Crop.IsFullyGrown(Now)) return false;
            ushort yield = crop.Crop.Def.Grow;
            Vector3 at = crop.GlobalPosition + Vector3.Up * 0.3f;
            if (yield != 0 && by != null)
            {
                by.DropWorldItem(new Item(yield), at);
                var ag = by.Skills?.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE);
                if (ag != null && GD.Randf() < ag.Mastery) by.DropWorldItem(new Item(yield), at + Vector3.Right * 0.25f);   // agriculture 2nd yield
            }
            crop.QueueFree();
            return true;
        }

        // The nearest fully-grown crop within reach of the player (for E-harvest). Null if none.
        public static CropNode NearestGrown(Vector3 from, float reach = 3.0f)
        {
            if (_inst == null) return null;
            CropNode best = null; float bestD = reach * reach;
            foreach (var n in _inst.GetTree().GetNodesInGroup("crop"))
                if (n is CropNode c && c.Crop != null && c.Crop.IsFullyGrown(Now))
                {
                    float d = from.DistanceSquaredTo(c.GlobalPosition);
                    if (d < bestD) { bestD = d; best = c; }
                }
            return best;
        }
    }
}
