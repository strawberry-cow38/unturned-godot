using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP DONE-gate for the HORDE BEACON fixture, BOTH axes: (1) replication -- a JOINED client materializes a view-only
    // Beacon obelisk from the placed entity; (2) server-auth LOGIC -- the server-side ServerBeacon actually SPAWNS a horde
    // of authoritative zombies, and that horde replicates to the joined client as puppets. A view-only beacon that only
    // rendered the obelisk would pass (1) and fail (2) -- a dead prop. TEETH by construction: the world builds with
    // noZombies, so the map has ZERO zombies -- any zombie that appears can ONLY have come from the beacon's horde.
    public class NetShellBeaconSpawnsHorde : GameTest
    {
        public override string Name => "net.shell_beacon_spawns_horde";
        public override double TimeoutSimSeconds => 45;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260721);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "builder" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            yield return Ticks(10);

            // (teeth precondition) the map has NO zombies -- so any that appear are the beacon's horde
            T.Check("(teeth) the world starts with ZERO zombies", ded.Server.Zombies.Count == 0);

            const ushort BeaconId = 1194;
            T.Check("server granted the Beacon item", sHave && sInv.Inventory.tryAddItem(new Item(BeaconId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(BeaconId) == 1, 5);

            var spot = new Vector3(0f, 0f, 0f);
            T.Check("the place request fired through the real PlaceDeployable intent",
                    sess.Shell.RequestPlaceDeployable(BeaconId, spot, 0f));

            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the beacon entity", ded.Server.Deployables.Count == 1);

            // (b) the joined client materialized a VIEW-ONLY beacon obelisk (not a Deployable body)
            Beacon view = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetBeacon(e.NetIdValue, out view)) return true;
                return false;
            }, 5);
            T.Check("(b) the joined client materialized a view-only beacon", view != null && view.IsReplica);
            T.Check("(b2) it did NOT materialize as a Deployable body", sess.Deploys.NodeCount == 0);

            // (c) the server-auth system is driving it + activated its horde
            yield return Until(() => ded.Beacon != null && ded.Beacon.TrackedCount == 1, 5);
            T.Check("(c) ServerBeacon is driving the placed beacon", ded.Beacon.TrackedCount == 1);

            // (d) THE PROOF: the server spawned a horde of authoritative zombies (opening burst = MaxAlive 12)
            yield return Until(() => ded.Server.Zombies.Count >= 12, 10);
            T.Check($"(d) the server-authoritative beacon SPAWNED a horde ({ded.Server.Zombies.Count} zombies, was 0)", ded.Server.Zombies.Count >= 12);
            T.Check($"(d2) ServerBeacon is tracking its live horde ({ded.Beacon.LiveHorde})", ded.Beacon.LiveHorde >= 12);

            // (e) the horde replicated to the joined client as puppets
            yield return Until(() => sess.Puppets.PuppetCount >= 12, 10);
            T.Check($"(e) the horde replicated to the joined client ({sess.Puppets.PuppetCount} puppets)", sess.Puppets.PuppetCount >= 12);

            // (f) removing the beacon retires its client obelisk view + despawns the horde
            uint beaconId = 0;
            foreach (var e in ded.Server.Deployables.All) beaconId = e.NetIdValue;
            ded.Server.Deployables.ServerRemove(beaconId, ded.Server.Session.CurrentTick);
            yield return Until(() => sess.Deploys.BeaconCount == 0, 5);
            T.Check("(f) removing the beacon retired the client obelisk view", sess.Deploys.BeaconCount == 0);
        }
    }
}
