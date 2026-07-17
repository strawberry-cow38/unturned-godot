using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// The server side of the Phase 3+ world stack, engine-free: NetServerSession (handshake/reliability)
    /// + CommandRegistry (ack + MoveInput + the Phase 5 combat commands) + the replicated systems
    /// (players / player-combat / zombies / projectiles) + SnapshotComposer, wired the way MP_PLAN §2.5
    /// orders a tick. The host (SimRoot registration in game/, or a test harness) calls the two phases in
    /// order every 50 Hz tick:
    ///   TickSimulation()  -- receive datagrams, dispatch commands (input-apply), step player movement,
    ///                        step server combat (bullets/melee timers/grenades/respawns)
    ///   TickReplication() -- compose + send per-client snapshots; MUST run after all state mutation
    ///                        (registered LAST on the SimRoot, §2.5 "replication send last")
    /// Snapshots go out every SnapshotDivisorTicks-th tick (2 = the plan's 25 Hz default). Zombie state is
    /// published INTO ZombieReplication by the game-side ZombieNetSync at 12.5 Hz (§2.5 cadence table).
    /// </summary>
    public sealed class NetWorldServer
    {
        public readonly NetServerSession Session;
        public readonly CommandRegistry Commands = new CommandRegistry();
        public readonly NetIdMinter Ids = new NetIdMinter();
        public readonly PlayerReplication Players = new PlayerReplication();
        public readonly PlayerCombatReplication CombatState = new PlayerCombatReplication();
        public readonly ZombieReplication Zombies = new ZombieReplication();
        public readonly ProjectileReplication Projectiles = new ProjectileReplication();
        // Phase 6 -- the transactional slice (MP_PLAN §4 Phase 6)
        public readonly SkillsReplication Skills = new SkillsReplication();
        public readonly DeployableReplication Deployables = new DeployableReplication();
        public readonly InventoryReplication Inventories = new InventoryReplication();
        public readonly WorldItemReplication WorldItems = new WorldItemReplication();
        // Phase 7 -- vehicles (MP_PLAN §3.6)
        public readonly VehicleReplication Vehicles = new VehicleReplication();
        // Phase 8 -- world state (MP_PLAN §3.7)
        public readonly WorldClockReplication Clock = new WorldClockReplication();
        public readonly CropReplication Crops = new CropReplication();
        public readonly ResourceReplication Resources = new ResourceReplication();
        public readonly ServerVehicles VehicleHost;
        public readonly ServerCombat Combat;
        public readonly ServerTransactions Transactions;
        public readonly SnapshotComposer Composer;

        public int SnapshotDivisorTicks = 2; // 25 Hz at the 50 Hz tick (MP_PLAN §2.5)

        /// <summary>Where a joining peer's player entity spawns (PEI_CLIENT_PLAN §3 C2). Defaults to the
        /// demo origin line so every existing test/demo world is byte-untouched; the dedicated server
        /// overrides it with real Spawns/Players.dat points at terrain height.</summary>
        public System.Func<ushort, Vector3> SpawnProvider = SpawnPosition;

        /// <summary>XP per credited kill (the §3.2 award hook, wired through ServerCombat.KillCredited).
        /// Default 0 = award nothing, because the SP port awards no kill XP yet either -- MP must not
        /// out-reward the direct path. Bump both together when kill XP lands in SP.</summary>
        public uint KillExperience = 0;

        public NetWorldServer(IServerTransport transport,
                              ServerTransportConnectionFailureCallback connectionFailureCallback = null,
                              int maxPeers = 32,
                              ulong contentHash = 0)
        {
            Session = new NetServerSession(transport, connectionFailureCallback, maxPeers: maxPeers, contentHash: contentHash);
            Composer = new SnapshotComposer(new IReplicatedSystem[] { Players, CombatState, Zombies, Projectiles,
                                                                      Skills, Deployables, Inventories, WorldItems,
                                                                      Vehicles, Clock, Crops, Resources });
            Composer.CurrentTick = () => Session.CurrentTick;   // review L1: rejects acks of future ticks
            Composer.RegisterAck(Commands);
            Combat = new ServerCombat(Players, CombatState, Zombies, Projectiles, Ids, BroadcastEvent, SendEventTo);
            Transactions = new ServerTransactions(Players, CombatState, Skills, Inventories, WorldItems, Deployables,
                                                  Ids, () => Session.CurrentTick, BroadcastEvent, SendEventTo,
                                                  Crops, Resources);
            Transactions.Register(Commands);
            VehicleHost = new ServerVehicles(Vehicles, Players, CombatState, () => Session.CurrentTick, BroadcastEvent);
            VehicleHost.Register(Commands);
            Combat.KillCredited = killer => { if (KillExperience > 0) Transactions.AwardXp(killer, KillExperience); };
            Commands.Register<MoveInput>(ReplicationIds.CommandMoveInput, MoveInput.TryRead,
                (sender, cmd) => Players.ServerQueueInput(sender, cmd),
                // a corpse's inputs drop at the choke point; so do a DRIVER's (the seat teleport owns the
                // avatar while driving -- walked positions must not fight it, §3.6)
                validate: (sender, cmd) => CombatState.IsAlive(sender) && !VehicleHost.IsDriver(sender));
            Commands.Register<FireCommand>(ReplicationIds.CommandFire, FireCommand.TryRead,
                (sender, cmd) => Combat.OnFire(sender, cmd, Session.CurrentTick));
            Commands.Register<MeleeCommand>(ReplicationIds.CommandMelee, MeleeCommand.TryRead,
                (sender, cmd) => Combat.OnMelee(sender, cmd, Session.CurrentTick));
            Commands.Register<GrenadeCommand>(ReplicationIds.CommandGrenade, GrenadeCommand.TryRead,
                (sender, cmd) => Combat.OnGrenade(sender, cmd, Session.CurrentTick));
            Commands.Register<ReloadCommand>(ReplicationIds.CommandReload, ReloadCommand.TryRead,
                (sender, cmd) => Combat.OnReload(sender, cmd, Session.CurrentTick));

            Session.PeerConnected += peer =>
            {
                var spawn = SpawnProvider(peer.PlayerId);
                var e = Players.ServerSpawn(Ids.Mint(), peer.PlayerId, spawn, Session.CurrentTick);
                CombatState.ServerAdd(peer.PlayerId, e.Pos, Combat.GunFor(peer.PlayerId).MagCapacity, Session.CurrentTick);
                // Phase 6 per-player authoritative state -- added BEFORE the join snapshot composes below,
                // so the joiner's own owner-only skills/inventory blocks ride the join snapshot too.
                Skills.ServerAdd(peer.PlayerId, Session.CurrentTick);
                Inventories.ServerAdd(peer.PlayerId, Session.CurrentTick);
                // The join flow (MP_PLAN §4 Phase 4): Accept -> reliable FULL snapshot -> deltas. The full
                // snapshot rides ReliableOrdered where fragmentation is safe (§2.2) -- a lost datagram
                // retransmits instead of the client waiting out unreliable full-resends. Composed in
                // TickReplication (NOT here): PeerConnected fires mid-TickSimulation, and a snapshot
                // composed here would miss anything else this same tick still mutates -- most concretely a
                // SECOND same-tick joiner, whose entities are stamped with this very tick and therefore
                // never make a later delta once this client acks it (found by the Part C desync detector:
                // two simultaneous joiners each permanently missing the other's combat entity). §2.5's
                // "replication send last" applies to the join snapshot too.
                _pendingJoinSnapshots.Add(peer);
            };
            Session.PeerDisconnected += (peer, reason) =>
            {
                _pendingJoinSnapshots.Remove(peer);   // joined and vanished within the same tick
                VehicleHost.OnPeerDisconnected(peer.PlayerId);   // frees the seat BEFORE the player entity goes
                Players.ServerRemove(peer.PlayerId, Session.CurrentTick);
                CombatState.ServerRemove(peer.PlayerId, Session.CurrentTick);
                Skills.ServerRemove(peer.PlayerId);
                Inventories.ServerRemove(peer.PlayerId, Session.CurrentTick);   // also releases any crate they held open
                Composer.ForgetClient(peer.PlayerId);
                // Phase 8 rejoin hardening: per-client relevancy sets must not leak to a recycled playerId
                Zombies.ForgetClient(peer.PlayerId);
                WorldItems.ForgetClient(peer.PlayerId);
            };
        }

        // deterministic joins-in-a-row spacing so demo avatars don't spawn inside each other
        static Vector3 SpawnPosition(ushort playerId) => new Vector3(((playerId - 1) % 8) * 2f, 0f, 0f);

        /// <summary>Turn on desync detection (hardening Part C): every intervalTicks the snapshot carries a
        /// StateHash per globally-mirrored system (players / player-combat / projectiles / deployables /
        /// vehicles / clock / crops / resources -- NOT the owner-only or relevancy-filtered ones, which
        /// differ per client by design). Clients compare after applying and raise DesyncDetected on a
        /// confirmed mismatch. Default cadence 50 = one check per second; the block is 74 bytes.</summary>
        public void EnableSyncCheck(int intervalTicks = 50)
        {
            Composer.EnableSyncCheck(intervalTicks,
                ReplicationIds.SystemPlayers, ReplicationIds.SystemPlayerCombat, ReplicationIds.SystemProjectiles,
                ReplicationIds.SystemDeployables, ReplicationIds.SystemVehicles, ReplicationIds.SystemWorldClock,
                ReplicationIds.SystemCrops, ReplicationIds.SystemResources);
        }

        /// <summary>Reliable event to every connected peer (the event plane, §2.3).</summary>
        public void BroadcastEvent(byte[] message)
        {
            foreach (var peer in Session.Peers) peer.SendReliable(message);
        }

        /// <summary>Reliable event to one player (e.g. the shooter's HitConfirm).</summary>
        public void SendEventTo(ushort playerId, byte[] message)
        {
            Session.FindPeer(playerId)?.SendReliable(message);
        }

        /// <summary>Receive + input-apply + player sim + combat step (§2.5 order: input-apply, player sim,
        /// then combat/projectiles).</summary>
        public void TickSimulation()
        {
            Session.Tick();
            foreach (var peer in Session.Peers)
            {
                while (peer.TryReceiveReliable(out byte[] msg)) Commands.TryDispatch(msg, peer.PlayerId);
                while (peer.TryReceiveUnreliable(out byte[] msg)) Commands.TryDispatch(msg, peer.PlayerId);
            }
            Players.ServerStep(Session.CurrentTick, (float)SimClock.FixedDelta);
            VehicleHost.Step(Session.CurrentTick);   // drivers ride their vehicle entity; dead drivers exit
            Combat.Step(Session.CurrentTick);
            // stamp this tick onto every inventory the dispatch round dirtied (owner-block delta baseline)
            Inventories.ServerCommitDirty(Session.CurrentTick);
            if (NetLog.Enabled) LogRollupIfDue();
        }

        // ---- net-diag traffic rollup (hardening Part B): one line per second aggregating the per-peer
        // session counters + composer + command registry. Peers leaving take their counters with them, so
        // each delta clamps at 0 rather than going negative across a disconnect. Zero cost unless NetLog
        // is enabled (the call itself is gated). ----
        long _lastRollupTick;
        long _rlDgIn, _rlDgOut, _rlBytesIn, _rlBytesOut, _rlRetx, _rlUnkRej, _rlMalRej, _rlValRej,
             _rlSnapFull, _rlSnapDelta, _rlSnapBytes, _rlSkips, _rlReasmDrop;

        void LogRollupIfDue()
        {
            if (Session.CurrentTick - _lastRollupTick < NetProtocol.TicksPerSecond) return;
            _lastRollupTick = Session.CurrentTick;

            long dgIn = 0, dgOut = 0, bytesIn = 0, bytesOut = 0, retx = 0, reasmDrop = 0;
            foreach (var peer in Session.Peers)
            {
                var d = peer.Session.Diag;
                dgIn += d.DatagramsReceived; dgOut += d.DatagramsSent;
                bytesIn += d.BytesReceived; bytesOut += d.BytesSent;
                retx += d.ReliableRetransmits;
                reasmDrop += d.ReassemblyOverflowDropped + d.ReassemblyEvicted;
            }
            long D(long cur, ref long prev) { long delta = System.Math.Max(0, cur - prev); prev = cur; return delta; }

            NetLog.Sink($"[NET] 1s: peers {Session.Peers.Count} (half-open {Session.HalfOpenCount})" +
                        $" | pkts in {D(dgIn, ref _rlDgIn)} out {D(dgOut, ref _rlDgOut)}" +
                        $" | bytes in {D(bytesIn, ref _rlBytesIn)} out {D(bytesOut, ref _rlBytesOut)}" +
                        $" | retx {D(retx, ref _rlRetx)}" +
                        $" | cmd rej unk {D(Commands.Diag.UnknownIdRejected, ref _rlUnkRej)}" +
                        $" mal {D(Commands.Diag.MalformedRejected, ref _rlMalRej)}" +
                        $" val {D(Commands.Diag.ValidationRejected, ref _rlValRej)}" +
                        $" | snaps full {D(Composer.Diag.FullSnapshotsComposed, ref _rlSnapFull)}" +
                        $" delta {D(Composer.Diag.DeltaSnapshotsComposed, ref _rlSnapDelta)}" +
                        $" bytes {D(Composer.Diag.BytesComposed, ref _rlSnapBytes)}" +
                        $" skips {D(Composer.Diag.OversizedBlocksSkipped, ref _rlSkips)}" +
                        $" | reasm drops {D(reasmDrop, ref _rlReasmDrop)}");
        }

        readonly System.Collections.Generic.List<NetPeer> _pendingJoinSnapshots = new System.Collections.Generic.List<NetPeer>();

        /// <summary>Compose + send snapshots. Registered LAST on the tick so it captures this tick's final
        /// state; viewPos is the owning player's position (the §2.6 interest hook, policy AllRelevant v1).
        /// A peer with no acked baseline yet is mid-join: its world state is the reliable join snapshot
        /// already in flight (retransmitted until acked), so unreliable snapshots hold off until the first
        /// ack -- the join path is reliable BY CONSTRUCTION, not by racing the unreliable stream.</summary>
        public void TickReplication()
        {
            // this tick's joiners get their reliable FULL snapshot first -- composed here, after ALL of the
            // tick's mutation, so acking its tick really does mean "I have everything through that tick"
            if (_pendingJoinSnapshots.Count > 0)
            {
                foreach (var peer in _pendingJoinSnapshots)
                {
                    Vector3 spawnPos = Players.TryGetByOwner(peer.PlayerId, out var pe) ? pe.Pos : Vector3.zero;
                    var joinSnapshot = Composer.Compose(Session.CurrentTick, peer.PlayerId, spawnPos,
                                                        maxBytes: NetProtocol.MaxReliableMessageBytes / 2);
                    peer.SendReliable(NetMessagePak.Pack(ReplicationIds.EventJoinSnapshot, w =>
                    {
                        // explicit byteLen prefix: NetPakReader.RemainingSegmentLength is imprecise (the
                        // reader pre-buffers 32-bit words), so like every other frame on this stack the
                        // payload carries its own length
                        w.WriteUInt16((ushort)joinSnapshot.Length);
                        w.WriteBytes(joinSnapshot, 0, joinSnapshot.Length);
                    }, bufferSize: joinSnapshot.Length + 8));
                }
                _pendingJoinSnapshots.Clear();
            }

            if (SnapshotDivisorTicks > 1 && (Session.CurrentTick % SnapshotDivisorTicks) != 0) return;
            foreach (var peer in Session.Peers)
            {
                if (Composer.GetClientBaseline(peer.PlayerId) == 0) continue;   // join snapshot not acked yet
                Vector3 viewPos = Players.TryGetByOwner(peer.PlayerId, out var e) ? e.Pos : Vector3.zero;
                peer.SendUnreliableSequenced(Composer.Compose(Session.CurrentTick, peer.PlayerId, viewPos));
            }
        }

        public void TearDown() => Session.TearDown();
    }

    /// <summary>
    /// The client side: NetClientSession + the replica systems + SnapshotApplier + the ack piggyback, the
    /// event plane (join snapshot + the Phase 5 combat facts, surfaced as plain C# events for the game
    /// shell to render fx from -- fx are always client-LOCAL, §3.4), and the predicted local player
    /// (MP_PLAN §2.5b). Tick() once per 50 Hz tick.
    /// </summary>
    public sealed class NetWorldClient
    {
        public readonly NetClientSession Session;
        public readonly PlayerReplication Players = new PlayerReplication();
        public readonly PlayerCombatReplication CombatState = new PlayerCombatReplication();
        public readonly ZombieReplication Zombies = new ZombieReplication();
        public readonly ProjectileReplication Projectiles = new ProjectileReplication();
        // Phase 6 replicas: skills/inventory arrive owner-only (only MY entry ever fills in); the
        // deployable graph + world items are world state every client mirrors and solves locally (§3.1).
        public readonly SkillsReplication Skills = new SkillsReplication();
        public readonly DeployableReplication Deployables = new DeployableReplication();
        public readonly InventoryReplication Inventories = new InventoryReplication();
        public readonly WorldItemReplication WorldItems = new WorldItemReplication();
        // Phase 7 replica: every client mirrors vehicle state; puppets render from it (§3.6)
        public readonly VehicleReplication Vehicles = new VehicleReplication();
        // Phase 8 replicas (§3.7): world clock (time derives from the snapshot tick), crops, resources
        public readonly WorldClockReplication Clock = new WorldClockReplication();
        public readonly CropReplication Crops = new CropReplication();
        public readonly ResourceReplication Resources = new ResourceReplication();
        public readonly SnapshotApplier Applier;
        public readonly EventRegistry Events = new EventRegistry();
        public readonly ClientPrediction Prediction = new ClientPrediction();

        public long JoinSnapshotsApplied { get; private set; }   // reliable join-path proof for tests

        // Phase 5 combat facts (server -> this client). The shell subscribes to drive local fx/HUD:
        // damage numbers wait for HitConfirmed (§3.4); ImpactFx spawns decals/blood for OTHER players' shots.
        public event System.Action<HitConfirmEvent> HitConfirmed;
        public event System.Action<ImpactFxEvent> ImpactFx;
        public event System.Action<PlayerDiedEvent> PlayerDied;
        public event System.Action<PlayerRespawnedEvent> PlayerRespawned;
        public event System.Action<ZombieHitEvent> ZombieHit;
        public event System.Action<ZombieDiedEvent> ZombieDied;
        public event System.Action<AttackSwingEvent> ZombieSwung;
        public event System.Action<GrenadeExplodedEvent> GrenadeExploded;

        // Phase 6 transactional facts (already applied to the replicas above when these fire -- the game
        // layer subscribes for fx/UI, e.g. DeployableReplicaView rebuilds nodes, the HUD pings XP).
        public event System.Action<XpAwardedEvent> XpAwarded;
        public event System.Action<DeployablePlacedEvent> DeployablePlaced;
        public event System.Action<DeployableRemovedEvent> DeployableRemoved;
        public event System.Action<WireConnectedEvent> WireConnected;
        public event System.Action<WireRemovedEvent> WireRemoved;
        public event System.Action<DeployableToggledEvent> DeployableToggled;
        public event System.Action<WorldItemSpawnedEvent> WorldItemSpawned;
        public event System.Action<WorldItemSettledEvent> WorldItemSettled;
        public event System.Action<WorldItemRemovedEvent> WorldItemRemoved;
        public event System.Action<ItemPickupDeniedEvent> ItemPickupDenied;
        public event System.Action<ConsoleResultEvent> ConsoleResult;
        public event System.Action<StorageOpenedEvent> StorageOpened;
        public event System.Action<StorageClosedEvent> StorageClosed;

        // Phase 7 vehicle facts (occupancy also rides the snapshot; the event gives the requester immediacy)
        public event System.Action<VehicleEnteredEvent> VehicleEntered;
        public event System.Action<VehicleExitedEvent> VehicleExited;

        // Phase 8 world-state facts (§3.7 -- already applied to the replicas when these fire)
        public event System.Action<CropPlantedEvent> CropPlanted;
        public event System.Action<CropHarvestedEvent> CropHarvested;
        public event System.Action<ResourceHarvestedEvent> ResourceHarvested;
        public event System.Action<ResourceRespawnedEvent> ResourceRespawned;

        /// <summary>Hardening Part C: a confirmed replica-vs-server StateHash mismatch (the server must
        /// have EnableSyncCheck on; silent otherwise). The game shell surfaces this to the player.</summary>
        public event System.Action<DesyncReport> DesyncDetected;

        ushort _inputSeq;
        ushort _combatSeq;

        public NetWorldClient(IClientTransport transport, string playerName = "", ulong contentHash = 0)
        {
            Session = new NetClientSession(transport, playerName, contentHash: contentHash);
            Applier = new SnapshotApplier(new IReplicatedSystem[] { Players, CombatState, Zombies, Projectiles,
                                                                    Skills, Deployables, Inventories, WorldItems,
                                                                    Vehicles, Clock, Crops, Resources });
            Applier.DesyncDetected += report => DesyncDetected?.Invoke(report);   // (already NetLog'd in the applier)
            Events.Register(ReplicationIds.EventJoinSnapshot, reader =>
            {
                if (!reader.ReadUInt16(out ushort len) || len == 0) return;
                var snapshot = new byte[len];
                if (!reader.ReadBytes(snapshot, len)) return;
                // Cross-channel staleness guard: unreliable snapshots may beat a (retransmitted) reliable
                // join snapshot; applying the older FULL one would roll the replica back and deltas against
                // a newer server baseline would then miss its changes. Peek the tick, drop if stale.
                var peek = new NetPakReader();
                peek.Reset();   // SetBufferSegment alone does not reset the cursor
                peek.SetBufferSegment(snapshot, len);
                if (!peek.ReadUInt32(out uint tick) || tick < (uint)Applier.LastAppliedServerTick) return;
                if (ApplySnapshot(snapshot, len)) JoinSnapshotsApplied++;
            });
            Events.Register<HitConfirmEvent>(ReplicationIds.EventHitConfirm, HitConfirmEvent.TryRead, e => HitConfirmed?.Invoke(e));
            Events.Register<ImpactFxEvent>(ReplicationIds.EventImpactFx, ImpactFxEvent.TryRead, e => ImpactFx?.Invoke(e));
            Events.Register<PlayerDiedEvent>(ReplicationIds.EventPlayerDied, PlayerDiedEvent.TryRead, e => PlayerDied?.Invoke(e));
            Events.Register<PlayerRespawnedEvent>(ReplicationIds.EventPlayerRespawned, PlayerRespawnedEvent.TryRead, e => PlayerRespawned?.Invoke(e));
            Events.Register<ZombieHitEvent>(ReplicationIds.EventZombieHit, ZombieHitEvent.TryRead, e => ZombieHit?.Invoke(e));
            Events.Register<ZombieDiedEvent>(ReplicationIds.EventZombieDied, ZombieDiedEvent.TryRead, e => ZombieDied?.Invoke(e));
            Events.Register<AttackSwingEvent>(ReplicationIds.EventAttackSwing, AttackSwingEvent.TryRead, e => ZombieSwung?.Invoke(e));
            Events.Register<GrenadeExplodedEvent>(ReplicationIds.EventGrenadeExploded, GrenadeExplodedEvent.TryRead, e => GrenadeExploded?.Invoke(e));
            // Phase 6: topology/world-item facts apply straight onto the replicas (idempotent -- a delta
            // snapshot may have carried the same state first), then surface for the game layer.
            Events.Register<XpAwardedEvent>(ReplicationIds.EventXpAwarded, XpAwardedEvent.TryRead, e => XpAwarded?.Invoke(e));
            Events.Register<DeployablePlacedEvent>(ReplicationIds.EventDeployablePlaced, DeployablePlacedEvent.TryRead,
                e => { Deployables.ApplyPlaced(e, Applier.LastAppliedServerTick); DeployablePlaced?.Invoke(e); });
            Events.Register<DeployableRemovedEvent>(ReplicationIds.EventDeployableRemoved, DeployableRemovedEvent.TryRead,
                e => { Deployables.ApplyRemoved(e, Applier.LastAppliedServerTick); DeployableRemoved?.Invoke(e); });
            Events.Register<WireConnectedEvent>(ReplicationIds.EventWireConnected, WireConnectedEvent.TryRead,
                e => { Deployables.ApplyWireConnected(e, Applier.LastAppliedServerTick); WireConnected?.Invoke(e); });
            Events.Register<WireRemovedEvent>(ReplicationIds.EventWireRemoved, WireRemovedEvent.TryRead,
                e => { Deployables.ApplyWireRemoved(e, Applier.LastAppliedServerTick); WireRemoved?.Invoke(e); });
            Events.Register<DeployableToggledEvent>(ReplicationIds.EventDeployableToggled, DeployableToggledEvent.TryRead,
                e => { Deployables.ApplyToggled(e, Applier.LastAppliedServerTick); DeployableToggled?.Invoke(e); });
            Events.Register<WorldItemSpawnedEvent>(ReplicationIds.EventWorldItemSpawned, WorldItemSpawnedEvent.TryRead,
                e => { WorldItems.ApplySpawned(e, Applier.LastAppliedServerTick); WorldItemSpawned?.Invoke(e); });
            Events.Register<WorldItemSettledEvent>(ReplicationIds.EventWorldItemSettled, WorldItemSettledEvent.TryRead,
                e => { WorldItems.ApplySettled(e, Applier.LastAppliedServerTick); WorldItemSettled?.Invoke(e); });
            Events.Register<WorldItemRemovedEvent>(ReplicationIds.EventWorldItemRemoved, WorldItemRemovedEvent.TryRead,
                e => { WorldItems.ApplyRemoved(e, Applier.LastAppliedServerTick); WorldItemRemoved?.Invoke(e); });
            Events.Register<ItemPickupDeniedEvent>(ReplicationIds.EventItemPickupDenied, ItemPickupDeniedEvent.TryRead, e => ItemPickupDenied?.Invoke(e));
            Events.Register<ConsoleResultEvent>(ReplicationIds.EventConsoleResult, ConsoleResultEvent.TryRead, e => ConsoleResult?.Invoke(e));
            Events.Register<StorageOpenedEvent>(ReplicationIds.EventStorageOpened, StorageOpenedEvent.TryRead, e => StorageOpened?.Invoke(e));
            Events.Register<StorageClosedEvent>(ReplicationIds.EventStorageClosed, StorageClosedEvent.TryRead, e => StorageClosed?.Invoke(e));
            Events.Register<VehicleEnteredEvent>(ReplicationIds.EventVehicleEntered, VehicleEnteredEvent.TryRead,
                e => { Vehicles.ApplyEntered(e, Applier.LastAppliedServerTick); VehicleEntered?.Invoke(e); });
            Events.Register<VehicleExitedEvent>(ReplicationIds.EventVehicleExited, VehicleExitedEvent.TryRead,
                e => { Vehicles.ApplyExited(e, Applier.LastAppliedServerTick); VehicleExited?.Invoke(e); });
            // Phase 8: world-state facts apply straight onto the replicas, then surface for fx/views
            Events.Register<CropPlantedEvent>(ReplicationIds.EventCropPlanted, CropPlantedEvent.TryRead,
                e => { Crops.ApplyPlanted(e, Applier.LastAppliedServerTick); CropPlanted?.Invoke(e); });
            Events.Register<CropHarvestedEvent>(ReplicationIds.EventCropHarvested, CropHarvestedEvent.TryRead,
                e => { Crops.ApplyHarvested(e, Applier.LastAppliedServerTick); CropHarvested?.Invoke(e); });
            Events.Register<ResourceHarvestedEvent>(ReplicationIds.EventResourceHarvested, ResourceHarvestedEvent.TryRead,
                e => { Resources.ApplyHarvested(e, Applier.LastAppliedServerTick); ResourceHarvested?.Invoke(e); });
            Events.Register<ResourceRespawnedEvent>(ReplicationIds.EventResourceRespawned, ResourceRespawnedEvent.TryRead,
                e => { Resources.ApplyRespawned(e, Applier.LastAppliedServerTick); ResourceRespawned?.Invoke(e); });
        }

        public NetSessionState State => Session.State;
        public ushort PlayerId => Session.PlayerId;

        public void Connect() => Session.Connect();

        public void Tick()
        {
            Session.Tick();
            while (Session.TryReceiveUnreliable(out byte[] msg))
                ApplySnapshot(msg, msg.Length);
            while (Session.TryReceiveReliable(out byte[] msg))
                Events.TryDispatch(msg);   // reliable app messages are events (join snapshot + combat facts)
            // ack the newest applied tick EVERY tick (newest-wins, ~5 bytes): the server holds unreliable
            // snapshots until the first ack lands, so a lost ack datagram must never be able to stall the
            // join -- the next tick re-acks
            if (Session.State == NetSessionState.Connected && Applier.LastAppliedServerTick > 0)
                Session.SendUnreliableSequenced(NetMessagePak.Pack(SnapshotComposer.AckCommandId,
                    w => w.WriteUInt32((uint)Applier.LastAppliedServerTick)));
            // reconcile the predicted local player against the newest own-entity authoritative state
            if (Session.State == NetSessionState.Connected)
                Prediction.Reconcile(Players, Session.PlayerId);
        }

        bool ApplySnapshot(byte[] data, int length) => Applier.Apply(data, length);

        /// <summary>Send this tick's movement intent; returns the input seq (0 = not connected, nothing
        /// sent) so the shell can record its prediction under the same seq. buttons = the v2 held-button
        /// bits (MoveInput.ButtonJump | ...).</summary>
        public ushort SendMoveInput(float moveX, float moveY, float yawDegrees, byte buttons = 0)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new MoveInput { Seq = ++_inputSeq, MoveX = moveX, MoveY = moveY, YawDegrees = yawDegrees, Buttons = buttons };
            if (_inputSeq == 0) cmd.Seq = ++_inputSeq;   // seq 0 is the reconciler's "none" sentinel; skip it on wrap
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandMoveInput, cmd.Write));
            return cmd.Seq;
        }

        // ---- Phase 5 combat commands (ReliableOrdered -- transactional, §2.3). Each returns the seq the
        // server will echo in HitConfirm (0 = not connected, nothing sent). ----

        public ushort SendFire(Vector3 origin, Vector3 dir)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new FireCommand { Seq = NextCombatSeq(), Origin = origin, Dir = dir };
            Session.SendReliable(NetMessagePak.Pack(ReplicationIds.CommandFire, cmd.Write));
            return cmd.Seq;
        }

        public ushort SendMelee(bool strong, float yawDegrees)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new MeleeCommand { Seq = NextCombatSeq(), Strong = strong, YawDegrees = yawDegrees };
            Session.SendReliable(NetMessagePak.Pack(ReplicationIds.CommandMelee, cmd.Write));
            return cmd.Seq;
        }

        public ushort SendGrenade(Vector3 origin, Vector3 velocity)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new GrenadeCommand { Seq = NextCombatSeq(), Origin = origin, Velocity = velocity };
            Session.SendReliable(NetMessagePak.Pack(ReplicationIds.CommandGrenade, cmd.Write));
            return cmd.Seq;
        }

        public ushort SendReload()
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new ReloadCommand { Seq = NextCombatSeq() };
            Session.SendReliable(NetMessagePak.Pack(ReplicationIds.CommandReload, cmd.Write));
            return cmd.Seq;
        }

        ushort NextCombatSeq()
        {
            if (++_combatSeq == 0) _combatSeq = 1;
            return _combatSeq;
        }

        // ---- Phase 6 transactional commands (all ReliableOrdered -- §2.3). Each returns false when not
        // connected (nothing sent); results come back as owner-block snapshots and/or events. ----

        bool SendCommand(byte commandId, System.Action<SDG.NetPak.NetPakWriter> write)
        {
            if (Session.State != NetSessionState.Connected) return false;
            Session.SendReliable(NetMessagePak.Pack(commandId, write));
            return true;
        }

        public bool SendUpgradeSkill(byte speciality, byte index)
            => SendCommand(ReplicationIds.CommandUpgradeSkill, new UpgradeSkillCommand { Speciality = speciality, Index = index }.Write);

        public bool SendPlaceDeployable(ushort defId, Vector3 pos, float yawDegrees)
            => SendCommand(ReplicationIds.CommandPlaceDeployable, new PlaceDeployableCommand { DefId = defId, Pos = pos, YawDegrees = yawDegrees }.Write);

        public bool SendSalvageDeployable(uint netId)
            => SendCommand(ReplicationIds.CommandSalvageDeployable, new SalvageDeployableCommand { NetId = netId }.Write);

        public bool SendConnectWire(uint srcId, byte srcPort, uint dstId, byte dstPort)
            => SendCommand(ReplicationIds.CommandConnectWire, new ConnectWireCommand { SrcId = srcId, SrcPort = srcPort, DstId = dstId, DstPort = dstPort }.Write);

        public bool SendRemoveWire(uint wireId)
            => SendCommand(ReplicationIds.CommandRemoveWire, new RemoveWireCommand { WireId = wireId }.Write);

        public bool SendToggleDeployable(uint netId, bool on)
            => SendCommand(ReplicationIds.CommandToggleDeployable, new ToggleDeployableCommand { NetId = netId, On = on }.Write);

        public bool SendMoveItem(byte page0, byte x0, byte y0, byte page1, byte x1, byte y1, byte rot1)
            => SendCommand(ReplicationIds.CommandMoveItem, new MoveItemCommand { Page0 = page0, X0 = x0, Y0 = y0, Page1 = page1, X1 = x1, Y1 = y1, Rot1 = rot1 }.Write);

        public bool SendDropItem(byte page, byte x, byte y)
            => SendCommand(ReplicationIds.CommandDropItem, new DropItemCommand { Page = page, X = x, Y = y }.Write);

        public bool SendPickupItem(uint netId)
            => SendCommand(ReplicationIds.CommandPickupItem, new PickupItemCommand { NetId = netId }.Write);

        public bool SendEquipItem(byte fromPage, byte x, byte y, byte slot)
            => SendCommand(ReplicationIds.CommandEquipItem, new EquipItemCommand { FromPage = fromPage, X = x, Y = y, Slot = slot }.Write);

        public bool SendCraft(ushort blueprintIndex)
            => SendCommand(ReplicationIds.CommandCraft, new CraftCommand { BlueprintIndex = blueprintIndex }.Write);

        public bool SendConsume(byte page, byte x, byte y)
            => SendCommand(ReplicationIds.CommandConsume, new ConsumeCommand { Page = page, X = x, Y = y }.Write);

        public bool SendOpenStorage(uint netId)
            => SendCommand(ReplicationIds.CommandOpenStorage, new OpenStorageCommand { NetId = netId }.Write);

        public bool SendCloseStorage()
            => SendCommand(ReplicationIds.CommandCloseStorage, new CloseStorageCommand().Write);

        public bool SendConsole(string text)
            => SendCommand(ReplicationIds.CommandConsole, new ConsoleCommand { Text = text }.Write);

        // ---- Phase 8 crop commands (§3.7): both transactional, ReliableOrdered ----

        public bool SendPlantCrop(ushort seedId, Vector3 pos)
            => SendCommand(ReplicationIds.CommandPlantCrop, new PlantCropCommand { SeedId = seedId, Pos = pos }.Write);

        public bool SendHarvestCrop(uint netId)
            => SendCommand(ReplicationIds.CommandHarvestCrop, new HarvestCropCommand { NetId = netId }.Write);

        // ---- Phase 7 vehicle commands (§3.6): Enter/Exit transactional, DriveInput @50 Hz unreliable ----

        public bool SendEnterVehicle(uint netId)
            => SendCommand(ReplicationIds.CommandEnterVehicle, new EnterVehicleCommand { NetId = netId }.Write);

        public bool SendExitVehicle()
            => SendCommand(ReplicationIds.CommandExitVehicle, new ExitVehicleCommand().Write);

        /// <summary>This tick's driving intent (held-input model like MoveInput -- the newest keeps applying
        /// server-side until replaced, so single loss costs nothing). Returns the seq (0 = not connected).</summary>
        public ushort SendDriveInput(uint vehicleNetId, float throttle, float steer, bool handbrake)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            if (++_driveSeq == 0) _driveSeq = 1;
            var cmd = new DriveInputCommand { Seq = _driveSeq, NetId = vehicleNetId, Throttle = throttle, Steer = steer, Handbrake = handbrake };
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandDriveInput, cmd.Write));
            return cmd.Seq;
        }

        ushort _driveSeq;

        public void Disconnect() => Session.Disconnect();
    }
}
