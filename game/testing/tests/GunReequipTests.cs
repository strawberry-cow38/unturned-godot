using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // "i also saw a bug where putting away a gun and pulling it out reloads it fully?" (strawberry)
    //
    // This is a REPRO ATTEMPT before it is a regression test. Reading the code, the mechanism looks correct --
    // SaveGunState writes Ammo onto the backing item, RestoreGunState reads it back -- so the interesting question is
    // which PATH loses it, and reading harder was never going to answer that. So: drive each way a gun can leave and
    // re-enter the hand and measure the ammo.
    //
    // The suspicious shape is RestoreGunState's early-out: `if (item == null || item.gunAmmo < 0) return;` leaves the
    // gun on LoadGun's fresh defaults, which is a FULL magazine. So any route that holsters without an item to write
    // to, or re-equips against an item that was never written, comes back full -- and looks exactly like a free reload.
    public sealed class GunReequipAmmoTests : GameTest
    {
        public override string Name => "gun.reequip_keeps_ammo";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);

            // An Eaglefire in the PRIMARY slot -- the hotbar-key path strawberry was using ("switching to the same
            // slot you currently have equipped will put away that item").
            var gunItem = new Item(4);
            p.Inventory.items[0].tryAddItem(gunItem);
            var stored = p.Inventory.items[0].getItem(0);
            T.Check("an eaglefire is in the primary slot", stored != null && stored.item != null);

            p.EquipHotbar(1);   // key 1 = the primary slot
            yield return Ticks(2);
            T.Check($"it equips ({p.HeldGunName}, {p.Ammo} rounds)", p.HasGunOut && p.Ammo > 0);

            // Fire it down. Set directly rather than simulating trigger pulls: the question is whether the ammo
            // SURVIVES a holster, not whether shooting decrements it (gun.mag_reload already covers that).
            p.Ammo = 7;
            int before = p.Ammo;

            // ---- ROUTE 1: the same-slot hotbar gesture -- press the slot you're already holding. ----
            p.EquipHotbar(1);   // key 1 = the primary slot
            yield return Ticks(2);
            T.Check("pressing the held slot puts the gun away", !p.HasGunOut);
            T.Check($"...and the item remembers what was left in it ({stored.item.gunAmmo})", stored.item.gunAmmo == before);

            p.EquipHotbar(1);   // key 1 = the primary slot
            yield return Ticks(2);
            T.Check($"pulling it back out keeps {before} rounds -- it does NOT reload itself ({p.Ammo})", p.Ammo == before);

            // ---- ROUTE 2: holster explicitly, then re-equip. ----
            p.Ammo = 3;
            p.EquipUnarmed();
            yield return Ticks(2);
            T.Check($"an explicit holster saves the ammo onto the item ({stored.item.gunAmmo})", stored.item.gunAmmo == 3);
            p.EquipHotbar(1);   // key 1 = the primary slot
            yield return Ticks(2);
            T.Check($"...and re-equipping restores 3, not a full magazine ({p.Ammo})", p.Ammo == 3);

            // ---- ROUTE 3: swap to ANOTHER gun and back. The outgoing gun's state is saved by the INCOMING equip,
            // which is a different call site to the two above and the one most likely to drop it.
            var second = new Item(363);   // Maplestrike, secondary slot
            p.Inventory.items[1].tryAddItem(second);
            var stored2 = p.Inventory.items[1].getItem(0);
            p.Ammo = 11;
            p.EquipHotbar(2);   // key 2 = the secondary slot   // -> maplestrike
            yield return Ticks(2);
            T.Check($"swapping to the other gun works ({p.HeldGunName})", p.HasGunOut && p.HeldGunName == "maplestrike");
            T.Check($"...and the eaglefire kept its 11 on the way out ({stored.item.gunAmmo})", stored.item.gunAmmo == 11);
            p.Ammo = 5;
            p.EquipHotbar(1);   // key 1 = the primary slot   // back to the eaglefire
            yield return Ticks(2);
            T.Check($"swapping back gives the eaglefire its 11 rounds ({p.Ammo})", p.Ammo == 11);
            T.Check($"...and the maplestrike kept its 5 ({stored2.item.gunAmmo})", stored2.item.gunAmmo == 5);

            // THE INVARIANT the three routes above rely on: a gun in your hands is ALWAYS a real inventory Item, and
            // that item is the single home for its ammo/firemode/mag/attachments. Asserted structurally rather than
            // trusted, because it is what makes this whole area work and it is one careless `EquipHeldGun(name)` away
            // from not being true.
            //
            // `EquipHeldGun(name, null)` DOES hand back a full magazine -- SaveGunState has nowhere to write and
            // RestoreGunState's `item == null` early-out leaves LoadGun's fresh defaults standing. I briefly "fixed"
            // that with a per-gun stash on the player before checking whether anything reaches it. Nothing does: every
            // production call site passes a backing item, and `give` puts a real Assets.makeLoot item in your bag. So
            // the stash was a second home for state guarding a path only a test could take, and it went back out.
            // Guard the invariant instead -- if a call site ever drops the item, THAT is the bug to fix.
            T.Check($"holding a gun means holding an ITEM ({(p.HeldItemForTest != null ? "yes" : "NO -- state has nowhere to live")})",
                p.HasGunOut && p.HeldItemForTest != null);
            T.Check($"...and it is the very item sitting in the slot, not a copy ({ReferenceEquals(p.HeldItemForTest, stored.item)})",
                ReferenceEquals(p.HeldItemForTest, stored.item));
        }
    }
}
