using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // A GUN'S MAGAZINE REFILLS ITSELF WHEN YOU MOVE THE GUN (strawberry: "sometimes ammo magically refills into
    // guns after some combo of equip/dequip/moving around between primary slot/inv").
    //
    // gun.reequip_keeps_ammo already covers holster -> re-equip and passes, because it drives a bare
    // PlayerController where SaveGunState's write and RestoreGunState's read hit the SAME Item object. The game
    // does not run that path: singleplayer goes through the loopback server, the server owns the grid, and a
    // move is a REQUEST -- the client does not touch its own grid, it waits for the owner echo and repaints from
    // it. AdoptReplicatedInventory then replaces every jar with a fresh Item built by ReadJar.
    //
    // gunAmmo is on the wire and round-trips correctly, so ItemWireCompletenessTests is green. What no wire test
    // can see is that nothing ever populates the SERVER's copy: gunAmmo is written by SaveGunState (client only)
    // and no client->server command carries it, so every gun in the server's grid holds the -1 default forever.
    // The echo is therefore faithfully transmitting -1, and RestoreGunState reads that as "no saved state" and
    // leaves LoadGun's fresh defaults standing -- a full magazine.
    //
    // The CONTROL leg matters as much as the failing one: dequip/re-equip with NO move must keep the ammo. That
    // is what pins the stimulus on the move rather than on the equip cycle, and it is the leg that would still
    // pass if someone "fixed" this by making RestoreGunState refuse to default.
    public sealed class ItemStateMoveLoopbackTests : GameTest
    {
        public override string Name => "net.item_state_survives_move";
        public override double TimeoutSimSeconds => 60;

        const int FiredDownTo = 7;   // a count no gun's Ammo_Max is, so a refill can never be mistaken for a pass

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            ItemAsset rifle = null;
            for (ushort id = 1; id < 2000 && rifle == null; id++)
            {
                var a = Assets.find(id);
                if (a?.gunName != null && a.slot == ESlotType.PRIMARY) rifle = a;
            }
            T.Check($"a PRIMARY gun exists to test with ({rifle?.itemName})", rifle != null);
            if (rifle == null) yield break;

            var net = new MemNetwork(20260826);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "gunner" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv)) { T.Check("server owns an inventory for the joiner", false); yield break; }

            sInv.Inventory.items[0].addItem(0, 0, 0, new Item(rifle.id));
            sInv.Inventory.items[2].clear();
            sInv.Inventory.items[2].raiseStateUpdated();

            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);
            T.Check("the client adopted the rifle into its primary slot", sess.Shell.Inventory.items[0].getItemCount() == 1);
            T.Check("the shell's bag is server-owned (this suite is meaningless otherwise)", sess.Shell.InventoryIsServerOwned);

            // ---- CONTROL: fire it down, holster, take it back out. No move anywhere. ----
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"the rifle is in hand ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);
            if (!sess.Shell.HasGunOut) yield break;

            int fullMag = sess.Shell.Ammo;
            T.Check($"a freshly equipped gun starts full ({fullMag})", fullMag > FiredDownTo);

            sess.Shell.Ammo = FiredDownTo;      // stand-in for having fired; SaveGunState is what a real shot rides on
            sess.Shell.DebugSaveGunState();
            sess.Shell.EquipUnarmed();
            yield return Ticks(2);
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"(control) holster + re-equip with NO move keeps the ammo ({sess.Shell.Ammo} of {fullMag})",
                sess.Shell.Ammo == FiredDownTo);

            // ---- THE BUG: same gun, same ammo, but move it out of the slot and back. ----
            sess.Shell.Ammo = FiredDownTo;
            sess.Shell.DebugSaveGunState();
            sess.Shell.EquipUnarmed();
            yield return Ticks(2);

            var beforeMove = sess.Shell.Inventory.items[0].getItem(0)?.item;
            T.Check($"the gun's item carries the saved ammo before any move ({beforeMove?.gunAmmo})",
                beforeMove != null && beforeMove.gunAmmo == FiredDownTo);
            if (beforeMove == null) yield break;
            // Ammo is the field strawberry noticed, but it is not the only one with this shape. NINE fields on
            // Item are written only by the client (SaveGunState + AttachmentFit) and have no server-side writer
            // at all, so the server's copy of each is a permanent default. Recorded HERE, from whatever the real
            // equip path stamped, so the post-move check compares against reality rather than a guessed value.
            int seededSight = beforeMove.gunSightId, seededAttach = beforeMove.gunAttach;
            bool seeded = beforeMove.gunAttachSeeded;
            int savedMag = beforeMove.gunMagId, savedMode = beforeMove.gunFiremode;
            T.Check($"(setup) equipping seeded the gun's factory attachments (seeded={seeded}, sight={seededSight})", seeded);

            // Name the mechanism the pass depends on. Without this the suite is satisfied by ANY route that
            // happens to preserve the ammo -- including RebindHeldRefs, which re-stamps the HELD item and would
            // quietly cover a broken fix if the gun were still in hand (it is not: EquipUnarmed ran above).
            long statesBefore = ded.Server.Transactions.Diag.GunStatesApplied;
            T.Check("slot -> bag move request reached the wire", sess.Shell.RequestMoveItem(0, 0, 0, 2, 0, 0, 0));
            yield return Until(() => ded.Server.Transactions.Diag.GunStatesApplied > statesBefore, 5);
            T.Check($"the SERVER adopted the client's gun state before the move ({ded.Server.Transactions.Diag.GunStatesApplied - statesBefore})",
                ded.Server.Transactions.Diag.GunStatesApplied > statesBefore);
            yield return Until(() => sess.Shell.Inventory.items[2].getItemCount() == 1, 5);
            T.Check("the server moved it into the bag and the client adopted that",
                sess.Shell.Inventory.items[2].getItemCount() == 1 && sess.Shell.Inventory.items[0].getItemCount() == 0);

            var inBag = sess.Shell.Inventory.items[2].getItem(0)?.item;
            T.Check($"THE ONE THAT MATTERS: the ammo survived the move into the bag ({inBag?.gunAmmo}, want {FiredDownTo})",
                inBag != null && inBag.gunAmmo == FiredDownTo);
            // The blast radius: the same echo owns every other client-only field. If ammo is the only thing that
            // comes back wrong then the cause is ammo-specific; if these fail alongside it the cause is the
            // echo, and an ammo-only fix would leave the rest of them broken and unwatched.
            T.Check($"...and so did the fitted attachments (sight {inBag?.gunSightId} want {seededSight}, mask {inBag?.gunAttach} want {seededAttach})",
                inBag != null && inBag.gunSightId == seededSight && inBag.gunAttach == seededAttach && inBag.gunAttachSeeded == seeded);
            T.Check($"...and the loaded mag + fire mode (mag {inBag?.gunMagId} want {savedMag}, mode {inBag?.gunFiremode} want {savedMode})",
                inBag != null && inBag.gunMagId == savedMag && inBag.gunFiremode == savedMode);

            T.Check("bag -> slot move request reached the wire", sess.Shell.RequestMoveItem(2, 0, 0, 0, 0, 0, 0));
            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);
            T.Check("the server moved it back into the slot", sess.Shell.Inventory.items[0].getItemCount() == 1);

            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"and equipping it again does NOT hand back a full magazine ({sess.Shell.Ammo} of {fullMag}, want {FiredDownTo})",
                sess.Shell.Ammo == FiredDownTo);

            // ---- SWAPPING between two guns, which is where the flush address is easy to get wrong ----
            // EquipFromLocation calls NoteHeldFrom(NEW cell) BEFORE EquipItemAsset, and EquipHeldGun's first act
            // is SaveGunState() on the OUTGOING gun. Anything that reads _heldPage at that instant pairs the
            // outgoing gun's magazine with the incoming gun's address -- so the outgoing gun's state is sent to
            // the wrong cell (rejected on the id check, if you are lucky) and never reaches the server at all.
            // The single-gun legs above cannot see this: they holster to fists, where no new address is recorded.
            ItemAsset sidearm = null;
            for (ushort id = 1; id < 2000 && sidearm == null; id++)
            {
                var a = Assets.find(id);
                if (a?.gunName != null && a.slot == ESlotType.SECONDARY) sidearm = a;
            }
            T.Check($"a SECONDARY gun exists to swap to ({sidearm?.itemName})", sidearm != null);
            if (sidearm != null)
            {
                sInv.Inventory.items[1].addItem(0, 0, 0, new Item(sidearm.id));
                yield return Until(() => sess.Shell.Inventory.items[1].getItemCount() == 1, 5);
                T.Check("(setup) the sidearm reached the secondary slot", sess.Shell.Inventory.items[1].getItemCount() == 1);

                // The rifle is ALREADY in hand from the leg above -- do NOT press 1 again. EquipFromLocation
                // treats a press on the held item's own slot as "put it away" (strawberry's toggle), so the
                // re-equip I first wrote here holstered to fists and the save below had no gun to save.
                const int RifleAmmo = 13;
                T.Check($"(gate) the rifle is still in hand for the swap ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);
                sess.Shell.Ammo = RifleAmmo;
                sess.Shell.DebugSaveGunState();
                T.Check($"the save queued a send ({sess.Shell.DebugGunStatePending})", sess.Shell.DebugGunStatePending);
                long beforeSwap = ded.Server.Transactions.Diag.GunStatesApplied;
                long rejBefore = ded.Server.Transactions.Diag.GunStatesRejected;

                sess.Shell.EquipHotbar(2);          // THE SWAP -- rifle out, sidearm in
                // AND the sidearm saves in the SAME FRAME, with no tick in between. This is not a contrived
                // ordering -- swapping weapons and firing the new one inside a quarter second is ordinary play --
                // but it has to be forced here, because there is exactly ONE pending-send slot and the coalescing
                // timer is otherwise free to drain it before the collision happens. With no tick between the two
                // saves no flush can have run, so the outgoing rifle's magazine survives only if a save for a
                // different gun pushes the pending one out first.
                sess.Shell.Ammo = 3;
                sess.Shell.DebugSaveGunState();     // the sidearm now claims the pending slot
                yield return Ticks(6);
                long applied = ded.Server.Transactions.Diag.GunStatesApplied - beforeSwap;
                long rejected = ded.Server.Transactions.Diag.GunStatesRejected - rejBefore;
                // BOTH guns' states have to land, and NEITHER may be rejected. A rejection here is the specific
                // symptom of sending a gun's state to another gun's address, which is what reading _heldPage at
                // save time does -- and it is silent in the game, because the sender never hears about it.
                T.Check($"both guns' states reached the server across the swap (+{applied})", applied >= 2);
                T.Check($"and nothing was sent to the wrong address (+{rejected} rejected)", rejected == 0);
                T.Check($"(stimulus) the sidearm is in hand ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);

                // Round-trip the rifle through the bag so an echo rebuilds it. Nothing here touches the sidearm.
                T.Check("rifle slot -> bag", sess.Shell.RequestMoveItem(0, 0, 0, 2, 0, 0, 0));
                yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 0, 5);
                T.Check("rifle bag -> slot", sess.Shell.RequestMoveItem(2, 0, 0, 0, 0, 0, 0));
                yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);

                sess.Shell.EquipHotbar(1);
                yield return Ticks(4);
                T.Check($"the rifle kept ITS ammo across a gun-to-gun swap ({sess.Shell.Ammo}, want {RifleAmmo})",
                    sess.Shell.Ammo == RifleAmmo);
            }

            // ---- the second field the audit turned up: autodrink, same shape, same echo ----
            // Item.autoDrink is on the wire and is written only by the inventory's toggle, so the server's copy
            // was its `= true` initialiser forever and any move switched a deliberately-disabled bottle back ON.
            ItemAsset bottle = null;
            for (ushort id = 1; id < 2000 && bottle == null; id++)
            {
                var a = Assets.find(id);
                if (a != null && a.IsFluidContainer) bottle = a;
            }
            if (bottle != null)
            {
                // Parked at the far RIGHT of the page on purpose: the stimulus below drags the 4x2 rifle back in
                // at (0,0), and a bottle sitting in its footprint would make the server reject the drag -- the
                // test would then fail on a grid-space rule rather than on the thing it is about.
                byte bagW = sess.Shell.Inventory.items[2].width;
                byte bx = (byte)System.Math.Max(0, bagW - bottle.size_x);
                // Derived from the two items rather than a number picked to fit today's page: the rifle lands at
                // (0,0) and occupies its own width, so the bottle needs the column after it.
                int needW = rifle.size_x + bottle.size_x;
                T.Check($"(setup) the bag is wide enough to park a bottle clear of the rifle ({bagW} wide, need {needW})", bagW >= needW);
                sInv.Inventory.items[2].addItem(bx, 0, 0, new Item(bottle.id));
                yield return Until(() => sess.Shell.Inventory.items[2].getIndex(bx, 0) != byte.MaxValue, 5);
                byte bi = sess.Shell.Inventory.items[2].getIndex(bx, 0);
                T.Check($"(setup) the bottle reached the client's bag ({bottle.itemName})", bi != byte.MaxValue);
                if (bi != byte.MaxValue)
                {
                    T.Check("autodrink starts ON (the default the echo used to force back)",
                        sess.Shell.Inventory.items[2].getItem(bi).item.autoDrink);
                    T.Check("the toggle routed to the server", sess.Shell.RequestSetAutoDrink(2, bx, 0, bottle.id, false));
                    yield return Until(() => ded.Server.Transactions.Diag.AutoDrinkApplied > 0, 5);

                    // The stimulus is a move of a DIFFERENT item -- that is the whole complaint. Nobody touched
                    // the bottle; the echo the rifle's move triggered is what used to reset it.
                    T.Check("(stimulus) move the rifle again so an echo repaints the bag",
                        sess.Shell.RequestMoveItem(0, 0, 0, 2, 0, 0, 0));
                    yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 0, 5);
                    yield return Ticks(4);
                    byte bi2 = sess.Shell.Inventory.items[2].getIndex(bx, 0);
                    T.Check($"autodrink stayed OFF through an unrelated item's move ({(bi2 == byte.MaxValue ? "bottle lost" : sess.Shell.Inventory.items[2].getItem(bi2).item.autoDrink.ToString())})",
                        bi2 != byte.MaxValue && !sess.Shell.Inventory.items[2].getItem(bi2).item.autoDrink);
                }
            }

            world.Sim.Sim.Remove(pump);
        }
    }
}
