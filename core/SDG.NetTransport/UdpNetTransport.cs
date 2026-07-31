using System.Net;
using System.Net.Sockets;
using Unturned.SystemEx; // IPv4Address

namespace SDG.NetTransport.Udp
{
    // A standalone UDP transport implementing the real SDG.NetTransport interfaces -- the "transport rework"
    // the plan called for: it replaces the Steam-coupled SystemSockets impl so a headless dedicated server +
    // clients can exchange NetPak buffers with no Steam dependency. Poll-based Receive, matching the interface,
    // so ported NetMessaging/NetGen code can drive it unchanged.

    public sealed class UdpTransportConnection : ITransportConnection
    {
        readonly Socket _socket;
        public readonly IPEndPoint Remote;

        public UdpTransportConnection(Socket socket, IPEndPoint remote) { _socket = socket; Remote = remote; }

        public void Send(byte[] buffer, long size, ENetReliability reliability)
            => _socket.SendTo(buffer, 0, (int)size, SocketFlags.None, Remote);

        public bool TryGetIPv4Address(out uint address)
        {
            byte[] b = Remote.Address.MapToIPv4().GetAddressBytes();
            address = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            return true;
        }
        public bool TryGetPort(out ushort port) { port = (ushort)Remote.Port; return true; }
        public bool TryGetSteamId(out ulong steamId) { steamId = 0; return false; }
        public System.Net.IPAddress GetAddress() => Remote.Address;
        public string GetAddressString(bool withPort) => withPort ? Remote.ToString() : Remote.Address.ToString();
        public void CloseConnection() { }

        // identity by endpoint so the server can key clients on their connection
        public bool Equals(ITransportConnection other) => other is UdpTransportConnection o && o.Remote.Equals(Remote);
        public override bool Equals(object obj) => Equals(obj as ITransportConnection);
        public override int GetHashCode() => Remote.GetHashCode();
    }

    public sealed class UdpServerTransport : IServerTransport
    {
        readonly ushort _port;
        Socket _socket;

        // --- server-browser status query (unconnected, pre-handshake ping + player count) ---
        // A client that hasn't joined can't read Session.Peers or the session RTT, so it sends a tiny status-req and
        // we answer it HERE in the transport, below the session (no NetWorldHost involvement). Anti-abuse: the reply
        // (12 B) is smaller than the padded request (>= 24 B) so it can't be an amplification vector, and replies are
        // rate-limited to one per source IP per StatusMinIntervalMs. StatusPlayerCount is pushed in by the host each
        // tick (DedicatedServer). A status-req is swallowed -- never surfaced to the session.
        static readonly byte[] StatusReqMagic = { (byte)'U', (byte)'G', (byte)'S', (byte)'Q' };
        static readonly byte[] StatusRespMagic = { (byte)'U', (byte)'G', (byte)'S', (byte)'R' };
        const int StatusReqMinSize = 24;          // 4 magic + 4 nonce + 16 pad -> req >= resp (no amplification)
        const long StatusMinIntervalMs = 1000;    // per-source-IP rate limit
        public volatile int StatusPlayerCount;    // live player count, pushed by the host each tick
        public volatile int StatusMaxPlayers = 24;
        public ulong StatusVersion;               // server content identity (NetContent.Hash); the browser grays + blocks join on a mismatch
        readonly System.Collections.Generic.Dictionary<uint, long> _statusLastMs = new();
        long _statusPruneMs;

        public UdpServerTransport(ushort port) { _port = port; }

        public void Initialize(ServerTransportConnectionFailureCallback connectionFailureCallback)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        public void TearDown() { _socket?.Close(); _socket = null; }

        public bool Receive(byte[] buffer, out long size, out ITransportConnection transportConnection)
        {
            size = 0; transportConnection = null;
            if (_socket == null) return false;
            // A status-req is answered + swallowed here; keep polling until a real game datagram or the socket drains.
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote); }
                catch (SocketException) { return false; }   // WouldBlock == no datagram pending
                if (n >= StatusReqMinSize && buffer[0] == StatusReqMagic[0] && buffer[1] == StatusReqMagic[1]
                    && buffer[2] == StatusReqMagic[2] && buffer[3] == StatusReqMagic[3])
                {
                    ReplyStatus(buffer, (IPEndPoint)remote);
                    continue;   // swallow it; the session never sees a status-req
                }
                size = n;
                transportConnection = new UdpTransportConnection(_socket, (IPEndPoint)remote);
                return true;
            }
        }

        void ReplyStatus(byte[] req, IPEndPoint remote)
        {
            long now = System.Environment.TickCount64;
            byte[] ab = remote.Address.MapToIPv4().GetAddressBytes();
            uint ip = (uint)((ab[0] << 24) | (ab[1] << 16) | (ab[2] << 8) | ab[3]);
            if (_statusLastMs.TryGetValue(ip, out long last) && now - last < StatusMinIntervalMs) return;   // rate limit per source
            _statusLastMs[ip] = now;
            if (now - _statusPruneMs > 10000) { _statusPruneMs = now; PruneStatus(now); }
            int players = StatusPlayerCount < 0 ? 0 : (StatusPlayerCount > 65535 ? 65535 : StatusPlayerCount);
            int max = StatusMaxPlayers < 0 ? 0 : (StatusMaxPlayers > 65535 ? 65535 : StatusMaxPlayers);
            ulong ver = StatusVersion;
            var resp = new byte[20];   // magic(4)+nonce(4)+players(2)+max(2)+version(8) = 20 B, still < the 24 B request (no amplification)
            resp[0] = StatusRespMagic[0]; resp[1] = StatusRespMagic[1]; resp[2] = StatusRespMagic[2]; resp[3] = StatusRespMagic[3];
            resp[4] = req[4]; resp[5] = req[5]; resp[6] = req[6]; resp[7] = req[7];   // echo the nonce (client rejects spoofed / stale replies)
            resp[8] = (byte)(players & 0xFF); resp[9] = (byte)((players >> 8) & 0xFF);
            resp[10] = (byte)(max & 0xFF); resp[11] = (byte)((max >> 8) & 0xFF);
            for (int i = 0; i < 8; i++) resp[12 + i] = (byte)((ver >> (8 * i)) & 0xFF);   // content version (little-endian)
            try { _socket.SendTo(resp, 0, resp.Length, SocketFlags.None, remote); } catch (SocketException) { }
        }

        void PruneStatus(long now)
        {
            System.Collections.Generic.List<uint> stale = null;
            foreach (var kv in _statusLastMs) if (now - kv.Value > 10000) (stale ??= new()).Add(kv.Key);
            if (stale != null) foreach (uint k in stale) _statusLastMs.Remove(k);
        }
    }

    public sealed class UdpClientTransport : IClientTransport
    {
        readonly IPEndPoint _server;
        Socket _socket;

        public UdpClientTransport(string host, ushort port) { _server = new IPEndPoint(ResolveHost(host), port); }

        // Accept a literal IP ("127.0.0.1") OR a hostname ("claw.bitvox.me"). IPAddress.Parse throws a
        // FormatException on a name, so fall back to DNS and prefer an IPv4 address to match the
        // InterNetwork (IPv4) socket created in Initialize.
        static IPAddress ResolveHost(string host)
        {
            if (IPAddress.TryParse(host, out var literal)) return literal;
            var resolved = Dns.GetHostAddresses(host);
            return System.Array.Find(resolved, a => a.AddressFamily == AddressFamily.InterNetwork) ?? resolved[0];
        }

        public void Initialize(ClientTransportReady callback, ClientTransportFailure failureCallback)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // ephemeral local port
            callback?.Invoke();
        }
        public void TearDown() { _socket?.Close(); _socket = null; }

        public void Send(byte[] buffer, long size, ENetReliability reliability)
            => _socket.SendTo(buffer, 0, (int)size, SocketFlags.None, _server);

        public bool Receive(byte[] buffer, out long size)
        {
            size = 0;
            if (_socket == null) return false;
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                size = n; return true;
            }
            catch (SocketException) { return false; }
        }

        public bool TryGetIPv4Address(out IPv4Address address) { address = IPv4Address.Zero; return false; }
        public bool TryGetConnectionPort(out ushort connectionPort) { connectionPort = (ushort)_server.Port; return true; }
        public bool TryGetQueryPort(out ushort queryPort) { queryPort = 0; return false; }
        public bool TryGetPing(out int pingMs) { pingMs = 0; return false; }
    }
}
