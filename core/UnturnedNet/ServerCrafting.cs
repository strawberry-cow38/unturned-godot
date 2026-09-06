using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>Timed crafting on the SERVER (master 2026-09-06: "add crafting timed jobs to the server").
    ///
    /// Before this, OnCraft called Crafting.DoCraft the instant the command arrived: ingredients out, product
    /// in, same tick. The recipe times added earlier that day were therefore single-player only -- the client
    /// queue enforced them and the server did not, which is the worst arrangement of the two, because the
    /// authoritative side was the one with no rule.
    ///
    /// INGREDIENTS ARE SPENT AT ENQUEUE, not at completion, and that is the load-bearing decision. If they were
    /// only checked at the start and taken at the end, the same ten scrap could be queued into ten jobs that all
    /// validate and all pay out -- a duplication bug that looks like patience. Spending up front means a queued
    /// job is already paid for; the SP queue does the same thing and calls the held ingredients limbo.
    ///
    /// A job that cannot be paid out (the player left, the grid is full) drops rather than retrying forever.</summary>
    public sealed class ServerCrafting
    {
        public sealed class Job
        {
            public ushort BlueprintIndex;
            public float SecondsLeft;
            public float SecondsTotal;
            public List<(ushort id, int amt)> Spent;   // what came out of the bag, for a refund on cancel
        }

        readonly InventoryReplication _inventories;
        readonly Dictionary<ushort, List<Job>> _byOwner = new Dictionary<ushort, List<Job>>();

        /// <summary>The catalog, indexed the way the wire indexes it, read through a FUNC rather than copied.
        ///
        /// A copy taken at construction captures ServerTransactions.Blueprints while it is still Array.Empty --
        /// the game assigns the real catalog later (DedicatedServer / MpLoopback set it from BlueprintRegistry
        /// once the world is up). Every craft would then reject on a bounds check, silently, forever. Same trap
        /// as calling SetDrops before Register creates the record it writes to; a live read cannot be early.</summary>
        public System.Func<IReadOnlyList<BlueprintDef>> BlueprintsSource;
        IReadOnlyList<BlueprintDef> Blueprints => BlueprintsSource?.Invoke();

        /// <summary>(owner) whenever their queue changes -- started, finished, or cancelled. The host turns it
        /// into a unicast so the client can draw a queue it otherwise cannot see at all.</summary>
        public System.Action<ushort> QueueChanged;

        /// <summary>Default seconds for a recipe that declares none, matching the SP menu's base.</summary>
        public const float BaseSeconds = 1f;

        public ServerCrafting(InventoryReplication inventories) { _inventories = inventories; }

        public int JobCount(ushort owner) => _byOwner.TryGetValue(owner, out var l) ? l.Count : 0;
        public IReadOnlyList<Job> Jobs(ushort owner) => _byOwner.TryGetValue(owner, out var l) ? l : (IReadOnlyList<Job>)System.Array.Empty<Job>();

        public static float SecondsFor(BlueprintDef bp) => bp != null && bp.Seconds > 0f ? bp.Seconds : BaseSeconds;

        /// <summary>Take the ingredients and start the clock. Returns false when the bag cannot pay, in which
        /// case nothing is taken and nothing is queued.</summary>
        public bool Enqueue(ushort owner, ushort blueprintIndex)
        {
            if (Blueprints == null || blueprintIndex >= Blueprints.Count) return false;
            var bp = Blueprints[blueprintIndex];
            if (bp == null) return false;
            if (!_inventories.TryGet(owner, out var entry)) return false;

            var adapter = new Crafting.PlayerInvAdapter(entry.Inventory);
            if (!Crafting.CanCraft(bp, adapter, out _)) return false;

            // Spend now. Consume=false inputs are TOOLS -- they must be present (CanCraft just checked) and are
            // never taken, so a saw survives sawing.
            var spent = new List<(ushort, int)>();
            foreach (var ing in bp.Inputs)
            {
                if (!ing.Consume) continue;
                ushort id = Crafting.Resolve(ing.Guid);
                if (id == 0) continue;
                adapter.Remove(id, ing.Amount);
                spent.Add((id, ing.Amount));
            }

            float secs = SecondsFor(bp);
            if (!_byOwner.TryGetValue(owner, out var list)) _byOwner[owner] = list = new List<Job>();
            list.Add(new Job { BlueprintIndex = blueprintIndex, SecondsLeft = secs, SecondsTotal = secs, Spent = spent });
            _inventories.ServerMarkDirty(owner);
            QueueChanged?.Invoke(owner);
            return true;
        }

        /// <summary>One server tick. Finished jobs pay out in the order they were queued.</summary>
        public void Step(float dt)
        {
            if (_byOwner.Count == 0) return;
            List<ushort> changed = null;
            foreach (var kv in _byOwner)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;
                bool touched = false;
                // ONE job at a time, like the SP queue: a queue that ran every job in parallel would make the
                // times meaningless the moment you queued five.
                var job = list[0];
                job.SecondsLeft -= dt;
                if (job.SecondsLeft > 0f) continue;

                list.RemoveAt(0);
                touched = true;
                if (_inventories.TryGet(kv.Key, out var entry) && Blueprints != null && job.BlueprintIndex < Blueprints.Count)
                {
                    var bp = Blueprints[job.BlueprintIndex];
                    var adapter = new Crafting.PlayerInvAdapter(entry.Inventory);
                    foreach (var outp in bp.Outputs)
                    {
                        ushort id = Crafting.Resolve(outp.Guid);
                        if (id != 0) adapter.Add(id, outp.Amount);
                    }
                    _inventories.ServerMarkDirty(kv.Key);
                }
                if (touched) (changed ??= new List<ushort>()).Add(kv.Key);
            }
            if (changed != null) foreach (var o in changed) QueueChanged?.Invoke(o);
        }

        /// <summary>Give back everything a player still has in flight -- on disconnect, so leaving mid-craft
        /// does not eat the materials. Returns how many jobs were refunded.</summary>
        public int RefundAll(ushort owner)
        {
            if (!_byOwner.TryGetValue(owner, out var list) || list.Count == 0) return 0;
            int n = list.Count;
            if (_inventories.TryGet(owner, out var entry))
            {
                var adapter = new Crafting.PlayerInvAdapter(entry.Inventory);
                foreach (var job in list)
                    foreach (var (id, amt) in job.Spent) adapter.Add(id, amt);
                _inventories.ServerMarkDirty(owner);
            }
            list.Clear();
            QueueChanged?.Invoke(owner);
            return n;
        }

        public void Forget(ushort owner) { _byOwner.Remove(owner); }
    }
}
