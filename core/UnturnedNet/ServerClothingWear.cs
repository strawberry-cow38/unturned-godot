using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// DURABILITY, the slow half: worn clothing wears out by being worn (strawberry 2026-10-06: "clothes get damaged by
    /// being worn (over hours)"). A garment on a living player loses its whole condition over
    /// <see cref="Durability.ClothingDaysToZero"/> IN-GAME days -- in game time, not wall time, so a server that runs a
    /// longer day wears clothes proportionally slower, the way food spoils per in-game day.
    ///
    /// The other half (a hit wears what covers it) is ServerCombat.ClothingHit, wired by the host.
    ///
    /// SERVER ONLY. The owner's copy of a worn item is overwritten by every inventory echo, so the loss has to land on
    /// the server's copy and ride the echo out (ServerMarkDirty), the same route the gas-mask filter burn takes.
    /// </summary>
    public sealed class ServerClothingWear
    {
        readonly InventoryReplication _inventories;

        /// <summary>Is this player alive? A corpse does not wear its clothes out. Unset = everyone counts.</summary>
        public Func<ushort, bool> IsAlive;

        /// <summary>Seconds in one in-game day (the world clock's). Unset, or 0 = an hour, PEI's day.</summary>
        public Func<float> DayLengthSeconds;

        public const float FallbackDaySeconds = 3600f;

        /// <summary>Points lost so far, all players -- for tests and the net log.</summary>
        public long PointsLost;

        // The fraction of a point each worn ITEM has accumulated. Keyed by the Item object, not by (player, slot): an
        // item taken off and worn again later carries its own partial progress, and a garment handed to someone else
        // does not inherit theirs. Weak, so a dropped and despawned item takes its entry with it.
        readonly ConditionalWeakTable<Item, StrongBox<float>> _partial = new ConditionalWeakTable<Item, StrongBox<float>>();

        public ServerClothingWear(InventoryReplication inventories) { _inventories = inventories; }

        public void Step(float dt)
        {
            if (dt <= 0f) return;
            float day = DayLengthSeconds?.Invoke() ?? 0f;
            if (day <= 0f) day = FallbackDaySeconds;
            float pointsPerSecond = 100f / (Durability.ClothingDaysToZero * day);
            float gain = pointsPerSecond * dt;
            foreach (var e in _inventories.Owners)
            {
                if (e?.Inventory == null) continue;
                if (IsAlive != null && !IsAlive(e.OwnerPlayerId)) continue;
                bool changed = false;
                foreach (var (_, item) in e.Inventory.WornPieces())
                {
                    if (item.quality == 0) continue;
                    var a = Assets.find(item.id);
                    if (a == null || PlayerInventory.IsFilterMask(a)) continue;   // a respirator's quality is its filter
                    var acc = _partial.GetValue(item, _ => new StrongBox<float>(0f));
                    acc.Value += gain;
                    if (acc.Value < 1f) continue;
                    int whole = (int)acc.Value;
                    acc.Value -= whole;
                    int loss = Math.Min(whole, (int)item.quality);
                    item.quality = (byte)(item.quality - loss);
                    PointsLost += loss;
                    changed = true;
                }
                if (changed) _inventories.ServerMarkDirty(e.OwnerPlayerId);
            }
        }
    }
}
