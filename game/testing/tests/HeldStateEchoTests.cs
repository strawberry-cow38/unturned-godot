using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // WHAT YOU ARE HOLDING HAS TO SURVIVE AN OWNER ECHO.
    //
    // The owner block rebuilds every jar as a FRESH Item on every snapshot, so a client that keeps a bare
    // reference to "the item in my hands" is holding a dead object one echo later. Nothing announced that: the
    // gun still fired, the HUD still drew, and the damage only showed up in state that is written through the
    // reference -- ammo went back to full on a holster, and the item's own menu offered "Equip" for the gun
    // already in your hands because IsHeld is a ReferenceEquals.
    //
    // Both checks below are on the SERVER-OWNED path deliberately. Every one of these bugs is invisible to a
    // bare PlayerController, because without a server nothing ever replaces the object.
    public sealed class HeldStateEchoTests : GameTest
    {
        public override string Name => "net.held_state_survives_echo";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
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

            var net = new MemNetwork(20260816);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "holder" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv)) { T.Check("server owns an inventory", false); yield break; }

            // A gun in the primary slot, with a SCOPE fitted on the server's copy -- the attachment ids are the
            // half of the schema that used to be missing, so the echo rebuilt the gun without them.
            var served = new Item(rifle.id) { gunSightId = 21, gunAttachSeeded = true };
            sInv.Inventory.items[0].addItem(0, 0, 0, served);

            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);
            var jar0 = sess.Shell.Inventory.items[0].getItem(0);
            T.Check($"the client adopted the rifle ({jar0?.item?.id})", jar0?.item?.id == rifle.id);
            if (jar0?.item == null) yield break;

            // ---- 1. THE ATTACHMENT IDS RIDE THE WIRE. Without them a fitted scope is destroyed by the echo:
            // gone from the gun AND from the bag, because the server really did spend it.
            T.Check($"the fitted sight survived the echo (gunSightId {jar0.item.gunSightId})", jar0.item.gunSightId == 21);
            T.Check("...and so did gunAttachSeeded, or SeedDefaults re-installs the factory irons on the next equip",
                jar0.item.gunAttachSeeded);
            T.Check($"an unset slot stays unset rather than defaulting to 0 (gunGripId {jar0.item.gunGripId})",
                jar0.item.gunGripId == -1);

            // ---- 2. THE HELD REFERENCE SURVIVES. Equip, then force a FRESH echo by dirtying the server grid,
            // and check the shell is still holding the object that is actually in the grid.
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"the rifle is in the player's hands ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);
            if (!sess.Shell.HasGunOut) yield break;

            sess.Shell.Ammo = 7;                                  // spend some rounds
            sInv.Inventory.items[2].addItem(0, 0, 0, new Item(rifle.id));   // unrelated change -> the owner block goes out
            yield return Until(() => sess.Shell.Inventory.items[2].getItemCount() == 1, 5);
            yield return Ticks(3);

            var live = sess.Shell.Inventory.items[0].getItem(0)?.item;
            T.Check("the primary slot still holds the rifle after the echo", live != null && live.id == rifle.id);
            if (live == null) yield break;
            // THE ASSERTION THAT BINDS THE BUG: identity, against the object now in the grid. Before the rebind
            // this was false and everything downstream of it quietly did the wrong thing.
            T.Check("the shell is holding the object that is IN the grid, not a pre-echo orphan",
                sess.Shell.IsHeld(Assets.find(live.id), live));
            T.Check("...which is the same object HeldItemForTest points at",
                ReferenceEquals(sess.Shell.HeldItemForTest, live));

            // ---- 3. THE CONSEQUENCE, measured rather than assumed: holster and re-equip must not refill the mag.
            sess.Shell.EquipUnarmed();
            yield return Ticks(3);
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"holstering and re-equipping keeps the rounds you had ({sess.Shell.Ammo} of 7)",
                sess.Shell.HasGunOut && sess.Shell.Ammo == 7);

            world.Sim.Sim.Remove(pump);
        }
    }
}
