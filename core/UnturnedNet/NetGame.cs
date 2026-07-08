using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.NetTransport.Udp;

namespace UnturnedGodot.Net
{
    // The server-authoritative 2-player layer on top of the ported NetPak + the UDP transport.
    // Clients send their PlayerState each tick; the server assigns ids, collects states, and broadcasts the
    // whole world back; clients apply it to see each other. Engine-agnostic + headless-testable; the Godot
    // side just feeds local state in and spawns remote-player nodes from Remote. (NetGen RPCs come later;
    // this is the raw state channel that proves the loop.)

    public enum MsgType : byte { ClientState = 1, WorldState = 2 }

    public struct PlayerState
    {
        public byte Id;
        public uint Tick;
        public float X, Y, Z, Yaw;

        public void Write(NetPakWriter w)
        {
            w.WriteBits(Id, 8);
            w.WriteBits(Tick, 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(X), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(Y), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(Z), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(Yaw), 32);
        }

        public static PlayerState Read(NetPakReader r)
        {
            var s = new PlayerState();
            r.ReadBits(8, out uint id); s.Id = (byte)id;
            r.ReadBits(32, out uint t); s.Tick = t;
            r.ReadBits(32, out uint x); s.X = BitConverter.UInt32BitsToSingle(x);
            r.ReadBits(32, out uint y); s.Y = BitConverter.UInt32BitsToSingle(y);
            r.ReadBits(32, out uint z); s.Z = BitConverter.UInt32BitsToSingle(z);
            r.ReadBits(32, out uint yaw); s.Yaw = BitConverter.UInt32BitsToSingle(yaw);
            return s;
        }
    }

    // Authoritative server: owns player ids + the world snapshot.
    public sealed class NetServer
    {
        readonly UdpServerTransport _transport;
        readonly Dictionary<ITransportConnection, byte> _clients = new();
        readonly Dictionary<byte, PlayerState> _states = new();
        readonly byte[] _rx = new byte[2048];
        byte _nextId = 1;

        public int ClientCount => _clients.Count;
        public IReadOnlyDictionary<byte, PlayerState> States => _states;

        public NetServer(ushort port)
        {
            _transport = new UdpServerTransport(port);
            _transport.Initialize(null);
        }

        // Drain all pending client datagrams into the world snapshot.
        public void Poll()
        {
            while (_transport.Receive(_rx, out long size, out ITransportConnection conn))
            {
                if (!_clients.TryGetValue(conn, out byte id)) { id = _nextId++; _clients[conn] = id; }
                var r = new NetPakReader();
                r.SetBufferSegment(_rx, (int)size);
                r.ReadBits(8, out uint type);
                if ((MsgType)type == MsgType.ClientState)
                {
                    var st = PlayerState.Read(r);
                    st.Id = id; // server is authoritative over id
                    _states[id] = st;
                }
            }
        }

        // Broadcast the full world snapshot to every connected client.
        public void Broadcast()
        {
            var w = new NetPakWriter { buffer = new byte[2048] };
            w.Reset();
            w.WriteBits((byte)MsgType.WorldState, 8);
            w.WriteBits((uint)_states.Count, 8);
            foreach (var st in _states.Values) st.Write(w);
            w.Flush();
            foreach (var conn in _clients.Keys)
                conn.Send(w.buffer, w.writeByteIndex, ENetReliability.Unreliable);
        }

        public void TearDown() => _transport.TearDown();
    }

    // Client: sends local state, applies the world snapshot into Remote (id -> state, includes self echo).
    public sealed class NetClient
    {
        readonly UdpClientTransport _transport;
        readonly byte[] _rx = new byte[2048];

        public readonly Dictionary<byte, PlayerState> Remote = new();

        public NetClient(string host, ushort port)
        {
            _transport = new UdpClientTransport(host, port);
            _transport.Initialize(null, null);
        }

        public void SendState(PlayerState s)
        {
            var w = new NetPakWriter { buffer = new byte[128] };
            w.Reset();
            w.WriteBits((byte)MsgType.ClientState, 8);
            s.Write(w);
            w.Flush();
            _transport.Send(w.buffer, w.writeByteIndex, ENetReliability.Unreliable);
        }

        public void Poll()
        {
            while (_transport.Receive(_rx, out long size))
            {
                var r = new NetPakReader();
                r.SetBufferSegment(_rx, (int)size);
                r.ReadBits(8, out uint type);
                if ((MsgType)type == MsgType.WorldState)
                {
                    r.ReadBits(8, out uint count);
                    Remote.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        var st = PlayerState.Read(r);
                        Remote[st.Id] = st;
                    }
                }
            }
        }

        public void TearDown() => _transport.TearDown();
    }
}
