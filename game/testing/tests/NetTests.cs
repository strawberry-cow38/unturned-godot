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
}
