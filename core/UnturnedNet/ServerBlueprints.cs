using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot;   // BlueprintDef + Crafting (engine-free, core/UnturnedSim)

namespace UnturnedGodot.Net
{
    /// <summary>Which LOCKED recipes each player knows (strawberry 2026-10-04: "certain recipes arent known by default.
    /// unlocked by either directly learning or via skill unlocks ... learned blueprints should be tracked per player",
    /// "also persisting through saving etc").
    ///
    /// SERVER-OWNED, like inventory and skills. The client is told its own set (KnownBlueprintsEvent, owner-only) and
    /// draws from it; it never decides. OnCraft asks Knows() before it spends anything, so a client that pretends to
    /// know a recipe gets a refusal, not the item.
    ///
    /// KEYED BY RECIPE, never by skill. A skill is only a TRIGGER (BlueprintDef.Unlocks), read when it fires; what is
    /// stored is the recipe's Key. The skill set is about to be replaced, and a saved table keyed by a skill id would
    /// be the first thing that broke.
    ///
    /// Only LOCKED recipes are ever stored. An unlocked one is known by everybody already, and storing it would make
    /// the saved set a copy of the catalog that goes stale the day a row changes.</summary>
    public sealed class ServerBlueprints
    {
        readonly Dictionary<ushort, HashSet<string>> _known = new Dictionary<ushort, HashSet<string>>();
        readonly Dictionary<ushort, long> _skillsSeen = new Dictionary<ushort, long>();

        /// <summary>The catalog, read live (the host assigns it after construction -- see ServerCrafting).</summary>
        public Func<IReadOnlyList<BlueprintDef>> Catalog;
        public SkillsReplication Skills;
        /// <summary>(owner) whenever their set changes. The host turns it into the owner-only event.</summary>
        public Action<ushort> Changed;

        public long LearnedTotal;     // diagnostics: recipes newly learned, by any route
        public long ItemsSpent;       // teaching items consumed

        public void ServerAdd(ushort owner) { _known[owner] = new HashSet<string>(); _skillsSeen.Remove(owner); }
        public void ServerRemove(ushort owner) { _known.Remove(owner); _skillsSeen.Remove(owner); }
        public bool Has(ushort owner) => _known.ContainsKey(owner);

        public IReadOnlyCollection<string> KnownBy(ushort owner)
            => _known.TryGetValue(owner, out var s) ? s : (IReadOnlyCollection<string>)Array.Empty<string>();

        public bool Knows(ushort owner, BlueprintDef bp)
            => bp != null && (!bp.Locked || (_known.TryGetValue(owner, out var s) && s.Contains(bp.Key)));

        BlueprintDef Find(string keyOrName)
        {
            var cat = Catalog?.Invoke();
            if (cat == null || string.IsNullOrEmpty(keyOrName)) return null;
            foreach (var bp in cat) if (bp.Key == keyOrName) return bp;
            return null;
        }

        /// <summary>Learn one recipe. False when there is nothing to learn: no such recipe, it is not locked, or it is
        /// already known -- none of which is an error, and none of which may fire Changed.</summary>
        public bool Learn(ushort owner, string key)
        {
            if (!_known.TryGetValue(owner, out var set)) return false;
            var bp = Find(key);
            if (bp == null || !bp.Locked || !set.Add(bp.Key)) return false;
            LearnedTotal++;
            Changed?.Invoke(owner);
            return true;
        }

        /// <summary>Learn every locked recipe in one go (the admin console's `learnall`). Returns how many were new.</summary>
        public int LearnAll(ushort owner)
        {
            if (!_known.TryGetValue(owner, out var set)) return 0;
            var cat = Catalog?.Invoke();
            int n = 0;
            if (cat != null) foreach (var bp in cat) if (bp.Locked && set.Add(bp.Key)) n++;
            if (n > 0) { LearnedTotal += n; Changed?.Invoke(owner); }
            return n;
        }

        public bool Forget(ushort owner, string key)
        {
            if (!_known.TryGetValue(owner, out var set) || !set.Remove(key)) return false;
            Changed?.Invoke(owner);
            return true;
        }

        /// <summary>Put a SAVED set back. Keys are kept even when no recipe currently carries them: a catalog row that
        /// is missing today (renamed, being edited) must not cost the player the knowledge the day it returns. Only the
        /// shape is checked, because this came off disk. Does not fire Changed -- the join path sends the whole set
        /// once, after everything else is restored.</summary>
        public void Restore(ushort owner, IEnumerable<string> keys)
        {
            if (!_known.TryGetValue(owner, out var set) || keys == null) return;
            foreach (var k in keys) if (BlueprintDef.IsValidKey(k) && set.Count < KnownBlueprintsEvent.MaxKeys) set.Add(k);
        }

        /// <summary>What happens when `owner` uses item `itemId`, as far as blueprints go:
        ///   -1  the item teaches nothing (the caller carries on and treats it as ordinary use)
        ///    0  it teaches only recipes they already know (refuse, and do NOT spend the item)
        ///   &gt;0  that many recipes newly learned (the caller spends ONE of the item)</summary>
        public int LearnFromItem(ushort owner, ushort itemId)
        {
            var cat = Catalog?.Invoke();
            if (cat == null || itemId == 0 || !_known.TryGetValue(owner, out var set)) return -1;
            bool teaches = false;
            int learned = 0;
            foreach (var bp in cat)
            {
                if (!bp.Locked) continue;
                foreach (var id in bp.TaughtByItems())
                {
                    if (id != itemId) continue;
                    teaches = true;
                    if (set.Add(bp.Key)) learned++;
                    break;
                }
            }
            if (!teaches) return -1;
            if (learned > 0) { LearnedTotal += learned; Changed?.Invoke(owner); }
            return learned;
        }

        /// <summary>Does using this item teach anything at all (regardless of who is asking).</summary>
        public bool Teaches(ushort itemId)
        {
            var cat = Catalog?.Invoke();
            if (cat == null) return false;
            foreach (var bp in cat)
                if (bp.Locked) foreach (var id in bp.TaughtByItems()) if (id == itemId) return true;
            return false;
        }

        /// <summary>Learn every locked recipe whose `skill:` unlock this player now meets. Returns how many were new.</summary>
        public int CheckSkillUnlocks(ushort owner)
        {
            if (!_known.TryGetValue(owner, out var set)) return 0;
            var cat = Catalog?.Invoke();
            if (cat == null || Skills == null || !Skills.TryGet(owner, out var se) || se.Skills == null) return 0;
            int n = 0;
            foreach (var bp in cat)
                if (bp.Locked && !set.Contains(bp.Key) && Crafting.SkillUnlockMet(bp, se.Skills) && set.Add(bp.Key)) n++;
            if (n > 0) { LearnedTotal += n; Changed?.Invoke(owner); }
            return n;
        }

        /// <summary>One server tick: re-check skill unlocks for anyone whose skills changed since we last looked. Keyed
        /// off the skills entry's own change stamp, so a quiet server costs one dictionary walk and nothing else -- and
        /// every route that levels a skill (XP spend, the console, a save restore) is covered without each of them
        /// having to remember to call in here.</summary>
        public void Step()
        {
            if (Skills == null || _known.Count == 0) return;
            List<ushort> due = null;
            foreach (var owner in _known.Keys)
            {
                if (!Skills.TryGet(owner, out var se)) continue;
                if (_skillsSeen.TryGetValue(owner, out long seen) && seen == se.LastChangedTick) continue;
                (due ??= new List<ushort>()).Add(owner);
                _skillsSeen[owner] = se.LastChangedTick;
            }
            if (due != null) foreach (var o in due) CheckSkillUnlocks(o);
        }
    }

    /// <summary>v55: to the OWNER only -- every locked recipe key they know, whole. Sent on join and on every change;
    /// a full set rather than a delta because it moves at learning speed and a few dozen short strings are cheaper
    /// than an ack chain (the NpcStateEvent reasoning). Each key is untrusted text on arrival and is checked against
    /// BlueprintDef.IsValidKey, and the count is bounded BEFORE the allocation.</summary>
    public struct KnownBlueprintsEvent
    {
        public const int MaxKeys = 1024;
        public string[] Keys;

        public void Write(NetPakWriter w)
        {
            var k = Keys ?? Array.Empty<string>();
            int n = Math.Min(k.Length, MaxKeys);
            w.WriteUInt16((ushort)n);
            for (int i = 0; i < n; i++) w.WriteString(k[i] ?? "");
        }

        public static bool TryRead(NetPakReader r, out KnownBlueprintsEvent e)
        {
            e = default;
            if (!r.ReadUInt16(out ushort n) || n > MaxKeys) return false;
            var keys = new string[n];
            for (int i = 0; i < n; i++)
            {
                if (!r.ReadString(out string s) || !BlueprintDef.IsValidKey(s)) return false;
                keys[i] = s;
            }
            e = new KnownBlueprintsEvent { Keys = keys };
            return true;
        }
    }
}
