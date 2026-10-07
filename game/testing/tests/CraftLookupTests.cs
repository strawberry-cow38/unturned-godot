using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>U / R over an item in the bag (strawberry 2026-10-04: "hovering over an inventory item and pressing 'U'
    /// will switch to the crafting menu, filtered to recipes that can be crafted with the item you 'U'd. hovering and
    /// pressing 'r' will show the recipe(s) to MAKE the item you hovered over").
    ///
    /// Run on the SHIPPED catalog, not the retail fixture: log -> plank -> stick, with a saw as the unconsumed tool.
    /// That small set happens to separate every claim. A plank is MADE by one recipe and USED by one, out of six, so
    /// a filter that did nothing (six) or answered the other question (the other one) both fail; the saw is used by
    /// all six but only as a TOOL, so dropping tools from "uses" reads zero.
    ///
    /// ⚠ WHAT THIS CANNOT SEE: where a real cursor is. Headless has no pointer, so the "hover" is DebugCellPoint's
    /// mid-cell point fed to the same resolve-and-switch the key runs (PointToCell -> the jar -> ShowCraftingLookup).
    /// The key itself is exercised in section 5, through _Input, on the one branch a pointer is not needed for.</summary>
    public sealed class CraftLookupTests : GameTest
    {
        public override string Name => "craft.item_lookup";
        public override double TimeoutSimSeconds => 30;

        static ushort IdOf(string guid) => Assets.findByGuid(guid)?.id ?? 0;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();          // the shipped catalog, whatever an earlier test loaded
            var idx = BlueprintRegistry.Index();
            T.Check($"the shipped catalog indexes recipes ({idx.Count})", idx.Count >= 2);

            // ---- the fixture, DERIVED from the catalog rather than hard-coded: an item that some recipe MAKES and some
            // other recipe CONSUMES (a plank), and an input that is never consumed (the saw).
            string midGuid = null, toolGuid = null;
            foreach (var a in idx)
                foreach (var o in a.Outputs)
                    foreach (var b in idx)
                        foreach (var i in b.Inputs)
                            if (midGuid == null && i.Consume && i.Guid == o.Guid) midGuid = o.Guid;
            foreach (var bp in idx) foreach (var i in bp.Inputs) if (toolGuid == null && !i.Consume) toolGuid = i.Guid;
            ushort mid = midGuid != null ? IdOf(midGuid) : (ushort)0, tool = toolGuid != null ? IdOf(toolGuid) : (ushort)0;
            T.Check($"found an intermediate item (made by one recipe, eaten by another): {Assets.find(mid)?.itemName ?? "none"}", mid != 0);
            T.Check($"found a tool input: {Assets.find(tool)?.itemName ?? "none"}", tool != 0);
            if (mid == 0 || tool == 0) yield break;

            // the expected answers, counted a SECOND way -- by GUID over the raw index -- not by the menu's own predicate
            int usesMid = 0, makesMid = 0, usesTool = 0;
            foreach (var bp in idx)
            {
                bool u = false, m = false, t = false;
                foreach (var i in bp.Inputs) { if (i.Guid == midGuid) u = true; if (i.Guid == toolGuid) t = true; }
                foreach (var o in bp.Outputs) if (o.Guid == midGuid) m = true;
                if (u) usesMid++; if (m) makesMid++; if (t) usesTool++;
            }
            T.Check($"the fixture discriminates: {usesMid} use it, {makesMid} make it, {idx.Count} in all",
                    usesMid > 0 && makesMid > 0 && usesMid < idx.Count && makesMid < idx.Count);

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var ui = p.DebugInvUI; var craft = p.DebugCraftMenu;
            T.Check("the player has an inventory screen and a crafting menu", ui != null && craft != null);
            if (ui == null || craft == null) yield break;

            var hands = p.Inventory.items[2];
            T.Check("seeded the intermediate item", hands.tryAddItem(new Item(mid)));
            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(6);
            var jar = hands.getItem(0);
            if (!ui.DebugCellPoint(2, jar.x, jar.y, out Vector2 at))
            {
                T.Check("UNRUNNABLE: the hands grid has no layout headless", false);
                yield break;
            }

            // ---- 1. U: the recipes that USE it.
            T.Check("U over the item is handled", ui.DebugLookupAt(CraftingMenu.ItemLookup.Uses, at));
            yield return Ticks(2);
            T.Check($"...and it SWITCHED: crafting open {craft.IsOpen}, inventory open {ui.IsOpen}", craft.IsOpen && !ui.IsOpen);
            var view = craft.DebugView();
            bool allUse = view.Count > 0; foreach (var bp in view) if (!CraftingMenu.UsesItem(bp, mid)) allUse = false;
            T.Check($"U lists exactly the recipes that use it ({view.Count} shown, {usesMid} by GUID count, {craft.Lookup})",
                    craft.Lookup == CraftingMenu.ItemLookup.Uses && craft.LookupItemId == mid && view.Count == usesMid && allUse);

            // ---- 2. R: the recipes that MAKE it -- a different set, not the same one under a new label.
            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(4);
            T.Check("R over the same item is handled", ui.DebugLookupAt(CraftingMenu.ItemLookup.Recipes, at));
            yield return Ticks(2);
            view = craft.DebugView();
            bool allMake = view.Count > 0; foreach (var bp in view) if (!CraftingMenu.MakesItem(bp, mid)) allMake = false;
            T.Check($"R lists exactly the recipes that make it ({view.Count} shown, {makesMid} by GUID count)",
                    craft.IsOpen && craft.Lookup == CraftingMenu.ItemLookup.Recipes && view.Count == makesMid && allMake);
            T.Check($"...and says what it is filtered to (\"{craft.DebugHeader}\")",
                    craft.DebugHeader.Contains(Assets.find(mid)?.itemName ?? "?"));

            // ---- 3. OPENING CRAFTING NORMALLY DROPS THE LOOKUP. A lookup that stuck would leave Y showing two recipes forever.
            p.ShowMenu(MenuNavbar.Tab.Craft);
            yield return Ticks(2);
            T.Check($"Y / the tab opens the ordinary view ({craft.Lookup}, {craft.DebugView().Count} shown of {idx.Count})",
                    craft.Lookup == CraftingMenu.ItemLookup.None && craft.DebugView().Count > usesMid);

            // ---- 4. A TOOL COUNTS AS A USE, and NOTHING UNDER THE CURSOR IS NOT A LOOKUP.
            T.Check("seeded the tool", hands.tryAddItem(new Item(tool)));
            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(6);
            ItemJar toolJar = null;
            for (byte i = 0; i < hands.getItemCount(); i++) if (hands.getItem(i).item.id == tool) toolJar = hands.getItem(i);
            if (toolJar != null && ui.DebugCellPoint(2, toolJar.x, toolJar.y, out Vector2 tat))
            {
                ui.DebugLookupAt(CraftingMenu.ItemLookup.Uses, tat);
                yield return Ticks(2);
                T.Check($"U on the tool lists every recipe it is a tool in ({craft.DebugView().Count} shown, {usesTool} by GUID)",
                        craft.DebugView().Count == usesTool && usesTool > 0);
            }
            else T.Check("UNRUNNABLE: could not place the tool", false);

            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(4);
            T.Check("over empty space the key is NOT handled", !ui.DebugLookupAt(CraftingMenu.ItemLookup.Uses, new Vector2(-500f, -500f)));
            yield return Ticks(1);
            T.Check($"...and nothing switched (inventory {ui.IsOpen}, crafting {craft.IsOpen})", ui.IsOpen && !craft.IsOpen);

            // ---- 5. THE KEY, THROUGH _Input: R while CARRYING something still rotates it and does not look anything up.
            // This is the branch the new binding shares a letter with, and it has to win.
            T.Check("picked the item up", ui.DebugStartDrag(2, jar.x, jar.y) && ui.DebugIsDragging);
            byte rot0 = ui.DebugDragRot;
            ui._Input(new InputEventKey { Pressed = true, Keycode = Key.R, PhysicalKeycode = Key.R });
            yield return Ticks(1);
            T.Check($"R while dragging rotated ({rot0} -> {ui.DebugDragRot}) and stayed in the bag (crafting {craft.IsOpen})",
                    ui.DebugDragRot != rot0 && !craft.IsOpen && ui.IsOpen);
            ui.DebugRmbCancel();

            // ---- 6. THE BINDINGS: U and R by default, bag-only, so R sharing with Reload is not a conflict.
            T.Check($"U is the default for ItemUses ({Keybinds.Default(GameAction.ItemUses).Label})", Keybinds.Default(GameAction.ItemUses).Key == Key.U);
            T.Check($"R is the default for ItemRecipes ({Keybinds.Default(GameAction.ItemRecipes).Label})", Keybinds.Default(GameAction.ItemRecipes).Key == Key.R);
            T.Check($"...and R is legal beside Reload (conflict: {Keybinds.ConflictWith(Keybinds.Default(GameAction.ItemRecipes), GameAction.ItemRecipes)?.ToString() ?? "none"})",
                    Keybinds.ConflictWith(Keybinds.Default(GameAction.ItemRecipes), GameAction.ItemRecipes) == null);

            p.ShowMenu(MenuNavbar.Tab.Inventory);
            BlueprintRegistry.ResetForTests();
        }
    }
}
