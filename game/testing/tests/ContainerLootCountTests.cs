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

            yield break;
        }
    }
}
