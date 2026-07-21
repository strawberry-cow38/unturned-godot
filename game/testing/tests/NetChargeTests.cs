using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP DONE-gate for the C4 CHARGE fixture, BOTH axes, over the FULL wire loop: (1) replication -- a JOINED client
    // materializes a view-only Charge brick from the placed entity; (2) server-auth LOGIC -- the joined client presses its
    // detonator (Player.RequestDetonateCharges -> DetonateChargesCommand -> the server's OnDetonateCharges seam ->
    // ServerCharge.DetonateAll), whose AoE blast KILLS an authoritative zombie, and the kill replicates back. TEETH: the
    // charge does NOTHING on its own (inert until detonated) -- the zombie must survive until the client fires the
    // detonator, so the kill is unambiguously the blast (and no player fires a gun).
    public class NetShellChargeDetonatesZombie : GameTest
    {
        public override string Name => "net.shell_charge_detonates_zombie";
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

            const ushort ChargeId = 1241;
            T.Check("server granted the Charge item", sHave && sInv.Inventory.tryAddItem(new Item(ChargeId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(ChargeId) == 1, 5);

            var spot = new Vector3(0f, 0f, 0f);
            T.Check("the place request fired through the real PlaceDeployable intent",
                    sess.Shell.RequestPlaceDeployable(ChargeId, spot, 0f));

            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the charge entity", ded.Server.Deployables.Count == 1);

            // (b) the joined client materialized a VIEW-ONLY charge brick (not a Deployable body)
            Charge view = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetCharge(e.NetIdValue, out view)) return true;
                return false;
            }, 5);
            T.Check("(b) the joined client materialized a view-only charge", view != null && view.IsReplica);
            T.Check("(b2) it did NOT materialize as a Deployable body", sess.Deploys.NodeCount == 0);
            T.Check("(c) the ServerCharge system exists", ded.Charge != null);

            // a real zombie brain 1 m from the charge (well inside the 8 m blast), published to the wire
            var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(1f, 0.3f, 0f);
            yield return Until(() => sess.Puppets.PuppetCount == 1, 5);
            T.Check("the zombie replicated to the joined client", sess.Puppets.PuppetCount == 1);

            // (TEETH) a planted charge is INERT -- it does nothing until detonated, so the zombie survives
            for (int i = 0; i < 40; i++) yield return Ticks(1);
            T.Check("(teeth) the un-detonated charge does NOT kill the zombie", !z.Dead);

            // (d) DETONATE over the wire: the joined client presses its detonator -> DetonateChargesCommand -> the
            // server's OnDetonateCharges seam -> ServerCharge.DetonateAll -> the AoE blast KILLS the zombie
            sess.Shell.RequestDetonateCharges();
            for (int i = 0; i < 60 && !z.Dead; i++) yield return Ticks(1);
            T.Check("(d) the client-fired detonator blast KILLED the zombie server-side", z.Dead);

            // (e) the kill replicated to the joined client as a ZombieDied event
            yield return Until(() => zDeaths.Count >= 1, 5);
            T.Check($"(e) the joined client saw the ZombieDied event ({zDeaths.Count})", zDeaths.Count >= 1);

            // (f) the charge self-destructed -> its entity is gone + the client's charge view retires
            yield return Until(() => sess.Deploys.ChargeCount == 0 && ded.Server.Deployables.Count == 0, 5);
            T.Check("(f) the charge self-destructed (server entity gone + client view retired)",
                    sess.Deploys.ChargeCount == 0 && ded.Server.Deployables.Count == 0);
        }
    }
}
