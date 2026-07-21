using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP DONE-gate for the oil-pump fixture: a JOINED client places one through the real PlaceDeployable intent,
    // the server plants the entity (server owns the fuel reservoir), and the client's DeployableReplicaView
    // materializes it as a VIEW-ONLY OilPump node mirroring the replicated Fuel. Proves the replication half a
    // joined client needs -- a passing SP/host run proves nothing here. Modeled on net.shell_place_deployable.
    // The oil pump's placement GHOST must render (ProcBox), or equipping it from the bag shows nothing to place and
    // reads as "not equippable". Without ProcBox, BuildMesh -> LoadMesh() returns null (no .obj Model) -> invisible.
    public class OilPumpEquippableGhost : GameTest
    {
        public override string Name => "oilpump.equippable_ghost";
        public override IEnumerable<Step> Run()
        {
            var ghost = Deployable.BuildMesh(DeployableDef.OilPump, out _);
            T.Check("the oil pump is a Fixture", DeployableDef.OilPump.Fixture == FixtureKind.OilPump);
            T.Check("its placement ghost builds a VISIBLE mesh (equippable)", ghost != null && ghost.Mesh != null);
            ghost?.QueueFree();
            yield break;
        }
    }

    public class NetShellPlaceOilPump : GameTest
    {
        public override string Name => "net.shell_place_oilpump";
        public override double TimeoutSimSeconds => 30;

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

            const ushort OilPumpId = 1219;
            // seed the authoritative server inventory with the oil-pump item; the echo lands it in the shell bag so
            // placement (which spends the item) validates.
            T.Check("server granted the Oil Pump item", sHave && sInv.Inventory.tryAddItem(new Item(OilPumpId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(OilPumpId) == 1, 5);
            T.Check("the grant echoed into the bag", sess.Shell.Inventory.getItemCount(OilPumpId) == 1);

            var fwd = -sess.Shell.GlobalTransform.Basis.Z;
            var spot = sess.Shell.GlobalPosition + fwd * 3f;
            T.Check("the place request fired through the real PlaceDeployable intent",
                    sess.Shell.RequestPlaceDeployable(OilPumpId, spot, 30f));

            // (a) the SERVER planted the entity (it owns the state; no Godot node server-side)
            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the oil-pump entity", ded.Server.Deployables.Count == 1);
            T.Check("(b) the SERVER spent the item", sInv.Inventory.getItemCount(OilPumpId) == 0);

            // (c) the JOINED client's ReplicaView materialized it as a VIEW-ONLY OilPump fixture (not a Deployable body)
            OilPump opump = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetOilPump(e.NetIdValue, out opump)) return true;
                return false;
            }, 5);
            T.Check("(c) the joined client materialized the oil pump (view-only fixture)", opump != null && opump.IsReplica);
            T.Check("(c2) it did NOT materialize as a Deployable body", sess.Deploys.NodeCount == 0);

            // (d) the client mirrors the server-owned Fuel reservoir (a fresh build starts full = def Fuel 2500)
            yield return Until(() => opump != null && opump.Fuel > 0f, 5);
            T.Check($"(d) the client mirrors the server-owned Fuel ({opump?.Fuel:0} > 0)", opump != null && opump.Fuel > 0f);

            // (e) removing the entity retires the client's view node
            ded.Server.Deployables.ServerRemove(opump.NetId, ded.Server.Session.CurrentTick);
            yield return Until(() => sess.Deploys.OilPumpCount == 0, 5);
            T.Check("(e) removing the entity retired the client's oil-pump view", sess.Deploys.OilPumpCount == 0);
        }
    }
}
