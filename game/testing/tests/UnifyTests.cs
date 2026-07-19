using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // SP/MP-unify P1 phase gate (pattern-setter). Proves the "local view CONSUMES a server replica instead
    // of OWNING a direct node" seam for the DEPLOYABLE/POWER subsystem -- the exact shape MpLoopback now
    // takes under --spconsume, and the shape later phases copy for the other subsystems.
    //
    // A SINGLE client stands in for the local loopback player: it BOTH drives the deployable actions over the
    // wire (Client.Send*, as MpLoopback's seams now do) AND consumes the results through a DeployableReplicaView
    // (as MpLoopback now wires when ConsumeDeployables). The dedicated server validates + spends + broadcasts
    // (net-stack modeled on NetTests.NetDeployWirePower); the power assertions mirror PowerTests (a generator
    // wired to a spotlight => 4000W at the consumer, Powered, 250W generator load).
    //
    // PARITY, two ways: (1) the server + client solvers agree on the replicated INPUTS (no solver output ever
    // crosses the wire, §3.1); (2) a directly-spawned SP rig (the exact PowerRig the PowerTests use) placed in
    // the SAME world produces the identical consumer-port state as the consumed replica -- consume == direct.
    //
    // TEETH: the placement must arrive with a real server-assigned NetId (a direct SP spawn stamps NetId 0),
    // and the consumed lamp must end up POWERED/LIT. If the seam or the replica view were absent, the local
    // node would never materialize over the wire -- NetId would be 0 and the lamp would be unpowered, failing
    // these checks. So this test cannot pass on the old publish-only loopback.
    public class UnifyDeployConsumeParity : GameTest
    {
        public override string Name => "unify.deploy_consume_parity";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            // the ONE world path, dedicated mode (flat fallback on CI -- the bogus map forces it, no sockets)
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // `give 458/459` resolve against the real catalog (2x2 -> fit the demo pockets)

            // net stack over MemTransport -- ONE client, the local loopback player's stand-in (it places AND
            // consumes, exactly as MpLoopback does under --spconsume). Client pump is registered BEFORE the
            // DedicatedServer so each tick runs delivery + client BEFORE the server sim, replicate staying LAST (§2.5).
            var net = new MemNetwork(20260719);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            DeployableNetSchema.RegisterAll(client.Deployables.Schema);   // (server side is registered by DedicatedServer)
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), AllowCheats = true };   // console-give stocks the player; cheats OFF by default on the real server
            World.AddChild(ded);

            // the P1 pattern: the local view materializes the server's deployable/wire entities into real
            // nodes -- the SOLE spawner of local deployable nodes when the seam routes over the wire.
            var view = new DeployableReplicaView { Client = client };
            World.AddChild(view);

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // stock the player through the server-gated console (§2.3), then place the rig OVER THE WIRE --
            // the same Client.Send* the MpLoopback seams now invoke instead of the direct SP path.
            client.SendConsole("give 458");
            client.SendConsole("give 459");
            yield return Until(() =>
                ded.Server.Inventories.TryGet(client.PlayerId, out var inv)
                && inv.Inventory.getItemCount(458) == 1 && inv.Inventory.getItemCount(459) == 1, 5);
            T.Check("server stocked the local player (generator + spotlight)",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sInv)
                    && sInv.Inventory.getItemCount(458) == 1 && sInv.Inventory.getItemCount(459) == 1);

            client.SendPlaceDeployable(458, new UnityEngine.Vector3(-2f, 0f, 0f), 0f);   // generator
            client.SendPlaceDeployable(459, new UnityEngine.Vector3(2f, 0f, 0f), 0f);    // spotlight
            yield return Until(() => client.Deployables.Count == 2, 5);
            uint genId = 0, spotId = 0;
            foreach (var e in client.Deployables.All)
            {
                if (e.DefId == 458) genId = e.NetIdValue;
                if (e.DefId == 459) spotId = e.NetIdValue;
            }
            // TEETH: a server-assigned NetId proves the entity came OVER THE WIRE, not from a direct SP spawn
            // (Deployable.Spawn leaves NetId 0). No seam / no replica -> these stay 0 and the test fails here.
            T.Check($"placements replicated back with server NetIds (gen {genId}, spot {spotId})", genId != 0 && spotId != 0);

            client.SendConnectWire(genId, 0, spotId, 0);   // gen Output(port 0) -> spot Consumer(port 0)
            client.SendToggleDeployable(genId, true);
            yield return Until(() => client.Deployables.WireCount == 1
                                  && client.Deployables.TryGet(genId, out var g) && g.ToggledOn, 5);
            T.Check("client mirrors the full graph (2 deployables + 1 wire + toggle) from broadcast facts",
                    client.Deployables.Count == 2 && client.Deployables.WireCount == 1);
            T.Check("graph parity: client replica == server authority", client.Deployables.StateHash() == ded.Server.Deployables.StateHash());

            // PARITY (1): both solve the replicated INPUTS with the SAME pure PowerSolver and agree -- 250W
            // generator load, consumer powered. No solver output ever crossed the wire (§3.1). 250W is the
            // known-correct direct-SP value (PowerTests.power.gen_powers_spotlight asserts the same load).
            ded.Server.Deployables.Solve();
            client.Deployables.Solve();
            ded.Server.Deployables.TryGet(spotId, out var sSpot);
            client.Deployables.TryGet(spotId, out var cSpot);
            T.Check("server solve: consumer powered", sSpot.Solved[0].Powered);
            T.Check("client replica solve: consumer powered (same solver, same inputs)", cSpot.Solved[0].Powered);
            ded.Server.Deployables.TryGet(genId, out var sGen);
            client.Deployables.TryGet(genId, out var cGen);
            T.Check($"both agree on generator load == 250W (server {sGen.Solved[0].Draw:0}, client {cGen.Solved[0].Draw:0})",
                    sGen.Solved[0].Draw == cGen.Solved[0].Draw && cGen.Solved[0].Draw == 250f);

            // the consume payoff: the replica view materialized the graph into REAL local nodes, and the local
            // PowerNet (the SP power path, unchanged) lights the lamp off the replicated inputs -- exactly as SP.
            yield return Until(() => view.NodeCount == 2, 5);
            T.Check("replica view materialized both deployable nodes", view.NodeCount == 2);
            bool gotSpot = view.TryGetNode(spotId, out var spotNode);
            // TEETH: the materialized node carries the SERVER NetId -> it came from the replica, not a direct spawn
            T.Check($"materialized spotlight node stamped with the server NetId ({(gotSpot ? spotNode.NetId : 0)})",
                    gotSpot && spotNode.NetId == spotId && spotNode.NetId != 0);
            yield return Until(() => view.TryGetNode(spotId, out var d) && d.DebugConsumerPowered, 5);
            T.Check("consumed spotlight's consumer is POWERED through the local PowerNet node-graph",
                    view.TryGetNode(spotId, out var pn) && pn.DebugConsumerPowered);
            yield return Until(() => view.TryGetNode(spotId, out var d) && d.DebugLampsLit, 5);
            T.Check("consumed spotlight's lamp is LIT (warmup envelope past the visibility floor)",
                    view.TryGetNode(spotId, out var ln) && ln.DebugLampsLit);

            // PARITY (2): stand up the EXACT direct-SP rig the PowerTests use, in the SAME world (disjoint
            // power component -- PowerNet solves per connected graph), and compare the consumed-replica
            // spotlight's consumer port to the directly-spawned one. consume == direct, port for port.
            var direct = PowerRig.Build(World);   // gen->spot->wire, DeployableDef.Generator/Spotlight (the SP path)
            yield return Ticks(1);                 // let the direct nodes enter the tree
            direct.Gen.TogglePower();              // the SP toggle (CanTogglePower gate) -- the replica used NetSetPowered
            PowerNet.Recompute(Tree);              // one global solve covers both disjoint rigs
            var replicaCons = view.TryGetNode(spotId, out var rn)
                ? rn.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer) : null;
            T.Check("consumed replica exposes a real consumer port", replicaCons != null);
            T.Check("consume == direct: both spotlight consumers powered",
                    replicaCons != null && replicaCons.Powered && direct.ConsA.Powered
                    && replicaCons.Powered == direct.ConsA.Powered);
            T.Check($"consume == direct: same consumer wattage (consume {replicaCons?.Live:0}W, direct {direct.ConsA.Live:0}W == 4000W)",
                    replicaCons != null && PowerRig.Approx(replicaCons.Live, direct.ConsA.Live)
                    && PowerRig.Approx(direct.ConsA.Live, 4000f));

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }
}
