using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SDG.NetTransport.Udp;

namespace NetTransport.Tests
{
    // The browser's status socket answers ANY stranger who sends four magic bytes, so its size behaviour is a
    // security property and not a detail. The original code held it as a comment pairing two constants
    // ("20 B, still < the 24 B request"); the v2 block turns that into a rule the code enforces:
    //
    //     THE REPLY MAY NEVER BE LONGER THAN THE REQUEST THAT ASKED FOR IT.
    //
    // That is what keeps this port off the list of UDP services worth pointing at somebody, whatever an
    // operator later puts in the MOTD -- and it is why a newer client PADS its request: the padding is what
    // buys the extra block, rather than the server simply deciding to send more.
    [TestFixture]
    public class StatusQueryTests
    {
        const int Port = 47931;   // a port no other test in the fleet binds

        static byte[] Ask(UdpServerTransport srv, int reqBytes, uint nonce, out int respLen)
        {
            var req = new byte[reqBytes];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'S'; req[3] = (byte)'Q';
            req[4] = (byte)(nonce & 0xFF); req[5] = (byte)((nonce >> 8) & 0xFF);
            req[6] = (byte)((nonce >> 16) & 0xFF); req[7] = (byte)((nonce >> 24) & 0xFF);

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port));

            // The transport answers status requests from inside Receive(), so it has to be pumped.
            var buf = new byte[2048];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(2); } }) { IsBackground = true };
            pump.Start();

            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] r = udp.Receive(ref from);
            respLen = r.Length;
            return r;
        }

        [Test]
        public void ReplyIsNeverLongerThanTheRequest_AtBothSizes()
        {
            var srv = new UdpServerTransport(Port);
            srv.Initialize(null);   // the ctor only stores the port -- Initialize is what binds the socket
            srv.StatusPlayerCount = 3; srv.StatusMaxPlayers = 24; srv.StatusVersion = 0xDEADBEEF;
            srv.StatusProtocol = 51; srv.StatusMap = "PEI"; srv.StatusGamemode = "Survival";
            srv.StatusMotd = new string('M', 500);   // an operator trying to spend more room than the asker gave

            byte[] small = Ask(srv, 24, 0x11111111, out int smallLen);
            Assert.That(smallLen, Is.LessThanOrEqualTo(24), "a 24 B request must never draw a longer reply");
            Assert.That(smallLen, Is.EqualTo(20), "with no room for the v2 tail, the core is sent alone");

            Thread.Sleep(1100);   // the responder rate-limits one reply per source IP per second
            byte[] big = Ask(srv, 512, 0x22222222, out int bigLen);
            Assert.That(bigLen, Is.LessThanOrEqualTo(512), "a padded request must still never draw a longer reply");
            Assert.That(bigLen, Is.GreaterThan(20), "a padded request is how a client asks for the v2 block");

            // The core must be byte-identical either way: a pre-v2 client still parses exactly what it did.
            for (int i = 8; i < 20; i++)
                Assert.That(big[i], Is.EqualTo(small[i]), $"core byte {i} differs between the two request sizes");
        }

        [Test]
        public void TheV2TailCarriesProtocolAndClampedOperatorText()
        {
            var srv = new UdpServerTransport(Port + 1);
            srv.Initialize(null);   // the ctor only stores the port -- Initialize is what binds the socket
            srv.StatusProtocol = 45;
            srv.StatusMap = "PEI";
            srv.StatusGamemode = "Arena";
            srv.StatusPvp = false;
            srv.StatusMotd = new string('x', 400);   // over MotdMaxBytes on purpose

            var req = new byte[512];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'S'; req[3] = (byte)'Q';
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 1));
            var buf = new byte[2048];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(2); } }) { IsBackground = true };
            pump.Start();
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] r = udp.Receive(ref from);

            int o = 20;
            Assert.That(r[o++], Is.EqualTo(45), "the server states its protocol version so a refusal can name both numbers");
            byte flags = r[o++];
            Assert.That(flags & 1, Is.EqualTo(0), "PvE server must not advertise PvP");
            o += 2;   // max ping
            Assert.That(ReadStr(r, ref o), Is.EqualTo("PEI"));
            Assert.That(ReadStr(r, ref o), Is.EqualTo("Arena"));
            string motd = ReadStr(r, ref o);
            Assert.That(motd.Length, Is.LessThanOrEqualTo(UdpServerTransport.MotdMaxBytes),
                "an over-long MOTD is clamped by the SERVER, not left for the client to survive");
            Assert.That(motd.Length, Is.GreaterThan(0), "clamping must not delete it outright");
        }

        // A multi-byte string clamped by BYTES can be cut mid-codepoint; the clamp walks back to a character
        // boundary instead. A Cyrillic server name is not an exotic case, and a half codepoint is a string
        // that will not decode on the far side.
        [Test]
        public void ClampCutsOnACharacterBoundary_NotMidCodepoint()
        {
            var srv = new UdpServerTransport(Port + 2);
            srv.Initialize(null);   // the ctor only stores the port -- Initialize is what binds the socket
            srv.StatusMotd = new string('я', 300);   // 2 bytes each -> 600 bytes, well over the cap

            var req = new byte[512];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'S'; req[3] = (byte)'Q';
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 2));
            var buf = new byte[2048];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(2); } }) { IsBackground = true };
            pump.Start();
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] r = udp.Receive(ref from);

            int o = 20 + 1 + 1 + 2;
            ReadStr(r, ref o); ReadStr(r, ref o);            // map, gamemode
            int n = r[o++];
            string motd = Encoding.UTF8.GetString(r, o, n);
            Assert.That(motd, Does.Not.Contain('�'), "clamped mid-codepoint: the decode produced a replacement char");
            Assert.That(motd.Length * 2, Is.LessThanOrEqualTo(UdpServerTransport.MotdMaxBytes + 1));
            foreach (char c in motd) Assert.That(c, Is.EqualTo('я'));
        }

        // The icon is PULLED in chunks rather than pushed in the status reply, because a 320x180 png is tens
        // of KB and the reply-never-exceeds-the-request rule would otherwise demand a 60 KB request per server
        // per refresh. Pull keeps the rule per-chunk: fetching 60 KB costs the asker 60 KB, so a spoofed
        // source address reflects nothing an attacker did not already send.
        [Test]
        public void IconChunksNeverExceedTheirRequest_AndReassembleExactly()
        {
            var srv = new UdpServerTransport(Port + 3);
            srv.Initialize(null);
            var icon = new byte[5000];
            for (int i = 0; i < icon.Length; i++) icon[i] = (byte)(i * 7 % 251);   // a pattern a truncation or an off-by-one would disturb
            srv.StatusIcon = icon;

            var got = new byte[icon.Length];
            int have = 0, chunks = 0;
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            var buf = new byte[4096];
            var pump = new Thread(() => { for (int i = 0; i < 4000; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(1); } }) { IsBackground = true };
            pump.Start();

            const int reqBytes = 1200;
            while (have < icon.Length && chunks < 200)
            {
                var req = new byte[reqBytes];
                req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'I'; req[3] = (byte)'Q';
                req[4] = (byte)(have & 0xFF); req[5] = (byte)((have >> 8) & 0xFF); req[6] = (byte)((have >> 16) & 0xFF);
                udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 3));
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] r = udp.Receive(ref from);
                chunks++;

                Assert.That(r.Length, Is.LessThanOrEqualTo(reqBytes), "a chunk reply must never exceed the request that asked for it");
                Assert.That(Encoding.ASCII.GetString(r, 0, 4), Is.EqualTo("UGIR"));
                int off = r[4] | (r[5] << 8) | (r[6] << 16);
                int total = r[7] | (r[8] << 8) | (r[9] << 16);
                Assert.That(off, Is.EqualTo(have), "the server echoes the offset it is answering");
                Assert.That(total, Is.EqualTo(icon.Length), "the total is stated and stable across chunks");
                int n = r.Length - 10;
                Assert.That(n, Is.GreaterThan(0));
                Array.Copy(r, 10, got, have, n);
                have += n;
            }

            Assert.That(have, Is.EqualTo(icon.Length), $"reassembled {have} of {icon.Length} in {chunks} chunks");
            Assert.That(got, Is.EqualTo(icon), "the reassembled bytes must be the icon, exactly");
        }

        // ⚠ A 1200 B request is bounded by the CHUNK CAP (1024), not by the request -- so it does not test the
        // budget clamp at all. A small request is the only shape that does, and without this case removing the
        // clamp entirely leaves every other test green.
        [Test]
        public void ASmallRequestIsAnsweredWithASmallChunk()
        {
            var srv = new UdpServerTransport(Port + 6);
            srv.Initialize(null);
            srv.StatusIcon = new byte[5000];

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            const int tiny = 100;
            var req = new byte[tiny];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'I'; req[3] = (byte)'Q';
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 6));
            var buf = new byte[4096];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(1); } }) { IsBackground = true };
            pump.Start();
            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] r = udp.Receive(ref from);

            Assert.That(r.Length, Is.LessThanOrEqualTo(tiny),
                "a 100 B request must draw at most 100 B -- the icon is a pull, and the asker sets the budget");
            Assert.That(r.Length, Is.GreaterThan(10), "it should still carry SOME payload, not just a header");
        }

        // An offset past the end is a malformed ask; answering it with anything is how a scanner learns the
        // shape of your memory. Silence is the whole response.
        [Test]
        public void AnOffsetPastTheEndIsNotAnswered()
        {
            var srv = new UdpServerTransport(Port + 4);
            srv.Initialize(null);
            srv.StatusIcon = new byte[100];

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 700;
            var req = new byte[1200];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'I'; req[3] = (byte)'Q';
            req[4] = 0xFF; req[5] = 0xFF; req[6] = 0x00;   // offset 65535, well past a 100 B icon
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 4));
            var buf = new byte[4096];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(1); } }) { IsBackground = true };
            pump.Start();

            var from = new IPEndPoint(IPAddress.Any, 0);
            Assert.Throws<SocketException>(() => udp.Receive(ref from), "an out-of-range offset must draw no reply at all");
        }

        // No icon configured means no reply -- not an empty one. An empty reply is still a reflected datagram
        // and still tells a scanner the port is live and willing.
        [Test]
        public void NoIconConfiguredDrawsNoReply()
        {
            var srv = new UdpServerTransport(Port + 5);
            srv.Initialize(null);
            srv.StatusIcon = null;

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 700;
            var req = new byte[1200];
            req[0] = (byte)'U'; req[1] = (byte)'G'; req[2] = (byte)'I'; req[3] = (byte)'Q';
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, Port + 5));
            var buf = new byte[4096];
            var pump = new Thread(() => { for (int i = 0; i < 400; i++) { srv.Receive(buf, out _, out _); Thread.Sleep(1); } }) { IsBackground = true };
            pump.Start();

            var from = new IPEndPoint(IPAddress.Any, 0);
            Assert.Throws<SocketException>(() => udp.Receive(ref from));
        }

        static string ReadStr(byte[] r, ref int o)
        {
            int n = r[o++];
            string v = Encoding.UTF8.GetString(r, o, n);
            o += n;
            return v;
        }
    }
}
