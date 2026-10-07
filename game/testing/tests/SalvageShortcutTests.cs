using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The Salvage entry on an item's right-click menu.
    ///
    /// Master 2026-10-07: "add salvage shortcuts to the rmb menu of salvagable inventory items."
    ///
    /// Three separate things can be broken here, and only one of them is the button:
    ///   1. the INDEX -- SalvageFor answers null for everything, and a menu with no Salvage button looks exactly
    ///      like an item that simply cannot be salvaged. So the count is asserted before anything is looked up,
    ///      and a material that must NOT be salvageable is asserted too: without that control a broken index that
    ///      says "no" to every question would pass half of this file.
    ///   2. the LAYOUT -- the panel was a fixed 300px and the buttons step 44 from y=150, so a sixth row rendered
    ///      past the bottom edge. A Panel does not clip, so it did not look like overflow; it looked like Close
    ///      had wandered onto the inventory behind it.
    ///   3. WHICH COPY GOES -- the craft queue spends the first copy it finds in page order. Right-click the
    ///      beaten-up one, press Salvage, and it takes the pristine one instead. The yield is identical, so
    ///      nothing errors and nothing looks wrong; you are just quietly poorer. The fixture below puts the GOOD
    ///      copy first on purpose, so first-found is the wrong answer and this test fails if the preference is
    ///      ever dropped.</summary>
    public class SalvageShortcut : GameTest
    {
        public override string Name => "craft.salvage_shortcut";
        public override double TimeoutSimSeconds => 30;

        const ushort Scrap = 67;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            BlueprintRegistry.Load();
            yield return Ticks(1);

            // ---- 1. THE INDEX, and a control it must answer "no" to -----------------------------------------
            T.Check($"the salvage index is populated ({BlueprintRegistry.SalvageCount} recipes)",
                    BlueprintRegistry.SalvageCount > 20);
            // Metal Scrap is what salvaging PRODUCES. If it had a recipe of its own the sweep would be matching on
            // something it should not, and scrap -> scrap would be an infinite supply.
            T.Check("control: Metal Scrap itself has no salvage recipe", BlueprintRegistry.SalvageFor(Scrap) == null);

            // The fixture is DERIVED, not named: "a salvageable thing that carries condition" is the case that
            // matters for check 3, and hardcoding one helmet means this test silently stops covering it the day
            // that helmet's recipe changes. The chosen item is printed so a failure says what it was measuring.
            ItemAsset pick = null;
            var sorted = new List<ItemAsset>(Assets.all());
            sorted.Sort((a, b) => a.id.CompareTo(b.id));
            foreach (var a in sorted)
                if (a != null && BlueprintRegistry.SalvageFor(a.id) != null && Durability.HasCondition(a)
                    && a.size_x <= 2 && a.size_y <= 2) { pick = a; break; }
            T.Check($"found a salvageable item that carries condition ({pick?.itemName ?? "none"} #{pick?.id})", pick != null);
            if (pick == null) yield break;

            var bp = BlueprintRegistry.SalvageFor(pick.id);
            T.Check($"...its recipe consumes the item itself and yields {bp.Outputs.Count} material(s)",
                    bp.Inputs.Count == 1 && bp.Inputs[0].Consume && Assets.findByGuid(bp.Inputs[0].Guid)?.id == pick.id
                    && bp.Outputs.Count > 0);

            // ---- 1b. A SALVAGE IS NOT A DYE, and is kept out of the lists that mean "what can I make" ---------
            // ⚠ THIS IS WHERE IT WENT WRONG ONCE. IsRecolour took the OWNER item for the recipe's output, and a
            // salvage recipe's owner is its INGREDIENT -- so owner == input, the names matched trivially, and all
            // 800 landed in "Dyes". Nothing threw; the craft menu just said "36 shown" out of 837 and salvage was
            // nowhere. Asserted on a real recipe rather than on the predicate in the abstract.
            T.Check($"a salvage recipe is not classified as a recolour ({bp.Name})", !BlueprintRegistry.IsRecolour(bp));
            T.Check("...and the registry knows it IS a salvage", BlueprintRegistry.IsSalvage(bp));
            int dyes = 0, salv = 0;
            foreach (var r in BlueprintRegistry.Index())
            { if (BlueprintRegistry.IsSalvage(r)) salv++; else if (BlueprintRegistry.IsRecolour(r)) dyes++; }
            T.Check($"the catalog's salvage recipes are not sitting in the dye pile ({salv} salvage, {dyes} dyes)",
                    salv > 20 && dyes < salv);

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var inv = p.Inventory; var ui = p.DebugInvUI; var craft = p.DebugCraftMenu;

            // ⚠ THE PRISTINE COPY GOES IN FIRST, so it is the one first-found would take. That ordering IS the
            // test: with the fixture the other way round, a Salvage that ignored the clicked jar would still
            // happen to remove the right item and this file would pass while broken.
            inv.tryAddItem(new Item(pick.id, 1, 100));
            inv.tryAddItem(new Item(pick.id, 1, 8));
            T.Check($"fixture: two {pick.itemName}, and the 100% one is first-found ({inv.peekItem(pick.id)?.quality ?? 0}%)",
                    inv.getItemCount(pick.id) == 2 && inv.peekItem(pick.id)?.quality == 100);

            // locate the BEATEN-UP one
            byte page = 255, cx = 0, cy = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES && page == 255; b++)
                for (byte i = 0; i < inv.items[b].getItemCount(); i++)
                {
                    var j = inv.items[b].getItem(i);
                    if (j?.item != null && j.item.id == pick.id && j.item.quality == 8) { page = b; cx = j.x; cy = j.y; break; }
                }
            T.Check("found the 8% copy in the bag", page != 255);
            if (page == 255) yield break;

            // ---- 2. THE MENU OFFERS IT, AND THE PANEL HOLDS IT ----------------------------------------------
            var acts = ui.DebugSelectionActions(page, cx, cy, out float panelH, out float lastBottom);
            T.Check($"the right-click menu offers Salvage ({string.Join("/", acts)})", acts.Contains("Salvage"));
            T.Check($"...and every button fits inside the panel (last ends {lastBottom:0}, panel {panelH:0})",
                    lastBottom <= panelH);

            // the CONTROL for the button: metal scrap is in the bag, and its menu must NOT offer Salvage
            inv.tryAddItem(new Item(Scrap, 5));
            var sj = FindJar(inv, Scrap);
            T.Check("control fixture: metal scrap is in the bag", sj.page != 255);
            if (sj.page != 255)
            {
                var sacts = ui.DebugSelectionActions(sj.page, sj.x, sj.y, out float sh, out float sb);
                T.Check($"control: an unsalvageable item's menu has no Salvage ({string.Join("/", sacts)})",
                        !sacts.Contains("Salvage"));
                T.Check($"...and its buttons fit too (last ends {sb:0}, panel {sh:0})", sb <= sh);
            }

            // ---- 2b. THE TALL CASE, which is the one the fixed height broke -----------------------------------
            // ⚠ WITHOUT THIS THE FIT CHECK ABOVE IS VACUOUS. That menu is three rows (Salvage/Drop/Close) and three
            // rows fit in the old 300px, so it would have passed with the bug still in. Opening a crate adds Store,
            // and FOUR rows is where the last button used to render past the bottom edge. Asserted by MEASURING the
            // panel against its own buttons rather than against a number, so it stays true if a row is ever added.
            inv.items[PlayerInventory.STORAGE].resize(6, 4);   // a crate is open
            var tall = ui.DebugSelectionActions(page, cx, cy, out float tallH, out float tallBottom);
            T.Check($"a crate being open adds a row ({string.Join("/", tall)})",
                    tall.Contains("Salvage") && tall.Contains("Store") && tall.Count >= 4);
            T.Check($"...and the panel grew to hold it (last ends {tallBottom:0}, panel {tallH:0})", tallBottom <= tallH);
            T.Check($"...having actually needed to grow past the old fixed 300 ({tallBottom:0} > 300)", tallBottom > 300f);
            inv.items[PlayerInventory.STORAGE].resize(0, 0);   // crate closed again: step 3 must not quick-transfer
            ui.DebugSelectionActions(page, cx, cy, out _, out _);

            // ---- 2c. AND IT IS NOT IN THE "WHAT CAN I MAKE" LISTS --------------------------------------------
            p.ShowMenu(MenuNavbar.Tab.Craft);
            yield return Ticks(2);
            T.Check($"the craft menu files it under Salvage, not Dyes ({craft.DebugCategoryOf(bp)})",
                    craft.DebugCategoryOf(bp) == "Salvage");
            T.Check($"...so browsing All does not show it ({craft.DebugView().Count} shown)", !craft.DebugView().Contains(bp));
            craft.Close();
            yield return Ticks(2);

            // ---- 3. IT SPENDS THE COPY YOU CLICKED ----------------------------------------------------------
            int queued = craft.DebugQueueCount;
            ui.DebugSalvage(page, cx, cy);
            yield return Ticks(2);
            T.Check($"Salvage queued a craft job ({craft.DebugQueueCount - queued})", craft.DebugQueueCount == queued + 1);
            var left = inv.peekItem(pick.id);
            T.Check($"one {pick.itemName} is left ({inv.getItemCount(pick.id)})", inv.getItemCount(pick.id) == 1);
            T.Check($"...and it is the PRISTINE one, not the 8% one ({left?.quality ?? 0}%)", left != null && left.quality == 100);

            yield break;
        }

        static (byte page, byte x, byte y) FindJar(PlayerInventory inv, ushort id)
        {
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
                for (byte i = 0; i < inv.items[b].getItemCount(); i++)
                {
                    var j = inv.items[b].getItem(i);
                    if (j?.item != null && j.item.id == id) return (b, j.x, j.y);
                }
            return (255, 0, 0);
        }
    }
}
