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

        static string ReadStr(byte[] r, ref int o)
        {
            int n = r[o++];
            string v = Encoding.UTF8.GetString(r, o, n);
            o += n;
            return v;
        }
    }
}
