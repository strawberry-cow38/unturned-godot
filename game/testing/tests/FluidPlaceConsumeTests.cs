using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-09-10: "the fluid io deployables arent consumed on place", spawning them with
    // `give 9114` -- so the item is in the BAG, not the console's backing-less hold.
    //
    // WHY THIS IS A NET TEST AND NOT AN SP ONE, which is the whole reason it was worth building: the
    // real game boots through the loopback (Main.AttachMpLoopback with gameDefault:true), so
    // NetPlaceDeployable is LIVE in a normal single-player session. On that path PlayerController
    // deliberately SKIPS the local removeItemAmount -- the P1 invariant, because the owner-inventory
    // echo would otherwise put the item straight back -- and leaves the spend to the server's
    // OnPlaceDeployable. Anything that tests the direct SP branch passes while the path players
    // actually run is broken, which is exactly the shape of bug being reported.
    //
    // Teeth: the assertions are on the SERVER's count first and the client's bag second, so a fix that
    // only mutates locally fails (a), and a server spend the client never hears about fails (b).
    public class FluidPlaceConsume : GameTest
    {
        public override string Name => "fluid.place_consumes_over_the_wire";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260910);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "placer" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server holds the shell's inventory", sHave);
            if (!sHave) yield break;

            const ushort Id = 9114;   // Fluid Pump -- a device with power ports, so it exercises the busiest def
            // ⚠ ONE JAR OF TWO, NOT TWO JARS OF ONE -- and that is the whole difference between this test being
            // able to fail and not (tinyclaw's catch, 2026-09-10). Two separate jars each spend via
            // page.removeItem, which raises onStateUpdated and echoes correctly; only a STACK decrement is the
            // bare `jar.item.amount -= take` that raises nothing. The first version of this test seeded two jars
            // and ran 18/18 green against a build that was eating the player's bag.
            // getItemCount sums AMOUNTS, not jars, so every assertion below reads 2 either way.
            var stack = new Item(Id); stack.amount = 2; sInv.Inventory.tryAddItem(stack);
            for (int i = 0; i < 60 && sess.Shell.Inventory.getItemCount(Id) != 2; i++) yield return Ticks(2);
            GD.Print($"[fpc] after seed: server={sInv.Inventory.getItemCount(Id)} client={sess.Shell.Inventory.getItemCount(Id)}");
            int serverBefore = sInv.Inventory.getItemCount(Id);
            T.Check("server grid carries 2 Fluid Pumps", serverBefore == 2);
            T.Check("the shell's bag adopted them", sess.Shell.Inventory.getItemCount(Id) == 2);

            var asset = Assets.find(Id);
            T.Check("9114 resolves as an item with a deployable def", asset != null && DeployableDef.ById(Id)?.Fluid != null);

            // Equip it the way the inventory UI does -- WITH the bag item as the backing. The console's
            // `deploy`/`hold` verbs pass null here and are infinite by design; that is not this path.
            var backing = new Item(Id);
            T.Check("equipped from the bag", sess.Shell.EquipItemAsset(asset, backing));
            T.Check("held as a deployable with a backing item",
                sess.Shell.DebugHeldDeployable?.Id == Id && sess.Shell.DebugDeployBacking != null);

            T.Check("placement ghost exists", sess.Shell.DebugPlacerActive);
            T.Check("the shell has an Inventory to spend from", sess.Shell.Inventory != null);
            // Which branch the place takes hinges on this. Wired -> the SERVER spends; unwired -> the
            // local removeItemAmount does. Asserting it means the failure names which path was live.
            T.Check("net place seam is wired (server spends)", sess.Shell.DebugNetPlaceWired);
            sess.Shell.DebugArmPlace(sess.Shell.GlobalPosition + new Vector3(2f, 0f, 0f));
            sess.Shell.DebugDeployTick(0.05f);
            T.Check($"the place gesture completed (timer now {sess.Shell.DebugPlaceTimer:0.###})", sess.Shell.DebugPlaceTimer <= 0f);

            for (int i = 0; i < 60 && sInv.Inventory.getItemCount(Id) == serverBefore; i++) yield return Ticks(2);
            GD.Print($"[fpc] after place: server={sInv.Inventory.getItemCount(Id)} client={sess.Shell.Inventory.getItemCount(Id)} (was {serverBefore})");
            T.Check("(a) the SERVER spent one on place", sInv.Inventory.getItemCount(Id) == serverBefore - 1);

            for (int i = 0; i < 60 && sess.Shell.Inventory.getItemCount(Id) != serverBefore - 1; i++) yield return Ticks(2);
            GD.Print($"[fpc] final: server={sInv.Inventory.getItemCount(Id)} client={sess.Shell.Inventory.getItemCount(Id)}");
            T.Check("(b) the owner echo took it out of the shell's bag",
                sess.Shell.Inventory.getItemCount(Id) == serverBefore - 1);

            world.Sim.Sim.Remove(pump);
        }
    }
    // strawberry 2026-09-10: "the items i get from picking up deployables are phantom. ie when i update the
    // inv they disappear" -- on FLUID devices specifically.
    //
    // WHY THE OLD CODE COULD NOT DO BETTER: fluid defs were registered LocalOnly, so ServerPlace bailed before
    // creating an entity. A placed pump therefore had NO NetId, there was nothing for a pickup to be validated
    // against, and PickupFluid granted the item with a bare local tryAddItem. The bag is server-owned on every
    // path -- singleplayer included, since SP runs through MpLoopback -- so the next owner echo deleted it. The
    // item did not vanish on refresh; it was never real.
    //
    // TEETH. Check (a) fails on the old build outright: with LocalOnly there is no server entity to find, so
    // there is no NetId to pick anything up by. And (f) is the one that catches a fix that only mutates the
    // client -- it re-reads the count after further ticks, which is exactly when a phantom evaporates. A test
    // that stopped at (e) would pass on the bug being fixed here.
    public class FluidPickupRefunds : GameTest
    {
        public override string Name => "fluid.pickup_refunds_over_the_wire";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260911);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "picker" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server holds the shell's inventory", sHave);
            if (!sHave) yield break;

            const ushort Id = 9114;   // Fluid Pump
            sInv.Inventory.tryAddItem(new Item(Id));
            for (int i = 0; i < 60 && sess.Shell.Inventory.getItemCount(Id) != 1; i++) yield return Ticks(2);
            T.Check("the bag carries one Fluid Pump", sess.Shell.Inventory.getItemCount(Id) == 1);

            var asset = Assets.find(Id);
            var backing = new Item(Id);
            T.Check("equipped from the bag", sess.Shell.EquipItemAsset(asset, backing));
            T.Check("net place seam is wired", sess.Shell.DebugNetPlaceWired);
            sess.Shell.DebugArmPlace(sess.Shell.GlobalPosition + new Vector3(2f, 0f, 0f));
            sess.Shell.DebugDeployTick(0.05f);

            // (a) THE WHOLE POINT OF SMALL A: the server keeps a real entity for a fluid device now. Under
            // LocalOnly this list stayed empty for fluid ids no matter how many pumps you put down.
            uint netId = 0;
            for (int i = 0; i < 80 && netId == 0; i++)
            {
                foreach (var e in ded.Server.Deployables.All) if (e.DefId == Id) { netId = e.NetIdValue; break; }
                if (netId == 0) yield return Ticks(2);
            }
            GD.Print($"[fpr] server entity for {Id}: netId={netId}");
            T.Check("(a) the SERVER registered an entity for the placed fluid device", netId != 0);
            if (netId == 0) yield break;

            // (b) ...and it reached the client's replicated list, which is what DeployableReplicaView
            // materializes the FluidContainer from. No entry here = the placer sees nothing at all.
            bool onClient = false;
            for (int i = 0; i < 60 && !onClient; i++)
            {
                foreach (var e in sess.Client.Deployables.All) if (e.NetIdValue == netId) { onClient = true; break; }
                if (!onClient) yield return Ticks(2);
            }
            T.Check("(b) the entity replicated to the client", onClient);
            T.Check("(c) the place spent the item", sInv.Inventory.getItemCount(Id) == 0);

            // Pick it up through the SAME seam PickupFluid now dispatches to when the device carries a NetId.
            T.Check("pickup seam is wired", sess.Shell.NetPickupDeployable != null);
            sess.Shell.NetPickupDeployable(netId);

            for (int i = 0; i < 80 && sInv.Inventory.getItemCount(Id) != 1; i++) yield return Ticks(2);
            GD.Print($"[fpr] after pickup: server={sInv.Inventory.getItemCount(Id)} client={sess.Shell.Inventory.getItemCount(Id)}");
            T.Check("(d) the SERVER refunded the item", sInv.Inventory.getItemCount(Id) == 1);

            for (int i = 0; i < 60 && sess.Shell.Inventory.getItemCount(Id) != 1; i++) yield return Ticks(2);
            T.Check("(e) the owner echo put it in the shell's bag", sess.Shell.Inventory.getItemCount(Id) == 1);

            // (f) THE PHANTOM CHECK. A client-only grant survives the instant it is made and dies at the next
            // owner echo, so re-reading after more ticks is the difference between a real item and the bug.
            for (int i = 0; i < 30; i++) yield return Ticks(2);
            GD.Print($"[fpr] settled: server={sInv.Inventory.getItemCount(Id)} client={sess.Shell.Inventory.getItemCount(Id)}");
            T.Check("(f) it is STILL there after the grid settles -- not a phantom",
                sess.Shell.Inventory.getItemCount(Id) == 1 && sInv.Inventory.getItemCount(Id) == 1);

            // (g) and the device is gone from both sides, so the refund did not clone it.
            bool stillServer = false; foreach (var e in ded.Server.Deployables.All) if (e.NetIdValue == netId) stillServer = true;
            T.Check("(g) the device was removed server-side", !stillServer);
            world.Sim.Sim.Remove(pump);
        }
    }

}
