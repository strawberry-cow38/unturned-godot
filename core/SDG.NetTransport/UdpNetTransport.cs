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

        // Guarded for the same reason as UdpClientTransport.Send: this is the SERVER's traffic path, so an
        // EAGAIN/ENOBUFS on a fragment burst used to throw all the way out through TickReplication into
        // SimRoot.Frame and abandon that tick's replication for every remaining peer -- one slow peer's full
        // socket buffer stalling everyone. Losing a datagram is what the reliability layer above is for.
        public void Send(byte[] buffer, long size, ENetReliability reliability)
        {
            try { _socket.SendTo(buffer, 0, (int)size, SocketFlags.None, Remote); }
            catch (SocketException) { }
        }

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

        // ---- v2 status block: what the browser wants to show WITHOUT joining (strawberry 2026-09-15).
        // Server-set, all optional. Each is clamped on the way OUT as well as on the way in, because these are
        // operator-authored strings heading for other people's clients.
        public volatile string StatusMotd = "";       // operator message, clamped to MotdMaxBytes
        public volatile string StatusMap = "";
        public volatile string StatusGamemode = "";
        public volatile bool StatusPvp = true;
        public volatile bool StatusPassworded;
        public volatile int StatusMaxPing;            // 0 = no limit
        public volatile byte StatusProtocol;          // NetProtocol.Version of this server; 0 = not stated
        public const int MotdMaxBytes = 200;          // a browser row, not a broadcast channel
        public const int NameMaxBytes = 64;
        public const int ModeMaxBytes = 32;
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
                    ReplyStatus(buffer, n, (IPEndPoint)remote);   // n, not buffer.Length -- the REQUEST's size is the budget
                    continue;   // swallow it; the session never sees a status-req
                }
                size = n;
                transportConnection = new UdpTransportConnection(_socket, (IPEndPoint)remote);
                return true;
            }
        }

        // reqLen is the DATAGRAM length, not the receive buffer's. Passing the buffer would hand the reply a
        // 2 KB budget that no client ever asked for, which is precisely the amplification this guards against
        // -- and it is invisible until something asserts the sizes, because the reply still looks correct.
        void ReplyStatus(byte[] req, int reqLen, IPEndPoint remote)
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

            // ⭐ THE INVARIANT IS "NEVER LONGER THAN WHAT ASKED FOR IT". The first 20 bytes are unchanged and
            // always sent, so a pre-v2 client (24 B request) still parses exactly what it always did. A newer
            // client pads its request out, and that padding is what BUYS the extra block -- the response is
            // clamped to the request length, so this socket can never be an amplifier no matter what an
            // operator puts in the MOTD. That is the same property the original 24-vs-20 comment protected,
            // expressed as a rule instead of as two constants that have to be remembered together.
            var extra = new System.Collections.Generic.List<byte>();
            extra.Add(StatusProtocol);
            byte flags = (byte)((StatusPvp ? 1 : 0) | (StatusPassworded ? 2 : 0));
            extra.Add(flags);
            int mp = StatusMaxPing < 0 ? 0 : (StatusMaxPing > 65535 ? 65535 : StatusMaxPing);
            extra.Add((byte)(mp & 0xFF)); extra.Add((byte)((mp >> 8) & 0xFF));
            AppendString(extra, StatusMap, NameMaxBytes);
            AppendString(extra, StatusGamemode, ModeMaxBytes);
            AppendString(extra, StatusMotd, MotdMaxBytes);

            int want = 20 + extra.Count;
            int len = want <= reqLen ? want : 20;   // no room in the caller's budget -> send the core only
            var resp = new byte[len];
            resp[0] = StatusRespMagic[0]; resp[1] = StatusRespMagic[1]; resp[2] = StatusRespMagic[2]; resp[3] = StatusRespMagic[3];
            resp[4] = req[4]; resp[5] = req[5]; resp[6] = req[6]; resp[7] = req[7];   // echo the nonce (client rejects spoofed / stale replies)
            resp[8] = (byte)(players & 0xFF); resp[9] = (byte)((players >> 8) & 0xFF);
            resp[10] = (byte)(max & 0xFF); resp[11] = (byte)((max >> 8) & 0xFF);
            for (int i = 0; i < 8; i++) resp[12 + i] = (byte)((ver >> (8 * i)) & 0xFF);   // content version (little-endian)
            if (len > 20) extra.CopyTo(0, resp, 20, len - 20);
            try { _socket.SendTo(resp, 0, resp.Length, SocketFlags.None, remote); } catch (SocketException) { }
        }

        // length-prefixed UTF-8, truncated at a CHARACTER boundary. Clamping by byte count alone can cut a
        // multi-byte codepoint in half and hand the client a string that will not decode -- a server name in
        // Cyrillic is not an exotic case.
        static void AppendString(System.Collections.Generic.List<byte> into, string value, int maxBytes)
        {
            value ??= "";
            var enc = System.Text.Encoding.UTF8;
            byte[] b = enc.GetBytes(value);
            if (b.Length > maxBytes)
            {
                int chars = value.Length;
                while (chars > 0 && enc.GetByteCount(value.Substring(0, chars)) > maxBytes) chars--;
                b = enc.GetBytes(value.Substring(0, chars));
            }
            into.Add((byte)b.Length);
            into.AddRange(b);
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

        // Swallow a send failure rather than throwing into the sim. The socket is NON-BLOCKING and NetSession.Admit
        // blasts every fragment of a message back to back with no pacing, so a near-budget join snapshot is 100+
        // datagrams in one loop and can hit EAGAIN/ENOBUFS; an iptables REJECT gives EPERM the same way. The throw
        // used to unwind Transmit -> Admit -> TickReplication -> SimRoot.Frame, abandoning that whole tick's
        // replication for every remaining peer. ReplyStatus already caught this on the status socket -- the hazard
        // was handled at the one call site that carries no game traffic and missed at the ones that do. A dropped
        // datagram is exactly what the reliability layer above already exists to handle. Review 2026-08-16.
        public void Send(byte[] buffer, long size, ENetReliability reliability)
        {
            try { _socket.SendTo(buffer, 0, (int)size, SocketFlags.None, _server); }
            catch (SocketException) { }
        }

        public bool Receive(byte[] buffer, out long size)
        {
            size = 0;
            if (_socket == null) return false;
            // ONLY THE SERVER WE DIALLED. `from` was filled and then thrown away, so any datagram that reached this
            // ephemeral port was handed to HandleDatagram as if the server had sent it: one spoofed 12-byte
            // Control/Disconnect drops the player, and a spoofed UnreliableSequenced datagram is worse -- it
            // advances _lastUnreliableSeq, so the REAL server's next snapshots are discarded as stale. The server
            // side has never had this hole because peers are keyed on the connection itself. Review 2026-08-16.
            //
            // Foreign datagrams are SKIPPED rather than returned as "nothing to read": the caller drains with
            // `while (Receive(...))`, so bailing on the first stray would leave real packets queued behind it and
            // hand an attacker a cheap way to starve the loop by spraying.
            while (true)
            {
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                    if (from is IPEndPoint ip && (ip.Port != _server.Port || !ip.Address.Equals(_server.Address))) continue;
                    size = n; return true;
                }
                catch (SocketException) { return false; }   // WouldBlock -> nothing left this frame
            }
        }

        public bool TryGetIPv4Address(out IPv4Address address) { address = IPv4Address.Zero; return false; }
        public bool TryGetConnectionPort(out ushort connectionPort) { connectionPort = (ushort)_server.Port; return true; }
        public bool TryGetQueryPort(out ushort queryPort) { queryPort = 0; return false; }
        public bool TryGetPing(out int pingMs) { pingMs = 0; return false; }
    }
}
