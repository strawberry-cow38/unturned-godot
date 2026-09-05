using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // PRIMARY / SECONDARY HOLSTER RULES (strawberry 2026-08-16: "guns can only be sent to the hands if they are
    // in the 1/2 slots. some guns only fit in the primary, and not the secondary, but all secondary guns fit in
    // the primary as well as secondary. binding items 3-9 still works for the equip path of all non-guns").
    //
    // That is retail's ESlotType exactly, and the .dat files the port already ships carry the key -- eaglefire
    // and maplestrike are `Slot Primary`, colt is `Slot Secondary`. Nothing parsed it, so every item behaved as
    // NONE: any gun could be equipped from any bag cell, and a 3-9 bind on a backpack was a third weapon slot.
    //
    // The checks below read the DATA rather than hardcoding which gun is which, so they keep meaning something
    // when more guns are ported -- a test asserting "the colt is a sidearm" would pass forever even if the
    // parser stopped working, as long as I never changed the constant.
    public sealed class SlotRuleTests : GameTest
    {
        public override string Name => "inventory.slot_rules";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- 1. THE DATA PARSES. Without this every check below passes vacuously on NONE.
            // FOUND BY DATA, not by id. The first cut hardcoded the Colt as 81, which is not its id (97) -- so
            // it asserted against a NONE asset and "the parser is broken" and "I typed the wrong number" looked
            // identical. Scanning for the property under test also means this keeps working as guns are ported.
            ItemAsset rifle = null, sidearm = null;
            for (ushort id = 1; id < 2000 && (rifle == null || sidearm == null); id++)
            {
                var a = Assets.find(id);
                if (a?.gunName == null) continue;
                if (rifle == null && a.slot == ESlotType.PRIMARY) rifle = a;
                if (sidearm == null && a.slot == ESlotType.SECONDARY) sidearm = a;
            }
            T.Check($"at least one PRIMARY gun parsed out of the .dats ({rifle?.itemName})", rifle != null);
            T.Check($"at least one SECONDARY gun parsed out of the .dats ({sidearm?.itemName})", sidearm != null);
            if (rifle == null || sidearm == null) yield break;
            T.Check($"{rifle.itemName} is PRIMARY", rifle.slot == ESlotType.PRIMARY);
            T.Check($"{sidearm.itemName} is SECONDARY", sidearm.slot == ESlotType.SECONDARY);
            // A non-weapon must stay NONE, or binding food to 3-9 breaks.
            var food = Assets.find(13);
            T.Check($"a non-weapon stays NONE ({food?.itemName} {food?.slot})", food == null || food.slot == ESlotType.NONE);

            // ---- 2. THE RULES, as strawberry stated them.
            T.Check("a primary-only gun fits the primary", rifle.slot.CanEquipAsPrimary());
            T.Check("...and NOT the secondary", !rifle.slot.CanEquipAsSecondary());
            T.Check("a secondary gun fits the secondary", sidearm.slot.CanEquipAsSecondary());
            T.Check("...AND the primary, which is the asymmetry", sidearm.slot.CanEquipAsPrimary());
            T.Check("neither can be equipped straight from a bag page",
                !rifle.slot.CanEquipFromBag() && !sidearm.slot.CanEquipFromBag());
            T.Check("...while a non-weapon still can (3-9 binds keep working)",
                ESlotType.NONE.CanEquipFromBag());
            // Preferred slot: a sidearm holsters at the hip so it does not squat in the big slot by default.
            T.Check($"a sidearm prefers the secondary ({sidearm.slot.PreferredSlot()})", sidearm.slot.PreferredSlot() == 1);
            T.Check($"a rifle prefers the primary ({rifle.slot.PreferredSlot()})", rifle.slot.PreferredSlot() == 0);

            // ---- 3. THE BEHAVIOUR. Asserted on what ends up in the player's hands, not on the predicate --
            // the rule being right and the equip path consulting it are different claims.
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(4);
            p.EquipUnarmed();
            yield return Ticks(2);

            // A rifle sitting in a BAG page, bound to 3, must not reach the hands.
            var pockets = p.Inventory.items[2];
            p.Inventory.items[2].tryAddItem(new Item(rifle.id));
            byte bx = 255, by = 255;
            for (byte i = 0; i < pockets.getItemCount(); i++)
            { var j = pockets.getItem(i); if (j.item?.id == rifle.id) { bx = j.x; by = j.y; break; } }
            T.Check("the rifle is in a bag page", bx != 255);
            p.BindHotbar(3, 2, bx, by);
            p.EquipHotbar(3);
            yield return Ticks(2);
            T.Check($"a rifle in the BAG cannot be equipped from a 3-9 bind (held gun {p.HeldGunName ?? "none"})",
                !p.HasGunOut);

            // The same rifle in the PRIMARY slot equips fine.
            p.Inventory.items[0].addItem(0, 0, 0, new Item(rifle.id));
            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check($"...but the same rifle in the primary slot does ({p.HeldGunName ?? "none"})", p.HasGunOut);

            // ---- 4. PRESSING AN EMPTY SLOT PUTS IT AWAY.
            T.Check("holding something before the empty press", p.HasSomethingHeld);
            p.EquipHotbar(2);   // secondary is empty
            yield return Ticks(2);
            T.Check("pressing an EMPTY slot de-equips what was in hand", !p.HasGunOut);

            // ---- 5. REMOVING IT FROM ITS SLOT PULLS IT OUT OF YOUR HANDS.
            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check("re-equipped from the primary", p.HasGunOut);
            p.Inventory.items[0].removeItem(0);
            yield return Ticks(2);
            T.Check("emptying the primary slot de-equips the held gun", !p.HasGunOut);

            // ---- 6. THE HOTBAR ROW shows only what is actually there (strawberry: "providing theres something
            // in those slots"/"providing you have anything bound"). Asserted on the built row, not on the data
            // it reads: an empty hotbar and a hotbar that failed to build both show nothing on screen.
            // Re-stock the primary: section 5 deliberately emptied it, and the first cut of this section forgot
            // that and expected two cells against a one-cell row.
            p.Inventory.items[0].addItem(0, 0, 0, new Item(rifle.id));
            yield return Ticks(2);
            var hud = new HUD { Player = p };
            World.AddChild(hud);
            yield return Ticks(3);
            // The HUD's hotbar refresh is a 30 Hz RENDER-frame cadence (TickHub.Add from _Process, delta-accumulated), while
            // Ticks() advances PHYSICS steps. Alone, render frames interleave and the row is built inside three ticks; in the
            // full batch under load three physics ticks can pass with no 30 Hz render tick at all and the row reads 0 cells
            // (nightly 2026-09-05, "passes alone, fails in the batch", identical without any of the day's branches). The row
            // logic is what this test is about, so drive the tick the way tests drive p._Process(dt): directly.
            hud.HubTick(0.05);
            int cells = hud.DebugHotbarCells;
            T.Check($"a player holding a primary gun and one bind shows those entries ({cells})", cells == 2);

            p.Inventory.items[0].removeItem(0);   // empty the primary
            yield return Ticks(3);
            hud.HubTick(0.05);
            T.Check($"...emptying the primary drops it from the row ({hud.DebugHotbarCells})", hud.DebugHotbarCells == 1);

            p.HotbarBinds.Clear();
            yield return Ticks(3);
            hud.HubTick(0.05);
            T.Check($"...and with nothing slotted or bound the row is empty ({hud.DebugHotbarCells})", hud.DebugHotbarCells == 0);

            yield break;
        }
    }
}
