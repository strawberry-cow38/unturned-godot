using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.NetTransport.Mem;
using UnityEngine;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Wires the Phase 1 NetSimHarness (deterministic MemTransport sim) together with the Phase 2 snapshot
    /// plane (SnapshotComposer/SnapshotApplier + the ack piggyback) for tests that need the real wire round
    /// trip -- packet loss/reorder, join-mid-scenario -- rather than just the framing logic in isolation.
    /// One shared NetIdMinter mints entity ids for the server-side mock systems (MP_PLAN §2.6: one flat,
    /// server-minted id space).
    ///
    /// Call order per tick: ServerTickApp() drains commands/acks received last network tick and composes +
    /// sends a fresh snapshot to every connected client, then ClientTickApp() lets each client drain +
    /// apply + ack, then Net.Step() actually advances the transport/session layer one 50 Hz tick. This
    /// mirrors how app-level sends precede Step() in the existing Phase 1 tests (ReliableChannelTests etc).
    /// </summary>
    sealed class ReplicationHarness
    {
        public readonly NetSimHarness Net;
        public readonly NetIdMinter Ids = new NetIdMinter();
        public readonly List<IReplicatedSystem> ServerSystems;
        public readonly SnapshotComposer Composer;
        public readonly CommandRegistry ServerCommands = new CommandRegistry();

        public sealed class ClientHandle
        {
            public NetClientSession Session;
            public List<IReplicatedSystem> Systems;
            public SnapshotApplier Applier;
        }
        public readonly List<ClientHandle> Clients = new List<ClientHandle>();

        long _tick;
        public long Tick => _tick;

        public ReplicationHarness(int seed, List<IReplicatedSystem> serverSystems,
                                  FaultyLinkConfig clientToServer = null, FaultyLinkConfig serverToClient = null)
        {
            Net = new NetSimHarness(seed, clientToServer, serverToClient);
            ServerSystems = serverSystems;
            Composer = new SnapshotComposer(ServerSystems);
            Composer.RegisterAck(ServerCommands);
        }

        /// <summary>Connect a fresh client (pumps ticks internally until the handshake completes) and start
        /// tracking it for the snapshot loop. makeClientSystems must build systems with the SAME SystemIds
        /// as ServerSystems (a subset is fine -- that's exactly the unknown-systemId-skip scenario).</summary>
        public ClientHandle AddClient(Func<List<IReplicatedSystem>> makeClientSystems, string name = "player")
        {
            var session = Net.ConnectClient(name);
            var systems = makeClientSystems();
            var handle = new ClientHandle
            {
                Session = session,
                Systems = systems,
                Applier = new SnapshotApplier(systems),
            };
            Clients.Add(handle);
            return handle;
        }

        void ServerTickApp()
        {
            foreach (var peer in Net.Server.Peers)
            {
                while (peer.TryReceiveReliable(out var msg)) ServerCommands.TryDispatch(msg, peer.PlayerId);
                while (peer.TryReceiveUnreliable(out var msg)) ServerCommands.TryDispatch(msg, peer.PlayerId);
            }

            _tick++;
            foreach (var peer in Net.Server.Peers)
            {
                var bytes = Composer.Compose(_tick, peer.PlayerId, Vector3.zero);
                peer.SendUnreliableSequenced(bytes);
            }
        }

        void ClientTickApp(ClientHandle client)
        {
            while (client.Session.TryReceiveUnreliable(out var msg))
            {
                if (client.Applier.Apply(msg, msg.Length))
                {
                    var ack = NetMessagePak.Pack(SnapshotComposer.AckCommandId,
                        w => w.WriteUInt32((uint)client.Applier.LastAppliedServerTick));
                    client.Session.SendUnreliableSequenced(ack);
                }
            }
        }

        /// <summary>One full app tick: server drains+composes+sends, every tracked client drains+applies+acks,
        /// then the underlying transport/session layer advances one 50 Hz tick.</summary>
        public void Step()
        {
            ServerTickApp();
            foreach (var c in Clients) ClientTickApp(c);
            Net.Step();
        }

        public void Step(int ticks)
        {
            for (int i = 0; i < ticks; i++) Step();
        }
    }
}
