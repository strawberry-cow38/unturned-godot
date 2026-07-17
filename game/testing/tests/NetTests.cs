using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // MP_PLAN §4 Phase 3: the dedicated server boots headless -- WorldBuilder assembles the dedicated-mode
    // world (no camera/HUD/viewmodel/local player) into the sandbox, the SimRoot spine ticks it, and a
    // client joins the DedicatedServer's NetServerSession and gets replicated its own avatar. The map path
    // is deliberately bogus so the build takes the deterministic no-map fallback (flat ground) on every
    // box; the transport is MemTransport so the test opens no sockets. What this proves: the ONE world
    // path + net host + sim spine boot and serve under --tests, on any machine.
    public class NetDedicatedBoot : GameTest
    {
        public override string Name => "net.dedicated_boot";
        public override IEnumerable<Step> Run()
        {
            // dedicated world build: syncLoad -> zero frame-yields, completes before the Task is observed
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            T.Check("dedicated world build completed synchronously (syncLoad)", task.IsCompleted);
            var world = task.Result;
            T.Check("dedicated world ready (fallback ground, no map data needed)", world.Ready);
            T.Check("sim spine (SimDriver/SimRoot) present", world.Sim != null);
            T.Check("no local player in a dedicated world", world.Player == null);

            // the net host over an in-memory transport (no sockets under --tests). The client pump is
            // registered FIRST so each tick runs transport-delivery + client session BEFORE the server's
            // simulation step, with the server's replication send staying LAST (added by DedicatedServer's
            // _Ready, which fires inside AddChild) -- the §2.5 ordering, on the real SimRoot spine.
            var net = new MemNetwork(4242);
            var client = new NetWorldClient(new MemClientTransport(net), "l1", contentHash: NetContent.Hash);   // Phase 4: the join gate rejects a mismatched content hash
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => { net.Tick(); client.Tick(); }, "l1.clientpump"));
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            client.Connect();

            long tick0 = world.Sim.Tick;
            yield return Ticks(30);
            // >= 25, not == 30: the host decrements its tick budget before the driver node processes, and
            // the driver was added mid-frame -- alignment slack of a couple ticks is expected, advancing is what matters
            T.Check($"sim ticks advance under --tests (from {tick0} to {world.Sim.Tick})", world.Sim.Tick >= tick0 + 25);
            yield return Until(() => client.State == NetSessionState.Connected, 5);
            T.Check("client joined the dedicated server", client.State == NetSessionState.Connected);
            T.Check("server session tracks 1 peer", ded.Server.Session.Peers.Count == 1);

            // the joined player walks; the server integrates authoritatively and snapshots it back
            var walk = new DelegateSimStep((t, dt) => client.SendMoveInput(0f, 1f, 0f), "l1.input");
            world.Sim.Sim.Add(walk);
            yield return Until(() => client.Players.TryGetByOwner(client.PlayerId, out var me) && me.Pos.z > 0.5f, 5);
            bool has = client.Players.TryGetByOwner(client.PlayerId, out var self);
            T.Check("client received its own avatar through the snapshot plane", has);
            T.Check($"server-authoritative movement replicated back (z={self?.Pos.z:0.00})", has && self.Pos.z > 0.5f);
            T.Check("snapshots flowed (composer diagnostics)", ded.Server.Composer.Diag.FullSnapshotsComposed + ded.Server.Composer.Diag.DeltaSnapshotsComposed > 0);

            // teardown: unhook the extra sim steps so nothing pumps the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(walk);
            client.Disconnect();
        }
    }

    // MP_PLAN §4 Phase 4: two in-process clients on the real world path over MemTransport. Client A is a
    // REAL PlayerController shell -- the SP-loopback/listen-server construction: the node walks with real
    // physics on the world's ground, MoveInputs flow for the ack loop, and ServerDrive writes the shell's
    // transform into the authoritative replication entity. Client B is a headless PREDICTED walker
    // (ClientPrediction -- the same sim-core as the server). Both join through the full handshake
    // (content hash -> Accept -> reliable FULL join snapshot -> deltas); B appears in A's world through
    // the RemotePlayers CharacterModel-puppet path; every replica converges on the authority.
    public class NetLoopbackJoinMove : GameTest
    {
        public override string Name => "net.loopback_join_move";
        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(7777);
            // client A's shell: a real PlayerController walking the real ground via ScriptedInput
            var player = Rigs.Player(World, new Vector3(0f, 1.1f, 0f));
            player.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   // walk forward
            var a = new NetWorldClient(new MemClientTransport(net), "shell", contentHash: NetContent.Hash);
            var b = new NetWorldClient(new MemClientTransport(net), "walker", contentHash: NetContent.Hash);

            DedicatedServer ded = null;
            float bForward = 1f;    // B's held input; zeroed to settle
            bool inputsQuiet = false;   // final settle: stop sending seqs entirely so replicas reach EXACT parity (held-keys model)
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); a.Tick(); b.Tick();
                if (inputsQuiet) return;
                if (a.State == NetSessionState.Connected && ded != null && GodotObject.IsInstanceValid(player))
                {
                    // the loopback local player: input over the wire + the node as ServerDrive authority
                    float yaw = player.RotationDegrees.Y;
                    ushort seq = a.SendMoveInput(player.LastMoveInput.x, player.LastMoveInput.y, yaw);
                    var p = player.GlobalPosition;
                    ded.Server.Players.ServerDrive(a.PlayerId, new UnityEngine.Vector3(p.X, p.Y, p.Z), yaw,
                                                   seq, ded.Server.Session.CurrentTick);
                    if (seq != 0) a.Prediction.Reconciler.Record(seq, new UnityEngine.Vector3(p.X, p.Y, p.Z));
                }
                if (b.State == NetSessionState.Connected)
                {
                    ushort seq = b.SendMoveInput(0f, bForward, 90f);
                    b.Prediction.PredictAndRecord(seq, 0f, bForward, 90f, (float)dt);
                }
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + replicate stay after/LAST (§2.5)
            ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            var remotes = new RemotePlayers { Client = a };
            World.AddChild(remotes);
            a.Connect();
            b.Connect();

            yield return Until(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected, 5);
            T.Check("both clients joined (content hash accepted)", a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            T.Check("server tracks 2 peers", ded.Server.Session.Peers.Count == 2);
            yield return Until(() => a.JoinSnapshotsApplied >= 1 && b.JoinSnapshotsApplied >= 1, 5);
            T.Check($"reliable FULL join snapshot applied on both (a={a.JoinSnapshotsApplied}, b={b.JoinSnapshotsApplied})",
                    a.JoinSnapshotsApplied >= 1 && b.JoinSnapshotsApplied >= 1);

            // both walk for ~2.5 s of sim
            yield return Ticks(125);
            T.Check("A sees B's avatar as a puppet (CharacterModel path)", remotes.PuppetCount == 1);
            bool bSeen = b.Players.TryGetByOwner(b.PlayerId, out var bSelf);
            // spawn is at most 2 m from origin (SpawnPosition), the walk covers ~11 m -> well past 4 m
            T.Check($"B moved under server authority (|pos| {(bSeen ? bSelf.Pos.magnitude : 0f):0.0} m)",
                    bSeen && bSelf.Pos.magnitude > 4f);

            // settle: stop both movers (zero-axes inputs still flow -> the server processes final seqs)...
            player.ScriptedInput = UnityEngine.Vector2.zero;
            bForward = 0f;
            yield return Ticks(40);
            // ...then go input-QUIET so the last snapshots flush and replicas can reach exact parity
            // (while seqs keep flowing, lastProcessedInputSeq on a replica always trails by one snapshot)
            inputsQuiet = true;
            yield return Ticks(30);

            // positions converge, every replica against the authority (the plan's §6 parity shape)
            T.Check("A replica == server (StateHash parity)", a.Players.StateHash() == ded.Server.Players.StateHash());
            T.Check("B replica == server (StateHash parity)", b.Players.StateHash() == ded.Server.Players.StateHash());

            ded.Server.Players.TryGetByOwner(b.PlayerId, out var serverB);
            float predErr = (b.Prediction.Pos - serverB.Pos).magnitude;
            T.Check($"B's prediction converged on the authority (err {predErr:0.###} m)", predErr < 0.05f);

            bool hasPuppet = remotes.TryGetPuppet(b.PlayerId, out var puppet);
            float puppetErr = hasPuppet ? puppet.Position.DistanceTo(new Vector3(serverB.Pos.x, serverB.Pos.y, serverB.Pos.z)) : float.MaxValue;
            T.Check($"B's puppet in A's world converged (err {puppetErr:0.###} m)", puppetErr < 0.25f);

            ded.Server.Players.TryGetByOwner(a.PlayerId, out var serverA);
            bool aSeenByB = b.Players.TryGetByOwner(a.PlayerId, out var aOnB);
            float shellErr = aSeenByB ? (new UnityEngine.Vector3(player.GlobalPosition.X, player.GlobalPosition.Y, player.GlobalPosition.Z) - aOnB.Pos).magnitude : float.MaxValue;
            T.Check($"B sees A at A's real (physics-walked) position (err {shellErr:0.###} m)", shellErr < 0.05f);
            T.Check($"A's shell acked input seqs through the drive (seq {serverA?.LastProcessedInputSeq ?? 0})", (serverA?.LastProcessedInputSeq ?? 0) > 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            a.Disconnect();
            b.Disconnect();
        }
    }

    // MP_PLAN §4 Phase 6: the transactional showcase end to end on the real world path (§3.1). Client A
    // console-gives itself a generator + spotlight (ConsoleCommand -- the server-gated cheat plane), places
    // both and wires them by COMMAND; the server validates against the replicated def schema + A's server
    // inventory, owns the graph, and broadcasts the topology facts; client B -- who typed nothing -- mirrors
    // the graph, runs the SAME pure PowerSolver on its replica, and its DeployableReplicaView materializes
    // real Deployable/Wire nodes whose local PowerNet pass LIGHTS THE LAMP. No solver output ever crossed
    // the wire.
    public class NetDeployWirePower : GameTest
    {
        public override string Name => "net.deploy_wire_power";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // `give 458` resolves against the real catalog (2x2 -> fits pockets)

            var net = new MemNetwork(6060);
            var a = new NetWorldClient(new MemClientTransport(net), "placer", contentHash: NetContent.Hash);
            var b = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            DeployableNetSchema.RegisterAll(a.Deployables.Schema);
            DeployableNetSchema.RegisterAll(b.Deployables.Schema);
            string consoleReply = null;
            a.ConsoleResult += e => consoleReply = e.Text;

            var pump = new DelegateSimStep((t, dt) => { net.Tick(); a.Tick(); b.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            var view = new DeployableReplicaView { Client = b };   // B's node mirror -- the lamp that must light
            World.AddChild(view);
            a.Connect();
            b.Connect();

            yield return Until(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected, 5);
            T.Check("both clients joined", a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            // A stocks up through the SERVER-GATED console (§2.3 "including DevConsole")
            a.SendConsole("give 458");
            a.SendConsole("give 459");
            yield return Until(() =>
            {
                if (!ded.Server.Inventories.TryGet(a.PlayerId, out var inv)) return false;
                return inv.Inventory.getItemCount(458) == 1 && inv.Inventory.getItemCount(459) == 1;
            }, 5);
            T.Check($"console gave A both deployable items server-side (reply: {consoleReply})",
                    ded.Server.Inventories.TryGet(a.PlayerId, out var aInv)
                    && aInv.Inventory.getItemCount(458) == 1 && aInv.Inventory.getItemCount(459) == 1);

            // A builds the rig by command: gen at (-2,0,0), spot at (+2,0,0), wire output->consumer, toggle on
            a.SendPlaceDeployable(458, new UnityEngine.Vector3(-2f, 0f, 0f), 0f);
            a.SendPlaceDeployable(459, new UnityEngine.Vector3(2f, 0f, 0f), 0f);
            yield return Until(() => a.Deployables.Count == 2, 5);
            uint genId = 0, spotId = 0;
            foreach (var e in a.Deployables.All)
            {
                if (e.DefId == 458) genId = e.NetIdValue;
                if (e.DefId == 459) spotId = e.NetIdValue;
            }
            T.Check("A's placements replicated back with NetIds", genId != 0 && spotId != 0);
            T.Check("placing consumed A's items (server grid)",
                    ded.Server.Inventories.TryGet(a.PlayerId, out var aInv2)
                    && aInv2.Inventory.getItemCount(458) == 0 && aInv2.Inventory.getItemCount(459) == 0);

            a.SendConnectWire(genId, 0, spotId, 0);
            a.SendToggleDeployable(genId, true);
            yield return Until(() => b.Deployables.WireCount == 1
                                  && b.Deployables.TryGet(genId, out var g) && g.ToggledOn, 5);
            T.Check("B mirrors the full graph (2 deployables + 1 wire + toggle) from events alone",
                    b.Deployables.Count == 2 && b.Deployables.WireCount == 1);
            T.Check("graph parity: A == server", a.Deployables.StateHash() == ded.Server.Deployables.StateHash());
            T.Check("graph parity: B == server", b.Deployables.StateHash() == ded.Server.Deployables.StateHash());

            // the §3.1 payoff: both sides SOLVE the replicated inputs and agree -- no output crossed the wire
            ded.Server.Deployables.Solve();
            b.Deployables.Solve();
            ded.Server.Deployables.TryGet(spotId, out var sSpot);
            b.Deployables.TryGet(spotId, out var bSpot);
            T.Check("server solve: consumer powered", sSpot.Solved[0].Powered);
            T.Check("B replica solve: consumer powered (same pure PowerSolver, same inputs)", bSpot.Solved[0].Powered);
            ded.Server.Deployables.TryGet(genId, out var sGen);
            b.Deployables.TryGet(genId, out var bGen);
            T.Check($"both sides agree on generator load (server {sGen.Solved[0].Draw:0} W, B {bGen.Solved[0].Draw:0} W)",
                    sGen.Solved[0].Draw == bGen.Solved[0].Draw && bGen.Solved[0].Draw == 250f);

            // and B's WORLD shows it: the replica view's nodes run the local PowerNet pass -> the lamp LIGHTS
            yield return Until(() => view.NodeCount == 2, 5);
            T.Check("B's replica view materialized both deployable nodes", view.NodeCount == 2);
            yield return Until(() => view.TryGetNode(spotId, out var d) && d.DebugConsumerPowered, 5);
            bool nodePowered = view.TryGetNode(spotId, out var spotNode) && spotNode.DebugConsumerPowered;
            T.Check("B's spotlight node consumer is powered through the node-graph solve", nodePowered);
            yield return Until(() => view.TryGetNode(spotId, out var d) && d.DebugLampsLit, 5);
            T.Check("B's lamp is LIT (warmup envelope past the visibility floor)",
                    view.TryGetNode(spotId, out var litNode) && litNode.DebugLampsLit);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            a.Disconnect();
            b.Disconnect();
        }
    }

    // MP_PLAN §4 Phase 5: the zombie brain/puppet split, end to end on the real world path. The SERVER runs
    // a real ZombieController brain (sensing/vision/nav chase) against a real PlayerController avatar it
    // acquires through PlayerRegistry (no Target wired -- the §3.5 generalization); ZombieNetSync (inside
    // DedicatedServer) publishes it at 12.5 Hz; a headless observer client receives the replicas and a
    // ZombiePuppets node renders an IsPuppet ZombieController that follows by INTERPOLATION -- visibly
    // chasing, never teleporting, never running AI of its own.
    public class NetZombieChaseSync : GameTest
    {
        public override string Name => "net.zombie_chase_sync";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // a walkable navmesh for the brain: one authored quad over the flat fallback ground (the real
            // map's pockets are pre-baked; the fallback world has none)
            var nm = new NavigationMesh();
            nm.Vertices = new[] { new Vector3(-60f, 0f, -60f), new Vector3(-60f, 0f, 60f), new Vector3(60f, 0f, 60f), new Vector3(60f, 0f, -60f) };
            nm.AddPolygon(new int[] { 0, 1, 2, 3 });
            World.AddChild(new NavigationRegion3D { NavigationMesh = nm });

            var net = new MemNetwork(20260717);
            var player = Rigs.Player(World, new Vector3(0f, 1.1f, 0f));   // the avatar the brain hunts (registers in PlayerRegistry)
            var b = new NetWorldClient(new MemClientTransport(net), "watcher", contentHash: NetContent.Hash);
            int swings = 0;
            b.ZombieSwung += _ => swings++;
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); b.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + zombie publish + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            var puppets = new ZombiePuppets { Client = b };
            World.AddChild(puppets);
            b.Connect();

            yield return Until(() => b.State == NetSessionState.Connected, 5);
            T.Check("observer client joined", b.State == NetSessionState.Connected);

            // the BRAIN: a real ZombieController with NO Target wired -- it must acquire the player through
            // PlayerRegistry (§3.5 "sensing generalizes to any player avatar"). Spawned 8 m out, facing the
            // player, inside the sneak-halved vision cone (20 * 0.5 = 10 m).
            var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(0f, 0.3f, 8f);

            yield return Until(() => b.Zombies.Count == 1, 5);
            T.Check("server zombie replicated to the observer (12.5 Hz snap)", b.Zombies.Count == 1);
            T.Check("exactly one zombie tracked server-side (the puppet never re-registers)", ded.Server.Zombies.Count == 1);
            yield return Until(() => puppets.PuppetCount == 1, 5);
            uint netId = 0;
            foreach (var e in b.Zombies.All) { netId = e.NetIdValue; break; }
            bool havePuppet = puppets.TryGetPuppet(netId, out var pup);
            T.Check("a puppet ZombieController spawned from the replica (IsPuppet)", havePuppet && pup.IsPuppet);

            // the avatar walks to meet the horde (a stationary player on open flat ground parks the RUSH
            // approach at ~2.3 m -- the 1 m short approach point + the agent's 1.3 m finish radius -- a
            // pre-existing brain equilibrium this phase must NOT retune; any movement breaks it)
            player.RotationDegrees = new Vector3(0f, 180f, 0f);   // face +Z, toward the zombie
            player.ScriptedInput = new UnityEngine.Vector2(0f, 0.4f);

            // sample the chase per tick: the brain closes 8 m -> swing range; the puppet follows by glide --
            // total displacement proves it tracked, the max per-tick step proves it interpolated (no snaps)
            float maxStep = 0f, total = 0f;
            Vector3 prev = pup.GlobalPosition;
            for (int i = 0; i < 250; i++)
            {
                yield return Ticks(1);
                var now = pup.GlobalPosition;
                float d = now.DistanceTo(prev);
                maxStep = Mathf.Max(maxStep, d);
                total += d;
                prev = now;
                float brainToPlayer = z.GlobalPosition.DistanceTo(player.GlobalPosition);
                if (brainToPlayer < 1.45f) player.ScriptedInput = UnityEngine.Vector2.zero;   // met the zombie -> stand and get bitten
                if (brainToPlayer < 1.5f && total > 3f) break;
            }
            float brainDist = z.GlobalPosition.DistanceTo(player.GlobalPosition);
            T.Check($"brain chased the registry-acquired player (now {brainDist:0.00} m out, from 8 m)", brainDist < 2.5f);
            T.Check($"puppet followed the chase ({total:0.0} m tracked)", total > 3f);
            T.Check($"puppet moved by INTERPOLATION (max per-tick step {maxStep:0.00} m)", maxStep > 0f && maxStep < 1.5f);
            float lag = pup.GlobalPosition.DistanceTo(z.GlobalPosition);
            T.Check($"puppet within glide lag of the brain ({lag:0.00} m)", lag < 2.5f);

            // in reach the brain swings: the AttackSwing event flows, and the avatar authoritatively bleeds
            yield return Until(() => swings > 0 && player.Health < 100f, 8);
            T.Check($"AttackSwing event reached the observer ({swings} swings)", swings > 0);
            T.Check($"server brain bit the avatar (health {player.Health:0})", player.Health < 100f);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            b.Disconnect();
        }
    }

    // MP_PLAN §3.7: the resource index space against the REAL committed content. The load-order index is
    // the implicit wire id the alive-bitmap keys on, so the dedicated build (VisualInstances=false --
    // colliders only, no MultiMesh) and the client build must agree on it EXACTLY, and SetAlive must fell/
    // respawn instances in both shapes.
    public class WorldResourceIndexSpace : GameTest
    {
        public override string Name => "world.resource_index_space";
        public override IEnumerable<Step> Run()
        {
            var visual = new ResourceField();                             // client/SP shape
            World.AddChild(visual);
            visual.LoadResources("NONE");
            var headless = new ResourceField { VisualInstances = false }; // dedicated shape (§5 fx hygiene)
            World.AddChild(headless);
            headless.LoadResources("NONE");

            T.Check($"committed content yields instances ({visual.InstanceCount})", visual.InstanceCount > 0);
            T.Check("dedicated + client builds agree on the index space (the §3.7 implicit wire id)",
                    visual.InstanceCount == headless.InstanceCount);
            int last = visual.InstanceCount - 1;
            T.Check("everything alive at load", visual.IsAlive(0) && visual.IsAlive(last) && headless.IsAlive(0));

            visual.SetAlive(0, false);
            headless.SetAlive(last, false);
            T.Check("SetAlive fells an instance in both build modes", !visual.IsAlive(0) && !headless.IsAlive(last));
            visual.SetAlive(0, true);
            T.Check("respawn restores it", visual.IsAlive(0));
            T.Check("out-of-range indices are ignored", !visual.IsAlive(-1) && !visual.IsAlive(visual.InstanceCount));
            yield break;
        }
    }

    // MP_PLAN §4 Phase 8: the disconnect/rejoin soak. Clients join and HARD-DROP repeatedly -- including
    // while seated in a vehicle (the nastiest state to leak) -- against a dedicated world with a persistent
    // observer watching. The world must stay consistent the whole way: seats free on disconnect, player
    // entities drain, the observer's replicas hold exact StateHash parity, the Phase 8 world clock keeps
    // deriving the same time on both sides, and a rejoin under a previously-used name lands clean.
    public class NetDropinDropout : GameTest
    {
        public override string Name => "net.dropin_dropout";
        public override double TimeoutSimSeconds => 45;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260717);
            var clients = new List<NetWorldClient>();   // everything in here gets pumped each tick
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); for (int i = 0; i < clients.Count; i++) clients[i].Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                                            DayNight = world.DayNight, Resources = world.Resources };   // Phase 8 world-state syncs
            World.AddChild(ded);

            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            clients.Add(observer);
            observer.Connect();
            yield return Until(() => observer.State == NetSessionState.Connected, 5);
            T.Check("persistent observer joined", observer.State == NetSessionState.Connected);

            // the world state the churners fight over: one real vehicle node
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(1f, 1.2f, 0f);
            yield return Until(() => observer.Vehicles.Count == 1, 5);
            uint vehId = 0;
            foreach (var e in observer.Vehicles.All) { vehId = e.NetIdValue; break; }
            T.Check("vehicle replicated to the observer", vehId != 0);
            yield return Ticks(50);   // let the spawn drop settle

            const int Cycles = 5;
            int seatedCycles = 0, seatFreedCycles = 0, drainedCycles = 0;
            for (int i = 0; i < Cycles; i++)
            {
                var churn = new NetWorldClient(new MemClientTransport(net), $"churn{i}", contentHash: NetContent.Hash);
                clients.Add(churn);
                churn.Connect();
                yield return Until(() => churn.State == NetSessionState.Connected && churn.JoinSnapshotsApplied >= 1, 5);
                if (churn.State != NetSessionState.Connected) break;

                for (int k = 0; k < 15; k++) { churn.SendMoveInput(0f, 1f, (i * 60f) % 360f); yield return Ticks(1); }   // walk a little
                for (int k = 0; k < 5; k++) { churn.SendMoveInput(0f, 0f, 0f); yield return Ticks(1); }                 // stop (held-keys)
                // joins-in-a-row spawn farther and farther out (SpawnPosition spacing) -- park the avatar
                // beside the jeep server-side so every cycle's Enter passes the same 6 m reach gate
                ded.Server.Players.ServerTeleport(churn.PlayerId, new UnityEngine.Vector3(2f, 0f, 0f), ded.Server.Session.CurrentTick);
                yield return Ticks(2);
                churn.SendEnterVehicle(vehId);
                yield return Until(() => ded.Server.Vehicles.TryGet(vehId, out var v) && v.DriverPlayerId == churn.PlayerId, 5);
                if (ded.Server.Vehicles.TryGet(vehId, out var seatE) && seatE.DriverPlayerId == churn.PlayerId) seatedCycles++;

                // HARD DROP while seated -- the hardening case: the seat must free through PeerDisconnected
                churn.Disconnect();
                yield return Until(() => ded.Server.Session.Peers.Count == 1, 8);
                clients.Remove(churn);
                if (ded.Server.Session.Peers.Count == 1) drainedCycles++;
                if (ded.Server.Vehicles.TryGet(vehId, out var freedE) && freedE.DriverPlayerId == 0) seatFreedCycles++;
            }
            T.Check($"all {Cycles} churn clients took the seat ({seatedCycles})", seatedCycles == Cycles);
            T.Check($"every drop freed the seat ({seatFreedCycles}/{Cycles})", seatFreedCycles == Cycles);
            T.Check($"every drop drained the peer ({drainedCycles}/{Cycles})", drainedCycles == Cycles);
            T.Check("player entities drained back to observer-only", ded.Server.Players.Count == 1);

            // the observer's world converged through all the churn -- exact parity, never a tolerance (§6)
            yield return Ticks(30);
            T.Check("observer players replica == server (StateHash parity)", observer.Players.StateHash() == ded.Server.Players.StateHash());
            T.Check("observer vehicles replica == server (StateHash parity)", observer.Vehicles.StateHash() == ded.Server.Vehicles.StateHash());

            // Phase 8 world clock (§3.7): synced through the churn, same tick -> same derived time-of-day
            T.Check("world clock synced to the observer", observer.Clock.HasClock);
            long ct = observer.Applier.LastAppliedServerTick;
            T.Check("observer derives the server's exact time-of-day from the snapshot tick",
                    observer.Clock.HasClock && observer.Clock.TimeOfDayAt(ct) == ded.Server.Clock.TimeOfDayAt(ct));

            // rejoin under a previously-used name: a clean join snapshot, full parity, no leaked state
            var rejoin = new NetWorldClient(new MemClientTransport(net), "churn0", contentHash: NetContent.Hash);
            clients.Add(rejoin);
            rejoin.Connect();
            yield return Until(() => rejoin.State == NetSessionState.Connected && rejoin.JoinSnapshotsApplied >= 1, 5);
            T.Check("rejoin under a reused name lands clean (reliable join snapshot)",
                    rejoin.State == NetSessionState.Connected && rejoin.JoinSnapshotsApplied >= 1);
            yield return Ticks(30);
            T.Check("rejoiner players replica == server", rejoin.Players.StateHash() == ded.Server.Players.StateHash());
            T.Check("rejoiner vehicles replica == server", rejoin.Vehicles.StateHash() == ded.Server.Vehicles.StateHash());
            T.Check("server tracks observer + rejoiner", ded.Server.Session.Peers.Count == 2);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            observer.Disconnect();
            rejoin.Disconnect();
        }
    }

    // MP_PLAN §4 Phase 7: server-authoritative vehicle physics end to end on the real world path (§3.6).
    // A real Vehicle NODE (VehicleBody3D) lives on the dedicated server world; a DRIVER client takes the
    // seat by command (validated at the choke point) and streams DriveInput @50 Hz; VehicleNetSync feeds
    // the held input into Vehicle.Drive -- the SAME seam the SP shell uses -- so the node physically
    // drives on the server's ground; an OBSERVER client's VehicleReplicaView renders a mesh-only puppet
    // that follows by dead-reckoned interpolation: converging on the server transform within tolerance,
    // never teleporting, never running vehicle physics of its own.
    public class NetVehicleDriveSync : GameTest
    {
        public override string Name => "net.vehicle_drive_sync";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(74747);
            var driver = new NetWorldClient(new MemClientTransport(net), "driver", contentHash: NetContent.Hash);
            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            uint drivingVeh = 0;   // once seated, the pump streams the held DriveInput every tick
            float throttle = 0f;
            bool handbrake = false;
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); driver.Tick(); observer.Tick();
                if (drivingVeh != 0) driver.SendDriveInput(drivingVeh, throttle, 0f, handbrake);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + vehicle sync + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            var view = new VehicleReplicaView { Client = observer };
            World.AddChild(view);
            driver.Connect();
            observer.Connect();

            // the server-side vehicle: a REAL jeep node dropped beside the (0,0,0) player spawn area
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(1f, 1.2f, 0f);

            yield return Until(() => driver.State == NetSessionState.Connected && observer.State == NetSessionState.Connected, 5);
            T.Check("both clients joined", driver.State == NetSessionState.Connected && observer.State == NetSessionState.Connected);
            yield return Until(() => driver.Vehicles.Count == 1 && observer.Vehicles.Count == 1, 5);
            T.Check("the vehicle node replicated to both clients (VehicleNetSync minted its entity)",
                    driver.Vehicles.Count == 1 && observer.Vehicles.Count == 1);
            T.Check("exactly one vehicle tracked server-side", ded.Server.Vehicles.Count == 1);
            uint netId = 0;
            foreach (var e in driver.Vehicles.All) { netId = e.NetIdValue; break; }

            yield return Ticks(50);   // let the spawn drop settle onto the fallback ground before driving

            int entered = 0;
            driver.VehicleEntered += e => { if (e.PlayerId == driver.PlayerId) entered++; };
            driver.SendEnterVehicle(netId);
            yield return Until(() => ded.Server.Vehicles.TryGet(netId, out var e) && e.DriverPlayerId == driver.PlayerId, 5);
            bool seated = ded.Server.Vehicles.TryGet(netId, out var seatE) && seatE.DriverPlayerId == driver.PlayerId;
            T.Check("the Enter command took the seat server-side (occupancy + reach validated)", seated);
            T.Check("the node's engine started (SP enter side effects through the sync)", jeep.EngineOn);
            yield return Until(() => entered > 0, 5);
            T.Check($"the VehicleEntered fact reached the driver ({entered})", entered > 0);

            // drive forward ~4 s of sim; the observer's puppet must exist and TRACK the server node
            drivingVeh = netId;
            throttle = 1f;
            yield return Until(() => view.TryGetPuppet(netId, out _), 5);
            // VehiclePuppet is a plain Node3D by type -- the compiler itself guarantees no VehicleBody3D here
            bool havePuppet = view.TryGetPuppet(netId, out var pup);
            T.Check("the observer materialized a mesh-only puppet (no VehicleBody3D)", havePuppet);
            var start = jeep.GlobalPosition;
            float maxErr = 0f, maxStep = 0f;
            Vector3 prev = pup.GlobalPosition;
            for (int i = 0; i < 200; i++)
            {
                yield return Ticks(1);
                var now = pup.GlobalPosition;
                maxStep = Mathf.Max(maxStep, now.DistanceTo(prev));
                prev = now;
                if (i > 50) maxErr = Mathf.Max(maxErr, now.DistanceTo(jeep.GlobalPosition));   // measure once the glide latched
            }
            float driven = jeep.GlobalPosition.DistanceTo(start);
            T.Check($"the server NODE physically drove under the remote DriveInput ({driven:0.0} m)", driven > 8f);
            T.Check($"the puppet converged on the driven vehicle (max err {maxErr:0.00} m, dead-reckoned)", maxErr < 2.5f);
            T.Check($"the puppet moved by interpolation, not teleports (max per-tick step {maxStep:0.00} m)", maxStep > 0f && maxStep < 1.5f);

            // brake to a stop -> at rest the puppet parks on the node's exact transform
            throttle = 0f;
            handbrake = true;
            yield return Ticks(150);
            float restErr = pup.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"at rest the puppet sits on the server transform (err {restErr:0.00} m)", restErr < 0.6f);

            // exit frees the seat and parks the node (SP exit side effects through the sync)
            drivingVeh = 0;
            driver.SendExitVehicle();
            yield return Until(() => ded.Server.Vehicles.TryGet(netId, out var e) && e.DriverPlayerId == 0, 5);
            T.Check("Exit freed the seat", ded.Server.Vehicles.TryGet(netId, out var freedE) && freedE.DriverPlayerId == 0);
            yield return Ticks(5);
            T.Check("the node parked (engine off) after exit", !jeep.EngineOn);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            driver.Disconnect();
            observer.Disconnect();
        }
    }
}
