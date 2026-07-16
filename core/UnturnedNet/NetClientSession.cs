using System;
using SDG.NetPak;
using SDG.NetTransport;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Client side of the session layer: one NetSession over an IClientTransport, plus the connect
    /// handshake (MP_PLAN §2.2 lifecycle): Connect{name, contentHash} retried every 0.5 s ->
    /// Accept{playerId, serverTick} or Reject{reason}; then 1 Hz keepalives and a 5 s silence timeout.
    /// Tick() once per 50 Hz tick. The content hash (wire v2, Phase 4) is the map/content identity --
    /// a server whose hash differs rejects the join with ContentMismatch before any state flows.
    /// </summary>
    public sealed class NetClientSession
    {
        readonly IClientTransport _transport;
        readonly NetSession _session;
        readonly string _playerName;
        readonly ulong _contentHash;
        readonly byte[] _rx = new byte[NetProtocol.MaxDatagramBytes];

        long _tick;
        long _connectStartTick;
        long _lastConnectSendTick;

        public NetSessionState State { get; private set; } = NetSessionState.Disconnected;
        public NetDisconnectReason DisconnectReason { get; private set; }
        public NetRejectReason RejectReason { get; private set; }
        public ushort PlayerId { get; private set; }
        public uint ServerTickAtAccept { get; private set; }
        public long CurrentTick => _tick;
        public NetSession Session => _session;

        public NetClientSession(IClientTransport transport, string playerName = "", byte protocolVersion = NetProtocol.Version,
                                ulong contentHash = 0)
        {
            _transport = transport;
            _playerName = playerName ?? "";
            _contentHash = contentHash;
            _session = new NetSession((buffer, length) => _transport.Send(buffer, length, ENetReliability.Unreliable), protocolVersion);
            _session.ControlReceived += OnControl;
            _transport.Initialize(null, OnTransportFailure);
        }

        void OnTransportFailure(string message)
        {
            if (State == NetSessionState.Disconnected) return;
            State = NetSessionState.Disconnected;
            DisconnectReason = NetDisconnectReason.Timeout;
        }

        /// <summary>Begin the handshake. The session reports progress via State.</summary>
        public void Connect()
        {
            if (State != NetSessionState.Disconnected) return;
            State = NetSessionState.Connecting;
            DisconnectReason = NetDisconnectReason.None;
            RejectReason = NetRejectReason.None;
            _connectStartTick = _tick;
            SendConnect();
        }

        void SendConnect()
        {
            _session.SendControl(NetControlType.Connect, w =>
            {
                w.WriteString(_playerName);
                w.WriteUInt64(_contentHash);   // wire v2: content identity, validated server-side (§2.2)
            });
            _lastConnectSendTick = _tick;
        }

        public void Tick()
        {
            _tick++;
            while (_transport.Receive(_rx, out long size))
                _session.HandleDatagram(_tick, _rx, (int)size);

            switch (State)
            {
                case NetSessionState.Connecting:
                    if (_tick - _connectStartTick >= NetProtocol.ConnectTimeoutTicks)
                    {
                        State = NetSessionState.Disconnected;
                        DisconnectReason = NetDisconnectReason.Timeout;
                    }
                    else if (_tick - _lastConnectSendTick >= NetProtocol.ConnectRetryTicks)
                    {
                        SendConnect();
                    }
                    break;

                case NetSessionState.Connected:
                    _session.Tick(_tick);
                    if (_tick - _session.LastReceiveTick >= NetProtocol.TimeoutTicks)
                    {
                        State = NetSessionState.Disconnected;
                        DisconnectReason = NetDisconnectReason.Timeout;
                        _session.KeepAliveEnabled = false;
                    }
                    break;
            }
        }

        void OnControl(NetControlType type, NetPakReader reader)
        {
            switch (type)
            {
                case NetControlType.Accept:
                    if (!reader.ReadUInt16(out ushort playerId) || !reader.ReadUInt32(out uint serverTick)) return;
                    if (State != NetSessionState.Connecting) return; // duplicate Accept (our ack got lost)
                    PlayerId = playerId;
                    ServerTickAtAccept = serverTick;
                    State = NetSessionState.Connected;
                    _session.KeepAliveEnabled = true;
                    break;

                case NetControlType.Reject:
                    if (State != NetSessionState.Connecting) return;
                    reader.ReadUInt8(out byte reason);
                    RejectReason = (NetRejectReason)reason;
                    State = NetSessionState.Disconnected;
                    DisconnectReason = NetDisconnectReason.Rejected;
                    break;

                case NetControlType.Disconnect:
                    if (State == NetSessionState.Disconnected) return;
                    State = NetSessionState.Disconnected;
                    DisconnectReason = NetDisconnectReason.Kicked;
                    _session.KeepAliveEnabled = false;
                    break;
            }
        }

        // app-facing message API (valid once Connected; delegating unconditionally is harmless)
        public bool SendReliable(byte[] data, int offset, int count) => _session.SendReliable(data, offset, count);
        public bool SendReliable(byte[] data) => _session.SendReliable(data);
        public bool SendUnreliableSequenced(byte[] data, int offset, int count) => _session.SendUnreliableSequenced(data, offset, count);
        public bool SendUnreliableSequenced(byte[] data) => _session.SendUnreliableSequenced(data);
        public bool TryReceiveReliable(out byte[] message) => _session.TryReceiveReliable(out message);
        public bool TryReceiveUnreliable(out byte[] message) => _session.TryReceiveUnreliable(out message);

        /// <summary>Graceful shutdown: blast a few Disconnects (best effort -- if all are lost the server's
        /// idle timeout cleans up) and tear the transport down.</summary>
        public void Disconnect()
        {
            if (State == NetSessionState.Connected || State == NetSessionState.Connecting)
            {
                for (int i = 0; i < 3; i++)
                    _session.SendControl(NetControlType.Disconnect, w => w.WriteUInt8((byte)NetDisconnectReason.Requested));
                State = NetSessionState.Disconnected;
                DisconnectReason = NetDisconnectReason.Requested;
                _session.KeepAliveEnabled = false;
            }
            _transport.TearDown();
        }
    }
}
