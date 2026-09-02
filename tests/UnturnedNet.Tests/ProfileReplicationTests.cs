using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A DISPLAY NAME AND A PICTURE, ACROSS THE WIRE, WITH THE SERVER NOT BELIEVING EITHER.
    //
    // ProfileRulesTests already proves the sanitiser is correct in isolation. This proves the thing that
    // matters more and that a unit test cannot see: that the SERVER runs it, on what actually arrives, and
    // publishes its own answer. The distinction is the whole point of the request -- a client-side sanitise
    // is a courtesy to the player, and a modified client simply does not perform it. So every hostile-input
    // test here goes through SendRawProfileForTest, which skips the client's pass exactly as an attacker
    // would, and asserts on what the OTHER client ends up rendering.
    [TestFixture]
    public class ProfileReplicationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        /// <summary>A structurally valid 128x128 PNG. `tint` changes the bytes (and so the hash) without
        /// changing anything the validator looks at.</summary>
        static byte[] Png(int w = 128, int h = 128, byte tint = 0)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, w); WriteBE32(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(ms, "IHDR", ihdr);
            var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[64];
                for (int i = 0; i < row.Length; i++) row[i] = tint;
                z.Write(row);
            }
            Chunk(ms, "IDAT", raw.ToArray());
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE32(len, 0, data.Length); s.Write(len);
            foreach (char c in type) s.WriteByte((byte)c);
            s.Write(data);
            s.Write(new byte[4]);
        }

        static void WriteBE32(byte[] b, int i, int v)
        {
            b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
        }

        static string NameSeenBy(NetWorldClient viewer, ushort ofPlayer)
            => viewer.Profiles.TryGet(ofPlayer, out var e) ? e.Name : null;

        /// <summary>A 128x128 PNG the SIZE a real one is. Png() above is structurally valid but 69 bytes -- one
        /// zlib'd row of a flat colour -- and that is why the wire bug lived for as long as it did: nothing in
        /// this suite ever sent a picture that could not fit in NetMessagePak's 256-byte default buffer, while
        /// no picture a launcher can produce ever could (the smallest flat-colour 128x128 is ~361 bytes; a real
        /// photo squished to 128x128 is 15-60 KB). The validator is header-only and never inflates the IDAT, so
        /// its payload can be incompressible pseudo-random bytes of whatever length the test needs.</summary>
        static byte[] RealSizedPng(int idatBytes, int seed)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, 128); WriteBE32(ihdr, 4, 128);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(ms, "IHDR", ihdr);
            var idat = new byte[idatBytes];
            new Random(seed).NextBytes(idat);
            Chunk(ms, "IDAT", idat);
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        [Test]
        public void a_real_sized_picture_and_the_name_beside_it_reach_the_other_player()
        {
            // THE LIVE BUG (strawberry 2026-09-02: "custom names and pfps arent showing on the server"). Every
            // joiner with a picture rendered as "player" + the checkerboard: SetProfileCommand (name, then
            // u32 length, then the bytes) was packed into NetMessagePak's 256-byte default buffer, WriteBytes
            // overflowed and wrote NOTHING, Pack shipped the truncated message anyway, the server's TryRead
            // failed on the missing bytes and CommandRegistry dropped the WHOLE command as malformed --
            // name included. The 69-byte Png() in the test above fits, so the suite never saw it.
            //
            // TEETH: with the 256-byte truncation back (NetMessagePak.Pack not growing, SendSetProfile not
            // sizing), the NAME assertion fails first -- exactly the symptom -- and MalformedRejected is 1.
            var h = new TransactionalHarness(8899).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var png = RealSizedPng(idatBytes: 24 * 1024, seed: 7);   // a 24 KB photo -- mid-range for a squished 128x128
            Assert.That(ProfileRules.CheckAvatarPng(png), Is.EqualTo(ProfileRules.AvatarVerdict.Ok), "fixture: the validator accepts it (header-only)");
            Assert.That(png.Length, Is.GreaterThan(256 * 4), "fixture: far bigger than the default pack buffer AND its first growth step");

            Assert.That(a.SendSetProfile("strawberry_cow", png), Is.True);
            Assert.That(h.StepUntil(() => NameSeenBy(b, a.PlayerId) == "strawberry_cow"), Is.True,
                $"b never saw a's NAME -- the whole command was dropped with the picture (seed={h.Net.Seed}, malformed={h.Server.Commands.Diag.MalformedRejected})");
            Assert.That(h.Server.Commands.Diag.MalformedRejected, Is.EqualTo(0), "the server parsed the command whole -- nothing was truncated on the way in");

            Assert.That(h.StepUntil(() => b.Profiles.TryGetAvatar(a.PlayerId, out _)), Is.True,
                $"b never received a's picture (seed={h.Net.Seed})");
            Assert.That(b.Profiles.TryGetAvatar(a.PlayerId, out var got), Is.True);
            Assert.That(got, Is.EqualTo(png), "all 24 KB arrived unchanged (the server->client AvatarData event grew too)");
        }

        [Test]
        public void the_largest_allowed_picture_reaches_the_other_player()
        {
            // The cap is the contract: a picture the validator ACCEPTS must also SEND. 64 KB minus framing.
            var h = new TransactionalHarness(8898).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var png = RealSizedPng(idatBytes: ProfileRules.MaxAvatarBytes - 8 - 25 - 12 - 12, seed: 11);
            Assert.That(png.Length, Is.LessThanOrEqualTo(ProfileRules.MaxAvatarBytes), "fixture: exactly at the cap");
            Assert.That(ProfileRules.CheckAvatarPng(png), Is.EqualTo(ProfileRules.AvatarVerdict.Ok), "fixture: the validator accepts it");

            Assert.That(a.SendSetProfile("edge", png), Is.True);
            Assert.That(h.StepUntil(() => b.Profiles.TryGetAvatar(a.PlayerId, out _), maxTicks: 800), Is.True,
                $"b never received the cap-sized picture (seed={h.Net.Seed}, malformed={h.Server.Commands.Diag.MalformedRejected})");
            Assert.That(b.Profiles.TryGetAvatar(a.PlayerId, out var got), Is.True);
            Assert.That(got, Is.EqualTo(png));
            Assert.That(NameSeenBy(b, a.PlayerId), Is.EqualTo("edge"));
        }

        [Test]
        public void a_name_and_a_picture_reach_the_other_player()
        {
            // The baseline the security tests below are variations on: this has to work at all.
            var h = new TransactionalHarness(8801).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            var png = Png(tint: 3);

            Assert.That(a.SendSetProfile("strawberry_cow", png), Is.True);
            Assert.That(h.StepUntil(() => NameSeenBy(b, a.PlayerId) == "strawberry_cow"), Is.True,
                $"b never saw a's name (seed={h.Net.Seed})");

            Assert.That(h.StepUntil(() => b.Profiles.TryGetAvatar(a.PlayerId, out _)), Is.True,
                $"b never received a's picture (seed={h.Net.Seed})");
            Assert.That(b.Profiles.TryGetAvatar(a.PlayerId, out var got), Is.True);
            Assert.That(got, Is.EqualTo(png), "the bytes arrived unchanged");
        }

        [Test]
        public void the_server_sanitises_a_name_a_modified_client_never_sanitised()
        {
            // THE ONE THE REQUEST IS ABOUT. SendRawProfileForTest is what a client that skipped the polite
            // client-side pass sends. Nobody else may ever see this string.
            var h = new TransactionalHarness(8802).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];

            // Wait for the SEEDED entry first and remember it, so the condition below is "the profile command
            // landed and changed something" rather than something the initial state already satisfies. My
            // first cut asserted `stored != hostile`, which is true before a single tick runs -- StepUntil
            // returned immediately and the test then read the handshake's name and called it a pass.
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out _)), Is.True);
            h.Server.Profiles.TryGet(a.PlayerId, out var seeded);
            string seededName = seeded.Name;

            const string hostile = "[img]https://evil.example/track.png[/img]";
            Assert.That(a.SendRawProfileForTest(hostile, null), Is.True);
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out var cur) && cur.Name != seededName), Is.True,
                $"the profile command never landed on the server (seed={h.Net.Seed})");
            Assert.That(h.StepUntil(() => NameSeenBy(b, a.PlayerId) != null && NameSeenBy(b, a.PlayerId) != seededName), Is.True,
                $"the other client never saw the change (seed={h.Net.Seed})");

            string onServer = h.Server.Profiles.TryGet(a.PlayerId, out var e) ? e.Name : null;
            Assert.That(onServer, Is.Not.Null);
            Assert.That(onServer, Does.Not.Contain("["), $"the SERVER stored a BBCode name: '{onServer}'");
            Assert.That(onServer, Is.EqualTo(ProfileRules.SanitizeName(hostile)),
                "the server's stored name must be exactly what ProfileRules produces, not a second opinion");

            string onOtherClient = NameSeenBy(b, a.PlayerId);
            Assert.That(onOtherClient, Does.Not.Contain("["), $"another player renders a BBCode name: '{onOtherClient}'");
        }

        [Test]
        public void a_name_that_would_forge_a_log_line_never_reaches_the_log()
        {
            var h = new TransactionalHarness(8803).Connected("a");
            var a = h.Clients[0];
            Assert.That(a.SendRawProfileForTest("bob\r\n[warn] admin granted", null), Is.True);
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out var e) && e.Name != ProfileRules.FallbackName
                                          && e.Name.StartsWith("bob")), Is.True);
            h.Server.Profiles.TryGet(a.PlayerId, out var se);
            Assert.That(se.Name, Does.Not.Contain("\n"));
            Assert.That(se.Name, Does.Not.Contain("\r"));
        }

        [Test]
        public void a_picture_that_is_not_a_valid_128px_png_is_refused()
        {
            var h = new TransactionalHarness(8804).Connected("a");
            var a = h.Clients[0];

            // A good one first, so the rejection below is a rejection rather than a never-worked.
            var good = Png(tint: 9);
            Assert.That(a.SendSetProfile("bob", good), Is.True);
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out var e) && e.AvatarHash != 0), Is.True);
            h.Server.Profiles.TryGet(a.PlayerId, out var e1);
            ulong goodHash = e1.AvatarHash;

            foreach (var bad in new[]
                     {
                         Png(16384, 16384),                                  // a decompression bomb by declared size
                         Png(64, 64),                                        // wrong dimensions
                         new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
                                      16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33 },  // a JPEG
                     })
            {
                Assert.That(a.SendRawProfileForTest("bob", bad), Is.True);
                h.Step(20);
                h.Server.Profiles.TryGet(a.PlayerId, out var e2);
                Assert.That(e2.AvatarHash, Is.EqualTo(goodHash),
                    "a refused picture must leave the player's existing one alone, not clear it");
            }
        }

        [Test]
        public void an_oversized_payload_never_becomes_an_allocation()
        {
            // The length prefix is attacker-controlled. TryRead has to refuse BEFORE it allocates -- the
            // command is dropped by the reader, so the server's state simply does not move.
            var h = new TransactionalHarness(8805).Connected("a");
            var a = h.Clients[0];
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out _)), Is.True);
            h.Server.Profiles.TryGet(a.PlayerId, out var before);
            ulong hashBefore = before.AvatarHash;

            var huge = new byte[ProfileRules.MaxAvatarBytes + 1];
            huge[0] = 0x89; huge[1] = 0x50; huge[2] = 0x4E; huge[3] = 0x47;
            a.SendRawProfileForTest("bob", huge);
            h.Step(20);

            h.Server.Profiles.TryGet(a.PlayerId, out var after);
            Assert.That(after.AvatarHash, Is.EqualTo(hashBefore), "an oversized picture changed server state");
        }

        [Test]
        public void a_joiner_receives_the_pictures_of_people_already_here()
        {
            // The snapshot names an avatar by HASH. Someone who joins later has none of the bytes, so the
            // server has to hand them over -- otherwise every nameplate but your own is blank after a rejoin.
            var h = new TransactionalHarness(8806).Connected("a");
            var a = h.Clients[0];
            var png = Png(tint: 42);
            Assert.That(a.SendSetProfile("first_player", png), Is.True);
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out var e) && e.AvatarHash != 0), Is.True);

            var late = h.AddClient("late");
            Assert.That(h.StepUntil(() => late.State == NetSessionState.Connected, 600), Is.True);
            Assert.That(h.StepUntil(() => late.Profiles.TryGetAvatar(a.PlayerId, out _)), Is.True,
                $"the joiner never got the existing player's picture (seed={h.Net.Seed})");
            late.Profiles.TryGetAvatar(a.PlayerId, out var got);
            Assert.That(got, Is.EqualTo(png));
            Assert.That(NameSeenBy(late, a.PlayerId), Is.EqualTo("first_player"));
        }

        [Test]
        public void avatar_bytes_whose_hash_does_not_match_are_dropped_on_the_client()
        {
            // The client keys its cache by hash, so it recomputes rather than believing the label. Mismatched
            // bytes would otherwise poison the cache for every player using that hash.
            var repl = new PlayerProfileReplication();
            var png = Png(tint: 7);
            ulong real = ProfileRules.AvatarHash(png);
            Assert.That(repl.ClientAcceptAvatar(real ^ 0xFFFF, png), Is.False, "a mislabelled payload was cached");
            Assert.That(repl.ClientAcceptAvatar(real, png), Is.True);
            Assert.That(repl.HasAvatarBytes(real), Is.True);

            // and bytes that are not a valid avatar are refused even when the hash is honest
            var bad = Png(64, 64);
            Assert.That(repl.ClientAcceptAvatar(ProfileRules.AvatarHash(bad), bad), Is.False,
                "the client is the last stop before an image decoder -- it re-validates");
        }

        [Test]
        public void a_picture_is_sent_to_each_peer_at_most_once()
        {
            // strawberry: "add an optimized system for serving pfps to clients". The optimisation IS this
            // assertion -- without a per-peer ledger the server re-broadcasts a full picture to everyone on
            // every profile change, including to peers holding those exact bytes already.
            var h = new TransactionalHarness(8808).Connected("a", "b");
            var a = h.Clients[0];
            var png = Png(tint: 11);

            Assert.That(a.SendSetProfile("bob", png), Is.True);
            Assert.That(h.StepUntil(() => h.Clients[1].Profiles.TryGetAvatar(a.PlayerId, out _)), Is.True);
            int afterFirst = h.Server.Profiles.DebugSentCount(h.Clients[1].PlayerId);
            long skippedBefore = h.Server.Profiles.AvatarSendsSkipped;

            // Re-state the SAME picture, which is exactly what a rejoin or a name edit does. Stepped past the
            // cooldown first, so this measures the LEDGER rather than the rate limit.
            h.Step((int)PlayerProfileReplication.ProfileCooldownTicks + 5);
            Assert.That(a.SendSetProfile("bobby", png), Is.True);
            h.Step(30);

            Assert.That(h.Server.Profiles.DebugSentCount(h.Clients[1].PlayerId), Is.EqualTo(afterFirst),
                "the same picture was sent to the same peer twice");
            Assert.That(h.Server.Profiles.AvatarSendsSkipped, Is.GreaterThan(skippedBefore),
                "the ledger did not report the skip -- which would mean it was never consulted");
            Assert.That(NameSeenBy(h.Clients[1], a.PlayerId), Is.EqualTo("bobby"),
                "and the NAME still changed -- deduping the picture must not swallow the profile");
        }

        [Test]
        public void a_client_cannot_make_the_server_broadcast_pictures_on_demand()
        {
            // One 64 KB upload becomes 64 KB x every peer. Alternating two pictures in a loop turns that into
            // an amplifier pointed at the server's uplink, so the rate gate drops the command outright.
            var h = new TransactionalHarness(8809).Connected("a", "b");
            var a = h.Clients[0];
            long limitedBefore = h.Server.Profiles.ProfilesRateLimited;

            Assert.That(a.SendSetProfile("bob", Png(tint: 1)), Is.True);
            h.Step(10);
            for (int i = 0; i < 20; i++) { a.SendSetProfile("bob", Png(tint: (byte)(i + 2))); h.Step(2); }

            Assert.That(h.Server.Profiles.ProfilesRateLimited, Is.GreaterThan(limitedBefore),
                "a flood of profile changes was accepted at full rate");
            Assert.That(h.Server.Profiles.DebugSentCount(h.Clients[1].PlayerId), Is.LessThan(5),
                $"the flood still fanned out {h.Server.Profiles.DebugSentCount(h.Clients[1].PlayerId)} pictures to one peer");
        }

        [Test]
        public void everyone_has_a_name_before_they_ever_send_a_profile()
        {
            // A player whose profile command is lost, or who runs an older client, must still be referable
            // to. The join handshake's name seeds the entry -- sanitised like everything else.
            var h = new TransactionalHarness(8807).Connected("a");
            var a = h.Clients[0];
            Assert.That(h.StepUntil(() => h.Server.Profiles.TryGet(a.PlayerId, out _)), Is.True);
            h.Server.Profiles.TryGet(a.PlayerId, out var e);
            Assert.That(e.Name, Is.Not.Null.And.Not.Empty);
            Assert.That(ProfileRules.IsCleanName(e.Name), Is.True, $"the seeded name was not sanitised: '{e.Name}'");
        }
    }
}
