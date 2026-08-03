using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // "it shows in the grid, i can select it, but it wont actually move to the tile i drag it to. the drop button in
    // the inv does nothing" (strawberry, on a detached iron sight).
    //
    // The model layer is already proven clean by gun.attachment_item_behaves -- Inv.TryDrag moves the sight, drop
    // spawns a world item. So the fault is in the GESTURE, and there was no instrument for it: every existing
    // inventory test calls Inv.TryDrag directly, and the only real-gesture seam (DebugDropGestureOnSlot) covers
    // clothing. A drag the model accepts and the UI silently refuses fell straight through that gap.
    //
    // This drives the actual Drop() handler through its actual PointToCell hit-test, and runs a CONTROL item beside
    // the sight -- if both fail it is not the sight, and if neither fails the fault is further out still (input
    // events, or something only present in her session).
    public sealed class InventoryDragGestureTests : GameTest
    {
        public override string Name => "inv.drag_gesture_moves_item";

        // Move `id` one cell and report whether the GESTURE moved it. null = the harness could not run the gesture
        // (no layout), which must never be reported as a pass.
        static bool? DragOneCell(InventoryUI ui, PlayerInventory inv, ushort id, out string detail)
        {
            detail = "";
            byte page = 255, gx = 0, gy = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2) && page == 255; b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item?.id == id) { page = b; gx = j.x; gy = j.y; break; }
                }
            }
            if (page == 255) { detail = "not in any page"; return false; }

            var grid = inv.items[page];
            for (byte ty = 0; ty < grid.height; ty++)
                for (byte tx = 0; tx < grid.width; tx++)
                {
                    if (tx == gx && ty == gy) continue;
                    if (grid.getIndex(tx, ty) != byte.MaxValue) continue;
                    if (!ui.DebugDropGestureOnCell(page, gx, gy, page, tx, ty, out bool layoutOk))
                    {
                        if (!layoutOk) { detail = "no Control layout -- gesture unrunnable headless"; return null; }
                        detail = $"gesture refused ({gx},{gy} -> {tx},{ty})";
                        return false;
                    }
                    byte now = grid.getIndex(tx, ty);
                    bool landed = now != byte.MaxValue && grid.getItem(now)?.item?.id == id;
                    detail = landed ? $"moved ({gx},{gy} -> {tx},{ty})" : $"did NOT land at ({tx},{ty})";
                    return landed;
                }
            detail = "no free cell to move into";
            return false;
        }

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++) if (pg.getItem(i)?.item?.id == id) n++;
            }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            PlayerInventory inv = new PlayerInventory();
            inv.wearBackpack(new Item(253));
            var ui = new InventoryUI { Inv = inv };
            World.AddChild(ui);
            ui.Open();
            yield return Ticks(3);

            T.Check("a detached sight goes into the bag", inv.tryAddItem(new Item(5)));    // Eaglefire Iron Sights
            T.Check("...and a control item beside it", inv.tryAddItem(new Item(14)));      // Bottled Water
            yield return Ticks(2);

            bool? sight = DragOneCell(ui, inv, 5, out string sd);
            bool? ctrl = DragOneCell(ui, inv, 14, out string cd);

            if (sight == null || ctrl == null)
            {
                // The harness itself could not run. Reported as a real failure rather than skipped silently: an
                // untestable UI is the actual finding here, and a green run that tested nothing is how this bug
                // stayed invisible in the first place.
                T.Check($"HARNESS: the drag gesture is unrunnable in this environment (sight: {sd} / control: {cd})", false);
                yield break;
            }

            T.Check($"the CONTROL item moves by real gesture ({cd})", ctrl == true);
            T.Check($"the detached SIGHT moves by real gesture ({sd})", sight == true);
            // The discriminator strawberry's report needs: same gesture, two items, one difference.
            T.Check($"...and both behave the same (sight={sight}, control={ctrl})", sight == ctrl);

            // ---- THE REPORTED CONDITION: "its JUST these attachment items AFTER I TAKE THEM OFF A GUN."
            // Adding Item(5) to the bag directly (above) passes, so the difference has to be in the detach path
            // itself. Drive the real one: equip a gun, detach through the menu's own button, then drag what it gave.
            var p2 = new PlayerController { CaptureMouse = false };
            World.AddChild(p2);
            var menu = new AttachmentMenu();
            World.AddChild(menu);
            p2.AttachMenu = menu;
            yield return Ticks(2);
            // PlayerController._Ready builds its OWN PlayerInventory, so passing one into the initialiser is
            // silently discarded -- the first run of this test equipped nothing because the UI and the player were
            // holding different bags. Bind the UI to the player's bag AFTER _Ready instead.
            inv = p2.Inventory;
            inv.wearBackpack(new Item(253));
            ui.Inv = inv;
            ui.Refresh();
            yield return Ticks(2);

            inv.items[0].tryAddItem(new Item(4));            // Eaglefire into the primary slot
            p2.EquipHotbar(1);
            yield return Ticks(3);
            T.Check($"gun in hand ({p2.HeldGunName})", p2.HasGunOut);
            T.Check($"...seeded with its factory irons ({AttachmentFit.InstalledId(p2.HeldItemForTest, "Sight")})",
                AttachmentFit.InstalledId(p2.HeldItemForTest, "Sight") == 5);

            // A REAL Viewmodel: ToggleFan early-returns on VM == null (correctly -- the menu only opens over a gun),
            // so a null here builds no options and the test would "fail" on its own scaffolding.
            var vm = new Viewmodel { GunName = "eaglefire" };
            World.AddChild(vm);
            yield return Ticks(2);
            menu.VM = vm; menu.Player = p2;
            // The one structural difference left between this harness and her session: in game the UI has a Player
            // bound, so Drop() takes the `Player != null && Player.RequestMoveItem(...)` branch before the local
            // TryDrag. Bind it, or this tests a path she never uses.
            ui.Player = p2;
            int beforeDetach = CountOf(inv, 5);
            bool detached = menu.DebugDetach("Sight");
            yield return Ticks(2);
            int afterDetach = CountOf(inv, 5);
            T.Check($"the menu's Detach ran ({detached})", detached);
            T.Check($"...and put a sight in the bag ({beforeDetach} -> {afterDetach})", afterDetach == beforeDetach + 1);
            T.Check($"...and cleared the gun's slot ({AttachmentFit.InstalledId(p2.HeldItemForTest, "Sight")})",
                AttachmentFit.InstalledId(p2.HeldItemForTest, "Sight") == -1);

            ui.Refresh();
            yield return Ticks(2);
            bool? afterOff = DragOneCell(ui, inv, 5, out string ad);
            T.Check($"a sight taken OFF A GUN drags like any other item ({ad})", afterOff == true);
        }
    }
}
