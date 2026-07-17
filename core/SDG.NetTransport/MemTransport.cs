using System;
using System.Collections.Generic;
using System.Net;

namespace SDG.NetTransport.Mem
{
    // Paired in-memory transports implementing the real SDG.NetTransport interfaces -- the deterministic
    // test workhorse from MP_PLAN §4 Phase 1. No sockets, no threads, no wall clock: a MemNetwork hub owns
    // a tick counter the test harness advances, and every datagram crosses a FaultyLink whose loss/dup/
    // reorder/latency behavior is fully determined by the network's seed. Same seed + same call order =
    // byte-identical delivery schedule, every run.

    /// <summary>
    /// Adverse-network knobs for one direction of a MemNetwork. All randomness comes from the owning
    /// MemNetwork's seeded RNG, so a given (seed, config, send-order) is exactly reproducible.
    /// </summary>
    public sealed class FaultyLinkConfig
    {
        /// <summary>Probability [0,1] a datagram is dropped.</summary>
        public double LossProbability;
        /// <summary>Probability [0,1] a datagram is delivered twice (rolled independently of loss).</summary>
        public double DuplicateProbability;
        /// <summary>Base delivery delay in ticks (0 = deliverable the tick it was sent).</summary>
        public int LatencyTicks;
        /// <summary>Random extra 0..N ticks per datagram; a later send can outrun an earlier one = reordering.</summary>
        public int ReorderJitterTicks;
        /// <summary>Link stall: any datagram whose delivery would land before this network tick is held
        /// and becomes deliverable AT this tick instead, in send order -- nothing crosses during the
        /// window, then the whole backlog arrives as one in-order burst (the ~100 ms wifi stall that
        /// bunches five ticks of input into one delivery). 0 / past ticks = no effect.</summary>
        public long HoldUntilTick;
    }

    /// <summary>
    /// The in-memory "wire": one server socket + N client endpoints. The test harness calls Tick() once
    /// per 50 Hz sim tick; datagrams become receivable when their (send tick + latency + jitter) elapses.
    /// </summary>
    public sealed class MemNetwork
    {
        public int Seed { get; }
        public long CurrentTick { get; private set; }

        /// <summary>Link conditions applied to client->server datagrams.</summary>
        public FaultyLinkConfig ClientToServer { get; set; } = new FaultyLinkConfig();
        /// <summary>Link conditions applied to server->client datagrams.</summary>
        public FaultyLinkConfig ServerToClient { get; set; } = new FaultyLinkConfig();

        // observability for tests
        public long DeliveredCount { get; private set; }
        public long DroppedCount { get; private set; }
        public long DuplicatedCount { get; private set; }

        internal struct Packet
        {
            public long DeliverTick;
            public long Order;       // global send order; tie-breaks equal DeliverTicks so delivery is stable
            public int FromEndpoint;
            public byte[] Data;
        }

        readonly Random _rng;
        long _orderCounter;
        int _nextEndpoint = 1;
        bool _serverBound;
        readonly List<Packet> _serverInbox = new List<Packet>();
        readonly Dictionary<int, List<Packet>> _clientInboxes = new Dictionary<int, List<Packet>>();

        public MemNetwork(int seed = 12345)
        {
            Seed = seed;
            _rng = new Random(seed);
        }

        public void Tick() => CurrentTick++;

        internal void BindServer()
        {
            if (_serverBound) throw new InvalidOperationException("MemNetwork already has a bound server");
            _serverBound = true;
        }
        internal void UnbindServer() { _serverBound = false; _serverInbox.Clear(); }

        internal int RegisterClient()
        {
            int id = _nextEndpoint++;
            _clientInboxes[id] = new List<Packet>();
            return id;
        }
        internal void UnregisterClient(int endpoint) => _clientInboxes.Remove(endpoint);

        internal void SendToServer(int fromEndpoint, byte[] buffer, int length)
        {
            if (_serverBound) Transmit(ClientToServer, _serverInbox, fromEndpoint, buffer, length);
        }

        internal void SendToClient(int endpoint, byte[] buffer, int length)
        {
            if (_clientInboxes.TryGetValue(endpoint, out var inbox))
                Transmit(ServerToClient, inbox, 0, buffer, length);
        }

        void Transmit(FaultyLinkConfig link, List<Packet> inbox, int fromEndpoint, byte[] buffer, int length)
        {
            // Loss and duplication are rolled independently and unconditionally (keeps the RNG stream
            // stable for a given config): a lost-but-duplicated datagram still arrives exactly once.
            bool lost = _rng.NextDouble() < link.LossProbability;
            bool duplicated = _rng.NextDouble() < link.DuplicateProbability;
            if (lost) DroppedCount++;
            else Enqueue(link, inbox, fromEndpoint, buffer, length);
            if (duplicated) { DuplicatedCount++; Enqueue(link, inbox, fromEndpoint, buffer, length); }
        }

        void Enqueue(FaultyLinkConfig link, List<Packet> inbox, int fromEndpoint, byte[] buffer, int length)
        {
            var data = new byte[length];
            Buffer.BlockCopy(buffer, 0, data, 0, length);
            long jitter = link.ReorderJitterTicks > 0 ? _rng.Next(link.ReorderJitterTicks + 1) : 0;
            long deliverTick = CurrentTick + link.LatencyTicks + jitter;
            if (deliverTick < link.HoldUntilTick) deliverTick = link.HoldUntilTick;
            inbox.Add(new Packet
            {
                DeliverTick = deliverTick,
                Order = _orderCounter++,
                FromEndpoint = fromEndpoint,
                Data = data,
            });
        }

        // Pop the eligible packet with the smallest (DeliverTick, Order). Delivery is by arrival time, not
        // send order -- that's exactly how jitter manifests as reordering on the receiving side.
        internal bool TryReceive(List<Packet> inbox, out Packet packet)
        {
            int best = -1;
            for (int i = 0; i < inbox.Count; i++)
            {
                if (inbox[i].DeliverTick > CurrentTick) continue;
                if (best < 0
                    || inbox[i].DeliverTick < inbox[best].DeliverTick
                    || (inbox[i].DeliverTick == inbox[best].DeliverTick && inbox[i].Order < inbox[best].Order))
                    best = i;
            }
            if (best < 0) { packet = default; return false; }
            packet = inbox[best];
            inbox.RemoveAt(best);
            DeliveredCount++;
            return true;
        }

        internal bool TryReceiveServer(out Packet packet) => TryReceive(_serverInbox, out packet);

        internal bool TryReceiveClient(int endpoint, out Packet packet)
        {
            if (_clientInboxes.TryGetValue(endpoint, out var inbox)) return TryReceive(inbox, out packet);
            packet = default;
            return false;
        }
    }

    /// <summary>Server side of a client connection. Identity is by endpoint id (mirrors UdpTransportConnection's
    /// by-endpoint equality) so the session layer can key clients in a dictionary.</summary>
    public sealed class MemTransportConnection : ITransportConnection
    {
        readonly MemNetwork _net;
        public readonly int Endpoint;

        public MemTransportConnection(MemNetwork net, int endpoint) { _net = net; Endpoint = endpoint; }

        public void Send(byte[] buffer, long size, ENetReliability reliability)
            => _net.SendToClient(Endpoint, buffer, (int)size);

        public bool TryGetIPv4Address(out uint address) { address = 0; return false; }
        public bool TryGetPort(out ushort port) { port = (ushort)Endpoint; return true; }
        public bool TryGetSteamId(out ulong steamId) { steamId = 0; return false; }
        public IPAddress GetAddress() => IPAddress.Loopback;
        public string GetAddressString(bool withPort) => "mem:" + Endpoint;
        public void CloseConnection() { }

        public bool Equals(ITransportConnection other)
            => other is MemTransportConnection o && o.Endpoint == Endpoint && ReferenceEquals(o._net, _net);
        public override bool Equals(object obj) => Equals(obj as ITransportConnection);
        public override int GetHashCode() => Endpoint;
    }

    public sealed class MemServerTransport : IServerTransport
    {
        readonly MemNetwork _net;
        bool _bound;

        public MemServerTransport(MemNetwork net) { _net = net; }

        public void Initialize(ServerTransportConnectionFailureCallback connectionFailureCallback)
        {
            _net.BindServer();
            _bound = true;
        }

        public void TearDown()
        {
            if (_bound) { _net.UnbindServer(); _bound = false; }
        }

        public bool Receive(byte[] buffer, out long size, out ITransportConnection transportConnection)
        {
            size = 0; transportConnection = null;
            if (!_bound || !_net.TryReceiveServer(out var packet)) return false;
            Buffer.BlockCopy(packet.Data, 0, buffer, 0, packet.Data.Length);
            size = packet.Data.Length;
            // fresh connection object per datagram, like UdpServerTransport -- equality is by endpoint
            transportConnection = new MemTransportConnection(_net, packet.FromEndpoint);
            return true;
        }
    }

    public sealed class MemClientTransport : IClientTransport
    {
        readonly MemNetwork _net;
        int _endpoint; // 0 = not registered

        public MemClientTransport(MemNetwork net) { _net = net; }

        public void Initialize(ClientTransportReady callback, ClientTransportFailure failureCallback)
        {
            _endpoint = _net.RegisterClient();
            callback?.Invoke();
        }

        public void TearDown()
        {
            if (_endpoint != 0) { _net.UnregisterClient(_endpoint); _endpoint = 0; }
        }

        public void Send(byte[] buffer, long size, ENetReliability reliability)
        {
            if (_endpoint != 0) _net.SendToServer(_endpoint, buffer, (int)size);
        }

        public bool Receive(byte[] buffer, out long size)
        {
            size = 0;
            if (_endpoint == 0 || !_net.TryReceiveClient(_endpoint, out var packet)) return false;
            Buffer.BlockCopy(packet.Data, 0, buffer, 0, packet.Data.Length);
            size = packet.Data.Length;
            return true;
        }

        public bool TryGetIPv4Address(out Unturned.SystemEx.IPv4Address address)
        {
            address = Unturned.SystemEx.IPv4Address.Zero;
            return false;
        }
        public bool TryGetConnectionPort(out ushort connectionPort) { connectionPort = 0; return false; }
        public bool TryGetQueryPort(out ushort queryPort) { queryPort = 0; return false; }
        public bool TryGetPing(out int pingMs) { pingMs = 0; return false; }
    }
}
