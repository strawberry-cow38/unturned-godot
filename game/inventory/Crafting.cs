using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Craft execution: given a BlueprintDef + an inventory, check the recipe is satisfiable (consumable inputs
    // present in sufficient quantity + tools present) then execute it (consume consumables, keep tools, add
    // outputs). Blueprint ingredients are GUIDs -> resolved to numeric item ids via Assets.findByGuid.
    // Skill-level and nearby-station gating are the CALLER's responsibility (needs a skill system + station
    // detection, neither of which exists yet); this class does the recipe/item math.
    public static class Crafting
    {
        // Minimal inventory abstraction the logic runs against. The real PlayerInventory can implement/adapt this.
        public interface IInv
        {
            int Count(ushort id);        // total amount of item `id` held
            void Remove(ushort id, int amount);
            void Add(ushort id, int amount);
        }

        static ushort Resolve(string guid) => Assets.findByGuid(guid)?.id ?? (ushort)0;

        // Is `bp` craftable from `inv`? (item-satisfiability only; skill/station gated by the caller)
        public static bool CanCraft(BlueprintDef bp, IInv inv, out string reason)
        {
            foreach (var ing in bp.Inputs)
            {
                ushort id = Resolve(ing.Guid);
                if (id == 0) { reason = $"unresolved ingredient {ing.Guid}"; return false; }
                if (inv.Count(id) < ing.Amount)
                {
                    reason = $"need {ing.Amount}x {Assets.find(id)?.itemName ?? id.ToString()} (have {inv.Count(id)})";
                    return false;
                }
            }
            reason = "ok";
            return true;
        }

        // Execute: consume consumable inputs (Consume=true), leave tools (Consume=false), add outputs.
        // Returns false (no change) if not craftable. Note: RepairTargetItem/Ammo/Salvage operations that act on a
        // TARGET item (rather than producing outputs) are handled by the caller via bp.Operation after DoCraft
        // consumes the supplies -- e.g. RepairTargetItem sets the target's quality to 100.
        public static bool DoCraft(BlueprintDef bp, IInv inv)
        {
            if (!CanCraft(bp, inv, out _)) return false;
            foreach (var ing in bp.Inputs)
            {
                if (!ing.Consume) continue;   // a tool -> must be present but is not consumed
                inv.Remove(Resolve(ing.Guid), ing.Amount);
            }
            foreach (var outp in bp.Outputs)
            {
                ushort id = Resolve(outp.Guid);
                if (id != 0) inv.Add(id, outp.Amount);
            }
            return true;
        }

        // A simple dictionary-backed inventory (for tests / non-grid callers).
        public sealed class DictInv : IInv
        {
            public readonly Dictionary<ushort, int> Items = new();
            public int Count(ushort id) => Items.TryGetValue(id, out var n) ? n : 0;
            public void Remove(ushort id, int amount) { int n = Count(id) - amount; if (n > 0) Items[id] = n; else Items.Remove(id); }
            public void Add(ushort id, int amount) { Items[id] = Count(id) + amount; }
        }
    }
}
