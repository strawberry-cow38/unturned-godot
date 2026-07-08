using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.NetTransport.Udp;

namespace UnturnedGodot.Net
{
    // Server-authoritative multiplayer layer on the ported NetPak + UDP transport. Clients send their
    // PlayerState each tick; the server assigns ids, runs the authoritative ZOMBIE sim (zombies chase the
    // nearest player), and broadcasts the whole world (players + zombies); clients render it. Engine-agnostic
    // + headless-testable. (NetGen RPCs/reliability come later; this is the raw authoritative state channel.)

    public enum MsgType : byte { ClientState = 1, WorldState = 2, Fire = 3, Welcome = 4 }

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

    public struct ZombieState
    {
        public float X, Y, Z;
        public void Write(NetPakWriter w)
        {
            w.WriteBits(BitConverter.SingleToUInt32Bits(X), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(Y), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(Z), 32);
        }
        public static ZombieState Read(NetPakReader r)
        {
            var s = new ZombieState();
            r.ReadBits(32, out uint x); s.X = BitConverter.UInt32BitsToSingle(x);
            r.ReadBits(32, out uint y); s.Y = BitConverter.UInt32BitsToSingle(y);
            r.ReadBits(32, out uint z); s.Z = BitConverter.UInt32BitsToSingle(z);
            return s;
        }
    }

    // Authoritative server: owns player ids, the zombie sim, and the world snapshot.
    public sealed class NetServer
    {
        readonly UdpServerTransport _transport;
        readonly Dictionary<ITransportConnection, byte> _clients = new();
        readonly Dictionary<byte, PlayerState> _states = new();
        readonly List<ZombieState> _zombies = new();
        readonly byte[] _rx = new byte[4096];
        byte _nextId = 1;
        float _spawnCd;
        readonly Random _rng = new Random(12345);

        public int ClientCount => _clients.Count;
        public int ZombieCount => _zombies.Count;
        public int Kills { get; private set; }
        public IReadOnlyDictionary<byte, PlayerState> States => _states;

        public NetServer(ushort port)
        {
            _transport = new UdpServerTransport(port);
            _transport.Initialize(null);
        }

        public void Poll()
        {
            while (_transport.Receive(_rx, out long size, out ITransportConnection conn))
            {
                if (!_clients.TryGetValue(conn, out byte id))
                {
                    id = _nextId++; _clients[conn] = id;
                    var ww = new NetPakWriter { buffer = new byte[8] };
                    ww.Reset(); ww.WriteBits((byte)MsgType.Welcome, 8); ww.WriteBits(id, 8); ww.Flush();
                    conn.Send(ww.buffer, ww.writeByteIndex, ENetReliability.Reliable); // tell the client its id
                }
                var r = new NetPakReader();
                r.SetBufferSegment(_rx, (int)size);
                r.ReadBits(8, out uint type);
                if ((MsgType)type == MsgType.ClientState)
                {
                    var st = PlayerState.Read(r);
                    st.Id = id;
                    _states[id] = st;
                }
                else if ((MsgType)type == MsgType.Fire)
                {
                    r.ReadBits(32, out uint ox); r.ReadBits(32, out uint oy); r.ReadBits(32, out uint oz);
                    r.ReadBits(32, out uint dx); r.ReadBits(32, out uint dy); r.ReadBits(32, out uint dz);
                    Hitscan(
                        BitConverter.UInt32BitsToSingle(ox), BitConverter.UInt32BitsToSingle(oy), BitConverter.UInt32BitsToSingle(oz),
                        BitConverter.UInt32BitsToSingle(dx), BitConverter.UInt32BitsToSingle(dy), BitConverter.UInt32BitsToSingle(dz));
                }
            }
        }

        // Server-authoritative hitscan: kill the nearest zombie whose center lies within a small radius of
        // the ray (origin + t*dir, t>0). Math only -- no engine physics on the dedicated server.
        void Hitscan(float ox, float oy, float oz, float dx, float dy, float dz, float radius = 0.6f, float range = 200f)
        {
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4f) return;
            dx /= len; dy /= len; dz /= len;
            int hit = -1; float bestT = range;
            for (int i = 0; i < _zombies.Count; i++)
            {
                var z = _zombies[i];
                float vx = z.X - ox, vy = z.Y - oy, vz = z.Z - oz;
                float t = vx * dx + vy * dy + vz * dz;            // projection onto ray
                if (t < 0f || t > bestT) continue;
                float cx = ox + dx * t - z.X, cy = oy + dy * t - z.Y, cz = oz + dz * t - z.Z;
                if (cx * cx + cy * cy + cz * cz <= radius * radius) { bestT = t; hit = i; }
            }
            if (hit >= 0) { _zombies.RemoveAt(hit); Kills++; }
        }

        // Authoritative zombie sim: keep a horde, each chases the nearest player on the ground plane.
        public void TickZombies(float dt, int maxZombies = 10, float speed = 3.0f)
        {
            _spawnCd -= dt;
            if (_zombies.Count < maxZombies && _states.Count > 0 && _spawnCd <= 0f)
            {
                double a = _rng.NextDouble() * Math.PI * 2.0;
                _zombies.Add(new ZombieState { X = (float)Math.Cos(a) * 16f, Y = 1f, Z = (float)Math.Sin(a) * 16f });
                _spawnCd = 0.4f;
            }
            for (int i = 0; i < _zombies.Count; i++)
            {
                var z = _zombies[i];
                float bx = 0, bz = 0, best = float.MaxValue;
                foreach (var p in _states.Values)
                {
                    float dx = p.X - z.X, dz = p.Z - z.Z, d = dx * dx + dz * dz;
                    if (d < best) { best = d; bx = dx; bz = dz; }
                }
                float dist = (float)Math.Sqrt(best);
                if (dist > 0.6f)
                {
                    z.X += bx / dist * speed * dt;
                    z.Z += bz / dist * speed * dt;
                }
                _zombies[i] = z;
            }
        }

        public void Broadcast()
        {
            var w = new NetPakWriter { buffer = new byte[4096] };
            w.Reset();
            w.WriteBits((byte)MsgType.WorldState, 8);
            w.WriteBits((uint)_states.Count, 8);
            foreach (var st in _states.Values) st.Write(w);
            w.WriteBits((uint)_zombies.Count, 8);
            foreach (var z in _zombies) z.Write(w);
            w.Flush();
            foreach (var conn in _clients.Keys)
                conn.Send(w.buffer, w.writeByteIndex, ENetReliability.Unreliable);
        }

        public void TearDown() => _transport.TearDown();
    }

    // Client: sends local state, applies the world snapshot (players + zombies).
    public sealed class NetClient
    {
        readonly UdpClientTransport _transport;
        readonly byte[] _rx = new byte[4096];

        public readonly Dictionary<byte, PlayerState> Remote = new();
        public readonly List<ZombieState> Zombies = new();
        public byte SelfId;   // assigned by the server's Welcome

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

        // Tell the server "I fired a ray from origin along dir" -- the server does authoritative hit-reg.
        public void SendFire(float ox, float oy, float oz, float dx, float dy, float dz)
        {
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset();
            w.WriteBits((byte)MsgType.Fire, 8);
            w.WriteBits(BitConverter.SingleToUInt32Bits(ox), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(oy), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(oz), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(dx), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(dy), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(dz), 32);
            w.Flush();
            _transport.Send(w.buffer, w.writeByteIndex, ENetReliability.Reliable);
        }

        public void Poll()
        {
            while (_transport.Receive(_rx, out long size))
            {
                var r = new NetPakReader();
                r.SetBufferSegment(_rx, (int)size);
                r.ReadBits(8, out uint type);
                if ((MsgType)type == MsgType.Welcome) { r.ReadBits(8, out uint sid); SelfId = (byte)sid; continue; }
                if ((MsgType)type != MsgType.WorldState) continue;

                r.ReadBits(8, out uint pcount);
                Remote.Clear();
                for (int i = 0; i < pcount; i++)
                {
                    var st = PlayerState.Read(r);
                    Remote[st.Id] = st;
                }
                r.ReadBits(8, out uint zcount);
                Zombies.Clear();
                for (int i = 0; i < zcount; i++)
                    Zombies.Add(ZombieState.Read(r));
            }
        }

        public void TearDown() => _transport.TearDown();
    }
}
