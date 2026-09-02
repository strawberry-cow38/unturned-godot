using Godot;
using System.Collections.Generic;
using System.Linq;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // DEATH DROPS EVERYTHING, on the real world path (strawberry 2026-09-02: "your items are kept after death
    // instead of dropping on the ground"). The L0 suite (tests/UnturnedNet.Tests/DeathDropTests.cs) proves the
    // server-side mechanics; THIS proves what a player actually sees: a real ClientWorldSession shell carrying a
    // rifle and beans dies to real server damage, and (1) the items appear as world-item PUPPET NODES in ANOTHER
    // client's world -- built by that client's own WorldItemReplicaView from its own replica, i.e. the thing a
    // second player would walk up to and press F on -- (2) the victim's own view shows them too, (3) the victim's
    // adopted bag is empty, and (4) on respawn the spawn outfit (hoodie + cargo pants, the grid) is back on the
    // server AND adopted by the shell, so the new life can pick things up again.
    //
    // TEETH: the assertions are on world items that EXIST, keyed by NetId, in a second client's node tree.
    // "The inventory is empty" alone would also pass if the items had been deleted. Reverting the
    // Combat.PlayerDied wiring in NetWorldServer leaves the ground unchanged and the bag full: (1)-(3) fail.
    // Reverting the DedicatedServer respawn re-grant leaves the respawned player naked with no grid: (4) fails.
    public class NetDeathDropsItemsVisibleToOthers : GameTest
    {
        public override string Name => "net.death_drops_items_visible_to_others";
        public override double TimeoutSimSeconds => 60;

        const ushort RifleId = 4, BeansId = 13, HoodieId = 3, CargoPantsId = 209;

        static int Carried(PlayerInventory inv)
        {
            int n = 0;
            for (byte p = 0; p < PlayerInventory.STORAGE; p++) n += inv.items[p].getItemCount();
            foreach (var w in new[] { inv.wornHat, inv.wornGlasses, inv.wornMask, inv.wornShirt, inv.wornVest, inv.wornBackpack, inv.wornPants })
                if (w != null) n++;
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260903);
            // the WITNESS: a headless second client whose replica is materialized by its OWN WorldItemReplicaView
            // into real puppet nodes -- exactly what a second player's game does with the same facts
            var witness = new NetWorldClient(new MemClientTransport(net), "witness", contentHash: NetContent.Hash);
            var witnessView = new WorldItemReplicaView { Client = witness };
            World.AddChild(witnessView);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); witness.Tick(); }, "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "victim" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            witness.Connect();

            yield return Until(() => sess.Shell != null && witness.State == NetSessionState.Connected, 5);
            T.Check("victim shell spawned + witness joined", sess.Shell != null && witness.State == NetSessionState.Connected);
            if (sess.Shell == null) { world.Sim.Sim.Remove(pump); yield break; }
            var shell = sess.Shell;
            ushort me = sess.Client.PlayerId;

            // stock the VICTIM'S SERVER grid (the authority): a rifle with state + beans, on top of the spawn clothes
            var serverInv = ded.Server.Transactions.InventoryForTest(me);
            T.Check("the server grid exists", serverInv != null);
            if (serverInv == null) { world.Sim.Sim.Remove(pump); yield break; }
            var rifle = new Item(RifleId) { quality = 61, gunAmmo = 9 };
            T.Check("fixture: rifle granted server-side", serverInv.tryAddItem(rifle));
            T.Check("fixture: beans granted server-side", serverInv.tryAddItem(new Item(BeansId)));
            T.Check("fixture: wearing the spawn hoodie + cargo pants", serverInv.wornShirt?.id == HoodieId && serverInv.wornPants?.id == CargoPantsId);
            int carried = Carried(serverInv);
            T.Check($"fixture: {carried} things carried (rifle, beans, hoodie, pants)", carried == 4);
            yield return Until(() => shell.Inventory.getItemCount(RifleId) == 1 && shell.Inventory.getItemCount(BeansId) == 1, 5);
            T.Check("the shell adopted the stocked bag before dying (so the post-death echo is a real change)",
                    shell.Inventory.getItemCount(RifleId) == 1 && shell.Inventory.getItemCount(BeansId) == 1);

            yield return Ticks(20);
            var deathSpot = shell.TruePhysicsPosition;
            var groundBefore = new HashSet<uint>(ded.Server.WorldItems.All.Select(e => e.NetIdValue));

            // real server damage kills the owner
            ded.Server.Combat.QueueDebugPlayerDamage(me, 1000f, 0);
            yield return Until(() => shell.IsDead, 5);
            T.Check("the server death fact rendered on the owner (shell _dead)", shell.IsDead);

            // (1) the SERVER put every carried thing on the ground at the death spot
            var dropped = ded.Server.WorldItems.All.Where(e => !groundBefore.Contains(e.NetIdValue)).ToList();
            T.Check($"the server created exactly {carried} new world items ({dropped.Count})", dropped.Count == carried);
            var ids = dropped.Select(e => e.ItemId).OrderBy(i => i).ToArray();
            var want = new ushort[] { RifleId, BeansId, HoodieId, CargoPantsId }.OrderBy(i => i).ToArray();
            T.Check($"they are the rifle, beans, hoodie and pants ({string.Join(",", ids)})", ids.SequenceEqual(want));
            float far = dropped.Count == 0 ? 999f : dropped.Max(e => new Vector3(e.Pos.x, e.Pos.y, e.Pos.z).DistanceTo(deathSpot));
            T.Check($"all within 2 m of where the player died (farthest {far:0.00} m)", dropped.Count > 0 && far < 2f);
            var groundRifle = dropped.FirstOrDefault(e => e.ItemId == RifleId);
            T.Check("the rifle on the ground is the SAME Item, ammo and quality intact (9 rounds, q61)",
                    groundRifle != null && ReferenceEquals(groundRifle.ServerItem, rifle) && groundRifle.ServerItem.gunAmmo == 9 && groundRifle.Quality == 61);
            T.Check("the server bag is empty, clothes off", Carried(serverInv) == 0 && serverInv.wornShirt == null && serverInv.wornPants == null);

            // (2) the WITNESS materialized a puppet NODE for each of them -- what a second player would see + F
            yield return Until(() => dropped.All(e => witnessView.TryGetNode(e.NetIdValue, out _)), 5);
            int seen = dropped.Count(e => witnessView.TryGetNode(e.NetIdValue, out _));
            T.Check($"the witness's WorldItemReplicaView built a puppet for every dropped item ({seen}/{dropped.Count})", dropped.Count > 0 && seen == dropped.Count);
            float puppetErr = 0f;
            foreach (var e in dropped)
                if (witnessView.TryGetNode(e.NetIdValue, out var node))
                    puppetErr = Mathf.Max(puppetErr, node.GlobalPosition.DistanceTo(new Vector3(e.Pos.x, e.Pos.y, e.Pos.z)));
            T.Check($"...each puppet sits at the server's spot (max err {puppetErr:0.000} m)", puppetErr < 0.05f);

            // (3) the VICTIM: its own view shows them, and its adopted bag is empty
            yield return Until(() => dropped.All(e => sess.Items.TryGetNode(e.NetIdValue, out _)) && Carried(shell.Inventory) == 0, 5);
            T.Check("the victim's own WorldItemReplicaView shows every dropped item", dropped.All(e => sess.Items.TryGetNode(e.NetIdValue, out _)));
            T.Check($"the victim's adopted bag is EMPTY ({Carried(shell.Inventory)} carried)", Carried(shell.Inventory) == 0);
            T.Check("the victim is not holding the rifle it just dropped", !shell.HasSomethingHeld && shell.Gun == null);

            // (4) respawn: the spawn outfit is back on the server and adopted -- the new life has a grid
            yield return Until(() => !shell.IsDead, 8);
            T.Check("the owner revived on the server respawn fact", !shell.IsDead);
            T.Check("the server re-issued the spawn outfit (hoodie + cargo pants)", serverInv.wornShirt?.id == HoodieId && serverInv.wornPants?.id == CargoPantsId);
            yield return Until(() => shell.Inventory.wornShirt?.id == HoodieId && shell.Inventory.wornPants?.id == CargoPantsId, 5);
            T.Check("...and the shell adopted it", shell.Inventory.wornShirt?.id == HoodieId && shell.Inventory.wornPants?.id == CargoPantsId);
            T.Check($"the pants page is a real grid again ({shell.Inventory.items[PlayerInventory.PANTS].width}x{shell.Inventory.items[PlayerInventory.PANTS].height})",
                    shell.Inventory.items[PlayerInventory.PANTS].width > 0);
            T.Check("the respawned player did NOT get the rifle back (it is still on the ground)",
                    shell.Inventory.getItemCount(RifleId) == 0 && ded.Server.WorldItems.TryGet(groundRifle?.NetIdValue ?? 0, out _));
            T.Check("the outfit re-grant did not double up: the ground still holds exactly one hoodie + one pants from this death",
                    ded.Server.WorldItems.All.Count(e => !groundBefore.Contains(e.NetIdValue) && e.ItemId == HoodieId) == 1
                    && ded.Server.WorldItems.All.Count(e => !groundBefore.Contains(e.NetIdValue) && e.ItemId == CargoPantsId) == 1);

            world.Sim.Sim.Remove(pump);
            witness.Disconnect();
        }
    }
}
