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
        // SP/MP-unify wave 2 (v11): three new systems, empty stubs at reservation; bodies filled by owners
        // (13 = tinyclaw, 14/15 = cow tools). Registered in the Composer array below; EXCLUDED from EnableSyncCheck.
        public readonly PlayerVitalsReplication Vitals = new PlayerVitalsReplication();
        public readonly ContainerReplication Containers = new ContainerReplication();
        public readonly AnimalReplication Animals = new AnimalReplication();
        // destructible props (rubble): the alive-bitmap + the server-only health/respawn authority
        public readonly DestructibleReplication Destructibles = new DestructibleReplication();
        public readonly ServerDestructibles DestructibleHost;
        public readonly ServerVehicles VehicleHost;
        public readonly ServerPlayerAuthority PlayerHost;   // mp-clientauth-foot (v9): on-foot claims -> envelope -> ServerDrive adopt
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
                              ulong contentHash = 0,
                              string activeHoliday = "")
        {
            Session = new NetServerSession(transport, connectionFailureCallback, maxPeers: maxPeers, contentHash: contentHash,
                                           activeHoliday: activeHoliday);
            Composer = new SnapshotComposer(new IReplicatedSystem[] { Players, CombatState, Zombies, Projectiles,
                                                                      Skills, Deployables, Inventories, WorldItems,
                                                                      Vehicles, Clock, Crops, Resources,
                                                                      Vitals, Containers, Animals, Destructibles });   // wave 2 (v11): 13/14/15; v13: Destructibles(16) last, ascending
            Composer.CurrentTick = () => Session.CurrentTick;   // review L1: rejects acks of future ticks
            Composer.RegisterAck(Commands);
            Combat = new ServerCombat(Players, CombatState, Zombies, Projectiles, Ids, BroadcastEvent, SendEventTo);
            // destructible props (rubble): health/respawn authority; combat routes an object hit into it
            DestructibleHost = new ServerDestructibles(Destructibles, BroadcastEvent);
            Combat.DamageObject = (index, amount, tick) => DestructibleHost.DamageObject(index, amount, tick);
            Transactions = new ServerTransactions(Players, CombatState, Skills, Inventories, WorldItems, Deployables,
                                                  Ids, () => Session.CurrentTick, BroadcastEvent, SendEventTo,
                                                  Crops, Resources, Vitals);
            Transactions.Register(Commands);
            VehicleHost = new ServerVehicles(Vehicles, Players, CombatState, () => Session.CurrentTick, BroadcastEvent, SendEventTo);
            VehicleHost.Register(Commands);
            // mp-clientauth-foot (v9): the owner's on-foot transform stream -- envelope-validated, then
            // adopted through ServerDrive (the entity goes ExternallyDriven; ServerStep never integrates
            // a client-driven owner). Seated peers' walk claims drop at the choke point like MoveInput's.
            PlayerHost = new ServerPlayerAuthority(Players, CombatState, VehicleHost.IsDriver,
                                                   () => Session.CurrentTick, SendEventTo);
            PlayerHost.Register(Commands);
            // P3a (SP/MP-unify): a server respawn of a client-authoritative owner must ride the recov/
            // freeze-until-echo primitive (else the shell's next PlayerStateCommand ServerDrives the entity
            // straight back off the spawn). ServerCombat.Respawn calls this; it no-ops (returns false ->
            // ServerCombat ServerTeleports) for owners with no client-auth stream (bystander avatars, loopback).
            Combat.RepositionOwner = PlayerHost.RepositionOwner;
            // P3b (SP/MP-unify): route the server-DERIVED fall + out-of-bounds damage (computed off the owner's
            // adopted Vel/Grounded/Pos, never a client-reported number) into the same ServerCombat sink the weapon
            // paths funnel through. Keeps HP fully server-authored for the client-auth walker.
            PlayerHost.DamageOwner = (victim, dmg) => Combat.DamagePlayerExternal(victim, dmg);
            // mp-event-coalesce (v10): route each deduped carried combat event to the ServerCombat handler
            // by Kind. The authority holds only a PlayerCombatReplication (for IsAlive); the OnFire/etc
            // handlers live on ServerCombat, so the carry is dispatched through this delegate. The standalone
            // CommandFire/Melee/Grenade/Reload registrations below stay in place but dormant (the client no
            // longer sends them) -- mirrors MoveInput staying registered for demo walkers/loopback.
            PlayerHost.CombatDispatch = (sender, ev, tick) =>
            {
                switch (ev.Kind)
                {
                    case CombatEventKind.Fire:    Combat.OnFire(sender, ev.Fire, tick); break;
                    case CombatEventKind.Melee:   Combat.OnMelee(sender, ev.Melee, tick); break;
                    case CombatEventKind.Grenade: Combat.OnGrenade(sender, ev.Grenade, tick); break;
                    case CombatEventKind.Reload:  Combat.OnReload(sender, ev.Reload, tick); break;
                }
            };
            Transactions.IsSeated = VehicleHost.IsDriver;   // console teleport rejects seated senders (the seat teleport owns the entity, #27)
            Combat.KillCredited = killer => { if (KillExperience > 0) Transactions.AwardXp(killer, KillExperience); };
            // B5 (SP/MP-unify): server-authoritative fine vitals. HP is NEVER owned by the vitals sim -- each
            // tick ServerStep re-seeds Sim.Health from the single HP authority (CombatState.HealthExact) and
            // routes the delta OUT: starvation loss through the queued DamagePlayerExternal env sink (death-
            // capable, landing in THIS tick's Combat.Step, which runs right after Vitals.ServerStep in
            // TickSimulation), regen through a direct HealthExact raise. Stamina is server-owned but sprint
            // stays client-auth: the server derives `sprinting` from the ADOPTED stance (PlayerHost DrivenState
            // for the MP shell, or the held MoveInput for a loopback/demo walker) -- no second body. HP-delta
            // routing runs only while SurvivalDrain is on (default OFF = SP byte-identical coarse-HP path).
            Vitals.IsAlive = pid => CombatState.IsAlive(pid);
            Vitals.SprintingOf = pid =>
                PlayerHost.TryGetDrivenState(pid, out var ds) ? ds.Stance == EPlayerStance.SPRINT
                : Players.TryGetHeldInput(pid, out var mi) && mi.Stance == EPlayerStance.SPRINT;
            Vitals.MultipliersOf = pid => Skills.TryGet(pid, out var se)
                ? new PlayerVitalsSim.Multipliers
                {
                    ExerciseStaminaDrain = se.Skills.ExerciseStaminaDrainMultiplier(),
                    CardioStaminaRegen = se.Skills.CardioStaminaRegenMultiplier(),
                    SurvivalDrain = se.Skills.SurvivalDrainMultiplier(),
                    VitalityRegen = se.Skills.VitalityRegenMultiplier(),
                }
                : PlayerVitalsSim.Multipliers.None;
            Vitals.HealthOf = pid => CombatState.TryGet(pid, out var ce) ? ce.HealthExact : 100f;
            Vitals.DamageSink = (pid, dmg) => Combat.DamagePlayerExternal(pid, dmg);   // env attacker 0 -> Killer 0, death-capable
            Vitals.RegenSink = (pid, amt) =>
            {
                if (!CombatState.TryGet(pid, out var ce) || !ce.Alive) return;
                ce.HealthExact = System.Math.Min(100f, ce.HealthExact + amt);
                ce.Health = (byte)System.Math.Clamp((int)System.Math.Ceiling(ce.HealthExact), 0, 100);   // same coarsening as ApplyPlayerDamage
                CombatState.MarkDirty(ce, Session.CurrentTick);
            };
            Commands.Register<MoveInputPacket>(ReplicationIds.CommandMoveInput, MoveInputPacket.TryRead,
                // C1 (plan §4.2): enqueue every carried entry oldest-first -- ServerQueueInput's
                // strictly-increasing-seq guard drops the entries an earlier datagram already delivered,
                // so the redundant backfill is idempotent and a queue hole now needs 3 consecutive losses
                (sender, pkt) => { for (int i = 0; i < pkt.Count; i++) Players.ServerQueueInput(sender, pkt.Get(i)); },
                // a corpse's inputs drop at the choke point; so do a DRIVER's (the seat teleport owns the
                // avatar while driving -- walked positions must not fight it, §3.6)
                validate: (sender, pkt) => CombatState.IsAlive(sender) && !VehicleHost.IsDriver(sender));
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
                Vitals.ServerAdd(peer.PlayerId, Session.CurrentTick);   // B5: one PlayerVitalsSim per player, owner-only on the wire
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
                PlayerHost.OnPeerDisconnected(peer.PlayerId);    // the authority window dies with the peer
                Players.ServerRemove(peer.PlayerId, Session.CurrentTick);
                CombatState.ServerRemove(peer.PlayerId, Session.CurrentTick);
                Skills.ServerRemove(peer.PlayerId);
                Vitals.ServerRemove(peer.PlayerId);   // B5: the leaving peer's vitals sim dies with it
                Inventories.ServerRemove(peer.PlayerId, Session.CurrentTick);   // also releases any crate they held open
                Composer.ForgetClient(peer.PlayerId);
                _pendingRecoveryFulls.Remove(peer.PlayerId);   // a reused playerId must not inherit a stale hold
                // Phase 8 rejoin hardening: per-client relevancy sets must not leak to a recycled playerId
                Zombies.ForgetClient(peer.PlayerId);
                WorldItems.ForgetClient(peer.PlayerId);
                Containers.ForgetClient(peer.PlayerId);   // review #8: the two NEW relevancy-filtered systems must clear per-client state on disconnect too, like Zombies/WorldItems
                Animals.ForgetClient(peer.PlayerId);
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
                ReplicationIds.SystemCrops, ReplicationIds.SystemResources, ReplicationIds.SystemDestructibles);
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
            // B5: BETWEEN VehicleHost.Step and Combat.Step so a queued starvation drain lands in THIS tick's
            // Combat.Step (the external-damage queue drains at the top of Combat.Step) -- death same tick.
            Vitals.ServerStep(Session.CurrentTick, (float)SimClock.FixedDelta);
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

        // Starvation recovery (docs/DRIVE_DRIVER_VIEW_ROOTCAUSE.md): peers with a reliable recovery FULL
        // in flight -> the tick it was composed at. Unreliable snapshots hold until the client's ack
        // reaches that tick (mirroring the join hold below), then normal delta flow resumes.
        readonly System.Collections.Generic.Dictionary<ushort, long> _pendingRecoveryFulls = new System.Collections.Generic.Dictionary<ushort, long>();

        /// <summary>Compose + send one FULL snapshot on the ReliableOrdered channel with the join-path
        /// budget (fragmentation is safe there, §2.2). Used by the join flow and by starvation recovery:
        /// a full snapshot is the loss-RECOVERY mechanism, so it must never be composed under a datagram
        /// budget smaller than the world it has to carry -- a full block over the unreliable budget is
        /// skipped from EVERY unreliable compose, its pinned baseline then never advances, and
        /// WillSendFull latches forever (the live "driven vehicle frozen on the driver's own client"
        /// wedge). The client applies it through the same EventJoinSnapshot handler (stale-tick guarded).</summary>
        void SendReliableFullSnapshot(NetPeer peer, Vector3 viewPos)
        {
            var snapshot = Composer.Compose(Session.CurrentTick, peer.PlayerId, viewPos,
                                            maxBytes: NetProtocol.MaxReliableMessageBytes / 2);
            peer.SendReliable(NetMessagePak.Pack(ReplicationIds.EventJoinSnapshot, w =>
            {
                // explicit byteLen prefix: NetPakReader.RemainingSegmentLength is imprecise (the
                // reader pre-buffers 32-bit words), so like every other frame on this stack the
                // payload carries its own length
                w.WriteUInt16((ushort)snapshot.Length);
                w.WriteBytes(snapshot, 0, snapshot.Length);
            }, bufferSize: snapshot.Length + 8));
        }

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
                    SendReliableFullSnapshot(peer, spawnPos);
                }
                _pendingJoinSnapshots.Clear();
            }

            if (SnapshotDivisorTicks > 1 && (Session.CurrentTick % SnapshotDivisorTicks) != 0) return;
            foreach (var peer in Session.Peers)
            {
                if (Composer.GetClientBaseline(peer.PlayerId) == 0) continue;   // join snapshot not acked yet
                // a reliable recovery full is in flight: hold the unreliable stream (the join hold, again)
                // until the client acks the recovery tick -- that ack provably advances every system's
                // baseline (the reliable full carried them all), which clears WillSendFull
                if (_pendingRecoveryFulls.TryGetValue(peer.PlayerId, out long recoveryTick))
                {
                    if (Composer.GetClientBaseline(peer.PlayerId) < recoveryTick) continue;
                    _pendingRecoveryFulls.Remove(peer.PlayerId);
                }
                Vector3 viewPos = Players.TryGetByOwner(peer.PlayerId, out var e) ? e.Pos : Vector3.zero;
                if (Composer.WillSendFull(peer.PlayerId, Session.CurrentTick))
                {
                    // Starvation-recovery fulls must ride the RELIABLE channel (the fix of
                    // docs/DRIVE_DRIVER_VIEW_ROOTCAUSE.md §5): under the 1187-byte unreliable budget a
                    // full world block that outgrew the datagram (PEI's vehicles block is ~2.9 KB) is
                    // budget-skipped from every compose, so the client this full is meant to RESYNC can
                    // never receive exactly the system it lost -- the permanent per-client freeze. Sent
                    // ONCE per wedge; the hold above keeps the reliable/unreliable streams from racing.
                    SendReliableFullSnapshot(peer, viewPos);
                    _pendingRecoveryFulls[peer.PlayerId] = Session.CurrentTick;
                    if (NetLog.Enabled) NetLog.Sink($"[NET] reliable recovery full -> player {peer.PlayerId} (ack gap or starved system at tick {Session.CurrentTick})");
                    continue;
                }
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
        // SP/MP-unify wave 2 (v11): three new client-side replicas, empty stubs at reservation (bodies filled by owners).
        public readonly PlayerVitalsReplication Vitals = new PlayerVitalsReplication();
        public readonly ContainerReplication Containers = new ContainerReplication();
        public readonly AnimalReplication Animals = new AnimalReplication();
        // destructible props (rubble): client mirrors the alive-bitmap; DestructibleAliveView hides/restores nodes
        public readonly DestructibleReplication Destructibles = new DestructibleReplication();
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
        // Part A: the server rolled this driver's vehicle back (out-of-envelope state) -- teleport the
        // local vehicle to the payload, freeze, echo RecovCounter in the outgoing state stream
        public event System.Action<VehicleRecovEvent> VehicleRecov;
        // mp-clientauth-foot (v9): the server rolled this owner's on-foot claim back (out of envelope) --
        // teleport the shell to the payload, re-seed the sim velocity, echo RecovCounter in the state stream
        public event System.Action<PlayerRecovEvent> PlayerRecov;

        // Phase 8 world-state facts (§3.7 -- already applied to the replicas when these fire)
        public event System.Action<CropPlantedEvent> CropPlanted;
        public event System.Action<CropHarvestedEvent> CropHarvested;
        public event System.Action<ResourceHarvestedEvent> ResourceHarvested;
        public event System.Action<ResourceRespawnedEvent> ResourceRespawned;
        // destructible props (rubble): a placed object broke / respawned (already applied to the bitmap when these fire)
        public event System.Action<ObjectDestroyedEvent> ObjectDestroyed;
        public event System.Action<ObjectRestoredEvent> ObjectRestored;

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
                                                                    Vehicles, Clock, Crops, Resources,
                                                                    Vitals, Containers, Animals, Destructibles });   // wave 2 (v11): 13/14/15; v13: Destructibles(16), symmetric with the server Composer
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
            Events.Register<VehicleRecovEvent>(ReplicationIds.EventVehicleRecov, VehicleRecovEvent.TryRead,
                e => VehicleRecov?.Invoke(e));   // touches no replica -- the rollback targets the driver's LOCAL vehicle only
            Events.Register<PlayerRecovEvent>(ReplicationIds.EventPlayerRecov, PlayerRecovEvent.TryRead,
                e => PlayerRecov?.Invoke(e));    // touches no replica -- the rollback targets the owner's LOCAL shell only
            // Phase 8: world-state facts apply straight onto the replicas, then surface for fx/views
            Events.Register<CropPlantedEvent>(ReplicationIds.EventCropPlanted, CropPlantedEvent.TryRead,
                e => { Crops.ApplyPlanted(e, Applier.LastAppliedServerTick); CropPlanted?.Invoke(e); });
            Events.Register<CropHarvestedEvent>(ReplicationIds.EventCropHarvested, CropHarvestedEvent.TryRead,
                e => { Crops.ApplyHarvested(e, Applier.LastAppliedServerTick); CropHarvested?.Invoke(e); });
            Events.Register<ResourceHarvestedEvent>(ReplicationIds.EventResourceHarvested, ResourceHarvestedEvent.TryRead,
                e => { Resources.ApplyHarvested(e, Applier.LastAppliedServerTick); ResourceHarvested?.Invoke(e); });
            Events.Register<ResourceRespawnedEvent>(ReplicationIds.EventResourceRespawned, ResourceRespawnedEvent.TryRead,
                e => { Resources.ApplyRespawned(e, Applier.LastAppliedServerTick); ResourceRespawned?.Invoke(e); });
            // destructible props (rubble): apply the alive-bit flip straight onto the bitmap, then surface for the break/respawn fx + node hide
            Events.Register<ObjectDestroyedEvent>(ReplicationIds.EventObjectDestroyed, ObjectDestroyedEvent.TryRead,
                e => { Destructibles.ApplyDestroyed(e, Applier.LastAppliedServerTick); ObjectDestroyed?.Invoke(e); });
            Events.Register<ObjectRestoredEvent>(ReplicationIds.EventObjectRestored, ObjectRestoredEvent.TryRead,
                e => { Destructibles.ApplyRestored(e, Applier.LastAppliedServerTick); ObjectRestored?.Invoke(e); });
        }

        public NetSessionState State => Session.State;
        public ushort PlayerId => Session.PlayerId;
        /// <summary>P3 (wire v6): the server world's activeHoliday from the Accept -- what the joining
        /// client must build its holiday-gated props/colliders with. "" until Connected.</summary>
        public string ServerHoliday => Session.ServerHoliday;

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
            // v10 (mp-event-coalesce): drain the redundant combat ring up to the owner-facing combat ack the
            // just-applied snapshot carries -- once the server has applied an event we stop re-including it.
            // Single-path (only the shell client sends combat; loopback's ring is always empty -> a no-op).
            if (Session.State == NetSessionState.Connected && Players.TryGetByOwner(Session.PlayerId, out var self))
                AckCombat(self.LastProcessedCombatSeq);
        }

        bool ApplySnapshot(byte[] data, int length) => Applier.Apply(data, length);

        /// <summary>Send this tick's movement intent (demo walkers / loopback -- the shell client streams
        /// SendPlayerState instead since v9); returns the input seq (0 = not connected, nothing sent) so
        /// the walker can record its prediction under the same seq. buttons = the v2 held-button bits
        /// (MoveInput.ButtonJump | ...). C1 (plan §4.2): the datagram carries the newest input plus the
        /// previous two (MoveInputPacket), so a single lost/overtaken datagram costs the server nothing
        /// -- the next one backfills the hole.</summary>
        public ushort SendMoveInput(float moveX, float moveY, float yawDegrees, byte buttons = 0)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new MoveInput { Seq = ++_inputSeq, MoveX = moveX, MoveY = moveY, YawDegrees = yawDegrees, Buttons = buttons };
            if (_inputSeq == 0) cmd.Seq = ++_inputSeq;   // seq 0 is the reconciler's "none" sentinel; skip it on wrap
            // a PAUSE in the send stream (ride mode, respawn -- anything that stopped ShellStep sending)
            // voids the redundancy ring: the server cleared its input state at the pause boundary
            // (ServerClearInput), and backfilling stale pre-pause intents into the resumed stream would
            // integrate ticks of walk the client stopped predicting long ago
            if (Session.CurrentTick != _lastMoveSendTick + 1) _movePrevCount = 0;
            _lastMoveSendTick = Session.CurrentTick;
            var pkt = new MoveInputPacket { Count = (byte)(_movePrevCount + 1) };
            if (_movePrevCount == 2) { pkt.I0 = _movePrev2; pkt.I1 = _movePrev1; pkt.I2 = cmd; }
            else if (_movePrevCount == 1) { pkt.I0 = _movePrev1; pkt.I1 = cmd; }
            else pkt.I0 = cmd;
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandMoveInput, pkt.Write));
            _movePrev2 = _movePrev1;
            _movePrev1 = cmd;
            if (_movePrevCount < 2) _movePrevCount++;
            return cmd.Seq;
        }

        MoveInput _movePrev1, _movePrev2;   // the last / second-to-last sent inputs (the C1 redundancy ring)
        int _movePrevCount;
        long _lastMoveSendTick = long.MinValue;

        // ---- Phase 5 combat commands. mp-event-coalesce (v10): these no longer ride their OWN
        // ReliableOrdered datagram (a single drop head-of-line-blocks the reliable-ordered channel and
        // stalls all following combat). Each now ENQUEUES a CarriedCombatEvent into a bounded pending ring;
        // SendPlayerState folds the whole (unacked) ring into the 50 Hz unreliable transform stream every
        // tick, and keeps re-including each event until the server ACKs it (AckCombat) -- so a dropped
        // datagram is covered by the next one. The signature is unchanged: each returns the seq the server
        // echoes in HitConfirm (0 = not connected, nothing enqueued), which the caller uses to correlate. ----

        public ushort SendFire(Vector3 origin, Vector3 dir)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new FireCommand { Seq = NextCombatSeq(), Origin = origin, Dir = dir };
            EnqueueCombat(new CarriedCombatEvent { Kind = CombatEventKind.Fire, Fire = cmd });
            return cmd.Seq;
        }

        public ushort SendMelee(bool strong, float yawDegrees)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new MeleeCommand { Seq = NextCombatSeq(), Strong = strong, YawDegrees = yawDegrees };
            EnqueueCombat(new CarriedCombatEvent { Kind = CombatEventKind.Melee, Melee = cmd });
            return cmd.Seq;
        }

        public ushort SendGrenade(Vector3 origin, Vector3 velocity)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new GrenadeCommand { Seq = NextCombatSeq(), Origin = origin, Velocity = velocity };
            EnqueueCombat(new CarriedCombatEvent { Kind = CombatEventKind.Grenade, Grenade = cmd });
            return cmd.Seq;
        }

        public ushort SendReload()
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new ReloadCommand { Seq = NextCombatSeq() };
            EnqueueCombat(new CarriedCombatEvent { Kind = CombatEventKind.Reload, Reload = cmd });
            return cmd.Seq;
        }

        ushort NextCombatSeq()
        {
            if (++_combatSeq == 0) _combatSeq = 1;
            return _combatSeq;
        }

        // ---- the redundant combat pending ring (mp-event-coalesce v10) ----
        // Up to MaxCarriedEvents recent UNACKED events, oldest-first. SendPlayerState re-includes them all
        // every tick; AckCombat drops the ones the server has applied. Bounded: on overflow the oldest is
        // dropped (only reachable under unrealistic sustained saturation with no acks coming back).
        readonly CarriedCombatEvent[] _combatRing = new CarriedCombatEvent[PlayerStateCommand.MaxCarriedEvents];
        int _combatRingCount;

        // Test seams (the DisableEnvelope pattern), all default-off:
        /// <summary>Clear the ring after each SendPlayerState include -- simulates the pre-v10 send-once
        /// behaviour. Teeth for the "redundancy survives a dropped packet" test.</summary>
        public bool DisableCombatRedundancy;
        /// <summary>Make AckCombat a no-op so the ring never drains. Teeth for the "ack drains the ring" test.</summary>
        public bool DisableCombatAck;
        /// <summary>Write the ring newest-first instead of oldest-first. With the server's strictly-increasing
        /// dedup guard this makes all but the newest event get dropped -- teeth for the "oldest-first" test.</summary>
        public bool CombatRingReverseOrder;

        void EnqueueCombat(in CarriedCombatEvent ev)
        {
            if (_combatRingCount == _combatRing.Length)
            {
                // ring full: drop the OLDEST (shift left one), keeping the newest MaxCarriedEvents-1
                System.Array.Copy(_combatRing, 1, _combatRing, 0, _combatRingCount - 1);
                _combatRingCount--;
            }
            _combatRing[_combatRingCount++] = ev;
        }

        /// <summary>Drop every pending event whose combat seq is NOT strictly newer than ackedSeq (i.e.
        /// applied server-side already). Wrap-aware via NetSeq; the 0 sentinel ("no ack yet") drains nothing.
        /// Stable compaction preserves the oldest-first ordering of the survivors.</summary>
        public void AckCombat(ushort ackedSeq)
        {
            if (DisableCombatAck || ackedSeq == 0) return;
            int w = 0;
            for (int i = 0; i < _combatRingCount; i++)
                if (NetSeq.IsNewer(_combatRing[i].Seq, ackedSeq))
                    _combatRing[w++] = _combatRing[i];
            _combatRingCount = w;
        }

        /// <summary>Drop the whole pending combat ring. The game layer calls this when the owner leaves a
        /// state where it can legitimately fire on-foot -- entering a vehicle, dying, or respawning -- so
        /// shots enqueued in the ~1 RTT around that transition don't sit UN-ACKED (the server's
        /// alive/not-seated validate rejects them, so they're never acked and never drain) and then REPLAY
        /// when the gate re-opens on exit/respawn. A dead or seated player's shots don't count; dropping
        /// them is the authoritative result. (The tight server-respawn race -- backlog accepted in the ~1
        /// RTT before the client's respawn event lands -- is closed server-side alongside vitals; see #52.)</summary>
        public void ClearCombatRing() => _combatRingCount = 0;

        /// <summary>Test-visible pending-ring depth.</summary>
        public int PendingCombatCount => _combatRingCount;
        /// <summary>Test helper: does the pending ring still hold this combat seq?</summary>
        public bool PendingCombatContains(ushort seq)
        {
            for (int i = 0; i < _combatRingCount; i++) if (_combatRing[i].Seq == seq) return true;
            return false;
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

        public bool SendPickupDeployable(uint netId)   // B2: hold-F returns the live deployable to the bag (removal echoes back through the replica view)
            => SendCommand(ReplicationIds.CommandPickupDeployable, new PickupDeployableCommand { NetId = netId }.Write);

        public bool SendExtractFuel(uint pumpNetId)   // A2: RMB a powered pump with a gas can -> server drains the shared station tank into the can (owner echo re-adopts the fuller can)
            => SendCommand(ReplicationIds.CommandExtractFuel, new ExtractFuelCommand { PumpNetId = pumpNetId }.Write);

        public bool SendAttachTow(uint towerNetId, uint towedNetId)   // B11: tie a rope between two replicated vehicles (tower rear -> towed front); the committed rope echoes back via A6's TowedNetId (never mutated client-side)
            => SendCommand(ReplicationIds.CommandAttachTow, new AttachTowCommand { TowerNetId = towerNetId, TowedNetId = towedNetId }.Write);

        public bool SendDetachTow(uint netId)   // B11: untie a roped vehicle (either end); the cleared relationship echoes back via A6's TowedNetId->0
            => SendCommand(ReplicationIds.CommandDetachTow, new DetachTowCommand { NetId = netId }.Write);

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

        /// <summary>Part A (CLIENT_PREDICTION_PLAN §5.2 A2): the predicted driver's reported vehicle state
        /// -- UnreliableSequenced, sent by the session every 2nd tick (25 Hz). recovAck echoes the last
        /// VehicleRecovEvent counter received (the session tracks it; 0 = none yet). Returns the seq
        /// (0 = not connected, nothing sent).</summary>
        public ushort SendVehicleState(uint vehicleNetId, Vector3 pos, Vector3 eulerDegrees, Vector3 linVel, Vector3 angVel,
                                       float steerDegrees, float throttle, float steer, bool handbrake, byte flags, byte recovAck)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            if (++_vehStateSeq == 0) _vehStateSeq = 1;
            var cmd = new VehicleStateCommand
            {
                Seq = _vehStateSeq, NetId = vehicleNetId, RecovAck = recovAck,
                Pos = pos, YawDegrees = eulerDegrees.y, PitchDegrees = eulerDegrees.x, RollDegrees = eulerDegrees.z,
                LinVel = linVel, AngVel = angVel, SteerDegrees = steerDegrees,
                Throttle = throttle, Steer = steer, Handbrake = handbrake, Flags = flags,
            };
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandVehicleState, cmd.Write));
            return cmd.Seq;
        }

        ushort _vehStateSeq;

        /// <summary>mp-clientauth-foot (v9): the OWNER's on-foot transform stream -- UnreliableSequenced,
        /// sent by the shell session every tick (50 Hz), latest-wins by Seq server-side. pos/yaw ride the
        /// SAME quantizers as the player snapshot block, so an adopted claim replicates back bit-exact.
        /// recovAck echoes the last PlayerRecovEvent counter received (0 = none yet). Returns the seq
        /// (0 = not connected, nothing sent).</summary>
        public ushort SendPlayerState(Vector3 pos, float yawDegrees, float pitchDegrees, Vector3 velocity,
                                      byte buttons, bool grounded, byte recovAck)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            if (++_playerStateSeq == 0) _playerStateSeq = 1;
            var cmd = new PlayerStateCommand
            {
                Seq = _playerStateSeq, RecovAck = recovAck,
                Pos = pos, YawDegrees = yawDegrees, PitchDegrees = pitchDegrees,
                LinVel = velocity, Buttons = buttons, Grounded = grounded,
            };
            // v10 (mp-event-coalesce): fold the whole pending combat ring in, oldest-first. The ring array
            // is shared by reference (Write consumes only the first EventCount entries synchronously here) so
            // the steady-state carry allocates nothing; the reverse-order test seam takes the copy path.
            cmd.EventCount = (byte)_combatRingCount;
            if (CombatRingReverseOrder && _combatRingCount > 0)
            {
                var rev = new CarriedCombatEvent[_combatRingCount];
                for (int i = 0; i < _combatRingCount; i++) rev[i] = _combatRing[_combatRingCount - 1 - i];
                cmd.Events = rev;
            }
            else cmd.Events = _combatRing;
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandPlayerState, cmd.Write));
            // redundancy = keep re-including until acked; the seam clears here to model the pre-v10 send-once
            if (DisableCombatRedundancy) _combatRingCount = 0;
            return cmd.Seq;
        }

        ushort _playerStateSeq;

        public void Disconnect() => Session.Disconnect();
    }
}
