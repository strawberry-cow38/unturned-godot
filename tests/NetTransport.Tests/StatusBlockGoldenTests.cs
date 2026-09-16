using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using SDG.NetTransport.Udp;

namespace NetTransport.Tests
{
    /// <summary>
    /// A BYTE GOLDEN over the UGSR status reply. This block is a SECOND PROTOCOL, in a different assembly
    /// from the game wire: raw byte offsets, no NetPak, no struct, so the reflection-driven wire goldens
    /// cannot reach it and nothing derived pinned it.
    ///
    /// ⚠ WHY IT MATTERS MORE THAN A TEST-ONLY GAP. MainMenuConnect.RunConnectProbe gates EVERY join on
    /// this parse succeeding, and refuses on st.Version != NetContent.Hash or on the ping gate. So a shape
    /// drift here does not fail loudly -- a HEALTHY server is refused, with a message that says "content
    /// mismatch" or "ping too high". That is worse than the 2026-09-15 incident, where the live box sat
    /// ticking and answered nothing: a wrong diagnosis sends whoever debugs it at the protocol byte, which
    /// is the one thing that is not broken.
    ///
    /// ⚠ AND THE BLOCK CANNOT CARRY A VERSION. The first 20 bytes are frozen by the amplification
    /// invariant (a pre-v2 client must still parse them); tail[0] is StatusProtocol, which is the GAME's
    /// version and moves for unrelated reasons, so it cannot discriminate tail shapes; appending is
    /// useless because you must know the shape to find the end. The extension mechanism is therefore
    /// "append and let short reads degrade", which degrades correctly on TRUNCATION and NOT AT ALL on
    /// INSERTION -- every field after an inserted one shifts. Build-time loudness is the whole available
    /// win, and this is it.
    ///
    /// ⭐ DERIVED, NOT CAPTURED. The literal below was written out by hand from the field layout in
    /// UdpNetTransport.ReplyStatus before the test was ever run. Capturing it by running ReplyStatus would
    /// pin nothing -- it would pass by construction and agree with any shape the writer happened to have.
    /// Hand-derivation is honest here in a way it would not be for a NetPak message, because ReplyStatus
    /// never touches NetPak (grep: zero WriteBits/NetPakWriter in that file) -- it is plain byte
    /// assignment at known offsets plus length-prefixed UTF-8, so the offsets are checkable by eye.
    ///
    /// FIXTURE RULE, learned four times over in this repo on 2026-09-16: every same-typed neighbour holds
    /// a DISTINCT value, and distinct in the BYTES.
    ///   - players/maxPlayers are adjacent u16 and differ, and neither is a byte-swap of the other.
    ///   - Pvp is TRUE and Passworded is FALSE, so flags == 1. The shipped StatusQueryTests sets BOTH
    ///     true, which makes the byte 3 whichever bit is which -- a bit swap was invisible there and was
    ///     confirmed green under exactly that mutation. Here a swap reads 2 and fails.
    ///   - map / gamemode / motd are three consecutive length-prefixed strings with DIFFERENT LENGTHS and
    ///     different contents, so transposing any two moves bytes rather than permuting equals.
    /// </summary>
    [TestFixture]
    public class StatusBlockGoldenTests
    {
        const int Port = 47933;   // a port no other test in the fleet binds

        // ⚠ THIS FIXTURE MUST RELEASE ITS SOCKET, and the reason is worth keeping. StatusQueryTests has no
        // TearDown and leaks a bound UdpServerTransport per test method; that is invisible while it is the
        // only fixture in the assembly, because each leaked socket is finalized before the next Initialize
        // needs the port. Adding a SECOND socket-owning fixture changed that timing and the SHIPPED tests
        // started failing with "Address already in use" on their own port -- a failure I caused and first
        // mistook for pre-existing flake. So: stop the pump, then TearDown, in a finally.
        static volatile bool _pumping;
        static Thread _pump;

        // Hand-derived from ReplyStatus's field layout:
        //   "UGSR"                  55 47 53 52
        //   nonce echoed verbatim   11 22 33 44      (request carries 0x44332211 little-endian)
        //   players  u16 LE 0x0102  02 01
        //   max      u16 LE 0x0304  04 03
        //   version  u64 LE         08 07 06 05 04 03 02 01   (0x0102030405060708)
        //   -- v2 tail --
        //   StatusProtocol 52       34
        //   flags: Pvp only         01
        //   maxPing  u16 LE 0x0506  06 05
        //   map      len 3 "PEI"    03 50 45 49
        //   gamemode len 2 "Sv"     02 53 76
        //   motd     len 6 "hello!" 06 68 65 6C 6C 6F 21
        //   iconTotal 24-bit LE 261 05 01 00
        // 20 core + 21 tail = 41 bytes.
        const string Golden =
            "5547535211223344" + "0201" + "0403" + "0807060504030201"
            + "34" + "01" + "0605" + "03504549" + "025376" + "0668656C6C6F21" + "050100";

        static string Hex(byte[] b, int len)
        {
            var sb = new System.Text.StringBuilder(len * 2);
            for (int i = 0; i < len; i++) sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

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
            _pumping = true;
            _pump = new Thread(() =>
            {
                try { while (_pumping) { srv.Receive(buf, out _, out _); Thread.Sleep(2); } }
                catch (ObjectDisposedException) { }   // TearDown closed the socket under us; that is the exit path
                catch (SocketException) { }
            }) { IsBackground = true };
            _pump.Start();

            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] r = udp.Receive(ref from);
            respLen = r.Length;
            return r;
        }

        [Test]
        public void the_status_block_layout_has_not_moved()
        {
            var srv = new UdpServerTransport(Port);
            try
            {
            srv.Initialize(null);   // the ctor only stores the port; Initialize binds the socket
            srv.StatusPlayerCount = 0x0102; srv.StatusMaxPlayers = 0x0304;
            srv.StatusVersion = 0x0102030405060708UL;
            srv.StatusProtocol = 52;
            srv.StatusPvp = true; srv.StatusPassworded = false;   // DELIBERATELY UNEQUAL -- see the class note
            srv.StatusMaxPing = 0x0506;
            srv.StatusMap = "PEI"; srv.StatusGamemode = "Sv"; srv.StatusMotd = "hello!";
            srv.StatusIcon = new byte[261];                       // 261 -> 05 01 00, three distinguishable bytes

            byte[] r = Ask(srv, 64, 0x44332211u, out int len);
            Assert.That(Hex(r, len), Is.EqualTo(Golden),
                "the UGSR status block's byte layout changed. This block CANNOT carry a version -- the core "
                + "20 bytes are frozen by the amplification invariant and tail[0] is the game protocol -- so "
                + "an INSERTION shifts every field after it on every deployed client with nothing to detect "
                + "that. The visible symptom is a HEALTHY server refused at the join probe with 'content "
                + "mismatch' or 'ping too high'. If this change is intended, the client parse in "
                + "game/MainMenuServers.cs StatusQueryFull must move in the same commit.");
            }
            finally
            {
                _pumping = false;
                _pump?.Join(500);
                srv.TearDown();   // IServerTransport.TearDown closes the socket; without it the port stays bound
            }
        }
    }
}
