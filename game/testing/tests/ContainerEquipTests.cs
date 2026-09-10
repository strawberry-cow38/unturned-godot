using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // TWO ASKS, ONE FILE, because both are about the same thing: which PAGE an item is on deciding what the player
    // is allowed to do with it (strawberry 2026-09-10, "allow dragging equipables from storage containers straight
    // onto the paper doll/primary/secondary slots" and "prevent items in containers being eligable for autodrink").
    //
    // Page-scope rules are invisible in a screenshot and cheap to get backwards, and both of these were ONE
    // comparison away from being wrong in opposite directions -- the equip gate refused a page it should allow,
    // the autodrink scan walked two pages it should not. So every check here is a pair: the same item, the same
    // gesture, differing only in the page it lives on.
    public sealed class ContainerEquipTests : GameTest
    {
        public override string Name => "inv.container_equip_and_autodrink";

        static ItemJar First(Items pg) => pg != null && pg.getItemCount() > 0 ? pg.getItem(0) : null;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var inv = new PlayerInventory();
            inv.wearBackpack(new Item(253));                 // Alicepack -> a bag page with room
            var ui = new InventoryUI { Inv = inv };
            World.AddChild(ui);
            ui.Open();
            yield return Ticks(3);

            // ---------- EQUIP: a rifle in an open container, dragged onto the paperdoll ----------
            var crate = inv.items[PlayerInventory.STORAGE];
            crate.loadSize(6, 6);
            T.Check("a rifle sits in the open container", crate.tryAddItem(new Item(4)));   // Eaglefire -> primary
            yield return Ticks(2);

            var cj = First(crate);
            int fromCrate = -1;
            bool laidOut = false;
            if (cj != null) fromCrate = ui.DebugDropGestureOnPaperdoll(PlayerInventory.STORAGE, cj.x, cj.y, out laidOut);
            if (!laidOut)
            {
                // Reported as a failure, never skipped: an unrunnable gesture is the finding. A green run that
                // exercised nothing is exactly how the drag bugs in this UI stayed invisible before.
                T.Check("HARNESS: the paperdoll drop gesture is unrunnable here (no Control layout)", false);
                yield break;
            }
            T.Check($"a rifle dragged out of a CONTAINER onto the paperdoll equips (slot {fromCrate})", fromCrate >= 0);
            T.Check("...and it actually left the container", First(crate) == null);

            // ---------- CONTROL: the ground is still not a valid source ----------
            // The fix opened one page, not the gate. If this ever equips, the check was deleted rather than
            // narrowed, and picking loot off the floor by waving it at the paperdoll became possible by accident.
            var area = inv.items[PlayerInventory.AREA];
            area.loadSize(6, 6);
            T.Check("a rifle sits on the ground (Nearby)", area.tryAddItem(new Item(363)));   // Maplestrike
            yield return Ticks(2);
            var aj = First(area);
            int fromArea = aj == null ? -1 : ui.DebugDropGestureOnPaperdoll(PlayerInventory.AREA, aj.x, aj.y, out _);
            T.Check($"...and dragging it onto the paperdoll does NOT equip it (slot {fromArea})", fromArea < 0);
            T.Check("...it is still lying on the ground", First(area) != null);

            // ---------- AUTODRINK: the page decides eligibility ----------
            // Bottle in the CRATE first, and nothing anywhere else. Same fill, same flag as the one below.
            var crate2 = inv.items[PlayerInventory.STORAGE];
            var inCrate = new Item(14);                       // Bottled Water
            FluidItem.Write(inCrate, FluidType.Water, 1000f, WaterQuality.Clean);
            inCrate.autoDrink = true;
            T.Check("a full, autodrink-ON bottle goes into the container", crate2.tryAddItem(inCrate));
            yield return Ticks(2);
            T.Check("a bottle in a CONTAINER is not drunk from", FluidItem.ActiveAutoDrink(inv) == null);

            // ...and the same bottle on the ground.
            var onFloor = new Item(14);
            FluidItem.Write(onFloor, FluidType.Water, 1000f, WaterQuality.Clean);
            onFloor.autoDrink = true;
            area.tryAddItem(onFloor);
            yield return Ticks(2);
            T.Check("a bottle on the GROUND is not drunk from either", FluidItem.ActiveAutoDrink(inv) == null);

            // ⭐ THE CONTROL THAT MAKES THE TWO ABOVE MEAN ANYTHING. Identical item, identical flags -- only the
            // page differs. Without this, ActiveAutoDrink returning null because the scan is broken outright, or
            // because Read/Safe rejected the fluid, would read as a pass twice over.
            var mine = new Item(14);
            FluidItem.Write(mine, FluidType.Water, 1000f, WaterQuality.Clean);
            mine.autoDrink = true;
            T.Check("the same bottle goes into MY bag", inv.tryAddItem(mine));
            yield return Ticks(2);
            T.Check("...and THAT one is drunk from", ReferenceEquals(FluidItem.ActiveAutoDrink(inv), mine));
            yield break;
        }
    }
}
