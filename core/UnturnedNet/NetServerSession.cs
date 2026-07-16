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
        readonly byte[] _rx = new byte[NetProtocol.MaxDatagramBytes];
        readonly NetPakReader _peekReader = new NetPakReader();
        readonly NetPakWriter _rawWriter = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };

        readonly Dictionary<ITransportConnection, NetPeer> _peersByConn = new Dictionary<ITransportConnection, NetPeer>();
        readonly List<NetPeer> _peers = new List<NetPeer>();
        readonly List<NetPeer> _timedOut = new List<NetPeer>();

        long _tick;
        ushort _nextPlayerId = 1;

        public long CurrentTick => _tick;
        public IReadOnlyList<NetPeer> Peers => _peers;

        public event Action<NetPeer> PeerConnected;
        public event Action<NetPeer, NetDisconnectReason> PeerDisconnected;

        public NetServerSession(IServerTransport transport,
                                ServerTransportConnectionFailureCallback connectionFailureCallback = null,
                                byte protocolVersion = NetProtocol.Version,
                                int maxPeers = 32,
                                ulong contentHash = 0)
        {
            _transport = transport;
            _connectionFailure = connectionFailureCallback;
            _version = protocolVersion;
            _maxPeers = maxPeers;
            _contentHash = contentHash;
            _transport.Initialize(connectionFailureCallback);
        }

        public void Tick()
        {
            _tick++;
            while (_transport.Receive(_rx, out long size, out ITransportConnection conn))
            {
                if (_peersByConn.TryGetValue(conn, out var peer))
                    peer.Session.HandleDatagram(_tick, _rx, (int)size);
                else
                    HandleUnknownEndpoint(conn, _rx, (int)size);
            }

            foreach (var peer in _peers)
                peer.Session.Tick(_tick);

            // timeout scan (collected first: removal mutates _peers)
            _timedOut.Clear();
            foreach (var peer in _peers)
            {
                long lastHeard = Math.Max(peer.Session.LastReceiveTick, peer.CreatedTick);
                if (_tick - lastHeard >= NetProtocol.TimeoutTicks) _timedOut.Add(peer);
            }
            foreach (var peer in _timedOut)
            {
                RemovePeer(peer, NetDisconnectReason.Timeout);
                _connectionFailure?.Invoke(peer.Connection, "session timeout (5 s of silence)", true);
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
                SendRawReject(conn, NetRejectReason.VersionMismatch);
                return;
            }
            if (_peers.Count >= _maxPeers)
            {
                SendRawReject(conn, NetRejectReason.ServerFull);
                return;
            }

            var peer = new NetPeer
            {
                PlayerId = _nextPlayerId++,
                Connection = conn,
                CreatedTick = _tick,
            };
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
                            for (int i = 0; i < 3; i++)   // best-effort blast, like the other Reject paths
                                peer.Session.SendControl(NetControlType.Reject, w => w.WriteUInt8((byte)NetRejectReason.ContentMismatch));
                            RemovePeer(peer, NetDisconnectReason.Rejected);
                            return;
                        }
                        peer.Name = name ?? "";
                        PeerConnected?.Invoke(peer);
                    }
                    SendAccept(peer);
                    break;

                case NetControlType.Disconnect:
                    if (RemovePeer(peer, NetDisconnectReason.Requested))
                        _connectionFailure?.Invoke(peer.Connection, "client disconnected", false);
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

        bool RemovePeer(NetPeer peer, NetDisconnectReason reason)
        {
            if (!_peersByConn.Remove(peer.Connection)) return false;
            _peers.Remove(peer);
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
