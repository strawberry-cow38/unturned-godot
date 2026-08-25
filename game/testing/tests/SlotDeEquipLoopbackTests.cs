using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // TAKE IT OUT OF THE SLOT, IT LEAVES YOUR HANDS -- THROUGH THE LOOPBACK (strawberry 2026-08-16).
    //
    // WHY THIS SUITE EXISTS, which matters more than the rule it checks: the rule shipped BROKEN in 23394b26,
    // and it shipped with a green teeth check. SlotRuleTests section 5 drives a bare PlayerController and empties
    // the slot with an in-process `Inventory.items[0].removeItem(0)`, which raises onStateUpdated and fires the
    // watcher. The GAME never does that. Singleplayer runs through the loopback server, the server owns the grid,
    // and the slot empties via the owner echo -> CopyPage -> Items.clear(), which was silent. So the watcher could
    // not fire on the only path anyone plays, while the test that "proved" it went red on demand.
    //
    // Deleting the watcher made that test fail, so the check had teeth. It had teeth on a path the game does not
    // run. A teeth check validates the assertion<->code binding; it says nothing about whether the caller exists.
    //
    // Hence this suite: same rule, driven where the player lives. The stimulus is a real MoveItem command handled
    // by the real server, and the assertion is on what is in the player's hands after the echo lands.
    public sealed class SlotDeEquipLoopbackTests : GameTest
    {
        public override string Name => "net.slot_deequip";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            // A PRIMARY gun, found BY THE PROPERTY UNDER TEST rather than by a hardcoded id -- an id typo and a
            // broken slot parser look identical otherwise (the lesson from SlotRuleTests' first cut).
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
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "holster" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            T.Check("server owns an inventory for the joiner", ded.Server.Inventories.TryGet(sess.Client.PlayerId, out _));
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv)) yield break;

            // Seed SERVER-SIDE: the rifle into the primary slot, and an empty bag page to drag it into. Clearing
            // page 2 guarantees (0,0) is free for the move without having to hunt for a hole.
            sInv.Inventory.items[0].addItem(0, 0, 0, new Item(rifle.id));
            sInv.Inventory.items[2].clear();
            sInv.Inventory.items[2].raiseStateUpdated();   // clear() is silent by design; announce the rebuild

            // The client must ADOPT that before anything below means anything.
            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);
            T.Check($"the client adopted the rifle into its primary slot ({sess.Shell.Inventory.items[0].getItemCount()})",
                sess.Shell.Inventory.items[0].getItemCount() == 1);
            T.Check($"...and the bag page the drag targets is empty ({sess.Shell.Inventory.items[2].getItemCount()})",
                sess.Shell.Inventory.items[2].getItemCount() == 0);
            T.Check("the shell's bag really is server-owned (this is the whole point of the suite)",
                sess.Shell.InventoryIsServerOwned);

            // EQUIP IT from the slot, exactly as pressing 1 does.
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"the rifle is in the player's hands ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);
            if (!sess.Shell.HasGunOut) yield break;

            // NOW TAKE IT OUT OF THE SLOT, through the wire path the dashboard drag actually uses. Not a local
            // removeItem -- that is the direct-path stimulus this suite exists to stop trusting.
            T.Check("the move request reached the wire", sess.Shell.RequestMoveItem(0, 0, 0, 2, 0, 0, 0));
            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 0, 5);
            T.Check($"the server emptied the primary slot and the client adopted it ({sess.Shell.Inventory.items[0].getItemCount()})",
                sess.Shell.Inventory.items[0].getItemCount() == 0);
            T.Check($"...and the rifle really moved rather than vanishing ({sess.Shell.Inventory.items[2].getItemCount()})",
                sess.Shell.Inventory.items[2].getItemCount() == 1);

            // THE RULE. Asserted on what is in the player's hands, not on whether an event fired.
            yield return Ticks(4);
            T.Check($"emptying the primary slot OVER THE WIRE de-equips the held gun (held {sess.Shell.HeldGunName ?? "none"})",
                !sess.Shell.HasGunOut);

            world.Sim.Sim.Remove(pump);
        }
    }
}
