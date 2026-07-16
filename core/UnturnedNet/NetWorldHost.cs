using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// The server side of the Phase 3 world stack, engine-free: NetServerSession (handshake/reliability)
    /// + CommandRegistry (ack + MoveInput) + PlayerReplication + SnapshotComposer, wired the way MP_PLAN
    /// §2.5 orders a tick. The host (SimRoot registration in game/, or a test harness) calls the two
    /// phases in order every 50 Hz tick:
    ///   TickSimulation()  -- receive datagrams, dispatch commands (input-apply), step player movement
    ///   TickReplication() -- compose + send per-client snapshots; MUST run after all state mutation
    ///                        (registered LAST on the SimRoot, §2.5 "replication send last")
    /// Snapshots go out every SnapshotDivisorTicks-th tick (2 = the plan's 25 Hz default).
    /// </summary>
    public sealed class NetWorldServer
    {
        public readonly NetServerSession Session;
        public readonly CommandRegistry Commands = new CommandRegistry();
        public readonly NetIdMinter Ids = new NetIdMinter();
        public readonly PlayerReplication Players = new PlayerReplication();
        public readonly SnapshotComposer Composer;

        public int SnapshotDivisorTicks = 2; // 25 Hz at the 50 Hz tick (MP_PLAN §2.5)

        public NetWorldServer(IServerTransport transport,
                              ServerTransportConnectionFailureCallback connectionFailureCallback = null,
                              int maxPeers = 32)
        {
            Session = new NetServerSession(transport, connectionFailureCallback, maxPeers: maxPeers);
            Composer = new SnapshotComposer(new IReplicatedSystem[] { Players });
            Composer.RegisterAck(Commands);
            Commands.Register<MoveInput>(ReplicationIds.CommandMoveInput, MoveInput.TryRead,
                (sender, cmd) => Players.ServerQueueInput(sender, cmd));

            Session.PeerConnected += peer =>
                Players.ServerSpawn(Ids.Mint(), peer.PlayerId, SpawnPosition(peer.PlayerId), Session.CurrentTick);
            Session.PeerDisconnected += (peer, reason) =>
            {
                Players.ServerRemove(peer.PlayerId, Session.CurrentTick);
                Composer.ForgetClient(peer.PlayerId);
            };
        }

        // deterministic joins-in-a-row spacing so demo avatars don't spawn inside each other
        static Vector3 SpawnPosition(ushort playerId) => new Vector3(((playerId - 1) % 8) * 2f, 0f, 0f);

        /// <summary>Receive + input-apply + player sim (§2.5 order: input-apply then player sim).</summary>
        public void TickSimulation()
        {
            Session.Tick();
            foreach (var peer in Session.Peers)
            {
                while (peer.TryReceiveReliable(out byte[] msg)) Commands.TryDispatch(msg, peer.PlayerId);
                while (peer.TryReceiveUnreliable(out byte[] msg)) Commands.TryDispatch(msg, peer.PlayerId);
            }
            Players.ServerStep(Session.CurrentTick, (float)SimClock.FixedDelta);
        }

        /// <summary>Compose + send snapshots. Registered LAST on the tick so it captures this tick's final
        /// state; viewPos is the owning player's position (the §2.6 interest hook, policy AllRelevant v1).</summary>
        public void TickReplication()
        {
            if (SnapshotDivisorTicks > 1 && (Session.CurrentTick % SnapshotDivisorTicks) != 0) return;
            foreach (var peer in Session.Peers)
            {
                Vector3 viewPos = Players.TryGetByOwner(peer.PlayerId, out var e) ? e.Pos : Vector3.zero;
                peer.SendUnreliableSequenced(Composer.Compose(Session.CurrentTick, peer.PlayerId, viewPos));
            }
        }

        public void TearDown() => Session.TearDown();
    }

    /// <summary>
    /// The client side: NetClientSession + PlayerReplication replica + SnapshotApplier + the ack piggyback.
    /// Tick() once per 50 Hz tick; SendMoveInput() each tick while connected (the held-keys input model --
    /// the server keeps applying the latest input, so single loss costs nothing).
    /// </summary>
    public sealed class NetWorldClient
    {
        public readonly NetClientSession Session;
        public readonly PlayerReplication Players = new PlayerReplication();
        public readonly SnapshotApplier Applier;

        ushort _inputSeq;

        public NetWorldClient(IClientTransport transport, string playerName = "")
        {
            Session = new NetClientSession(transport, playerName);
            Applier = new SnapshotApplier(new IReplicatedSystem[] { Players });
        }

        public NetSessionState State => Session.State;
        public ushort PlayerId => Session.PlayerId;

        public void Connect() => Session.Connect();

        public void Tick()
        {
            Session.Tick();
            while (Session.TryReceiveUnreliable(out byte[] msg))
            {
                if (Applier.Apply(msg, msg.Length))
                {
                    // ack the applied tick so the server can send deltas against it (SnapshotComposer baseline)
                    Session.SendUnreliableSequenced(NetMessagePak.Pack(SnapshotComposer.AckCommandId,
                        w => w.WriteUInt32((uint)Applier.LastAppliedServerTick)));
                }
            }
            while (Session.TryReceiveReliable(out byte[] _)) { } // no reliable app messages yet (events land in Phase 5+)
        }

        public void SendMoveInput(float moveX, float moveY, float yawDegrees)
        {
            if (Session.State != NetSessionState.Connected) return;
            var cmd = new MoveInput { Seq = ++_inputSeq, MoveX = moveX, MoveY = moveY, YawDegrees = yawDegrees };
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandMoveInput, cmd.Write));
        }

        public void Disconnect() => Session.Disconnect();
    }
}
