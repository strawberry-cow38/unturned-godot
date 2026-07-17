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
        public readonly ServerCombat Combat;
        public readonly SnapshotComposer Composer;

        public int SnapshotDivisorTicks = 2; // 25 Hz at the 50 Hz tick (MP_PLAN §2.5)

        public NetWorldServer(IServerTransport transport,
                              ServerTransportConnectionFailureCallback connectionFailureCallback = null,
                              int maxPeers = 32,
                              ulong contentHash = 0)
        {
            Session = new NetServerSession(transport, connectionFailureCallback, maxPeers: maxPeers, contentHash: contentHash);
            Composer = new SnapshotComposer(new IReplicatedSystem[] { Players, CombatState, Zombies, Projectiles });
            Composer.RegisterAck(Commands);
            Combat = new ServerCombat(Players, CombatState, Zombies, Projectiles, Ids, BroadcastEvent, SendEventTo);
            Commands.Register<MoveInput>(ReplicationIds.CommandMoveInput, MoveInput.TryRead,
                (sender, cmd) => Players.ServerQueueInput(sender, cmd),
                validate: (sender, cmd) => CombatState.IsAlive(sender));   // a corpse's inputs drop at the choke point
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
                var spawn = SpawnPosition(peer.PlayerId);
                var e = Players.ServerSpawn(Ids.Mint(), peer.PlayerId, spawn, Session.CurrentTick);
                CombatState.ServerAdd(peer.PlayerId, e.Pos, Combat.GunFor(peer.PlayerId).MagCapacity, Session.CurrentTick);
                // The join flow (MP_PLAN §4 Phase 4): Accept -> reliable FULL snapshot -> deltas. The full
                // snapshot rides ReliableOrdered where fragmentation is safe (§2.2) -- a lost datagram
                // retransmits instead of the client waiting out unreliable full-resends. Composed HERE so
                // the joiner's own freshly-spawned avatar is inside it; the reliable-channel budget means a
                // big world (many zombies) still fits the join snapshot whole.
                var joinSnapshot = Composer.Compose(Session.CurrentTick, peer.PlayerId, e.Pos,
                                                    maxBytes: NetProtocol.MaxReliableMessageBytes / 2);
                peer.SendReliable(NetMessagePak.Pack(ReplicationIds.EventJoinSnapshot, w =>
                {
                    // explicit byteLen prefix: NetPakReader.RemainingSegmentLength is imprecise (the reader
                    // pre-buffers 32-bit words), so like every other frame on this stack the payload carries
                    // its own length
                    w.WriteUInt16((ushort)joinSnapshot.Length);
                    w.WriteBytes(joinSnapshot, 0, joinSnapshot.Length);
                }, bufferSize: joinSnapshot.Length + 8));
            };
            Session.PeerDisconnected += (peer, reason) =>
            {
                Players.ServerRemove(peer.PlayerId, Session.CurrentTick);
                CombatState.ServerRemove(peer.PlayerId, Session.CurrentTick);
                Composer.ForgetClient(peer.PlayerId);
            };
        }

        // deterministic joins-in-a-row spacing so demo avatars don't spawn inside each other
        static Vector3 SpawnPosition(ushort playerId) => new Vector3(((playerId - 1) % 8) * 2f, 0f, 0f);

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
            Combat.Step(Session.CurrentTick);
        }

        /// <summary>Compose + send snapshots. Registered LAST on the tick so it captures this tick's final
        /// state; viewPos is the owning player's position (the §2.6 interest hook, policy AllRelevant v1).
        /// A peer with no acked baseline yet is mid-join: its world state is the reliable join snapshot
        /// already in flight (retransmitted until acked), so unreliable snapshots hold off until the first
        /// ack -- the join path is reliable BY CONSTRUCTION, not by racing the unreliable stream.</summary>
        public void TickReplication()
        {
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

        ushort _inputSeq;
        ushort _combatSeq;

        public NetWorldClient(IClientTransport transport, string playerName = "", ulong contentHash = 0)
        {
            Session = new NetClientSession(transport, playerName, contentHash: contentHash);
            Applier = new SnapshotApplier(new IReplicatedSystem[] { Players, CombatState, Zombies, Projectiles });
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
        /// sent) so the shell can record its prediction under the same seq.</summary>
        public ushort SendMoveInput(float moveX, float moveY, float yawDegrees)
        {
            if (Session.State != NetSessionState.Connected) return 0;
            var cmd = new MoveInput { Seq = ++_inputSeq, MoveX = moveX, MoveY = moveY, YawDegrees = yawDegrees };
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

        public void Disconnect() => Session.Disconnect();
    }
}
