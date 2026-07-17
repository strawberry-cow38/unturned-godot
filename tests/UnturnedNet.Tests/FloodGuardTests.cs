using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.NetTransport;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Hardening review H1: the connect flood guard. On this one-datagram handshake a spoofed-source
    // Connect blast produces peers the server never hears from again ("half-open") -- the guard caps that
    // pool well below maxPeers, and caps concurrent sessions per source IP so one host's ephemeral-port
    // blast can't hold the peer table. Driven through a scripted transport whose connections report real
    // IPv4s (MemTransport reports none, so mem-based tests are exempt from the per-source cap by design).
    [TestFixture]
    public class FloodGuardTests
    {
        const int DefaultMaxHalfOpen = 8;
        const int DefaultMaxPerSource = 8;

        // ---- scripted transport: the test enqueues (datagram, fake endpoint) pairs ----

        sealed class FakeConnection : ITransportConnection
        {
            public uint Ip;
            public ushort Port;
            public readonly List<byte[]> Sent = new List<byte[]>();

            public void Send(byte[] buffer, long size, ENetReliability reliability)
            {
                var copy = new byte[size];
                Buffer.BlockCopy(buffer, 0, copy, 0, (int)size);
                Sent.Add(copy);
            }

            public bool TryGetIPv4Address(out uint address) { address = Ip; return true; }
            public bool TryGetPort(out ushort port) { port = Port; return true; }
            public bool TryGetSteamId(out ulong steamId) { steamId = 0; return false; }
            public System.Net.IPAddress GetAddress() => new System.Net.IPAddress(Ip);
            public string GetAddressString(bool withPort) => $"{Ip}:{Port}";
            public void CloseConnection() { }
            public bool Equals(ITransportConnection other) => other is FakeConnection o && o.Ip == Ip && o.Port == Port;
            public override bool Equals(object obj) => Equals(obj as ITransportConnection);
            public override int GetHashCode() => (int)(Ip * 31 + Port);
        }

        sealed class ScriptedServerTransport : IServerTransport
        {
            public readonly Queue<(byte[] Data, ITransportConnection Conn)> Inbox = new Queue<(byte[], ITransportConnection)>();
            public void Initialize(ServerTransportConnectionFailureCallback connectionFailureCallback) { }
            public void TearDown() { }

            public bool Receive(byte[] buffer, out long size, out ITransportConnection transportConnection)
            {
                size = 0; transportConnection = null;
                if (Inbox.Count == 0) return false;
                var (data, conn) = Inbox.Dequeue();
                Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                size = data.Length;
                transportConnection = conn;
                return true;
            }
        }

        static byte[] BuildDatagram(NetChannel channel, ushort seq, Action<NetPakWriter> payload)
        {
            var w = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };
            w.Reset();
            NetProtocol.WriteHeader(w, new NetProtocol.Header
            {
                MagicByte = NetProtocol.Magic,
                Version = NetProtocol.Version,
                Channel = channel,
                Seq = seq,
                Ack = 0,
                AckBits = 0,
            });
            payload(w);
            w.Flush();
            var data = new byte[w.writeByteIndex];
            Buffer.BlockCopy(w.buffer, 0, data, 0, w.writeByteIndex);
            return data;
        }

        static byte[] BuildConnect(string name = "flood", ulong contentHash = 0)
            => BuildDatagram(NetChannel.Control, 1, w =>
            {
                w.WriteUInt8((byte)NetControlType.Connect);
                w.WriteString(name);
                w.WriteUInt64(contentHash);
            });

        static byte[] BuildKeepAlive(ushort seq = 2)
            => BuildDatagram(NetChannel.Control, seq, w => w.WriteUInt8((byte)NetControlType.KeepAlive));

        static NetRejectReason? LastRejectReason(FakeConnection conn)
        {
            for (int i = conn.Sent.Count - 1; i >= 0; i--)
            {
                var r = new NetPakReader();
                r.SetBufferSegment(conn.Sent[i], conn.Sent[i].Length);
                if (!NetProtocol.TryReadHeader(r, out var h) || h.Channel != NetChannel.Control) continue;
                if (!r.ReadUInt8(out byte type) || (NetControlType)type != NetControlType.Reject) continue;
                r.ReadUInt8(out byte reason);
                return (NetRejectReason)reason;
            }
            return null;
        }

        [Test]
        public void SpoofedConnectBlast_IsCappedAtMaxHalfOpen()
        {
            var transport = new ScriptedServerTransport();
            var server = new NetServerSession(transport);
            int joins = 0;
            server.PeerConnected += _ => joins++;

            // 20 one-shot valid Connects from 20 distinct spoofed source IPs; none will ever answer
            var conns = new List<FakeConnection>();
            for (uint i = 0; i < 20; i++)
            {
                var conn = new FakeConnection { Ip = 0x0A000001 + i, Port = 40000 };
                conns.Add(conn);
                transport.Inbox.Enqueue((BuildConnect($"spoof{i}"), conn));
            }
            server.Tick();

            Assert.That(server.Peers.Count, Is.EqualTo(DefaultMaxHalfOpen),
                "the blast holds no more than the half-open cap of the 32 peer slots");
            Assert.That(server.HalfOpenCount, Is.EqualTo(DefaultMaxHalfOpen));
            Assert.That(joins, Is.EqualTo(DefaultMaxHalfOpen), "the admitted ones did complete the (one-datagram) handshake");
            Assert.That(LastRejectReason(conns[19]), Is.EqualTo(NetRejectReason.ServerFull),
                "over-cap connects are told ServerFull -- no wire change");
        }

        [Test]
        public void HalfOpenPeers_TimeOut_AndTheServerRecovers()
        {
            var transport = new ScriptedServerTransport();
            var server = new NetServerSession(transport);

            for (uint i = 0; i < DefaultMaxHalfOpen; i++)
                transport.Inbox.Enqueue((BuildConnect($"spoof{i}"), new FakeConnection { Ip = 0x0A000100 + i, Port = 40000 }));
            server.Tick();
            Assert.That(server.HalfOpenCount, Is.EqualTo(DefaultMaxHalfOpen), "pool full: further one-shots are rejected");

            for (int t = 0; t < NetProtocol.TimeoutTicks + 5; t++) server.Tick();
            Assert.That(server.Peers.Count, Is.Zero, "silent half-open peers age out on the normal 5 s timeout");
            Assert.That(server.HalfOpenCount, Is.Zero);

            // a fresh joiner is admitted again
            var late = new FakeConnection { Ip = 0x0B000001, Port = 50000 };
            transport.Inbox.Enqueue((BuildConnect("late"), late));
            server.Tick();
            Assert.That(server.Peers.Count, Is.EqualTo(1));
            Assert.That(LastRejectReason(late), Is.Null, "no reject for the post-recovery joiner");
        }

        [Test]
        public void PerSourceIpCap_LimitsOneHost_ButNotOthers()
        {
            var transport = new ScriptedServerTransport();
            var server = new NetServerSession(transport);

            // one host opens sessions from many ephemeral ports, each proving liveness (a keepalive
            // follow-up) so the half-open cap never binds -- only the per-source cap can stop it
            const uint attackerIp = 0xC0A80001;
            var attackerConns = new List<FakeConnection>();
            for (int i = 0; i < DefaultMaxPerSource + 2; i++)
            {
                var conn = new FakeConnection { Ip = attackerIp, Port = (ushort)(41000 + i) };
                attackerConns.Add(conn);
                transport.Inbox.Enqueue((BuildConnect($"port{i}"), conn));
                server.Tick();
                transport.Inbox.Enqueue((BuildKeepAlive(), conn));
                server.Tick();
            }

            Assert.That(server.Peers.Count, Is.EqualTo(DefaultMaxPerSource),
                "one source IP cannot hold more than its per-source share of the peer table");
            Assert.That(LastRejectReason(attackerConns[DefaultMaxPerSource]), Is.EqualTo(NetRejectReason.ServerFull));

            // a different host is unaffected
            var other = new FakeConnection { Ip = 0xC0A80002, Port = 42000 };
            transport.Inbox.Enqueue((BuildConnect("legit"), other));
            server.Tick();
            Assert.That(server.Peers.Count, Is.EqualTo(DefaultMaxPerSource + 1), "a different source IP still joins");
            Assert.That(LastRejectReason(other), Is.Null);
        }

        [Test]
        public void PerSourceAccounting_Releases_OnDisconnect()
        {
            var transport = new ScriptedServerTransport();
            var server = new NetServerSession(transport);
            const uint ip = 0xC0A80005;

            // fill the source's allowance (all proven)
            var conns = new List<FakeConnection>();
            for (int i = 0; i < DefaultMaxPerSource; i++)
            {
                var conn = new FakeConnection { Ip = ip, Port = (ushort)(43000 + i) };
                conns.Add(conn);
                transport.Inbox.Enqueue((BuildConnect($"c{i}"), conn));
                server.Tick();
                transport.Inbox.Enqueue((BuildKeepAlive(), conn));
                server.Tick();
            }
            // one leaves gracefully...
            transport.Inbox.Enqueue((BuildDatagram(NetChannel.Control, 3, w =>
            {
                w.WriteUInt8((byte)NetControlType.Disconnect);
                w.WriteUInt8((byte)NetDisconnectReason.Requested);
            }), conns[0]));
            server.Tick();
            Assert.That(server.Peers.Count, Is.EqualTo(DefaultMaxPerSource - 1));

            // ...and the freed allowance admits a new session from the same IP
            var again = new FakeConnection { Ip = ip, Port = 43999 };
            transport.Inbox.Enqueue((BuildConnect("again"), again));
            server.Tick();
            Assert.That(server.Peers.Count, Is.EqualTo(DefaultMaxPerSource));
            Assert.That(LastRejectReason(again), Is.Null, "per-source count released with the departed peer");
        }
    }

    // Hardening review L2: _nextPlayerId is a ushort that wraps under sustained connect churn. The mint
    // must skip 0 (the "none"/"empty seat" sentinel across the game code) and any id a live peer holds.
    [TestFixture]
    public class PlayerIdMintTests
    {
        [Test]
        public void PlayerIdWrap_SkipsZero_AndIdsInUse()
        {
            var h = new NetSimHarness(seed: 4242);
            var a = h.ConnectClient("longtimer");
            Assert.That(a.PlayerId, Is.EqualTo(1), "first joiner minted id 1");

            // simulate 64k of connect churn having happened: the counter is about to wrap
            h.Server.NextPlayerIdForTest = ushort.MaxValue;
            var b = h.ConnectClient("prewrap");
            Assert.That(b.PlayerId, Is.EqualTo(ushort.MaxValue));

            var c = h.ConnectClient("postwrap");
            Assert.That(c.PlayerId, Is.Not.Zero, "id 0 is never minted (it means 'nobody' everywhere)");
            Assert.That(c.PlayerId, Is.Not.EqualTo(a.PlayerId).And.Not.EqualTo(b.PlayerId),
                "a live peer's id is never re-minted (FindPeer would cross-wire the two players)");
            Assert.That(c.PlayerId, Is.EqualTo(2), "wrap skips 0, then skips in-use id 1, lands on 2");
        }
    }
}
