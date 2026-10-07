using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>How much a container is stocked with, and what a toaster is stocked with.
    ///
    /// Master 2026-10-07: "stop storage containers spawning a million items inside. switch the loot tables inside
    /// toasters to only spawn bread."
    ///
    /// ⭐ THE FIRST ONE WAS A DEFAULT, NOT A SETTING. `StoreShelf.Prof` fell through to **Shelf_1** -- the
    /// five-metre store gondola, Min 12 / Max 22 -- for every mesh without its own entry. So a microwave, a
    /// garbage bag and a filing cabinet were each stocked like a supermarket aisle, and nothing anywhere said
    /// "22": it was one fallback written for the one prop that really is an aisle.
    ///
    /// ⚠ Asserted on BOTH paths. SP builds containers through StoreShelf.Spawn and MP through
    /// ContainerNetSync, and they ask for the loot count SEPARATELY -- so a fix applied to one leaves the other
    /// stocking a different world, which is exactly the two-paths-one-feature drift that has bitten the ocean
    /// builders and the power-line loader in this codebase already.</summary>
    public class ContainerLootCount : GameTest
    {
        public override string Name => "loot.container_counts";

        public override IEnumerable<Step> Run()
        {
            // ---- THE DEFAULT: a solid container is not a shop aisle ---------------------------------------
            var (dmin, dmax) = StoreShelf.LootCount("No_Such_Prop_0", display: false);
            var (gmin, gmax) = StoreShelf.LootCount("No_Such_Prop_0", display: true);
            GD.Print($"[loot-test] unknown mesh -> solid {dmin}-{dmax}, display {gmin}-{gmax}");

            T.Check($"a SOLID container's default is a cupboard's worth, not an aisle's ({dmin}-{dmax})",
                    dmax <= 6 && dmin >= 1 && dmin <= dmax);
            // ⭐ THE CONTROL. If this stops being the gondola the test above goes quiet for the wrong reason --
            // it would pass just as happily if every container dropped to one item because the profile table
            // broke. The display shelf must STILL be generous.
            T.Check($"control: a DISPLAY shelf still stocks like a gondola ({gmin}-{gmax})", gmax >= 12);
            T.Check("...and the two defaults are genuinely different", dmax < gmax);

            // A named prop keeps its own tuning either way -- the fallback must not override an explicit entry.
            var (tmin, tmax) = StoreShelf.LootCount("Toaster_0", display: false);
            T.Check($"an explicitly profiled prop keeps its own count (Toaster_0 {tmin}-{tmax})", tmax <= 2);

            // ---- AND THE ROLL HONOURS IT ------------------------------------------------------------------
            // Not just the number: that the stocking code actually uses it. A count nobody reads is the shape of
            // bug this whole file exists for.
            // ⚠ makeLoot resolves an id through the catalog, so without this RollInto places NOTHING and the
            // count assertion below passes or fails for a reason that has nothing to do with the count.
            SDG.Unturned.ItemCatalog.RegisterAll();
            // ⚠ The empty roll is pinned OFF for the COUNT check and left real for the empty check further down.
            // Mixing them made this assertion fail 18% of the time on a container that was behaving correctly --
            // the same flakiness EmptyChance introduced into unify.container_loot. One question per assertion.
            StoreShelf.EmptyChanceForTests = 0f;
            var storage = new SDG.Unturned.Items(SDG.Unturned.PlayerInventory.STORAGE);
            storage.resize(8, 6);
            StoreShelf.RollInto(storage, dmin, dmax, LootTables.Toaster);
            int placed = storage.getItemCount();
            GD.Print($"[loot-test] RollInto placed {placed} item(s) for a solid container");
            T.Check($"RollInto respects the solid count ({placed} placed, asked {dmin}-{dmax})",
                    placed >= dmin && placed <= dmax);

            // ---- A TOASTER HOLDS BREAD ---------------------------------------------------------------------
            // ⚠ Rolled MANY times, not once. A single roll passing proves nothing about a table whose failure
            // mode is "mostly bread, occasionally a can of beans" -- which is what table 6 Food would give.
            int bread = 0, other = 0, bad = 0;
            for (int i = 0; i < 200; i++)
            {
                int id = LootTables.Roll(LootTables.Toaster);
                if (id == 460) bread++;
                else if (id < 0) bad++;
                else other++;
            }
            GD.Print($"[loot-test] toaster rolls: {bread} bread, {other} other, {bad} failed");
            T.Check($"every toaster roll is bread over 200 draws ({bread} bread, {other} other, {bad} failed)",
                    bread == 200 && other == 0 && bad == 0);
            T.Check("the toaster table is named, so the editor's picker can show it",
                    LootTables.TableName(LootTables.Toaster) == "Toaster");
            // ⭐ CONTROL: the virtual table must not have broken the REAL ones beside it. Table 6 is Food, which
            // is what the toaster used to roll -- if this came back bread-only too, the dispatch would be wrong.
            if (LootTables.Loaded && LootTables.TableCount > 6)
            {
                int food6 = 0, breadFrom6 = 0;
                for (int i = 0; i < 200; i++) { int id = LootTables.Roll(6); if (id >= 0) { food6++; if (id == 460) breadFrom6++; } }
                GD.Print($"[loot-test] control, real table 6: {food6} rolled, {breadFrom6} of them bread");
                T.Check($"control: the real Food table still rolls more than bread ({breadFrom6}/{food6} bread)",
                        food6 == 0 || breadFrom6 < food6);
            }

            // ---- PER-CONTAINER TABLES (master 2026-10-07) --------------------------------------------------
            //
            // ⭐ Every one of these is asserted on the ITEM THAT COMES OUT, not on the table being wired. A table
            // pointing at an empty id list rolls -1 forever and looks exactly like a table that is simply unlucky,
            // which is the failure mode a derived-from-the-catalog table actually has.
            bool AllRollsSatisfy(int table, int draws, System.Func<SDG.Unturned.ItemAsset, bool> ok, out int n, out string worst)
            {
                n = 0; worst = null; bool good = true;
                for (int i = 0; i < draws; i++)
                {
                    int id = LootTables.Roll(table);
                    if (id < 0) { good = false; worst ??= "roll returned nothing"; continue; }
                    var a = SDG.Unturned.Assets.find((ushort)id);
                    if (a == null) { good = false; worst ??= $"id {id} is not an item"; continue; }
                    n++;
                    if (!ok(a)) { good = false; worst ??= $"{a.itemName} ({a.id})"; }
                }
                return good;
            }

            // FRIDGE: perishable food, or a drink. Perishable is FoodSpoil's own rate, the same source the table is
            // built from -- so this asserts the pipeline, and the PerishableAtLeast control below asserts the rule.
            bool fridgeOk = AllRollsSatisfy(LootTables.Fridge, 300, a =>
                (a.type == SDG.Unturned.EItemType.FOOD && FoodSpoil.PerDay(a) >= LootTables.PerishableAtLeast)
                || a.type == SDG.Unturned.EItemType.WATER, out int fridgeN, out string fridgeBad);
            GD.Print($"[loot-test] fridge: {fridgeN} rolled, offender={fridgeBad ?? "none"}");
            T.Check($"a fridge holds only perishables and drinks ({fridgeN} rolled, offender {fridgeBad ?? "none"})",
                    fridgeOk && fridgeN > 0);

            // ⭐ CONTROL: a CANNED good must be excluded. If PerishableAtLeast ever drifts above every food's rate
            // the check above passes vacuously -- an empty table satisfies "everything in it is perishable".
            var beans = SDG.Unturned.Assets.find(13);
            T.Check($"control: canned food is NOT perishable by the rule (beans {FoodSpoil.PerDay(beans):0.#}/day < {LootTables.PerishableAtLeast})",
                    beans != null && FoodSpoil.PerDay(beans) < LootTables.PerishableAtLeast);

            // FREEZER: perishables only, and no drinks at all.
            bool freezeOk = AllRollsSatisfy(LootTables.Freezer, 200, a =>
                a.type == SDG.Unturned.EItemType.FOOD && FoodSpoil.PerDay(a) >= LootTables.PerishableAtLeast,
                out int freezeN, out string freezeBad);
            T.Check($"a freezer holds perishables and no drinks ({freezeN} rolled, offender {freezeBad ?? "none"})",
                    freezeOk && freezeN > 0);

            // OVEN: master was explicit -- "ovens shouldnt spawn food".
            bool ovenOk = AllRollsSatisfy(LootTables.Oven, 200, a => a.type != SDG.Unturned.EItemType.FOOD,
                                          out int ovenN, out string ovenBad);
            T.Check($"an oven never holds food ({ovenN} rolled, offender {ovenBad ?? "none"})", ovenOk && ovenN > 0);

            // DISHWASHER / FILING CABINET: fixed id lists, so the check is that they resolve to REAL items at all.
            bool dishOk = AllRollsSatisfy(LootTables.Dishwasher, 200, _ => true, out int dishN, out string dishBad);
            T.Check($"every dishwasher roll is a real item ({dishN}, {dishBad ?? "ok"})", dishOk && dishN == 200);
            bool fileOk = AllRollsSatisfy(LootTables.FilingCabinet, 200, _ => true, out int fileN, out string fileBad);
            T.Check($"every filing-cabinet roll is a real item ({fileN}, {fileBad ?? "ok"})", fileOk && fileN == 200);
            bool binOk = AllRollsSatisfy(LootTables.GarbageBag, 300, _ => true, out int binN, out string binBad);
            T.Check($"every garbage-bag roll is a real item ({binN}, {binBad ?? "ok"})", binOk && binN == 300);

            // ---- CONDITION BIAS: the thing that makes it "good spoil %" / "low durability" -------------------
            //
            // ⚠ These tables exist only in code, so they have no row in any map's lootcond file -- which is exactly
            // how they would end up rolling uniform condition while looking wired. Asserted through the SAME
            // accessor the loot roller uses, after a map load has cleared the file-backed map.
            SDG.Unturned.LootCondition.Load("res://content/spawns/does_not_exist.txt");   // clears, as a map load does
            LootTables.ApplyVirtualBias();
            float fridgeBias = SDG.Unturned.LootCondition.Bias(LootTables.Fridge);
            float binBias = SDG.Unturned.LootCondition.Bias(LootTables.GarbageBag);
            GD.Print($"[loot-test] bias after a map load: fridge {fridgeBias:0.00}, garbage {binBias:0.00}");
            T.Check($"a fridge keeps its GOOD-condition bias across a map load ({fridgeBias:0.00})", fridgeBias > 0.4f);
            T.Check($"a garbage bag keeps its WORN bias across a map load ({binBias:0.00})", binBias < -0.4f);
            T.Check("control: an ordinary map table is still unbiased by default", Mathf.IsZeroApprox(SDG.Unturned.LootCondition.Bias(17)));

            // ---- CONTAINERS MAY SPAWN EMPTY ------------------------------------------------------------------
            StoreShelf.EmptyChanceForTests = null;   // ...and back to the REAL odds, which is what this part measures
            int empties = 0;
            for (int i = 0; i < 400; i++)
            {
                var box = new SDG.Unturned.Items(SDG.Unturned.PlayerInventory.STORAGE);
                box.resize(8, 6);
                StoreShelf.RollInto(box, 2, 5, LootTables.Dishwasher);
                if (box.getItemCount() == 0) empties++;
            }
            float frac = empties / 400f;
            GD.Print($"[loot-test] empty containers: {empties}/400 ({frac:P0})");
            // Bounded both ways: "sometimes empty" is a range, and a check that only tests one end passes just as
            // happily when EVERY container is empty.
            T.Check($"containers sometimes spawn empty ({empties}/400)", empties > 0);
            T.Check($"...but usually do not ({frac:P0} empty)", frac < 0.5f);

            yield break;
        }
    }
}
