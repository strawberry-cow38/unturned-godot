using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedNet.Tests
{
    // Kick/ban rules. Engine-free on purpose: expiry, matching and duration parsing are where this goes wrong
    // and none of them need a socket.
    [TestFixture]
    public class ServerModerationTests
    {
        const long T0 = 1_760_000_000;   // an arbitrary "now"
        static uint Ip(byte a, byte b, byte c, byte d) => (uint)((a << 24) | (b << 16) | (c << 8) | d);

        [Test]
        public void A_Timed_Ban_Blocks_Then_Lapses()
        {
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,5), "griefer", T0 + 600, "spam", ModerationKind.Kick);
            Assert.That(m.IsBanned(Ip(10,0,0,5), "griefer", T0, out _), Is.True, "blocked immediately");
            Assert.That(m.IsBanned(Ip(10,0,0,5), "griefer", T0 + 599, out _), Is.True, "still blocked a second before");
            Assert.That(m.IsBanned(Ip(10,0,0,5), "griefer", T0 + 600, out _), Is.False, "lapses exactly on expiry");
        }

        [Test]
        public void Expiry_Zero_Means_Permanent_Not_Already_Expired()
        {
            // The sentinel is the trap: 0 as "epoch" would read as expired-long-ago and let a permanent ban
            // through on the first connection.
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,6), "forever", 0, "cheating", ModerationKind.Ban);
            Assert.That(m.IsBanned(Ip(10,0,0,6), "forever", T0, out var hit), Is.True);
            Assert.That(hit.IsPermanent, Is.True);
            Assert.That(m.IsBanned(Ip(10,0,0,6), "forever", long.MaxValue / 2, out _), Is.True, "still banned far in the future");
        }

        [Test]
        public void Either_Handle_Matches_So_A_Rename_Or_A_New_Lease_Is_Not_A_Free_Pass()
        {
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,7), "sneaky", 0, "", ModerationKind.Ban);
            Assert.That(m.IsBanned(Ip(10,0,0,7), "different name", T0, out _), Is.True, "same address, new name");
            Assert.That(m.IsBanned(Ip(203,0,113,9), "sneaky", T0, out _), Is.True, "new address, same name");
            Assert.That(m.IsBanned(Ip(203,0,113,9), "someone else", T0, out _), Is.False, "neither handle");
        }

        // THE ONE THAT WOULD LOCK OUT THE BOX. Loopback and in-memory transports report no address (0). If an
        // unaddressed entry matched an unaddressed peer, banning one local player would ban every local player.
        [Test]
        public void An_Unaddressed_Ban_Does_Not_Match_Every_Other_Unaddressed_Peer()
        {
            var m = new ServerModeration();
            m.Add(0, "localguy", 0, "", ModerationKind.Ban);
            Assert.That(m.IsBanned(0, "localguy", T0, out _), Is.True, "still catches them by name");
            Assert.That(m.IsBanned(0, "someone else", T0, out _), Is.False, "but not everyone else on loopback");
        }

        [Test]
        public void Re_Banning_Extends_Rather_Than_Stacking()
        {
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,8), "repeat", T0 + 60, "first", ModerationKind.Kick);
            m.Add(Ip(10,0,0,8), "repeat", T0 + 6000, "second", ModerationKind.Ban);
            Assert.That(m.Count, Is.EqualTo(1), "one row, not two that disagree");
            Assert.That(m.IsBanned(Ip(10,0,0,8), "repeat", T0 + 100, out var hit), Is.True);
            Assert.That(hit.Reason, Is.EqualTo("second"));
        }

        // ---- from the fable review -------------------------------------------------------------------

        [Test]
        public void Re_Banning_EXTENDS_And_Never_Shortens()
        {
            // The doc said "extends"; the code was Remove-then-Add, which shortened. Three ways it lost
            // time somebody had already been given.
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,20), "bob", 0, "cheating", ModerationKind.Ban);        // permanent
            m.Add(Ip(10,0,0,20), "bob", T0 + 600, "spam", ModerationKind.Kick);    // a 10m kick after it
            Assert.That(m.IsBanned(Ip(10,0,0,20), "bob", T0 + 100000, out var hit), Is.True,
                        "a permanent ban must not be shortened by a later timed one");
            Assert.That(hit.IsPermanent, Is.True);

            // A household member's timed kick must not erase bob's permanent row either.
            var m2 = new ServerModeration();
            m2.Add(Ip(10,0,0,21), "bob", 0, "", ModerationKind.Ban);
            m2.Add(Ip(10,0,0,21), "alice", T0 + 600, "", ModerationKind.Kick);
            Assert.That(m2.IsBanned(Ip(10,0,0,21), "bob", T0 + 100000, out _), Is.True);

            // And the longer of two timed sentences wins.
            var m3 = new ServerModeration();
            m3.Add(Ip(10,0,0,22), "carl", T0 + 7200, "", ModerationKind.Ban);
            m3.Add(Ip(10,0,0,22), "carl", T0 + 60, "", ModerationKind.Kick);
            Assert.That(m3.IsBanned(Ip(10,0,0,22), "carl", T0 + 3600, out _), Is.True, "the 2h sentence stands");
        }

        [Test]
        public void A_Duration_That_Would_Overflow_Is_Refused()
        {
            // long.TryParse accepts 9223372036854775807; multiplying wrapped it to a NEGATIVE duration, and
            // "153722867280912931h" produced a 52-minute ban out of nonsense -- announcing one sentence and
            // applying another.
            foreach (var junk in new[] { "9223372036854775807w", "9223372036854775807s",
                                         "144115188075855872w", "153722867280912931h", "99999999999999d" })
                Assert.That(ServerModeration.TryParseDuration(junk, out _, out _), Is.False, $"'{junk}' must not parse");
            // A century still works, so nothing realistic was caught by the bound.
            Assert.That(ServerModeration.TryParseDuration("5200w", out long s, out _), Is.True);
            Assert.That(s, Is.EqualTo(5200L * 604800L));
        }

        [Test]
        public void Unban_Lifts_By_Either_Handle_And_Prune_Drops_Only_The_Dead()
        {
            var m = new ServerModeration();
            m.Add(Ip(10,0,0,9), "a", 0, "", ModerationKind.Ban);
            m.Add(Ip(10,0,0,10), "b", T0 + 10, "", ModerationKind.Kick);
            Assert.That(m.Remove(0, "a"), Is.EqualTo(1));
            Assert.That(m.IsBanned(Ip(10,0,0,9), "a", T0, out _), Is.False);
            Assert.That(m.Prune(T0), Is.EqualTo(0), "b has not expired yet");
            Assert.That(m.Prune(T0 + 11), Is.EqualTo(1));
            Assert.That(m.Count, Is.EqualTo(0));
        }

        [Test]
        public void Names_Compare_Ordinally_And_Ignore_Case_And_Padding()
        {
            Assert.That(ServerModeration.NameMatches("Griefer", "  griefer "), Is.True);
            Assert.That(ServerModeration.NameMatches("griefer", "griefer2"), Is.False);
            Assert.That(ServerModeration.NameMatches("", "griefer"), Is.False, "an empty stored name matches nobody");
        }

        [Test]
        public void Durations_Parse_By_Unit_And_A_Bare_Number_Is_Minutes()
        {
            Assert.That(ServerModeration.TryParseDuration("45s", out long s, out _), Is.True); Assert.That(s, Is.EqualTo(45));
            Assert.That(ServerModeration.TryParseDuration("10m", out s, out _), Is.True); Assert.That(s, Is.EqualTo(600));
            Assert.That(ServerModeration.TryParseDuration("2h", out s, out _), Is.True); Assert.That(s, Is.EqualTo(7200));
            Assert.That(ServerModeration.TryParseDuration("3d", out s, out _), Is.True); Assert.That(s, Is.EqualTo(259200));
            Assert.That(ServerModeration.TryParseDuration("1w", out s, out _), Is.True); Assert.That(s, Is.EqualTo(604800));
            // `kick griefer 10` means ten MINUTES. Seconds would make the common case a ten-second kick that
            // reads as "the command did nothing".
            Assert.That(ServerModeration.TryParseDuration("10", out s, out _), Is.True); Assert.That(s, Is.EqualTo(600));
        }

        [Test]
        public void Permanent_Words_Parse_And_Junk_Is_Refused_Rather_Than_Guessed()
        {
            Assert.That(ServerModeration.TryParseDuration("perm", out _, out bool p), Is.True); Assert.That(p, Is.True);
            Assert.That(ServerModeration.TryParseDuration("forever", out _, out p), Is.True); Assert.That(p, Is.True);
            foreach (var junk in new[] { "", "  ", "soon", "10y", "-5m", "m", "abc" })
                Assert.That(ServerModeration.TryParseDuration(junk, out _, out _), Is.False, $"'{junk}' must not parse");
            // Zero is refused SPECIFICALLY because 0 is the permanent sentinel: `ban x 0` becoming a forever-ban
            // is the one mistake here that waiting cannot undo.
            Assert.That(ServerModeration.TryParseDuration("0", out _, out _), Is.False);
            Assert.That(ServerModeration.TryParseDuration("0m", out _, out _), Is.False);
        }

        [Test]
        public void Describe_Reads_Like_A_Sentence()
        {
            Assert.That(ServerModeration.DescribeDuration(0, true), Is.EqualTo("permanently"));
            Assert.That(ServerModeration.DescribeDuration(600, false), Is.EqualTo("for 10m"));
            Assert.That(ServerModeration.DescribeDuration(9000, false), Is.EqualTo("for 2h 30m"));
            Assert.That(ServerModeration.DescribeDuration(45, false), Is.EqualTo("for 45s"));
        }
    }
}
