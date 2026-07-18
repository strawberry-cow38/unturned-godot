using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.NetTransport;

namespace UnturnedGodot.Net
{
    /// <summary>A connected client as the server session sees it: transport connection + reliability engine.</summary>
    public sealed class NetPeer
    {
        public ushort PlayerId { get; internal set; }
        public string Name { get; internal set; }
        public ITransportConnection Connection { get; internal set; }
        public NetSession Session { get; internal set; }
        internal long CreatedTick;
        internal ulong SourceKey;   // per-source-IP accounting key (review H1)
        internal bool Proven;       // heard something beyond the Connect datagram that admitted it (review H1)

        public bool SendReliable(byte[] data, int offset, int count) => Session.SendReliable(data, offset, count);
        public bool SendReliable(byte[] data) => Session.SendReliable(data);
        public bool SendUnreliableSequenced(byte[] data, int offset, int count) => Session.SendUnreliableSequenced(data, offset, count);
        public bool SendUnreliableSequenced(byte[] data) => Session.SendUnreliableSequenced(data);
        public bool TryReceiveReliable(out byte[] message) => Session.TryReceiveReliable(out message);
        public bool TryReceiveUnreliable(out byte[] message) => Session.TryReceiveUnreliable(out message);
    }

    /// <summary>
    /// Server side of the session layer: one NetSession per client, keyed by endpoint (both
    /// UdpTransportConnection and MemTransportConnection are endpoint-equatable). Owns the handshake --
    /// Connect from an unknown endpoint becomes Accept{playerId, serverTick} or Reject{reason} -- plus
    /// per-peer keepalive/timeout; a peer silent for ~5 s is removed through the existing
    /// ServerTransportConnectionFailureCallback seam. Tick() once per 50 Hz tick.
    /// </summary>
    public sealed class NetServerSession
    {
        readonly IServerTransport _transport;
        readonly ServerTransportConnectionFailureCallback _connectionFailure;
        readonly byte _version;
        readonly int _maxPeers;
        readonly ulong _contentHash;
        // P3 holiday parity (wire v6): the server world's activeHoliday rides the Accept so the joining
        // client builds the SERVER's holiday-gated props/colliders, not its own wall clock's -- the one
        // static-collision decision the content hash cannot cover (it hashes content identity, not the
        // local-clock gate).
        readonly string _activeHoliday;
        readonly byte[] _rx = new byte[NetProtocol.MaxDatagramBytes];
        readonly NetPakReader _peekReader = new NetPakReader();
        readonly NetPakWriter _rawWriter = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };

        readonly Dictionary<ITransportConnection, NetPeer> _peersByConn = new Dictionary<ITransportConnection, NetPeer>();
        readonly List<NetPeer> _peers = new List<NetPeer>();
        readonly List<NetPeer> _timedOut = new List<NetPeer>();

        // review H1 flood guard state. "Half-open" on this one-datagram handshake means: admitted, but
        // nothing has been heard from that endpoint beyond the Connect that admitted it -- exactly what a
        // spoofed-source Connect blast produces (it can never hear the Accept). Real clients send more
        // datagrams within a tick or two, so they leave the pool immediately.
        readonly int _maxHalfOpen;
        readonly int _maxPeersPerSource;
        readonly Dictionary<ulong, int> _peersBySource = new Dictionary<ulong, int>();
        int _halfOpenCount;

        long _tick;
        ushort _nextPlayerId = 1;
        internal ushort NextPlayerIdForTest { get => _nextPlayerId; set => _nextPlayerId = value; }

        public long CurrentTick => _tick;
        public IReadOnlyList<NetPeer> Peers => _peers;
        public int HalfOpenCount => _halfOpenCount;

        public event Action<NetPeer> PeerConnected;
        public event Action<NetPeer, NetDisconnectReason> PeerDisconnected;

        public NetServerSession(IServerTransport transport,
                                ServerTransportConnectionFailureCallback connectionFailureCallback = null,
                                byte protocolVersion = NetProtocol.Version,
                                int maxPeers = 32,
                                ulong contentHash = 0,
                                int maxHalfOpen = 8,
                                int maxPeersPerSource = 8,
                                string activeHoliday = "")
        {
            _transport = transport;
            _connectionFailure = connectionFailureCallback;
            _version = protocolVersion;
            _maxPeers = maxPeers;
            _contentHash = contentHash;
            _maxHalfOpen = maxHalfOpen;
            _maxPeersPerSource = maxPeersPerSource;
            _activeHoliday = activeHoliday ?? "";
            _transport.Initialize(connectionFailureCallback);
        }

        public void Tick()
        {
            _tick++;
            while (_transport.Receive(_rx, out long size, out ITransportConnection conn))
            {
                if (_peersByConn.TryGetValue(conn, out var peer))
                {
                    // anything beyond the admitting Connect proves the endpoint is live (review H1)
                    if (!peer.Proven) { peer.Proven = true; _halfOpenCount--; }
                    peer.Session.HandleDatagram(_tick, _rx, (int)size);
                }
                else
                    HandleUnknownEndpoint(conn, _rx, (int)size);
            }

            foreach (var peer in _peers)
                peer.Session.Tick(_tick);

            // timeout + reassembly-abuse scan (collected first: removal mutates _peers)
            _timedOut.Clear();
            foreach (var peer in _peers)
            {
                long lastHeard = Math.Max(peer.Session.LastReceiveTick, peer.CreatedTick);
                if (_tick - lastHeard >= NetProtocol.TimeoutTicks || peer.Session.ReassemblyBudgetExceeded)
                    _timedOut.Add(peer);
            }
            foreach (var peer in _timedOut)
            {
                bool abuse = peer.Session.ReassemblyBudgetExceeded;
                if (NetLog.Enabled) NetLog.Warn($"peer {peer.PlayerId} ({peer.Connection.GetAddressString(true)}) dropped: " +
                                                (abuse ? "reliable reassembly budget exceeded (M1 guard)" : "timeout (5 s of silence)"));
                RemovePeer(peer, NetDisconnectReason.Timeout);
                _connectionFailure?.Invoke(peer.Connection,
                    abuse ? "reliable reassembly budget exceeded" : "session timeout (5 s of silence)", true);
            }
        }

        void HandleUnknownEndpoint(ITransportConnection conn, byte[] buffer, int length)
        {
            _peekReader.Reset(); // SetBufferSegment alone keeps the previous datagram's read position
            _peekReader.SetBufferSegment(buffer, length);
            if (!NetProtocol.TryReadHeader(_peekReader, out var h) || h.MagicByte != NetProtocol.Magic) return;
            if (h.Channel != NetChannel.Control) return; // stray non-handshake datagram from a stranger
            if (!_peekReader.ReadUInt8(out byte type) || (NetControlType)type != NetControlType.Connect) return;

            if (h.Version != _version)
            {
                if (NetLog.Enabled) NetLog.Info($"reject {conn.GetAddressString(true)}: version mismatch (theirs {h.Version}, ours {_version})");
                SendRawReject(conn, NetRejectReason.VersionMismatch);
                return;
            }
            if (_peers.Count >= _maxPeers)
            {
                if (NetLog.Enabled) NetLog.Info($"reject {conn.GetAddressString(true)}: server full ({_peers.Count}/{_maxPeers})");
                SendRawReject(conn, NetRejectReason.ServerFull);
                return;
            }
            // review H1 flood guard: a Connect blast must not hold the whole peer table. Cap concurrent
            // half-open (never-heard-from-again) sessions well below maxPeers, and cap live sessions per
            // source IP (real IPv4 when the transport knows it; endpoint-unique otherwise, e.g. MemTransport,
            // where the per-source cap deliberately never binds). Rejected as ServerFull -- no wire change.
            ulong sourceKey = SourceKeyOf(conn);
            _peersBySource.TryGetValue(sourceKey, out int fromSource);
            if (_halfOpenCount >= _maxHalfOpen || fromSource >= _maxPeersPerSource)
            {
                if (NetLog.Enabled) NetLog.Warn($"reject {conn.GetAddressString(true)}: flood guard " +
                                                $"(half-open {_halfOpenCount}/{_maxHalfOpen}, from this source {fromSource}/{_maxPeersPerSource})");
                SendRawReject(conn, NetRejectReason.ServerFull);
                return;
            }

            var peer = new NetPeer
            {
                PlayerId = MintPlayerId(),
                Connection = conn,
                CreatedTick = _tick,
                SourceKey = sourceKey,
            };
            _halfOpenCount++;
            _peersBySource[sourceKey] = fromSource + 1;
            peer.Session = new NetSession((b, l) => peer.Connection.Send(b, l, ENetReliability.Unreliable), _version)
            {
                KeepAliveEnabled = true,
            };
            peer.Session.ControlReceived += (t, r) => OnPeerControl(peer, t, r);
            _peersByConn[conn] = peer;
            _peers.Add(peer);

            // replay the Connect through the session so its seq enters the ack accounting,
            // and so the ControlReceived handler below sends the Accept
            peer.Session.HandleDatagram(_tick, buffer, length);
        }

        void OnPeerControl(NetPeer peer, NetControlType type, NetPakReader reader)
        {
            switch (type)
            {
                case NetControlType.Connect:
                    // first Connect names the peer + fires the join; duplicates (lost Accept) just re-Accept
                    if (peer.Name == null)
                    {
                        reader.ReadString(out string name);
                        // Phase 4 join gate (§2.2 "version + content hash -> Accept"): the client's content
                        // hash must equal ours. A truncated payload (older build with no hash) fails the
                        // read and lands on the same mismatch path.
                        if (!reader.ReadUInt64(out ulong contentHash) || contentHash != _contentHash)
                        {
                            if (NetLog.Enabled) NetLog.Info($"reject {peer.Connection.GetAddressString(true)}: content hash mismatch");
                            for (int i = 0; i < 3; i++)   // best-effort blast, like the other Reject paths
                                peer.Session.SendControl(NetControlType.Reject, w => w.WriteUInt8((byte)NetRejectReason.ContentMismatch));
                            RemovePeer(peer, NetDisconnectReason.Rejected);
                            return;
                        }
                        peer.Name = name ?? "";
                        if (NetLog.Enabled) NetLog.Info($"accept {peer.Connection.GetAddressString(true)} as player {peer.PlayerId} '{peer.Name}'");
                        PeerConnected?.Invoke(peer);
                    }
                    SendAccept(peer);
                    break;

                case NetControlType.Disconnect:
                    if (RemovePeer(peer, NetDisconnectReason.Requested))
                    {
                        if (NetLog.Enabled) NetLog.Info($"player {peer.PlayerId} ({peer.Connection.GetAddressString(true)}) disconnected (requested)");
                        _connectionFailure?.Invoke(peer.Connection, "client disconnected", false);
                    }
                    break;
            }
        }

        void SendAccept(NetPeer peer)
        {
            uint serverTick = (uint)_tick;
            peer.Session.SendControl(NetControlType.Accept, w =>
            {
                w.WriteUInt16(peer.PlayerId);
                w.WriteUInt32(serverTick);
                w.WriteString(_activeHoliday);   // wire v6: the server world's holiday -- the client builds THIS holiday's props/colliders
            });
        }

        // One-off Reject to an endpoint we refuse to build a session for. seq 1 / no acks: the rejected
        // client only ever parses the control payload out of this.
        void SendRawReject(ITransportConnection conn, NetRejectReason reason)
        {
            _rawWriter.Reset();
            NetProtocol.WriteHeader(_rawWriter, new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic,
                Version = _version,
                Channel = NetChannel.Control,
                Seq = 1,
                Ack = 0,
                AckBits = 0,
            });
            _rawWriter.WriteUInt8((byte)NetControlType.Reject);
            _rawWriter.WriteUInt8((byte)reason);
            _rawWriter.Flush();
            conn.Send(_rawWriter.buffer, _rawWriter.writeByteIndex, ENetReliability.Unreliable);
        }

        /// <summary>Server-initiated removal (kick): best-effort Disconnect blast, then drop the peer.</summary>
        public void DisconnectPeer(NetPeer peer)
        {
            if (!_peersByConn.ContainsKey(peer.Connection)) return;
            for (int i = 0; i < 3; i++)
                peer.Session.SendControl(NetControlType.Disconnect, w => w.WriteUInt8((byte)NetDisconnectReason.Kicked));
            RemovePeer(peer, NetDisconnectReason.Kicked);
        }

        /// <summary>Per-source accounting key (review H1): the real IPv4 when the transport knows it, else
        /// a key unique to the endpoint (high bit namespace keeps the two spaces disjoint). MemTransport
        /// connections have no IPv4, so mem tests get per-endpoint keys and the per-source cap never binds.</summary>
        static ulong SourceKeyOf(ITransportConnection conn)
            => conn.TryGetIPv4Address(out uint ip) ? ip : (1UL << 32) | (uint)conn.GetHashCode();

        /// <summary>review L2: _nextPlayerId is a ushort that wraps under sustained connect churn -- never
        /// mint 0 (the "none" sentinel all over the game code: empty seat, no attacker, ...) and never an
        /// id a live peer still holds (FindPeer returns first match, so a collision would cross-wire two
        /// players' commands and snapshots).</summary>
        ushort MintPlayerId()
        {
            for (int i = 0; i <= ushort.MaxValue; i++)
            {
                ushort id = _nextPlayerId++;
                if (id != 0 && FindPeer(id) == null) return id;
            }
            return 1; // unreachable: the peer table is capped far below 65535 ids
        }

        bool RemovePeer(NetPeer peer, NetDisconnectReason reason)
        {
            if (!_peersByConn.Remove(peer.Connection)) return false;
            _peers.Remove(peer);
            if (!peer.Proven) { peer.Proven = true; _halfOpenCount--; }
            if (_peersBySource.TryGetValue(peer.SourceKey, out int n))
            {
                if (n <= 1) _peersBySource.Remove(peer.SourceKey);
                else _peersBySource[peer.SourceKey] = n - 1;
            }
            peer.Session.KeepAliveEnabled = false;
            // PeerDisconnected only balances a PeerConnected: a peer dropped before completing the
            // handshake (content-hash reject, or a malformed Connect that idles into the timeout) never
            // announced a join, so it doesn't announce a leave.
            if (peer.Name != null) PeerDisconnected?.Invoke(peer, reason);
            return true;
        }

        public NetPeer FindPeer(ushort playerId)
        {
            foreach (var peer in _peers)
                if (peer.PlayerId == playerId) return peer;
            return null;
        }

        public void TearDown() => _transport.TearDown();
    }
}
