using Godot;
using SDG.NetTransport;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // The dedicated server's net host (MP_PLAN §4 Phase 3): wraps the engine-free NetWorldServer
    // (NetServerSession + CommandRegistry + PlayerReplication + SnapshotComposer) and registers its two
    // tick phases on the world's SimRoot in §2.5 order -- simulation (receive + input-apply + player sim)
    // first, replication (snapshot compose/send) LAST. Boots from Main's --dedicated after WorldBuilder
    // has assembled the dedicated world; the L1 net.dedicated_boot test injects a MemServerTransport.
    public partial class DedicatedServer : Node
    {
        public ushort Port = 47872;
        public SimDriver Driver;                     // the world's sim spine (WorldBuildResult.Sim)
        public IServerTransport TransportOverride;   // tests inject MemTransport; null = real UDP
        public bool AllowCheats = false;             // SECURITY (review C1): give/xp/skill console cheats. OFF by default on the public server; tests set true to exercise the give plane.
        public bool RemoteAvatars = false;           // C2: PlayerNetSync avatar bodies (real spawns/collision/jump) for remote peers. Opt-in: Main.BuildDedicated turns it ON for the real server; default OFF keeps every pre-C2 harness (flat IntegrateFlat movement, ServerTeleport-driven tests) byte-identical.
        public Terrain Terr;                         // optional (WorldBuildResult.Terr): grounds server grenade bounces on real terrain
        public DayNightCycle DayNight;               // optional (WorldBuildResult.DayNight): tick-derived day-night (§3.7)
        public ResourceField Resources;              // optional (WorldBuildResult.Resources): the §3.7 alive-bitmap index space
        public string MapRoot;                       // optional: loads the 19 nav pockets as relevancy cells (§2.6)

        public NetWorldServer Server { get; private set; }
        public PlayerNetSync PlayerSync { get; private set; }
        public ZombieNetSync ZombieSync { get; private set; }
        public WorldItemNetSync WorldItemSync { get; private set; }
        public VehicleNetSync VehicleSync { get; private set; }
        public WorldClockNetSync ClockSync { get; private set; }
        public CropNetSync CropSync { get; private set; }
        public ResourceNetSync ResourceSync { get; private set; }

        long _lastStatusTick;

        public override void _Ready()
        {
            // Cap the headless main loop: with no FPS limit Godot spins the idle loop as fast as the CPU
            // allows and burns a whole core even at 0 players (nothing to render, but it never sleeps). The
            // sim + netcode run on the fixed 50 Hz physics accumulator, independent of this cap, so 60 leaves
            // ample headroom while the process idles instead of busy-waiting. Only the REAL dedicated server
            // (real UDP transport); L1 hosts inject a MemTransport and pump ticks themselves.
            if (TransportOverride == null) Engine.MaxFps = 60;

            // net diagnostics (hardening Part B): route the engine-free NetLog through Godot so it lands in
            // journald; OFF unless UG_NETLOG=1 or --netlog (zero overhead when off -- call sites are gated)
            NetLog.Sink = s => GD.Print(s);
            NetLog.ErrorSink = s => GD.PrintErr(s);
            if (System.Environment.GetEnvironmentVariable("UG_NETLOG") == "1") NetLog.Enabled = true;

            Server = new NetWorldServer(TransportOverride ?? new UdpServerTransport(Port),
                (conn, reason, isError) => GD.Print($"[DEDICATED] connection dropped ({conn.GetAddressString(true)}): {reason}"),
                contentHash: NetContent.Hash);   // §2.2: joiners with a different content identity are rejected
            Server.EnableSyncCheck();   // hardening Part C: 1 Hz rolling StateHash block -> clients self-check for desync
            Server.Session.PeerConnected += peer => GD.Print($"[DEDICATED] player {peer.PlayerId} '{peer.Name}' joined ({Server.Session.Peers.Count} online)");
            Server.Session.PeerDisconnected += (peer, reason) => GD.Print($"[DEDICATED] player {peer.PlayerId} left ({reason})");
            // Phase 6: the transactional slice's def tables -- the same DeployableDef/blueprint data every
            // client build carries (content-hash-matched), feeding placement validation + the server solve.
            DeployableNetSchema.RegisterAll(Server.Deployables.Schema);
            Server.Transactions.Blueprints = BlueprintRegistry.All;
            Server.Transactions.AllowCheats = AllowCheats;   // SECURITY (review C1): default OFF on the public dedicated server -- no client may run the give/xp/skill console cheats. Tests/admins opt in via the AllowCheats field; a real per-connection admin gate is future work.
            // Phase 5 combat hooks: server bullets/blasts stop at the world's real geometry, grenades
            // bounce on real ground height. Both are optional seams on the engine-free ServerCombat.
            Server.Combat.WorldRay = GodotWorldRay;
            Server.Combat.PvPEnabled = false;   // D1 (PEI_COMBAT_PLAN §3): players safe -- shell vitals are still local, so server-side player damage would only rubber-band an unrendered death. Removed in D2.
            if (Terr != null) Server.Combat.GroundHeight = (x, z) => Terr.SampleHeight(x, z);
            // C6 (§7 risk 6): the vehicle-exit teleport spot has no ground snap in core -- on a hillside the
            // beside-the-door point can land INSIDE the slope and drop the avatar through the world. Lift a
            // below-terrain exit onto the surface; an above-ground exit (bridge, crest) just falls, like SP.
            if (Terr != null)
                Server.VehicleHost.AdjustExitSpot = p =>
                {
                    float h = Terr.SampleHeight(p.x, p.z);
                    return p.y < h + 0.1f ? new UnityEngine.Vector3(p.x, h + 0.5f, p.z) : p;
                };

            // Phase 8 interest policy (§2.6): distance rings for the world entities, plus the 19 PEI nav
            // pockets as relevancy cells -- a town's whole horde stays relevant while a client is in that
            // town. Fallback worlds (no map) have no pockets: rings only.
            var pockets = MapRoot != null ? ZombieNav.LoadPockets(MapRoot) : new System.Collections.Generic.List<NavPocket>();
            System.Func<UnityEngine.Vector3, int> cellOf = pos =>
            {
                var p = new Vector3(pos.x, pos.y, pos.z);
                for (int i = 0; i < pockets.Count; i++) if (pockets[i].Box.HasPoint(p)) return i;
                return -1;
            };
            Server.Zombies.Interest = new InterestPolicy { RingRadius = 192f, CellOf = cellOf };
            Server.WorldItems.Interest = new InterestPolicy { RingRadius = 128f, CellOf = cellOf };

            // C2 real spawns: joiners land on real Spawns/Players.dat points at terrain height (+1.5 m drop),
            // spread deterministically by playerId. Fallback worlds (no map/terrain) keep the default demo
            // origin line -- every pre-C2 test spawn is byte-identical.
            if (Terr != null && MapRoot != null)
            {
                var spawns = LevelSpawns.PlayerSpawns(MapRoot);
                if (spawns.Count > 0)
                    Server.SpawnProvider = playerId =>
                    {
                        var pick = spawns[(playerId - 1) % spawns.Count];
                        return new UnityEngine.Vector3(pick.x, Terr.SampleHeight(pick.x, pick.z) + 1.5f, pick.z);
                    };
            }

            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Server.TickSimulation(), "net.server.sim"));
            // C2 remote avatar bodies: real PlayerController physics per peer, written back through
            // ServerDrive -- registered right after the sim step (input is dispatched) and before every
            // publish sync, so published state (zombie targets etc.) sees this tick's driven positions.
            if (RemoteAvatars)
            {
                PlayerSync = new PlayerNetSync(Server, this);
                Driver.Sim.Add(new DelegateSimStep((tick, dt) => PlayerSync.Tick(), "net.players.sync"));
            }
            // zombie brains -> ZombieReplication at 12.5 Hz (§3.5), BEFORE the snapshot send
            ZombieSync = new ZombieNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ZombieSync.Tick(), "net.zombies.publish"));
            // world-item nodes (LootField streaming etc.) -> WorldItemReplication at 5 Hz (§3.3)
            WorldItemSync = new WorldItemNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => WorldItemSync.Tick(), "net.worlditems.publish"));
            // vehicle nodes -> VehicleReplication publish + remote DriveInput onto Vehicle.Drive (§3.6, Phase 7)
            VehicleSync = new VehicleNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => VehicleSync.Tick(), "net.vehicles.sync"));
            // Phase 8 world state (§3.7): tick-derived day-night, crops, the resource alive-bitmap
            ClockSync = new WorldClockNetSync(Server, DayNight, driveFromTick: true);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ClockSync.Tick(), "net.worldclock.sync"));
            CropSync = new CropNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => CropSync.Tick(), "net.crops.sync"));
            ResourceSync = new ResourceNetSync(Server, Resources);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ResourceSync.Tick(), "net.resources.sync"));
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Replicate(tick), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        // Server-side bullet/LoS raycast against static world geometry (terrain/buildings on layer 0,
        // see-through props on 6) -- runs inside the SimRoot's _PhysicsProcess, where space access is safe.
        bool GodotWorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point)
        {
            point = default;
            var world = GetViewport()?.World3D;
            if (world == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = world.DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            return true;
        }

        void Replicate(long tick)
        {
            Server.TickReplication();
            if (tick - _lastStatusTick >= 500)   // 10 s heartbeat so a headless console shows life
            {
                _lastStatusTick = tick;
                GD.Print($"[DEDICATED] tick {Server.Session.CurrentTick} | players {Server.Session.Peers.Count} | snapshots full={Server.Composer.Diag.FullSnapshotsComposed} delta={Server.Composer.Diag.DeltaSnapshotsComposed}");
            }
        }

        public override void _ExitTree() => Server?.TearDown();
    }
}
