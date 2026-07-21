using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP DONE-gate for the SENTRY fixture, BOTH axes: (1) replication -- a JOINED client materializes a view-only
    // Sentry from the placed entity; (2) server-auth LOGIC -- the server-side ServerSentries actually SCANS, FIRES, and
    // KILLS an authoritative zombie, and that kill replicates to the joined client. A view-only sentry that only rendered
    // would pass (1) and fail (2) -- a dead prop. This asserts the kill, so a non-firing sentry can't pass. The zombie
    // has no other damage source (no player fires), so its death can ONLY come from the sentry -- teeth by construction.
    public class NetShellSentryKillsZombie : GameTest
    {
        public override string Name => "net.shell_sentry_kills_zombie";
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

            var zDeaths = new List<ZombieDiedEvent>();
            sess.Client.ZombieDied += zDeaths.Add;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            yield return Ticks(10);

            const ushort SentryId = 1244;
            T.Check("server granted the Sentry item", sHave && sInv.Inventory.tryAddItem(new Item(SentryId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(SentryId) == 1, 5);

            // place the sentry at a fixed spot facing -Z (yaw 0) via the REAL PlaceDeployable intent
            var spot = new Vector3(0f, 0f, 0f);
            T.Check("the place request fired through the real PlaceDeployable intent",
                    sess.Shell.RequestPlaceDeployable(SentryId, spot, 0f));

            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the sentry entity", ded.Server.Deployables.Count == 1);

            // (b) the joined client materialized a VIEW-ONLY sentry (not a Deployable body)
            Sentry view = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetSentry(e.NetIdValue, out view)) return true;
                return false;
            }, 5);
            T.Check("(b) the joined client materialized a view-only sentry", view != null && view.IsReplica);
            T.Check("(b2) it did NOT materialize as a Deployable body", sess.Deploys.NodeCount == 0);

            // (c) the server-auth system is driving it
            yield return Until(() => ded.Sentries != null && ded.Sentries.TrackedCount == 1, 5);
            T.Check("(c) ServerSentries is driving the placed sentry", ded.Sentries.TrackedCount == 1);

            // a real zombie brain 10 m in FRONT of the sentry (-Z, inside its facing arc + range), published to the wire
            var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(0f, 0.3f, -10f);
            yield return Until(() => sess.Puppets.PuppetCount == 1, 5);
            T.Check("the target zombie replicated to the joined client", sess.Puppets.PuppetCount == 1);
            T.Check("(baseline) the zombie is alive before the sentry engages", !z.Dead);

            // (d) THE PROOF: with no player firing, the server-auth sentry scans -> fires -> KILLS the zombie
            for (int i = 0; i < 700 && !z.Dead; i++) yield return Ticks(1);
            T.Check("(d) the server-authoritative sentry KILLED the zombie (no player fired)", z.Dead);

            // (e) and the kill replicated to the joined client as a ZombieDied event
            yield return Until(() => zDeaths.Count >= 1, 5);
            T.Check($"(e) the joined client saw the ZombieDied event ({zDeaths.Count})", zDeaths.Count >= 1);
        }
    }
}
