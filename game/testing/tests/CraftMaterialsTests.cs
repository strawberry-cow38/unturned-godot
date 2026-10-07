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
            foreach (var (id, least) in new[] { ((ushort)69, 10), ((ushort)64, 10), ((ushort)67, 20), ((ushort)71, 100),
                                                ((ushort)9338, 20), ((ushort)9339, 20), ((ushort)9340, 20) })
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
                (9327, "Gunpowder"), (9328, "Sulfur"), (9329, "Screws"), (9330, "Springs"),
                (9331, "Gears"), (9332, "Hinges"), (9333, "Charcoal"), (9334, "Black Powder"), (9335, "Pipe"),
                (9338, "Ceramic"), (9339, "Circuitry"), (9340, "Asbestos"),
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

            var powder = Making(9334);
            T.Check("there is a recipe that makes Black Powder", powder != null);
            T.Check("...from Sulfur", Takes(powder, 9328));
            T.Check("...and Charcoal", Takes(powder, 9333));

            var gun = Making(9327);
            T.Check("there is a recipe that makes Gunpowder", gun != null);
            T.Check("...from Black Powder", Takes(gun, 9334));
            T.Check("...and Fertilizer", Takes(gun, 332));
            // ⭐ THE CHAIN IS TWO STEPS, which is what master asked for. If anyone later folds it into one recipe
            // that takes sulfur straight to gunpowder, this is the check that notices.
            T.Check("gunpowder does NOT come straight from sulfur -- the chain stays two steps", !Takes(gun, 9328));

            // ---- CHARCOAL IN A BARBECUE --------------------------------------------------------------------
            int coal = 0, food = 0, bad = 0;
            for (int i = 0; i < 400; i++)
            {
                int id = LootTables.Roll(LootTables.Barbecue);
                if (id == 9333) coal++;
                else if (id < 0) bad++;
                else { var a = Assets.find((ushort)id); if (a != null && a.type == EItemType.FOOD) food++; else bad++; }
            }
            GD.Print($"[craft-test] barbecue rolls: {coal} charcoal, {food} food, {bad} neither");
            T.Check($"a barbecue holds charcoal ({coal}/400)", coal > 0);
            T.Check($"...and still holds the food it used to ({food}/400)", food > 0);
            T.Check($"...and nothing else ({bad} unexpected)", bad == 0);

            // ---- MAKESHIFT SCOPE = BINOCULARS + TAPE (master 2026-10-07) -----------------------------------
            var scope = Making(476);
            T.Check("there is a recipe that makes the Makeshift Scope", scope != null);
            T.Check("...from Binoculars", Takes(scope, 333));
            T.Check("...and Tape", Takes(scope, 69));
            // ⚠ The binoculars are CONSUMED -- you are sacrificing them for the lens. A non-consumed input would
            // make this a tool recipe that prints free scopes, which is the opposite of the trade being described.
            bool binosConsumed = false;
            if (scope != null)
                foreach (var i in scope.Inputs)
                    if (Assets.findByGuid(i.Guid)?.id == 333) binosConsumed = i.Consume;
            T.Check("...and the binoculars are consumed, not used as a tool", binosConsumed);

            // ---- AN AMMO RECIPE FOR EVERY AMMO (master 2026-10-07) -----------------------------------------
            //
            // ⭐ Asserted over the SET, not over a list I wrote down. "Every ammo" is the requirement, so the test
            // enumerates isAmmo the same way the generator does -- if someone adds a cartridge and the generator
            // stops covering it, this fails without anyone remembering to add a case.
            int ammo = 0, covered = 0; string uncovered = null;
            int minScrap = int.MaxValue, maxScrap = 0, minBatch = int.MaxValue, maxBatch = 0;
            foreach (var a in Assets.all())
            {
                if (a == null || !a.isAmmo || string.IsNullOrEmpty(a.guid)) continue;
                ammo++;
                BlueprintDef made = null;
                foreach (var bp in BlueprintRegistry.All)
                {
                    if (bp.Operation != "Craft") continue;
                    foreach (var o in bp.Outputs) if (Assets.findByGuid(o.Guid)?.id == a.id) { made = bp; break; }
                    if (made != null) break;
                }
                if (made == null) { uncovered ??= $"{a.itemName} ({a.id})"; continue; }
                covered++;

                int scrapAmt = 0, powderAmt = 0, batch = 0;
                foreach (var i in made.Inputs)
                {
                    var ia = Assets.findByGuid(i.Guid);
                    if (ia?.id == 67) scrapAmt = i.Amount;
                    else if (ia?.id == 9327) powderAmt = i.Amount;
                }
                foreach (var o in made.Outputs) if (Assets.findByGuid(o.Guid)?.id == a.id) batch = o.Amount;

                if (scrapAmt <= 0 || powderAmt <= 0)
                    uncovered ??= $"{a.itemName} is made without scrap+powder";
                minScrap = Mathf.Min(minScrap, scrapAmt); maxScrap = Mathf.Max(maxScrap, scrapAmt);
                minBatch = Mathf.Min(minBatch, batch); maxBatch = Mathf.Max(maxBatch, batch);
            }
            GD.Print($"[craft-test] ammo: {covered}/{ammo} craftable, scrap {minScrap}-{maxScrap}, batch {minBatch}-{maxBatch}");
            T.Check($"there is ammo in the catalog to check ({ammo})", ammo > 0);
            T.Check($"every ammo has a recipe ({covered}/{ammo}, first gap: {uncovered ?? "none"})",
                    covered == ammo && uncovered == null);
            T.Check($"every one costs metal scrap AND gunpowder ({uncovered ?? "ok"})", uncovered == null);

            // ⭐ THE RATIO IS BALANCED, i.e. it actually VARIES with the round. A generator that returned the same
            // cost for everything would satisfy "every ammo has a recipe" perfectly.
            T.Check($"the cost varies by round rather than being flat (scrap {minScrap}-{maxScrap})", maxScrap > minScrap);
            T.Check($"...and so does the batch ({minBatch}-{maxBatch})", maxBatch > minBatch);

            // ⚠ A BIG round must cost MORE PER ROUND than a small one -- the actual meaning of "balanced ratios".
            // 12-gauge buckshot (113, stacks 32) vs 9mm (5007, stacks 180).
            float PerRound(ushort id)
            {
                foreach (var bp in BlueprintRegistry.All)
                {
                    if (bp.Operation != "Craft") continue;
                    int batch = 0, cost = 0;
                    foreach (var o in bp.Outputs) if (Assets.findByGuid(o.Guid)?.id == id) batch = o.Amount;
                    if (batch <= 0) continue;
                    foreach (var i in bp.Inputs) { var ia = Assets.findByGuid(i.Guid); if (ia?.id == 67 || ia?.id == 9327) cost += i.Amount; }
                    return cost / (float)batch;
                }
                return -1f;
            }
            float shell = PerRound(113), pistol = PerRound(5007);
            GD.Print($"[craft-test] per-round cost: 12ga {shell:0.00}, 9mm {pistol:0.00}");
            if (shell > 0f && pistol > 0f)
                T.Check($"a 12-gauge shell costs more per round than a 9mm ({shell:0.00} vs {pistol:0.00})", shell > pistol);

            yield break;
        }
    }
}
