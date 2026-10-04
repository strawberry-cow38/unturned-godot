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

        /// <summary>Blueprint guid -> item id, 0 when this port does not ship the item. Public since
        /// 2026-09-06: ServerCrafting spends and pays out through the same resolution the validator uses, and
        /// a second copy of this line is a second place for the two to disagree.</summary>
        public static ushort Resolve(string guid) => Assets.findByGuid(guid)?.id ?? (ushort)0;

        /// Does the player meet a blueprint's SKILL requirement? `BlueprintDef.Skill` is a free string straight out
        /// of the item .dat, so this resolves it in two passes.
        ///
        /// ⭐ FIRST, the name is matched against our OWN skill set, so a recipe can simply say "Carpentry" or
        /// "Gunsmithing" and gate on exactly that. That is the target state and new/retuned data should use it.
        ///
        /// ⚠ SECOND, retail's three tags (Craft/Cook/Repair) still cover all 1,875 extracted rows. Cook and Repair
        /// map cleanly onto Cooking and Mechanics. **"Craft" does not map onto anything**, because the single
        /// retail CRAFTING skill is what we split three ways -- and the blueprint does not say whether it is
        /// making a plank, a pipe or a receiver.
        ///
        /// ⭐ So a generic "Craft" row is satisfied by the BEST of the three trades rather than by one arbitrarily
        /// chosen one. Picking (say) Carpentry would have silently made Metalworking and Gunsmithing ungated
        /// placeholders -- the dead-skill failure this whole rework exists to end -- and picking "all three" would
        /// gate a campfire behind gunsmithing. Best-of keeps every trade meaningful and gates nothing absurdly,
        /// until the data is split per recipe.
        public static bool MeetsSkill(BlueprintDef bp, PlayerSkills skills)
        {
            if (!bp.RequiresSkill || skills == null) return true;
            if (System.Enum.TryParse<ESkill>(bp.Skill, true, out var exact) && System.Enum.IsDefined(typeof(ESkill), exact))
                return skills.Level(exact) >= bp.SkillLevel;
            switch ((bp.Skill ?? "").ToLowerInvariant())
            {
                case "cook": return skills.Level(ESkill.Cooking) >= bp.SkillLevel;
                case "repair": return skills.Level(ESkill.Mechanics) >= bp.SkillLevel;
                case "craft": return BestTrade(skills) >= bp.SkillLevel;
                default: return true;   // unknown tag -> don't gate
            }
        }

        /// The highest of the three making trades -- see the "Craft" note above.
        public static int BestTrade(PlayerSkills skills)
            => System.Math.Max((int)skills.Level(ESkill.Metalworking),
                 System.Math.Max((int)skills.Level(ESkill.Carpentry), (int)skills.Level(ESkill.Gunsmithing)));

        // Skill-aware craftability: the item math AND the skill gate (source: a blueprint needs its Craft/Cook/Repair level).
        public static bool CanCraft(BlueprintDef bp, IInv inv, PlayerSkills skills, out string reason)
        {
            if (!MeetsSkill(bp, skills)) { reason = $"need {bp.Skill} skill level {bp.SkillLevel}"; return false; }
            return CanCraft(bp, inv, out reason);
        }

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

        // Does the player have the crafting STATIONS this blueprint needs? `available` = the crafting tags granted by
        // nearby placed stations (workbench/campfire/...) within Range + line-of-sight, computed by the caller.
        // A recipe with no station tags is craftable anywhere.
        public static bool HasStations(BlueprintDef bp, System.Collections.Generic.ICollection<string> available)
        {
            foreach (var tag in bp.StationTags)
                if (available == null || !available.Contains(tag)) return false;
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

        // Adapts the real grid PlayerInventory to IInv so crafting runs against the player's actual items.
        public sealed class PlayerInvAdapter : IInv
        {
            readonly PlayerInventory _inv;
            public PlayerInvAdapter(PlayerInventory inv) { _inv = inv; }
            public int Count(ushort id) => _inv.getItemCount(id);
            public void Remove(ushort id, int amount) => _inv.removeItemAmount(id, amount);
            public void Add(ushort id, int amount)
            {
                while (amount > 0) { int take = System.Math.Min(amount, ushort.MaxValue); _inv.tryAddItem(new Item(id, (ushort)take)); amount -= take; }
            }
        }
    }
}
