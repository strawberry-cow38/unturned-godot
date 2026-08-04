using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // infAmmo (master: "add infAmmo command. refills your mag of the held weapon after 0.5s of not firing").
    //
    // The delay is the whole feature. A per-shot top-up would be trivially easy to write and would look identical
    // from a single screenshot of a full magazine -- so the assertions below are about WHEN it does not fill as much
    // as when it does: down inside the lull, up once the lull passes.
    //
    // Physics runs at 50Hz (project.godot physics_ticks_per_second=50), so the 0.5s threshold is 25 ticks and every
    // wait here is counted in ticks rather than seconds. Ticks() advances PHYSICS frames, which is the clock the
    // refill actually runs on -- Until() would be the wrong stepper.
    public sealed class InfiniteAmmoTests : GameTest
    {
        public override string Name => "gun.inf_ammo";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            PlayerController.InfiniteAmmo = false;   // never inherit the flag from whatever ran before

            var p = new PlayerController { CaptureMouse = false };
            World.AddChild(p);
            yield return Ticks(2);
            p.Inventory.wearBackpack(new Item(253));
            p.Inventory.items[0].tryAddItem(new Item(4));   // Eaglefire
            p.EquipHotbar(1);
            yield return Ticks(100);   // the pull-out animation gates firing for ~1.63s; 100 ticks = 2s clears it
            T.Check($"gun in hand ({p.HeldGunName})", p.HasGunOut);

            p.Ammo = 5;
            yield return Ticks(40);   // 0.8s, comfortably past the threshold

            // CONTROL FIRST. Without this the whole suite could pass because something else in the tick refills
            // magazines, and "infAmmo works" would be indistinguishable from "ammo never goes down".
            T.Check($"OFF: the mag stays down ({p.Ammo})", p.Ammo == 5);

            PlayerController.InfiniteAmmo = true;
            yield return Ticks(40);
            T.Check($"ON: the mag refills ({p.Ammo})", p.Ammo > 5);
            int full = p.Ammo;

            // Firing restarts the clock, so the refill must not land while you are still shooting.
            bool fired = p.Fire();
            yield return Ticks(2);
            T.Check($"the gun fires ({fired}, ammo {p.Ammo})", fired && p.Ammo < full);
            int afterShot = p.Ammo;

            yield return Ticks(10);   // ~0.24s since the shot: still inside the lull
            T.Check($"...and does NOT refill inside the lull ({p.Ammo} at {p.DebugSinceShot:0.00}s)", p.Ammo == afterShot);

            yield return Ticks(30);   // now ~0.84s since the shot
            T.Check($"...then refills once the lull passes ({p.Ammo} at {p.DebugSinceShot:0.00}s)", p.Ammo == full);

            // Sustained fire keeps resetting it, so the mag genuinely drains rather than being pinned full.
            int before = p.Ammo;
            for (int i = 0; i < 5; i++) { p.Fire(); yield return Ticks(6); }   // ~0.12s apart, each inside the lull
            T.Check($"sustained fire still drains the mag ({before} -> {p.Ammo})", p.Ammo < before);

            PlayerController.InfiniteAmmo = false;   // a leaked static would silently arm every later suite
            T.Check("flag left off for the suites after this one", !PlayerController.InfiniteAmmo);
        }
    }
}
