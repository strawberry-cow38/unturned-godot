using System;
using System.Text;

namespace SDG.Unturned
{
    /// <summary>
    /// What a player is allowed to write on a sign.
    ///
    /// This is engine-free and separate from the sign NODE on purpose: sign text is the only piece of
    /// player-authored content in the game that other players are shown verbatim, so the rules about
    /// what may be stored have to be enforceable on a dedicated server with no rendering, and testable
    /// without one. A client that sanitises its own input protects nobody -- the text arrives over the
    /// wire from a peer that may not be running our client at all.
    ///
    /// Sanitising here rather than at render time is deliberate: the stored string is what replicates,
    /// what persists, and what every future consumer reads, so it should already be safe by the time
    /// it is stored rather than relying on every reader to clean it again.
    /// </summary>
    public static class SignText
    {
        /// <summary>Hard cap on stored characters. Bounded because this string is replicated to every
        /// player in range and persists in the world -- an unbounded field is a cheap way for one
        /// client to cost every other client bandwidth.</summary>
        public const int MaxChars = 128;

        /// <summary>Most lines a sign may show. Beyond this the sign becomes a wall of text that
        /// overdraws whatever is behind it.</summary>
        public const int MaxLines = 4;

        /// <summary>
        /// Clean a proposed string into what may actually be stored. Never throws and never returns
        /// null: bad input becomes empty, because a rejected edit that silently does nothing is easier
        /// for a player to understand than a half-applied one.
        ///
        /// Rules, in order: drop control characters (except newline), collapse runs of newlines,
        /// cap the line count, trim trailing whitespace per line, then cap total length.
        /// </summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            var sb = new StringBuilder(Math.Min(raw.Length, MaxChars));
            int lines = 1;
            bool lastWasNewline = false;

            foreach (char c in raw)
            {
                if (sb.Length >= MaxChars) break;

                if (c == '\n')
                {
                    // Collapse blank-line spam: a run of newlines counts once. Stops a sign being used
                    // to push its own text off-screen or to draw a tall transparent column.
                    if (lastWasNewline) continue;
                    if (lines >= MaxLines) break;
                    lines++;
                    lastWasNewline = true;
                    sb.Append('\n');
                    continue;
                }

                lastWasNewline = false;

                // Control characters are dropped rather than escaped. Nothing legitimate types them,
                // and passing them through to a label is how a renderer gets surprised. \t is included
                // deliberately -- it would let one client shift text arbitrarily on everyone's screen.
                if (char.IsControl(c)) continue;

                sb.Append(c);
            }

            // Trim trailing whitespace on each line and drop a trailing blank line, so a sign does not
            // render with invisible padding that shifts it off centre.
            var parts = sb.ToString().Split('\n');
            int last = parts.Length - 1;
            while (last >= 0 && parts[last].Trim().Length == 0) last--;
            if (last < 0) return string.Empty;

            var outSb = new StringBuilder();
            for (int i = 0; i <= last; i++)
            {
                if (i > 0) outSb.Append('\n');
                outSb.Append(parts[i].TrimEnd());
            }
            return outSb.ToString();
        }

        /// <summary>Would this string survive sanitising unchanged? Lets a client grey out a Save
        /// button without duplicating the rules, and lets a server cheaply spot a peer that is not
        /// applying them.</summary>
        public static bool IsClean(string raw) => Sanitize(raw) == (raw ?? string.Empty);
    }
}
