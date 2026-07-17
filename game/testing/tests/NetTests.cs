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
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), AllowCheats = true };   // this test uses console-give to stock client A; cheats are off by default on the real server (review C1)
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

    // PEI_CLIENT_PLAN §3 Phase C2: server players live on REAL collision, not the flat demo integration.
    // A dedicated world (fallback ground) with RemoteAvatars on gets a ramp placed in the walker's path;
    // a remote client joins and (a) holds the MoveInput v2 JUMP bit standing still -- the replicated Y
    // leaves the ground (the buttons byte drives the body), then (b) walks forward up the ramp -- the
    // replicated Y climbs it. Both are impossible under IntegrateFlat, which never changes Y. The ack loop
    // must stay honest (LastProcessedInputSeq flows through ServerDrive), and the run must be DESYNC-QUIET:
    // the hardening Part C detector (EnableSyncCheck, on for every DedicatedServer) watches a full
    // snapshot-applying client across the whole real-collision run and must never fire.
    public class NetServerAvatarTerrain : GameTest
    {
        public override string Name => "net.server_avatar_terrain";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // the ramp: real collision the flat demo integration cannot see. 18 deg (under the shell's 55
            // FloorMaxAngle), 12 m wide so wire-grid drift still lands on it; its near edge is buried below
            // the fallback ground (top face crosses y=0 around z~4.7) so the walker strolls straight on.
            var ramp = new StaticBody3D { CollisionLayer = 1u << 0, Position = new Vector3(0f, 2.5f, 14f), RotationDegrees = new Vector3(-18f, 0f, 0f) };
            ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 1f, 24f) } });
            World.AddChild(ramp);

            var net = new MemNetwork(20260718);
            var walker = new NetWorldClient(new MemClientTransport(net), "climber", contentHash: NetContent.Hash);
            bool desynced = false;
            walker.DesyncDetected += _ => desynced = true;
            bool sendInput = false; float fwd = 0f; byte buttons = 0;
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                // yaw 180: the avatar NODE's forward (-Z at yaw 0) turns to +Z -- into the ramp
                if (sendInput) walker.SendMoveInput(0f, fwd, 180f, buttons);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + player sync + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            T.Check("walker joined (join snapshot applied)", walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1);
            yield return Until(() => ded.PlayerSync.TrackedCount == 1, 5);
            bool haveBody = ded.PlayerSync.TryGetBody(walker.PlayerId, out var body);
            T.Check("a NetAvatar PlayerController body spawned for the remote peer", haveBody && body.NetAvatar);
            T.Check("the avatar registered in PlayerRegistry (zombie/loot streaming compat)", PlayerRegistry.Count == 1);

            // (a) JUMP standing still: the v2 buttons bit -> ScriptedJump -> the body leaves the ground.
            // JUMP=7 under 3x gravity peaks ~0.83 m; IntegrateFlat would hold y at exactly 0 forever.
            sendInput = true; fwd = 0f; buttons = MoveInput.ButtonJump;
            float jumpMaxY = float.MinValue;
            for (int i = 0; i < 100; i++)
            {
                yield return Ticks(1);
                if (walker.Players.TryGetByOwner(walker.PlayerId, out var j)) jumpMaxY = Mathf.Max(jumpMaxY, j.Pos.y);
            }
            T.Check($"the jump bit drove the body off the ground (replicated peak y {jumpMaxY:0.00})", jumpMaxY > 0.4f);

            // land + settle so no ballistic hop can contaminate the ramp measurement
            buttons = 0;
            yield return Ticks(60);
            walker.Players.TryGetByOwner(walker.PlayerId, out var settled);
            T.Check($"back on the ground after the hops (y {settled?.Pos.y:0.00})", settled != null && settled.Pos.y < 0.2f);

            // (b) walk forward up the ramp: replicated Y must RISE above the ramp base -- impossible under
            // IntegrateFlat -- while the input-seq ack keeps flowing through the ServerDrive write-back
            float rampMaxY = float.MinValue, finalZ = 0f; ushort ackSeq = 0;
            fwd = 1f;
            for (int i = 0; i < 250; i++)
            {
                yield return Ticks(1);
                if (walker.Players.TryGetByOwner(walker.PlayerId, out var m))
                {
                    rampMaxY = Mathf.Max(rampMaxY, m.Pos.y);
                    finalZ = m.Pos.z;
                    ackSeq = m.LastProcessedInputSeq;
                }
            }
            T.Check($"replicated Y climbed the ramp (peak y {rampMaxY:0.00} m)", rampMaxY > 1.5f);
            T.Check($"the body physically advanced (z {finalZ:0.0} m)", finalZ > 6f);
            T.Check($"input seqs acked through the ServerDrive write-back (seq {ackSeq})", ackSeq > 0);

            // settle to exact parity (held-keys: explicit stop, then input-QUIET so the last snapshots flush)
            fwd = 0f;
            yield return Ticks(40);
            sendInput = false;
            yield return Ticks(60);
            T.Check("players replica == server (StateHash parity)", walker.Players.StateHash() == ded.Server.Players.StateHash());
            // the C2 promise (PEI_CLIENT_PLAN §3 C2 verify): real-collision server movement produces state
            // clients CONVERGE to -- the Part C runtime detector stayed silent across the whole run
            T.Check("DESYNC-QUIET: zero DesyncDetected across the real-collision avatar run", !desynced);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            walker.Disconnect();
        }
    }

    // PEI_CLIENT_PLAN §3 Phase C1: a joined client assembles its world through the ONE WorldBuilder path
    // in the new Client mode -- and that mode must stay pure scenery+physics: no local PlayerController,
    // no ZombieField, no local-authority spawns. On a missing map the contract is FAIL-FAST (Terr == null,
    // world NOT ready) -- Main.BuildClient shows an error screen instead of silently faking a demo arena;
    // the flat-ground fallback is Dedicated-only. The bogus map path makes this deterministic on any box
    // (no retail map data needed), the NetDedicatedBoot pattern.
    public class NetClientWorldMode : GameTest
    {
        public override string Name => "net.client_world_mode";
        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Client,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            T.Check("client world build completed synchronously (syncLoad)", task.IsCompleted);
            var world = task.Result;
            T.Check("sim spine (SimDriver/SimRoot) present", world.Sim != null);
            T.Check("fail-fast: Terr is null on a missing map", world.Terr == null);
            T.Check("fail-fast: world NOT ready (no flat-ground fallback for a client)", !world.Ready);
            T.Check("no local player in a client world", world.Player == null);
            T.Check("no ZombieField in a client world", world.Zombies == null);
            T.Check("no PlayerController registered (PlayerRegistry empty)", PlayerRegistry.Count == 0);
            yield return Ticks(1);   // let the sandbox tick once so teardown exercises the built nodes
        }
    }

    // PEI_CLIENT_PLAN §3 Phase C3: the locally-controlled PREDICTED player shell. A ClientWorldSession
    // joins a DedicatedServer (RemoteAvatars = the C2 avatar sync) over MemTransport on the fallback
    // world; the first authoritative own-entity sample spawns a REAL first-person PlayerController shell
    // (not a NetAvatar) at the server-adopted spawn. The shell walks under its OWN physics while its
    // input rides the wire, and PredictionReconciler corrections are CONSUMED -- applied TO THE NODE
    // through ApplyNetCorrection (the §7 risk 5 seam that shifts the manual render-interp samples with
    // it). Asserts the C3 bar: (a) the shell MOVES; (b) the replicated own-entity CONVERGES to the shell
    // within the wire grid once settled (loopback-parity style); (c) an injected ~5 m displacement of
    // the SERVER avatar SNAPS the shell (Reconciler.Snaps) and it re-converges on the displaced
    // authority; and DESYNC-QUIET throughout -- the Part C runtime detector (EnableSyncCheck, on for
    // every DedicatedServer) must never fire while corrections are being applied to the node.
    public class NetShellWalkReconcile : GameTest
    {
        public override string Name => "net.shell_walk_reconcile";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(30303);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery BEFORE the session's Client.Tick each tick
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "shellwalker" };
            World.AddChild(sess);      // registers net.client.pump + client.shell steps (before the server's, §2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            // join -> first authoritative own-entity sample -> the shell spawns at the server-adopted spawn
            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            T.Check("the shell is a REAL local player (not a NetAvatar)", !sess.Shell.NetAvatar);
            bool ownSeed = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var seed);
            float seedErr = ownSeed ? (seed.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"shell adopted the server spawn (err {seedErr:0.###} m)", seedErr < 0.5f);

            // (a) walk forward ~3 s under REAL shell physics; input rides the wire to the C2 avatar body
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            var start = sess.Shell.TruePhysicsPosition;
            yield return Ticks(150);
            float walked = start.DistanceTo(sess.Shell.TruePhysicsPosition);
            T.Check($"(a) the shell MOVED under its own physics ({walked:0.0} m)", walked > 4f);
            T.Check("corrections actually flowed (acks applied)", sess.Reconciler.AcksApplied > 0);

            // (b) stop + settle: the reconciler drains and the replicated own-entity lands ON the node
            // (both stationary -> residual is pure wire-grid quantization, far under 5 cm)
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"(b) replicated own-entity CONVERGED to the shell (err {err:0.###} m)", own && err < 0.05f);
            T.Check($"pending error drained (|pending| {sess.Reconciler.PendingError.magnitude:0.####} m)", sess.Reconciler.PendingError.magnitude < 0.01f);
            T.Check("no snap during a plain reconciled walk", sess.Reconciler.Snaps == 0);
            T.Check($"DESYNC-QUIET across the corrected walk ({desyncs} fired)", desyncs == 0);

            // (c) shove the SERVER avatar ~5 m sideways (a divergence only the server saw): past the 2 m
            // SnapThreshold the shell must SNAP onto the displaced authority, not glide. The shove goes
            // through ApplyNetCorrection -- a bare GlobalPosition write is UNDONE next tick by the body's
            // own manual-interp restore (the exact §7 risk 5 failure mode, server-side edition) and would
            // never reach the ServerDrive write-back.
            bool haveBody = ded.PlayerSync.TryGetBody(sess.Client.PlayerId, out var body);
            T.Check("C2 avatar body exists for the shell's peer", haveBody);
            long snapsBefore = sess.Reconciler.Snaps;
            body.ApplyNetCorrection(new Vector3(5f, 0f, 0f));
            yield return Ticks(50);
            T.Check($"(c) the 5 m server-side displacement SNAPPED the shell (snaps {sess.Reconciler.Snaps})", sess.Reconciler.Snaps > snapsBefore);
            bool own2 = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e2);
            float snapErr = own2 ? (e2.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"shell re-converged on the displaced authority (err {snapErr:0.###} m)", own2 && snapErr < 0.05f);
            T.Check($"still DESYNC-QUIET after the snap ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            // (the session's own steps die with the sandbox's SimDriver; _ExitTree disconnects)
            world.Sim.Sim.Remove(pump);
        }
    }

    // PEI_CLIENT_PLAN §3 Phase C4: ZombieField streams on ANY registered player -- and the SP path stays
    // byte-identical. Two NetAvatar PlayerControllers (the C2 server-avatar construction) register at far
    // apart positions; three synthetic pockets (the DebugAddPocket seam -- no map data on CI) sit near A,
    // near B, and far from both. With Player EXPLICITLY set to A (the SP shape), streaming keys on A ALONE:
    // B's pocket must NOT populate even though B is registered -- the SP guard. With Player = null (the
    // dedicated shape), the PlayerRegistry fallback streams per-pocket on the NEAREST player: A's and B's
    // pockets are both live (spawned brains carry Target = null -> the ZombieController registry hunt), the
    // far pocket stays empty, and moving B away despawns B's pocket through the same nearest-any query.
    public class NetZombieFieldAnyPlayer : GameTest
    {
        public override string Name => "net.zombiefield_anyplayer";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var terr = new Terrain();   // inert instance: the field only null-gates on Terr (points carry their own Y)
            World.AddChild(terr);

            // the two registered avatars: A at the origin, B 400 m north (far outside every hysteresis radius)
            var a = new PlayerController { NetAvatar = true, CaptureMouse = false };
            World.AddChild(a);
            a.GlobalPosition = new Vector3(0f, 1.1f, 0f);
            var b = new PlayerController { NetAvatar = true, CaptureMouse = false };
            World.AddChild(b);
            b.GlobalPosition = new Vector3(0f, 1.1f, 400f);
            yield return Ticks(2);
            T.Check("both NetAvatar controllers registered (PlayerRegistry)", PlayerRegistry.Count == 2);

            var field = new ZombieField { Player = a, Terr = terr };   // SP shape first: Player EXPLICITLY set
            World.AddChild(field);
            Vector3[] PtsAround(float cx, float cz) => new[]
            {
                new Vector3(cx - 3f, 0.5f, cz - 3f), new Vector3(cx + 3f, 0.5f, cz - 3f),
                new Vector3(cx - 3f, 0.5f, cz + 3f), new Vector3(cx + 3f, 0.5f, cz + 3f),
            };
            var half = new Vector3(10f, 50f, 10f);
            int pkA = field.DebugAddPocket(new Vector3(0f, 0f, 20f), half, PtsAround(0f, 20f), cap: 2);    // 10 m from A / 370 m from B
            int pkB = field.DebugAddPocket(new Vector3(0f, 0f, 380f), half, PtsAround(0f, 380f), cap: 2);  // 370 m from A / 10 m from B
            int pkFar = field.DebugAddPocket(new Vector3(0f, 0f, 200f), half, PtsAround(0f, 200f), cap: 2); // 170/190 m -- outside ActivateR for both

            // ---- SP guard: Player set -> streaming keys on A ALONE, the registry is never consulted ----
            yield return Ticks(40);   // > the 0.4 s streaming cadence
            T.Check("SP path: exactly ONE anchor (the explicit Player) despite 2 registered players", field.DebugAnchors().Count == 1);
            T.Check($"SP path: A's pocket activated + populated to cap ({field.DebugLiveCount(pkA)})",
                    field.DebugPocketActive(pkA) && field.DebugLiveCount(pkA) == 2);
            T.Check("SP path: B's pocket did NOT activate (registered B is invisible while Player is set)",
                    !field.DebugPocketActive(pkB) && field.DebugLiveCount(pkB) == 0);
            T.Check("far pocket inactive", !field.DebugPocketActive(pkFar));
            bool spTargets = true;
            foreach (var z in field.DebugLive(pkA)) spTargets &= z.Target == a;
            T.Check("SP path: spawned brains carry Target = Player EXACTLY (the old Spawn shape)", spTargets);
            T.Check($"SP path: B's pocket distance is B-blind ({field.DebugPocketDist(pkB):0} m)", field.DebugPocketDist(pkB) > 300f);

            // ---- dedicated shape: Player = null -> nearest-ANY-player per pocket via PlayerRegistry ----
            field.Player = null;
            yield return Ticks(40);
            T.Check("anyplayer: both registered players are anchors", field.DebugAnchors().Count == 2);
            T.Check($"anyplayer: B's pocket distance keys on the NEAREST player ({field.DebugPocketDist(pkB):0} m)", field.DebugPocketDist(pkB) < 90f);
            T.Check($"anyplayer: B's pocket activated + populated to cap ({field.DebugLiveCount(pkB)})",
                    field.DebugPocketActive(pkB) && field.DebugLiveCount(pkB) == 2);
            T.Check("anyplayer: A's pocket stays live (A is still near it)",
                    field.DebugPocketActive(pkA) && field.DebugLiveCount(pkA) == 2);
            T.Check("far pocket STILL inactive (nearest of both anchors is outside ActivateR)", !field.DebugPocketActive(pkFar));
            bool nullTargets = true;
            foreach (var z in field.DebugLive(pkB)) nullTargets &= z.Target == null;
            T.Check("anyplayer: spawned brains carry Target = null (the ZombieController registry-hunt fallback)", nullTargets);

            // ---- hysteresis under anyplayer: B leaves -> B's pocket despawns through the same query ----
            // (ApplyNetCorrection, not a bare GlobalPosition write -- the body's manual-interp restore
            // would undo the latter next tick, the §7 risk 5 seam)
            b.ApplyNetCorrection(new Vector3(0f, 0f, 600f));   // B: z 400 -> 1000, ~610 m from its pocket
            yield return Ticks(40);
            T.Check("anyplayer: B's pocket despawned once its nearest player left (40 m hysteresis passed)",
                    !field.DebugPocketActive(pkB) && field.DebugLiveCount(pkB) == 0);
            T.Check("anyplayer: A's pocket unaffected by B leaving", field.DebugPocketActive(pkA) && field.DebugLiveCount(pkA) == 2);
        }
    }

    // PEI_CLIENT_PLAN §3 Phase C4 verify (review fold): a POPULATED dedicated world is DESYNC-QUIET and its
    // join snapshot FITS. The population is built server-side BEFORE anyone joins (the real dedicated boot
    // order): real Vehicle nodes + real ZombieController brains (published by VehicleNetSync/ZombieNetSync),
    // plus PEI-scale wire entities spawned directly into the replication systems (120 vehicles / 96 zombies /
    // 300 world items -- the §7 risk 3 worst case, everything inside the joiner's relevancy rings). The
    // interaction under test: vehicles are AllRelevant -> IN the EnableSyncCheck set (NetWorldHost.cs
    // EnableSyncCheck lists SystemVehicles), so the client's vehicle replica must hash-converge; zombies are
    // relevancy-filtered -> EXCLUDED from the check by design, and actively-churning brains must not trip it
    // either. Then a FRESH joiner lands against the full population: everything arrives in ONE reliable join
    // snapshot, the composer never budget-drops a block (OversizedBlocksSkipped == 0), and a join-shaped
    // probe compose measures the actual bytes against the reliable budget (plan §7 risk 3 -- report numbers).
    public class NetPopulatedWorldQuiet : GameTest
    {
        public override string Name => "net.populated_world_quiet";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // a walkable navmesh for the brains (the net.zombie_chase_sync rig)
            var nm = new NavigationMesh();
            nm.Vertices = new[] { new Vector3(-60f, 0f, -60f), new Vector3(-60f, 0f, 60f), new Vector3(60f, 0f, 60f), new Vector3(60f, 0f, -60f) };
            nm.AddPolygon(new int[] { 0, 1, 2, 3 });
            World.AddChild(new NavigationRegion3D { NavigationMesh = nm });

            var net = new MemNetwork(20260719);
            var clients = new List<NetWorldClient>();
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); for (int i = 0; i < clients.Count; i++) clients[i].Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + publishes + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            // ---- POPULATE (before any join -- the C4 dedicated boot order) ----
            // real nodes: 6 vehicles across the spec pool + 8 zombie brains (they idle until a player registers)
            string[] specs = { "jeep", "sedan", "police", "ambulance", "humvee", "truck" };
            for (int i = 0; i < specs.Length; i++)
            {
                var v = Vehicle.BuildByName(specs[i], i);
                World.AddChild(v);
                v.GlobalPosition = new Vector3(-25f + i * 9f, 1.2f, -18f);
            }
            for (int i = 0; i < 8; i++)
            {
                float ang = i * 2.39996f;
                var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
                World.AddChild(z);
                z.GlobalPosition = new Vector3(18f * Mathf.Cos(ang), 0.3f, 18f * Mathf.Sin(ang) + 25f);
            }
            yield return Ticks(10);   // one publish round: VehicleNetSync/ZombieNetSync mint the node entities
            T.Check("node population minted (6 vehicles / 8 zombies tracked server-side)",
                    ded.Server.Vehicles.Count == 6 && ded.Server.Zombies.Count == 8);

            // PEI-scale wire entities, directly into the replication systems (wire size needs no nodes):
            // vehicles -> 126 total (PEI spawns ~100), zombies -> 104 total (the 96 GlobalMaxLive cap + the
            // 8 brains), world items -> 300 (a LootField-heavy town). ALL inside the joiner's rings
            // (zombies 192 m / items 128 m; vehicles are AllRelevant regardless).
            long tick = ded.Server.Session.CurrentTick;
            for (int i = 0; i < 120; i++)
                ded.Server.Vehicles.ServerSpawn(ded.Server.Ids.Mint(), (byte)(i % Vehicle.SpecNames.Length), (byte)i,
                                                new UnityEngine.Vector3(-60f + (i % 16) * 8f, 0.6f, -60f + (i / 16) * 8f), tick);
            for (int i = 0; i < 96; i++)
                ded.Server.Zombies.ServerSpawn(ded.Server.Ids.Mint(), 0,
                                               new UnityEngine.Vector3(-40f + (i % 12) * 7f, 0.3f, 40f + (i / 12) * 7f), tick);
            for (int i = 0; i < 300; i++)
                ded.Server.WorldItems.ServerSpawn(ded.Server.Ids.Mint(), new Item((ushort)(1 + i % 50), 1, 100),
                                                  new UnityEngine.Vector3(-50f + (i % 25) * 4f, 0.1f, -50f + (i / 25) * 4f), tick);
            T.Check("PEI-scale wire population stands (126 vehicles / 104 zombies / 300 items)",
                    ded.Server.Vehicles.Count == 126 && ded.Server.Zombies.Count == 104 && ded.Server.WorldItems.Count == 300);

            // ---- a client joins the POPULATED world; walks; the desync detector must stay silent ----
            var a = new NetWorldClient(new MemClientTransport(net), "walker", contentHash: NetContent.Hash);
            int desyncs = 0;
            a.DesyncDetected += _ => desyncs++;
            clients.Add(a);
            a.Connect();
            yield return Until(() => a.State == NetSessionState.Connected && a.JoinSnapshotsApplied >= 1, 5);
            T.Check("walker joined the populated world (reliable join snapshot applied)",
                    a.State == NetSessionState.Connected && a.JoinSnapshotsApplied >= 1);
            T.Check($"the ONE join snapshot carried the WHOLE population (v={a.Vehicles.Count} z={a.Zombies.Count} i={a.WorldItems.Count})",
                    a.Vehicles.Count == 126 && a.Zombies.Count == 104 && a.WorldItems.Count == 300);

            // walk for 10 s of sim among chasing brains + publishing vehicles: >= 10 sync-check rounds
            // (EnableSyncCheck default 50-tick interval, on for every DedicatedServer)
            var walkStep = new DelegateSimStep((t, dt) => a.SendMoveInput(0f, 1f, (float)((t / 100) % 4) * 90f), "l1.walk");
            world.Sim.Sim.Add(walkStep);
            yield return Ticks(500);
            world.Sim.Sim.Remove(walkStep);
            yield return Ticks(100);   // input-quiet settle: vehicles at rest, snapshots flushed
            T.Check($"sync-check blocks actually flowed ({ded.Server.Composer.Diag.SyncCheckBlocksWritten})",
                    ded.Server.Composer.Diag.SyncCheckBlocksWritten > 0);
            T.Check($"DESYNC-QUIET: zero DesyncDetected across the populated-world walk ({desyncs} fired)", desyncs == 0);
            // vehicles are IN the sync-check set (AllRelevant): the replica must hash-converge on the server
            T.Check("vehicles replica == server (StateHash parity -- the checked system converged)",
                    a.Vehicles.StateHash() == ded.Server.Vehicles.StateHash());
            // zombies are relevancy-filtered -> excluded from the check set BY DESIGN; the churning brains
            // replicated fine and never tripped the detector (desyncs == 0 above covers the whole run)
            T.Check($"zombie replicas flowed throughout ({a.Zombies.Count})", a.Zombies.Count == 104);

            // ---- join-snapshot size (plan §7 risk 3): a FRESH joiner against the full population ----
            long skippedBefore = ded.Server.Composer.Diag.OversizedBlocksSkipped;
            T.Check("no block was EVER budget-dropped so far (join + 25 Hz stream)", skippedBefore == 0);
            var late = new NetWorldClient(new MemClientTransport(net), "latecomer", contentHash: NetContent.Hash);
            clients.Add(late);
            late.Connect();
            yield return Until(() => late.State == NetSessionState.Connected && late.JoinSnapshotsApplied >= 1, 5);
            T.Check($"latecomer's ONE reliable join snapshot carried everything (v={late.Vehicles.Count} z={late.Zombies.Count} i={late.WorldItems.Count})",
                    late.JoinSnapshotsApplied >= 1 && late.Vehicles.Count == 126 && late.Zombies.Count == 104 && late.WorldItems.Count == 300);
            T.Check("the latecomer's join dropped NO system block (composer truncation never fired)",
                    ded.Server.Composer.Diag.OversizedBlocksSkipped == skippedBefore);

            // measure the join-shaped FULL compose (baseline 0, the exact reliable budget) for a probe id
            int budget = NetProtocol.MaxReliableMessageBytes / 2;
            var probe = ded.Server.Composer.Compose(ded.Server.Session.CurrentTick, 9999, default, maxBytes: budget);
            GD.Print($"[C4] populated join snapshot: {probe.Length} B of {budget} B reliable budget ({100.0 * probe.Length / budget:0.00}%) -- 126 vehicles / 104 zombies / 300 items / 2 players");
            T.Check($"populated join snapshot MEASURED: {probe.Length} B of {budget} B reliable budget ({100.0 * probe.Length / budget:0.0}%)",
                    probe.Length > 2000 && probe.Length < budget);
            T.Check("the probe compose dropped nothing either",
                    ded.Server.Composer.Diag.OversizedBlocksSkipped == skippedBefore);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            a.Disconnect();
            late.Disconnect();
        }
    }

    // PEI_CLIENT_PLAN §3 Phase C5: the client-session world views on the fallback world. The server spawns
    // a world item (the drop path), fells + respawns a resource index, and configures the clock; the
    // session's views must (a) materialize a STATIC item visual, (b) mirror the alive-bitmap onto the
    // client's ResourceField -- trunk collider included (§7 risk 7), (c) anchor the client DayNightCycle on
    // the tick-derived time (§7 risk 8 glide), and (d) puppet a replicated zombie. Server and client each
    // get their OWN ResourceField/DayNightCycle instance (same committed content -> same §3.7 index space),
    // so a mirrored flip is unambiguously the VIEW's work, not the server sync's. All views are read-only
    // replica consumers, so the desync detector must stay silent throughout -- the accidental-mutation guard.
    public class NetClientWorldViews : GameTest
    {
        public override string Name => "net.client_world_views";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // server-side world state: a headless ResourceField (the dedicated shape) + a distinctive clock
            var serverField = new ResourceField { VisualInstances = false };
            World.AddChild(serverField);
            serverField.LoadResources("NONE");
            T.Check($"committed resource content loaded server-side ({serverField.InstanceCount})", serverField.InstanceCount > 0);
            world.DayNight.Time = 0.80f;   // distinctive (default 0.35) -- the client must ADOPT this off the wire

            // client-side world state the views drive
            var clientField = new ResourceField { VisualInstances = false };
            World.AddChild(clientField);
            clientField.LoadResources("NONE");
            T.Check("client + server fields agree on the index space (§3.7)", clientField.InstanceCount == serverField.InstanceCount);
            var clientDnc = new DayNightCycle();   // default Time 0.35 / DayLength 120 -- BOTH must be adopted off the wire
            World.AddChild(clientDnc);

            var net = new MemNetwork(20260721);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net),
                                                PlayerName = "viewer", DayNight = clientDnc, Resources = clientField };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                                            RemoteAvatars = true, DayNight = world.DayNight, Resources = serverField };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned (its _Ready registered the ItemCatalog the item view needs)", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // (a) the server spawns a world item over the drop path; the view materializes a STATIC visual
            var item = ded.Server.Transactions.SpawnWorldItem(new Item(13, 1, 100),
                new UnityEngine.Vector3(3f, 0.4f, 2f), UnityEngine.Vector3.zero);
            T.Check("server minted the world-item entity", item != null);
            yield return Until(() => sess.Items.NodeCount == 1, 5);
            bool haveNode = sess.Items.TryGetNode(item.NetIdValue, out var itemNode);
            T.Check("(a) the item view materialized a node for the replicated entity", haveNode);
            T.Check("the replica is VISUAL-ONLY (no RigidBody3D, not in the worlditems group)",
                    haveNode && itemNode is not RigidBody3D && !itemNode.IsInGroup("worlditems"));
            // settle it 1.5 m away (the LootField/drop physics path publishes exactly this): the visual follows
            ded.Server.Transactions.SettleWorldItem(item.NetIdValue, new UnityEngine.Vector3(4.5f, 0.2f, 2f));
            var rest = new Vector3(4.5f, 0.2f, 2f);
            yield return Until(() => haveNode && itemNode.GlobalPosition.DistanceTo(rest) < 0.02f, 5);
            T.Check($"the settle event moved the visual to the server's rest transform ({itemNode.GlobalPosition})",
                    itemNode.GlobalPosition.DistanceTo(rest) < 0.02f);

            // (b) fell a TREE index server-side: the client field drops the instance AND its trunk collider
            int treeIdx = -1;
            for (int i = 0; i < clientField.InstanceCount; i++)
                if (clientField.DebugTrunk(i) != null) { treeIdx = i; break; }
            T.Check($"a tree index exists in the committed content (idx {treeIdx})", treeIdx >= 0);
            T.Check("server transaction felled the resource", ded.ResourceSync.SetAlive(treeIdx, false));
            yield return Until(() => !clientField.IsAlive(treeIdx), 5);
            T.Check("(b) the alive-bitmap mirrored onto the CLIENT field (SetAlive(i,false) ran)", !clientField.IsAlive(treeIdx));
            T.Check("the felled tree's client trunk collider is OFF (§7 risk 7)", clientField.DebugTrunk(treeIdx).CollisionLayer == 0);
            ded.ResourceSync.SetAlive(treeIdx, true);
            yield return Until(() => clientField.IsAlive(treeIdx), 5);
            T.Check("respawn restores the client instance + collider",
                    clientField.IsAlive(treeIdx) && clientField.DebugTrunk(treeIdx).CollisionLayer != 0);

            // (c) the clock view anchored the client cycle on the tick-derived time. Tolerance 0.005 of a
            // day (~1.5 s of the 300 s day) -- generous vs the 0.45-of-a-day default gap it had to jump,
            // and well over the re-anchor epsilon + one 2-tick snapshot interval (1.3e-4) of steady drift.
            T.Check($"client adopted the replicated day length ({clientDnc.DayLength})",
                    clientDnc.DayLength == ded.Server.Clock.DayLengthSeconds);
            float derived = sess.Client.Clock.TimeOfDayAt(sess.Client.Applier.LastAppliedServerTick);
            float clockErr = Mathf.Abs(Mathf.PosMod(clientDnc.Time - derived + 0.5f, 1f) - 0.5f);
            T.Check($"(c) DayNightCycle.Time anchored to the tick-derived value (err {clockErr:0.####} of a day)", clockErr < 0.005f);
            T.Check($"...and that value is the SERVER's 0.80 clock, not the 0.35 default ({derived:0.###})",
                    Mathf.Abs(Mathf.PosMod(derived - 0.80f + 0.5f, 1f) - 0.5f) < 0.02f);

            // (d) a replicated zombie gets an interpolated puppet (the ZombiePuppets attach)
            ded.Server.Zombies.ServerSpawn(ded.Server.Ids.Mint(), 0,
                new UnityEngine.Vector3(6f, 0.3f, 6f), ded.Server.Session.CurrentTick);
            yield return Until(() => sess.Puppets.PuppetCount == 1, 5);
            bool havePuppet = sess.Puppets.PuppetCount == 1;
            T.Check("(d) the replicated zombie got a puppet in the session's world", havePuppet);

            // remove the item -> the visual retires
            ded.Server.Transactions.RemoveWorldItem(item.NetIdValue);
            yield return Until(() => sess.Items.NodeCount == 0, 5);
            T.Check("the removal retired the item visual", sess.Items.NodeCount == 0);

            // the views are READ-ONLY replica consumers: >= 2 more sync-check rounds, still desync-quiet
            yield return Ticks(120);
            T.Check($"sync-check blocks flowed ({ded.Server.Composer.Diag.SyncCheckBlocksWritten})",
                    ded.Server.Composer.Diag.SyncCheckBlocksWritten > 0);
            T.Check($"DESYNC-QUIET with every C5 view attached ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }
}
