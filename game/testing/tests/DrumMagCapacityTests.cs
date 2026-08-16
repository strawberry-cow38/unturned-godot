using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // The Military Drum (item 17) is a 100-round STANAG-group magazine whose capacity OVERRIDES the gun's Ammo_Max, so
    // the full 100 rounds actually load into a 30-round rifle (master: "the 100 rounds should apply to the actual gun").
    // The SAME rule leaves an ordinary mag gun-capped -- a plain STANAG in that rifle still tops out at 30 -- which is
    // the .45/.50 "gun caps the mag" niche, untouched. Both directions are pinned so the override can neither silently
    // become the blanket rule (which would evaporate that niche) nor silently do nothing (the bug master reported:
    // a 100-round drum that still only fed the rifle 30).
    public sealed class DrumMagCapacityTests : GameTest
    {
        public override string Name => "gun.drum_mag_capacity";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- THE ITEM WIRING: item 17 is a functioning 100-round STANAG-caliber mag, flagged to override the cap.
            var drum = Assets.find(17);
            T.Check($"the Military Drum (17) is a functioning magazine (cap {drum?.magCapacity})", drum != null && drum.IsMagazine);
            T.Check($"...100 rounds, STANAG caliber 1 (cap {drum?.magCapacity}, cal {drum?.magCaliber})",
                drum != null && drum.magCapacity == 100 && drum.magCaliber == 1);
            T.Check("...and flagged to OVERRIDE the gun's Ammo_Max (the whole point)", drum != null && drum.magOverridesCapacity);
            var stanag = Assets.find(6);
            T.Check($"the plain STANAG (6) is a mag but does NOT override (override={stanag?.magOverridesCapacity})",
                stanag != null && stanag.IsMagazine && !stanag.magOverridesCapacity);

            // ---- LIVE: a 30-round eaglefire reloading off the drum tops out at the drum's 100, not the rifle's 30.
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);
            p.Inventory.wearBackpack(new Item(253));                                     // Alicepack -> room for the drum
            p.Inventory.items[PlayerInventory.BACKPACK].tryAddItem(new Item(17, 100));   // a full 100-round drum in the bag
            p.Inventory.items[0].tryAddItem(new Item(4));                                // Eaglefire (STANAG rifle) -> primary
            p.EquipHotbar(1);
            yield return Ticks(2);
            T.Check($"the eaglefire's own Ammo_Max is 30 ({p.Gun?.AmmoMax})", p.Gun != null && p.Gun.AmmoMax == 30);
            p.Ammo = 0;                                                                  // empty -> the reload draws the drum in
            p.DebugCompleteReload();
            T.Check($"reloading off the drum loads the FULL 100, not the rifle's 30 ({p.Ammo})", p.Ammo == 100);
            T.Check($"...and the HUD capacity reads /100 ({p.MagCapacity})", p.MagCapacity == 100);

            // ---- CONTROL: the SAME rifle with a plain 30-round STANAG still caps at 30 (the gun-caps-mag niche holds).
            var q = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(q);
            yield return Ticks(2);
            q.Inventory.wearBackpack(new Item(253));
            q.Inventory.items[PlayerInventory.BACKPACK].tryAddItem(new Item(6, 30));
            q.Inventory.items[0].tryAddItem(new Item(4));
            q.EquipHotbar(1);
            yield return Ticks(2);
            q.Ammo = 0;
            q.DebugCompleteReload();
            T.Check($"a plain STANAG in the same rifle still caps at 30 ({q.Ammo})", q.Ammo == 30);
            T.Check($"...HUD capacity reads /30 for it ({q.MagCapacity})", q.MagCapacity == 30);
        }
    }
}
