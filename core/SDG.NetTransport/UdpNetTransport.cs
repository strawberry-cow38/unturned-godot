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
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
                size = n;
                transportConnection = new UdpTransportConnection(_socket, (IPEndPoint)remote);
                return true;
            }
            catch (SocketException) { return false; } // WouldBlock == no datagram pending
        }
    }

    public sealed class UdpClientTransport : IClientTransport
    {
        readonly IPEndPoint _server;
        Socket _socket;

        public UdpClientTransport(string host, ushort port) { _server = new IPEndPoint(IPAddress.Parse(host), port); }

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
