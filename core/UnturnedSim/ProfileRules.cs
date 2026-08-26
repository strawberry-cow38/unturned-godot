using System;
using System.Globalization;
using System.Text;

namespace SDG.Unturned
{
    /// <summary>
    /// What a player is allowed to call themselves, and what a profile picture is allowed to be.
    ///
    /// THE THREAT MODEL, because "sanitise it" without one is how these get written wrong. There is no SQL
    /// anywhere in this project, so SQL escaping is not the risk. The risks a player-supplied display name
    /// actually carries here are:
    ///
    ///   1. MARKUP INJECTION. Godot's RichTextLabel renders BBCode when bbcode_enabled is set, and that
    ///      includes [img]https://attacker/x.png[/img] -- a name that makes every client fetch a URL is an
    ///      IP grabber, not a cosmetic glitch. [url], [color] and friends are the same family.
    ///   2. LOG INJECTION. The server logs the name on accept. A name containing CR/LF forges log lines,
    ///      which is how an attacker makes the log say someone else did something.
    ///   3. INVISIBLE AND REORDERING CHARACTERS. Cf (zero-width, bidi overrides) lets a name render as
    ///      something other than what it is, or as nothing at all -- two players with the "same" name, or a
    ///      name that reverses the text printed after it.
    ///   4. UNBOUNDED RENDERING COST. Stacked combining marks ("Zalgo") draw far outside a nameplate.
    ///   5. PATH TRAVERSAL, if a name were ever used to build a filename. Nothing here does, and nothing
    ///      should: avatars are keyed by player id, never by name.
    ///
    /// THE ORDER MATTERS. Normalise FIRST, then validate the NORMALISED string, then use only that string.
    /// Validating the raw input and then using the raw input is the classic hole; so is stripping AFTER
    /// matching, where removing an invisible character re-welds a sequence the match just cleared (a soft
    /// hyphen inside "[i&#173;mg]" defeats a naive "[img]" check, then vanishes at render time).
    ///
    /// AND THE SERVER DOES THIS ITSELF. The client sanitises so the player sees what they will get, but the
    /// server never trusts that: it re-runs the same function on what arrives and publishes ITS answer.
    /// </summary>
    public static class ProfileRules
    {
        public const int MinNameChars = 3;
        public const int MaxNameChars = 20;
        public const string FallbackName = "Survivor";

        /// <summary>Characters refused outright even though they are printable: every one is a metacharacter
        /// in some format this name is pasted into (BBCode, markup, shell, paths, format strings).</summary>
        const string ForbiddenPunctuation = "[]<>&{}()\\/|\"'`^$*%;:?!@#~=+,";

        const int MaxConsecutiveMarks = 2;   // one accent is a name; six is a rendering attack

        /// <summary>Normalise a raw name into the ONLY form that should ever be stored, logged or drawn.
        /// Always returns something printable -- FallbackName when nothing usable survives -- because a
        /// nameless player still has to be referred to somehow. `changed` reports whether the result differs
        /// from the input, which is what lets the client warn the player before they join.</summary>
        public static string SanitizeName(string raw, out bool changed)
        {
            string clean = Sanitize(raw);
            changed = !string.Equals(clean, raw ?? "", StringComparison.Ordinal);
            return clean;
        }

        public static string SanitizeName(string raw) => Sanitize(raw);

        /// <summary>True if the name is ALREADY in its normalised form -- i.e. SanitizeName would be a no-op.
        /// Used by the server to tell a well-behaved client from one that skipped the client-side pass.</summary>
        public static bool IsCleanName(string name) =>
            name != null && string.Equals(Sanitize(name), name, StringComparison.Ordinal);

        static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return FallbackName;

            // Compose first: NFC folds decomposed accents into single code points, so an "e" plus a combining
            // acute counts as one character rather than tripping the combining-mark limit below. It also means
            // two names that LOOK identical normalise identically, which is the point of comparing them.
            string src;
            try { src = raw.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { src = raw; }   // unpaired surrogates -- the filter below drops them anyway

            var sb = new StringBuilder(Math.Min(src.Length, MaxNameChars * 4));
            int marks = 0;
            bool pendingSpace = false;

            foreach (char c in src)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);

                // DROPPED ENTIRELY, and dropped BEFORE anything is matched against the result: control
                // characters (log injection), format characters (zero-width joiners, bidi overrides),
                // surrogates, private-use and unassigned code points.
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format
                    || cat == UnicodeCategory.Surrogate || cat == UnicodeCategory.PrivateUse
                    || cat == UnicodeCategory.OtherNotAssigned) continue;

                // Any whitespace (including the exotic Unicode spaces) collapses to at most one plain space,
                // and a leading one never starts the name.
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }

                if (ForbiddenPunctuation.IndexOf(c) >= 0) continue;

                bool isMark = cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                           || cat == UnicodeCategory.EnclosingMark;
                if (isMark)
                {
                    if (sb.Length == 0 || marks >= MaxConsecutiveMarks) continue;   // a name cannot START with an accent
                    marks++;
                }
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                {
                    marks = 0;
                }
                else continue;   // anything not explicitly allowed -- symbols, emoji, dingbats

                if (pendingSpace)
                {
                    if (sb.Length >= MaxNameChars) break;
                    sb.Append(' ');
                    pendingSpace = false;
                    marks = 0;
                }
                if (sb.Length >= MaxNameChars) break;
                sb.Append(c);
            }

            string outp = sb.ToString().TrimEnd();

            // A name of nothing but underscores and dots is not a name; require something readable in it.
            bool hasAlnum = false;
            foreach (char c in outp) if (char.IsLetterOrDigit(c)) { hasAlnum = true; break; }
            if (!hasAlnum || outp.Length < MinNameChars) return FallbackName;
            return outp;
        }

        // ---- profile picture -------------------------------------------------------------------------

        public const int AvatarPixels = 128;                 // exactly 128x128; the launcher squishes to fit
        public const int MaxAvatarBytes = 64 * 1024;         // a 128x128 PNG is a few KB; 64K is generous and bounded

        public enum AvatarVerdict { Ok, Empty, TooLarge, NotPng, BadHeader, WrongSize, Animated, Interlaced }

        public static string Explain(AvatarVerdict v) => v switch
        {
            AvatarVerdict.Ok => "ok",
            AvatarVerdict.Empty => "no image",
            AvatarVerdict.TooLarge => $"larger than {MaxAvatarBytes / 1024} KB",
            AvatarVerdict.NotPng => "not a PNG",
            AvatarVerdict.BadHeader => "truncated or malformed PNG header",
            AvatarVerdict.WrongSize => $"not exactly {AvatarPixels}x{AvatarPixels}",
            AvatarVerdict.Animated => "animated PNGs are not accepted",
            AvatarVerdict.Interlaced => "interlaced PNGs are not accepted",
            _ => "rejected",
        };

        static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Check a profile picture WITHOUT decoding it.
        ///
        /// Deliberately header-only. Decoding an attacker-supplied image is how image libraries get you, and
        /// the server has no reason to look at the pixels -- it stores the bytes and forwards them. So this
        /// reads the signature and the IHDR (which is fixed-position and fixed-length), confirms the exact
        /// dimensions, and refuses the two structural variants that would surprise a client: APNG (acTL
        /// before IDAT -- an animated "picture" that plays in a nameplate) and interlaced PNG.
        ///
        /// The dimension check is the load-bearing one. It is what stops a decompression bomb: 128x128 is at
        /// most 64 KB of pixels no matter what the compressed bytes claim.</summary>
        public static AvatarVerdict CheckAvatarPng(byte[] png)
        {
            if (png == null || png.Length == 0) return AvatarVerdict.Empty;
            if (png.Length > MaxAvatarBytes) return AvatarVerdict.TooLarge;
            if (png.Length < 8 + 25) return AvatarVerdict.BadHeader;   // signature + a complete IHDR chunk

            for (int i = 0; i < PngSignature.Length; i++)
                if (png[i] != PngSignature[i]) return AvatarVerdict.NotPng;

            // IHDR must be the FIRST chunk (PNG spec), so its position is not a guess.
            if (ReadBE32(png, 8) != 13) return AvatarVerdict.BadHeader;                      // IHDR length
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return AvatarVerdict.BadHeader;

            int width = ReadBE32(png, 16), height = ReadBE32(png, 20);
            if (width != AvatarPixels || height != AvatarPixels) return AvatarVerdict.WrongSize;
            if (png[28] != 0) return AvatarVerdict.Interlaced;   // byte 28 = interlace method (IHDR+12)

            return HasChunkBeforeIdat(png, 'a', 'c', 'T', 'L') ? AvatarVerdict.Animated : AvatarVerdict.Ok;
        }

        /// <summary>Walk the chunk list looking for one type before the first IDAT. Bounds-checked at every
        /// step: a hostile length field is the obvious way to walk this off the end of the buffer.</summary>
        static bool HasChunkBeforeIdat(byte[] png, char a, char b, char c, char d)
        {
            int pos = 8;
            while (pos + 8 <= png.Length)
            {
                int len = ReadBE32(png, pos);
                if (len < 0 || len > png.Length) return false;                 // nonsense length -> stop, do not trust it
                long next = (long)pos + 12 + len;                              // length + type + data + crc
                if (next > png.Length) return false;
                char t0 = (char)png[pos + 4], t1 = (char)png[pos + 5], t2 = (char)png[pos + 6], t3 = (char)png[pos + 7];
                if (t0 == 'I' && t1 == 'D' && t2 == 'A' && t3 == 'T') return false;
                if (t0 == a && t1 == b && t2 == c && t3 == d) return true;
                pos = (int)next;
            }
            return false;
        }

        static int ReadBE32(byte[] b, int i) =>
            (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

        /// <summary>FNV-1a over the avatar bytes -- the identity other clients cache on, so a returning
        /// player's picture is not re-sent. Not a security boundary: the bytes are validated on arrival, and
        /// a collision costs a wrong thumbnail, not access to anything.</summary>
        public static ulong AvatarHash(byte[] png)
        {
            if (png == null || png.Length == 0) return 0;
            ulong h = 14695981039346656037UL;
            foreach (byte x in png) { h ^= x; h *= 1099511628211UL; }
            return h;
        }
    }
}
