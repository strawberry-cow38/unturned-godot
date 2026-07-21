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
        public DestructibleField Destructibles;      // optional (WorldBuildResult.Destructibles): the rubble alive-bitmap index space
        public string MapRoot;                       // optional: loads the 19 nav pockets as relevancy cells (§2.6)
        public string ActiveHoliday = "NONE";        // P3 (wire v6): the holiday THIS world was built with -- rides the Accept so joiners build the same holiday-gated props/colliders
        public bool SurvivalDrain = false;           // B5 (SP/MP-unify): server-authoritative hunger/thirst + starvation + passive regen. OFF by default = SP byte-identical coarse-HP path (strawberry runs survival off); flip on for a survival server.
        public System.Collections.Generic.List<FixtureRecord> Fixtures;   // A3: world power fixtures (Circuit_0 grid sources) recorded by WorldBuilder -> ServerPlaced into the deployable graph at boot (mains OFF)
        public System.Collections.Generic.List<(string mesh, int table, bool display, string label, Godot.Vector3 pos, float yaw)> Containers;   // A1: world-build container manifest -> ContainerNetSync registers each as a server-owned fixture + stocks its grid
        public GasStationServer GasStation { get; private set; }          // A2: authoritative per-station fuel tanks (built from the placed gas-pump fixtures; the ExtractFuel choke drains them)
        public ServerSentries Sentries { get; private set; }              // sentry fixtures: the server-authoritative scan/fire/kill (the view-only replica just renders)
        public ServerTraps Traps { get; private set; }                    // trap fixtures: the server-authoritative edge-trigger/bite/landmine (the view-only replica just renders)
        public ServerBeacon Beacon { get; private set; }                  // beacon fixtures: the server-authoritative horde spawn/track/reward (the view-only replica just renders the obelisk)

        public NetWorldServer Server { get; private set; }
        public PlayerNetSync PlayerSync { get; private set; }
        public ZombieNetSync ZombieSync { get; private set; }
        public AnimalNetSync AnimalSync { get; private set; }   // A5: publishes AnimalAgent brains (no-op until AnimalField's streamer is PlayerRegistry-generalized for dedicated)
        public PlayerAppearanceNetSync AppearanceSync { get; private set; }   // B10: publishes each player's worn clothing + stance into the combat block
        public WorldItemNetSync WorldItemSync { get; private set; }
        public VehicleNetSync VehicleSync { get; private set; }
        public WorldClockNetSync ClockSync { get; private set; }
        public CropNetSync CropSync { get; private set; }
        public ContainerNetSync ContainerSync { get; private set; }   // A1: publishes world-build containers as server-owned fixtures + display digests
        public ResourceNetSync ResourceSync { get; private set; }
        public DestructibleNetSync DestructibleSync { get; private set; }

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
                contentHash: NetContent.Hash,    // §2.2: joiners with a different content identity are rejected
                activeHoliday: ActiveHoliday);   // P3: joiners build THIS world's holiday props/colliders, not their own clock's
            Server.EnableSyncCheck();   // hardening Part C: 1 Hz rolling StateHash block -> clients self-check for desync
            Server.Vitals.SurvivalDrain = SurvivalDrain;   // B5: default false = the coarse-HP path is byte-untouched (no starvation, no passive regen)
            Server.Session.PeerConnected += peer => GD.Print($"[DEDICATED] player {peer.PlayerId} '{peer.Name}' joined ({Server.Session.Peers.Count} online)");
            Server.Session.PeerDisconnected += (peer, reason) => GD.Print($"[DEDICATED] player {peer.PlayerId} left ({reason})");
            // MP pickup Step 4 (decision, ITEM_PICKUP_WIRING_PLAN §4.1): joiners get the DEMO KIT granted
            // into the SERVER grid -- the same bag the client shell always showed locally, now authoritative
            // (without it the owner-block adoption would empty the bag at join and reload/consume would have
            // nothing to chew on). Game-side only, so every core L0 harness stays byte-identical. Runs here
            // in PeerConnected -- after core's Inventories.ServerAdd (subscribed first, in the NetWorldServer
            // ctor) and BEFORE the join snapshot composes in TickReplication, so the kit rides the join
            // snapshot. In a catalog-less fallback boot the grants degrade to 1x1/no-bag -- harmless.
            Server.Session.PeerConnected += peer =>
            {
                if (Server.Inventories.TryGet(peer.PlayerId, out var inv))
                    PlayerController.PopulateDemoKit(inv.Inventory);
            };
            // Phase 6: the transactional slice's def tables -- the same DeployableDef/blueprint data every
            // client build carries (content-hash-matched), feeding placement validation + the server solve.
            DeployableNetSchema.RegisterAll(Server.Deployables.Schema);
            Server.Transactions.Blueprints = BlueprintRegistry.All;
            Server.Transactions.AllowCheats = AllowCheats;   // SECURITY (review C1): default OFF on the public dedicated server -- no client may run the give/xp/skill console cheats. Tests/admins opt in via the AllowCheats field; a real per-connection admin gate is future work.
            // A3 (SP/MP-unify): server-place the recorded grid-power fixtures into the deployable graph, in
            // deterministic map-file order, mains default OFF (ToggledOn = false). They ride SystemDeployables
            // to every joiner, whose DeployableReplicaView materializes a GridPowerSource node. NetIds minted
            // server-side only (§2.6). Registered AFTER the schema above so ServerPlace resolves DefId 9200.
            // A2 (SP/MP-unify): the authoritative fuel-station tanks (built from the gas-pump fixtures below).
            GasStation = new GasStationServer();
            if (Fixtures != null)
                foreach (var f in Fixtures)
                {
                    var fe = Server.Deployables.ServerPlace(Server.Ids.Mint(), f.DefId, 0,
                        new UnityEngine.Vector3(f.Pos.X, f.Pos.Y, f.Pos.Z), f.YawDegrees, Server.Session.CurrentTick);
                    // A2: a placed gas pump joins its shared station tank + seeds its replicated full percent.
                    if (fe != null && DeployableDef.ById(f.DefId)?.Fixture == FixtureKind.GasPump)
                        GasStation.RegisterPump(fe, f.StationId, Server.Deployables, Server.Session.CurrentTick);
                }
            Server.Transactions.FuelStations = GasStation;   // A2: the ExtractFuel choke drains the tanks through this seam
            // Phase 5 combat hooks: server bullets/blasts stop at the world's real geometry, grenades
            // bounce on real ground height. Both are optional seams on the engine-free ServerCombat.
            Server.Combat.WorldRay = GodotWorldRay;
            // P3a (SP/MP-unify): PvP is now ON (the ServerCombat default). Server-authoritative player damage
            // is owned + rendered on the owner shell: the coarse Health replica drives the HUD via
            // AdoptReplicatedVitals, PlayerDied/PlayerRespawned render death/respawn on the owner, and the
            // respawn reposition rides the recov/freeze-until-echo primitive. Landed ATOMICALLY with that
            // adoption -- flipping it alone (the old D1 posture removed here) would rubber-band an unrendered
            // death, which is exactly why it was held false until now.
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
            // sentry fixtures fire server-authoritatively: scan the authoritative zombies -> ServerCombat.ZombieHost
            // (ZombieNetSync, set above) applies the kill. Runs AFTER the zombie publish so it sees the live state.
            Sentries = new ServerSentries(Server.Zombies, Server.Deployables, Server.Combat);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Sentries.Tick(tick, (float)dt), "net.sentries.tick"));
            // trap fixtures edge-trigger server-authoritatively against the just-published zombies (same ZombieHost seam).
            Traps = new ServerTraps(Server.Zombies, Server.Deployables, Server.Combat);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Traps.Tick(tick, (float)dt), "net.traps.tick"));
            // beacon fixtures spawn their horde as real server ZombieControllers into the world root; ZombieNetSync
            // (net.zombies.publish, above) auto-mints + publishes each -- a freshly-spawned member lands on the next tick.
            Beacon = new ServerBeacon(Server.Zombies, Server.Deployables, GetParent() ?? (Node)this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Beacon.Tick(tick, (float)dt), "net.beacon.tick"));
            AnimalSync = new AnimalNetSync(Server, this);   // A5: publish wildlife brains (currently a no-op on dedicated -- see the AnimalField note above)
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => AnimalSync.Tick(), "net.animals.publish"));
            AppearanceSync = new PlayerAppearanceNetSync(Server);   // B10: publish each connected player's worn clothing + stance into the combat block
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => AppearanceSync.Tick(), "net.appearance.publish"));
            // world-item nodes (LootField streaming etc.) -> WorldItemReplication at 5 Hz (§3.3)
            WorldItemSync = new WorldItemNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => WorldItemSync.Tick(), "net.worlditems.publish"));
            // vehicle nodes -> VehicleReplication publish + remote DriveInput onto Vehicle.Drive (§3.6, Phase 7)
            VehicleSync = new VehicleNetSync(Server, this);
            VehicleSync.RegisterCommands();   // B11: tow tie/untie handlers on _server.Commands (game-side -- they mutate real Vehicle nodes)
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => VehicleSync.Tick(), "net.vehicles.sync"));
            // Phase 8 world state (§3.7): tick-derived day-night, crops, the resource alive-bitmap
            ClockSync = new WorldClockNetSync(Server, DayNight, driveFromTick: true);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ClockSync.Tick(), "net.worldclock.sync"));
            CropSync = new CropNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => CropSync.Tick(), "net.crops.sync"));
            ContainerSync = new ContainerNetSync(Server, this, Containers);   // A1: register + publish the world-build containers as server-owned fixtures
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ContainerSync.Tick(), "net.containers.publish"));
            ResourceSync = new ResourceNetSync(Server, Resources);
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => ResourceSync.Tick(), "net.resources.sync"));
            DestructibleSync = new DestructibleNetSync(Server, Destructibles);   // seed health/respawn + mirror rubble alive-bits
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => DestructibleSync.Tick(), "net.destructibles.sync"));
            Driver.Sim.Add(new DelegateSimStep((tick, dt) => Replicate(tick), "net.server.replicate"));   // LAST (MP_PLAN §2.5)
        }

        // Server-side bullet/LoS raycast against static world geometry (terrain/buildings on layer 0,
        // see-through props on 6) -- runs inside the SimRoot's _PhysicsProcess, where space access is safe.
        bool GodotWorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point, out int destructibleIndex)
        {
            point = default;
            destructibleIndex = -1;
            var world = GetViewport()?.World3D;
            if (world == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = world.DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            if (hit["collider"].As<GodotObject>() is StaticBody3D body && body.HasMeta(DestructibleField.MetaKey))
                destructibleIndex = (int)body.GetMeta(DestructibleField.MetaKey);
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
