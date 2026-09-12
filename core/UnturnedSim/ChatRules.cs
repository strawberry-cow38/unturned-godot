using System;
using System.Globalization;
using System.Text;

namespace SDG.Unturned
{
    /// <summary>Who a chat line came from. The server can talk too, and it must not be impersonable.</summary>
    public enum ChatChannel : byte
    {
        Global = 0,   // a player, to everyone
        Server = 1,   // the SERVER itself: console `say`, join/leave, kick and ban notices
    }

    public enum ChatVerdict : byte { Ok, Empty, TooSoon, Flooding }

    /// <summary>What a chat message is allowed to be.
    ///
    /// SHARES ProfileRules' threat model and deliberately NOT its character policy. A display name can ban
    /// `!?,'":` outright because no name needs them; a chat line is prose and banning them leaves the
    /// player typing in a language with no punctuation. So the rules that carry over are the ones about
    /// what the text can DO, not what it can say:
    ///
    ///   1. MARKUP INJECTION. Godot's RichTextLabel renders BBCode when bbcode_enabled is set, so
    ///      "[img]https://attacker/x.png[/img]" in chat is an IP grabber that fires on every client in the
    ///      server. Brackets are turned into parentheses rather than dropped -- a tag can no longer form,
    ///      and someone typing "[afk]" still reads as "(afk)" instead of silently becoming "afk".
    ///   2. LOG INJECTION AND FAKE LINES. Chat is logged and drawn line-by-line, so an embedded CR/LF lets
    ///      one message forge a second one -- including one that looks like it came from the SERVER.
    ///      All control characters go.
    ///   3. INVISIBLE AND REORDERING CHARACTERS. Cf (zero-width, bidi overrides) can make a line render as
    ///      something other than what it is, or reverse the text printed after it.
    ///   4. UNBOUNDED RENDERING COST. Stacked combining marks draw far outside the chat box.
    ///
    /// THE ORDER MATTERS, same as ProfileRules: sanitise FIRST, then length-check the SANITISED string,
    /// then send only that string. Length-checking the raw input lets 300 zero-width characters eat the
    /// budget for a message that renders as nothing, and matching before stripping lets a soft hyphen
    /// inside "[i&#173;mg]" pass a naive "[img]" test and then vanish at render time.
    ///
    /// AND THE SERVER RE-RUNS IT. The client sanitises so the player sees what they will send; the server
    /// never trusts that and publishes its own answer.</summary>
    public static class ChatRules
    {
        /// <summary>Characters that survive sanitising but are wide enough to matter, capped for the same
        /// reason a name caps them.</summary>
        const int MaxConsecutiveMarks = 2;

        /// <summary>Measured against the chat box, not chosen for roundness: at the UI's font size a line
        /// wraps around 90 characters, so 200 is two full lines and a bit. Long enough to say something,
        /// short enough that one message cannot own the screen.</summary>
        public const int MaxMessageChars = 200;

        /// <summary>Sanitise a chat line. Returns "" when nothing usable survives -- unlike a name, an empty
        /// chat message has no sensible fallback, so the caller rejects it rather than inventing text.</summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string src;
            try { src = raw.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { src = raw; }   // unpaired surrogates; the filter drops them anyway

            var sb = new StringBuilder(Math.Min(src.Length, MaxMessageChars));
            int marks = 0;
            bool pendingSpace = false;

            foreach (char c in src)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);

                // WHITESPACE IS TESTED FIRST, and the order is load-bearing. Tab, CR and LF are BOTH
                // whitespace and category Control, so dropping controls first deleted them outright and
                // welded the words either side together -- "hello\nworld" became "helloworld", which
                // silently changes what the player said. They collapse to a space instead; the control
                // characters that are NOT whitespace still go, below. (U+200B and friends are Format, not
                // whitespace, so they still vanish entirely rather than becoming a space.)
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }

                // Dropped outright, and dropped BEFORE the result is matched or measured.
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format
                    || cat == UnicodeCategory.Surrogate || cat == UnicodeCategory.PrivateUse
                    || cat == UnicodeCategory.OtherNotAssigned) continue;

                bool isMark = cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                           || cat == UnicodeCategory.EnclosingMark;
                if (isMark)
                {
                    if (sb.Length == 0 || marks >= MaxConsecutiveMarks) continue;
                    marks++;
                }
                else marks = 0;

                if (pendingSpace)
                {
                    if (sb.Length >= MaxMessageChars) break;
                    sb.Append(' ');
                    pendingSpace = false;
                }
                if (sb.Length >= MaxMessageChars) break;

                // NEUTRALISED, not dropped: a bracket that vanishes turns "[img]x[/img]" into "imgx/img",
                // which is harmless but silently rewrites what the player typed. Turning it into a paren
                // keeps the line honest and still makes a tag impossible to form.
                sb.Append(c == '[' ? '(' : c == ']' ? ')' : c);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>True if the text is already in its sanitised form -- i.e. Sanitize is a no-op. Lets the
        /// server tell a well-behaved client from one that skipped its own pass.</summary>
        public static bool IsClean(string text) =>
            text != null && string.Equals(Sanitize(text), text, StringComparison.Ordinal);
    }

    /// <summary>Per-player chat rate limiting, engine-free so the rules are testable without a socket.
    ///
    /// Two limits, because one does not cover both shapes of abuse: a MINIMUM GAP stops a hold-the-key
    /// stream, and a BURST WINDOW stops someone pacing themselves just above that gap and still filling the
    /// screen. Either alone leaves the other open.
    ///
    /// The server's own messages are never rate limited -- a kick notice must go out even if the console is
    /// busy -- so this is only ever consulted for ChatChannel.Global.</summary>
    public sealed class ChatRateLimiter
    {
        /// <summary>Shortest gap between two messages from one player.</summary>
        public const double MinGapSeconds = 0.75;

        /// <summary>At most this many messages in BurstWindowSeconds.</summary>
        public const int BurstCount = 5;
        public const double BurstWindowSeconds = 8.0;

        readonly System.Collections.Generic.Dictionary<ushort, double[]> _recent =
            new System.Collections.Generic.Dictionary<ushort, double[]>();
        readonly System.Collections.Generic.Dictionary<ushort, int> _next =
            new System.Collections.Generic.Dictionary<ushort, int>();

        /// <summary>May this player speak now? Does NOT record the attempt -- call Record only when the
        /// message is actually accepted, so a rejected message cannot deepen its own rate limit.</summary>
        public ChatVerdict Check(ushort playerId, double nowSeconds)
        {
            if (!_recent.TryGetValue(playerId, out var stamps)) return ChatVerdict.Ok;

            double newest = double.NegativeInfinity;
            int inWindow = 0;
            foreach (double t in stamps)
            {
                if (t <= 0) continue;
                if (t > newest) newest = t;
                if (nowSeconds - t < BurstWindowSeconds) inWindow++;
            }
            if (newest > double.NegativeInfinity && nowSeconds - newest < MinGapSeconds) return ChatVerdict.TooSoon;
            if (inWindow >= BurstCount) return ChatVerdict.Flooding;
            return ChatVerdict.Ok;
        }

        /// <summary>Record an ACCEPTED message. A ring of BurstCount stamps per player: bounded memory, and
        /// no allocation per message.</summary>
        public void Record(ushort playerId, double nowSeconds)
        {
            if (!_recent.TryGetValue(playerId, out var stamps))
            {
                stamps = new double[BurstCount];
                _recent[playerId] = stamps;
                _next[playerId] = 0;
            }
            int i = _next[playerId];
            stamps[i] = nowSeconds;
            _next[playerId] = (i + 1) % BurstCount;
        }

        /// <summary>Drop a player's history on disconnect, so a recycled player id does not inherit the
        /// previous occupant's rate limit.</summary>
        public void Forget(ushort playerId) { _recent.Remove(playerId); _next.Remove(playerId); }

        public void Clear() { _recent.Clear(); _next.Clear(); }

        public static string Explain(ChatVerdict v) => v switch
        {
            ChatVerdict.Ok => "ok",
            ChatVerdict.Empty => "say something first",
            ChatVerdict.TooSoon => "slow down",
            ChatVerdict.Flooding => "you are sending messages too fast",
            _ => "rejected",
        };
    }
}
