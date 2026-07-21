using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP DONE-gate for the TRAP fixture, BOTH axes: (1) replication -- a JOINED client materializes a view-only Trap
    // from the placed entity; (2) server-auth LOGIC -- the server-side ServerTraps EDGE-TRIGGERS on an authoritative
    // zombie stepping onto it and KILLS it, and that kill replicates to the joined client. A view-only trap that only
    // rendered would pass (1) and fail (2) -- a dead prop. TEETH: the zombie must SURVIVE while it sits OUTSIDE the trap
    // footprint (time/proximity alone don't trigger), and only DIE on a fresh ENTER -- and no player ever fires, so the
    // death can ONLY come from the trap trigger. Uses a LANDMINE (one-shot AoE) for a deterministic kill + self-destruct.
    public class NetShellTrapKillsZombie : GameTest
    {
        public override string Name => "net.shell_trap_kills_zombie";
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

            const ushort LandmineId = 1101;
            T.Check("server granted the Landmine item", sHave && sInv.Inventory.tryAddItem(new Item(LandmineId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(LandmineId) == 1, 5);

            // place the landmine at the origin via the REAL PlaceDeployable intent
            var spot = new Vector3(0f, 0f, 0f);
            T.Check("the place request fired through the real PlaceDeployable intent",
                    sess.Shell.RequestPlaceDeployable(LandmineId, spot, 0f));

            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the trap entity", ded.Server.Deployables.Count == 1);

            // (b) the joined client materialized a VIEW-ONLY trap (not a Deployable body)
            Trap view = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetTrap(e.NetIdValue, out view)) return true;
                return false;
            }, 5);
            T.Check("(b) the joined client materialized a view-only trap", view != null && view.IsReplica);
            T.Check("(b2) it did NOT materialize as a Deployable body", sess.Deploys.NodeCount == 0);

            // (c) the server-auth system is driving it
            yield return Until(() => ded.Traps != null && ded.Traps.TrackedCount == 1, 5);
            T.Check("(c) ServerTraps is driving the placed trap", ded.Traps.TrackedCount == 1);

            // a real zombie brain that starts OUTSIDE the landmine's footprint (5 m away), published to the wire
            var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(5f, 0.3f, 0f);
            yield return Until(() => sess.Puppets.PuppetCount == 1, 5);
            T.Check("the zombie replicated to the joined client", sess.Puppets.PuppetCount == 1);

            // (TEETH) while it sits OUTSIDE the footprint (well past the 0.25 s arm), the trap must NOT kill it --
            // proximity + time alone don't trigger; only a fresh ENTER does.
            for (int i = 0; i < 40; i++) yield return Ticks(1);
            T.Check("(teeth) the zombie SURVIVES while outside the trap footprint", !z.Dead);

            // step ONTO the landmine -> a fresh ENTER fires it (a landmine one-shots + self-destructs)
            z.GlobalPosition = new Vector3(0f, 0.3f, 0f);
            for (int i = 0; i < 200 && !z.Dead; i++) yield return Ticks(1);
            T.Check("(d) stepping on the trap KILLED the zombie server-side (no player fired)", z.Dead);

            // (e) the kill replicated to the joined client as a ZombieDied event
            yield return Until(() => zDeaths.Count >= 1, 5);
            T.Check($"(e) the joined client saw the ZombieDied event ({zDeaths.Count})", zDeaths.Count >= 1);

            // (f) the one-shot landmine self-destructed -> its entity is gone + the client's trap view retires
            yield return Until(() => sess.Deploys.TrapCount == 0 && ded.Server.Deployables.Count == 0, 5);
            T.Check("(f) the landmine self-destructed (server entity gone + client trap view retired)",
                    sess.Deploys.TrapCount == 0 && ded.Server.Deployables.Count == 0);
        }
    }
}
