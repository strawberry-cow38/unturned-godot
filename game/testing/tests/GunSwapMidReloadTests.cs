using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // "putting away a gun and pulling it out reloads it fully?" -- strawberry, then, when I asked: "i only tested sp,
    // gun never left the pri/sec slots."
    //
    // That ruled out every route I had suspected (drop/pickup, containers, MP resync) and left exactly one difference
    // between her session and my passing re-equip test: she FIRES and RELOADS the gun, and my test assigned ammo.
    //
    // A reload is a TIMER. EquipHeldMelee clears it on the way out ("swapping off a gun mid-reload aborts it") and so
    // does EquipUnarmed -- but EquipHeldGun does not, so gun-to-gun was the one swap that left it running. The tick
    // then completes the reload against whatever is in your hands NOW and calls SaveGunState, which writes the full
    // magazine onto the NEW gun's item. Start a reload on your primary, tap 2, and your secondary fills itself.
    //
    // It presents as "pulling a gun out reloads it" because the gun you swapped TO is the one that ends up full, and
    // the reload that caused it belonged to the gun you put away.
    public sealed class GunSwapMidReloadTests : GameTest
    {
        public override string Name => "gun.swap_mid_reload";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);

            // A backpack with spare Military mags: the eaglefire reloads from mag ITEMS, so with none in the bag
            // StartReload dry-fires and returns instead of starting -- the reload has to be REAL for this to test
            // anything (first run of this test failed exactly there, on a player carrying no mags).
            p.Inventory.wearBackpack(new Item(253));          // Alicepack -> the backpack page has room
            var bag = p.Inventory.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new Item(6, 30));
            bag.tryAddItem(new Item(6, 30));

            p.Inventory.items[0].tryAddItem(new Item(4));     // Eaglefire  -> primary
            p.Inventory.items[1].tryAddItem(new Item(363));   // Maplestrike -> secondary
            var pri = p.Inventory.items[0].getItem(0);
            var sec = p.Inventory.items[1].getItem(0);

            // Both guns half spent, and both mirrored onto their items so the swap has real state to preserve.
            p.EquipHotbar(2);
            yield return Ticks(2);
            p.Ammo = 8; p.DebugSaveGunState();
            p.EquipHotbar(1);
            yield return Ticks(2);
            p.Ammo = 3; p.DebugSaveGunState();
            T.Check($"primary on 3, secondary stored on 8 ({p.Ammo}, {sec.item.gunAmmo})",
                p.Ammo == 3 && sec.item.gunAmmo == 8);

            // Start a REAL reload on the primary, then swap before it can finish. A whole-gun reload is ~1.6s, so a
            // couple of ticks is comfortably mid-flight.
            p.DebugStartReload();
            T.Check("a reload is running on the primary", p.DebugIsReloading);
            yield return Ticks(2);
            T.Check("...still running when we swap away", p.DebugIsReloading);

            p.EquipHotbar(2);   // tap 2 -- switch to the secondary mid-reload
            yield return Ticks(2);
            T.Check($"the secondary is out ({p.HeldGunName})", p.HasGunOut && p.HeldGunName == "maplestrike");

            // THE BUG: the abandoned reload keeps ticking and completes against the gun now in hand.
            T.Check("swapping guns ABORTS the reload -- it must not keep running on the new gun", !p.DebugIsReloading);

            // Let more than a full reload's worth of time pass, then check the secondary was not silently topped up.
            yield return Ticks(200);
            T.Check($"the secondary still has its own 8 rounds, not a free magazine ({p.Ammo})", p.Ammo == 8);
            T.Check($"...and nothing wrote a full magazine onto its item ({sec.item.gunAmmo})", sec.item.gunAmmo == 8);

            // The primary must be untouched too: an aborted reload leaves it as it was, to be reloaded properly later.
            T.Check($"the interrupted primary kept its 3 rounds ({pri.item.gunAmmo})", pri.item.gunAmmo == 3);
            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check($"...and comes back out on 3, not full ({p.Ammo})", p.Ammo == 3);

            // ---- THE LINK EVERY OTHER TEST HERE BYPASSES. The re-equip tests all set p.Ammo and then call
            // DebugSaveGunState, so they prove "a saved value survives a swap" -- they never prove the value gets
            // SAVED in the first place. A player never calls SaveGunState; they pull a trigger. If firing does not
            // mirror ammo onto the backing item, the item keeps a STALE (higher, or never-written -1) value and the
            // next re-equip restores THAT -- which is a free reload, arrived at from the other direction.
            // Fired through the real Fire(), not simulated, because the mirror lives at the end of that method and
            // an early return anywhere before it is exactly the failure being looked for.
            int fireStart = p.Ammo;
            T.Check($"a fired-from shot needs ammo to spend ({fireStart})", fireStart > 2);
            // Loop the trigger: Fire() is gated on the viewmodel's equip animation finishing (~1.6 s) and on the
            // per-shot cooldown, so a single call right after a swap legitimately does nothing. Same pattern the
            // destructible fire test uses.
            bool shot = false;
            for (int i = 0; i < 300 && !shot; i++) { shot = p.Fire(); yield return Ticks(1); }
            T.Check($"the shot went off ({fireStart} -> {p.Ammo})", shot && p.Ammo == fireStart - 1);
            T.Check($"...and firing MIRRORS the new count onto the item, with no explicit save ({pri.item.gunAmmo} vs {p.Ammo})",
                pri.item.gunAmmo == p.Ammo);

            // ...and that survives the round trip a player actually does: shoot, tap 2, tap 1.
            int afterShot = p.Ammo;
            p.EquipHotbar(2);
            yield return Ticks(2);
            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check($"shoot -> swap away -> swap back keeps the fired count ({afterShot} vs {p.Ammo})", p.Ammo == afterShot);
        }
    }
}
