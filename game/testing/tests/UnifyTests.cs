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

    // B2 (SP/MP-unify): hold-F pickup of a placed deployable must route over the wire under the consuming
    // loopback. The SHIPPED SOLO BUG was PlayerController.PickupDeployable @677 early-returning on
    // `d.NetId != 0` -- under --spconsume deployables ARE replica nodes (NetId!=0), so retrieving a placed
    // generator did NOTHING. The fix drops that guard and routes the pickup as an intent (NetPickupDeployable
    // -> Client.SendPickupDeployable); the server tears the entity down + hands the item back through the
    // owner-inventory echo, and the DeployableReplicaView retires the node off EventDeployableRemoved.
    //
    // Built on the REAL node graph the SP game boots (a PlayerController shell + the actual MpLoopback under
    // ConsumeDeployables), verbatim from unify.loopback_upgrade_skill -- a substituted seam would mask the bug.
    //
    // TEETH: PickupDeployable is called on the MATERIALIZED replica node (NetId!=0). Pre-fix the @677
    // early-return leaves the node standing AND sends no item echo -- both Until()s below time out. Post-fix
    // the node retires and the damaged generator (quality 50) lands back in the local bag.
    public class UnifyLoopbackPickupDeployable : GameTest
    {
        public override string Name => "unify.deploy_pickup";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // `give 458` resolves against the real catalog; the returned item fits the demo bag

            // the same headless-safe SP stand-ins unify.loopback_upgrade_skill feeds AttachMpLoopback
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // the REAL shell the loopback drives + adopts onto
            yield return Ticks(2);   // let _Ready build the shell Inventory

            // the REAL consuming loopback -- the exact node AttachMpLoopback builds under --spconsume
            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);

            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected + server inventory present",
                    loop.Client.State == NetSessionState.Connected
                    && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _));

            // stock the server grid with a generator (console-give, server-gated), then place it OVER THE WIRE --
            // the DeployableReplicaView materializes it into a real local node stamped with the server NetId.
            loop.Client.SendConsole("give 458");
            yield return Until(() => loop.Server.Inventories.TryGet(loop.Client.PlayerId, out var si)
                                     && si.Inventory.getItemCount(458) == 1, 10);
            loop.Client.SendPlaceDeployable(458, new UnityEngine.Vector3(2f, 0f, 0f), 0f);
            yield return Until(() => loop.Deploys != null && loop.Deploys.NodeCount == 1, 10);

            uint genId = 0;
            foreach (var e in loop.Client.Deployables.All) if (e.DefId == 458) genId = e.NetIdValue;
            // TEETH: a server NetId proves the node came from the replica view, not a direct SP spawn (NetId 0)
            T.Check($"generator materialized as a replica node with a server NetId ({genId})",
                    genId != 0 && loop.Deploys.TryGetNode(genId, out _));
            // the placement spent the item off the server grid; the owner echo emptied the local bag
            yield return Until(() => player.Inventory.getItemCount(458) == 0, 10);
            T.Check("(baseline) local bag holds no generator after placing it", player.Inventory.getItemCount(458) == 0);

            // damage the generator server-side so the returned item carries real state: 225/450 HP -> quality 50
            loop.Server.Deployables.ServerSetScalars(genId, 225f, 30f, onFire: false, loop.Server.Session.CurrentTick);

            // THE ACT: hold-F pickup on the materialized replica node. Pre-fix @677 early-returns on NetId!=0.
            loop.Deploys.TryGetNode(genId, out var pick);
            T.Check("got the materialized node to pick up", pick != null && pick.NetId == genId);
            player.PickupDeployable(pick);

            // GATE 1: the node retires off the server's EventDeployableRemoved (send-and-return, no local mutation)
            yield return Until(() => !loop.Deploys.TryGetNode(genId, out _) && loop.Deploys.NodeCount == 0, 15);
            T.Check("(gate) the replica node retired after the wire pickup", loop.Deploys.NodeCount == 0);

            // GATE 2: the generator returned to the local bag via the owner-inventory echo, HP quality intact
            yield return Until(() => player.Inventory.getItemCount(458) == 1, 15);
            T.Check($"(gate) the generator is back in the local bag ({player.Inventory.getItemCount(458)})",
                    player.Inventory.getItemCount(458) == 1);
            var back = FindBagItem(player.Inventory, 458);
            T.Check($"(gate) the returned item carries the stamped HP quality (q{back?.quality}, expected 50)",
                    back != null && back.quality == 50);
        }

        static SDG.Unturned.Item FindBagItem(SDG.Unturned.PlayerInventory inv, ushort id)
        {
            foreach (var page in inv.items)
                foreach (var jar in page.items)
                    if (jar.item != null && jar.item.id == id) return jar.item;
            return null;
        }
    }

    // A1 + B9 (SP/MP-unify): a world-build CONTAINER round-trips end-to-end through the REAL consuming loopback --
    // ContainerNetSync registers it server-side (fixture + a crate grid under one NetId), the StorageReplicaView
    // materializes it as a ServerOwned StoreShelf node (no local loot roll), and OpenCrate on that node opens the
    // dashboard OVER THE WIRE (B9), never a local copy-back. Built on the exact MpLoopback AttachMpLoopback builds
    // under --spconsume, with the SP-local SpawnMapContainers gated OFF -- so a server NetId on the node PROVES it
    // came from the replica, not an SP spawn. (The loot roll + display digest are L0-covered in ContainerReplicationTests.)
    public class UnifyContainerMaterializeAndOpen : GameTest
    {
        public override string Name => "unify.container_materialize";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // makeLoot / display resolve against the real catalog

            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // the REAL shell the loopback drives
            yield return Ticks(2);

            // a one-container manifest -- a store gondola at (2,0,0), the exact shape WorldBuilder.result.Containers carries
            var manifest = new System.Collections.Generic.List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)>
            {
                ("Shelf_1", 6, true, "Store Shelf", new Vector3(2f, 0f, 0f), 0f),
            };

            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        Containers = manifest,
                                        ConsumeDeployables = true };
            World.AddChild(loop);

            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected", loop.Client.State == NetSessionState.Connected);

            // ContainerNetSync registered the fixture server-side + the StorageReplicaView materialized it locally
            yield return Until(() => loop.Storage != null && loop.Storage.NodeCount == 1, 15);
            T.Check("the container materialized as a replica StoreShelf node", loop.Storage != null && loop.Storage.NodeCount == 1);

            uint cid = 0;
            foreach (var e in loop.Client.Containers.All) cid = e.NetIdValue;
            // TEETH: a server NetId proves the node came from the replica view, not the SP SpawnMapContainers (gated off)
            T.Check($"container has a server NetId ({cid})", cid != 0 && loop.Storage.TryGetNode(cid, out _));
            loop.Storage.TryGetNode(cid, out var shelfNode);
            T.Check("the materialized shelf is ServerOwned (no local loot roll)", shelfNode != null && shelfNode.ServerOwned);

            // the server owns the authoritative crate grid under the SAME NetId (ContainerNetSync mints one id for both)
            T.Check("the server registered the crate grid under the fixture's NetId",
                    loop.Server.Inventories.TryGetCrate(cid, out _));

            // THE ACT (B9): open the replicated container. OpenCrate must route NetId!=0 over the wire, not copy locally.
            T.Check("OpenCrate routed the replicated container over the wire (B9)", player.OpenCrate(shelfNode));

            // GATE: the server validated (reach) + the StorageOpened fact opened the dashboard with the server's grid
            yield return Until(() => player.DashboardOpen, 15);
            T.Check("(gate) the storage dashboard opened on the server's StorageOpened fact", player.DashboardOpen);
        }
    }

    // A1 (regression, master 2026-07-20 empty-shelves fix): with loot tables LOADED, a display shelf's tier loot
    // projects end-to-end through the consume path -- ContainerNetSync rolls into the crate, the display digest carries
    // the items, and StorageReplicaView.ApplyDisplay applies them onto the shelf's grid. The bug was the tables never
    // loading under consume (LootTables.Load lived in the gated-off SpawnMapContainers -> empty crates -> bare shelves);
    // this guards that once they ARE loaded the projection works. Uses an injected table (no real Items.dat needed).
    public class UnifyContainerLoot : GameTest
    {
        public override string Name => "unify.container_loot";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            // deterministic table 0: one always-hit tier of real demo items (so tryAddItem sizes + places them)
            LootTables.ResetForTests();
            LootTables.LoadTiersForTests(
                new (float, ushort[])[][] { new (float, ushort[])[] { (1f, new ushort[] { 209, 253, 458 }) } },
                new[] { "TestLoot" });

            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var manifest = new System.Collections.Generic.List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)>
            {
                ("Shelf_1", 0, true, "Store Shelf", new Vector3(2f, 0f, 0f), 0f),   // display gondola rolling table 0
            };
            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources, Containers = manifest, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected, 15);

            // the container replicates + its display digest carries the ROLLED loot (the fix's whole point)
            yield return Until(() => loop.Client.Containers.Count == 1, 15);
            uint cid = 0; int cells = 0;
            foreach (var e in loop.Client.Containers.All) { cid = e.NetIdValue; cells = e.Display.Length; }
            T.Check($"the shelf's display digest carries rolled loot ({cells} cells)", cells > 0);

            // and StorageReplicaView applied it onto the materialized shelf's grid (ApplyDisplay with real items)
            yield return Until(() => loop.Storage != null && loop.Storage.TryGetNode(cid, out _), 15);
            loop.Storage.TryGetNode(cid, out var shelf);
            T.Check($"(gate) the materialized shelf shows loot on its tiers ({shelf?.Storage?.getItemCount()} items)",
                    shelf != null && shelf.Storage != null && shelf.Storage.getItemCount() > 0);
        }
    }

    // A5 (SP/MP-unify): wildlife round-trips through the REAL consuming loopback -- a host AnimalAgent brain (as
    // AnimalField spawns) is published by AnimalNetSync into AnimalReplication, replicates to the (loopback)
    // client, and AnimalPuppets materializes it from the replica by species. Mirrors the zombie brain/puppet
    // split. Proves the server-publish + client-materialize halves; the wire parity is L0 (AnimalReplicationTests).
    public class UnifyAnimalMaterialize : GameTest
    {
        public override string Name => "unify.animal_materialize";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected, 15);

            // spawn a real AnimalAgent in the "animals" group (as AnimalField does on the host) -- a cow (species 2)
            var agent = new AnimalAgent { Species = 2, Home = new Vector3(3f, 0f, 0f) };
            World.AddChild(agent);
            agent.GlobalPosition = new Vector3(3f, 0f, 0f);
            agent.Begin();   // joins the "animals" group + starts the wander state machine
            T.Check("the agent joined the animals group", agent.IsInGroup("animals"));

            // AnimalNetSync publishes it -> a server entity exists + the loopback client received the replica
            yield return Until(() => loop.AnimalSync != null && loop.AnimalSync.TrackedCount == 1
                                     && loop.Server.Animals.Count == 1, 15);
            T.Check("AnimalNetSync published the agent as a server entity", loop.Server.Animals.Count == 1);
            yield return Until(() => loop.Client.Animals.Count == 1, 15);
            T.Check("the animal replicated to the client", loop.Client.Animals.Count == 1);

            uint aid = 0; byte species = 255;
            foreach (var e in loop.Client.Animals.All) { aid = e.NetIdValue; species = e.Species; }
            T.Check($"the species byte replicated (cow=2, got {species})", species == 2);

            // materialize a puppet from the replica (the joined-client AnimalPuppets path) -> proves the client half
            var pups = new AnimalPuppets { Client = loop.Client };
            World.AddChild(pups);
            yield return Until(() => pups.PuppetCount == 1 && pups.TryGetPuppet(aid, out _), 15);
            T.Check("AnimalPuppets materialized the replicated animal", pups.PuppetCount == 1);

            // retire the brain (streamed out) -> the entity + the puppet both retire
            agent.QueueFree();
            yield return Until(() => loop.Server.Animals.Count == 0 && loop.Client.Animals.Count == 0
                                     && pups.PuppetCount == 0, 15);
            T.Check("(gate) freeing the brain retired the replica + the puppet", pups.PuppetCount == 0);
        }
    }

    // B10 (SP/MP-unify): a player's worn clothing round-trips through the REAL consuming loopback --
    // PlayerAppearanceNetSync reads the server-side worn inventory + publishes it into the combat block, which
    // replicates; and RemotePlayers.ApplyWorn reconstructs the worn slots from that replica (the render's core,
    // exercised directly since a puppet only spawns for a networked REMOTE player). The wire parity is L0
    // (PlayerAppearanceReplicationTests). Held-gun id is deferred (needs PlayerStateCommand.HeldItemId).
    public class UnifyPlayerAppearance : GameTest
    {
        public override string Name => "unify.player_appearance";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;

            // dress the player's SERVER-side inventory (as the consuming wear path populates it)
            loop.Server.Inventories.TryGet(pid, out var sinv);
            sinv.Inventory.wearShirt(new SDG.Unturned.Item(3));       // Orange Hoodie
            sinv.Inventory.wearPants(new SDG.Unturned.Item(209));     // Cargo Pants
            sinv.Inventory.wearBackpack(new SDG.Unturned.Item(253));  // Alicepack

            // PlayerAppearanceNetSync publishes it into the combat block -> replicated to the (loopback) client
            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var ce) && ce.WornShirt == 3, 15);
            loop.Client.CombatState.TryGet(pid, out var c1);
            T.Check("worn shirt published + replicated", c1.WornShirt == 3);
            T.Check($"worn pants replicated ({c1.WornPants})", c1.WornPants == 209);
            T.Check($"worn backpack replicated ({c1.WornBackpack})", c1.WornBackpack == 253);

            // RENDER: RemotePlayers reconstructs the worn slots from the replica (the outfit a joiner would dress)
            var puppetInv = new SDG.Unturned.PlayerInventory();
            RemotePlayers.ApplyWorn(puppetInv, c1);
            T.Check($"puppet worn shirt reconstructed ({puppetInv.wornShirt?.id})", puppetInv.wornShirt != null && puppetInv.wornShirt.id == 3);
            T.Check("puppet worn pants reconstructed", puppetInv.wornPants != null && puppetInv.wornPants.id == 209);
            T.Check("puppet has no hat (unworn slot stays empty)", puppetInv.wornHat == null);

            // a change re-publishes (swap the shirt off) -> the replica + a fresh reconstruction both clear it
            sinv.Inventory.wearShirt(null);
            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var ce) && ce.WornShirt == 0, 15);
            loop.Client.CombatState.TryGet(pid, out var c2);
            RemotePlayers.ApplyWorn(puppetInv, c2);
            T.Check("(gate) removing the shirt re-published + the puppet un-dressed it", c2.WornShirt == 0 && puppetInv.wornShirt == null);
        }
    }

    // SP/MP-unify P1b phase gate (SECOND pattern-setter). Closes the gap P1 surfaced: a wire placement (P1)
    // SPENDS an item server-side BEFORE broadcasting, so the local loopback player's SERVER inventory must be
    // STOCKED + owner-replicated or a real placement is server-REJECTED. This proves the server-authoritative
    // inventory end-to-end -- the exact shape MpLoopback now takes under --spconsume (seed the demo kit into
    // the server grid on join, route the grid/consume seams over the wire, adopt the owner block).
    //
    // Net setup modeled on unify.deploy_consume_parity + NetTests: ONE client stands in for the local loopback
    // player. It drives inventory/placement over the wire (Client.Send*, as MpLoopback's seams now do) and
    // reads its OWN owner-replicated inventory (client.Inventories -- exactly what AdoptReplicatedInventory
    // copies into the shell). The DedicatedServer seeds the demo kit into the SERVER grid on join
    // (DedicatedServer:67-71 = the same PopulateDemoKit seed MpLoopback now does under --spconsume).
    //
    // TEETH (the whole point of server-authority inventory): a placement of an item the SERVER grid does NOT
    // hold is REJECTED -- no spend, no materialization. And after the seeded item is spent, a SECOND identical
    // placement is REJECTED too, proving the decrement was real on the authoritative grid, not cosmetic. On
    // the old empty-server-inventory loopback the spend could never validate, so this test cannot pass there.
    //
    // AllowCheats stays OFF: this phase seeds the server grid DIRECTLY (as P1b does) and spends the SEEDED
    // item -- NOT console-give (that was P1's stand-in). The spend must validate against a real server bag.
    public class UnifyInventoryAuthority : GameTest
    {
        public override string Name => "unify.inventory_authority";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // item 458 (generator) + 95 (Bandage) resolve against the real catalog

            // net stack over MemTransport -- ONE client, the local loopback player's stand-in. Client pump is
            // registered BEFORE the DedicatedServer so each tick runs delivery + client BEFORE the server sim,
            // replicate staying LAST (§2.5). Cheats OFF: no console-give -- the server grid is seeded directly.
            var net = new MemNetwork(20260713);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            DeployableNetSchema.RegisterAll(client.Deployables.Schema);   // (server side is registered by DedicatedServer)
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);   // seeds the demo kit into the SERVER grid on join (DedicatedServer:67-71 = the P1b seed)

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // the P1b invariant: the server OWNS a stocked inventory for the local player (demo kit seeded on
            // join), and it is owner-replicated back to the client -- the grid the shell adopts.
            yield return Until(() => ded.Server.Inventories.TryGet(client.PlayerId, out var s0)
                                  && s0.Inventory.getItemCount(4) == 1, 5);
            T.Check("server owns a STOCKED inventory for the local player (demo-kit Eaglefire present)",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sBag) && sBag.Inventory.getItemCount(4) == 1);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var r0)
                                  && r0.Inventory.getItemCount(4) == 1, 5);
            T.Check("the owner block replicates the stocked inventory back (what AdoptReplicatedInventory copies in)",
                    client.Inventories.TryGet(client.PlayerId, out var rBag) && rBag.Inventory.getItemCount(4) == 1);

            // TEETH #1 -- empty of the deployable: the demo kit holds NO generator (458). A wire placement of an
            // item the SERVER grid doesn't hold is REJECTED at the validator (getItemCount(458) > 0 is false):
            // no spend, no broadcast, no materialization. This is exactly the gap P1 surfaced.
            client.SendPlaceDeployable(458, new UnityEngine.Vector3(-2f, 0f, 0f), 0f);
            yield return Ticks(20);
            T.Check("placement REJECTED with no 458 in the server grid (no materialization)", client.Deployables.Count == 0);
            T.Check("nothing spent from an item the grid never held",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sEmpty) && sEmpty.Inventory.getItemCount(458) == 0);

            // SEED a placeable deployable into the SERVER grid directly (models the P1b seed granting a stocked
            // item -- here a generator so the placement can SPEND it). Not console-give: the spend must validate
            // against a REAL server inventory, so a real item goes in it.
            ded.Server.Inventories.TryGet(client.PlayerId, out var bag);
            bool seeded = bag.Inventory.tryAddItem(new Item(458));
            T.Check("seeded a generator (458) into the local player's SERVER inventory", seeded && bag.Inventory.getItemCount(458) == 1);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var r1) && r1.Inventory.getItemCount(458) == 1, 5);
            T.Check("the seeded item replicated to the owner block", client.Inventories.TryGet(client.PlayerId, out var r1b) && r1b.Inventory.getItemCount(458) == 1);

            // (a) + (b): a REAL placement over the wire spending the SEEDED item -- assert BOTH the deployable
            // materialized with a server NetId AND the server-side inventory item was CONSUMED (1 -> 0), proving
            // the spend validated against the real server grid (P1's console-give never proved the CONSUME half).
            client.SendPlaceDeployable(458, new UnityEngine.Vector3(-2f, 0f, 0f), 0f);
            yield return Until(() => client.Deployables.Count == 1, 5);
            uint genId = 0;
            foreach (var e in client.Deployables.All) if (e.DefId == 458) genId = e.NetIdValue;
            T.Check($"(a) the deployable MATERIALIZED over the wire with a server NetId ({genId})", genId != 0);
            yield return Until(() => ded.Server.Inventories.TryGet(client.PlayerId, out var s2) && s2.Inventory.getItemCount(458) == 0, 5);
            T.Check("(b) the SERVER inventory item was CONSUMED (spend validated against the real server grid)",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sSpent) && sSpent.Inventory.getItemCount(458) == 0);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var r2) && r2.Inventory.getItemCount(458) == 0, 5);
            T.Check("the consumed spend echoed to the owner block (the shell's bag would mirror it)",
                    client.Inventories.TryGet(client.PlayerId, out var rSpent) && rSpent.Inventory.getItemCount(458) == 0);

            // TEETH #2 -- the item was really spent: a second identical placement is REJECTED (count is truly 0
            // on the authoritative grid). Proves the decrement was real, not a cosmetic client prediction.
            client.SendPlaceDeployable(458, new UnityEngine.Vector3(-4f, 0f, 0f), 0f);
            yield return Ticks(20);
            T.Check("second placement REJECTED -- the seeded item was really spent (still just 1 deployable)", client.Deployables.Count == 1);

            // (move round-trip): drag a demo Bandage (95, 1x1) to a free cell in the POCKETS page over the wire.
            // Assert the REPLICATED owner inventory reflects the new layout -- the grid the shell adopts.
            var spg = bag.Inventory.items[2];   // POCKETS (server-side truth)
            byte sx = 255, sy = 255;
            for (byte i = 0; i < spg.getItemCount(); i++) { var j = spg.getItem(i); if (j.item?.id == 95) { sx = j.x; sy = j.y; break; } }
            T.Check("found a demo Bandage on the server POCKETS grid", sx != 255);
            byte dx = 255, dy = 255;
            for (byte y = 0; y < spg.height && dx == 255; y++)
                for (byte x = 0; x < spg.width && dx == 255; x++)
                    if (spg.checkSpaceEmpty(x, y, 1, 1, 0)) { dx = x; dy = y; }
            T.Check("found a free destination cell", dx != 255 && (dx != sx || dy != sy));
            long movesBefore = ded.Server.Transactions.Diag.GridMovesApplied;
            client.SendMoveItem(2, sx, sy, 2, dx, dy, 0);
            yield return Until(() => ded.Server.Transactions.Diag.GridMovesApplied == movesBefore + 1, 5);
            T.Check("(move) the SERVER grid applied the drag", ded.Server.Transactions.Diag.GridMovesApplied == movesBefore + 1);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var rm)
                                  && rm.Inventory.items[2].getIndex(dx, dy) != byte.MaxValue, 5);
            bool moved = client.Inventories.TryGet(client.PlayerId, out var rmv)
                      && rmv.Inventory.items[2].getIndex(dx, dy) != byte.MaxValue
                      && rmv.Inventory.items[2].getItem(rmv.Inventory.items[2].getIndex(dx, dy)).item?.id == 95;
            T.Check("(move) the REPLICATED owner inventory reflects the new layout (what the shell adopts)", moved);

            // (consume round-trip): eat a demo Bandage (95) over the wire -- assert the SERVER count decremented
            // AND the replicated owner block echoed it (the server deletes by id; the cell just names one).
            int serverBefore = bag.Inventory.getItemCount(95);
            T.Check("server grid carries the demo Bandages", serverBefore >= 2);
            byte cx = 255, cy = 255;
            for (byte i = 0; i < spg.getItemCount(); i++) { var j = spg.getItem(i); if (j.item?.id == 95) { cx = j.x; cy = j.y; break; } }
            T.Check("found a Bandage cell to name in the consume", cx != 255);
            long consumesBefore = ded.Server.Transactions.Diag.ConsumesApplied;
            client.SendConsume(2, cx, cy);
            yield return Until(() => ded.Server.Transactions.Diag.ConsumesApplied == consumesBefore + 1, 5);
            T.Check("(consume) the SERVER deleted a Bandage (Diag.ConsumesApplied bumped)",
                    ded.Server.Transactions.Diag.ConsumesApplied == consumesBefore + 1);
            yield return Until(() => ded.Server.Inventories.TryGet(client.PlayerId, out var s3) && s3.Inventory.getItemCount(95) == serverBefore - 1, 5);
            T.Check("(consume) the SERVER Bandage count decremented",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sCons) && sCons.Inventory.getItemCount(95) == serverBefore - 1);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var r3) && r3.Inventory.getItemCount(95) == serverBefore - 1, 5);
            T.Check("(consume) the replicated owner block echoed the deletion",
                    client.Inventories.TryGet(client.PlayerId, out var rCons) && rCons.Inventory.getItemCount(95) == serverBefore - 1);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }

    // GAP B4 (guard-fix): the InventoryUI "Use" button (UseSelected) must ROUTE the delete through
    // NetConsume (like TickConsume's completion) and SKIP its local decrement -- not decrement the local
    // jar and leave the server grid untouched. This drives the REAL UI action via DebugUse over a connected
    // ClientWorldSession shell (the same net stack net.shell_consume uses), through the RequestConsume seam.
    //
    // TWO amount-1 jars (Unturned consumables DON'T stack -- each is its own grid item; that's also what makes
    // the owner echo fire: the inventory dirty flag rides Items.onStateUpdated, which fires on a whole-jar
    // add/remove but NOT on a bare in-place jar.amount--, so a partial-stack decrement wouldn't replicate --
    // out of scope for this guard-fix). count 2 -> 1 = one jar deleted server-side, exactly OnConsume's remove.
    //
    // TEETH (the resurrect bug, exactly what ClientWorldSession.cs:193-194 warns about -- "consume decrement is
    // resurrected by the next full-state echo"): PRE-FIX, UseSelected removes only the LOCAL jar and never calls
    // NetConsume, so (a) the SERVER grid still holds 2 (Until times out, assert fails) and Diag.ConsumesApplied
    // never bumps, and (c) the next Inventories.ReplicaUpdated -> AdoptReplicatedInventory re-adopts the server's
    // 2, jumping the local bag BACK UP to 2 (the resurrect) -- so "stays 1 after an echo" fails. POST-FIX the
    // server owns the delete (2->1) and the echo repaints 1 with no resurrect. Mirror of net.shell_consume, but
    // driven through the UI Use button rather than the held-consume eat timer.
    public class UnifyUseButtonConsume : GameTest
    {
        public override string Name => "unify.use_button_consume";
        public override double TimeoutSimSeconds => 40;

        // the first grid cell of item `id` on page `p` -> its (x,y)
        static bool FindCell(PlayerInventory inv, byte p, ushort id, out byte x, out byte y)
        {
            var pg = inv.items[p];
            for (byte i = 0; i < pg.getItemCount(); i++)
            {
                var j = pg.getItem(i);
                if (j?.item != null && j.item.id == id) { x = j.x; y = j.y; return true; }
            }
            x = y = 0; return false;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();   // id 14 (Bottled Water) resolves as a WATER consumable

            var net = new MemNetwork(20260720);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "user" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server owns the local player's inventory", sHave);
            if (!sHave) yield break;
            yield return Ticks(10);

            // Seed exactly TWO consumables in the SERVER pockets grid: clear the demo Bottled Waters first so the
            // id count is unambiguous (getItemCount + OnConsume's remove-by-id), then add two amount-1 jars. Let
            // it replicate to the shell (a whole-jar add fires Items.onStateUpdated -> dirty -> owner echo).
            sInv.Inventory.removeItemAmount(14, 999);   // drop every demo Bottled Water across the bag
            bool s1 = sInv.Inventory.items[2].tryAddItem(new Item(14));
            bool s2 = sInv.Inventory.items[2].tryAddItem(new Item(14));
            T.Check("seeded two Bottled Waters into the SERVER pockets grid",
                    s1 && s2 && sInv.Inventory.getItemCount(14) == 2);
            T.Check("found a seeded Water cell to Use", FindCell(sInv.Inventory, 2, 14, out byte cx, out byte cy));
            FindCell(sInv.Inventory, 2, 14, out cx, out cy);

            yield return Until(() => sess.Shell.Inventory.getItemCount(14) == 2
                                  && sess.Shell.Inventory.items[2].getIndex(cx, cy) != byte.MaxValue, 5);
            T.Check("the shell adopted the two Waters (a Use-able cell at (2,cx,cy))",
                    sess.Shell.Inventory.getItemCount(14) == 2
                 && sess.Shell.Inventory.items[2].getIndex(cx, cy) != byte.MaxValue);

            // the InventoryUI wired exactly as the SP/MP shell wires it (Inv = the shell bag, Player = the shell)
            var ui = new InventoryUI { Inv = sess.Shell.Inventory, Player = sess.Shell };
            World.AddChild(ui);
            yield return Ticks(2);   // _Ready builds the storage columns so Refresh() is safe

            long consumesBefore = ded.Server.Transactions.Diag.ConsumesApplied;
            ui.DebugUse(2, cx, cy);   // the REAL Use-button path (UseSelected) -- routes RequestConsume, skips local decrement

            yield return Until(() => sInv.Inventory.getItemCount(14) == 1, 5);
            T.Check("(a) the SERVER grid decremented to 1 (Use routed NetConsume, not a local-only decrement)",
                    sInv.Inventory.getItemCount(14) == 1);
            T.Check("(b) Diag.ConsumesApplied bumped (the server owned the delete)",
                    ded.Server.Transactions.Diag.ConsumesApplied == consumesBefore + 1);

            // let several more owner-block echoes land AFTER the consume -- pre-fix this is where the local grid
            // resurrects (AdoptReplicatedInventory re-adopts the still-2 server grid). Post-fix it holds at 1.
            yield return Until(() => sess.Shell.Inventory.getItemCount(14) == 1, 5);
            yield return Ticks(20);
            T.Check("(c) the local bag STAYS at 1 after the echo -- no resurrect",
                    sess.Shell.Inventory.getItemCount(14) == 1);

            world.Sim.Sim.Remove(pump);
        }
    }

    // SP/MP-unify P2 phase gate. Proves the "local view CONSUMES a server WORLD-ITEM (dropped/loot) replica
    // instead of OWNING a direct SP node" seam -- the exact shape MpLoopback now takes under --spconsume
    // (a WorldItemReplicaView + the NetPickupItem wire seam), the direct world-item mirror of
    // unify.deploy_consume_parity. Net stack + pickup round trip modeled on that test + net.shell_pickup_item.
    //
    // A SINGLE client stands in for the local loopback player: it CONSUMES the server's world-item entities
    // through a WorldItemReplicaView (as MpLoopback now wires under --spconsume) AND drives the pickup over the
    // wire (Client.SendPickupItem -- what the NetPickupItem seam now invokes). The DedicatedServer spawns the
    // world item, validates the pickup (reach+facing), adds it to the P1b SERVER-authoritative inventory, and
    // broadcasts the removal; the owner block echoes the add (what AdoptReplicatedInventory copies into a shell).
    //
    // TEETH: the puppet must carry a real server-assigned NetId (a direct SP WorldItem.Spawn stamps 0); a pickup
    // of a NON-EXISTENT NetId is a no-op (the validator's _worldItems.TryGet fails -> no phantom item); a SECOND
    // pickup of the now-taken NetId is a no-op (count stays 1). Without the view + seam the puppet never
    // materializes and the pickup never lands -- this cannot pass on the old publish-only loopback.
    public class UnifyWorldItemConsume : GameTest
    {
        public override string Name => "unify.worlditem_consume";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // the puppet view + the join demo-kit seeding resolve against the catalog

            // net stack over MemTransport -- ONE client, the local loopback player's stand-in (it consumes AND
            // picks up, exactly as MpLoopback does under --spconsume). Client pump BEFORE the DedicatedServer so
            // each tick runs delivery + client BEFORE the server sim, replicate staying LAST (§2.5).
            var net = new MemNetwork(20260720);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);   // seeds the demo kit into the SERVER grid on join (the P1b server-authoritative bag)

            // the P2 pattern: the local view materializes the server's world-item entities into item puppets --
            // the SOLE materializer of these entities' local visuals when the pickup seam routes over the wire.
            var view = new WorldItemReplicaView { Client = client };
            World.AddChild(view);

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // the server's authoritative player entity for this peer -- its Pos/yaw are what the pickup validator
            // reach+facing-checks against. Spawn the item AT the player's feet so the facing cone is SKIPPED
            // (dist < PickupFacingSkipRange) and reach is trivially met -- robust without a shell driving yaw.
            yield return Until(() => ded.Server.Players.TryGetByOwner(client.PlayerId, out _), 5);
            T.Check("server owns the player entity for this peer", ded.Server.Players.TryGetByOwner(client.PlayerId, out _));
            ded.Server.Players.TryGetByOwner(client.PlayerId, out var me);

            // spawn a GENERATOR (458 -- NOT in the demo kit, so counts discriminate the pickup) on the SERVER at
            // the player's feet. This is the drop/loot analogue: OnDropItem funnels through this SAME SpawnWorldItem,
            // so a real over-the-wire drop (P1b's NetDropItem) produces exactly this entity for the view to consume.
            var e = ded.Server.Transactions.SpawnWorldItem(new Item(458), me.Pos, UnityEngine.Vector3.zero);
            T.Check("server spawned the world-item entity", e != null && ded.Server.WorldItems.Count == 1);

            // the consume payoff: the replica view materialized the entity into a real item PUPPET
            yield return Until(() => view.TryGetNode(e.NetIdValue, out _), 5);
            bool haveNode = view.TryGetNode(e.NetIdValue, out var node);
            T.Check("the item puppet materialized on the consuming view", haveNode);
            var wp = node as WorldItemPuppet;
            // TEETH: a server-assigned NetId proves the puppet came from the replicated ENTITY over the wire, not
            // a direct SP WorldItem.Spawn (which stamps NetId 0). No view -> no puppet -> fails here.
            T.Check($"the puppet carries the SERVER entity NetId ({(wp != null ? wp.NetId : 0)})",
                    wp != null && wp.NetId == e.NetIdValue && wp.NetId != 0);

            int serverBefore = ded.Server.Inventories.TryGet(client.PlayerId, out var sInv0) ? sInv0.Inventory.getItemCount(458) : -1;
            T.Check("the demo-kit server grid holds NO generator yet (the count discriminates)", serverBefore == 0);

            // TEETH #1 -- a pickup of a NON-EXISTENT NetId is a no-op: the validator's _worldItems.TryGet fails,
            // so nothing is spent/added and no phantom item appears (an unguarded handler would add a default
            // entity's ServerItem -> a ghost generator).
            client.SendPickupItem(0xDEADBEEFu);
            yield return Ticks(20);
            T.Check("(teeth) pickup of a non-existent NetId is a no-op -- the entity stays", ded.Server.WorldItems.Count == 1);
            T.Check("(teeth) no phantom item added to the server grid",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sPh) && sPh.Inventory.getItemCount(458) == 0);
            T.Check("(teeth) the puppet stayed", view.TryGetNode(e.NetIdValue, out _));

            // the real pickup, over the wire (the same Client.SendPickupItem the NetPickupItem seam now invokes)
            client.SendPickupItem(e.NetIdValue);

            // (a) the server world-item entity retired AND the puppet was diff-driven out of the world
            yield return Until(() => ded.Server.WorldItems.Count == 0, 5);
            T.Check("(a) the server world-item entity retired (pickup validated + WorldItemRemoved broadcast)",
                    ded.Server.WorldItems.Count == 0);
            yield return Until(() => !view.TryGetNode(e.NetIdValue, out _), 5);
            T.Check("(a) the puppet retired from the consuming view (diff-driven on WorldItemRemoved)",
                    !view.TryGetNode(e.NetIdValue, out _));

            // (b) the item landed in the P1b SERVER-authoritative inventory AND echoed to the owner block (the
            //     grid a shell would AdoptReplicatedInventory -- exactly what MpLoopback re-adopts locally)
            yield return Until(() => ded.Server.Inventories.TryGet(client.PlayerId, out var s1) && s1.Inventory.getItemCount(458) == 1, 5);
            T.Check("(b) the item landed in the SERVER-authoritative inventory (count 0 -> 1)",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var sInv) && sInv.Inventory.getItemCount(458) == 1);
            yield return Until(() => client.Inventories.TryGet(client.PlayerId, out var r1) && r1.Inventory.getItemCount(458) == 1, 5);
            T.Check("(b) the add echoed to the owner block (what the shell would adopt locally)",
                    client.Inventories.TryGet(client.PlayerId, out var rInv) && rInv.Inventory.getItemCount(458) == 1);

            // TEETH #2 -- a SECOND pickup of the now-taken NetId is a no-op: the entity is gone, so the validator
            // rejects and the count stays 1 (no phantom second generator from a re-picked ghost).
            client.SendPickupItem(e.NetIdValue);
            yield return Ticks(20);
            T.Check("(teeth) a second pickup of the taken NetId is a no-op -- count stays 1",
                    ded.Server.Inventories.TryGet(client.PlayerId, out var s2) && s2.Inventory.getItemCount(458) == 1);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }

    // SP/MP-unify P2b phase gate. Proves that under --spconsume a PASSIVE (world-streamed / salvage-spawned) loot
    // item is materialized on the host EXACTLY ONCE -- the WorldItemReplicaView puppet -- not twice. Under
    // --spconsume the host still owns real SP WorldItem NODES (LootField streaming, salvage scrap), AND
    // WorldItemNetSync mints server entities FROM those nodes, AND the P2 WorldItemReplicaView materializes those
    // same entities into puppets -- so WITHOUT P2b a passive item shows TWICE (its real SP node AND the puppet).
    // The fix (WorldItem.SuppressLocalVisual, which MpLoopback flips under --spconsume) hides the host's own node
    // and drops it off the look-hit layer while keeping it a live physics body in the "worlditems" group, so the
    // sync still publishes it for remote joiners; the puppet is the sole visible + focusable copy on the host.
    //
    // The passive pipeline is driven for real: a WorldItem NODE (exactly what LootField streams / salvage spawns)
    // is put in the world, and the DedicatedServer's own WorldItemNetSync (DedicatedServer:137-138) mints the
    // entity FROM it -- unlike unify.worlditem_consume, which spawns the entity directly (the player-DRIVEN path).
    //
    // TEETH: with the flag OFF (default SP + live MP-client path) a loot node IS a visible + focusable local
    // materialization -- asserted first, pinning the pre-fix doubling source. With the flag ON, the SAME node is
    // hidden + non-focusable, so among {the SP loot node, the view puppet} EXACTLY ONE is visible+focusable (the
    // puppet). On pre-P2b code the node stays visible+focusable -> the count is 2 -> the "== 1" gate FAILS.
    public class UnifyPassiveLootSingle : GameTest
    {
        public override string Name => "unify.passive_loot_single";
        public override double TimeoutSimSeconds => 30;

        // a world-item is LOCALLY materialized (visible + focusable) iff it renders AND carries a collider on the
        // item look-hit layer (bit 7 = WorldItem.ItemHitLayer, what the player's look-ray focuses). Works for both
        // the SP WorldItem RigidBody (itself a CollisionObject3D on bit 7) and the WorldItemPuppet (a Node3D whose
        // bit-7 StaticBody child is the look-detection body).
        static bool VisibleFocusable(Node3D n)
        {
            if (n == null || !GodotObject.IsInstanceValid(n) || !n.Visible) return false;
            if (n is CollisionObject3D self && (self.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            foreach (var ch in n.GetChildren())
                if (ch is CollisionObject3D co && (co.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // the item mesh/collider + the join demo-kit seeding resolve against the catalog

            // (teeth / control) flag OFF = default SP + live MP-client path: a streamed loot node materializes as a
            // VISIBLE + FOCUSABLE local node -- the very thing that, alongside the view's puppet, doubles passive loot
            // under --spconsume. Spawn one, assert it, then free it BEFORE the net stack so it never mints a puppet.
            WorldItem.SuppressLocalVisual = false;
            var control = WorldItem.Spawn(World, new Item(458), new Vector3(6f, 1f, 6f));
            T.Check("(teeth) flag OFF: a loot node is a VISIBLE + FOCUSABLE local materialization (the pre-fix doubling source)",
                    VisibleFocusable(control) && !control.LocalVisualSuppressed);
            control.QueueFree();
            yield return Ticks(1);

            // now the --spconsume posture: the host suppresses its OWN world-item nodes (what MpLoopback flips)
            WorldItem.SuppressLocalVisual = true;

            // net stack over MemTransport, modeled on unify.worlditem_consume. The DedicatedServer runs its own
            // WorldItemNetSync that mints entities FROM the "worlditems" group NODES -- the exact passive path
            // (LootField/salvage node -> sync -> entity), the mirror of that test's direct-SpawnWorldItem drop path.
            var net = new MemNetwork(20260724);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);

            // the P2 view -- the SOLE local materializer of world-item entities under --spconsume
            var view = new WorldItemReplicaView { Client = client };
            World.AddChild(view);

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // spawn a PASSIVE loot NODE -- a real SP WorldItem in the "worlditems" group, exactly what LootField
            // streams (and Deployable/Vehicle.Salvage spawn). Under the flag it is born suppressed.
            var lootNode = WorldItem.Spawn(World, new Item(458), new Vector3(-3f, 1f, -3f));
            T.Check("the passive loot NODE spawned into the worlditems group", lootNode.IsInGroup("worlditems"));
            T.Check("(fix) under --spconsume the host's own loot node is SUPPRESSED (hidden + off the look-hit layer)",
                    lootNode.LocalVisualSuppressed && !VisibleFocusable(lootNode));

            // the sync mints a server entity FROM the node (5 Hz), broadcasts it, and the view materializes a puppet
            yield return Until(() => ded.Server.WorldItems.Count == 1, 8);
            T.Check("WorldItemNetSync minted ONE server entity from the loot node (still published for remote joiners)",
                    ded.Server.WorldItems.Count == 1);
            uint netId = 0;
            foreach (var en in ded.Server.WorldItems.All) { netId = en.NetIdValue; break; }
            T.Check($"the minted entity carries a real server NetId ({netId})", netId != 0);

            yield return Until(() => view.TryGetNode(netId, out _), 8);
            bool havePuppet = view.TryGetNode(netId, out var puppetNode);
            T.Check("the WorldItemReplicaView materialized a puppet for the entity", havePuppet);
            var puppet = puppetNode as WorldItemPuppet;
            T.Check($"the puppet carries the server entity NetId ({(puppet != null ? puppet.NetId : 0)})",
                    puppet != null && puppet.NetId == netId && puppet.NetId != 0);
            T.Check("the puppet is the visible + focusable copy on the host", VisibleFocusable(puppet));

            // THE GATE: among the host's two representations of this ONE passive item -- the real SP loot node and
            // the view puppet -- EXACTLY ONE is visible + focusable. Pre-P2b the node is ALSO visible+focusable -> 2,
            // and this fails.
            int materializations = (VisibleFocusable(lootNode) ? 1 : 0) + (VisibleFocusable(puppet) ? 1 : 0);
            T.Check($"(gate) EXACTLY ONE visible+focusable materialization of the passive loot item (got {materializations}, the puppet)",
                    materializations == 1 && VisibleFocusable(puppet) && !VisibleFocusable(lootNode));

            // the suppression is host-local render/interaction ONLY: the node stayed a live physics body still in the
            // "worlditems" group, so WorldItemNetSync kept publishing it -- a remote joiner still gets the entity.
            T.Check("the suppressed node is still a live worlditems-group physics body (publishes for remote joiners)",
                    GodotObject.IsInstanceValid(lootNode) && lootNode.IsInGroup("worlditems") && ded.Server.WorldItems.Count == 1);

            // teardown: clear the process-global flag (belt-and-braces vs ResetGlobals) + unhook the pump
            WorldItem.SuppressLocalVisual = false;
            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }

    // SP/MP-unify P3a phase gate (part 1 of 3): server-authoritative HP ADOPTION on the owner shell. A real
    // first-person shell (ClientWorldSession) joins a DedicatedServer (PvP now ON) over MemTransport; the shell
    // mirrors the owner's replicated CombatEntity coarse Health into its own vitals each tick (the
    // AdoptReplicatedInventory/Skills analogue), so a HUD Player.Health read tracks server truth.
    //
    // TEETH (a): real server damage (QueueDebugPlayerDamage -> the SAME ApplyPlayerDamage the bullet/grenade/
    // melee paths funnel through, applied at the live tick inside Combat.Step) drops the ADOPTED shell HP to
    // match the server exactly, and adoption PINS it
    // (local regen can't drag it back up -- the last-writer rule). TEETH (d): PvP-on does NOT rubber-band a
    // LIVING owner -- the client-auth walk still adopts with ZERO recovs; owning HP is orthogonal to owning the
    // transform. On the pre-P3a shell (vitals local, PvP off) neither could be observed: the HUD showed a local
    // 100 while the server thought you were hurt/dead -- exactly the "rubber-band an unrendered death" this closes.
    public class UnifyVitalsAdopt : GameTest
    {
        public override string Name => "unify.vitals_adopt";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260721);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "adopter" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            T.Check("P3a posture: PvP is ON on the dedicated server", ded.Server.Combat.PvPEnabled);

            yield return Ticks(5);   // let a couple of ShellStep adoption passes run
            T.Check("the shell adopted server-authoritative vitals (NetVitalsAdopted)", sess.Shell.NetVitalsAdopted);
            T.Check($"adopted full health at spawn (shell {sess.Shell.Health:0})", Mathf.IsEqualApprox(sess.Shell.Health, 100f));

            // (d) a LIVING owner walks under client authority with PvP ON -- adoption of HP must not manufacture
            // a position recov (they are orthogonal). This is the "no rubber-band on a living owner" bar.
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            for (int i = 0; i < 100; i++) yield return Ticks(1);
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(20);
            T.Check($"(d) PvP-on did NOT rubber-band the living owner ({sess.RecovsApplied} recovs)", sess.RecovsApplied == 0);
            T.Check($"(d) a living owner keeps full HP with no combat (shell {sess.Shell.Health:0})", Mathf.IsEqualApprox(sess.Shell.Health, 100f));
            T.Check("(d) the walking owner is client-driven server-side", ded.Server.PlayerHost.IsClientDriven(sess.Client.PlayerId));

            // (a) real server damage through the production path: 40 -> exactly HealthExact 60 -> coarse 60. The
            // adopted shell HP must track it (HUD Player.Health), and adoption pins it (no local regen back to 100).
            ded.Server.Combat.QueueDebugPlayerDamage(sess.Client.PlayerId, 40f, 0);
            yield return Until(() => sess.Shell.Health <= 61f, 5);
            bool sOk = ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var sCs);
            T.Check("(a) server applied the damage (coarse Health 60, still alive)", sOk && sCs.Health == 60 && sCs.Alive);
            T.Check($"(a) the ADOPTED shell HP dropped to match the server (shell {sess.Shell.Health:0} == server 60)",
                    Mathf.IsEqualApprox(sess.Shell.Health, 60f));
            T.Check("(a) the shell is NOT rendering death (alive at 60)", !sess.Shell.IsDead);
            bool rOk = sess.Client.CombatState.TryGet(sess.Client.PlayerId, out var rCs);
            T.Check("(a) the owner's own combat replica agrees (Health 60)", rOk && rCs.Health == 60);

            // adoption is the LAST HP writer: over a full second of local ticks the shell must NOT regen back up
            yield return Ticks(50);
            T.Check($"(a) adoption pins HP -- no local regen ({sess.Shell.Health:0} still 60)", Mathf.IsEqualApprox(sess.Shell.Health, 60f));
            T.Check($"DESYNC-QUIET across the adoption run ({desyncs} fired)", desyncs == 0);

            world.Sim.Sim.Remove(pump);
        }
    }

    // SP/MP-unify P3a phase gate (part 2 of 3): server-authoritative DEATH/RESPAWN rendering + the recov
    // reposition -- the subtle correctness point of the phase. A real shell (ClientWorldSession) walks AWAY from
    // its spawn, then real server damage kills it: the server's PlayerDied fact renders death on the OWNER
    // (corpse, _dead), and -- because the server owns the 3.5 s clock -- the shell does NOT self-respawn; it
    // revives only when PlayerRespawned lands. The teeth: the respawn REPOSITION rides the recov/freeze-until-
    // echo primitive, NOT a bare ServerTeleport (which the client-auth owner's next PlayerStateCommand would
    // overwrite). Since NetRespawn deliberately does NOT reposition the shell, the ONLY thing that can move it
    // off the death spot back to spawn is the server recov -- so "shell ends at spawn, not the death spot" +
    // "the server recov counter bumped" together prove the recov path carried it. DESYNC-QUIET throughout.
    public class UnifyDeathRespawnRecov : GameTest
    {
        public override string Name => "unify.death_respawn_recov";
        public override double TimeoutSimSeconds => 45;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260722);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "diver" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;
            var myDeaths = new List<PlayerDiedEvent>();
            sess.Client.PlayerDied += myDeaths.Add;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            var spawn = sess.Shell.TruePhysicsPosition;

            // walk away from spawn so the death spot is far from where a respawn must land
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            for (int i = 0; i < 110; i++) yield return Ticks(1);
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(30);
            var deathSpot = sess.Shell.TruePhysicsPosition;
            float walked = spawn.DistanceTo(deathSpot);
            T.Check($"the shell walked well off its spawn ({walked:0.0} m)", walked > 3f);
            T.Check($"clean walk, ZERO recovs before death ({sess.RecovsApplied})", sess.RecovsApplied == 0);
            byte recovBefore = ded.Server.PlayerHost.DebugRecovCounter(sess.Client.PlayerId);
            T.Check("no recov yet server-side (counter 0)", recovBefore == 0);

            // (b) real server damage kills the owner -> the server death fact renders on the OWNER shell
            ded.Server.Combat.QueueDebugPlayerDamage(sess.Client.PlayerId, 200f, 0);
            yield return Until(() => sess.Shell.IsDead, 5);
            T.Check("(b) the server death fact RENDERED on the owner (shell _dead)", sess.Shell.IsDead);
            bool dOk = ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var dCs);
            T.Check("(b) server owns the death (Alive false, Health 0)", dOk && !dCs.Alive && dCs.Health == 0);
            bool sawMyDeath = false;
            foreach (var e in myDeaths) if (e.Victim == sess.Client.PlayerId) sawMyDeath = true;
            T.Check("(b) a PlayerDied fact for self reached the owner", sawMyDeath);

            // (c) the SERVER owns the 3.5 s clock: the shell revives only on the server's PlayerRespawned, and
            // the reposition to spawn rides the recov. RespawnDelayTicks = 175 ticks (3.5 s).
            yield return Until(() => !sess.Shell.IsDead, 8);
            T.Check("(c) the owner revived on the server respawn fact (shell no longer _dead)", !sess.Shell.IsDead);
            T.Check($"(c) the respawn rode the RECOV path client-side ({sess.RecovsApplied} recovs applied)", sess.RecovsApplied >= 1);
            T.Check($"(c) the server recov/freeze primitive fired (counter {recovBefore} -> {ded.Server.PlayerHost.DebugRecovCounter(sess.Client.PlayerId)})",
                    ded.Server.PlayerHost.DebugRecovCounter(sess.Client.PlayerId) > recovBefore);

            yield return Ticks(40);   // let the recov teleport + resume claim settle
            float toSpawn = sess.Shell.TruePhysicsPosition.DistanceTo(spawn);
            float toDeath = sess.Shell.TruePhysicsPosition.DistanceTo(deathSpot);
            // TEETH: NetRespawn does NOT reposition the shell -- so landing on spawn (and NOT stranded at the
            // death spot) is only possible because the server recov teleport carried it there. A bare
            // ServerTeleport would leave RecovsApplied 0 AND strand the shell at the death spot (its claim would
            // just re-drive the entity back to it) -- both asserts would fail.
            T.Check($"(c) the shell was repositioned to SPAWN via recov (err {toSpawn:0.00} m)", toSpawn < 0.6f);
            T.Check($"(c) NOT stranded at the death spot ({toDeath:0.0} m away from it)", toDeath > 2.5f);
            bool eOk = ded.Server.Players.TryGetByOwner(sess.Client.PlayerId, out var ent);
            T.Check($"(c) the server entity is held AT spawn (freeze-until-echo held, err {(eOk ? (ent.Pos - ToU(spawn)).magnitude : 9f):0.00} m)",
                    eOk && (ent.Pos - ToU(spawn)).magnitude < 0.6f);
            T.Check($"(c) respawned to full HP, adopted ({sess.Shell.Health:0})", !sess.Shell.IsDead && Mathf.IsEqualApprox(sess.Shell.Health, 100f));
            T.Check($"DESYNC-QUIET across death + respawn ({desyncs} fired)", desyncs == 0);

            world.Sim.Sim.Remove(pump);
        }
    }

    // SP/MP-unify P3a phase gate (part 3 of 3): the crisp "server owns the respawn clock" teeth, in isolation
    // (no net stack). Two bare shells on the ground: one under server-authoritative vitals (NetVitalsAdopted +
    // NetDie), one plain (the default SP path). Both die; both local death timers run out (well past 3.5 s).
    // The SERVER-OWNED shell stays dead -- its local self-respawn is DISABLED (the server drives NetRespawn) --
    // while the PLAIN shell self-respawns exactly as SP always did (byte-identical). This is the direct proof
    // that flipping HP to server-owned disables the local clock, without which the owner would double-respawn.
    public class UnifyDeathRespawnLocalClock : GameTest
    {
        public override string Name => "unify.death_respawn_local_clock";
        public override double TimeoutSimSeconds => 15;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var served = new PlayerController { CaptureMouse = false };
            World.AddChild(served);
            served.GlobalPosition = new Vector3(0f, 1f, 0f);
            var local = new PlayerController { CaptureMouse = false };
            World.AddChild(local);
            local.GlobalPosition = new Vector3(4f, 1f, 0f);
            yield return Ticks(3);   // let both _Ready + settle on the ground

            served.AdoptReplicatedVitals(100);   // HP is now server-owned on this shell
            T.Check("served shell adopted server vitals", served.NetVitalsAdopted);

            served.NetDie();                 // the server death fact
            local.TakeDamage(9999f);         // the default SP local-death path
            T.Check("both shells rendered death", served.IsDead && local.IsDead);
            T.Check("server-owned death zeroed HP", Mathf.IsEqualApprox(served.Health, 0f));

            // 200 ticks = 4 s, well past the 3.5 s local death timer both shells carry
            yield return Ticks(200);
            // TEETH: the server-owned shell must NOT have self-respawned -- the server owns the clock (only
            // NetRespawn revives it). The plain shell self-respawned on its local timer, exactly as SP always did.
            T.Check("(teeth) the SERVER-owned shell stayed dead past 3.5 s (local self-respawn DISABLED)", served.IsDead);
            T.Check("(contrast) the plain SP shell self-respawned on its local timer (byte-identical)", !local.IsDead);

            served.NetRespawn(reposition: true);   // the server's respawn fact drives the revive
            T.Check("NetRespawn revived the server-owned shell", !served.IsDead);
            T.Check("...to full HP", Mathf.IsEqualApprox(served.Health, served.MaxHealth));
        }
    }

    // SP/MP-unify P3b phase gate (source 3 of 5): SERVER-DERIVED fall damage. The client-auth walker streams
    // its (envelope-validated) Vel + Grounded each PlayerStateCommand; the server tracks the peak downward
    // speed while airborne and, on the airborne->grounded edge, applies the SAME FallMath curve the SP client
    // uses in CheckFallDamage -- WITHOUT the client ever reporting a damage number (the one client-auth cheat
    // hole this closes). Position is held static so the derivation is exercised off Vel+Grounded alone (the
    // envelope keys on position delta, which is 0 here).
    //
    // TEETH: a GENTLE landing (peak 10 m/s, under the 22 m/s threshold) does NOT damage; a HARD landing (peak
    // 40 m/s) drops the server HP by exactly FallMath.Damage(-40)=40, and the owner's replica adopts it. On the
    // pre-P3b server neither could land -- fall was a local TakeDamage the adopted shell no-op'd (invulnerable).
    public class UnifyDamageFall : GameTest
    {
        public override string Name => "unify.damage_fall";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260730);
            var walker = new NetWorldClient(new MemClientTransport(net), "faller", contentHash: NetContent.Hash);
            bool send = false, grounded = true;
            UnityEngine.Vector3 claim = default, claimVel = default;
            byte recovAck = 0;
            walker.PlayerRecov += e => { recovAck = e.RecovCounter; claim = e.Pos; };   // stray-recov insurance (none expected on a static claim)
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 0f, 0f, claimVel, 0, grounded, recovAck);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            T.Check("walker joined", walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;

            // grounded baseline: adopt the claim stream, HP full
            send = true; grounded = true; claimVel = default;
            yield return Ticks(8);
            T.Check("the walking owner is client-driven server-side", ded.Server.PlayerHost.IsClientDriven(walker.PlayerId));
            T.Check("(baseline) full server HP before any fall", ServerHp(ded, walker) == 100);

            // (teeth 1) a GENTLE landing: airborne at 10 m/s down (under the 22 m/s threshold), then touch down.
            grounded = false; claimVel = new UnityEngine.Vector3(0f, -10f, 0f);
            yield return Ticks(10);
            grounded = true; claimVel = default;
            yield return Ticks(8);
            T.Check($"(teeth) a gentle {10} m/s landing dealt NO fall damage (server HP {ServerHp(ded, walker)})", ServerHp(ded, walker) == 100);

            // (teeth 2) a HARD landing: airborne peaking at 30 m/s down (within the [-32,32) wire LinVel range,
            // so it round-trips exactly), then touch down -> FallMath.Damage(-30)=30. (A fall harder than 32 m/s
            // is wire-clamped to 32 and caps at 32 damage server-side -- flagged in ServerPlayerAuthority.)
            int expected = 100 - FallMath.Damage(-30f);   // 70
            grounded = false; claimVel = new UnityEngine.Vector3(0f, -30f, 0f);
            yield return Ticks(12);
            grounded = true; claimVel = default;
            yield return Until(() => ServerHp(ded, walker) < 100, 5);
            T.Check($"(teeth) a hard 40 m/s landing dropped server HP to the FallMath curve (got {ServerHp(ded, walker)}, expect {expected})",
                    ServerHp(ded, walker) == expected);
            T.Check("the owner is still alive after the survivable fall", ded.Server.CombatState.TryGet(walker.PlayerId, out var aCs) && aCs.Alive);
            yield return Until(() => OwnerHp(walker) == expected, 5);
            T.Check($"the owner's replica adopted the server fall HP (replica {OwnerHp(walker)} == server {expected})", OwnerHp(walker) == expected);

            world.Sim.Sim.Remove(pump);
            walker.Disconnect();
        }

        static int ServerHp(DedicatedServer ded, NetWorldClient c) => ded.Server.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
        static int OwnerHp(NetWorldClient c) => c.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
    }

    // SP/MP-unify P3b phase gate (source 4 of 5): OUT-OF-BOUNDS is the server-side safety net (review finding 1).
    // An adopted authoritative Y below the world floor (-1030, matching PlayerController.cs:3386) is lethal --
    // without it an owner who clips through the floor falls forever. DisableEnvelope adopts the below-floor claim
    // verbatim (a real terminal-velocity plunge would adopt over many legal ticks; this is the deterministic seam).
    //
    // TEETH: an adopted position below -1030 KILLS the server-owned player (Health 0, not Alive) and the owner
    // adopts the death; a position just ABOVE the floor does not. On the pre-P3b server the plunging shell's
    // local OOB TakeDamage was a no-op -> it fell forever with the server none the wiser.
    public class UnifyDamageOob : GameTest
    {
        public override string Name => "unify.damage_oob";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260731);
            var walker = new NetWorldClient(new MemClientTransport(net), "plunger", contentHash: NetContent.Hash);
            bool send = false;
            UnityEngine.Vector3 claim = default;
            byte recovAck = 0;
            walker.PlayerRecov += e => { recovAck = e.RecovCounter; claim = e.Pos; };
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 0f, 0f, UnityEngine.Vector3.zero, 0, grounded: true, recovAck);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;
            ded.Server.PlayerHost.DisableEnvelope = true;   // adopt the (otherwise fast) below-floor claim verbatim
            send = true;
            yield return Ticks(6);
            T.Check("(baseline) alive at full HP above the floor", ded.Server.CombatState.TryGet(walker.PlayerId, out var b0) && b0.Alive && b0.Health == 100);

            // a deep-but-in-bounds Y (-100, well within the ±256 wire band, above the -250 server floor): NOT OOB
            claim = new UnityEngine.Vector3(spawnE.Pos.x, -100f, spawnE.Pos.z);
            yield return Ticks(10);
            T.Check("(teeth) a deep-but-in-bounds Y (-100) does NOT kill", ded.Server.CombatState.TryGet(walker.PlayerId, out var a0) && a0.Alive);

            // below the world floor: the wire Pos.y clamps to -256 (< the -250 server OOB floor) -> lethal
            claim = new UnityEngine.Vector3(spawnE.Pos.x, -1030.5f, spawnE.Pos.z);
            yield return Until(() => ded.Server.CombatState.TryGet(walker.PlayerId, out var cs) && !cs.Alive, 5);
            T.Check("(teeth) a below-world adopted Y (wire-pinned at -256, under the -250 server floor) KILLED the player (Alive false, Health 0)",
                    ded.Server.CombatState.TryGet(walker.PlayerId, out var kCs) && !kCs.Alive && kCs.Health == 0);
            yield return Until(() => walker.CombatState.TryGet(walker.PlayerId, out var r) && !r.Alive, 5);
            T.Check("the owner's replica adopted the OOB death", walker.CombatState.TryGet(walker.PlayerId, out var rCs) && !rCs.Alive);

            world.Sim.Sim.Remove(pump);
            walker.Disconnect();
        }
    }

    // SP/MP-unify P3b phase gate (source 1 of 5): ZOMBIE MELEE. A real server-side zombie BRAIN adjacent to the
    // client-auth walker's follower body swings and its hit lands on the SERVER HP -- routed through the follower
    // body's NetDamageSink (PlayerNetSync) into ServerCombat.DamagePlayerExternal, instead of the old NetAvatar
    // no-op. This is the whole point of the source: the brain runs unchanged (SP-identical), the sink is what P3b
    // adds. On the pre-P3b server the zombie chased + swung forever while the adopted body stayed invulnerable.
    public class UnifyDamageZombie : GameTest
    {
        public override string Name => "unify.damage_zombie";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260732);
            var walker = new NetWorldClient(new MemClientTransport(net), "bitten", contentHash: NetContent.Hash);
            bool send = false;
            UnityEngine.Vector3 claim = default;
            byte recovAck = 0;
            walker.PlayerRecov += e => { recovAck = e.RecovCounter; claim = e.Pos; };
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 0f, 0f, UnityEngine.Vector3.zero, 0, grounded: true, recovAck);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;
            send = true;   // adopt -> the follower body spawns
            yield return Until(() => ded.PlayerSync != null && ded.PlayerSync.TrackedCount == 1, 5);
            bool haveBody = ded.PlayerSync.TryGetBody(walker.PlayerId, out var body);
            T.Check("the follower body spawned for the adopted owner", haveBody && body.NetAvatar);
            if (!haveBody) { world.Sim.Sim.Remove(pump); walker.Disconnect(); yield break; }
            yield return Ticks(2);

            // a real NORMAL zombie brain placed 1 m dead ahead of the body (at the body's -Z so its default facing
            // points at it -> the vision cone catches it on the first idle sense tick), targeting the body.
            var bp = body.GlobalPosition;
            var z = new ZombieController { Target = body };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(bp.X, bp.Y, bp.Z + 1.0f);   // within ATTACK_PLAYER reach (sqrt2 ~ 1.41 m)
            z.Rotation = Vector3.Zero;                                 // forward -Z -> toward the body
            T.Check("(baseline) full server HP before the zombie swings", ServerHp(ded, walker) == 100);

            // the brain senses -> hunts -> plants -> swings; the hit lands mid-swing (~0.4 s) on the SERVER HP
            yield return Until(() => ServerHp(ded, walker) < 100, 15);
            int hp = ServerHp(ded, walker);
            T.Check($"(teeth) the server zombie brain's melee landed on the SERVER HP (100 -> {hp})", hp < 100 && hp <= 100 - (int)z.AttackDamage + 1);
            T.Check($"(teeth) exactly one swing's worth (AttackDamage {z.AttackDamage:0}) so far (server HP {hp})", hp == 100 - (int)z.AttackDamage);
            yield return Until(() => OwnerHp(walker) == hp, 5);
            T.Check($"the owner's replica adopted the zombie-melee HP (replica {OwnerHp(walker)} == server {hp})", OwnerHp(walker) == hp);

            world.Sim.Sim.Remove(pump);
            walker.Disconnect();
        }

        static int ServerHp(DedicatedServer ded, NetWorldClient c) => ded.Server.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
        static int OwnerHp(NetWorldClient c) => c.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
    }

    // SP/MP-unify P3b phase gate (source 2 of 5): EXPLOSIONS. A real server-side deployable Explode group-scan
    // hits the client-auth walker's follower body, routed through its NetDamageSink into DamagePlayerExternal
    // with the source LINEAR falloff. On the pre-P3b server the blast's TakeDamage on the adopted body was a
    // no-op -> explosions couldn't hurt an adopted player.
    public class UnifyDamageExplosion : GameTest
    {
        public override string Name => "unify.damage_explosion";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260733);
            var walker = new NetWorldClient(new MemClientTransport(net), "blasted", contentHash: NetContent.Hash);
            bool send = false;
            UnityEngine.Vector3 claim = default;
            byte recovAck = 0;
            walker.PlayerRecov += e => { recovAck = e.RecovCounter; claim = e.Pos; };
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 0f, 0f, UnityEngine.Vector3.zero, 0, grounded: true, recovAck);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;
            send = true;
            yield return Until(() => ded.PlayerSync != null && ded.PlayerSync.TrackedCount == 1, 5);
            bool haveBody = ded.PlayerSync.TryGetBody(walker.PlayerId, out var body);
            T.Check("the follower body spawned for the adopted owner", haveBody && body.NetAvatar);
            if (!haveBody) { world.Sim.Sim.Remove(pump); walker.Disconnect(); yield break; }
            yield return Ticks(2);

            // a server-owned deployable ~1 m from the body; DebugStage("wreck") detonates it immediately so the
            // Explode() group-scan runs THIS frame (no 4 s dead-timer wait), hitting the body over the wire sink.
            var bp = body.GlobalPosition;
            var dep = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(bp.X + 1.0f, bp.Y, bp.Z), 0f);
            yield return Ticks(2);   // let it enter the tree + settle onto the ground
            T.Check("(baseline) full server HP before the blast", ServerHp(ded, walker) == 100);

            float d = dep.GlobalPosition.DistanceTo(body.GlobalPosition);
            int expected = Mathf.Clamp(Mathf.CeilToInt(100f - ExplosionMath.Linear(120f, d, 5f)), 0, 100);
            dep.DebugStage("wreck");
            yield return Until(() => ServerHp(ded, walker) < 100, 5);
            int hp = ServerHp(ded, walker);
            T.Check($"(teeth) the deployable blast landed on the SERVER HP with LINEAR falloff (d {d:0.00} m -> HP {hp}, expect {expected})",
                    hp == expected && hp < 100);
            yield return Until(() => OwnerHp(walker) == hp, 5);
            T.Check($"the owner's replica adopted the blast HP (replica {OwnerHp(walker)} == server {hp})", OwnerHp(walker) == hp);

            world.Sim.Sim.Remove(pump);
            walker.Disconnect();
        }

        static int ServerHp(DedicatedServer ded, NetWorldClient c) => ded.Server.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
        static int OwnerHp(NetWorldClient c) => c.CombatState.TryGet(c.PlayerId, out var cs) ? cs.Health : -1;
    }

    // SP/MP-unify P3b (source 5 of 5): the spawn-window guard (review finding 5), in isolation (no net stack).
    // Between shell spawn and the first AdoptReplicatedVitals latching NetVitalsAdopted there is a 1-3 tick
    // window; ExpectServerVitals (set at spawn by ClientWorldSession/MpLoopback) suppresses a LOCAL death there
    // so it can't fire before the server owns HP. A plain SP shell (no pend) dies normally -- the contrast proof.
    public class UnifyDamageSpawnWindow : GameTest
    {
        public override string Name => "unify.damage_spawn_window";
        public override double TimeoutSimSeconds => 10;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var pending = new PlayerController { CaptureMouse = false };
            World.AddChild(pending);
            pending.GlobalPosition = new Vector3(0f, 1f, 0f);
            var plain = new PlayerController { CaptureMouse = false };
            World.AddChild(plain);
            plain.GlobalPosition = new Vector3(4f, 1f, 0f);
            yield return Ticks(3);

            pending.ExpectServerVitals();   // server vitals PENDING (spawn window) -- not yet adopted
            T.Check("pending shell has not adopted server vitals yet", !pending.NetVitalsAdopted);

            // a lethal hit in the spawn window: the pending shell must NOT die locally; the plain shell dies as SP always did
            pending.TakeDamage(9999f);
            plain.TakeDamage(9999f);
            T.Check("(teeth) the pending-vitals shell did NOT die in the spawn window (local death suppressed)", !pending.IsDead && pending.Health > 0f);
            T.Check("(contrast) the plain SP shell died normally (byte-identical)", plain.IsDead);

            // once adoption lands, HP is server-owned -> local death stays suppressed (the P3a guard takes over)
            pending.AdoptReplicatedVitals(100);
            pending.TakeDamage(9999f);
            T.Check("still no local death after adoption (server owns HP)", !pending.IsDead);
        }
    }

    // SP/MP-unify P6a phase gate (the staged flip): the SP GAME Playable entries boot the CONSUMING listen-server
    // BY DEFAULT, while the TEST HARNESSES stay on the direct path. This locks the decision logic
    // (Main.ResolveLoopbackMode) that the two AttachMpLoopback call sites drive: the game "Drive PEI"/--peidrive +
    // --peiplay pass gameDefault=true; the nav-bake/navpath/zombietest harnesses (and Aerial --objects, which
    // early-returns on a null player) pass gameDefault=false. The consume machinery itself is unchanged from the
    // --spconsume path (P1-P5, already gated green); P6a only flips WHICH entries turn it on.
    //
    // TEETH: the whole flip is "a game boot with NO flags now CONSUMES" -- (attach, consume) == (true, true). On the
    // pre-P6a gate ("attach iff --mploopback") that same no-flag game boot was (false, false) = pure direct, so this
    // assertion FAILS on pre-flip logic. The opt-out (--direct) restores (false, false), and the harness stays direct
    // unless it explicitly opts in with --mploopback -- all asserted, so a regression in either direction trips a check.
    public class UnifyDefaultFlip : GameTest
    {
        public override string Name => "unify.default_flip";
        public override double TimeoutSimSeconds => 5;

        public override IEnumerable<Step> Run()
        {
            // signature: Main.ResolveLoopbackMode(gameDefault, mpLoopback, spConsume, direct) -> (attach, consume)

            // --- the GAME path (menu "Drive PEI"/--peidrive, --peiplay): CONSUME by DEFAULT, no flags needed ---
            var g = Main.ResolveLoopbackMode(gameDefault: true, mpLoopback: false, spConsume: false, direct: false);
            T.Check($"(FLIP) game default, no flags -> loopback ATTACHES + CONSUMES ({g})", g.attach && g.consume);

            var gd = Main.ResolveLoopbackMode(gameDefault: true, mpLoopback: false, spConsume: false, direct: true);
            T.Check($"(opt-out) game + --direct -> NO loopback, pure direct SP fallback ({gd})", !gd.attach && !gd.consume);

            // legacy explicit flags on the game path are subsumed by the default (still consuming, never publish-only)
            var gm = Main.ResolveLoopbackMode(gameDefault: true, mpLoopback: true, spConsume: false, direct: false);
            T.Check($"(game) legacy --mploopback alone still CONSUMES on the game path ({gm})", gm.attach && gm.consume);
            var gmd = Main.ResolveLoopbackMode(gameDefault: true, mpLoopback: true, spConsume: true, direct: true);
            T.Check($"(game) --direct WINS over --mploopback/--spconsume -> pure direct ({gmd})", !gmd.attach && !gmd.consume);

            // --- the TEST HARNESS path (nav bake / navpath / zombietest reaching a Playable world): stays DIRECT ---
            var h = Main.ResolveLoopbackMode(gameDefault: false, mpLoopback: false, spConsume: false, direct: false);
            T.Check($"(harness) no flags -> stays DIRECT, no loopback ({h})", !h.attach && !h.consume);

            var hm = Main.ResolveLoopbackMode(gameDefault: false, mpLoopback: true, spConsume: false, direct: false);
            T.Check($"(harness) legacy --mploopback -> publish-only loopback, NO consume ({hm})", hm.attach && !hm.consume);

            var hmc = Main.ResolveLoopbackMode(gameDefault: false, mpLoopback: true, spConsume: true, direct: false);
            T.Check($"(harness) --mploopback --spconsume -> consuming loopback (legacy path intact) ({hmc})", hmc.attach && hmc.consume);

            // --direct never turns a harness ON -- it's an opt-OUT knob for the game default only
            var hd = Main.ResolveLoopbackMode(gameDefault: false, mpLoopback: false, spConsume: false, direct: true);
            T.Check($"(harness) --direct alone is a no-op on a harness (already direct) ({hd})", !hd.attach && !hd.consume);

            yield break;
        }
    }

    // GAP B1 regression: storage item-loss on close for a NON-replicated (look-opened / SP-local) crate.
    //
    // Post-A1/B9 most containers replicate (NetId!=0) and route their close over the wire (NetCloseStorage set by
    // the loopback). But a shelf opened via the LOOK path -- PlayerController.OpenCrate -- copies the crate grid
    // into STORAGE (page 7) WITHOUT latching a server NetId (only OnReplicatedStorageOpened does that), so
    // _openCrateNetId stays 0. On the pre-fix CloseCrate the mere presence of a wired NetCloseStorage took the net
    // branch, saw _openCrateNetId==0, and RETURNED before any copy-back -- everything dragged into the shelf was
    // silently dropped. The fix guards that branch on `_openCrateNetId != 0` so a NetId==0 crate FALLS THROUGH to
    // the local copy-back (STORAGE -> crate.Storage).
    //
    // TEETH: NetCloseStorage is wired (a no-op stand-in for the loopback seam) and the shelf is opened via the look
    // path (_openCrateNetId==0). Drop an item into the STORAGE grid, then CloseCrate, and assert the shelf's own
    // Storage KEPT it. On pre-fix code the net branch returns before the copy-back, so crate.Storage stays empty
    // (getItemCount 0) and this fails -- the exact shipped item-loss bug.
    public class UnifyStorageCloseNoLoss : GameTest
    {
        public override string Name => "unify.storage_close_no_loss";
        public override double TimeoutSimSeconds => 15;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            ItemCatalog.RegisterAll();   // item 95 (Bandage, 1x1) resolves against the catalog

            // a SP-LOCAL StoreShelf: NetId==0 (never replicated), no rolled loot + no tier-display machinery
            // (MinItems=MaxItems=0, ShowItems=false) so the copy-back is what's exercised, not the render. It is
            // still a real StoreShelf -- the exact class the bug was reported on.
            var shelf = new StoreShelf { MinItems = 0, MaxItems = 0, ShowItems = false, RenderMesh = false };
            World.AddChild(shelf);
            shelf.GlobalPosition = new Vector3(2f, 0f, 0f);

            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);   // let both _Ready run (shelf Storage grid + player Inventory built)

            T.Check("the shelf is SP-local (its own Storage grid exists, starts empty)",
                    shelf.Storage != null && shelf.Storage.getItemCount() == 0);

            // wire the close seam exactly as the loopback does (NetCloseStorage != null) -- a no-op stand-in, since
            // there is no server here; the point is the delegate is NON-NULL, which is what tripped the buggy branch.
            player.NetCloseStorage = () => { };

            // open via the LOOK path -- OpenCrate loads the (empty) crate grid into STORAGE (7) and does NOT latch a
            // server NetId, so _openCrateNetId stays 0 (only OnReplicatedStorageOpened sets it).
            bool opened = player.OpenCrate(shelf);
            T.Check("look-opened the shelf (STORAGE page loaded, dashboard open)", opened && player.DashboardOpen);

            // drop an item into the STORAGE grid (page 7) at a fixed cell -- the "drag into the shelf" the user did
            var storagePage = player.Inventory.items[PlayerInventory.STORAGE];
            storagePage.addItem(0, 0, 0, new Item(95));
            T.Check("an item sits in the open STORAGE view before close", storagePage.getItemCount() == 1);
            T.Check("...and the crate's own Storage is still empty (not yet written back)", shelf.Storage.getItemCount() == 0);

            // close the crate (the ESC/Tab path, without an InputEvent). With the fix, NetId==0 falls through to the
            // local copy-back; pre-fix the net branch returned here and the item was lost.
            player.DebugCloseCrate();

            // THE GATE: the item was SAVED into the shelf's Storage (getItemCount preserved), not silently dropped.
            T.Check($"(gate) the item was written back into the shelf's Storage (count {shelf.Storage.getItemCount()}, expected 1)",
                    shelf.Storage.getItemCount() == 1);
            T.Check("(gate) the written-back item is the one we dropped (Bandage 95)",
                    shelf.Storage.getItemCount() == 1 && shelf.Storage.getItem(0)?.item?.id == 95);
        }
    }

    // GAP B7 regression: skills wired + adopted in the loopback.
    //
    // The consuming loopback (MpLoopback --spconsume) left TWO seams unwired that the MP shell (ClientWorldSession)
    // has: (1) Player.NetUpgradeSkill, so a skill spend never routed over the wire -- SkillsUI fell back to a LOCAL
    // TryUpgrade against the shell's 0-XP pool (SP grants no demo skills) and did nothing; and (2) the per-tick
    // AdoptReplicatedSkills in TickLocal, so even server-owned XP/levels never mirrored onto the shell. The fix wires
    // NetUpgradeSkill = Client.SendUpgradeSkill (verbatim ClientWorldSession:468) and adopts Client.Skills each tick
    // beside the vitals adoption (verbatim ClientWorldSession:260). Zero protocol change: CommandUpgradeSkill(6) +
    // SystemSkills(5) already exist; this is pure seam-wiring.
    //
    // This exercises a REAL MpLoopback (not a DedicatedServer + hand-rolled NetWorldClient stand-in like the P1-P5
    // gates), because the gap lives in MpLoopback's OWN _Ready wiring + TickLocal -- a stand-in that hand-sets the
    // seam would mask the bug. It builds the actual node the SP GAME boots: a real PlayerController shell + SimDriver
    // spine + DayNight/Resources (the headless-safe rig the L1 host uses -- the full WorldBuilder Playable path NREs
    // under pure --headless because its player HUD/window/camera need a display; this rig gives the loopback the exact
    // same handles: a real shell, the sim spine, and non-null clock/resource fields) + MpLoopback{ConsumeDeployables=
    // true}, awards XP server-side, spends it through the SkillsUI request path, ticks, and asserts the shell's LOCAL
    // Skills level rose via adoption.
    //
    // TEETH: pre-fix NetUpgradeSkill is null -> RequestUpgradeSkill returns false -> the SkillsUI fallback runs the
    // LOCAL TryUpgrade against 0 XP (no-op), the server entity is never leveled (no command sent), and adoption is
    // never wired -- so BOTH the "(server) leveled" Until and the "(gate) local adopted" check fail. With the fix the
    // spend routes over the wire, the server levels + replicates, and TickLocal adopts the rise onto the shell.
    public class UnifyLoopbackUpgradeSkill : GameTest
    {
        public override string Name => "unify.loopback_upgrade_skill";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // MpLoopback --spconsume seeds the SP demo kit on join -> resolve items against the catalog

            // headless-safe stand-ins for exactly what the SP GAME feeds AttachMpLoopback (Player/Sim/DayNight/
            // Resources): a real shell, the real sim spine, and the two world-state fields the loopback's syncs read.
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };   // headless: no Sun/Env, so keep _Process off (WorldClockNetSync reads .Time only)
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false }; // the dedicated/headless shape (§5 fx hygiene)
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));       // the REAL PlayerController shell the loopback drives + adopts onto
            yield return Ticks(2);   // let _Ready run (shell Inventory/Skills built)

            // attach the REAL consuming loopback -- the exact node AttachMpLoopback builds on the SP GAME path. Its
            // _Ready spins up the in-process listen-server + client over MemTransport and registers the sim steps
            // (TickLocal + server sim/replicate) onto driver.Sim, driven by the SimDriver each physics tick.
            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);

            // wait for the loopback client to connect + the server to ServerAdd the skills entity (fires on PeerConnected)
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Skills.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected + server skills entity present",
                    loop.Client.State == NetSessionState.Connected
                    && loop.Server.Skills.TryGet(loop.Client.PlayerId, out _));

            const byte SPEC = (byte)SDG.Unturned.EPlayerSpeciality.OFFENSE;
            const byte IDX  = (byte)SDG.Unturned.EPlayerOffense.OVERKILL;   // baseCost 10 @ level 0 -> 50 XP is plenty for one level

            // baseline: OVERKILL is level 0 on the shell and the shell holds no XP (SP demo grants none), so a bare
            // LOCAL SkillsUI TryUpgrade could never level it -- only the server's XP + the wire can.
            T.Check("(baseline) local shell OVERKILL is level 0", player.Skills.skills[SPEC][IDX].level == 0);
            T.Check("(baseline) local shell has 0 XP (SP demo grants no skills)", player.Skills.experience == 0);

            // award XP SERVER-SIDE (the §3.2 XP hook kills/harvests/console feed) -- authoritative server state the
            // shell only ever sees via replication + adoption.
            uint total = loop.Server.Transactions.AwardXp(loop.Client.PlayerId, 50);
            T.Check("(server) 50 XP awarded on the server skills entity", total == 50);
            yield return Ticks(3);   // pump a few replication ticks so the owner skills block lands + TickLocal adopts it

            // spend it via the SkillsUI upgrade path VERBATIM (SkillsUI.cs:109-110): request over the wire, else fall
            // back to the LOCAL TryUpgrade. With the fix the seam is set -> request wins; pre-fix it is null -> the
            // fallback runs against 0 local XP and does nothing.
            if (!player.RequestUpgradeSkill(SPEC, IDX))
                player.Skills.TryUpgrade(SPEC, IDX);

            // the wire spend: server validates cost/cap, levels its entity, and replicates the owner block
            yield return Until(() => loop.Server.Skills.TryGet(loop.Client.PlayerId, out var se)
                                     && se.Skills.skills[SPEC][IDX].level == 1, 15);
            T.Check("(server) the server skills entity leveled OVERKILL to 1 (wire-validated spend)",
                    loop.Server.Skills.TryGet(loop.Client.PlayerId, out var sSk) && sSk.Skills.skills[SPEC][IDX].level == 1);

            // THE GATE: MpLoopback.TickLocal's AdoptReplicatedSkills mirrors the server level rise onto the shell.
            yield return Until(() => player.Skills.skills[SPEC][IDX].level == 1, 10);
            T.Check($"(gate) the local shell adopted the server level rise (OVERKILL lvl {player.Skills.skills[SPEC][IDX].level}, expected 1)",
                    player.Skills.skills[SPEC][IDX].level == 1);
        }
    }

    // B8 (SP/MP-unify): vehicle seat arbitration is a tick-ORDERING race, not a missing seam. The listen-server
    // host drives via the direct SP path (its node IS the authority -- routing it through the puppet/Net* path
    // would build a SECOND client-local body = the two-body inchworm). Its occupancy becomes TRUTH (the entity's
    // DriverPlayerId, which a remote EnterVehicle validates against) ONLY via VehicleNetSync's local-occupancy
    // reconcile. B8 moves that reconcile out of VehicleNetSync.Tick() (which runs AFTER net.server.sim) into a
    // dedicated PRE-SIM step (MpLoopback registers net.vehicles.occupancy before net.server.sim), so the host's
    // CURRENT Driving state is stamped into DriverPlayerId BEFORE the sim dispatches+validates a remote Enter
    // that same tick. ZERO protocol change -- pure step-order.
    //
    // This exercises a REAL MpLoopback (like unify.loopback_upgrade_skill) because the gap lives in MpLoopback's
    // OWN _Ready step ORDER -- a hand-wired stand-in reconciling at an arbitrary point would mask it. A real
    // remote NetWorldClient joins the loopback's in-process server over the same MemNetwork; the host SP-direct-
    // enters a jeep and, in the SAME coroutine gap (so both land on one tick T), the remote sends EnterVehicle
    // for that NetId.
    //
    // TEETH: the race bites ONLY on the tick the host BECOMES the driver -- in steady state both orderings reject
    // the remote. The test sets the host's _driving and transmits the remote's Enter in the same gap: SendReliable
    // transmits immediately with DeliverTick == the net's fixed CurrentTick, so the next tick's net.server.sim
    // dispatches it -- the SAME tick the host transitions, with DriverPlayerId==0 entering it. POST-fix the pre-sim
    // reconcile claims the seat for the host first, so CanEnter REJECTS the remote and DriverPlayerId stays the
    // host. PRE-fix the reconcile runs only in Tick() (after net.server.sim), so the remote's Enter validates
    // against the stale DriverPlayerId==0, WINS the seat, and Tick()'s later reconcile can't reclaim it (its claim
    // branch requires DriverPlayerId==0) -> DriverPlayerId ends up the REMOTE (double-seat). Verified by moving the
    // occupancy step back after net.server.sim, rebuilding, and confirming this test fails with seat==remote.
    public class UnifyHostSeatArbitration : GameTest
    {
        public override string Name => "unify.host_seat_arbitration";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // the server's PeerConnected reads gun defs (Combat.GunFor) -> resolve against the catalog

            // headless-safe stand-ins for exactly what the SP GAME feeds AttachMpLoopback (see unify.loopback_upgrade_skill)
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // the REAL host shell the loopback drives
            yield return Ticks(2);

            // the publish-only loopback is enough: the vehicle occupancy sync is registered UNCONDITIONALLY (outside
            // the --spconsume block), so ConsumeDeployables stays false -> minimal machinery, same step order.
            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected, 15);
            T.Check("host loopback client connected", loop.Client.State == NetSessionState.Connected);

            // a REAL remote joins the SAME in-process server over the loopback's MemNetwork; its own pump only ticks
            // the remote client (Net.Tick is the loopback's, one per sim tick -- never double-tick the network).
            var remote = new NetWorldClient(new MemClientTransport(loop.Net), "remote", contentHash: NetContent.Hash);
            var remotePump = new DelegateSimStep((t, dt) => remote.Tick(), "l1.remotepump");
            driver.Sim.Add(remotePump);
            remote.Connect();
            yield return Until(() => remote.State == NetSessionState.Connected
                                     && loop.Server.Players.TryGetByOwner(remote.PlayerId, out _), 15);
            T.Check("remote client joined the loopback server", remote.State == NetSessionState.Connected
                    && loop.Server.Players.TryGetByOwner(remote.PlayerId, out _));
            ushort hostId = loop.Client.PlayerId;
            ushort remoteId = remote.PlayerId;
            T.Check($"distinct player ids (host {hostId}, remote {remoteId})", hostId != 0 && remoteId != 0 && hostId != remoteId);

            // the remote's server entity is frozen at spawn (no MoveInput -> ServerStep skips it), so place the
            // cars right on it -> the remote is well inside EnterReach (6 m) for CanEnter.
            loop.Server.Players.TryGetByOwner(remoteId, out var rp);
            var near = new Vector3(rp.Pos.x, rp.Pos.y + 0.5f, rp.Pos.z);

            // a real jeep in reach -> VehicleNetSync mints its entity once it's in the "vehicles" group + tree
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = near;
            yield return Until(() => loop.Server.Vehicles.Count == 1, 10);
            T.Check("VehicleNetSync minted the jeep entity", loop.Server.Vehicles.Count == 1);
            uint netId = 0;
            foreach (var e in loop.Server.Vehicles.All) { netId = e.NetIdValue; break; }
            T.Check($"jeep has a server NetId ({netId})", netId != 0);
            T.Check("(baseline) seat empty", loop.Server.Vehicles.TryGet(netId, out var b0) && b0.DriverPlayerId == 0);
            T.Check("(baseline) host not driving", !player.IsDriving);

            // --- THE RACE (forward, TEETH) --- one coroutine gap == the net's CurrentTick is fixed here: the host
            // SP-direct-enters the jeep (sets _driving NOW, so it's the driver ENTERING the next tick, DriverPlayerId
            // still 0), and the remote transmits EnterVehicle for the same NetId (DeliverTick == CurrentTick). The
            // next tick's net.server.sim dispatches it -- the SAME tick the host transitions.
            player.EnterVehicle(jeep);
            T.Check("host took the seat via the direct SP path", player.IsDriving && player.Driving == jeep);
            bool sent = remote.SendEnterVehicle(netId);
            T.Check("remote dispatched EnterVehicle for the jeep", sent);
            yield return Ticks(1);    // the transition tick: occupancy(pre-sim) -> sim(dispatch remote Enter) -> vehicles.sync
            yield return Ticks(3);    // settle (steady state -- no reclaim possible)

            bool got = loop.Server.Vehicles.TryGet(netId, out var after);
            ushort seat = got ? after.DriverPlayerId : (ushort)0;
            T.Check($"the host KEEPS the seat -- remote Enter rejected (DriverPlayerId {seat}, host {hostId}, remote {remoteId})",
                    got && seat == hostId);
            T.Check("the host is still driving its real body", player.IsDriving && player.Driving == jeep);
            T.Check("the remote was NOT registered as a driver server-side", !loop.Server.VehicleHost.IsDriver(remoteId));

            // --- REVERSE (complementary) --- remote drives an EMPTY car; the host's F is blocked by NetDriverId.
            var jeep2 = Vehicle.BuildByName("jeep");
            World.AddChild(jeep2);
            jeep2.GlobalPosition = new Vector3(rp.Pos.x, rp.Pos.y + 0.5f, rp.Pos.z + 1.0f);   // still in the remote's reach
            yield return Until(() => loop.Server.Vehicles.Count == 2, 10);
            uint netId2 = 0;
            foreach (var e in loop.Server.Vehicles.All) { if (e.NetIdValue != netId) { netId2 = e.NetIdValue; break; } }
            T.Check($"second jeep minted ({netId2})", netId2 != 0);
            remote.SendEnterVehicle(netId2);   // empty car, remote in reach -> CanEnter passes
            yield return Until(() => loop.Server.Vehicles.TryGet(netId2, out var e2) && e2.DriverPlayerId == remoteId, 10);
            T.Check("remote took the empty second jeep (server occupancy)",
                    loop.Server.Vehicles.TryGet(netId2, out var re2) && re2.DriverPlayerId == remoteId);
            // VehicleNetSync.Tick stamps NetDriverId on the node for a remote-held seat -> the host's direct
            // EnterVehicle guard (if NetDriverId != 0 return) blocks it.
            yield return Until(() => jeep2.NetDriverId == remoteId, 10);
            T.Check("the node's NetDriverId reflects the remote driver", jeep2.NetDriverId == remoteId);
            player.EnterVehicle(jeep2);   // host F on the remote-occupied car
            T.Check("host F is BLOCKED by NetDriverId (never seats on the remote's car)",
                    !player.IsDriving || player.Driving != jeep2);
            T.Check("the remote still holds the second seat (no host takeover)",
                    loop.Server.Vehicles.TryGet(netId2, out var re3) && re3.DriverPlayerId == remoteId);

            driver.Sim.Remove(remotePump);   // teardown: nothing touches the dying MemNetwork after QueueFree
            remote.Disconnect();
        }
    }

    // GAP A4 (crops client-view): proves the CropReplicaView materializes a server crop entity into a real
    // CropNode on a joined client AND derives its GROWTH STAGE from the snapshot tick -- NOT a client
    // CropManager clock (there is none). One client stands in for the local loopback player: the server plants
    // a FRESH crop, the view materializes it (young), and after the def's growth window elapses on the tick
    // clock the node's stage FLIPS to grown -- straight off Client.Crops.IsGrown(e, LastAppliedServerTick).
    //
    // Carrot's real growth (10800 s) can't be simmed, so its schema def is overridden to GrowthSeconds=2 (100
    // ticks) on BOTH sides -- a client+server-agreed DERIVATION only (growth stage is never a wire/StateHash
    // byte; only SeedId/Pos/PlantedAtTick/Grown-flag are), so the override moves no bytes and can't desync.
    //
    // TEETH: the node must carry the SERVER entity NetId (a SP direct CropNode.Spawn stamps 0), start YOUNG,
    // then FLIP to grown purely from the advancing tick. Without the view -> no node (materialize fails);
    // without the tick-derived SetGrown -> the node stays young forever (the grow assertion times out).
    public class UnifyCropView : GameTest
    {
        public override string Name => "unify.crop_view";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260725);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            CropNetSchema.RegisterAll(client.Crops.Schema);   // loads Crop/FarmRegistry (server side registered by DedicatedServer's CropNetSync)
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);

            // the A4 pattern: the SOLE crop materializer on a joined client (no client CropManager)
            var view = new CropReplicaView { Client = client };
            World.AddChild(view);

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // shrink carrot's growth to 2 s (100 ticks) so the FLIP is simmable -- a pure derivation on both
            // sides (growth stage is never hashed), applied AFTER the server's CropNetSync registered its schema
            var fast = new CropNetDef { SeedId = 330, GrowthSeconds = 2, YieldItemId = 329 };
            client.Crops.Schema.Register(fast);
            ded.Server.Crops.Schema.Register(fast);

            // plant a FRESH carrot server-side (the remote/console Plant path funnels through this same PlantCrop)
            var e = ded.Server.Transactions.PlantCrop(330, new UnityEngine.Vector3(3f, 0f, 3f), grown: false);
            T.Check("server planted a fresh carrot entity", e != null && ded.Server.Crops.Count == 1);
            if (e == null) yield break;

            // the consume payoff: the view materialized the entity into a real CropNode
            yield return Until(() => view.TryGetNode(e.NetIdValue, out _), 5);
            bool have = view.TryGetNode(e.NetIdValue, out var node);
            T.Check("the CropReplicaView materialized a CropNode for the entity", have);
            if (!have) yield break;
            // TEETH: a server-assigned NetId proves it came from the replicated entity, not a direct SP spawn (NetId 0)
            T.Check($"the node carries the SERVER entity NetId ({node.NetId})", node.NetId == e.NetIdValue && node.NetId != 0);
            T.Check("the replica joined the harvest-scannable \"crop\" group", node.IsInGroup("crop"));
            // fresh + <100 ticks elapsed -> the tick-derived stage is YOUNG (the pre-flip half of the teeth)
            T.Check("the fresh replica starts YOUNG (tick-derived, not grown yet)",
                    !node.Grown && !client.Crops.IsGrown(e, client.Applier.LastAppliedServerTick));

            // step past the 2 s (100-tick) growth window: the node's stage FLIPS to grown, driven ONLY by the
            // advancing snapshot tick through Client.Crops.IsGrown -- no client CropManager clock exists
            yield return Until(() => view.TryGetNode(e.NetIdValue, out var n) && n.Grown, 5);
            T.Check("the replica FLIPPED to grown off the snapshot tick (Client.Crops.IsGrown-derived)",
                    view.TryGetNode(e.NetIdValue, out var grown) && grown.Grown
                    && client.Crops.IsGrown(e, client.Applier.LastAppliedServerTick));

            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }

    // GAP A4 (crops client harvest): proves a joined client can HARVEST a grown replicated crop -- the shell's
    // F-interact seam RequestHarvestNearestCrop scans the "crop" group for the nearest grown NetId!=0 and routes
    // Client.SendHarvestCrop; the server validates + removes the crop + drops the yield as a replicated world
    // item, and BOTH results reflect on the client: the crop replica DESPAWNS and a visible+focusable yield
    // puppet materializes through the WorldItemReplicaView. A real ClientWorldSession shell + server over
    // MemTransport (the net.shell_* pattern); the crop is planted GROWN server-side within the shell's reach.
    //
    // TEETH: pre-fix the shell has NO NetHarvestCrop seam (RequestHarvestNearestCrop returns false) and no
    // CropReplicaView materializes a NetId-stamped grown node -> the harvest never sends, the crop stays, no
    // yield appears. Post-fix the request fires, the crop despawns, and the yield puppet is the visible+focusable
    // reflection. (Verified by reverting the seam wiring: RequestHarvestNearestCrop then returns false.)
    public class UnifyCropClientHarvest : GameTest
    {
        public override string Name => "unify.crop_client_harvest";
        public override double TimeoutSimSeconds => 40;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        // a world-item is visible + focusable iff it renders AND carries a collider on the item look-hit layer
        // (bit 7 -- what the player's look-ray focuses). Matches UnifyPassiveLootSingle's check.
        static bool VisibleFocusable(Node3D n)
        {
            if (n == null || !GodotObject.IsInstanceValid(n) || !n.Visible) return false;
            if (n is CollisionObject3D self && (self.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            foreach (var ch in n.GetChildren())
                if (ch is CollisionObject3D co && (co.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // the yield world-item puppet resolves against the real catalog

            var net = new MemNetwork(20260726);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "farmer" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            yield return Ticks(5);

            // plant a GROWN carrot ~1 m from the shell (well inside the 3 m harvest reach). Grown:true -> the
            // entity's Grown flag short-circuits IsGrown, so the replica renders grown + is harvest-scannable.
            var cropPos = sess.Shell.GlobalPosition + new Vector3(1.0f, 0f, 0f);
            var e = ded.Server.Transactions.PlantCrop(330, ToU(cropPos), grown: true);
            T.Check("server planted a GROWN carrot near the shell", e != null && ded.Server.Crops.Count == 1);
            if (e == null) yield break;

            // the CropReplicaView materialized it (grown) into a NetId-stamped node in the "crop" group
            yield return Until(() => sess.Crops.TryGetNode(e.NetIdValue, out var n) && n.Grown, 5);
            bool have = sess.Crops.TryGetNode(e.NetIdValue, out var cropNode);
            T.Check("the grown crop replica materialized on the shell's view", have && cropNode.Grown);
            T.Check($"the replica carries the server NetId ({(have ? cropNode.NetId : 0)}) in the \"crop\" group",
                    have && cropNode.NetId == e.NetIdValue && cropNode.NetId != 0 && cropNode.IsInGroup("crop"));
            int itemsBefore = sess.Items.NodeCount;
            T.Check("no yield world item yet (the harvest hasn't fired)", itemsBefore == 0 && ded.Server.WorldItems.Count == 0);

            // the F-interact seam: scan the "crop" group -> SendHarvestCrop. Public so we drive it without the raycast.
            bool req = sess.Shell.RequestHarvestNearestCrop();
            T.Check("RequestHarvestNearestCrop found the grown replica and routed the harvest", req);

            // (a) the server validated + removed the crop, and the replica DESPAWNED off the diff-driven view
            yield return Until(() => ded.Server.Crops.Count == 0, 5);
            T.Check("(a) the server removed the crop (harvest validated + CropHarvested broadcast)", ded.Server.Crops.Count == 0);
            yield return Until(() => !sess.Crops.TryGetNode(e.NetIdValue, out _), 5);
            T.Check("(a) the crop replica despawned from the shell's view", !sess.Crops.TryGetNode(e.NetIdValue, out _));

            // (b) the yield dropped as a replicated world item, materialized as a visible+focusable puppet
            yield return Until(() => ded.Server.WorldItems.Count >= 1, 5);
            uint yieldId = 0;
            foreach (var wi in ded.Server.WorldItems.All) { yieldId = wi.NetIdValue; break; }
            T.Check($"(b) the server spawned the harvest yield world item ({yieldId})", yieldId != 0);
            yield return Until(() => sess.Items.TryGetNode(yieldId, out _), 5);
            bool haveYield = sess.Items.TryGetNode(yieldId, out var yieldNode);
            T.Check("(b) the yield world item materialized on the shell's WorldItemReplicaView", haveYield);
            T.Check("(b) the yield puppet is visible + focusable (the harvest reflection)",
                    haveYield && VisibleFocusable(yieldNode) && ((WorldItemPuppet)yieldNode).NetId == yieldId);

            world.Sim.Sim.Remove(pump);
        }
    }

    // GAP B3 (loopback crop-harvest yield): proves the LISTEN-SERVER host's F-interact harvest routes over the
    // (already-complete v10) crop wire instead of the direct CropManager.Harvest -- so the dropped yield is the
    // server's REPLICATED (visible + focusable) world item and the harvest XP is server-awarded, NOT a hidden
    // SuppressLocalVisual SP drop with XP awarded locally then overwritten by adoption. Exercises a REAL MpLoopback
    // (like unify.loopback_upgrade_skill): the host owns its OWN real CropManager CropNode (there is NO
    // CropReplicaView in the loopback, per A4's double-authority guard). B3 wires Player.NetHarvestCrop =
    // SendHarvestCrop AND CropNetSync stamps the server crop id onto that host node, so RequestHarvestNearestCrop
    // (NetId!=0 scan) finds it and routes over the wire.
    //
    // TEETH: reverting EITHER B3 edit breaks it. Revert the CropNetSync NetId stamp -> the host node keeps NetId 0
    // -> the "(B3) stamped" Until never holds (and the NetId!=0 harvest scan would skip it too). Revert the
    // MpLoopback seam -> NetHarvestCrop is null -> RequestHarvestNearestCrop returns false -> no wire harvest ->
    // the crop stays, no server yield world item, server XP stays 0. (Verified by reverting each edit + rerunning.)
    public class UnifyCropHarvestYield : GameTest
    {
        public override string Name => "unify.crop_harvest_yield";
        public override double TimeoutSimSeconds => 40;

        // a world-item is visible + focusable iff it renders AND carries a collider on the item look-hit layer
        // (bit 7 -- what the player's look-ray focuses). Same check as UnifyCropClientHarvest / UnifyPassiveLootSingle.
        static bool VisibleFocusable(Node3D n)
        {
            if (n == null || !GodotObject.IsInstanceValid(n) || !n.Visible) return false;
            if (n is CollisionObject3D self && (self.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            foreach (var ch in n.GetChildren())
                if (ch is CollisionObject3D co && (co.CollisionLayer & WorldItem.ItemHitLayer) != 0) return true;
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // the yield world-item puppet resolves against the real catalog

            // the exact headless rig unify.loopback_upgrade_skill uses, plus a CropManager -- the listen-server host
            // owns its real crop nodes (Plant is the SP path; CropNetSync mints + stamps them for the wire).
            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false }; World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false }; World.AddChild(resources);
            var cropMgr = new CropManager(); World.AddChild(cropMgr);   // _Ready loads crops/farms + sets the singleton
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);   // _Ready: shell + CropManager registries

            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);

            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Skills.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected + server skills entity present",
                    loop.Client.State == NetSessionState.Connected
                    && loop.Server.Skills.TryGet(loop.Client.PlayerId, out _));

            // the host plants a GROWN carrot ~1 m to the side (well within the 3 m harvest reach) via its real
            // CropManager -- the exact SP path. CropNetSync mints the server crop entity from the node next 2 Hz tick.
            var cropNode = CropManager.Plant("carrot", player.GlobalPosition + new Vector3(1f, 0f, 0f), grown: true);
            T.Check("host planted a GROWN carrot node in the \"crop\" group",
                    cropNode != null && cropNode.Grown && cropNode.IsInGroup("crop"));
            if (cropNode == null) yield break;

            // (B3 stamp) CropNetSync mints the entity AND stamps the server crop id onto the host's own node
            yield return Until(() => cropNode.NetId != 0 && loop.Server.Crops.Count >= 1, 10);
            T.Check($"(B3) CropNetSync stamped the server crop id ({cropNode.NetId}) onto the host node + minted the entity",
                    cropNode.NetId != 0 && loop.Server.Crops.Count >= 1);

            // baseline: nothing harvested yet -- server XP 0, no yield world item on either side
            loop.Server.Skills.TryGet(loop.Client.PlayerId, out var se0);
            T.Check("(baseline) server harvest XP is 0", se0.Skills.experience == 0);
            T.Check("(baseline) no yield world item yet", loop.Server.WorldItems.Count == 0 && loop.Items.NodeCount == 0);

            // (B3 seam) F-interact routes RequestHarvestNearestCrop -> NetHarvestCrop -> SendHarvestCrop; public so we
            // drive the proximity scan without the raycast. The direct CropManager.Harvest else-branch is superseded.
            bool routed = player.RequestHarvestNearestCrop();
            T.Check("(B3) RequestHarvestNearestCrop found the stamped grown node + routed the wire harvest", routed);

            // (a) the server validated + removed the crop; CropNetSync retires the now-orphaned host node
            yield return Until(() => loop.Server.Crops.Count == 0, 10);
            T.Check("(a) the server removed the crop (OnHarvestCrop sink)", loop.Server.Crops.Count == 0);
            yield return Until(() => !GodotObject.IsInstanceValid(cropNode), 10);
            T.Check("(a) the host crop node retired via CropNetSync (server-driven, not a direct QueueFree)",
                    !GodotObject.IsInstanceValid(cropNode));

            // (b) the yield dropped as a REPLICATED world item, materialized VISIBLE+FOCUSABLE through the loopback's
            //     WorldItemReplicaView -- even under SuppressLocalVisual=true (the whole point: not a hidden SP drop).
            yield return Until(() => loop.Server.WorldItems.Count >= 1, 10);
            uint yieldId = 0;
            foreach (var wi in loop.Server.WorldItems.All) { yieldId = wi.NetIdValue; break; }
            T.Check($"(b) the server spawned the harvest yield world item ({yieldId})", yieldId != 0);
            yield return Until(() => loop.Items.TryGetNode(yieldId, out _), 10);
            bool haveYield = loop.Items.TryGetNode(yieldId, out var yieldNode);
            T.Check("(b) the yield materialized VISIBLE+FOCUSABLE on the host's WorldItemReplicaView",
                    haveYield && VisibleFocusable(yieldNode));

            // (c) the harvest XP is SERVER-awarded (not the SP local award that per-tick adoption overwrites)
            yield return Until(() => loop.Server.Skills.TryGet(loop.Client.PlayerId, out var se) && se.Skills.experience == 1, 10);
            loop.Server.Skills.TryGet(loop.Client.PlayerId, out var se1);
            T.Check($"(c) the server awarded the harvest XP (experience {se1.Skills.experience}, expected 1)", se1.Skills.experience == 1);
        }
    }

    // GAP B3 (no double-mutation on the loopback harvest): the invariant B3 leans on -- with the harvest seam wired,
    // F-interact routes the wire harvest AND the direct CropManager.Harvest else-branch is SUPERSEDED
    // (PlayerController.cs:2543, the "seam set => direct superseded" rule). Wiring the seam without skipping the
    // direct path would DOUBLE-mutate: a hidden SP yield drop + a locally-awarded XP + a self-QueueFree'd node
    // racing CropNetSync's removal. Uses a REAL MpLoopback so CropNetSync really stamps the host node (B3), then
    // overrides the seam with a CAPTURING lambda (the harvest still ROUTES but mutates no server/world state, so
    // "did the DIRECT branch also run" reads cleanly off the node + the "worlditems" group), and drives the REAL
    // F-interact chain (the p._UnhandledInput(Key) pattern from NetTests -- a Key event isn't gated by mouse capture).
    //
    // TEETH (against the B3 CropNetSync stamp): revert the stamp -> the host node keeps NetId 0 ->
    // RequestHarvestNearestCrop returns false inside the F chain -> it falls through to the direct
    // CropManager.NearestGrown/Harvest branch, which RUNS: the node self-QueueFree's and a local SP yield drops
    // (WorldItem.Spawn into "worlditems"). Post-fix "lambda fired + node still alive + no local yield" all hold;
    // reverting the stamp flips them (and the "(B3) stamped" gate times out first). (Verified by revert + rerun.)
    public class UnifyCropHarvestNoDouble : GameTest
    {
        public override string Name => "unify.crop_harvest_no_double";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false }; World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false }; World.AddChild(resources);
            var cropMgr = new CropManager(); World.AddChild(cropMgr);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Skills.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected", loop.Client.State == NetSessionState.Connected);

            var cropNode = CropManager.Plant("carrot", player.GlobalPosition + new Vector3(1f, 0f, 0f), grown: true);
            T.Check("host planted a GROWN carrot node", cropNode != null && cropNode.Grown);
            if (cropNode == null) yield break;

            // the REAL CropNetSync stamps the server crop id onto the host node (B3) -- the pre-req the no-double
            // invariant hinges on: only a stamped (NetId!=0) grown node lets RequestHarvestNearestCrop win the chain.
            yield return Until(() => cropNode.NetId != 0, 10);
            T.Check($"(B3) CropNetSync stamped the host node (NetId {cropNode.NetId})", cropNode.NetId != 0);

            // override the wire seam with a CAPTURING lambda: the harvest ROUTES (proving the seam path fires) but
            // mutates no server/world state, so "did the DIRECT branch also run" is read cleanly off the node + world.
            uint captured = 0; bool fired = false;
            player.NetHarvestCrop = netId => { captured = netId; fired = true; };
            int worldItemsBefore = World.GetTree().GetNodesInGroup("worlditems").Count;

            // drive the REAL F-interact chain headlessly (NetTests p._UnhandledInput(Key) pattern). No focusable
            // item/deployable/vehicle in the rig + the crop is to the SIDE (not in the look ray), so the chain falls
            // through to the harvest branches -> RequestHarvestNearestCrop wins -> the direct branch is skipped.
            player._UnhandledInput(new InputEventKey { Pressed = true, Keycode = Key.F });

            // the seam routed with the crop's NetId ...
            T.Check($"F-interact routed the harvest over the seam (fired={fired}, id={captured}, expected {cropNode.NetId})",
                    fired && captured == cropNode.NetId);
            // ... and the DIRECT CropManager.Harvest did NOT also run: the node is intact (not self-QueueFree'd) and
            //     no local SP yield world item dropped. Pre-fix (NetId 0) the direct branch frees the node + drops a yield.
            T.Check("no double-mutation: the host crop node is still alive (direct CropManager.Harvest skipped)",
                    GodotObject.IsInstanceValid(cropNode));
            T.Check("no double-mutation: no local SP yield world item was dropped",
                    World.GetTree().GetNodesInGroup("worlditems").Count == worldItemsBefore);
        }
    }

    // B5 (SP/MP-unify) phase gate 1: a REAL ClientWorldSession shell, server-authoritative fine vitals. The
    // shipped bug: HP was server-adopted but food/water/stamina ran the shell's LOCAL sim and the `died`
    // result was DISCARDED under adoption -> you drained to Food=0 and never died (the server ran no hunger
    // sim). Post-fix the server owns the hunger sim (stepped BETWEEN VehicleHost.Step and Combat.Step) and the
    // shell CONSUMES the SystemVitals(13) owner block (AdoptReplicatedFineVitals) -- its local fine mutation
    // is skipped. Phase A: the shell STARVES TO DEATH server-owned (HP drains through the SAME ServerCombat
    // sink weapons use -> PlayerDied Killer=0), then the server owns the respawn clock. Phase B: a consumed
    // FOOD raises server Food over the wire and HP REGENS (fed + hydrated) -- with clean teeth (pre-fix
    // OnConsume never raises food, so the food-gate stays closed and HP never regens). DESYNC-QUIET throughout.
    public class UnifyFineVitalsStarve : GameTest
    {
        public override string Name => "unify.fine_vitals_starve";
        public override double TimeoutSimSeconds => 90;

        static bool FindCell(PlayerInventory inv, ushort id, out byte page, out byte x, out byte y)
        {
            for (byte p = 0; p < inv.items.Length; p++)
            {
                var pg = inv.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item != null && j.item.id == id) { page = p; x = j.x; y = j.y; return true; }
                }
            }
            page = x = y = 0; return false;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // id 13 (Canned Beans: useFood 55, useHealth 10) resolves as a FOOD consumable

            var net = new MemNetwork(20260725);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "starver" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true, SurvivalDrain = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;
            var myDeaths = new List<PlayerDiedEvent>();
            sess.Client.PlayerDied += myDeaths.Add;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            T.Check("the DedicatedServer mirrored SurvivalDrain onto the vitals authority", ded.Server.Vitals.SurvivalDrain);
            yield return Ticks(5);
            T.Check("the shell adopts server-authoritative FINE vitals (NetFineVitalsAdopted)", sess.Shell.NetFineVitalsAdopted);

            // --- Phase A: starve to death, server-owned ---
            T.Check("the server owns a vitals entry for the player", ded.Server.Vitals.TryGet(sess.Client.PlayerId, out _));
            ded.Server.Vitals.TryGet(sess.Client.PlayerId, out var ve);
            ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var ce);
            ve.Sim.Food = 0f;                     // no food -> starvation
            ce.HealthExact = 5f; ce.Health = 5;   // seed HP low so death lands inside the budget

            yield return Until(() => sess.Shell.Food <= 0.02f, 5);
            T.Check($"(A) the shell adopted the server's starving Food ({sess.Shell.Food:0.00}), not its own local sim", sess.Shell.Food <= 0.02f);

            yield return Until(() => sess.Shell.IsDead, 12);
            T.Check("(A) the shell STARVED TO DEATH server-owned (IsDead)", sess.Shell.IsDead);
            bool sOk = ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var dCs);
            T.Check("(A) the server owns the death (Alive false, Health 0)", sOk && !dCs.Alive && dCs.Health == 0);
            bool sawKiller0 = false;
            foreach (var e in myDeaths) if (e.Victim == sess.Client.PlayerId && e.Killer == 0) sawKiller0 = true;
            T.Check("(A) PlayerDied(Killer=0) reached the owner (starvation routed through the env sink)", sawKiller0);

            // re-feed the server sim during the dead window so the respawned shell isn't instantly re-starving
            ve.Sim.Food = 1f; ve.Sim.Water = 1f;

            // --- server owns the 3.5 s respawn clock -> the shell revives at full HP + food ---
            yield return Until(() => !sess.Shell.IsDead, 8);
            T.Check("(A) the owner revived on the server respawn fact", !sess.Shell.IsDead);
            yield return Ticks(30);   // let the recov reposition + resume settle
            T.Check($"(A) respawned to full HP, adopted ({sess.Shell.Health:0})", Mathf.IsEqualApprox(sess.Shell.Health, 100f));

            // --- Phase B: consume a FOOD raises server Food; HP then REGENS (fed + hydrated) ---
            bool haveInv = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("the server owns the local player's inventory", haveInv);
            if (!haveInv) yield break;
            bool seeded = sInv.Inventory.items[2].tryAddItem(new Item(13));
            T.Check("seeded a Canned Beans into the SERVER grid", seeded);

            // damage HP to 60 and drop food BELOW the 0.30 regen gate so pre-consume there is NO regen
            ded.Server.Combat.QueueDebugPlayerDamage(sess.Client.PlayerId, 40f, 0);
            yield return Until(() => sess.Shell.Health <= 61f, 5);
            ded.Server.Vitals.TryGet(sess.Client.PlayerId, out ve);
            ve.Sim.Food = 0.20f;   // < 0.30 -> the fed-gate is closed, HP holds
            yield return Ticks(30);
            T.Check($"(B) HP HOLDS while under-fed -- no regen ({sess.Shell.Health:0} ~ 60)", sess.Shell.Health <= 62f);

            yield return Until(() => sess.Shell.Inventory.getItemCount(13) >= 1, 5);
            T.Check("the shell adopted the seeded beans", sess.Shell.Inventory.getItemCount(13) >= 1);
            FindCell(sess.Shell.Inventory, 13, out byte bp, out byte bx, out byte by);
            var ui = new InventoryUI { Inv = sess.Shell.Inventory, Player = sess.Shell };
            World.AddChild(ui);
            yield return Ticks(2);   // _Ready builds the columns so UseSelected is safe
            ui.DebugUse(bp, bx, by);   // the REAL Use-button path -> RequestConsume over the wire

            yield return Until(() => ded.Server.Vitals.TryGet(sess.Client.PlayerId, out var v2) && v2.Sim.Food > 0.5f, 5);
            ded.Server.Vitals.TryGet(sess.Client.PlayerId, out ve);
            T.Check($"(B) the consumed FOOD raised server Food ({ve.Sim.Food:0.00} > 0.5, +0.55)", ve.Sim.Food > 0.5f);

            // the +10 useHealth bump lands first (60->70); the CONTINUED rise past it is passive regen (post-fix
            // only -- pre-fix the food was never raised, so the fed-gate stays closed and HP stays flat at 70).
            yield return Until(() => sess.Shell.Health >= 69f, 5);
            float hpFed = sess.Shell.Health;
            yield return Until(() => sess.Shell.Health > hpFed + 1f, 5);
            T.Check($"(B) HP REGENS while fed ({sess.Shell.Health:0} rose past the just-fed {hpFed:0})", sess.Shell.Health > hpFed + 1f);
            T.Check($"DESYNC-QUIET across starve + respawn + feed ({desyncs} fired)", desyncs == 0);

            world.Sim.Sim.Remove(pump);
        }
    }

    // B5 (SP/MP-unify) phase gate 2: the loopback listen-server host. The host drives via the direct SP path
    // (its node IS the authority), but its vitals are server-owned + adopted like the MP shell. Two teeth: (1)
    // SPRINT is reflected in server Stamina -- proving MpLoopback now PACKS the shell's stance into SendMoveInput
    // (was buttons=0), so the server derives `sprinting` from the adopted stance and drains stamina (stamina
    // server-owned, sprint client-auth, no second body); pre-fix buttons=0 => the server sees STAND and stamina
    // never drains. (2) The host STARVES server-side and DIES -- the loopback's PlayerDied wiring renders it.
    public class UnifyFineVitalsLoopbackStarve : GameTest
    {
        public override string Name => "unify.fine_vitals_loopback_starve";
        public override double TimeoutSimSeconds => 45;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // MpLoopback --spconsume seeds the SP demo kit on join
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);   // let _Ready run (shell built)

            bool prevDrain = PlayerController.SurvivalDrain;
            PlayerController.SurvivalDrain = true;   // F1 `survival on` -- the loopback mirrors it into the server authority

            var loop = new MpLoopback { Player = player, Driver = driver,
                                        DayNight = dayNight, Resources = resources,
                                        ConsumeDeployables = true };
            World.AddChild(loop);

            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Vitals.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback connected + server vitals entry present",
                    loop.Client.State == NetSessionState.Connected && loop.Server.Vitals.TryGet(loop.Client.PlayerId, out _));
            yield return Ticks(6);
            T.Check("the loopback mirrored PlayerController.SurvivalDrain onto the server authority", loop.Server.Vitals.SurvivalDrain);
            T.Check("the host shell adopts server-authoritative fine vitals", player.NetFineVitalsAdopted);

            // --- teeth 1: sprint reflected in server Stamina (proves the SendMoveInput stance-pack) ---
            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out var ve);
            float staminaBefore = ve.Sim.Stamina;
            T.Check($"(sprint) stamina starts near full ({staminaBefore:0.00})", staminaBefore > 0.9f);
            player.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   // run forward
            player.ScriptedStance = EPlayerStance.SPRINT;             // force the sprint stance the shell packs

            yield return Until(() => loop.Server.Vitals.TryGet(loop.Client.PlayerId, out var v) && v.Sim.Stamina < staminaBefore - 0.03f, 6);
            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out ve);
            T.Check($"(sprint) the server derived sprinting from the PACKED stance -> Stamina drained ({ve.Sim.Stamina:0.00} < {staminaBefore:0.00})",
                    ve.Sim.Stamina < staminaBefore - 0.03f);
            T.Check($"(sprint) the host shell adopted the server Stamina ({player.Stamina:0.00})", player.Stamina <= ve.Sim.Stamina + 0.05f);
            player.ScriptedInput = UnityEngine.Vector2.zero;
            player.ScriptedStance = null;

            // --- teeth 2: starve to death server-side ---
            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out ve);
            loop.Server.CombatState.TryGet(loop.Client.PlayerId, out var ce);
            ve.Sim.Food = 0f;                     // no food -> starvation
            ce.HealthExact = 4f; ce.Health = 4;   // seed HP low so death lands inside the budget

            yield return Until(() => player.IsDead, 12);
            T.Check("(starve) the host shell drained to DEATH server-side (IsDead)", player.IsDead);
            bool cOk = loop.Server.CombatState.TryGet(loop.Client.PlayerId, out var dCs);
            T.Check("(starve) the server owns the death (Alive false, Health 0)", cOk && !dCs.Alive && dCs.Health == 0);

            PlayerController.SurvivalDrain = prevDrain;   // restore the process-global toggle
        }
    }

    // A3 (SP/MP-unify): the grid-power mains SOURCE promoted from an SP-local IPowerDevice into a server-placed
    // deployable-graph FIXTURE. The WorldBuilder records Circuit_0 sources; the dedicated / consuming-loopback
    // server ServerPlaces them (mains OFF), they ride SystemDeployables, and the client's DeployableReplicaView
    // materializes a GridPowerSource NODE deriving producing from the replicated entity.ToggledOn (never local
    // GlobalPower). The F1/toggleGlobalPower switch is a server-gated MECHANIC over the wire.
    //
    // This drives the REAL DedicatedServer.Fixtures -> ServerPlace plumbing (a synthetic fixture list), then the
    // wire toggle. TEETH: (1) the grid source materializes ONLY if DeployableDef.GridSource is registered (remove
    // it from DeployableDef.All -> ServerPlace(9200) returns null -> no fixture entity -> gridId stays 0). (2) the
    // consumer NODE energizes ONLY when the mains toggle over the wire flips ToggledOn (RunConsole verb absent ->
    // never toggles -> the node stays dark). The same node-power assert is checked BOTH dark and lit, gated only by
    // the mains bit, so it flips WITH the toggle, not by luck.
    public class UnifyGridPowerFixture : GameTest
    {
        public override string Name => "unify.grid_power_fixture";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            PowerNet.SetGlobalPower(false);   // defensive: mains OFF is the default; a replica derives producing from ToggledOn, NOT this flag
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // `give 459` resolves the spotlight (2x2) against the real catalog

            var net = new MemNetwork(20260720);
            var client = new NetWorldClient(new MemClientTransport(net), "local", contentHash: NetContent.Hash);
            DeployableNetSchema.RegisterAll(client.Deployables.Schema);   // server side registered by DedicatedServer
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);

            // hand the dedicated server a recorded grid-power fixture: DedicatedServer._Ready ServerPlaces it into
            // the deployable graph (mains OFF) -- the exact WorldBuilder.Fixtures -> ServerPlace plumbing A3 adds.
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), AllowCheats = true,
                Fixtures = new List<FixtureRecord> { new FixtureRecord { DefId = DeployableDef.GridSource.Id, Pos = new Vector3(-2f, 0f, 0f), YawDegrees = 0f, Basis = Basis.Identity } } };
            World.AddChild(ded);

            var view = new DeployableReplicaView { Client = client };
            World.AddChild(view);

            client.Connect();
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("local client joined the loopback server", client.State == NetSessionState.Connected);

            // the world-built grid source rode the join snapshot -> a fixture entity + a materialized GridPowerSource
            // node (NOT a Deployable body), stamped with the server NetId. TEETH (1): no def -> ServerPlace null -> 0.
            uint gridId = 0;
            yield return Until(() => { gridId = 0; foreach (var e in client.Deployables.All) if (e.DefId == DeployableDef.GridSource.Id) gridId = e.NetIdValue; return gridId != 0; }, 5);
            T.Check($"the world-built grid source replicated as a fixture entity (netId {gridId})", gridId != 0);
            yield return Until(() => view.TryGetGrid(gridId, out _), 5);
            T.Check($"the view materialized a GridPowerSource node stamped with the server NetId ({view.GridCount})",
                    view.TryGetGrid(gridId, out var gnode) && gnode.NetId == gridId && gnode.NetId != 0);

            // stock + place a spotlight consumer over the wire, then wire the grid Output(0) -> its Consumer(0)
            client.SendConsole("give 459");
            yield return Until(() => ded.Server.Inventories.TryGet(client.PlayerId, out var inv) && inv.Inventory.getItemCount(459) == 1, 5);
            client.SendPlaceDeployable(459, new UnityEngine.Vector3(2f, 0f, 0f), 0f);
            uint spotId = 0;
            yield return Until(() => { spotId = 0; foreach (var e in client.Deployables.All) if (e.DefId == 459) spotId = e.NetIdValue; return spotId != 0; }, 5);
            T.Check($"spotlight placed over the wire (netId {spotId})", spotId != 0);
            client.SendConnectWire(gridId, 0, spotId, 0);
            yield return Until(() => client.Deployables.WireCount == 1, 5);
            T.Check("grid source Output wired to the spotlight Consumer", client.Deployables.WireCount == 1);

            // mains OFF (default) -> the consumer is dark on BOTH the replicated-entity solve and the materialized node graph
            ded.Server.Deployables.Solve();
            client.Deployables.Solve();
            ded.Server.Deployables.TryGet(spotId, out var sOff);
            client.Deployables.TryGet(spotId, out var cOff);
            T.Check("OFF: server consumer dark", !sOff.Solved[0].Powered);
            T.Check("OFF: client consumer dark", !cOff.Solved[0].Powered);
            yield return Until(() => view.TryGetNode(spotId, out var d) && !d.DebugConsumerPowered, 5);
            T.Check("OFF: the consumed spotlight NODE is unpowered (mains never toggled)",
                    view.TryGetNode(spotId, out var dOff) && !dOff.DebugConsumerPowered);

            // toggle the MAINS ON over the wire (the F1/toggleGlobalPower plane): the server flips every GridSource's
            // ToggledOn + broadcasts; the replica node derives producing from it. TEETH (2): no verb -> never toggles.
            client.SendConsole("toggleglobalpower on");
            yield return Until(() => client.Deployables.TryGet(gridId, out var g) && g.ToggledOn, 5);
            T.Check("mains toggled ON + replicated the ToggledOn bit", client.Deployables.TryGet(gridId, out var gOn) && gOn.ToggledOn);

            // ON -> the replicated-entity solve AND the local node graph both energize the wired consumer
            ded.Server.Deployables.Solve();
            client.Deployables.Solve();
            ded.Server.Deployables.TryGet(spotId, out var sOn);
            client.Deployables.TryGet(spotId, out var cOn);
            T.Check("ON: server consumer powered off the mains", sOn.Solved[0].Powered);
            T.Check("ON: client replica consumer powered (same solver, replicated ToggledOn)", cOn.Solved[0].Powered);
            T.Check("ON: the materialized grid source PRODUCES (NetProducingOverride follows ToggledOn)",
                    view.TryGetGrid(gridId, out var gp) && gp.PowerProducing);
            yield return Until(() => view.TryGetNode(spotId, out var d) && d.DebugConsumerPowered, 5);
            T.Check("ON: the consumed spotlight NODE is POWERED through the local PowerNet grid-source node",
                    view.TryGetNode(spotId, out var pn) && pn.DebugConsumerPowered);

            // toggle OFF over the wire -> dark again: the SAME node-power assert flips with the mains bit (teeth)
            client.SendConsole("toggleglobalpower off");
            yield return Until(() => client.Deployables.TryGet(gridId, out var g) && !g.ToggledOn, 5);
            yield return Until(() => view.TryGetNode(spotId, out var d) && !d.DebugConsumerPowered, 5);
            T.Check("OFF again: the consumed spotlight node goes dark once the mains toggle off",
                    view.TryGetNode(spotId, out var dn) && !dn.DebugConsumerPowered);

            world.Sim.Sim.Remove(pump);
            client.Disconnect();
        }
    }

    // A2 (SP/MP-unify): server-authoritative gas-pump fuel extract over the consuming loopback. Every Gas_Pump_0
    // is promoted from an SP-local FluidTank the client never sees into a server-placed DeployableEntity
    // (FixtureKind.GasPump); the shared 8000 L tank lives ONLY on the server (GasStationServer), and
    // CommandExtractFuel is the SOLE mutation: it gates on a fresh Solve() (the pump's Consumer port Powered),
    // drains the absolute tank by min(canSpace, remaining), fills the held can, and writes the recomputed
    // 0..100 percent onto EVERY same-station pump's entity.Fuel in one tick.
    //
    // Built on the REAL MpLoopback under ConsumeDeployables (the exact node the SP game boots) + a real
    // PlayerController shell + TWO pumps sharing one station -- so the RMB extract drives the actual controller
    // seam (TryExtractFuel -> NetExtractFuel -> the wire), the server drains the shared tank, and both pumps'
    // replicated fill drops together. TEETH: without OnExtractFuel + GasStationServer the station never drains,
    // the can stays empty, and the pumps stay at their seeded 100% (every gate + the reject below flips on it).
    public class UnifyGasPumpFixtureExtract : GameTest
    {
        public override string Name => "unify.gaspump_fixture_extract";
        public override double TimeoutSimSeconds => 40;

        const ushort CAN = 1440;   // Industrial gas can (fuelCapacity 20) -- big enough that draining it moves the 8000 L station's quantized percent visibly (100 -> 99.75)

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // item 1440 resolves for `give` + the shell's held-can guard

            // the same headless-safe SP stand-ins unify.deploy_pickup feeds the loopback
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // the REAL shell the loopback drives + adopts onto
            yield return Ticks(2);

            // two gas pumps on ONE station (S), near the player, handed to the loopback as recorded world fixtures
            const int S = 4242;
            var fixtures = new List<FixtureRecord>
            {
                new FixtureRecord { DefId = DeployableDef.GasPump.Id, Pos = new Vector3(2f, 0f, 0f), YawDegrees = 0f, Basis = Basis.Identity, StationId = S },
                new FixtureRecord { DefId = DeployableDef.GasPump.Id, Pos = new Vector3(3.5f, 0f, 0f), YawDegrees = 0f, Basis = Basis.Identity, StationId = S },
            };
            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources,
                                        Fixtures = fixtures, ConsumeDeployables = true };
            World.AddChild(loop);

            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback client connected + server inventory present",
                    loop.Client.State == NetSessionState.Connected
                    && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _));

            // the two pumps rode the join snapshot -> fixture entities + materialized GasPump nodes, seeded 100% full
            uint p0 = 0, p1 = 0;
            yield return Until(() => { p0 = 0; p1 = 0; foreach (var e in loop.Client.Deployables.All) if (e.DefId == DeployableDef.GasPump.Id) { if (p0 == 0) p0 = e.NetIdValue; else p1 = e.NetIdValue; } return p0 != 0 && p1 != 0; }, 10);
            T.Check($"both gas pumps replicated as fixture entities ({p0}, {p1})", p0 != 0 && p1 != 0);
            yield return Until(() => loop.Deploys != null && loop.Deploys.GasPumpCount == 2, 10);
            T.Check($"the view materialized 2 GasPump nodes ({loop.Deploys?.GasPumpCount})", loop.Deploys != null && loop.Deploys.GasPumpCount == 2);
            loop.Client.Deployables.TryGet(p0, out var seed0);
            T.Check($"the pump seeded FULL (100%) before any extract (Fuel={seed0.Fuel})", seed0.Fuel >= 99.9f);

            // power pump#0: a live generator wired to its Consumer port (authority-seeded on the loopback server)
            var srv = loop.Server.Deployables;
            long stick = loop.Server.Session.CurrentTick;
            var gen = srv.ServerPlace(loop.Server.Ids.Mint(), DeployableDef.Generator.Id, 0, new UnityEngine.Vector3(-2f, 0f, 0f), 0f, stick);
            srv.ServerToggle(gen.NetIdValue, true, stick);
            srv.ServerConnectWire(loop.Server.Ids.Mint(), gen.NetIdValue, 0, p0, 0, stick);

            // give the server a gas can (the extract fills THIS server-side item), and hand the shell a held can (the
            // RMB guard). Two separate items: the shell's is the guard, the server's is the authoritative fill.
            loop.Client.SendConsole($"give {CAN}");
            yield return Until(() => ServerCanFuel(loop, CAN) >= 0f, 10);
            player.SetHeldFuelCanForTest(new SDG.Unturned.Item(CAN));   // fuelLevel -1 (fresh) -> 20 free space

            float full = loop.Server.Transactions.FuelStations.Remaining(S);
            T.Check($"the shared station tank starts full ({full} L)", full >= 7999f);

            // THE ACT: focus pump#0 + drive the REAL controller extract path (TryExtractFuel -> NetExtractFuel -> wire)
            loop.Deploys.TryGetGasPump(p0, out var node0);
            T.Check("got the materialized pump node to extract from", node0 != null && node0.NetId == p0);
            player.SetFocusGasPumpForTest(node0);
            player.TryExtractFuel();

            // GATE 1: the absolute station tank drained by the can's 20 L (server-authoritative)
            yield return Until(() => loop.Server.Transactions.FuelStations.Remaining(S) <= full - 19.9f, 15);
            float after = loop.Server.Transactions.FuelStations.Remaining(S);
            T.Check($"(gate) the shared station tank drained by the extract ({full} -> {after})", after <= full - 19.9f);

            // GATE 2: the server-side can filled by the pulled amount (the owner echo re-adopts the fuller can locally)
            yield return Until(() => ServerCanFuel(loop, CAN) >= 19.9f, 15);
            T.Check($"(gate) the held can filled server-side ({ServerCanFuel(loop, CAN)})", ServerCanFuel(loop, CAN) >= 19.9f);

            // GATE 3: BOTH pumps replicate the SAME drained percent (< 100 -- the fan-out fired), the view mirrors it
            yield return Until(() => loop.Client.Deployables.TryGet(p0, out var a) && loop.Client.Deployables.TryGet(p1, out var b) && a.Fuel == b.Fuel && a.Fuel < 100f, 15);
            loop.Client.Deployables.TryGet(p0, out var ce0); loop.Client.Deployables.TryGet(p1, out var ce1);
            T.Check($"(gate) both pumps replicate the SAME drained percent ({ce0.Fuel} == {ce1.Fuel}, < 100 = fan-out)", ce0.Fuel == ce1.Fuel && ce0.Fuel < 100f);
            loop.Deploys.TryGetGasPump(p0, out var mirror);
            T.Check($"(gate) the materialized pump node mirrors the replicated percent ({mirror.FillPercent} ~= {ce0.Fuel})",
                    Mathf.Abs(mirror.FillPercent - ce0.Fuel) < 0.01f);

            // reject-unpowered (TEETH): extract from pump#1 (never wired to a source) pulls nothing
            float before = loop.Server.Transactions.FuelStations.Remaining(S);
            loop.Deploys.TryGetGasPump(p1, out var node1);
            player.SetFocusGasPumpForTest(node1);
            player.TryExtractFuel();
            yield return Ticks(30);
            T.Check($"(teeth) an UNPOWERED pump drains nothing ({loop.Server.Transactions.FuelStations.Remaining(S)} == {before})",
                    Mathf.Abs(loop.Server.Transactions.FuelStations.Remaining(S) - before) < 0.01f);

            loop.Client.Disconnect();
        }

        static float ServerCanFuel(MpLoopback loop, ushort id)
        {
            if (!loop.Server.Inventories.TryGet(loop.Client.PlayerId, out var e)) return -999f;
            foreach (var page in e.Inventory.items)
                foreach (var jar in page.items)
                    if (jar.item != null && jar.item.id == id) return jar.item.fuelLevel;
            return -999f;
        }
    }
}
