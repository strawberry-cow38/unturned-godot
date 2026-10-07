using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Stackable crafting materials, the black-powder chain, and charcoal in a barbecue.
    ///
    /// Master 2026-10-07: "make duct tape, rope, metal scrap, nails stackable. add gunpowder, sulfur, screws,
    /// springs, pipes, gears, hinges as new items ... add a recipe for sulfur + charcoal to make black powder.
    /// black powder + fertilizer = gunpowder".
    ///
    /// ⚠ The recipes are keyed by GUID, not id, so the thing that silently breaks is a typo'd guid: the row loads,
    /// the blueprint exists, and no inventory on earth ever satisfies it. Every assertion here resolves the guid
    /// back to a REAL asset rather than trusting the string.</summary>
    public class CraftMaterials : GameTest
    {
        public override string Name => "craft.materials";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.Load();
            yield return Ticks(1);

            // ---- STACKABLE (master named four; the new components stack too) --------------------------------
            // ⚠ 69 is "Tape" in the catalog -- the retail name for what master calls duct tape. Asserted by ID so
            // this does not quietly start testing a different item if anything is ever renamed.
            foreach (var (id, least) in new[] { ((ushort)69, 10), ((ushort)64, 10), ((ushort)67, 20), ((ushort)71, 100) })
            {
                var a = Assets.find(id);
                T.Check($"item {id} exists", a != null);
                if (a != null)
                    T.Check($"{a.itemName} ({id}) stacks to at least {least} (got {a.stackSize})", a.stackSize >= least);
            }
            // CONTROL: stacking is not now universal -- a gun must still be one per slot, or "stackSize >= n" is
            // passing because something set every item in the game stackable.
            var rifle = Assets.find(4);
            T.Check($"control: a non-material is still unstackable ({rifle?.itemName} stack {rifle?.stackSize})",
                    rifle == null || rifle.stackSize <= 1);

            // ---- THE NEW COMPONENTS EXIST AND ARE REAL ITEMS ------------------------------------------------
            var wanted = new (ushort id, string name)[]
            {
                (9182, "Gunpowder"), (9183, "Sulfur"), (9184, "Screws"), (9185, "Springs"),
                (9186, "Gears"), (9187, "Hinges"), (9188, "Charcoal"), (9189, "Black Powder"), (9190, "Pipe"),
            };
            foreach (var w in wanted)
            {
                var a = Assets.find(w.id);
                T.Check($"{w.name} ({w.id}) is in the catalog as \"{a?.itemName}\"", a != null && a.itemName == w.name);
            }

            // ---- THE TWO RECIPES ---------------------------------------------------------------------------
            // Resolved by walking the loaded blueprints and matching on the OUTPUT's resolved asset, which is the
            // only form that proves the guid in the TSV actually names the item we meant.
            BlueprintDef Making(ushort outId)
            {
                foreach (var bp in BlueprintRegistry.All)
                    foreach (var o in bp.Outputs)
                        if (Assets.findByGuid(o.Guid)?.id == outId) return bp;
                return null;
            }
            bool Takes(BlueprintDef bp, ushort inId)
            {
                if (bp == null) return false;
                foreach (var i in bp.Inputs) if (Assets.findByGuid(i.Guid)?.id == inId) return true;
                return false;
            }

            var powder = Making(9189);
            T.Check("there is a recipe that makes Black Powder", powder != null);
            T.Check("...from Sulfur", Takes(powder, 9183));
            T.Check("...and Charcoal", Takes(powder, 9188));

            var gun = Making(9182);
            T.Check("there is a recipe that makes Gunpowder", gun != null);
            T.Check("...from Black Powder", Takes(gun, 9189));
            T.Check("...and Fertilizer", Takes(gun, 332));
            // ⭐ THE CHAIN IS TWO STEPS, which is what master asked for. If anyone later folds it into one recipe
            // that takes sulfur straight to gunpowder, this is the check that notices.
            T.Check("gunpowder does NOT come straight from sulfur -- the chain stays two steps", !Takes(gun, 9183));

            // ---- CHARCOAL IN A BARBECUE --------------------------------------------------------------------
            int coal = 0, food = 0, bad = 0;
            for (int i = 0; i < 400; i++)
            {
                int id = LootTables.Roll(LootTables.Barbecue);
                if (id == 9188) coal++;
                else if (id < 0) bad++;
                else { var a = Assets.find((ushort)id); if (a != null && a.type == EItemType.FOOD) food++; else bad++; }
            }
            GD.Print($"[craft-test] barbecue rolls: {coal} charcoal, {food} food, {bad} neither");
            T.Check($"a barbecue holds charcoal ({coal}/400)", coal > 0);
            T.Check($"...and still holds the food it used to ({food}/400)", food > 0);
            T.Check($"...and nothing else ({bad} unexpected)", bad == 0);

            yield break;
        }
    }
}
