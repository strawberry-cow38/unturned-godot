using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // A PLAYER-SUPPLIED DISPLAY NAME AND PICTURE ARE UNTRUSTED INPUT THAT EVERY OTHER CLIENT RENDERS.
    //
    // strawberry asked for this "secured against any kinda sql injection or w/e". There is no SQL in this
    // project, so the honest reading of the request is: make the untrusted fields safe against whatever they
    // actually get pasted into. That is BBCode in a RichTextLabel (where [img]https://attacker/x[/img] turns
    // every player's client into an IP grabber for the attacker), the server log (where a CR forges lines),
    // and the nameplate renderer (where stacked combining marks draw across the screen).
    //
    // Every invisible character below is written as a \u escape ON PURPOSE. A test for zero-width characters
    // that contains literal zero-width characters is a test nobody can review, and a stray one pasted into
    // the wrong line changes what is being asserted with no visible diff.
    //
    // Each test names the ATTACK rather than the character class, because "rejects control characters" does
    // not tell the next person why they cannot relax it.
    [TestFixture]
    public class ProfileRulesTests
    {
        const string SoftHyphen = "\u00AD";
        const string ZeroWidthSpace = "\u200B";
        const string ZeroWidthJoiner = "\u200D";
        const string ByteOrderMark = "\uFEFF";
        const string RightToLeftOverride = "\u202E";
        const string NonBreakingSpace = "\u00A0";
        const string CombiningAcute = "\u0301";
        static string San(string s) => ProfileRules.SanitizeName(s);

        /// <summary>Assert absence with ORDINAL comparison, which for these characters is the only kind that
        /// means anything. NUnit's Does.Not.Contain compares culture-sensitively, and ICU gives soft hyphens,
        /// zero-width characters and bidi controls ZERO collation weight -- so CompareInfo.IndexOf finds them
        /// inside every string, and the assertion fails against output that plainly does not contain them.
        /// The trap is the same shape as the bug under test: an invisible character behaving as though it
        /// were somewhere it is not.</summary>
        static void AssertAbsent(string haystack, string needle, string because)
            => Assert.That(haystack.IndexOf(needle, StringComparison.Ordinal), Is.LessThan(0),
                           $"{because} -- found U+{(int)needle[0]:X4} in '{haystack}'");

        [Test]
        public void an_ordinary_name_is_left_exactly_alone()
        {
            // The first thing to get right: sanitising must not mangle the 99% case, or everyone renames.
            foreach (var name in new[] { "strawberry_cow", "VoX", "Player 1", "barn-aldo", "a.b.c", "McWenker" })
                Assert.That(San(name), Is.EqualTo(name), $"'{name}' should survive untouched");
            Assert.That(ProfileRules.IsCleanName("strawberry_cow"), Is.True);
        }

        [Test]
        public void bbcode_cannot_survive_in_a_name()
        {
            // THE ONE THAT MATTERS MOST. A RichTextLabel with bbcode_enabled will fetch this URL.
            Assert.That(San("[img]https://evil.example/x.png[/img]"), Does.Not.Contain("["));
            Assert.That(San("[img]https://evil.example/x.png[/img]"), Does.Not.Contain("]"));
            Assert.That(San("[url=http://evil]click[/url]"), Does.Not.Contain("["));
            Assert.That(San("[color=red]admin[/color]"), Does.Not.Contain("["));
            // and the payload's own punctuation goes with it -- a bare "https://evil.example" left in a name
            // is still something a reader can be socially engineered by.
            foreach (char c in "[]<>&{}()\\/|\"'`^$*%;:?!@#~=+,")
                Assert.That(San("ab" + c + "cd"), Does.Not.Contain(c.ToString()), $"'{c}' must not survive");
        }

        [Test]
        public void an_invisible_character_cannot_re_weld_a_blocked_sequence()
        {
            // THE ORDERING BUG THIS CLASS EXISTS TO AVOID. A soft hyphen inside the tag defeats a naive
            // "does it contain [img]" check, and then RENDERS AS NOTHING -- so the check passes and the
            // player sees a working tag. Stripping must happen BEFORE the result is judged, and the judged
            // string must be the one that gets used.
            string welded = "[i" + SoftHyphen + "mg]https://evil" + ZeroWidthSpace + ".example[/img]";
            string clean = San(welded);
            Assert.That(clean, Does.Not.Contain("["));
            AssertAbsent(clean, SoftHyphen, "soft hyphen survived");
            AssertAbsent(clean, ZeroWidthSpace, "zero-width space survived");
        }

        [Test]
        public void a_name_cannot_forge_a_log_line()
        {
            // The server logs "accept <addr> as player <id> '<name>'". A CR/LF here writes a second line
            // that looks exactly like a real one.
            string forged = San("bob\r\n[warn] server: admin granted to bob");
            Assert.That(forged, Does.Not.Contain("\n"));
            Assert.That(forged, Does.Not.Contain("\r"));
            Assert.That(San("a\tb"), Does.Not.Contain("\t"));
            AssertAbsent(San("a" + NonBreakingSpace + "b"), NonBreakingSpace, "non-breaking space survived");
        }

        [Test]
        public void bidi_overrides_cannot_reorder_the_text_around_the_name()
        {
            // U+202E flips rendering direction for everything that follows -- inside a chat line or a kill
            // feed that rewrites the sentence the server wrote, not just the name.
            foreach (int cp in new[] { 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069 })
            {
                string ctrl = char.ConvertFromUtf32(cp);
                AssertAbsent(San("bob" + ctrl + "xyz"), ctrl, "a bidi control survived");
            }
            Assert.That(San(RightToLeftOverride + "bob"), Is.EqualTo("bob"));
        }

        [Test]
        public void two_names_cannot_look_identical_by_hiding_a_zero_width_character()
        {
            // Impersonation: "VoX" and "VoX" differing only by an invisible joiner render the same.
            Assert.That(San("VoX" + ZeroWidthJoiner), Is.EqualTo(San("VoX")));
            Assert.That(San("V" + ByteOrderMark + "oX"), Is.EqualTo(San("VoX")));
        }

        [Test]
        public void stacked_combining_marks_are_capped()
        {
            // Zalgo: unbounded marks render far outside the nameplate box. A couple of accents is a name.
            string zalgo = "a" + string.Concat(System.Linq.Enumerable.Repeat(CombiningAcute, 40)) + "b";
            string clean = San(zalgo);
            int marks = 0;
            foreach (char c in clean) if (c.ToString() == CombiningAcute) marks++;
            Assert.That(marks, Is.LessThanOrEqualTo(2), $"kept {marks} combining marks");
            Assert.That(San(CombiningAcute + CombiningAcute + "bob"), Does.StartWith("bob"),
                "a name cannot start with a floating accent");
        }

        [Test]
        public void real_non_latin_names_still_work()
        {
            // The restriction is on METACHARACTERS and invisibles, not on alphabets. A whitelist that only
            // allowed ASCII would quietly tell a large part of the playerbase their name is invalid.
            foreach (var name in new[] { "草莓牛", "Ёжик", "さくら", "Ωμέγα" })
            {
                string clean = San(name);
                Assert.That(clean, Is.EqualTo(name), $"'{name}' should survive");
                Assert.That(clean, Is.Not.EqualTo(ProfileRules.FallbackName));
            }
            // NFC: an accented name typed DECOMPOSED comes back composed, so it compares equal to the
            // precomposed spelling instead of looking like a different player.
            Assert.That(San("e" + CombiningAcute + "ric"), Is.EqualTo("éric"));
        }

        [Test]
        public void length_is_bounded_at_both_ends()
        {
            Assert.That(San(new string('a', 200)).Length, Is.EqualTo(ProfileRules.MaxNameChars));
            Assert.That(San("ab"), Is.EqualTo(ProfileRules.FallbackName), "too short falls back rather than shipping a 2-char name");
            Assert.That(San(""), Is.EqualTo(ProfileRules.FallbackName));
            Assert.That(San(null), Is.EqualTo(ProfileRules.FallbackName));
            Assert.That(San("   "), Is.EqualTo(ProfileRules.FallbackName));
            Assert.That(San("___"), Is.EqualTo(ProfileRules.FallbackName), "punctuation alone is not a name");
        }

        [Test]
        public void whitespace_is_collapsed_and_trimmed()
        {
            Assert.That(San("  bob   the   builder  "), Is.EqualTo("bob the builder"));
            Assert.That(San("bob" + NonBreakingSpace + "the"), Is.EqualTo("bob the"),
                "a non-breaking space collapses like any other");
        }

        [Test]
        public void sanitizing_is_idempotent()
        {
            // If a second pass changed the answer, the server's re-run would disagree with the client's
            // preview -- and worse, IsCleanName would be false for a name the client did sanitise.
            foreach (var raw in new[] { "[img]x[/img]", "  bob  ", "a" + string.Concat(System.Linq.Enumerable.Repeat(CombiningAcute, 9)),
                                        "草莓牛", RightToLeftOverride + "bob", new string('z', 40),
                                        "e" + CombiningAcute + "ric", "" })
            {
                string once = San(raw), twice = San(once);
                Assert.That(twice, Is.EqualTo(once), $"not idempotent: '{once}' -> '{twice}'");
                Assert.That(ProfileRules.IsCleanName(once), Is.True, $"IsCleanName rejected its own output '{once}'");
            }
        }

        [Test]
        public void changed_reports_whether_the_player_will_see_a_different_name()
        {
            ProfileRules.SanitizeName("strawberry_cow", out bool a);
            Assert.That(a, Is.False);
            ProfileRules.SanitizeName("[img]x[/img]bob", out bool b);
            Assert.That(b, Is.True);
        }

        // ---- profile picture ---------------------------------------------------------------------------

        /// <summary>A real, structurally valid PNG built here rather than checked in as a fixture, so every
        /// test below varies ONE property at a time from a known-good file.</summary>
        static byte[] Png(int w, int h, byte interlace = 0, bool animated = false)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, w); WriteBE32(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = interlace;
            Chunk(ms, "IHDR", ihdr);
            if (animated) Chunk(ms, "acTL", new byte[8]);
            // A real IDAT, but of a 1-row scanline buffer: the declared dimensions are what the validator
            // reads, and building a genuine 16384x16384 pixel buffer here would cost a gigabyte.
            var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[64]);
            Chunk(ms, "IDAT", raw.ToArray());
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE32(len, 0, data.Length); s.Write(len);
            foreach (char c in type) s.WriteByte((byte)c);
            s.Write(data);
            s.Write(new byte[4]);   // CRC -- deliberately not checked by a header-only validator
        }

        static void WriteBE32(byte[] b, int i, int v)
        {
            b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
        }

        [Test]
        public void a_real_128px_png_is_accepted()
        {
            Assert.That(ProfileRules.CheckAvatarPng(Png(128, 128)), Is.EqualTo(ProfileRules.AvatarVerdict.Ok));
        }

        [Test]
        public void the_dimension_check_is_what_stops_a_decompression_bomb()
        {
            // A 16384x16384 PNG is a few KB compressed and a gigabyte decoded. Refusing it on the DECLARED
            // dimensions -- before anything decodes it -- is the whole defence; a byte-size cap alone is not,
            // because the compressed file is small. This is why the server never decodes the image at all.
            Assert.That(ProfileRules.CheckAvatarPng(Png(16384, 16384)), Is.EqualTo(ProfileRules.AvatarVerdict.WrongSize));
            Assert.That(ProfileRules.CheckAvatarPng(Png(127, 128)), Is.EqualTo(ProfileRules.AvatarVerdict.WrongSize));
            Assert.That(ProfileRules.CheckAvatarPng(Png(128, 129)), Is.EqualTo(ProfileRules.AvatarVerdict.WrongSize));
        }

        [Test]
        public void non_png_bytes_are_refused_before_anything_looks_at_them()
        {
            Assert.That(ProfileRules.CheckAvatarPng(null), Is.EqualTo(ProfileRules.AvatarVerdict.Empty));
            Assert.That(ProfileRules.CheckAvatarPng(Array.Empty<byte>()), Is.EqualTo(ProfileRules.AvatarVerdict.Empty));

            var jpeg = new byte[64];
            jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[3] = 0xE0;
            Assert.That(ProfileRules.CheckAvatarPng(jpeg), Is.EqualTo(ProfileRules.AvatarVerdict.NotPng),
                "a JPEG is not a PNG with a different extension");

            Assert.That(ProfileRules.CheckAvatarPng(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                Is.EqualTo(ProfileRules.AvatarVerdict.BadHeader), "signature alone, no IHDR");
        }

        [Test]
        public void an_oversized_payload_is_refused_by_size_alone()
        {
            Assert.That(ProfileRules.CheckAvatarPng(new byte[ProfileRules.MaxAvatarBytes + 1]),
                Is.EqualTo(ProfileRules.AvatarVerdict.TooLarge));
        }

        [Test]
        public void animated_and_interlaced_pngs_are_refused()
        {
            // An APNG in a nameplate is a video every other player is made to play.
            Assert.That(ProfileRules.CheckAvatarPng(Png(128, 128, animated: true)), Is.EqualTo(ProfileRules.AvatarVerdict.Animated));
            Assert.That(ProfileRules.CheckAvatarPng(Png(128, 128, interlace: 1)), Is.EqualTo(ProfileRules.AvatarVerdict.Interlaced));
        }

        [Test]
        public void a_hostile_chunk_length_cannot_walk_the_parser_off_the_end()
        {
            // The chunk walk is driven entirely by attacker-controlled length fields.
            foreach (int bogus in new[] { int.MaxValue, -1, 0x7F000000 })
            {
                var png = Png(128, 128);
                WriteBE32(png, 8 + 25, bogus);   // the chunk immediately after IHDR
                Assert.DoesNotThrow(() => ProfileRules.CheckAvatarPng(png), $"threw on chunk length {bogus}");
            }
            // and a file truncated at EVERY length still answers rather than throwing
            var full = Png(128, 128);
            for (int cut = 0; cut <= full.Length; cut++)
            {
                var part = new byte[cut];
                Array.Copy(full, part, cut);
                Assert.DoesNotThrow(() => ProfileRules.CheckAvatarPng(part), $"threw on a {cut}-byte truncation");
            }
        }

        [Test]
        public void the_hash_distinguishes_different_pictures_and_matches_identical_ones()
        {
            var a = Png(128, 128);
            var b = Png(128, 128);
            b[b.Length - 5] ^= 0x5A;
            Assert.That(ProfileRules.AvatarHash(a), Is.EqualTo(ProfileRules.AvatarHash(Png(128, 128))));
            Assert.That(ProfileRules.AvatarHash(a), Is.Not.EqualTo(ProfileRules.AvatarHash(b)));
            Assert.That(ProfileRules.AvatarHash(null), Is.EqualTo(0UL), "no picture hashes to 0 -- the 'none' sentinel");
        }
    }
}
