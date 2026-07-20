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
}
