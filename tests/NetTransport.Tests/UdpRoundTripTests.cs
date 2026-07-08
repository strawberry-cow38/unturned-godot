using System;
using System.Threading;
using NUnit.Framework;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.NetTransport.Udp;

namespace NetTransport.Tests
{
    // Proves the netcode spine end-to-end: a NetPak-packed player state crosses a REAL UDP socket through
    // the ported SDG.NetTransport interfaces (client -> server), unpacks byte-exact, and the server replies
    // back down the ITransportConnection. NetPak (ported source) + the UDP transport rework, working together.
    [TestFixture]
    public class UdpRoundTripTests
    {
        static bool PollReceive(UdpServerTransport s, byte[] buf, out long size, out ITransportConnection conn)
        {
            for (int i = 0; i < 250; i++) { if (s.Receive(buf, out size, out conn)) return true; Thread.Sleep(2); }
            size = 0; conn = null; return false;
        }
        static bool PollReceive(UdpClientTransport c, byte[] buf, out long size)
        {
            for (int i = 0; i < 250; i++) { if (c.Receive(buf, out size)) return true; Thread.Sleep(2); }
            size = 0; return false;
        }

        [Test]
        public void PlayerState_RoundTrips_ClientToServer_AndReplyBack()
        {
            const ushort port = 47851;
            var server = new UdpServerTransport(port);
            server.Initialize(null);
            var client = new UdpClientTransport("127.0.0.1", port);
            client.Initialize(null, null);

            // client packs a player state (tick + position x/y/z as float bits) with NetPak and sends it
            const uint tick = 1234567u;
            const float x = 1.5f, y = -2.25f, z = 100.125f;
            var w = new NetPakWriter { buffer = new byte[128] };
            w.Reset();
            w.WriteBits(tick, 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(x), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(y), 32);
            w.WriteBits(BitConverter.SingleToUInt32Bits(z), 32);
            w.Flush();
            client.Send(w.buffer, w.writeByteIndex, ENetReliability.Reliable);

            // server receives + unpacks
            var buf = new byte[1024];
            Assert.That(PollReceive(server, buf, out long size, out ITransportConnection conn), Is.True, "server got the datagram");
            var r = new NetPakReader();
            r.SetBufferSegment(buf, (int)size);
            r.ReadBits(32, out uint gTick);
            r.ReadBits(32, out uint gx);
            r.ReadBits(32, out uint gy);
            r.ReadBits(32, out uint gz);
            Assert.That(gTick, Is.EqualTo(tick));
            Assert.That(BitConverter.UInt32BitsToSingle(gx), Is.EqualTo(x));
            Assert.That(BitConverter.UInt32BitsToSingle(gy), Is.EqualTo(y));
            Assert.That(BitConverter.UInt32BitsToSingle(gz), Is.EqualTo(z));
            Assert.That(conn.TryGetPort(out _), Is.True);

            // server replies down the connection; client receives it
            var w2 = new NetPakWriter { buffer = new byte[16] };
            w2.Reset(); w2.WriteBits(0xBEEFu, 16); w2.Flush();
            conn.Send(w2.buffer, w2.writeByteIndex, ENetReliability.Reliable);

            var cbuf = new byte[64];
            Assert.That(PollReceive(client, cbuf, out long csize), Is.True, "client got the reply");
            var r2 = new NetPakReader();
            r2.SetBufferSegment(cbuf, (int)csize);
            r2.ReadBits(16, out uint reply);
            Assert.That(reply, Is.EqualTo(0xBEEFu));

            client.TearDown();
            server.TearDown();
        }
    }
}
