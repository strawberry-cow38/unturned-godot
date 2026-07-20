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
            // the DRIVER renders through the same puppet path (§3.6 v1: no driver-side prediction) -- its
            // own view must track too, not just the observer's (the live driver-freeze hid in this gap)
            var driverView = new VehicleReplicaView { Client = driver };
            World.AddChild(driverView);
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
            yield return Until(() => view.TryGetPuppet(netId, out _) && driverView.TryGetPuppet(netId, out _), 5);
            // VehiclePuppet is a plain Node3D by type -- the compiler itself guarantees no VehicleBody3D here
            bool havePuppet = view.TryGetPuppet(netId, out var pup);
            T.Check("the observer materialized a mesh-only puppet (no VehicleBody3D)", havePuppet);
            bool haveDriverPuppet = driverView.TryGetPuppet(netId, out var dpup);
            T.Check("the DRIVER materialized its own puppet too", haveDriverPuppet);
            var start = jeep.GlobalPosition;
            float maxErr = 0f, maxStep = 0f, dMaxErr = 0f;
            Vector3 prev = pup.GlobalPosition;
            for (int i = 0; i < 200; i++)
            {
                yield return Ticks(1);
                var now = pup.GlobalPosition;
                maxStep = Mathf.Max(maxStep, now.DistanceTo(prev));
                prev = now;
                if (i > 50)
                {
                    maxErr = Mathf.Max(maxErr, now.DistanceTo(jeep.GlobalPosition));   // measure once the glide latched
                    dMaxErr = Mathf.Max(dMaxErr, dpup.GlobalPosition.DistanceTo(jeep.GlobalPosition));
                }
            }
            float driven = jeep.GlobalPosition.DistanceTo(start);
            T.Check($"the server NODE physically drove under the remote DriveInput ({driven:0.0} m)", driven > 8f);
            T.Check($"the puppet converged on the driven vehicle (max err {maxErr:0.00} m, dead-reckoned)", maxErr < 2.5f);
            T.Check($"the DRIVER's own puppet tracked its driven vehicle (max err {dMaxErr:0.00} m)", dMaxErr < 2.5f);
            T.Check($"the puppet moved by interpolation, not teleports (max per-tick step {maxStep:0.00} m)", maxStep > 0f && maxStep < 1.5f);

            // brake to a stop -> at rest the puppets park on the node's exact transform
            throttle = 0f;
            handbrake = true;
            yield return Ticks(150);
            float restErr = pup.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"at rest the puppet sits on the server transform (err {restErr:0.00} m)", restErr < 0.6f);
            float dRestErr = dpup.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"at rest the DRIVER's puppet sits on the server transform (err {dRestErr:0.00} m)", dRestErr < 0.6f);

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

    // Regression for the live "driven vehicle frozen on the DRIVER's own client" bug
    // (docs/DRIVE_DRIVER_VIEW_ROOTCAUSE.md): with a PEI-scale vehicle population the full vehicles block
    // (~35 B per vehicle) exceeds the 1187-byte unreliable snapshot budget, so one >=64-tick gap in a
    // client's acks (a render hitch, a WAN stall) used to latch that client into a PERMANENT full-snapshot
    // wedge in which the vehicles block is budget-skipped on every compose -- its replica frozen forever
    // while every other client tracks fine. The fix routes the starvation-recovery full over the RELIABLE
    // channel with the join budget (once per wedge, unreliable stream held until acked). This test drives
    // the real end-to-end path: real jeep node + DriveInput, a 41-vehicle world over budget, an 80-tick
    // driver-client stall mid-drive, and asserts the driver's replica AND puppet recover afterwards.
    public class NetVehicleDriveSyncAckGap : GameTest
    {
        public override string Name => "net.vehicle_drive_sync_ackgap";
        public override int Tier => 2;
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(74748);
            var driver = new NetWorldClient(new MemClientTransport(net), "driver", contentHash: NetContent.Hash);
            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            uint drivingVeh = 0;
            float throttle = 0f;
            bool driverStalled = false;   // the hitch: no receive, no apply, no acks -- the client's frame stall
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick();
                if (!driverStalled)
                {
                    driver.Tick();
                    if (drivingVeh != 0) driver.SendDriveInput(drivingVeh, throttle, 0f, false);
                }
                observer.Tick();
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + vehicle sync + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);

            // pad the world past the unreliable budget BEFORE anyone joins: 40 parked phantom vehicle
            // entities (no nodes -- VehicleNetSync only reconciles entities it minted from nodes, so they
            // stand). 41 vehicles x ~35 B ≈ 1.4 KB > MaxUnreliablePayload (1187): a full vehicles block
            // can never ride an unreliable snapshot -- the wedge's PEI-scale precondition, and they all
            // ride each client's reliable join snapshot like the real server boot.
            for (int i = 0; i < 40; i++)
                ded.Server.Vehicles.ServerSpawn(ded.Server.Ids.Mint(), typeId: 0, variant: 0,
                    new UnityEngine.Vector3(60f + (i % 8) * 8f, 0.5f, -60f - (i / 8) * 8f),
                    ded.Server.Session.CurrentTick);

            // the DRIVER's own render path -- the exact view the live freeze hid in
            var driverView = new VehicleReplicaView { Client = driver };
            World.AddChild(driverView);
            driver.Connect();
            observer.Connect();

            // the driven vehicle: a REAL jeep node beside the (0,0,0) spawn area
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(1f, 1.2f, 0f);

            yield return Until(() => driver.State == NetSessionState.Connected && observer.State == NetSessionState.Connected, 5);
            T.Check("both clients joined", driver.State == NetSessionState.Connected && observer.State == NetSessionState.Connected);
            yield return Until(() => driver.Vehicles.Count == 41 && observer.Vehicles.Count == 41, 5);
            T.Check($"all 41 vehicles replicated (40 phantoms + the jeep; driver has {driver.Vehicles.Count})",
                    driver.Vehicles.Count == 41 && observer.Vehicles.Count == 41);
            // the jeep's entity is the one near the origin; the phantom pad is parked out at x >= 60
            uint netId = 0;
            foreach (var e in driver.Vehicles.All)
                if (e.Pos.magnitude < 10f) { netId = e.NetIdValue; break; }
            T.Check("found the jeep's replicated entity by its spawn position", netId != 0);

            yield return Ticks(50);   // let the spawn drop settle onto the fallback ground before driving

            driver.SendEnterVehicle(netId);
            yield return Until(() => ded.Server.Vehicles.TryGet(netId, out var e) && e.DriverPlayerId == driver.PlayerId, 5);
            T.Check("the Enter command took the seat server-side",
                    ded.Server.Vehicles.TryGet(netId, out var seatE) && seatE.DriverPlayerId == driver.PlayerId);

            // drive; the driver's puppet must be tracking BEFORE the stall so the recovery assert is honest
            drivingVeh = netId;
            throttle = 1f;
            yield return Until(() => driverView.TryGetPuppet(netId, out _), 5);
            T.Check("the driver materialized its own puppet", driverView.TryGetPuppet(netId, out var dpup));
            yield return Ticks(100);
            float preErr = dpup.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"pre-stall: the driver's puppet tracks its own drive (err {preErr:0.00} m)", preErr < 2.5f);

            // the hitch: 80 ticks (1.6 s) past the 64-tick dirty ring, mid-drive. The held server-side
            // DriveInput keeps the node driving the whole time (held-input model).
            driverStalled = true;
            yield return Ticks(80);
            driverStalled = false;

            // acks resume -> the driver's replica and puppet must RECOVER and track again
            yield return Ticks(150);

            ded.Server.Vehicles.TryGet(netId, out var srvE);
            float obsErr = observer.Vehicles.TryGet(netId, out var oRep) ? (oRep.Pos - srvE.Pos).magnitude : float.MaxValue;
            float repErr = driver.Vehicles.TryGet(netId, out var dRep) ? (dRep.Pos - srvE.Pos).magnitude : float.MaxValue;
            float pupErr = dpup.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            var diag = ded.Server.Composer.Diag;
            T.Check($"control: the never-stalled observer's replica tracked throughout (err {obsErr:0.00} m)", obsErr < 2.5f);
            T.Check($"after the ack gap the DRIVER's replica recovered (err {repErr:0.00} m; server x {srvE.Pos.x:0.0} vs replica x {(dRep != null ? dRep.Pos.x : float.NaN):0.0}; composer fulls {diag.FullSnapshotsComposed} deltas {diag.DeltaSnapshotsComposed} skips {diag.OversizedBlocksSkipped})",
                    repErr < 2.5f);
            T.Check($"after the ack gap the DRIVER's own puppet recovered (err {pupErr:0.00} m)", pupErr < 2.5f);

            // stop + teardown
            drivingVeh = 0;
            driver.SendExitVehicle();
            yield return Ticks(5);
            world.Sim.Sim.Remove(pump);
            driver.Disconnect();
            observer.Disconnect();
        }
    }

    // Regression for the MP "driver exits at his ENTRY position" bug (docs/EXIT_POSITION_ROOTCAUSE.md):
    // during a 7ce2305 recovery hold the server sends this client ZERO unreliable snapshots, so every
    // replica freezes at the hold-start state while unbounded dead-reckoning keeps the car gliding
    // convincingly on screen; the reliable VehicleExited fact still flows, and the exit spot used to be
    // computed CLIENT-SIDE from the FROZEN vehicle replica -- placing the driver back near where he got
    // in, tens of meters from the real (server) car. The fix carries the server-computed spot IN the
    // event. This test reproduces the exact live profile the ackgap test misses (it only exits AFTER
    // recovery): a ClientWorldSession driver on the over-budget 41-vehicle world, an inbound-only
    // blackout (the client keeps ticking + sending -- the WAN-loss shape, so DriveInput streams and the
    // real car keeps driving) long enough to latch the recovery hold, an exit requested MID-HOLD, and
    // the assertion that when the fact finally applies the shell re-appears beside the SERVER car.
    public class NetVehicleExitDuringAckGap : GameTest
    {
        public override string Name => "net.vehicle_exit_during_ackgap";
        public override int Tier => 2;
        public override double TimeoutSimSeconds => 40;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260731);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery before the session's Client.Tick each tick
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "exitdriver" };
            World.AddChild(sess);      // registers net.client.pump + client.shell (before the server's steps, §2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            // the PEI-scale precondition (the ackgap rig): 40 parked phantom entities push the full
            // vehicles block past the unreliable budget, so the starvation-recovery full MUST ride the
            // reliable channel (7ce2305) -- and its hold blacks out the whole snapshot stream
            for (int i = 0; i < 40; i++)
                ded.Server.Vehicles.ServerSpawn(ded.Server.Ids.Mint(), typeId: 0, variant: 0,
                    new UnityEngine.Vector3(60f + (i % 8) * 8f, 0.5f, -60f - (i / 8) * 8f),
                    ded.Server.Session.CurrentTick);

            // the driven vehicle: a REAL jeep ~4 m in front of the spawn -- inside EnterReach (6 m),
            // clear of the shell's body
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, -4f);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned (joined)", sess.Shell != null);
            if (sess.Shell == null) yield break;
            yield return Until(() => sess.Client.Vehicles.Count == 41, 5);
            T.Check($"all 41 vehicles replicated (40 phantoms + the jeep; client has {sess.Client.Vehicles.Count})",
                    sess.Client.Vehicles.Count == 41);
            uint netId = 0;
            foreach (var e in sess.Client.Vehicles.All)
                if (e.Pos.magnitude < 10f) { netId = e.NetIdValue; break; }
            T.Check("found the jeep's replicated entity by its spawn position", netId != 0);
            yield return Ticks(50);   // let the spawn drop settle onto the fallback ground

            // enter (raw request; the seat fact builds the Part A local vehicle through ShellStep) + drive
            sess.Client.SendEnterVehicle(netId);
            yield return Until(() => sess.Shell.IsDriving, 5);
            T.Check("seat handoff: driving-local, server DriverPlayerId == the peer", sess.Shell.IsDriving &&
                    ded.Server.Vehicles.TryGet(netId, out var seatE) && seatE.DriverPlayerId == sess.Client.PlayerId);
            sess.Shell.ScriptedDrive = new Vector2(0f, 1f);   // (steer, throttle): straight ahead, full throttle
            yield return Ticks(100);

            // INBOUND-ONLY blackout, the live WAN profile: the driver keeps ticking + sending (Part A:
            // the local car keeps driving and the VehicleState stream flows, so the server keeps ADOPTING
            // -- its node follows the blind driver) but receives NOTHING for 200 ticks -- past the
            // 64-tick ack-gap latch, under the 250-tick session timeout. The recovery full + hold
            // latch mid-window; everything reliable queues behind the wall.
            bool haveRep = sess.Client.Vehicles.TryGet(netId, out var repAtBlackout);
            var frozenAt = haveRep ? repAtBlackout.Pos : default;
            long fullsBefore = ded.Server.Composer.Diag.FullSnapshotsComposed;
            net.ServerToClient.HoldUntilTick = net.CurrentTick + 200;

            yield return Ticks(160);
            bool stillFrozen = sess.Client.Vehicles.TryGet(netId, out var repMid) && (repMid.Pos - frozenAt).magnitude < 0.01f;
            T.Check("mid-blackout: the driver's replica froze at the blackout-start state", haveRep && stillFrozen);
            float diverged = jeep.GlobalPosition.DistanceTo(new Vector3(frozenAt.x, frozenAt.y, frozenAt.z));
            T.Check($"...while the SERVER car kept moving under the adopted state stream ({diverged:0.0} m past the frozen state)", diverged > 15f);
            T.Check($"the recovery full latched during the blackout (fulls +{ded.Server.Composer.Diag.FullSnapshotsComposed - fullsBefore})",
                    ded.Server.Composer.Diag.FullSnapshotsComposed > fullsBefore);

            // capture the exit THE INSTANT the fact applies (ClientWorldSession's own handler runs first
            // -- subscription order -- so ExitPuppet has already placed the shell): where the shell
            // landed, where the frozen replica was, and where the server's authority actually put him
            bool exitSpotAdjusted = false;
            ded.Server.VehicleHost.AdjustExitSpot = p => { exitSpotAdjusted = true; return p; };   // §7 risk 6 seam probe (fallback world has no Terr)
            bool captured = false;
            Vector3 exitShellPos = default, serverCarPos = default;
            UnityEngine.Vector3 frozenRepPos = default, evtPos = default;
            sess.Client.VehicleExited += evt =>
            {
                if (captured || evt.PlayerId != sess.Client.PlayerId) return;
                captured = true;
                evtPos = evt.Pos;
                exitShellPos = sess.Shell.GlobalPosition;
                if (sess.Client.Vehicles.TryGet(evt.NetId, out var vv)) frozenRepPos = vv.Pos;
                serverCarPos = jeep.GlobalPosition;
            };

            // EXIT while the hold is live: the reliable request flows out fine; the server frees the
            // seat and teleports the entity beside the REAL car; the exited fact queues behind the wall
            sess.Shell.ScriptedDrive = new Vector2(0f, 0f);
            T.Check("the exit seam fired mid-blackout", sess.Shell.RequestExitPuppet());
            yield return Ticks(25);
            T.Check("server freed the seat while the client is still blind",
                    ded.Server.Vehicles.TryGet(netId, out var freedE) && freedE.DriverPlayerId == 0);
            T.Check("the exited fact is HELD -- the client still thinks it's driving", !captured && sess.Shell.IsDriving);
            T.Check("the exit teleport routed through AdjustExitSpot (§7 risk 6 seam)", exitSpotAdjusted);

            // blackout lifts -> the queued reliable burst delivers (recovery full first, then the exited
            // fact, ReliableOrdered) and the shell re-appears. THE regression: beside the SERVER CAR
            // (the real vehicle node), not beside the frozen replica ≈ where he entered. The reference is
            // deliberately the NODE, not the player entity: the entity is re-written from the RemoteAvatars
            // body post-exit, and the seated body's own physics history is not what this bug is about.
            yield return Until(() => captured, 5);
            T.Check("the exited fact applied after the blackout lifted", captured && !sess.Shell.IsDriving);
            float exitToCar = exitShellPos.DistanceTo(serverCarPos);
            float exitToFrozen = (ToU(exitShellPos) - frozenRepPos).magnitude;
            float frozenToCar = serverCarPos.DistanceTo(new Vector3(frozenRepPos.x, frozenRepPos.y, frozenRepPos.z));
            T.Check($"the frozen replica really is far from the server car at exit time ({frozenToCar:0.0} m) -- the assert below discriminates",
                    frozenToCar > 10f);
            T.Check($"EXIT LANDS BESIDE THE SERVER CAR, not the frozen replica (to car {exitToCar:0.00} m, to frozen replica {exitToFrozen:0.0} m, evt spot ({evtPos.x:0.0},{evtPos.y:0.0},{evtPos.z:0.0}))",
                    exitToCar < 3.5f);

            // stream recovery intact: the resumed walk/reconcile loop re-converges the shell onto its
            // server entity. (Deliberately NOT asserting WHERE they converge: the entity is written back
            // from the RemoteAvatars body, whose seated-physics history is its own story -- this test
            // owns the exit PLACEMENT, the convergence check owns post-hold stream health.)
            yield return Ticks(150);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e2);
            float healErr = own ? (e2.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"post-recovery the shell converged on its server entity (err {healErr:0.###} m)", own && healErr < 0.05f);

            world.Sim.Sim.Remove(pump);
        }
    }

    // The v9 FOLLOWER-BODY contract (replacing the C2 avatar-terrain test -- the server no longer
    // simulates any owner's movement, so "avatar climbs real collision" is extinct by design). A raw
    // NetWorldClient streams envelope-legal PlayerStateCommand claims -- walk, a real jump arc, a ramp
    // climb -- and the test asserts the whole server pipeline: the entity ADOPTS each claim bit-exact
    // (position + seq stamped through ServerDrive), the PlayerNetSync FOLLOWER body teleport-tracks the
    // entity (zombies/ballistics see a real body at the claimed spot), the CLAIMED stance dresses the
    // body's hitbox (a crouched claim = a crouched capsule), PlayerRegistry carries the body (zombie/
    // loot streaming compat), and the run is DESYNC-QUIET with exact replica StateHash parity -- the
    // observer-bit-exact half of the client-auth contract.
    public class NetClientAuthBodyFollows : GameTest
    {
        public override string Name => "net.clientauth_body_follows";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260718);
            var walker = new NetWorldClient(new MemClientTransport(net), "climber", contentHash: NetContent.Hash);
            bool desynced = false;
            walker.DesyncDetected += _ => desynced = true;
            var recovs = new System.Collections.Generic.List<PlayerRecovEvent>();
            walker.PlayerRecov += e => recovs.Add(e);
            bool send = false;
            UnityEngine.Vector3 claim = default, claimVel = default;
            byte claimButtons = 0;
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 180f, 0f, claimVel, claimButtons, grounded: true, recovAck: 0);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);   // registered BEFORE DedicatedServer -> server sim + player sync + replicate stay after/LAST (§2.5)
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            T.Check("walker joined (join snapshot applied)", walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;

            // (a) the claim stream starts -> adoption -> the follower body spawns and tracks
            send = true;
            claimButtons = MoveInput.PackStance(EPlayerStance.SPRINT);
            for (int i = 0; i < 50; i++) { claim.x += 7f * 0.02f; yield return Ticks(1); }
            yield return Ticks(4);   // the stream keeps repeating the final claim -- let it land + adopt
            yield return Until(() => ded.PlayerSync.TrackedCount == 1, 5);
            bool haveBody = ded.PlayerSync.TryGetBody(walker.PlayerId, out var body);
            T.Check("a follower PlayerController body spawned for the remote peer", haveBody && body.NetAvatar && body.NetHold);
            T.Check("the body registered in PlayerRegistry (zombie/loot streaming compat)", PlayerRegistry.Count == 1);
            T.Check("the entity is client-driven (adopted, not integrated)", ded.Server.PlayerHost.IsClientDriven(walker.PlayerId));
            bool sHave = ded.Server.Players.TryGetByOwner(walker.PlayerId, out var se);
            T.Check($"the entity IS the quantized claim (bit-exact adopt)", sHave && se.Pos == PlayerReplication.Quantize(claim));
            float bodyGap = haveBody ? body.GlobalPosition.DistanceTo(new Vector3(se.Pos.x, se.Pos.y, se.Pos.z)) : float.MaxValue;
            T.Check($"the follower body sits ON the entity ({bodyGap:0.###} m)", bodyGap < 0.01f);
            T.Check($"the body dressed the CLAIMED stance (SPRINT hitbox)", haveBody && body.Stance == EPlayerStance.SPRINT);

            // (b) a REAL jump arc claimed tick-by-tick (JUMP=7 under 3x gravity, peak ~0.83 m): the
            // vertical envelope admits it and the replicated Y flies the arc
            float vy = SDG.Unturned.PlayerMovementDef.JUMP;
            float y0 = claim.y;
            float peakY = float.MinValue;
            for (int i = 0; i < 25 && (vy > 0f || claim.y > y0); i++)
            {
                claim.y = Mathf.Max(y0, claim.y + vy * 0.02f);
                vy -= SDG.Unturned.PlayerMovementDef.GRAVITY * 0.02f;
                claimVel = new UnityEngine.Vector3(0f, vy, 0f);
                yield return Ticks(1);
                if (walker.Players.TryGetByOwner(walker.PlayerId, out var j)) peakY = Mathf.Max(peakY, j.Pos.y);
            }
            claim.y = y0; claimVel = default;
            yield return Ticks(5);
            T.Check($"the claimed jump arc replicated back (peak y {peakY:0.00})", peakY > y0 + 0.4f);
            T.Check($"no recov on a legal jump arc ({recovs.Count})", recovs.Count == 0);

            // (c) a ramp-climb claim track (walk-speed forward + 18-deg rise): the envelope admits the
            // slope and the follower body tracks the climb -- real collision is the CLIENT's job now
            claimButtons = MoveInput.PackStance(EPlayerStance.CROUCH);
            for (int i = 0; i < 100; i++)
            {
                claim.x += 4.5f * 0.02f;
                claim.y += 4.5f * 0.325f * 0.02f;   // tan(18 deg) x walk speed
                yield return Ticks(1);
            }
            yield return Ticks(4);   // let the final climb claim land + adopt before sampling
            ded.Server.Players.TryGetByOwner(walker.PlayerId, out var climbed);
            T.Check($"the climb claims adopted (entity y {climbed.Pos.y:0.00} m)", climbed.Pos.y > y0 + 2.5f);
            T.Check($"no recov on the slope climb ({recovs.Count})", recovs.Count == 0);
            T.Check("the body dressed the CROUCH claim (crouched hitbox on the way up)", haveBody && body.Stance == EPlayerStance.CROUCH);
            float climbGap = haveBody ? body.GlobalPosition.DistanceTo(new Vector3(climbed.Pos.x, climbed.Pos.y, climbed.Pos.z)) : float.MaxValue;
            T.Check($"the follower body tracked the climb ({climbGap:0.###} m)", climbGap < 0.01f);

            // settle to exact parity (stop the stream; the last adopted claim IS the entity)
            send = false;
            yield return Ticks(60);
            T.Check("players replica == server (StateHash parity)", walker.Players.StateHash() == ded.Server.Players.StateHash());
            T.Check("DESYNC-QUIET: zero DesyncDetected across the whole adopted run", !desynced);

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

    // The basic client-auth shell walk (v9, replacing the C3 predict/reconcile walk test): a
    // ClientWorldSession joins a DedicatedServer over MemTransport; the first authoritative own-entity
    // sample spawns a REAL first-person shell at the server-adopted spawn. The shell walks under its
    // OWN physics -- the ONLY body its movement has -- and streams its transform; the server envelope-
    // validates + adopts (ServerPlayerAuthority) and the follower body mirrors it. Asserts: (a) the
    // shell MOVES and the published entity tracks it with ZERO recovs (client-auth: no owner correction
    // exists in normal play -- the reconcile-era ack/dead-zone/snap asserts are deleted WITH the
    // machinery); (b) stop -> the entity lands ON the shell bit-exact; (c) a SERVER-side displacement
    // (ServerTeleport -- the respawn/console primitive) reaches the shell via the recov path: the
    // envelope rejects the now-stale claims and the rollback payload IS the teleport target. DESYNC-
    // QUIET throughout.
    public class NetShellWalkClientAuth : GameTest
    {
        public override string Name => "net.shell_walk_clientauth";
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

            // (a) walk forward ~3 s under REAL shell physics; the transform stream drives the entity
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            var start = sess.Shell.TruePhysicsPosition;
            float maxLag = 0f;
            for (int i = 0; i < 150; i++)
            {
                yield return Ticks(1);
                if (sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var lagE))
                    maxLag = Mathf.Max(maxLag, (lagE.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude);
            }
            float walked = start.DistanceTo(sess.Shell.TruePhysicsPosition);
            T.Check($"(a) the shell MOVED under its own physics ({walked:0.0} m)", walked > 4f);
            T.Check($"(a) the server ADOPTED the stream (entity client-driven)", ded.Server.PlayerHost.IsClientDriven(sess.Client.PlayerId));
            T.Check($"(a) the entity tracked the walk (max lag {maxLag:0.###} m)", maxLag < 1f);
            T.Check($"(a) ZERO recovs walking on a clean link ({sess.RecovsApplied})", sess.RecovsApplied == 0);

            // (b) stop + settle: the published entity lands ON the shell -- it IS the shell's own claim,
            // quantized, so the residual is EXACT zero on the wire grid
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(50);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"(b) published entity == the shell's own quantized claim (err {err:0.####} m)", own && err < 0.001f);
            T.Check($"DESYNC-QUIET across the walk ({desyncs} fired)", desyncs == 0);

            // (c) a SERVER-side displacement (the respawn/console primitive): teleport the ENTITY 5 m --
            // the shell's next claims are stale, the envelope rejects them, and the recov payload carries
            // the teleport target: the rubber-band IS the delivery mechanism now
            var target = e.Pos + new UnityEngine.Vector3(5f, 0f, 0f);
            ded.Server.Players.ServerTeleport(sess.Client.PlayerId, target, ded.Server.Session.CurrentTick);
            yield return Until(() => sess.RecovsApplied >= 1, 5);
            T.Check($"(c) the server displacement reached the shell via recov ({sess.RecovsApplied})", sess.RecovsApplied >= 1);
            yield return Ticks(30);
            float tpErr = (ToU(sess.Shell.TruePhysicsPosition) - target).magnitude;
            T.Check($"(c) the shell LANDED on the server's target (err {tpErr:0.###} m)", tpErr < 0.5f);
            T.Check($"still DESYNC-QUIET after the displacement ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // Sprint under client authority (v9; historically the mp-inchworm regression -- a sprinting shell
    // vs a stand-walking server avatar, yanked back every second. That bug class CANNOT recur: there is
    // no server integration of the owner at any stance). Asserts: sprint ground is KEPT (> 15 m in 3 s
    // -- walk-speed nets <= 13.5), zero recovs on clean AND jittery links (a 7 m/s sprint sits inside
    // the envelope's 7 x 1.25 headroom by construction), the FOLLOWER body dresses the claimed stance
    // (SPRINT while sprinting, STAND after -- the hitbox/stealth surface), and stop -> bit-exact
    // convergence, desync-quiet.
    public class NetShellSprintClientAuth : GameTest
    {
        public override string Name => "net.shell_sprint_clientauth";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(31313);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "sprinter" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // sprint forward ~3 s at ZERO latency
            sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            var start = sess.Shell.TruePhysicsPosition;
            yield return Ticks(150);
            float sprinted = start.DistanceTo(sess.Shell.TruePhysicsPosition);
            GD.Print($"[sprint-clientauth] clean link: sprinted={sprinted:0.0} m, recovs={sess.RecovsApplied}");
            T.Check($"sprint ground KEPT ({sprinted:0.0} m in 3 s -- walk-speed would be <= 13.5)", sprinted > 15f);
            bool haveBody = ded.PlayerSync.TryGetBody(sess.Client.PlayerId, out var body);
            T.Check("follower body dressed with the CLAIMED stance (SPRINT rode the state stream)",
                    haveBody && body.Stance == EPlayerStance.SPRINT);
            T.Check($"ZERO recovs during a zero-latency sprint ({sess.RecovsApplied})", sess.RecovsApplied == 0);

            // keep sprinting over a mild adverse link -- the envelope headroom must hold under real-ish
            // jitter, not just the zero-latency ideal
            net.ClientToServer.LatencyTicks = 3; net.ClientToServer.ReorderJitterTicks = 2; net.ClientToServer.LossProbability = 0.02;
            net.ServerToClient.LatencyTicks = 3; net.ServerToClient.ReorderJitterTicks = 2; net.ServerToClient.LossProbability = 0.02;
            long recovsBefore = sess.RecovsApplied;
            yield return Ticks(150);
            T.Check($"still ZERO recovs sprinting over the jittery link ({sess.RecovsApplied - recovsBefore} new)", sess.RecovsApplied == recovsBefore);

            // stop on a clean link -> the entity lands ON the shell (bit-exact: it IS the shell's claim),
            // and the follower body drops back to STAND with the stream
            net.ClientToServer.LatencyTicks = 0; net.ClientToServer.ReorderJitterTicks = 0; net.ClientToServer.LossProbability = 0;
            net.ServerToClient.LatencyTicks = 0; net.ServerToClient.ReorderJitterTicks = 0; net.ServerToClient.LossProbability = 0;
            sess.Shell.ScriptedStance = null;
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the sprint (err {err:0.####} m)", own && err < 0.001f);
            T.Check("follower body back to STAND after the sprint ended", haveBody && body.Stance == EPlayerStance.STAND);
            T.Check($"DESYNC-QUIET across the whole sprint run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // Sprint-stop cycles over a jittery/lossy uplink (v9; historically the mp-inputbuffer sprint-stop
    // yank -- the server's integration count drifting from the client's and resolving as one hard
    // correction at every stop. Structurally extinct: the server integrates NOTHING for the owner).
    // The client-auth assert: 5 sprint/hard-stop cycles over the faulty link produce ZERO recovs -- the
    // latest-wins claim stream self-heals over loss/reorder -- and the stop converges bit-exact.
    public class NetShellSprintStopJitter : GameTest
    {
        public override string Name => "net.shell_sprint_stop_jitter";
        public override double TimeoutSimSeconds => 60;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(41414);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "stopper" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // the repro link: latency + reorder-jitter + loss on the CLAIM channel
            net.ClientToServer.LatencyTicks = 3;
            net.ClientToServer.ReorderJitterTicks = 2;
            net.ClientToServer.LossProbability = 0.03;

            for (int cycle = 0; cycle < 5; cycle++)
            {
                sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                var legStart = sess.Shell.TruePhysicsPosition;
                yield return Ticks(100);
                T.Check($"cycle {cycle}: sprint leg covered ground ({legStart.DistanceTo(sess.Shell.TruePhysicsPosition):0.0} m)",
                        legStart.DistanceTo(sess.Shell.TruePhysicsPosition) > 10f);
                // STOP SUDDENLY -- the old model's yank moment; nothing can yank a client-auth owner
                sess.Shell.ScriptedStance = null;
                sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                yield return Ticks(60);
            }
            T.Check($"ZERO recovs across all sprint-stop cycles ({sess.RecovsApplied})", sess.RecovsApplied == 0);

            // settle on a clean link -> the published entity IS the shell's claim, bit-exact
            net.ClientToServer.LatencyTicks = 0; net.ClientToServer.ReorderJitterTicks = 0; net.ClientToServer.LossProbability = 0;
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the stop cycles (err {err:0.####} m)", own && err < 0.001f);
            T.Check($"DESYNC-QUIET across the whole run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // The network hitch (v9; historically the stall-burst count-invariant test). The envelope's
    // elapsed-tick ceiling is 1 s (ServerPlayerAuthority.EnvelopeMaxTicks = 50): a REAL stall shorter
    // than that -- here ~0.7 s at FULL SPRINT, far beyond any jitter buffer the old model had -- must
    // resume WITHOUT a rubber-band: the first claim through spans the whole gap and the elapsed-scaled
    // cap absorbs it. This is the false-trip non-regression at its worst legit case. Also asserts the
    // never-speculate half: while the claims are dark the ENTITY freezes (the server never ghost-runs
    // an owner on stale intent -- observers see the walker pause, not sprint into a wall).
    public class NetShellStallHitch : GameTest
    {
        public override string Name => "net.shell_stall_hitch";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(52525);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "staller" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // sprint to steady state
            sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(100);

            // THE HITCH: ~0.7 s where no claim crosses client->server, sprinting throughout (~4.9 m of
            // silent motion -- inside the 1 s envelope window's 8.75 m allowance)
            net.ClientToServer.HoldUntilTick = net.CurrentTick + 35;
            yield return Ticks(8);   // let the in-flight tail drain; the entity then freezes
            bool ownMid = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var frozen);
            var frozenPos = ownMid ? frozen.Pos : default;
            yield return Ticks(20);  // deep inside the hitch
            sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var still);
            float ghost = ownMid ? (still.Pos - frozenPos).magnitude : float.MaxValue;
            T.Check($"the entity FROZE during the hitch -- never ghost-ran on stale intent ({ghost:0.###} m)", ghost < 0.01f);

            // the hitch ends: the backlog bursts through, the spanning claim adopts, sprint continues
            yield return Ticks(60);
            T.Check($"NO rubber-band on a sub-1s sprint hitch ({sess.RecovsApplied} recovs) -- the envelope headroom absorbed it", sess.RecovsApplied == 0);

            // stop + settle -> bit-exact convergence
            sess.Shell.ScriptedStance = null;
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(60);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the hitch (err {err:0.####} m)", own && err < 0.001f);
            T.Check($"DESYNC-QUIET across the run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // The LONG outage (v9; historically the starvation-hold ghost-run test). Past the envelope's 1 s
    // elapsed ceiling a sprinting client legitimately outruns the cap, and the DESIGNED outcome is one
    // recov: the entity stays frozen at the last adopted claim for the whole blackout (never
    // ghost-runs -- the old model coasted stale sprint intent for 240 ms and needed a cap to stop), and
    // on resume the spanning claim exceeds 8.75 m -> the server rolls the owner back to the frozen spot
    // (safe, just abrupt -- the same posture as the vehicle envelope's long-stall rollback), the ack
    // echoes, and play resumes adopted. Exactly ONE recov, then clean.
    public class NetShellOutageRecov : GameTest
    {
        public override string Name => "net.shell_outage_recov";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(53535);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "starved" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // sprint to steady state, then the link goes fully dark for ~2 s while the player keeps
            // sprinting (~14 m of unheard motion -- far past the 8.75 m ceiling allowance)
            sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(100);
            net.ClientToServer.LossProbability = 1.0;
            yield return Ticks(8);   // drain the in-flight tail
            sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var frozen);
            var frozenPos = frozen.Pos;
            yield return Ticks(92);  // the rest of the ~2 s blackout, still sprinting
            sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var still);
            T.Check($"the entity stayed FROZEN for the whole blackout ({(still.Pos - frozenPos).magnitude:0.###} m moved) -- no ghost-run",
                    (still.Pos - frozenPos).magnitude < 0.01f);
            T.Check("no recov while the link is dark (nothing arrives to violate)", sess.RecovsApplied == 0);

            // the link returns: the spanning claim violates the ceiling -> exactly one recov rubber-bands
            // the shell back to the frozen spot, the echo resumes the stream
            net.ClientToServer.LossProbability = 0;
            yield return Until(() => sess.RecovsApplied >= 1, 5);
            T.Check($"the >1s sprint outage resolved as EXACTLY ONE recov ({sess.RecovsApplied})", sess.RecovsApplied == 1);
            yield return Ticks(10);
            float back = (ToU(sess.Shell.TruePhysicsPosition) - frozenPos).magnitude;
            T.Check($"the shell rubber-banded to the frozen last-good (+ a few resumed steps) ({back:0.0} m from it)", back < 3f);

            // keep sprinting -- the resumed stream adopts without further recovs; stop -> converge
            yield return Ticks(80);
            T.Check($"resume clean: still exactly one recov total ({sess.RecovsApplied})", sess.RecovsApplied == 1);
            sess.Shell.ScriptedStance = null;
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(60);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the outage (err {err:0.####} m)", own && err < 0.001f);
            T.Check($"DESYNC-QUIET across the run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // Downhill under client authority (v9; historically the mp-rubberband regression -- two separate
    // CharacterBody3Ds disagreeing on IsOnFloor down a slope, the reconciler yanking the descending
    // shell into the air. One body: the disagreement cannot exist, and the DeterministicGround fork
    // that patched it is deleted -- the shell runs the plain SP grounded path). The course still earns
    // its keep as the ENVELOPE's slope non-regression: climbing 18 deg at walk speed and descending it
    // (gravity-glued or briefly airborne) must never trip the vertical caps. Asserts: climb + descent
    // with ZERO recovs on clean and jittery links, no upward yank mid-descent (guards shell physics),
    // the shell reaches the flat ground, bit-exact convergence, desync-quiet.
    public class NetShellDownhillClientAuth : GameTest
    {
        public override string Name => "net.shell_downhill_clientauth";
        public override double TimeoutSimSeconds => 60;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // the ramp at -Z (the shell spawns facing -Z at yaw 0): 18 deg, 12 m wide, near edge buried
            // below the fallback ground so the shell strolls straight onto it and descends walking back.
            var ramp = new StaticBody3D { CollisionLayer = 1u << 0, Position = new Vector3(0f, 2.5f, -14f), RotationDegrees = new Vector3(18f, 0f, 0f) };
            ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 1f, 24f) } });
            World.AddChild(ramp);

            var net = new MemNetwork(20260717);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "descender" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool haveBody = ded.PlayerSync.TryGetBody(sess.Client.PlayerId, out var follower);
            T.Check("follower body exists for the shell's peer", haveBody);

            // ---- climb (clean link): forward is -Z at the spawn yaw -> up the ramp ----
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(180);
            float climbY = sess.Shell.TruePhysicsPosition.Y;
            T.Check($"climbed the ramp (shell y {climbY:0.00} m)", climbY > 1.5f);
            T.Check($"ZERO recovs on the climb ({sess.RecovsApplied}) -- the 18-deg ascent sits inside the vertical cap", sess.RecovsApplied == 0);

            // ---- descend (clean link): walk backward -> down the slope ----
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, -1f);
            float maxRise = 0f, maxFollowGap = 0f;
            float prevY = sess.Shell.TruePhysicsPosition.Y;
            for (int i = 0; i < 260; i++)
            {
                yield return Ticks(1);
                float y = sess.Shell.TruePhysicsPosition.Y;
                if (i >= 10) maxRise = Mathf.Max(maxRise, y - prevY);   // a DESCENDING walker must never move up (skip the turn-around transient)
                prevY = y;
                if (GodotObject.IsInstanceValid(follower))   // the follower tracks the adopted claims down the slope
                    maxFollowGap = Mathf.Max(maxFollowGap, follower.GlobalPosition.DistanceTo(new Vector3(
                        sess.Shell.TruePhysicsPosition.X, sess.Shell.TruePhysicsPosition.Y, sess.Shell.TruePhysicsPosition.Z)));
            }
            float bottomY = sess.Shell.TruePhysicsPosition.Y;
            GD.Print($"[downhill-clientauth] clean link: recovs={sess.RecovsApplied}, maxRise={maxRise:0.###} m, maxFollowGap={maxFollowGap:0.###} m, bottomY={bottomY:0.00}");
            T.Check($"(a) ZERO recovs on the descent ({sess.RecovsApplied})", sess.RecovsApplied == 0);
            T.Check($"(b) no upward yank mid-descent (max per-tick rise {maxRise:0.###} m)", maxRise < 0.05f);
            T.Check($"(c) the follower body tracked the descent (max gap {maxFollowGap:0.###} m, ~one claim of lag)", maxFollowGap < 0.8f);
            T.Check($"(d) the shell reached the flat ground (y {bottomY:0.00} m)", bottomY < 0.3f);

            // ---- climb again, then descend over a mild adverse link ----
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(180);
            T.Check($"re-climbed for the jittery descent (shell y {sess.Shell.TruePhysicsPosition.Y:0.00} m)", sess.Shell.TruePhysicsPosition.Y > 1.2f);
            net.ClientToServer.LatencyTicks = 3; net.ClientToServer.ReorderJitterTicks = 2; net.ClientToServer.LossProbability = 0.02;
            net.ServerToClient.LatencyTicks = 3; net.ServerToClient.ReorderJitterTicks = 2; net.ServerToClient.LossProbability = 0.02;
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, -1f);
            long recovsBefore = sess.RecovsApplied;
            float maxRiseJ = 0f;
            prevY = sess.Shell.TruePhysicsPosition.Y;
            for (int i = 0; i < 260; i++)
            {
                yield return Ticks(1);
                float y = sess.Shell.TruePhysicsPosition.Y;
                if (i >= 10) maxRiseJ = Mathf.Max(maxRiseJ, y - prevY);
                prevY = y;
            }
            GD.Print($"[downhill-clientauth] jittery link: recovs={sess.RecovsApplied - recovsBefore}, maxRise={maxRiseJ:0.###} m, bottomY={sess.Shell.TruePhysicsPosition.Y:0.00}");
            T.Check($"(e) ZERO recovs descending over the jittery link ({sess.RecovsApplied - recovsBefore} new)", sess.RecovsApplied == recovsBefore);
            T.Check($"(f) no upward yank over the jittery link (max rise {maxRiseJ:0.###} m)", maxRiseJ < 0.05f);
            T.Check($"(g) reached the flat ground over the jittery link (y {sess.Shell.TruePhysicsPosition.Y:0.00} m)", sess.Shell.TruePhysicsPosition.Y < 0.3f);

            // ---- settle on a clean link -> bit-exact convergence + quiet detector ----
            net.ClientToServer.LatencyTicks = 0; net.ClientToServer.ReorderJitterTicks = 0; net.ClientToServer.LossProbability = 0;
            net.ServerToClient.LatencyTicks = 0; net.ServerToClient.ReorderJitterTicks = 0; net.ServerToClient.LossProbability = 0;
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - PlayerReplication.Quantize(ToU(sess.Shell.TruePhysicsPosition))).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the descents (err {err:0.####} m)", own && err < 0.001f);
            T.Check($"DESYNC-QUIET across the whole downhill run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
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
            // (TeleportTo, not a bare GlobalPosition write -- the body's manual-interp restore would
            // undo the latter next tick, the §7 risk 5 seam)
            b.TeleportTo(b.GlobalPosition + new Vector3(0f, 0f, 600f));   // B: z 400 -> 1000, ~610 m from its pocket
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

    // PEI_CLIENT_PLAN §3 Phase C6 -- THE FINISH LINE: a joined player walks to a REPLICATED vehicle,
    // enters through the SHELL'S INTERACT SEAM (RequestEnterNearestPuppet -- the F-key path, not raw
    // commands), drives it across the world, and exits beside the door. A ClientWorldSession driver +
    // a raw observer client join a DedicatedServer (RemoteAvatars) on the fallback world. Asserts:
    // REACH-REJECT (an Enter sent from ~10 m -- beyond ServerVehicles.EnterReach -- never takes the
    // seat: the §2.3 choke point, a cheater can't seat across the map); the seat HANDOFF
    // (DriverPlayerId == the peer, ride mode latched, shell hidden); the vehicle PHYSICALLY DROVE
    // > 8 m under the streamed DriveInput -- on the server node AND on the observer's puppet (everyone
    // else sees it); the EXIT re-appears the shell beside the door and the resumed walk/reconcile loop
    // CONVERGES it onto the exit-teleported entity; and DESYNC-QUIET on BOTH clients across the whole
    // enter/drive/exit run (vehicles are in the EnableSyncCheck set -- the driven vehicle's state must
    // hash-converge while input streams @50 Hz).
    public class NetShellDrive : GameTest
    {
        public override string Name => "net.shell_drive";
        public override double TimeoutSimSeconds => 60;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260723);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery before the session's Client.Tick each tick
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "driver" };
            World.AddChild(sess);      // registers net.client.pump + client.shell (before the server's steps, §2.5)
            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            var obsPump = new DelegateSimStep((t, dt) => observer.Tick(), "l1.obspump");
            world.Sim.Sim.Add(obsPump);
            var obsView = new VehicleReplicaView { Client = observer };
            World.AddChild(obsView);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;
            observer.DesyncDetected += _ => desyncs++;
            observer.Connect();

            // the server-side vehicle: a REAL jeep 10 m in FRONT of the spawn line (shell spawns at the
            // origin facing -Z) -- far enough that the reach-reject case below is genuinely out of reach
            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, -10f);

            yield return Until(() => sess.Shell != null && observer.State == NetSessionState.Connected, 5);
            T.Check("shell spawned + observer joined", sess.Shell != null && observer.State == NetSessionState.Connected);
            if (sess.Shell == null) yield break;
            yield return Until(() => sess.Client.Vehicles.Count == 1 && observer.Vehicles.Count == 1, 5);
            uint netId = 0;
            foreach (var e in sess.Client.Vehicles.All) { netId = e.NetIdValue; break; }
            T.Check("the vehicle replicated to both clients", netId != 0 && observer.Vehicles.Count == 1);
            yield return Ticks(50);   // let the spawn drop settle onto the fallback ground

            // REACH-REJECT: a raw Enter from the spawn (~10 m out, > EnterReach 6 m) -- exactly what a
            // cheater bypassing the client-side 4 m interact gate would send. The choke point refuses it.
            bool ownFar = ded.Server.Players.TryGetByOwner(sess.Client.PlayerId, out var farP);
            bool jeepE = ded.Server.Vehicles.TryGet(netId, out var vFar);
            float farDist = ownFar && jeepE ? (vFar.Pos - farP.Pos).magnitude : 0f;
            T.Check($"the peer really is beyond EnterReach ({farDist:0.0} m)", farDist > ServerVehicles.EnterReach);
            sess.Client.SendEnterVehicle(netId);
            yield return Ticks(25);   // reliable command lands within a couple ticks; give it a second
            bool stillEmpty = ded.Server.Vehicles.TryGet(netId, out var rejE) && rejE.DriverPlayerId == 0;
            T.Check("REACH-REJECT: an Enter from across the field never takes the seat", stillEmpty);
            T.Check("...and no seat fact reached the client", sess.RidingVehicle == 0 && !sess.Shell.IsRiding && !sess.Shell.IsDriving);

            // walk to the vehicle (the C6 story: walk up, get in) -- REAL shell physics, input on the wire
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            for (int i = 0; i < 400 && sess.Shell.TruePhysicsPosition.DistanceTo(jeep.GlobalPosition) > 3f; i++)
                yield return Ticks(1);
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            float walkDist = sess.Shell.TruePhysicsPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"walked to the vehicle (dist {walkDist:0.0} m)", walkDist < 3.5f);
            yield return Ticks(25);   // stop + let the last walk inputs ack so the avatar is in reach too

            // ENTER through the interact seam (the F-key path): nearest puppet -> SendEnterVehicle
            int enterFacts = 0;
            sess.Client.VehicleEntered += e => { if (e.PlayerId == sess.Client.PlayerId) enterFacts++; };
            T.Check("the interact seam found the puppet + requested the seat", sess.Shell.RequestEnterNearestPuppet());
            yield return Until(() => sess.Shell.IsDriving, 5);
            bool seated = ded.Server.Vehicles.TryGet(netId, out var seatE) && seatE.DriverPlayerId == sess.Client.PlayerId;
            T.Check("SEAT HANDOFF: server DriverPlayerId == the peer", seated);
            T.Check($"the VehicleEntered fact reached the driver ({enterFacts})", enterFacts > 0);
            // Part A: the seat fact now builds a CLIENT-LOCAL real Vehicle and seats the shell through the
            // SP direct-drive path (retail client authority) -- ride-the-puppet mode is gone for drivers
            T.Check("driving-local latched: shell hidden, a real local Vehicle exists, its puppet suppressed",
                    sess.Shell.IsDriving && !sess.Shell.Visible && sess.LocalVehicle != null
                    && !sess.VehicleView.TryGetPuppet(netId, out _));
            yield return Until(() => observer.Vehicles.TryGet(netId, out var oe) && oe.DriverPlayerId == sess.Client.PlayerId, 5);
            T.Check("the observer sees the seat taken",
                    observer.Vehicles.TryGet(netId, out var obsSeat) && obsSeat.DriverPlayerId == sess.Client.PlayerId);

            // DRIVE ~4 s: ScriptedDrive -> the shell drives its LOCAL vehicle (0-tick wheel) -> the session
            // streams VehicleState @25 Hz -> the server ADOPTS it onto its held node + entity
            bool haveObsPup = obsView.TryGetPuppet(netId, out var obsPup);
            T.Check("the observer materialized a puppet", haveObsPup);
            var jeepStart = jeep.GlobalPosition;
            var obsPupStart = haveObsPup ? obsPup.GlobalPosition : Vector3.Zero;
            sess.Shell.ScriptedDrive = new Vector2(0f, 1f);   // (steer, throttle): straight ahead, full throttle
            yield return Ticks(200);
            float driven = jeep.GlobalPosition.DistanceTo(jeepStart);
            T.Check($"the server node MOVED under the adopted driver state ({driven:0.0} m)", driven > 8f);
            T.Check("the server node is HELD (frozen kinematic adopt -- retail updatePhysics)", jeep.Freeze && jeep.NetHeld);
            float obsDriven = haveObsPup ? obsPup.GlobalPosition.DistanceTo(obsPupStart) : 0f;
            T.Check($"...and the observer's puppet drove with it ({obsDriven:0.0} m) -- everyone else sees it", obsDriven > 8f);
            T.Check("the shell rode along (cam/exit anchor)", sess.Shell.GlobalPosition.DistanceTo(jeep.GlobalPosition) < 6f);

            // coast down, then EXIT through the seam (the F-while-riding path)
            sess.Shell.ScriptedDrive = new Vector2(0f, 0f);
            yield return Ticks(100);
            // the §7-risk-6 seam: prove ServerExit routes the teleport spot through AdjustExitSpot (the
            // dedicated server wires a Terr.SampleHeight clamp here; fallback worlds have no Terr, so the
            // L1 probes the seam with an identity lambda + flag)
            bool exitSpotAdjusted = false;
            ded.Server.VehicleHost.AdjustExitSpot = p => { exitSpotAdjusted = true; return p; };
            T.Check("the exit seam fired", sess.Shell.RequestExitPuppet());
            yield return Until(() => !sess.Shell.IsDriving, 5);
            T.Check("seat freed server-side", ded.Server.Vehicles.TryGet(netId, out var freedE) && freedE.DriverPlayerId == 0);
            T.Check("the exit teleport routed through AdjustExitSpot (§7 risk 6 seam)", exitSpotAdjusted);
            T.Check("the shell RE-APPEARED", !sess.Shell.IsDriving && sess.Shell.Visible);
            T.Check("the local vehicle was destroyed + the puppet view resumed", sess.LocalVehicle == null);
            var doorFlat = sess.Shell.GlobalPosition - jeep.GlobalPosition; doorFlat.Y = 0f;
            T.Check($"...beside the door, not in the seat and not at the curb it entered from ({doorFlat.Length():0.0} m from center)",
                    doorFlat.Length() > 1.0f && doorFlat.Length() < 8f);

            // the walk/reconcile loop RESUMED: the shell converges onto the server's exit-teleported entity
            // (the shell_walk_reconcile (b) bar -- both sides settle to the same grid point)
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e2);
            float exitErr = own ? (e2.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"shell CONVERGED on the exit-teleported entity (err {exitErr:0.###} m)", own && exitErr < 0.05f);
            var back = sess.Shell.TruePhysicsPosition;
            sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(75);
            sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            T.Check($"the walk plane resumed after the ride ({back.DistanceTo(sess.Shell.TruePhysicsPosition):0.0} m)",
                    back.DistanceTo(sess.Shell.TruePhysicsPosition) > 2f);

            // DESYNC-QUIET (the C6 review fold): vehicles are AllRelevant -> in the EnableSyncCheck set;
            // the whole enter/drive/exit run must hash-converge on BOTH snapshot-applying clients
            yield return Ticks(60);
            T.Check($"sync-check blocks flowed ({ded.Server.Composer.Diag.SyncCheckBlocksWritten})",
                    ded.Server.Composer.Diag.SyncCheckBlocksWritten > 0);
            T.Check($"DESYNC-QUIET across enter/drive/exit on both clients ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pumps so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            world.Sim.Sim.Remove(obsPump);
            observer.Disconnect();
        }
    }

    // PEI_COMBAT_PLAN §3 / SP-MP-unify P3a -- the combat lane: a joined client's fire routes OVER THE WIRE and
    // kills a real server zombie brain, with the server's facts (HitConfirmed / ZombieDied / kill credit /
    // the replicated Dead anim) rendering the result. The net.shell_walk_reconcile rig (ClientWorldSession +
    // DedicatedServer{RemoteAvatars}) plus the net.zombie_chase_sync brain. The shell fires through the seam
    // (scripted Shell.Fire() with NetFire wired -- never polled input): its LOCAL bullet is COSMETIC (tracer
    // only -- Kills stays 0, the shared-world brain takes no direct DamageHit), the server's bullet is the
    // authority. Then a burst at a second peer's avatar proves the P3a posture: PvP is now ON, so the burst is
    // server-resolved PLAYER damage that drops the bystander (was the D1 "players aren't targets" no-op) -- and
    // the whole run stays DESYNC-QUIET (SystemPlayerCombat is in the EnableSyncCheck set, so the kill/health
    // counters are hash-checked).
    public class NetShellFireZombie : GameTest
    {
        public override string Name => "net.shell_fire_zombie";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            // a walkable navmesh for the brain (the net.zombie_chase_sync rig)
            var nm = new NavigationMesh();
            nm.Vertices = new[] { new Vector3(-60f, 0f, -60f), new Vector3(-60f, 0f, 60f), new Vector3(60f, 0f, 60f), new Vector3(60f, 0f, -60f) };
            nm.AddPolygon(new int[] { 0, 1, 2, 3 });
            World.AddChild(new NavigationRegion3D { NavigationMesh = nm });

            var net = new MemNetwork(20260727);
            NetWorldClient bystander = null;
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); bystander?.Tick(); }, "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery + bystander session before the session's steps (§2.5)
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "gunner" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;
            bystander = new NetWorldClient(new MemClientTransport(net), "bystander", contentHash: NetContent.Hash);
            bystander.DesyncDetected += _ => desyncs++;
            bystander.Connect();

            var confirms = new List<HitConfirmEvent>();
            var zDeaths = new List<ZombieDiedEvent>();
            var pDeaths = new List<PlayerDiedEvent>();
            sess.Client.HitConfirmed += confirms.Add;
            sess.Client.ZombieDied += zDeaths.Add;
            sess.Client.PlayerDied += pDeaths.Add;
            bystander.PlayerDied += pDeaths.Add;

            yield return Until(() => sess.Shell != null && bystander.State == NetSessionState.Connected, 5);
            T.Check("shell spawned + bystander joined", sess.Shell != null && bystander.State == NetSessionState.Connected);
            if (sess.Shell == null) yield break;
            T.Check("P3a posture: PvP is ON on the dedicated server", ded.Server.Combat.PvPEnabled);
            T.Check($"the MP shell spawns holding the EAGLEFIRE -- the server's validation profile ({sess.Shell.HeldGunName})",
                    sess.Shell.HasGunOut && sess.Shell.HeldGunName == "eaglefire");
            yield return Until(() => ded.PlayerSync.TrackedCount == 2, 5);
            T.Check("both peers have C2 avatar bodies", ded.PlayerSync.TrackedCount == 2);

            // the target: a REAL ZombieController brain 8 m in front of the shell, published by ZombieNetSync
            var z = new ZombieController { Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = new Vector3(0f, 0.3f, 8f);
            yield return Until(() => sess.Puppets.PuppetCount == 1, 5);
            T.Check("the zombie replicated + puppeted in the session's world", sess.Puppets.PuppetCount == 1);
            uint zid = 0;
            foreach (var e in sess.Client.Zombies.All) { zid = e.NetIdValue; break; }

            // FIRE through the seam -- scripted Shell.Fire() (the NetFire delegate sends the aim ray), the
            // shell re-aimed at the brain each tick (it chases). Eaglefire Zombie_Damage 99 vs brain HP 100
            // -> the second landed bullet kills. The kill must come back over the wire, not from the local
            // bullet: the cosmetic flag means the shared-world brain never takes a direct local DamageHit.
            int shots = 0;
            for (int i = 0; i < 500 && !z.Dead; i++)
            {
                var eye = sess.Shell.TruePhysicsPosition + Vector3.Up * 1.6f;
                var dir = z.GlobalPosition + Vector3.Up * 1.2f - eye;
                sess.Shell.RotationDegrees = new Vector3(0f, Mathf.RadToDeg(Mathf.Atan2(-dir.X, -dir.Z)), 0f);
                if (sess.Shell.Fire()) shots++;
                yield return Ticks(1);
            }
            T.Check($"the WIRE killed the brain ({shots} shots fired)", z.Dead && shots >= 2);
            T.Check("the local cosmetic bullet never credited a kill (shell Kills == 0 -- credit is the server's)", sess.Shell.Kills == 0);
            yield return Until(() => zDeaths.Count > 0, 5);
            T.Check("ZombieDied fact reached the shooter", zDeaths.Count == 1);
            T.Check("...with the shell's kill credit", zDeaths.Count > 0 && zDeaths[0].NetId == zid && zDeaths[0].Killer == sess.Client.PlayerId);
            bool sawZombieConfirm = false, sawKillConfirm = false;
            foreach (var c in confirms)
            {
                if (c.TargetKind == (byte)HitTargetKind.Zombie) sawZombieConfirm = true;
                if (c.Killed) sawKillConfirm = true;
            }
            T.Check($"HitConfirmed flowed with TargetKind=Zombie ({confirms.Count} confirms)", sawZombieConfirm);
            T.Check("the killing hit confirmed Killed=true", sawKillConfirm);
            yield return Until(() => sess.Client.Zombies.TryGet(new NetId(zid), out var zr) && zr.IsDead, 5);
            T.Check("the replica anim byte reads DEAD (the puppet ragdolls off it)",
                    sess.Client.Zombies.TryGet(new NetId(zid), out var zRep) && zRep.IsDead);
            yield return Until(() => ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var cs) && cs.Kills == 1, 5);
            T.Check("server CombatState credited kills == 1", ded.Server.CombatState.TryGet(sess.Client.PlayerId, out var sCs) && sCs.Kills == 1);
            yield return Ticks(30);   // settle: the kills delta + a sync-check round flush to both clients
            T.Check("CombatState parity: session replica == server (StateHash)",
                    sess.Client.CombatState.StateHash() == ded.Server.CombatState.StateHash());
            T.Check("CombatState parity: bystander replica == server",
                    bystander.CombatState.StateHash() == ded.Server.CombatState.StateHash());

            // PvP-ON (P3a): a burst at the bystander's avatar (~2 m away, torso height) is server-resolved
            // PLAYER damage now -- 3 landed Eaglefire torso shots (40 each) drop the 100 HP bystander. This was
            // the D1 "players are simply not targets" no-op; P3a makes the server own the damage + death fact.
            long hitsPlayerBefore = ded.Server.Combat.Diag.BulletHitsPlayer;
            bool haveBy = ded.Server.Players.TryGetByOwner(bystander.PlayerId, out var bype);
            T.Check("bystander player entity exists server-side", haveBy);
            int pvpShots = 0;
            for (int i = 0; i < 200 && pvpShots < 4; i++)
            {
                var eye = sess.Shell.TruePhysicsPosition + Vector3.Up * 1.6f;
                var dir = new Vector3(bype.Pos.x, bype.Pos.y + 1.0f, bype.Pos.z) - eye;   // torso zone (1.0x), like the L0 kill-credit rig
                sess.Shell.RotationDegrees = new Vector3(0f, Mathf.RadToDeg(Mathf.Atan2(-dir.X, -dir.Z)), 0f);
                if (sess.Shell.Fire()) pvpShots++;
                yield return Ticks(1);
            }
            yield return Ticks(50);   // every bullet adjudicated + snapshots flushed
            T.Check($"a full burst went at the second peer ({pvpShots} shots)", pvpShots >= 3);
            T.Check($"PvP-ON: bullets resolved on the player ({ded.Server.Combat.Diag.BulletHitsPlayer - hitsPlayerBefore} hits)",
                    ded.Server.Combat.Diag.BulletHitsPlayer > hitsPlayerBefore);
            bool sawByDeath = false;
            foreach (var d in pDeaths) if (d.Victim == bystander.PlayerId) sawByDeath = true;
            T.Check("a PlayerDied fact for the bystander reached the clients (server-owned death)", sawByDeath);
            bool byOk = ded.Server.CombatState.TryGet(bystander.PlayerId, out var byCs);
            T.Check("bystander took server-authoritative damage: dead, Health 0", byOk && !byCs.Alive && byCs.Health == 0);
            bool byRepOk = bystander.CombatState.TryGet(bystander.PlayerId, out var byRep);
            T.Check("...and its own replica agrees (dead, Health 0)", byRepOk && !byRep.Alive && byRep.Health == 0);

            // the D1 hash-convergence proof: SystemPlayerCombat is in the sync-check set, so the kill/death
            // counters themselves were hash-checked all run -- and the detector stayed silent
            T.Check($"sync-check blocks flowed ({ded.Server.Composer.Diag.SyncCheckBlocksWritten})",
                    ded.Server.Composer.Diag.SyncCheckBlocksWritten > 0);
            T.Check($"DESYNC-QUIET across the whole combat run ({desyncs} fired)", desyncs == 0);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
            bystander.Disconnect();
        }
    }

    // MP pickup (ITEM_PICKUP_WIRING_PLAN Steps 1-4), the headline round trip: F on a focused world-item
    // puppet -> PickupItemCommand -> the §2.3 choke point -> OnPickupItem transacts into the SERVER grid ->
    // WorldItemRemoved retires the puppet everywhere + the owner-block echo makes the item appear in the
    // shell's LOCAL bag (the adoption seam -- the one piece no L0 can see). Also pins the Step 4 decision:
    // the demo kit is seeded SERVER-side on join, so the bag the player sees is the server's truth.
    public class NetShellPickupItem : GameTest
    {
        public override string Name => "net.shell_pickup_item";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();   // real ids: the puppet view + the join demo-kit seeding resolve against the catalog

            var net = new MemNetwork(20260808);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery before the session's Client.Tick each tick
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "picker" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // Step 4 seeding: the joiner's SERVER grid carries the demo kit -- the bag is truth, not fiction
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server grid seeded with the demo kit on join (Eaglefire in the primary slot)",
                    sHave && sInv.Inventory.getItemCount(4) == 1);
            yield return Ticks(10);   // the join echo settles into the shell's bag
            T.Check("the shell's bag ADOPTED the server grid (the seeded kit's medkits)",
                    sess.Shell.Inventory.getItemCount(15) == 2);

            // the target: a GENERATOR (458 -- not in the demo kit, so counts discriminate) 2 m ahead of
            // the shell's facing: inside PickupReach AND the Step 5 facing cone, past the at-feet skip
            var fwd = -sess.Shell.GlobalTransform.Basis.Z;
            var e = ded.Server.Transactions.SpawnWorldItem(new SDG.Unturned.Item(458), ToU(sess.Shell.GlobalPosition + fwd * 2f), UnityEngine.Vector3.zero);
            T.Check("server spawned the world-item entity", e != null && ded.Server.WorldItems.Count == 1);
            yield return Until(() => sess.Items.TryGetNode(e.NetIdValue, out _), 5);
            bool haveNode = sess.Items.TryGetNode(e.NetIdValue, out var node);
            T.Check("the item puppet materialized on the joined client", haveNode);
            var wp = node as WorldItemPuppet;
            T.Check("the puppet carries the entity NetId (Step 1)", wp != null && wp.NetId == e.NetIdValue);

            // drive the request through the seam (the F-chain path minus the focus raycast -- the same
            // way net.shell_drive drives ride mode). A REQUEST only: nothing local changes until the
            // server's facts come back.
            T.Check("the pickup request fired through the NetPickupItem seam", sess.Shell.RequestPickupPuppet(wp));

            yield return Until(() => ded.Server.WorldItems.Count == 0, 5);
            T.Check("(a) the server world-item entity retired", ded.Server.WorldItems.Count == 0);
            yield return Until(() => !sess.Items.TryGetNode(e.NetIdValue, out _), 5);
            T.Check("(b) the puppet node freed (WorldItemRemoved -> the diff-driven view)", !sess.Items.TryGetNode(e.NetIdValue, out _));
            T.Check("(c) the SERVER grid holds the item", sHave && sInv.Inventory.getItemCount(458) == 1);
            yield return Until(() => sess.Shell.Inventory.getItemCount(458) == 1, 5);
            T.Check("(d) the shell's LOCAL bag shows it -- the owner-block adoption echo",
                    sess.Shell.Inventory.getItemCount(458) == 1);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // MP pickup denial (ITEM_PICKUP_WIRING_PLAN Step 3): a LEGAL pickup into a FULL server grid --
    // ItemPickupDenied comes back to the requester only, the entity + puppet stay in the world, and
    // neither grid changes (the request made no local mutation to roll back). The L0
    // pickup_into_a_full_grid_is_denied_but_stays recipe, driven through the real shell seam.
    public class NetShellPickupDeniedStays : GameTest
    {
        public override string Name => "net.shell_pickup_denied_stays";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260809);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "hoarder" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;

            // pack the SERVER grid completely full (1x1 bandages into every free cell -- the authority
            // seeding its own state, the TransactionalHarness.Grant pattern)
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server inventory exists for the peer", sHave);
            int filled = 0;
            while (sInv.Inventory.tryAddItem(new SDG.Unturned.Item(95))) filled++;
            T.Check($"server grid packed FULL ({filled} filler items)", filled > 0);

            var fwd = -sess.Shell.GlobalTransform.Basis.Z;
            var e = ded.Server.Transactions.SpawnWorldItem(new SDG.Unturned.Item(13), ToU(sess.Shell.GlobalPosition + fwd * 2f), UnityEngine.Vector3.zero);
            yield return Until(() => sess.Items.TryGetNode(e.NetIdValue, out _), 5);
            bool haveNode = sess.Items.TryGetNode(e.NetIdValue, out var node);
            T.Check("the item puppet materialized", haveNode);
            yield return Ticks(10);   // let the fill's owner-block echo settle before capturing baselines
            int serverBeans = sInv.Inventory.getItemCount(13);
            int localBeans = sess.Shell.Inventory.getItemCount(13);

            bool denied = false;
            sess.Client.ItemPickupDenied += ev => denied |= ev.NetId == e.NetIdValue;
            long deniedBefore = ded.Server.Transactions.Diag.PickupsDenied;
            T.Check("the pickup request fired through the seam", sess.Shell.RequestPickupPuppet(node as WorldItemPuppet));

            yield return Until(() => denied, 5);
            T.Check("ItemPickupDenied reached the requester", denied);
            T.Check("Diag.PickupsDenied bumped (legal-but-full, not a validation reject)",
                    ded.Server.Transactions.Diag.PickupsDenied == deniedBefore + 1);
            T.Check("the world-item entity STAYED", ded.Server.WorldItems.Count == 1);
            yield return Ticks(15);   // a (wrong) removal broadcast/echo would land within this window
            T.Check("the puppet STAYED in the joined world", sess.Items.TryGetNode(e.NetIdValue, out _));
            T.Check("server grid unchanged (the item never entered it)", sInv.Inventory.getItemCount(13) == serverBeans);
            T.Check("the shell's bag unchanged", sess.Shell.Inventory.getItemCount(13) == localBeans);

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // Phase 6/8 client seams (mp-parity-clientseams): the connect shell DRAGS an item -- the InventoryUI
    // Drop path routes through RequestMoveItem/NetMoveItem, the SERVER's TryDrag applies it, and the
    // owner-block echo re-seats the jar in the shell's bag. Teeth: unwired, the request returns false and
    // the server grid never changes (the SP local drag would be silently reverted by the next echo anyway).
    public class NetShellMoveItem : GameTest
    {
        public override string Name => "net.shell_move_item";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260810);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "dragger" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            T.Check("server grid seeded (demo kit)", sHave);
            yield return Ticks(10);   // the join echo settles into the shell's bag

            // source: a Bandage (95, 1x1) in POCKETS (page 2), located on the SERVER grid (the truth the
            // shell's bag mirrors); destination: the first free 1x1 cell on the same page (checkSpaceEmpty
            // = the ported cell math the server's TryDrag validates with)
            var pg = sInv.Inventory.items[2];
            byte sx = 255, sy = 255;
            for (byte i = 0; i < pg.getItemCount(); i++)
            { var j = pg.getItem(i); if (j.item?.id == 95) { sx = j.x; sy = j.y; break; } }
            T.Check("found the pockets Bandage on the server grid", sx != 255);
            byte dx = 255, dy = 255;
            for (byte y = 0; y < pg.height && dx == 255; y++)
                for (byte x = 0; x < pg.width && dx == 255; x++)
                    if (pg.checkSpaceEmpty(x, y, 1, 1, 0)) { dx = x; dy = y; }
            T.Check("found a free destination cell", dx != 255 && (dx != sx || dy != sy));

            long movesBefore = ded.Server.Transactions.Diag.GridMovesApplied;
            T.Check("the move request fired through the NetMoveItem seam",
                    sess.Shell.RequestMoveItem(2, sx, sy, 2, dx, dy, 0));
            yield return Until(() => pg.getIndex(dx, dy) != byte.MaxValue, 5);
            byte di = pg.getIndex(dx, dy);
            T.Check("(a) the SERVER grid applied the drag", di != byte.MaxValue && pg.getItem(di).item?.id == 95);
            T.Check("(b) Diag.GridMovesApplied bumped", ded.Server.Transactions.Diag.GridMovesApplied == movesBefore + 1);
            yield return Until(() => sess.Shell.Inventory.items[2].getIndex(dx, dy) != byte.MaxValue, 5);
            var cpg = sess.Shell.Inventory.items[2];
            byte ci = cpg.getIndex(dx, dy);
            T.Check("(c) the shell's bag echoed the new layout", ci != byte.MaxValue && cpg.getItem(ci).item?.id == 95);

            world.Sim.Sim.Remove(pump);
        }
    }

    // Phase 6/8 client seams: the connect shell EATS a held consumable -- TickConsume's completion routes
    // through NetConsume (the cell names the item; the server deletes by id) instead of the local
    // removeItemAmount, and the owner echo empties the bag. Teeth: unwired, the SP branch would delete
    // locally and the SERVER count assertion (a) fails.
    public class NetShellConsume : GameTest
    {
        public override string Name => "net.shell_consume";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260811);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "eater" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            yield return Ticks(10);
            int serverBefore = sHave ? sInv.Inventory.getItemCount(95) : 0;   // demo kit: 2 pocket + 1 backpack Bandages
            T.Check("server grid carries the demo Bandages", serverBefore >= 2);
            T.Check("the shell's bag adopted them", sess.Shell.Inventory.getItemCount(95) == serverBefore);

            var asset = Assets.find(95);
            T.Check("Bandage resolves as a consumable", asset != null && asset.IsConsumable);
            long consumesBefore = ded.Server.Transactions.Diag.ConsumesApplied;
            sess.Shell.EquipHeldConsumable(asset, null);   // hold it (the InventoryUI "Hold" action)
            sess.Shell.StartConsume();                     // LMB: begin eating
            sess.Shell.DebugConsumeTick(30f);              // jump the eat timer -> the completion runs the seam branch

            yield return Until(() => sHave && sInv.Inventory.getItemCount(95) == serverBefore - 1, 5);
            T.Check("(a) the SERVER deleted the eaten Bandage", sInv.Inventory.getItemCount(95) == serverBefore - 1);
            T.Check("(b) Diag.ConsumesApplied bumped", ded.Server.Transactions.Diag.ConsumesApplied == consumesBefore + 1);
            yield return Until(() => sess.Shell.Inventory.getItemCount(95) == serverBefore - 1, 5);
            T.Check("(c) the shell's bag echoed the deletion", sess.Shell.Inventory.getItemCount(95) == serverBefore - 1);

            world.Sim.Sim.Remove(pump);
        }
    }

    // Phase 6/8 client seams: the connect shell SPENDS XP -- the SkillsUI upgrade routes through
    // RequestUpgradeSkill/NetUpgradeSkill, the server's PlayerSkills.TryUpgrade validates cost/cap, and
    // the owner skills block echoes the level + spend into AdoptReplicatedSkills. Also proves the
    // server-awarded XP echo (ServerAward -> the shell's local PlayerSkills) the upgrade depends on.
    public class NetShellUpgradeSkill : GameTest
    {
        public override string Name => "net.shell_upgrade_skill";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260812);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "student" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;

            ded.Server.Skills.ServerAward(sess.Client.PlayerId, 100, ded.Server.Session.CurrentTick);
            yield return Until(() => sess.Shell.Skills.experience == 100, 5);
            T.Check("the XP award echoed into the shell's local PlayerSkills (owner-block adoption)",
                    sess.Shell.Skills.experience == 100);

            bool sHave = ded.Server.Skills.TryGet(sess.Client.PlayerId, out var sSk);
            T.Check("server skills entry exists", sHave);
            T.Check("the upgrade request fired through the NetUpgradeSkill seam",
                    sess.Shell.RequestUpgradeSkill(0, 0));   // OFFENSE / Overkill (cost 10 at level 0)
            yield return Until(() => sHave && sSk.Skills.skills[0][0].level == 1, 5);
            T.Check("(a) the SERVER leveled Overkill", sSk.Skills.skills[0][0].level == 1);
            T.Check("(b) the SERVER spent the XP", sSk.Skills.experience == 90);
            yield return Until(() => sess.Shell.Skills.skills[0][0].level == 1, 5);
            T.Check("(c) the shell echoed the level", sess.Shell.Skills.skills[0][0].level == 1);
            T.Check("(d) the shell echoed the spend", sess.Shell.Skills.experience == 90);

            world.Sim.Sim.Remove(pump);
        }
    }

    // Phase 6/8 client seams: the connect shell OPENS A CRATE -- RequestOpenStorage/NetOpenStorage; the
    // server arbitrates (one opener), loads the crate grid into the opener's STORAGE page, and the
    // StorageOpened fact + owner echo bring the dashboard + grid back. Then a crate->bag drag rides the
    // SAME move seam (page 7 addressing, §3.7), and the ESC/Tab close path saves + clears server-side.
    public class NetShellOpenStorage : GameTest
    {
        public override string Name => "net.shell_open_storage";
        public override double TimeoutSimSeconds => 30;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260813);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "stasher" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            yield return Ticks(10);

            // a server-side crate 2 m from the shell (inside StorageReach) holding one bean can
            var fwd = -sess.Shell.GlobalTransform.Basis.Z;
            var crate = ded.Server.Inventories.ServerRegisterCrate(ded.Server.Ids.Mint(), 5, 4,
                ToU(sess.Shell.GlobalPosition + fwd * 2f));
            crate.Storage.tryAddItem(new Item(13));

            T.Check("the open request fired through the NetOpenStorage seam",
                    sess.Shell.RequestOpenStorage(crate.NetIdValue));
            yield return Until(() => crate.OpenBy == sess.Client.PlayerId, 5);
            T.Check("(a) the server granted the open (arbitration latch)", crate.OpenBy == sess.Client.PlayerId);
            var storagePage = sess.Shell.Inventory.items[PlayerInventory.STORAGE];
            yield return Until(() => storagePage.width == 5 && storagePage.getItemCount() == 1, 5);
            T.Check("(b) the CRATE grid echoed into the shell's STORAGE page", storagePage.width == 5 && storagePage.getItemCount() == 1);
            T.Check("(c) the StorageOpened fact opened the dashboard", sess.Shell.DashboardOpen);

            // drag the bean can OUT of the crate into the bag -- the same NetMoveItem seam, page-7 source
            var bj = storagePage.getItem(0);
            var pocket = sHave ? sInv.Inventory.items[2] : null;
            byte fx = 255, fy = 255;
            for (byte y = 0; y < pocket.height && fx == 255; y++)
                for (byte x = 0; x < pocket.width && fx == 255; x++)
                    if (pocket.checkSpaceEmpty(x, y, 1, 1, 0)) { fx = x; fy = y; }
            T.Check("found a free pockets cell", fx != 255);
            T.Check("the crate->bag move request fired", sess.Shell.RequestMoveItem(PlayerInventory.STORAGE, bj.x, bj.y, 2, fx, fy, 0));
            yield return Until(() => pocket.getIndex(fx, fy) != byte.MaxValue, 5);
            byte pi = pocket.getIndex(fx, fy);
            T.Check("(d) the server moved it out of the crate into the bag", pi != byte.MaxValue && pocket.getItem(pi).item?.id == 13);

            // close via the ESC/Tab path: the server saves the (now-empty) view back + frees the crate
            sess.Shell.DebugCloseCrate();
            yield return Until(() => crate.OpenBy == 0, 5);
            T.Check("(e) the server closed + freed the crate", crate.OpenBy == 0);
            T.Check("(f) the crate grid saved back empty (the bean left it)", crate.Storage.getItemCount() == 0);
            yield return Until(() => storagePage.width == 0, 5);
            T.Check("(g) the STORAGE page cleared on the echo", storagePage.width == 0);

            world.Sim.Sim.Remove(pump);
        }
    }

    // Phase 6/8 client seams: the connect shell PLANTS a deployable -- TickDeploy's place-confirm routes
    // through RequestPlaceDeployable/NetPlaceDeployable, the server validates spot + supplies and spends
    // the item, DeployablePlaced broadcasts, and DeployableReplicaView renders the real node (NetId
    // stamped). Then the F-toggle rides RequestToggleDeployable and the echo lands via NetSetPowered.
    public class NetShellPlaceDeployable : GameTest
    {
        public override string Name => "net.shell_place_deployable";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260814);
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

            // grant a Generator (458) into the SERVER grid (the authority seeding its own state); the
            // echo puts it in the bag -- placement validation requires the item to spend
            T.Check("server granted the Generator", sHave && sInv.Inventory.tryAddItem(new Item(458)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(458) == 1, 5);
            T.Check("the grant echoed into the bag", sess.Shell.Inventory.getItemCount(458) == 1);

            var fwd = -sess.Shell.GlobalTransform.Basis.Z;
            var spot = sess.Shell.GlobalPosition + fwd * 2.5f;
            T.Check("the place request fired through the NetPlaceDeployable seam",
                    sess.Shell.RequestPlaceDeployable(458, spot, 45f));
            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("(a) the SERVER planted the entity", ded.Server.Deployables.Count == 1);
            T.Check("(b) the SERVER spent the item", sInv.Inventory.getItemCount(458) == 0);
            Deployable node = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetNode(e.NetIdValue, out node)) return true;
                return false;
            }, 5);
            T.Check("(c) DeployableReplicaView rendered the node, NetId stamped", node != null && node.NetId != 0);
            yield return Until(() => sess.Shell.Inventory.getItemCount(458) == 0, 5);
            T.Check("(d) the bag echoed the spend", sess.Shell.Inventory.getItemCount(458) == 0);

            // the F-toggle: a REQUEST addressed by the replica NetId; the echo flips the node's target
            T.Check("the toggle request fired through the NetToggleDeployable seam",
                    sess.Shell.RequestToggleDeployable(node));
            yield return Until(() => node.PoweredTarget, 5);
            T.Check("(e) the toggle echoed into the replica node (NetSetPowered)", node.PoweredTarget);
            bool sEnt = ded.Server.Deployables.TryGet(node.NetId, out var ent);
            T.Check("(f) the server entity agrees (ToggledOn)", sEnt && ent.ToggledOn);

            world.Sim.Sim.Remove(pump);
        }
    }

    // MP console teleport (#27, branch mp-teleport): live-server F1 `teleport <location>` snapped the
    // player RIGHT BACK -- DevConsole ran a client-LOCAL Player.TeleportTo, the server's authoritative
    // entity never moved, and the reconciler dragged the shell home. Part (a) keeps that pre-fix path as
    // permanent teeth: a local-only TeleportTo with no server move MUST be dragged back (that IS the
    // reconciler's contract -- position is not the client's to write). Part (b) is the fix: the teleport
    // rides the EXISTING console wire as coordinates (DevConsole resolves the game-side location table,
    // RunConsole applies ServerTeleport), PlayerNetSync's avatar adopts the moved entity, and the shell
    // SNAPS onto the replicated spot and STAYS -- the round trip no L0 can see.
    public class NetShellConsoleTeleport : GameTest
    {
        public override string Name => "net.shell_console_teleport";
        public override double TimeoutSimSeconds => 40;

        static float Horiz(Vector3 a, Vector3 b) { var d = a - b; return new Vector2(d.X, d.Z).Length(); }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260817);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);   // datagram delivery before the session's Client.Tick each tick
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "porter" };
            World.AddChild(sess);
            // AllowCheats: the real `--dedicated` boot turns the console cheats on (Main.BuildDedicated);
            // the node's default is the locked public posture, so the rig opts in like an admin would
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true, AllowCheats = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            yield return Ticks(25);   // settle on the ground with corrections flowing
            var spawn = sess.Shell.TruePhysicsPosition;
            var target = spawn + new Vector3(30f, 3f, 20f);   // +3 up = the SP drop-height; both bodies land on the flat ground

            // (a) TEETH -- the teleport CHEAT (v9): a client-local TeleportTo with no server say-so is
            // now a 36 m claim jump the ENVELOPE rejects -- a recov rubber-bands the shell straight back
            // to the last-good spot and the target is never held. (With the envelope disabled the claim
            // would adopt verbatim -- the L0 battery proves that seam's teeth.)
            long recovsBefore = sess.RecovsApplied;
            sess.Shell.TeleportTo(target);
            yield return Ticks(50);
            float backAt = Horiz(sess.Shell.TruePhysicsPosition, spawn);
            T.Check($"(a) the local-only teleport was rubber-banded BACK to spawn ({backAt:0.0} m off)", backAt < 3f);
            T.Check($"(a) never held the target ({Horiz(sess.Shell.TruePhysicsPosition, target):0.0} m away)",
                    Horiz(sess.Shell.TruePhysicsPosition, target) > 20f);
            T.Check($"(a) the envelope recov'd it home (recovs {sess.RecovsApplied - recovsBefore})",
                    sess.RecovsApplied > recovsBefore);

            // (b) the FIX: the same jump as coordinates over the existing console wire. DevConsole's MP
            // branch builds exactly this numeric form from a location name; here the coords are the
            // spawn-relative target so the assert is map-independent.
            string verdict = null;
            sess.Client.ConsoleResult += e => verdict = e.Text;
            string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                       "teleport {0:0.##} {1:0.##} {2:0.##}", target.X, target.Y, target.Z);
            T.Check("console teleport sent on the wire", sess.Client.SendConsole(cmd));
            yield return Until(() => Horiz(sess.Shell.TruePhysicsPosition, target) < 1.5f, 10);
            T.Check($"(b) the shell CONVERGED on the teleport target (horiz err {Horiz(sess.Shell.TruePhysicsPosition, target):0.##} m)",
                    Horiz(sess.Shell.TruePhysicsPosition, target) < 1.5f);
            T.Check($"(b) server verdict echoed back ('{verdict}')", verdict != null && verdict.Contains("teleported to"));
            bool sHave = ded.Server.Players.TryGetByOwner(sess.Client.PlayerId, out var se);
            T.Check("(b) the AUTHORITATIVE entity is at the target (ServerTeleport, adopted by the avatar)",
                    sHave && Horiz(new Vector3(se.Pos.x, se.Pos.y, se.Pos.z), target) < 1.5f);

            // ...and STAYS: the snapback would land within this window if the entity hadn't moved
            yield return Ticks(100);
            T.Check($"(b) HELD the spot -- no snapback ({Horiz(sess.Shell.TruePhysicsPosition, target):0.##} m from target)",
                    Horiz(sess.Shell.TruePhysicsPosition, target) < 1.5f && Horiz(sess.Shell.TruePhysicsPosition, spawn) > 20f);

            // the client-side name resolution the real F1 path runs before sending (game-side MapNodes)
            bool resolved = DevConsole.TryResolveTeleport("Stratford", out string wire, out string loc);
            T.Check($"DevConsole resolves a location name to the numeric wire form ('{wire}')",
                    resolved && loc == "Stratford" && wire == "teleport -67.46 38.66 -505.93");

            // teardown: unhook the pump so nothing touches the dying MemNetwork after QueueFree
            world.Sim.Sim.Remove(pump);
        }
    }

    // #38 regression: the MP vehicle puppet builds the INTERIOR steering wheel and DressWheels turns it
    // with the replicated steer angle (the SP _steerPivot rotation, Vehicle.cs). Pre-fix the puppet only
    // dressed the road wheels -- the driver stared at a frozen wheel. Covers both SP build forms: the
    // Parts-based steer part (jeep) and the dedicated SteerModel mesh (semi); the trailer (SteerAxis
    // Zero) must build none and stay null-safe through DressWheels.
    public class NetPuppetSteerModel : GameTest
    {
        public override string Name => "net.puppet_steer_model";
        public override IEnumerable<Step> Run()
        {
            var jeep = Vehicle.BuildPuppetByName("jeep", 0);
            var semi = Vehicle.BuildPuppetByName("semi", 0);
            var trailer = Vehicle.BuildPuppetByName("trailer", 0);
            World.AddChild(jeep); World.AddChild(semi); World.AddChild(trailer);
            yield return Ticks(1);

            T.Check("jeep puppet builds the Parts-based steering wheel in a pivot", jeep.SteerPivot != null && jeep.SteerPivot.GetChildCount() == 1);
            T.Check("semi puppet builds the dedicated SteerModel wheel", semi.SteerPivot != null && semi.SteerPivot.GetChildCount() == 1);
            T.Check("trailer puppet has no steer model", trailer.SteerPivot == null);

            jeep.DressWheels(20f, 3f, 0.02f);
            var expect = new Basis(new Vector3(0f, 0.259f, 0.966f).Normalized(), Mathf.DegToRad(20f));   // the jeep spec's SteerAxis (disc normal)
            T.Check("jeep steering wheel turned to the replicated 20 deg about the spec's SteerAxis", jeep.SteerPivot.Basis.IsEqualApprox(expect));
            T.Check("front road wheels still dress alongside (steer yaw applied)",
                jeep.Wheels[0].Steer && !jeep.Wheels[0].Pivot.Basis.IsEqualApprox(Basis.Identity));
            jeep.DressWheels(0f, 0f, 0.02f);
            T.Check("steering wheel returns to centre at steer 0", jeep.SteerPivot.Basis.IsEqualApprox(Basis.Identity));
            trailer.DressWheels(15f, 2f, 0.02f);   // must not throw with no steer model
            T.Check("trailer DressWheels stays null-safe", true);
        }
    }

    // #37 regression: while seated on a replicated vehicle puppet the camera must be LOOKABLE. FP (the
    // spawn default): mouse free-look yaws/pitches the view in VEHICLE-LOCAL space (real Unturned lets you
    // look around while driving); pre-fix the FP ride cam was hard-locked to the fixed forward gaze and
    // mouse motion was silently ignored. 3P (H toggle): the chase-cam orbit consumes _driveCamYaw/Pitch for
    // _riding. The look angles are driven through the Debug seams (headless hosts can't capture the mouse);
    // the H toggle goes through the REAL _UnhandledInput path.
    public class NetRideFreelook : GameTest
    {
        public override string Name => "net.ride_freelook";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0, 0.1f, 0));
            var pup = Vehicle.BuildPuppetByName("jeep", 0);
            World.AddChild(pup);
            pup.GlobalPosition = new Vector3(5f, 0.5f, 0f);
            pup.RotationDegrees = new Vector3(0f, 90f, 0f);   // yawed vehicle -> proves the look math is vehicle-LOCAL
            yield return Ticks(2);

            p.EnterPuppet(pup);
            yield return Ticks(3);
            // The headless L1 host doesn't interleave frame callbacks with the stepped physics ticks, so the
            // camera positioner (a _Process hook) is driven DIRECTLY -- deterministic, same code path as live.
            var cam = p.Camera;
            p._Process(0.016);
            T.Check("ride cam sits at the puppet's driver eye",
                cam.GlobalPosition.DistanceTo(pup.GlobalTransform * pup.DriverEyeLocal) < 0.05f);
            // entry gaze = the classic fixed gaze: vehicle-forward, pitched atan(0.6/3.9) ~ 8.75 deg down
            Vector3 fwd0 = pup.GlobalBasis * new Vector3(0f, -Mathf.Sin(Mathf.DegToRad(8.75f)), -Mathf.Cos(Mathf.DegToRad(8.75f)));
            T.Check("FP entry gaze looks over the hood (vehicle forward, slight down-tilt)",
                (-cam.GlobalBasis.Z).Dot(fwd0.Normalized()) > 0.999f);

            p.DebugSetRideLook(90f, 0f);   // yaw +90 = look LEFT of the vehicle
            p._Process(0.016);
            T.Check("FP free-look yaw 90 turns the view to the vehicle's left",
                (-cam.GlobalBasis.Z).Dot(pup.GlobalBasis * new Vector3(-1f, 0f, 0f)) > 0.999f);
            p.DebugSetRideLook(0f, 45f);   // pitch +45 = look up
            p._Process(0.016);
            Vector3 up45 = pup.GlobalBasis * new Vector3(0f, Mathf.Sin(Mathf.DegToRad(45f)), -Mathf.Cos(Mathf.DegToRad(45f)));
            T.Check("FP free-look pitch 45 tilts the view up", (-cam.GlobalBasis.Z).Dot(up45.Normalized()) > 0.999f);

            // H (through the real input path -- allowed while riding) -> 3P chase; the orbit must consume the vars
            p._UnhandledInput(new InputEventKey { Pressed = true, Keycode = Key.H });
            p.DebugSetDriveOrbit(0f, 15f);
            p._Process(0.016);
            Vector3 chase0 = cam.GlobalPosition;
            T.Check("H toggles to the 3P chase cam (behind the car, not at the eye)",
                chase0.DistanceTo(pup.GlobalTransform * pup.DriverEyeLocal) > 3f);
            p.DebugSetDriveOrbit(120f, 15f);
            p._Process(0.016);
            T.Check("3P orbit yaw moves the chase cam around the riding puppet",
                cam.GlobalPosition.DistanceTo(chase0) > 2f);

            p._UnhandledInput(new InputEventKey { Pressed = true, Keycode = Key.H });   // back to FP for teardown parity
            p.ExitPuppet(new Vector3(0f, 0.1f, 0f));
            yield return Ticks(1);
        }
    }

    // ---- CLIENT_PREDICTION_PLAN §3: the injected-RTT WAN harness (Phase 0 of Part C) ----
    // The named simulated-WAN link profiles, so "does this fix the 100 ms worm" is a number against a
    // fixed profile, not a vibe. At the 50 Hz tick (1 tick = 20 ms): Wan ~= 120-200 ms RTT with jitter +
    // 2% loss per direction (strawberry's ~100+ ms WAN); HarshWan ~= 200-280 ms + 5% loss (the bad-day
    // ceiling). The ReorderJitterTicks matter as much as the latency: a jitter-overtaken datagram is
    // dropped stale by the UnreliableSequenced channel, so pre-C1 every overtake was a HOLE in the
    // MoveInput stream the server had to guess across (coast/substitute on held axes) -- the residual
    // high-RTT inchworm's main engine (plan §4.1 H1).
    static class WanLink
    {
        public static void Wan(MemNetwork net)
        {
            net.ClientToServer.LatencyTicks = 3; net.ClientToServer.ReorderJitterTicks = 2; net.ClientToServer.LossProbability = 0.02;
            net.ServerToClient.LatencyTicks = 3; net.ServerToClient.ReorderJitterTicks = 2; net.ServerToClient.LossProbability = 0.02;
        }

        public static void HarshWan(MemNetwork net)
        {
            net.ClientToServer.LatencyTicks = 5; net.ClientToServer.ReorderJitterTicks = 2; net.ClientToServer.LossProbability = 0.05;
            net.ServerToClient.LatencyTicks = 5; net.ServerToClient.ReorderJitterTicks = 2; net.ServerToClient.LossProbability = 0.05;
        }

        public static void Clean(MemNetwork net)
        {
            net.ClientToServer.LatencyTicks = 0; net.ClientToServer.ReorderJitterTicks = 0; net.ClientToServer.LossProbability = 0;
            net.ServerToClient.LatencyTicks = 0; net.ServerToClient.ReorderJitterTicks = 0; net.ServerToClient.LossProbability = 0;
        }
    }

    // §3 Phase-0 baseline 1 -- the WAN patrol walk: WALK-stance legs with corner turns (yaw steps) and
    // brief stops, over the Wan profile, for ~28 simulated seconds. The metric is the reconciler's
    // CorrectionAppliedMeters normalized to a simulated minute: how many metres of rope-tug the owner
    // FELT. On the pre-C1/C2 code this FAILS -- the teeth baseline for the whole phase.
    public class NetShellWanWalk : GameTest
    {
        public override string Name => "net.shell_wan_walk";
        public override double TimeoutSimSeconds => 90;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(70707);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "wanwalker" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            yield return Ticks(25);   // settle the spawn transient before the link degrades

            // the WAN link comes up, then a patrol walk: 8 legs of [walk forward 3 s, stop 0.5 s, turn
            // 90 deg] -- the everyday walk shape (movement, corners, pauses), nothing adversarial
            WanLink.Wan(net);
            // v9 (mp-clientauth-foot): the corr/pending/hot/snap metrics measured the reconciler's
            // rope-tug -- the OWNER-visible correction the old two-body model produced (pre-fix 13.951
            // m/min on this exact rig/seed; the C1-C2 ladder got it to 0.570). Under client authority
            // the owner correction is structurally ZERO in normal play: the assert is now recovs == 0
            // (the envelope never trips on a legit WAN walk) + the entity tracks the shell.
            long recovsStart = sess.RecovsApplied;
            float maxLag = 0f;
            int windowTicks = 0;
            float yaw = 0f;
            for (int leg = 0; leg < 8; leg++)
            {
                sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                for (int i = 0; i < 150; i++)
                {
                    yield return Ticks(1);
                    windowTicks++;
                    if (sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var lagE))
                        maxLag = Mathf.Max(maxLag, (lagE.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude);
                }
                sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                for (int i = 0; i < 25; i++) { yield return Ticks(1); windowTicks++; }
                yaw += 90f;
                sess.Shell.RotationDegrees = new Vector3(0f, yaw, 0f);
            }
            GD.Print($"[wan-walk] {windowTicks} ticks: recovs={sess.RecovsApplied - recovsStart}, maxEntityLag={maxLag:0.###} m");
            T.Check($"ZERO recovs on a WAN walk ({sess.RecovsApplied - recovsStart}) -- the envelope never false-trips legit play", sess.RecovsApplied == recovsStart);
            T.Check($"the published entity tracked the shell (max lag {maxLag:0.###} m, ~one uplink of walk)", maxLag < 1.5f);
            T.Check($"DESYNC-QUIET across the WAN walk ({desyncs} fired)", desyncs == 0);

            // settle on a clean link -> exact wire-grid convergence, the standard closing bar
            WanLink.Clean(net);
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the walk (err {err:0.###} m)", own && err < 0.05f);

            world.Sim.Sim.Remove(pump);
        }
    }

    // §3 Phase-0 baseline 2 -- WAN sprint with direction changes and stops: the maneuvering shape the
    // §4.1 H1 hypothesis says the worm feeds on (every jitter-overtaken/lost MoveInput during an input
    // TRANSITION makes the server integrate motion the client never predicted; at sprint speed one wrong
    // tick is ~0.14 m). Five cycles of sprint-forward / strafe-weave / hard-stop over the Wan profile.
    // On the pre-C1/C2 code this FAILS -- the second teeth baseline.
    public class NetShellWanSprintTurns : GameTest
    {
        public override string Name => "net.shell_wan_sprint_turns";
        public override double TimeoutSimSeconds => 90;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(80808);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "wansprinter" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            yield return Ticks(25);

            WanLink.Wan(net);
            // v9 (mp-clientauth-foot): this course used to measure the sprint-stop yank + weave rope-tug
            // (pre-fix 30.644 m/min on this rig/seed; the C1-C2 ladder got it to 1.041). Under client
            // authority the owner-visible correction is structurally zero: the weave asserts recovs == 0
            // (full-speed direction changes + hard stops never trip the envelope) + entity tracking.
            long recovsStart = sess.RecovsApplied;
            float maxLag = 0f;
            int windowTicks = 0;
            // one weave segment: sprint at these axes for 30 ticks each -- direction changes every 0.6 s,
            // the strafe-dodge cadence of real play
            var weave = new[]
            {
                new UnityEngine.Vector2(0f, 1f), new UnityEngine.Vector2(1f, 1f), new UnityEngine.Vector2(0f, 1f),
                new UnityEngine.Vector2(-1f, 1f), new UnityEngine.Vector2(-1f, 0f), new UnityEngine.Vector2(0f, 1f),
            };
            for (int cycle = 0; cycle < 5; cycle++)
            {
                sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                foreach (var axes in weave)
                {
                    sess.Shell.ScriptedInput = axes;
                    for (int i = 0; i < 30; i++)
                    {
                        yield return Ticks(1);
                        windowTicks++;
                        if (sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var lagE))
                            maxLag = Mathf.Max(maxLag, (lagE.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude);
                    }
                }
                // hard stop -- the old model's yank moment; client-auth: nothing can yank the owner
                sess.Shell.ScriptedStance = null;
                sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                for (int i = 0; i < 40; i++) { yield return Ticks(1); windowTicks++; }
            }
            GD.Print($"[wan-sprint] {windowTicks} ticks: recovs={sess.RecovsApplied - recovsStart}, maxEntityLag={maxLag:0.###} m");
            T.Check($"ZERO recovs across the WAN sprint-weave ({sess.RecovsApplied - recovsStart}) -- full-speed maneuvering never trips the envelope", sess.RecovsApplied == recovsStart);
            T.Check($"the published entity tracked the sprinting shell (max lag {maxLag:0.###} m)", maxLag < 2.5f);
            T.Check($"DESYNC-QUIET across the WAN weave ({desyncs} fired)", desyncs == 0);

            // settle on a clean link -> exact wire-grid convergence, the standard closing bar
            WanLink.Clean(net);
            yield return Ticks(100);
            bool own = sess.Client.Players.TryGetByOwner(sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - ToU(sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            T.Check($"replicated own-entity CONVERGED after the weave (err {err:0.###} m)", own && err < 0.05f);

            world.Sim.Sim.Remove(pump);
        }
    }

    // ---- CLIENT_PREDICTION_PLAN §5 Part A: vehicle client authority (the felt-latency killer) ----

    // §5.4 L1 #1 -- THE FEEL BAR under WAN: the driver's wheel must answer in ~0 ticks regardless of RTT,
    // because the driver's client OWNS the driven vehicle's physics (retail client authority,
    // U3 InteractableVehicle.cs:1490-1519). TEETH (measured on pre-A code, this rig's WAN profile,
    // 2026-07-18): the ride-the-puppet driver's rendered vehicle first responded to a throttle step at
    // tick 20 (400 ms) -- server node first motion tick 11 (uplink + accel lag) + ~9 more ticks of
    // snapshot downlink + dead-reckon glide -- and NO local control surface existed at all (the wheel
    // "answered" only via the server round trip). Post-A the bar: a real local Vehicle whose
    // EngineForce/steering answer within 1 TICK of input, at any RTT.
    public class NetShellDrivePredicted : GameTest
    {
        public override string Name => "net.shell_drive_predicted";
        public override double TimeoutSimSeconds => 90;

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260718);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "preddriver" };
            World.AddChild(sess);
            var observer = new NetWorldClient(new MemClientTransport(net), "observer", contentHash: NetContent.Hash);
            var obsPump = new DelegateSimStep((t, dt) => observer.Tick(), "l1.obspump");
            world.Sim.Sim.Add(obsPump);
            var obsView = new VehicleReplicaView { Client = observer };
            World.AddChild(obsView);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int desyncs = 0, recovs = 0;
            sess.Client.DesyncDetected += _ => desyncs++;
            observer.DesyncDetected += _ => desyncs++;
            sess.Client.VehicleRecov += _ => recovs++;
            observer.Connect();

            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, -4f);

            yield return Until(() => sess.Shell != null && observer.State == NetSessionState.Connected, 5);
            yield return Until(() => sess.Client.Vehicles.Count == 1 && observer.Vehicles.Count == 1, 5);
            uint netId = 0;
            foreach (var e in sess.Client.Vehicles.All) { netId = e.NetIdValue; break; }
            T.Check("vehicle replicated to both clients", netId != 0);
            yield return Ticks(50);   // settle the spawn drop

            WanLink.Wan(net);   // the WHOLE drive runs at ~120-200 ms RTT -- the profile the pre-A teeth were measured on
            yield return Ticks(25);

            sess.Client.SendEnterVehicle(netId);
            yield return Until(() => sess.Shell.IsDriving, 5);
            var veh = sess.LocalVehicle;
            T.Check("the driver OWNS a real local Vehicle (retail client authority)", sess.Shell.IsDriving && veh != null);
            yield return Ticks(25);   // settle the seat; car at rest, no input

            // ---- THE FEEL BAR: input -> local control surface within 1 tick, at WAN RTT ----
            T.Check("pre-input: engine idle", Mathf.Abs(veh.EngineForce) < 0.001f);
            sess.Shell.ScriptedDrive = new Vector2(1f, 1f);   // (steer, throttle) step
            yield return Ticks(1);
            T.Check($"1 TICK after input the local ENGINE answered (EngineForce {veh.EngineForce:0.0}) -- pre-A: no local vehicle existed; rendered response tick 20 (400 ms)",
                    Mathf.Abs(veh.EngineForce) > 1f);
            yield return Ticks(2);
            T.Check($"the local STEERING is ramping within 3 ticks (steer {veh.SteerAngleDegrees:0.00} deg)",
                    Mathf.Abs(veh.SteerAngleDegrees) > 0.05f);
            // first visible motion: pre-A tick 20 under this profile; locally it is pure engine accel lag
            var restPos = veh.GlobalPosition;
            int firstMotion = -1;
            for (int i = 1; i <= 60 && firstMotion < 0; i++)
            {
                yield return Ticks(1);
                if (veh.GlobalPosition.DistanceTo(restPos) > 0.02f) firstMotion = i;
            }
            GD.Print($"[drive-predicted] first driver-visible motion {firstMotion} ticks after input (pre-A baseline: 20)");
            T.Check($"driver-visible motion begins at engine-accel lag only ({firstMotion} ticks -- pre-A 20 = accel + a full wire round trip)",
                    firstMotion > 0 && firstMotion <= 12);

            // ---- convergence: server entity + observer puppet follow the driver's track ----
            sess.Shell.ScriptedDrive = new Vector2(0.3f, 1f);   // gentle sustained turn
            yield return Ticks(200);
            float srvErr = jeep.GlobalPosition.DistanceTo(veh.GlobalPosition);
            T.Check($"the server node converged on the driver's track (err {srvErr:0.00} m -- adoption lag ~RTT/2 of top speed)", srvErr < 3.5f);
            bool havePup = obsView.TryGetPuppet(netId, out var pup);
            float pupErr = havePup ? pup.GlobalPosition.DistanceTo(jeep.GlobalPosition) : float.MaxValue;
            T.Check($"the observer's puppet tracks the adopted truth (err {pupErr:0.00} m)", havePup && pupErr < 3.5f);
            float driven = veh.GlobalPosition.DistanceTo(restPos);
            T.Check($"the drive actually went somewhere ({driven:0.0} m)", driven > 15f);
            T.Check($"recov-quiet: a legitimate WAN driver never trips the envelope ({recovs})", recovs == 0);

            // ---- exit: the shell restores at the SERVER's authoritative spot ----
            sess.Shell.ScriptedDrive = new Vector2(0f, 0f);
            yield return Ticks(100);
            T.Check("the exit seam fired", sess.Shell.RequestExitPuppet());
            yield return Until(() => !sess.Shell.IsDriving, 5);
            T.Check("shell restored + local vehicle destroyed", sess.Shell.Visible && sess.LocalVehicle == null);
            var doorFlat = sess.Shell.GlobalPosition - jeep.GlobalPosition; doorFlat.Y = 0f;
            T.Check($"...at the server spot beside the door ({doorFlat.Length():0.0} m from center)",
                    doorFlat.Length() > 1.0f && doorFlat.Length() < 8f);

            yield return Ticks(60);
            T.Check($"DESYNC-QUIET across the whole predicted drive ({desyncs} fired)", desyncs == 0);

            world.Sim.Sim.Remove(pump);
            world.Sim.Sim.Remove(obsPump);
            observer.Disconnect();
        }
    }

    // §5.4 L1 #2 -- the recov rollback lands END-TO-END: an out-of-envelope teleport injected into the
    // driver's local vehicle mid-drive (what a teleport cheat or a physics bug would produce) makes the
    // server refuse + roll the driver back to the last-good state (retail tellRecov), and driving RESUMES
    // cleanly afterwards. Without the envelope the server would adopt the 50 m jump verbatim (the L0
    // teeth); this proves the CLIENT side of the loop -- teleport-back + freeze + echo + resume.
    public class NetShellDriveRecov : GameTest
    {
        public override string Name => "net.shell_drive_recov";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            var net = new MemNetwork(20260719);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "recovdriver" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            int recovs = 0;
            sess.Client.VehicleRecov += _ => recovs++;

            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, -4f);

            yield return Until(() => sess.Shell != null, 5);
            yield return Until(() => sess.Client.Vehicles.Count == 1, 5);
            uint netId = 0;
            foreach (var e in sess.Client.Vehicles.All) { netId = e.NetIdValue; break; }
            yield return Ticks(50);
            sess.Client.SendEnterVehicle(netId);
            yield return Until(() => sess.Shell.IsDriving, 5);
            var veh = sess.LocalVehicle;
            T.Check("driving locally", veh != null);

            sess.Shell.ScriptedDrive = new Vector2(0f, 1f);
            yield return Ticks(100);   // up to speed, adoption flowing
            T.Check("pre-inject: server node tracks the local car", jeep.GlobalPosition.DistanceTo(veh.GlobalPosition) < 2f);

            // INJECT: a 50 m teleport -- far past the envelope (cap = 12.5 x 0.5 s x 1.25 = 7.8 m even at
            // the clamped max interval). The next state packet reports it; the server must refuse.
            var beforeJump = veh.GlobalPosition;
            veh.GlobalPosition = beforeJump + new Vector3(50f, 0f, 0f);
            yield return Until(() => recovs > 0, 3);
            T.Check($"the recov event landed on the driver ({recovs})", recovs > 0);
            yield return Ticks(5);   // the echo send + release happen on the next DriveStep
            float rollbackErr = veh.GlobalPosition.DistanceTo(jeep.GlobalPosition);
            T.Check($"ROLLBACK: the local vehicle teleported back onto the server's last-good ({rollbackErr:0.00} m)",
                    rollbackErr < 3f);
            T.Check("the local vehicle resumed (unfrozen) after the RecovAck echo", !veh.Freeze && !veh.NetHeld);

            // driving resumes: the same held throttle keeps working and the server adopts again
            var resumeFrom = veh.GlobalPosition;
            yield return Ticks(150);
            float resumed = veh.GlobalPosition.DistanceTo(resumeFrom);
            T.Check($"driving RESUMED after the rollback ({resumed:0.0} m)", resumed > 8f);
            T.Check($"the server keeps adopting the resumed track (err {jeep.GlobalPosition.DistanceTo(veh.GlobalPosition):0.00} m)",
                    jeep.GlobalPosition.DistanceTo(veh.GlobalPosition) < 3f);
            T.Check($"exactly one recov (the discard window never double-fires) ({recovs})", recovs == 1);

            world.Sim.Sim.Remove(pump);
        }
    }

    // §5.4 L1 #3 -- the authority handoff: enter -> the server node freezes under adoption; exit -> the
    // node resumes REAL physics seeded from the last adopted state (retail removePlayer -> updatePhysics)
    // and the SP exit effects park it; driver DISCONNECT mid-drive frees the seat + releases the hold the
    // same way (NetWorldHost.OnPeerDisconnected -> ServerExit).
    public class NetShellDriveHandoff : GameTest
    {
        public override string Name => "net.shell_drive_handoff";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            var net = new MemNetwork(20260720);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "handoff" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(0f, 1.2f, -4f);

            yield return Until(() => sess.Shell != null, 5);
            yield return Until(() => sess.Client.Vehicles.Count == 1, 5);
            uint netId = 0;
            foreach (var e in sess.Client.Vehicles.All) { netId = e.NetIdValue; break; }
            yield return Ticks(50);

            // ---- exit handoff ----
            sess.Client.SendEnterVehicle(netId);
            yield return Until(() => sess.Shell.IsDriving, 5);
            sess.Shell.ScriptedDrive = new Vector2(0f, 1f);
            yield return Until(() => jeep.NetHeld, 5);
            T.Check("under adoption the server node is HELD (frozen, teleport-following the driver)", jeep.NetHeld && jeep.Freeze);
            yield return Ticks(150);   // up to speed
            float speedAtExit = sess.LocalVehicle != null ? sess.LocalVehicle.LinearVelocity.Length() : 0f;
            T.Check($"at speed before the exit ({speedAtExit:0.0} m/s)", speedAtExit > 5f);
            // exit WHILE at full throttle -- the seeded-velocity assert needs real speed at handoff, and it
            // must be read on the FIRST freed ticks: the freed wheels resume at rotation speed 0, so the
            // seeded ~10 m/s skid-brakes away in ~0.2 s (the net.vehicle_freeze_hold spike's documented shape)
            T.Check("exit requested", sess.Shell.RequestExitPuppet());
            float maxFreedV = 0f;
            for (int i = 0; i < 55 && (sess.Shell.IsDriving || i < 8); i++)
            {
                yield return Ticks(1);
                if (!jeep.NetHeld) maxFreedV = Mathf.Max(maxFreedV, jeep.LinearVelocity.Length());
            }
            T.Check("exit landed", !sess.Shell.IsDriving);
            T.Check("HANDOFF: the node unfroze (authority back to the server)", !jeep.NetHeld && !jeep.Freeze);
            T.Check($"...seeded with the last adopted velocity (peak freed v {maxFreedV:0.0} m/s -- not a dead stop)", maxFreedV > 2f);
            var exitSpot = jeep.GlobalPosition;
            yield return Ticks(200);
            T.Check($"...then SETTLED under real physics + the SP exit park (v {jeep.LinearVelocity.Length():0.00} m/s)",
                    jeep.LinearVelocity.Length() < 1f);
            T.Check("the shell stands beside the car, back on the walk plane", sess.Shell.Visible && !sess.Shell.IsDriving
                    && sess.Shell.GlobalPosition.DistanceTo(exitSpot) < 12f);

            // ---- disconnect handoff ----
            yield return Until(() => sess.Shell.RequestEnterNearestPuppet(), 5);   // the freed seat is takeable again (walk-up range)
            yield return Until(() => sess.Shell.IsDriving, 5);
            sess.Shell.ScriptedDrive = new Vector2(0f, 1f);
            yield return Until(() => jeep.NetHeld, 5);
            yield return Ticks(100);
            T.Check("second drive held + moving", jeep.NetHeld && jeep.GlobalPosition.DistanceTo(exitSpot) > 3f);
            sess.Client.Disconnect();
            yield return Ticks(25);
            bool freed = ded.Server.Vehicles.TryGet(netId, out var fe) && fe.DriverPlayerId == 0;
            T.Check("DISCONNECT freed the seat (OnPeerDisconnected -> ServerExit)", freed);
            T.Check("...and released the hold -- the node is the server's again", !jeep.NetHeld && !jeep.Freeze);
            yield return Until(() => jeep.LinearVelocity.Length() < 0.8f, 8);
            T.Check($"the abandoned car settled under server physics (v {jeep.LinearVelocity.Length():0.00} m/s)",
                    jeep.LinearVelocity.Length() < 0.8f);

            world.Sim.Sim.Remove(pump);
        }
    }

    // ---- the four geometry WAN courses (originally PREDICTION_GEOMETRY_DIAGNOSIS §8's teeth) ----
    // The flat WAN baselines above run on the no-map fallback world -- zero decision cliffs; these four
    // put OBSTACLES in the path (step-ups / doorway / thin collider / jumps at 100+ ms WAN). Harness
    // fact that makes the add minimal: in this rig the ClientWorldSession shell and the DedicatedServer
    // follower body live in ONE World tree = one physics space, so a test-local StaticBody3D is seen by
    // both -- no WorldBuilder change.
    //
    // v9 (mp-clientauth-foot) REWROTE the assert set: these courses used to measure the reconciler's
    // rope-tug (corr/min, pending spikes, worst-tick tug, snaps -- the two-body divergence rendered as
    // owner correction). Under client authority there IS no server sim of the owner and no reconciler
    // -- the two-body fork these numbers measured cannot exist. The client-auth reality each course
    // asserts instead: ZERO recovs (the envelope never false-trips on legit geometry play at WAN -- the
    // key non-regression; recov is reserved for genuine violations), the own-entity converges bit-exact
    // on a clean link, and the desync detector stays quiet. The easing/replay-era metric bars were
    // deleted WITH the machinery they measured, not weakened.
    static class WanGeo
    {
        /// <summary>A static obstacle: layer 0, the WorldBuilder object-collider rule
        /// (WorldBuilder.cs:226) -- the player capsule's mask is (1&lt;&lt;0)|(1&lt;&lt;6).</summary>
        public static StaticBody3D Box(Node3D world, Vector3 center, Vector3 size)
        {
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(body);
            body.GlobalPosition = center;
            return body;
        }

        public static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);

        /// <summary>Per-tick client-auth observables over a scripted course: call Sample() after every
        /// tick. Recovs = the owner-correction count (0 in normal play, at any RTT); MaxLag = the worst
        /// published-entity-vs-shell distance (bounded by ~uplink RTT x speed -- observers render this
        /// far behind the owner, they never see a wrong position).</summary>
        public sealed class Metrics
        {
            readonly ClientWorldSession _s;
            readonly long _recovs0;
            public int Ticks; public float MaxLag;
            public Metrics(ClientWorldSession s) { _s = s; _recovs0 = s.RecovsApplied; }
            public void Sample()
            {
                Ticks++;
                if (_s.Shell != null && GodotObject.IsInstanceValid(_s.Shell)
                    && _s.Client.Players.TryGetByOwner(_s.Client.PlayerId, out var e))
                {
                    float lag = (e.Pos - ToU(_s.Shell.TruePhysicsPosition)).magnitude;
                    if (lag > MaxLag) MaxLag = lag;
                }
            }
            public long Recovs => _s.RecovsApplied - _recovs0;
        }
    }

    // The NetShellWanWalk boot skeleton, shared by the four geometry courses: flat fallback world +
    // MemNetwork + ClientWorldSession shell + DedicatedServer avatar host, spawn-settled and facing -Z.
    sealed class WanGeoRig
    {
        public WorldBuildResult World;
        public MemNetwork Net;
        public ClientWorldSession Sess;
        public DedicatedServer Ded;
        public DelegateSimStep Pump;
        public int Desyncs;
        public Vector3 Org;                                    // shell feet after the spawn settle
        public readonly Vector3 Fwd = new Vector3(0f, 0f, -1f);    // shell forward at yaw 0 (input (0,1) walks -Z)
        public readonly Vector3 Right = new Vector3(1f, 0f, 0f);

        public IEnumerable<Step> Boot(GameTest test, int seed, string playerName)
        {
            var task = WorldBuilder.BuildFullWorld(test.World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            World = task.Result;
            test.T.Check("world ready (the ONE world path, flat fallback on CI)", World.Ready);

            Net = new MemNetwork(seed);
            Pump = new DelegateSimStep((t, dt) => Net.Tick(), "l1.netpump");
            World.Sim.Sim.Add(Pump);
            Sess = new ClientWorldSession { Driver = World.Sim, TransportOverride = new MemClientTransport(Net), PlayerName = playerName };
            test.World.AddChild(Sess);
            Ded = new DedicatedServer { Driver = World.Sim, TransportOverride = new MemServerTransport(Net), RemoteAvatars = true };
            test.World.AddChild(Ded);
            Desyncs = 0;
            Sess.Client.DesyncDetected += _ => Desyncs++;

            yield return Step.Until(() => Sess.Shell != null, 5);
            test.T.Check("shell spawned on the first authoritative own-entity sample", Sess.Shell != null);
            if (Sess.Shell == null) yield break;
            yield return Step.Ticks(25);   // settle the spawn transient before the link degrades
            Sess.Shell.RotationDegrees = new Vector3(0f, 0f, 0f);
            yield return Step.Ticks(1);
            Org = Sess.Shell.GlobalPosition;
        }

        /// <summary>How far along the course axis the shell currently is (metres from Org toward Fwd).</summary>
        public float Along() => (Sess.Shell.GlobalPosition - Org).Dot(Fwd);

        /// <summary>Face the shell at a world point (yaw only) -- facing is client-authoritative input,
        /// so steering by rotation is a legal course move (position is never written).</summary>
        public void FaceToward(Vector3 point)
        {
            var d = point - Sess.Shell.GlobalPosition;
            Sess.Shell.RotationDegrees = new Vector3(0f, Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z)), 0f);
        }

        /// <summary>The standard client-auth closing pattern: ZERO recovs across the course (the envelope
        /// never trips on legit play -- THE course bar), desync-quiet, then clean-link convergence (the
        /// published entity IS the shell's own claim, bit-exact once the stream settles).</summary>
        public IEnumerable<Step> Close(GameTest test, WanGeo.Metrics m, string course)
        {
            GD.Print($"[{course}] {m.Ticks} ticks: recovs={m.Recovs}, maxEntityLag={m.MaxLag:0.###} m");
            test.T.Check($"ZERO recovs across the course ({m.Recovs}) -- client-auth: no owner correction in normal play", m.Recovs == 0);
            test.T.Check($"DESYNC-QUIET across the course ({Desyncs} fired)", Desyncs == 0);
            WanLink.Clean(Net);
            yield return Step.Ticks(100);
            bool own = Sess.Client.Players.TryGetByOwner(Sess.Client.PlayerId, out var e);
            float err = own ? (e.Pos - WanGeo.ToU(Sess.Shell.TruePhysicsPosition)).magnitude : float.MaxValue;
            test.T.Check($"replicated own-entity CONVERGED after the course (err {err:0.###} m)", own && err < 0.05f);
            World.Sim.Sim.Remove(Pump);
        }
    }

    // §8 baseline 1 -- the curb course: two "curbs" across the path (0.15 m sidewalk lip, 0.30 m step --
    // both under StepHeight 0.5), crossed 16 times each way over the Wan profile at maneuvering cadence
    // (diagonal approaches, weave transitions, stop-starts at the lips). The §4-1 mechanism: StepUp is a
    // BINARY +0.5 m raise; a transition mispredicted in a starve/jitter window near the lip skews the two
    // bodies, a diagonal block slides them apart ALONG the face, and the step-up points fork.
    // PRE-FIX (mp-geomfix @ P0, this exact rig/seed 90909): corrApplied 2.843 m over 2307 ticks = 3.697
    // m/min, maxPending 0.145 m, worstTickCorr 0.050 m, snaps 0 -- 6.5x the flat wan_walk's 0.570 m/min,
    // a steady curb-crossing correction drizzle. FAILS the corr/min bar (the spike bars pass today and
    // ride along as guards). Measured honesty: the §8-scripted STRAIGHT crossing course measured a
    // PASSING 0.338 m/min -- square-on, the curb face clamps both bodies to the same spot, so the
    // pullback needs transitions at the lip, exactly the everyday shape strawberry maneuvers in.
    // POST-P2 (F3 swept corrections + slice cap + F4): corr 3.377 m/min (unchanged -- the residue is
    // pure §4-1 binary-StepUp forks, the F6 gate), maxPending 0.145, worstTickCorr 0.049. The spike
    // bars ARM here (P2 owns them: pre-cap the swept-blocked pending piled to 0.562 and released as a
    // 0.098 tug).
    // F6 SPIKE VERDICT: the doc's de-binarized StepUp FAILED its spike -- the minimal clearing height
    // at a graze is a knife-edge function of lateral offset (the hydrant baseline blew up: 30.763
    // m/min + 2 snaps) and the curb number still missed the bar (2.989). What survived is the
    // real-step gate (binary raise, only onto a walkable raised top): hydrant 7.824 -> 5.471 with 0
    // snaps, stepup 3.488 (unchanged). The remaining stepup drizzle is two-body step/slide TIMING skew
    // at maneuvering cadence -- rewind/replay territory: the corr/min bar is C3-gated (§7).
    public class NetShellWanStepUp : GameTest
    {
        public override string Name => "net.shell_wan_stepup";
        public override double TimeoutSimSeconds => 160;

        public override IEnumerable<Step> Run()
        {
            var rig = new WanGeoRig();
            foreach (var s in rig.Boot(this, 90909, "wanstepper")) yield return s;
            if (rig.Sess.Shell == null) yield break;

            float floor = rig.Org.Y;
            Vector3 At(float d, float h) { var c = rig.Org + rig.Fwd * d; return new Vector3(c.X, floor + h / 2f, c.Z); }
            WanGeo.Box(World, At(4f, 0.15f), new Vector3(16f, 0.15f, 1f));   // the sidewalk lip
            WanGeo.Box(World, At(9f, 0.30f), new Vector3(16f, 0.30f, 1f));   // the tall curb (still under StepHeight)

            WanLink.Wan(rig.Net);
            var m = new WanGeo.Metrics(rig.Sess);
            // The course puts input TRANSITIONS at the lips, not just crossings: a straight-line walk
            // keeps the avatar's count-preserving input integration aligned (measured 0.338 m/min -- the
            // curbs alone don't fork two aligned bodies), but a direction change or stop-start landing in
            // a starve/jitter window near the lip is mispredicted by the coast machinery, and THAT skew
            // hits §4-1's binary StepUp cliff. Weave cadence from the flat sprint baseline (direction
            // change ~every 0.3 s) + a hard stop at each lip + a weaving crossing.
            int wDir = 1;
            IEnumerable<Step> Leg(bool sprint, bool outbound)
            {
                float a = outbound ? 4f : 9f, b = outbound ? 9f : 4f;
                System.Func<float, float> dist = curb => outbound ? curb - rig.Along() : rig.Along() - curb;
                var pace = sprint ? EPlayerStance.SPRINT : (EPlayerStance?)null;
                foreach (var curb in new[] { a, b })
                {
                    // DIAGONAL approach (+-35 deg, alternating): squared-up, the curb face clamps both
                    // bodies to the same spot (the wall resyncs them, measured 0.338 m/min); hit at an
                    // angle, a blocked body keeps sliding ALONG the face, so skew accumulates while
                    // blocked and the two bodies' step-up points fork -- the §4-1 cliff, as players
                    // actually clip sidewalks
                    wDir = -wDir;
                    rig.Sess.Shell.RotationDegrees = new Vector3(0f, (outbound ? 0f : 180f) + wDir * 35f, 0f);
                    rig.Sess.Shell.ScriptedStance = pace;
                    rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                    int weave = 0, guard = 0;
                    while (dist(curb) > 0.45f && guard++ < 250)
                    {
                        if (++weave % 15 == 0) rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(wDir * 0.35f, 1f);
                        else if (weave % 15 == 8) rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                        yield return Step.Ticks(1); m.Sample();
                    }
                    rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;   // the stop transient AT the lip
                    rig.Sess.Shell.ScriptedStance = null;
                    for (int i = 0; i < 10; i++) { yield return Step.Ticks(1); m.Sample(); }
                    rig.Sess.Shell.ScriptedStance = pace;
                    rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                    int cross = sprint ? 26 : 40;   // ~3-3.6 m: up the lip diagonally, across the 1 m top, off the far side
                    for (int i = 0; i < cross; i++) { yield return Step.Ticks(1); m.Sample(); }
                }
                rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                rig.Sess.Shell.ScriptedStance = null;
                for (int i = 0; i < 12; i++) { yield return Step.Ticks(1); m.Sample(); }
            }
            for (int phase = 0; phase < 2; phase++)   // walk pace, then sprint pace
                for (int trip = 0; trip < 4; trip++)
                    for (int half = 0; half < 2; half++)
                        foreach (var s in Leg(phase == 1, half == 0)) yield return s;

            // v9: the corr/pending/tug/snap bars measured the two-body reconciler -- deleted with it.
            // The client-auth bars (zero recovs + convergence + desync-quiet) live in rig.Close.
            foreach (var s in rig.Close(this, m, "wan-stepup")) yield return s;
        }
    }

    // §8 baseline 2 -- the doorway: a 0.9 m gap between two wall slabs (capsule diameter 0.7), a lintel
    // over the gap whose underside is at y=1.9 (under the 2.0 stand capsule -- forces a crouch), the
    // approach offset 0.2 m from the gap centre (guaranteed jamb slide). Crouch-through-stand-up each
    // way, walking then sprinting. The §4-3 compound: jamb slide-side picks + StepUp/FloorSnap threshold
    // interplay + the avatar-side headroom re-gate (§3 asymmetry 2: the avatar re-derives the stance the
    // client already resolved, at a slightly different position -- a CROUCH-vs-STAND disagreement is a
    // 2.5 vs 4.5 m/s speed fork for the window it lasts).
    // PRE-FIX (mp-geomfix @ P0, seed 91919): corrApplied 1.750 m over 2304 ticks = 2.278 m/min,
    // maxPending 0.426 m, worstTickCorr 0.074 m, snaps 0 -- 4x the flat wan_walk, with the doorway's
    // signature 0.4 m correction spike. FAILS the corr/min and maxPending bars.
    // POST-P2 (F3 swept corrections + slice cap + F4 wire-stance trust): corr 1.957 m/min (bar 2 --
    // passes; margin is thin because the residue is the jamb slide-side pick, the same knife-edge
    // family as the hydrant), worstTickCorr 0.060, maxPending 0.440 (the one bifurcation event per
    // course -- the C3-class spike, stays soft). POST-F6-SPIKE (real-step gate): 1.800 m/min.
    public class NetShellWanDoorway : GameTest
    {
        public override string Name => "net.shell_wan_doorway";
        public override double TimeoutSimSeconds => 160;

        public override IEnumerable<Step> Run()
        {
            var rig = new WanGeoRig();
            foreach (var s in rig.Boot(this, 91919, "wandoorman")) yield return s;
            if (rig.Sess.Shell == null) yield break;

            float floor = rig.Org.Y;
            var doorC = rig.Org + rig.Fwd * 4f - rig.Right * 0.2f;   // gap centre 0.2 m off the walk line
            WanGeo.Box(World, new Vector3(doorC.X, floor + 1.25f, doorC.Z) + rig.Right * 1.95f, new Vector3(3f, 2.5f, 0.3f));
            WanGeo.Box(World, new Vector3(doorC.X, floor + 1.25f, doorC.Z) - rig.Right * 1.95f, new Vector3(3f, 2.5f, 0.3f));
            WanGeo.Box(World, new Vector3(doorC.X, floor + 2.0f, doorC.Z), new Vector3(0.9f, 0.2f, 0.3f));   // lintel: underside y=1.9

            WanLink.Wan(rig.Net);
            var m = new WanGeo.Metrics(rig.Sess);
            for (int phase = 0; phase < 2; phase++)
            {
                var pace = phase == 0 ? (EPlayerStance?)null : EPlayerStance.SPRINT;
                for (int trip = 0; trip < 6; trip++)
                    for (int half = 0; half < 2; half++)
                    {
                        // out: approach at pace, crouch ~0.7 m before the jamb plane, stand back up ~0.4 m
                        // past the lintel (position-triggered so WAN drift can't decay the engagement --
                        // the stand-up landing near the trailing edge is what pokes the headroom re-gate)
                        bool outbound = half == 0;
                        float door = 4f;
                        System.Func<float> to = () => outbound ? door - rig.Along() : rig.Along() - door;
                        rig.Sess.Shell.RotationDegrees = new Vector3(0f, outbound ? 0f : 180f, 0f);
                        rig.Sess.Shell.ScriptedStance = pace;
                        rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                        int guard = 0;
                        while (to() > 0.7f && guard++ < 200) { yield return Ticks(1); m.Sample(); }
                        rig.Sess.Shell.ScriptedStance = EPlayerStance.CROUCH;
                        guard = 0;
                        while (to() > -0.4f && guard++ < 200) { yield return Ticks(1); m.Sample(); }
                        rig.Sess.Shell.ScriptedStance = pace;                       // stand up just past the lintel
                        guard = 0;
                        while (to() > -3.9f && guard++ < 200) { yield return Ticks(1); m.Sample(); }
                        rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                        rig.Sess.Shell.ScriptedStance = null;
                        for (int i = 0; i < 12; i++) { yield return Ticks(1); m.Sample(); }
                    }
            }

            // v9: the reconciler metric bars are deleted with the reconciler (see WanGeo header).
            foreach (var s in rig.Close(this, m, "wan-doorway")) yield return s;
        }
    }

    // §8 baseline 3 -- the "hydrant": a 0.12 x 0.6 x 0.12 m post at +5 m, sprinted into dead-centre and
    // at knife-edge +-0.02 m lateral offsets, held against for 1 s each engagement. The §4-2 mechanism:
    // against a thin collider the slide DIRECTION (left vs right of the face) is decided by which side
    // of the centre the capsule lands on -- a knife-edge on the sub-band (<=0.08 m) offset the two
    // bodies always carry, after which they diverge LATERALLY at full speed until the post is cleared
    // ("my server position TWEAKED out"). §4 predicts this one still fails after F1-F4 -- it is the C3
    // (rewind+replay) gate.
    // PRE-FIX (mp-geomfix @ P0, seed 92929): corrApplied 5.468 m over 1530 ticks = 10.722 m/min,
    // maxPending 0.851 m, worstTickCorr 0.148 m, snaps 0 -- the "tweaked out" report reproduced: ~19x
    // the flat wan_walk, with 0.85 m lateral divergence spikes as the two bodies pick different sides.
    // FAILS the corr/min, maxPending and worst-tick bars.
    // POST-P2 (F1-F4 + slice cap): 7.824 m/min, maxPending 1.20, worstTickCorr 0.061 -- the worst-tick
    // TUG is tamed but the slide-side bifurcation itself is untouched, exactly as §4-2 predicted: no
    // cheap fix picks the same side twice. POST-F6-SPIKE (the real-step StepUp gate): 5.471 m/min,
    // maxPending 0.609 -- the binary raise no longer fires on step-less grazes past the post, the
    // course's best numbers yet. The residue is pure slide-side bifurcation: the C3 (rewind+replay)
    // gate; metric bars stay soft.
    public class NetShellWanThinCollider : GameTest
    {
        public override string Name => "net.shell_wan_thincollider";
        public override double TimeoutSimSeconds => 120;

        public override IEnumerable<Step> Run()
        {
            var rig = new WanGeoRig();
            foreach (var s in rig.Boot(this, 92929, "wanhydrant")) yield return s;
            if (rig.Sess.Shell == null) yield break;

            float floor = rig.Org.Y;
            var hydC = rig.Org + rig.Fwd * 5f;
            var hyd = new Vector3(hydC.X, floor + 0.3f, hydC.Z);
            WanGeo.Box(World, hyd, new Vector3(0.12f, 0.6f, 0.12f));

            WanLink.Wan(rig.Net);
            var m = new WanGeo.Metrics(rig.Sess);
            for (int run = 0; run < 9; run++)
            {
                // aim at the post with a -0.02 / 0 / +0.02 m lateral offset (the knife edge each side),
                // re-aimed from wherever the last run left us so drift self-corrects every lane
                float off = ((run % 3) - 1) * 0.02f;
                rig.FaceToward(new Vector3(hyd.X, floor, hyd.Z) + rig.Right * off);
                rig.Sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                for (int i = 0; i < 40; i++) { yield return Ticks(1); m.Sample(); }   // ~5.6 m: reach + hit
                for (int i = 0; i < 50; i++) { yield return Ticks(1); m.Sample(); }   // hold forward 1 s against it
                rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                rig.Sess.Shell.ScriptedStance = null;
                for (int i = 0; i < 10; i++) { yield return Ticks(1); m.Sample(); }
                // sidestep off the post (alternating sides), then sprint home for the next lane
                rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(run % 2 == 0 ? 1f : -1f, 0f);
                for (int i = 0; i < 12; i++) { yield return Ticks(1); m.Sample(); }
                rig.FaceToward(rig.Org);
                rig.Sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                for (int i = 0; i < 46; i++) { yield return Ticks(1); m.Sample(); }
                rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                rig.Sess.Shell.ScriptedStance = null;
                for (int i = 0; i < 12; i++) { yield return Ticks(1); m.Sample(); }
            }

            // v9: the slide-side bifurcation this course used to measure (two bodies picking different
            // sides of the knife edge) CANNOT EXIST anymore -- there is one body. The course keeps its
            // value as an envelope non-regression: grinding a thin collider at sprint must never recov.
            foreach (var s in rig.Close(this, m, "wan-thincollider")) yield return s;
        }
    }

    // P3 holiday parity (PREDICTION_GEOMETRY_DIAGNOSIS §2 footnote 1, wire v6): ~285 placed props carry
    // COLLIDERS and are gated by activeHoliday, which each side derived from its LOCAL wall clock -- a
    // client across a holiday boundary silently built a DIFFERENT static collision set the content-hash
    // join gate never catches (it hashes content identity, not the clock decision). The server's holiday
    // now rides the Accept, and the client's holiday-gated world content is DEFERRED at build and placed
    // by WorldBuildResult.ApplyHoliday with the SERVER's string (ClientWorldSession invokes it once, on
    // the first Connected tick). This test proves the full wire + plumbing: the callback fires exactly
    // once, with the server's holiday, before the shell spawns -- a value nothing client-side knows.
    // (The placement parity itself is by construction: ApplyHoliday replays the SAME PlaceObject body the
    // inline build runs; the flat CI world has no objects to place, so that half can't run here.)
    public class NetShellHolidayHandshake : GameTest
    {
        public override string Name => "net.shell_holiday_handshake";
        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(95959);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            string applied = null; int calls = 0; bool shellExistedAtApply = false;
            ClientWorldSession sess = null;
            sess = new ClientWorldSession
            {
                Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "wanclaus",
                ApplyServerHoliday = h => { applied = h; calls++; shellExistedAtApply = sess.Shell != null; },
            };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                                            RemoteAvatars = true, ActiveHoliday = "CHRISTMAS" };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            T.Check($"the client built the SERVER's holiday world ('{applied}')", applied == "CHRISTMAS");
            T.Check($"the deferred holiday content applied exactly once ({calls})", calls == 1);
            T.Check("and BEFORE the shell spawned (colliders exist before the player stands among them)", !shellExistedAtApply);
            yield return Ticks(20);
            T.Check($"no re-application on later ticks ({calls})", calls == 1);

            world.Sim.Sim.Remove(pump);
        }
    }

    // §8 baseline 4 -- jumps: (a) 16 sprint-jump arcs on flat with the jump key held ACROSS landings and
    // released mid-air (bunny-hop cadence -- every arc carries a held-bit landing edge and a release
    // edge for the §5-B coast/hole re-present machinery to chew under Wan jitter), then (b) 8
    // sprint-jumps with takeoff pulsed ~1.2 m short of a 0.15 m curb lip (§5-A: the takeoff-tick skew
    // where the two bodies' _detGrounded flags disagree). The extra bar is the worst VERTICAL pending
    // error -- the "server teleports me to the apex" C1.5-window signature (ground->apex in one
    // snapshot when the repay-drain exits mid-arc).
    // PRE-FIX (mp-geomfix @ P0, seed 93939): corrApplied 20.610 m over 1649 ticks = 37.496 m/min,
    // maxPending 0.791 m, maxPendingY 0.791 m (the apex teleport: ~an entire 0.83 m arc of vertical
    // error in one snapshot step), worstTickCorr 0.137 m, snaps 0 -- the WORST of the four courses,
    // 66x the flat wan_walk, exactly §5's ranking of jumps as the worst felt item. FAILS all four
    // metric bars.
    // POST-P1 (F1 takeoff-edge + strip + deferral, F2 grounded-tolerance, F2b phase join): 3.323
    // m/min, maxPending 0.402, maxPendingY 0.402, worstTickCorr 0.070 -- 11x better, worst-tick bar
    // now passes. The residue was traced tick-by-tick to §4-4, NOT a jump mechanism: an eased downward
    // correction slice lands the capsule fractionally inside the floor on the landing tick, the next
    // tick's StepUp foot-sweep reads the stuck capsule as "blocked at foot" and fires the +0.5 m raise
    // ON FLAT GROUND -- a phantom step-up that relaunches every arc ~0.4 m high. That is F3's bug
    // (corrections applied through geometry), so the bars arm with P2/F3.
    // POST-P2 (F3 killed the phantom flat-ground StepUp; slice cap): corr 1.665 m/min (22x under
    // pre-fix), worstTickCorr 0.060, snaps 0. POST-F6-SPIKE (the real-step StepUp gate): 1.444 m/min,
    // maxPending 0.160, maxPendingY 0.110, worstTickCorr 0.048 -- the curb-landing step pops were the
    // spike source, so ALL bars arm here (26x under pre-fix overall).
    public class NetShellWanJump : GameTest
    {
        public override string Name => "net.shell_wan_jump";
        public override double TimeoutSimSeconds => 120;

        public override IEnumerable<Step> Run()
        {
            var rig = new WanGeoRig();
            foreach (var s in rig.Boot(this, 93939, "wanjumper")) yield return s;
            if (rig.Sess.Shell == null) yield break;
            float floor = rig.Org.Y;

            WanLink.Wan(rig.Net);
            var m = new WanGeo.Metrics(rig.Sess);

            // (a) bunny-hop: a flat arc is 24-25 ticks; hold the key across the landing (t = 20..8 of
            // each 25-tick period) and release mid-air (t = 9..19) -- 16 arcs, 16 held landings, 16
            // release edges riding the Wan link's loss/reorder/starve windows
            rig.Sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
            rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            for (int i = 0; i < 400; i++)
            {
                int t = i % 25;
                rig.Sess.Shell.ScriptedJump = t >= 20 || t <= 8;
                yield return Ticks(1); m.Sample();
            }
            rig.Sess.Shell.ScriptedJump = false;
            rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
            rig.Sess.Shell.ScriptedStance = null;
            for (int i = 0; i < 30; i++) { yield return Ticks(1); m.Sample(); }
            GD.Print($"[wan-jump] phase (a) hops: recovs={m.Recovs}, maxEntityLag={m.MaxLag:0.###} m");

            // (b) the curb: placed NOW, relative to where the hop run ended (one shared physics space --
            // both bodies see it the same tick; inserted while stopped, converged and 6 m away)
            var start = rig.Sess.Shell.GlobalPosition;
            var curbC = start + rig.Fwd * 6f;
            WanGeo.Box(World, new Vector3(curbC.X, floor + 0.075f, curbC.Z), new Vector3(8f, 0.15f, 1f));
            float CurbAlong() => (rig.Sess.Shell.GlobalPosition - start).Dot(rig.Fwd);
            for (int rep = 0; rep < 8; rep++)
            {
                // out: sprint at the curb, takeoff pulsed ~1.2 m short of the lip (position-triggered)
                rig.Sess.Shell.RotationDegrees = new Vector3(0f, 0f, 0f);
                rig.Sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                int guard = 0;
                while (CurbAlong() < 4.8f && guard++ < 90) { yield return Ticks(1); m.Sample(); }
                rig.Sess.Shell.ScriptedJump = true;
                for (int i = 0; i < 3; i++) { yield return Ticks(1); m.Sample(); }
                rig.Sess.Shell.ScriptedJump = false;
                for (int i = 0; i < 28; i++) { yield return Ticks(1); m.Sample(); }   // fly the arc, land on/past the curb
                rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                rig.Sess.Shell.ScriptedStance = null;
                for (int i = 0; i < 12; i++) { yield return Ticks(1); m.Sample(); }
                // back: sprint home flat-footed (no jump) for the next rep
                rig.FaceToward(start);
                rig.Sess.Shell.ScriptedStance = EPlayerStance.SPRINT;
                rig.Sess.Shell.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                guard = 0;
                while (CurbAlong() > 0.4f && guard++ < 120) { yield return Ticks(1); m.Sample(); }
                rig.Sess.Shell.ScriptedInput = UnityEngine.Vector2.zero;
                rig.Sess.Shell.ScriptedStance = null;
                for (int i = 0; i < 12; i++) { yield return Ticks(1); m.Sample(); }
            }

            // v9: the apex-teleport signature (ground->apex pending in one snapshot) was a two-body
            // artifact -- gone with the server sim. The jump envelope non-regression is rig.Close's
            // zero-recov bar: bunny-hops + curb takeoffs must never trip the vertical caps.
            foreach (var s in rig.Close(this, m, "wan-jump")) yield return s;
        }
    }
}
