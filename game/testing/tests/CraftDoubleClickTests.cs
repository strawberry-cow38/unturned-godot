using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Double-click a recipe tile to craft one (strawberry 2026-10-05: "add double click grid tile to craft (if
    /// we can craft it)").
    ///
    /// Driven with REAL mouse events pushed through the viewport at the tile's centre, so the hit-test, the tile's
    /// Button and the engine's double-click flag are all in the path -- a seam that called the handler directly would
    /// pass with the handler wired to nothing. Three cases, because each separates a different wrong build: a single
    /// click must only SELECT (a handler that ignored the flag would craft on every click), a double-click on a recipe
    /// you can make must queue exactly ONE (not zero, not one per click), and a double-click on one you cannot make
    /// must queue nothing and still select it, so its reason line shows.
    ///
    /// ⚠ WHAT THIS CANNOT SEE: the OS's double-click timing. Headless has no pointer, so the flag is set on the event by
    /// hand; whether two real clicks are close enough to earn it is the platform's call, not ours.</summary>
    public sealed class CraftDoubleClickTests : GameTest
    {
        public override string Name => "craft.double_click";
        public override double TimeoutSimSeconds => 30;

        static void Click(Viewport vp, Vector2 at, bool dbl)
        {
            vp.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = dbl, Position = at, GlobalPosition = at }, true);
            vp.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at }, true);
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            BlueprintRegistry.ResetForTests();
            var idx = BlueprintRegistry.Index();

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            var craft = p.DebugCraftMenu;

            // the fixture: stock the ingredients of ONE recipe, and find one whose ingredients we then do NOT hold
            var can = idx[0];
            foreach (var ing in can.Inputs)
                if (Assets.findByGuid(ing.Guid) is ItemAsset ia) p.Inventory.tryAddItem(new Item(ia.id, (byte)Mathf.Clamp(ing.Amount * 4, 1, 255)));
            craft.Inv ??= p.Inventory;
            BlueprintDef cannot = null;
            foreach (var bp in idx) if (cannot == null && craft.CraftBlocker(bp, new Crafting.PlayerInvAdapter(p.Inventory), 1) != null) cannot = bp;
            T.Check($"fixture: {CraftingMenu.Title(can)} is craftable ({craft.CraftBlocker(can, new Crafting.PlayerInvAdapter(p.Inventory), 1) ?? "no blocker"}) and {(cannot != null ? CraftingMenu.Title(cannot) : "nothing")} is not",
                    craft.CraftBlocker(can, new Crafting.PlayerInvAdapter(p.Inventory), 1) == null && cannot != null);
            if (cannot == null) yield break;
            int consumed = can.Inputs.FindIndex(i => i.Consume);
            T.Check($"the craftable recipe eats something ({consumed})", consumed >= 0);
            if (consumed < 0) yield break;
            ushort inId = Assets.findByGuid(can.Inputs[consumed].Guid)?.id ?? 0;
            int per = can.Inputs[consumed].Amount;

            p.ShowMenu(MenuNavbar.Tab.Craft);
            yield return Ticks(30);   // past the swoop
            var vp = craft.GetViewport();

            // ---- 1. A SINGLE CLICK SELECTS AND DOES NOT CRAFT. Aimed at the uncraftable one first, so the selection
            // genuinely has to move (the grid opens with the first recipe already selected).
            var rCannot = craft.DebugTileRect(cannot);
            T.Check($"the uncraftable tile is in the grid ({rCannot})", rCannot.HasValue);
            if (!rCannot.HasValue) yield break;
            var rCan = craft.DebugTileRect(can).Value;
            int q0 = craft.DebugQueueCount, have0 = p.Inventory.getItemCount(inId);
            Click(vp, rCannot.Value.GetCenter(), false);
            yield return Ticks(2);
            T.Check($"single click on {CraftingMenu.Title(cannot)} selects it ({CraftingMenu.Title(craft.DebugSelected)})", ReferenceEquals(craft.DebugSelected, cannot));
            Click(vp, rCan.GetCenter(), false);
            yield return Ticks(2);
            T.Check($"single click on the craftable one selects it and queues nothing (queue {q0} -> {craft.DebugQueueCount}, {have0} -> {p.Inventory.getItemCount(inId)} in the bag)",
                    ReferenceEquals(craft.DebugSelected, can) && craft.DebugQueueCount == q0 && p.Inventory.getItemCount(inId) == have0);

            // ---- 2. A DOUBLE-CLICK ON ONE YOU CAN MAKE QUEUES EXACTLY ONE
            rCan = craft.DebugTileRect(can).Value;
            Click(vp, rCan.GetCenter(), false);   // a real double-click is a click, then a press that carries the flag
            yield return Ticks(1);
            Click(vp, craft.DebugTileRect(can).Value.GetCenter(), true);
            yield return Ticks(2);
            T.Check($"double-click queued one {CraftingMenu.Title(can)} (queue {q0} -> {craft.DebugQueueCount}; bag {have0} -> {p.Inventory.getItemCount(inId)}, one unit eats {per})",
                    craft.DebugQueueCount == q0 + 1 && p.Inventory.getItemCount(inId) == have0 - per);

            // ---- 3. A DOUBLE-CLICK ON ONE YOU CANNOT MAKE QUEUES NOTHING, AND STILL SELECTS IT
            int q1 = craft.DebugQueueCount;
            var r3 = craft.DebugTileRect(cannot).Value;
            Click(vp, r3.GetCenter(), false);
            yield return Ticks(1);
            Click(vp, craft.DebugTileRect(cannot).Value.GetCenter(), true);
            yield return Ticks(2);
            T.Check($"double-click on {CraftingMenu.Title(cannot)} queues nothing ({q1} -> {craft.DebugQueueCount}) and leaves it selected, reason showing",
                    craft.DebugQueueCount == q1 && ReferenceEquals(craft.DebugSelected, cannot));

            p.ShowMenu(MenuNavbar.Tab.Inventory);
            yield return Ticks(1);
        }
    }
}
