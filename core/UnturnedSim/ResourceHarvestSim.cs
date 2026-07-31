using System;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>One resource TYPE's harvest rules, baked from its retail .dat by
    /// tools/extract_resource_harvest.py.</summary>
    public sealed class ResourceHarvestDef
    {
        public ushort AssetId;
        public ushort Health;
        public uint RewardXp;
        public float ResetSeconds;
        public byte RewardMin, RewardMax;
        public bool HasDebris;
        public bool IsForage;

        /// <summary>Which blade cuts it (.dat BladeID, ABSENT on most trees so it defaults to 0).</summary>
        public byte BladeId;
        public bool VulnerableToAllMelee;   // .dat Vulnerable_To_All_Melee_Weapons
        public bool VulnerableToFists;      // .dat Vulnerable_To_Fists

        /// <summary>The reward SPAWN TABLE, already resolved to item ids with their weights. Retail's
        /// Reward_ID is a legacy spawn-table id, not an item id -- birch's 515 is a table, while item 515
        /// is Cooked Venison. Resolving it is the extractor's job so the sim never has to know.</summary>
        public (ushort Item, int Weight)[] Drops = Array.Empty<(ushort, int)>();

        public int TotalWeight
        {
            get { int t = 0; for (int i = 0; i < Drops.Length; i++) t += Drops[i].Weight; return t; }
        }
    }

    /// <summary>
    /// Chopping down a tree: health per standing instance, what it drops, and when it grows back.
    ///
    /// Engine-free because felling is server-authoritative -- the drops are real items and the XP is real
    /// progression, so a client cannot be trusted to say a tree fell. The port already had the wire half
    /// (deterministic instance indices, an alive bitmap, harvested/respawned events) with an entry point
    /// documented as "no game mechanic fells trees yet". This is that mechanic.
    ///
    /// Instances are addressed by their load-order index, the same id space the replication uses, so
    /// nothing here needs to know what a tree looks like or where it is.
    /// </summary>
    public sealed class ResourceHarvestSim
    {
        readonly Dictionary<ushort, ResourceHarvestDef> _defs = new Dictionary<ushort, ResourceHarvestDef>();
        readonly Dictionary<int, ushort> _assetOf = new Dictionary<int, ushort>();   // instance index -> asset id
        readonly Dictionary<int, int> _health = new Dictionary<int, int>();          // only for damaged instances
        readonly Dictionary<int, float> _regrow = new Dictionary<int, float>();      // felled index -> seconds left

        public void RegisterDef(ResourceHarvestDef def) => _defs[def.AssetId] = def;
        public bool TryGetDef(ushort assetId, out ResourceHarvestDef def) => _defs.TryGetValue(assetId, out def);
        public int DefCount => _defs.Count;

        /// <summary>Bind a world instance to its type. Called once per placed resource at world build.</summary>
        public void RegisterInstance(int index, ushort assetId) => _assetOf[index] = assetId;

        public bool TryGetDefForInstance(int index, out ResourceHarvestDef def)
        {
            def = null;
            return _assetOf.TryGetValue(index, out ushort a) && _defs.TryGetValue(a, out def);
        }

        /// <summary>Health left on a standing instance. Full health until something hits it -- storing a
        /// row per tree at world build would be 1694 entries that are almost all untouched.</summary>
        public int HealthOf(int index) =>
            _health.TryGetValue(index, out int h) ? h
            : TryGetDefForInstance(index, out var d) ? d.Health : 0;

        /// <summary>Whether a swing can hurt this resource at all.
        ///
        /// Retail: `vulnerableToAllMeleeWeapons || meleeAsset.hasBladeID(asset.bladeID)`. Skipping this and
        /// letting any melee weapon chop is the easy mistake -- it looks correct in play and quietly
        /// deletes a real gate, since trees default to blade 0 and a weapon has to declare 0 to fell one.
        /// Fists take the separate Vulnerable_To_Fists route rather than a blade list.</summary>
        public static bool CanChop(ResourceHarvestDef def, System.Func<int, bool> weaponHasBlade, bool isFists = false)
        {
            if (def == null) return false;
            if (isFists) return def.VulnerableToFists;
            if (def.VulnerableToAllMelee) return true;
            return weaponHasBlade != null && weaponHasBlade(def.BladeId);
        }

        /// <summary>Hit a resource. Returns true if THIS hit felled it.
        ///
        /// Retail computes `damage * times` at the call site and passes a ushort; the caller owns the
        /// weapon maths, this owns the health. A hit on an already-felled instance is not an error -- two
        /// players can swing at the same trunk on the same tick -- it just does nothing and returns false,
        /// so the drops cannot be claimed twice.</summary>
        public bool Damage(int index, int amount, out ResourceHarvestDef def)
        {
            def = null;
            if (amount <= 0) return false;
            if (_regrow.ContainsKey(index)) return false;          // already down, waiting to regrow
            if (!TryGetDefForInstance(index, out def)) return false;
            int hp = HealthOf(index) - amount;
            if (hp > 0) { _health[index] = hp; return false; }
            _health.Remove(index);
            _regrow[index] = def.ResetSeconds;
            return true;
        }

        public bool IsFelled(int index) => _regrow.ContainsKey(index);

        /// <summary>How many items a felling yields: retail's
        /// ceil(Random.Range(rewardMin, rewardMax + 1) * multiplier), clamped 0..100 so nobody can be
        /// crashed by a silly multiplier. `roll01` supplies the randomness so the server owns it.</summary>
        public static int RewardCount(ResourceHarvestDef def, double roll01, float dropMultiplier = 1f)
        {
            if (def == null || def.RewardMax < def.RewardMin) return 0;
            int span = def.RewardMax - def.RewardMin + 1;                 // Range(min, max+1) is inclusive of max
            int n = def.RewardMin + (int)(roll01 * span);
            if (n > def.RewardMax) n = def.RewardMax;                     // roll01 == 1.0 must not overshoot
            int scaled = (int)Math.Ceiling(n * (double)dropMultiplier);
            return scaled < 0 ? 0 : scaled > 100 ? 100 : scaled;
        }

        /// <summary>One weighted pick from the type's reward table. Rolled PER item, which is what makes a
        /// maple occasionally give syrup instead of a log rather than yielding a fixed ratio.</summary>
        public static ushort RollDrop(ResourceHarvestDef def, double roll01)
        {
            if (def == null || def.Drops.Length == 0) return 0;
            int total = def.TotalWeight;
            if (total <= 0) return 0;
            double t = roll01 * total;
            double acc = 0;
            for (int i = 0; i < def.Drops.Length; i++)
            {
                acc += def.Drops[i].Weight;
                if (t < acc) return def.Drops[i].Item;
            }
            return def.Drops[def.Drops.Length - 1].Item;   // roll01 == 1.0 lands here rather than nowhere
        }

        /// <summary>Advance regrow timers. Yields each index whose timer expired this step, so the caller
        /// can flip the alive bit and broadcast -- the sim never touches the world itself.</summary>
        public IEnumerable<int> Step(float dt)
        {
            if (_regrow.Count == 0) yield break;
            List<int> due = null;
            // Materialised before mutating: you cannot edit a Dictionary while enumerating it, and the
            // alternative (rebuilding the whole map every tick) costs more than the rare due list.
            foreach (var kv in _regrow)
            {
                float left = kv.Value - dt;
                if (left <= 0f) (due ??= new List<int>()).Add(kv.Key);
                else _pending[kv.Key] = left;
            }
            foreach (var kv in _pending) _regrow[kv.Key] = kv.Value;
            _pending.Clear();
            if (due == null) yield break;
            for (int i = 0; i < due.Count; i++) { _regrow.Remove(due[i]); yield return due[i]; }
        }

        readonly Dictionary<int, float> _pending = new Dictionary<int, float>();

        /// <summary>Forget everything -- between worlds, so one map's felled trees don't shadow the next.</summary>
        public void Clear() { _assetOf.Clear(); _health.Clear(); _regrow.Clear(); _pending.Clear(); }
    }
}
