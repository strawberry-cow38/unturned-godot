using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // WIRE v53: the Accept carries the server's NAME and its player CAP.
    //
    // Before this, nothing on the wire told a joined client what server it was on -- the name lived only in
    // the CLIENT's hardcoded browser list, so a --connect= join had no name at all and a browser join only
    // had one while the row that started it was still in memory. That is the bug these lock.
    //
    // ⚠ THE REAL RISK IN THIS CHANGE IS NOT THE NEW FIELDS, IT IS THE OLD ONE. They are APPENDED after the
    // v6 holiday string, and a reader that mis-orders an append corrupts the field BEFORE it while the new
    // ones still look plausible. So the holiday is asserted in the same breath, every time.
    [TestFixture]
    public class ServerIdentityWireTests
    {
        static NetClientSession Join(NetSimHarness h, string name = "player")
        {
            var c = h.AddClient(name);
            c.Connect();
            for (int i = 0; i < 200 && c.State != NetSessionState.Connected; i++) h.Step();
            Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), "client never joined; " + h.SeedInfo);
            return c;
        }

        [Test]
        public void Accept_CarriesServerNameAndCap()
        {
            var h = new NetSimHarness(1234, serverName: "VoX Official — PEI", maxPlayers: 24, activeHoliday: "halloween", gamemode: "Arena");
            var c = Join(h);
            Assert.That(c.ServerName, Is.EqualTo("VoX Official — PEI"), "the server's name did not survive the Accept");
            Assert.That(c.ServerMaxPlayers, Is.EqualTo(24), "the seat cap did not survive the Accept");
            Assert.That(c.ServerGamemode, Is.EqualTo("Arena"), "the gamemode did not survive the Accept");
            // ⚠ The append must not have disturbed what came before it.
            Assert.That(c.ServerHoliday, Is.EqualTo("halloween"),
                        "the v6 holiday string was corrupted by appending the v53 fields after it");
        }

        [Test]
        public void Accept_UnnamedServerYieldsEmpty_NotGarbage()
        {
            // A server started without UG_NAME sends an empty string. The client must read exactly that, so
            // the caller can fall back to the ADDRESS -- a garbage or partial name would be shown to a player
            // as though the server had chosen it.
            var h = new NetSimHarness(99, activeHoliday: "none");
            var c = Join(h);
            Assert.That(c.ServerName, Is.EqualTo(""));
            Assert.That(c.ServerMaxPlayers, Is.EqualTo(0), "0 means 'not advertised', which is what a caller tests for");
            Assert.That(c.ServerHoliday, Is.EqualTo("none"));
        }

        [Test]
        public void Accept_NameIsNotTruncatedByMultibyteCharacters()
        {
            // A length-prefixed string written in bytes and measured in chars truncates the moment a name is
            // not ASCII -- and server names are exactly where a non-ASCII character turns up first.
            const string name = "Ünturned — Ёжик сервер 🎃";
            var h = new NetSimHarness(7, serverName: name, maxPlayers: 8);
            var c = Join(h);
            Assert.That(c.ServerName, Is.EqualTo(name), "a multibyte server name did not round-trip intact");
            Assert.That(c.ServerMaxPlayers, Is.EqualTo(8), "the field AFTER the name is where a bad length shows up");
        }

        [Test]
        public void Accept_CapSurvivesItsFullRange()
        {
            // ushort on the wire. 65535 is the ceiling the writer clamps to; if the field were written as a
            // byte this is the case that catches it, and 24 would not.
            var h = new NetSimHarness(5150, serverName: "big", maxPlayers: 65535);
            var c = Join(h);
            Assert.That(c.ServerMaxPlayers, Is.EqualTo(65535));
            Assert.That(c.ServerName, Is.EqualTo("big"), "the field BEFORE the cap must be unaffected by its size");
        }
    }
}
