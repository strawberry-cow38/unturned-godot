using System;
using System.Collections.Generic;

namespace SDG.Unturned
{
    public enum ModerationKind : byte { Kick = 0, Ban = 1 }

    /// <summary>One removal: who, until when, and why.</summary>
    public struct BanEntry
    {
        public uint Ipv4;            // 0 = no address was available (loopback/mem transports); then Name is the only handle
        public string Name;

        /// <summary>Verified SteamID64, "" when the ban predates identities or was placed on a peer that
        /// never signed in. The STRONG handle: survives a new lease, a VPN and a rename, and cannot be
        /// chosen by the person being banned.</summary>
        public string SteamId;
        public long ExpiresUnix;     // 0 = PERMANENT. Not long.MaxValue: 0 is unambiguous in a text file a human edits.
        public string Reason;
        public ModerationKind Kind;

        public bool IsPermanent => ExpiresUnix == 0;
        public bool HasExpired(long nowUnix) => !IsPermanent && nowUnix >= ExpiresUnix;
    }

    /// <summary>Who is not allowed back in, and for how long.
    ///
    /// Engine-free so the rules are L0-testable: expiry, matching and duration parsing are where this goes wrong,
    /// and none of them need a socket. The game side owns disconnecting a live peer and persisting the list.
    ///
    /// ⚠ IDENTITY IS WEAK AND THAT IS NOT HIDDEN. Retail bans a SteamID; this port has no Steam, so the only
    /// stable handle a peer carries is its IPv4 address (ITransportConnection.TryGetIPv4Address) plus the name it
    /// chose. An IP ban catches a household, misses a reconnect on a new lease, and cannot see through a VPN;
    /// a name ban is trivially defeated by typing a different name. Both are matched, so a ban is harder to walk
    /// around than either alone -- but this is a nuisance control, not a security boundary, and calling it one
    /// would be the lie.
    ///
    /// ⭐ IDENTITIES HAVE NOW ARRIVED, and this does what the line above said to do: match on the SteamId and
    /// keep address and name as fallbacks. A SteamId ban survives a new lease, a VPN and a rename, and cannot
    /// be shrugged off by typing something else -- so for a signed-in peer this IS a real control rather than a
    /// nuisance one. It degrades to exactly the old behaviour for a peer with no id, which is everyone until
    /// the v54 handshake carries a token.
    ///
    /// ⚠ NOTE THE SEMANTICS INVERT relative to the player-save re-key landing alongside this. There, matching
    /// too broadly is the vulnerability -- a name match must never reach a claimed save. Here, matching broadly
    /// is the POINT: a ban should catch its evader by any handle that still fits. Same ingredients, opposite
    /// rule, so they share no code on purpose.</summary>
    public sealed class ServerModeration
    {
        readonly List<BanEntry> _entries = new List<BanEntry>();

        public IReadOnlyList<BanEntry> Entries => _entries;
        public int Count => _entries.Count;

        /// <summary>The one matching rule, because there were three copies of it and a fourth handle would have
        /// made four places to forget. Any handle that fits is a hit -- see the class comment on why breadth is
        /// correct here and wrong for saves.
        ///
        /// ⚠ EACH HANDLE MUST BE PRESENT ON BOTH SIDES. The ip guard already read `e.Ipv4 != 0 && ipv4 != 0`
        /// precisely so one loopback ban could not lock out every unaddressed peer at once; a SteamId compared
        /// without that guard repeats the bug exactly, and worse -- "" == "" would match every not-signed-in
        /// player against any identity ban, i.e. one ban closes the server to everybody who has not signed in.</summary>
        public static bool Matches(in BanEntry e, uint ipv4, string steamId, string name)
        {
            bool bySteam = !string.IsNullOrEmpty(e.SteamId) && !string.IsNullOrEmpty(steamId)
                           && string.Equals(e.SteamId, steamId, StringComparison.Ordinal);
            bool byIp = e.Ipv4 != 0 && ipv4 != 0 && e.Ipv4 == ipv4;
            bool byName = !string.IsNullOrEmpty(e.Name) && NameMatches(e.Name, name);
            return bySteam || byIp || byName;
        }

        /// <summary>Is this peer barred right now? Expired entries are dropped as they are found, so a list that
        /// is never explicitly pruned still cannot accumulate dead rows across a long uptime.</summary>
        public bool IsBanned(uint ipv4, string name, long nowUnix, out BanEntry hit)
            => IsBanned(ipv4, "", name, nowUnix, out hit);

        public bool IsBanned(uint ipv4, string steamId, string name, long nowUnix, out BanEntry hit)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.HasExpired(nowUnix)) { _entries.RemoveAt(i); continue; }
                if (Matches(e, ipv4, steamId, name)) { hit = e; return true; }
            }
            hit = default;
            return false;
        }

        /// <summary>Ordinal, case-insensitive, trimmed. Ordinal on purpose: a culture-aware compare makes
        /// "different string, same meaning" decisions that a ban list should not be making on its own.</summary>
        public static bool NameMatches(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Record a removal. One row per handle rather than a stack -- but the new row keeps
        /// whichever sentence is LONGER, so re-banning genuinely extends.
        ///
        /// ⚠ It used to just Remove-then-Add, which the doc called "extends" and the code did not do. Three
        /// ways that lost time somebody had already been given: `ban bob perm` then `ban bob 10m` SHORTENED
        /// a permanent ban to ten minutes; `ban bob perm` then `kick alice 10m` from bob's household IP
        /// replaced bob's row entirely; and a permanent row could be dropped by a timed one arriving on the
        /// other handle. Permanent (0) always wins, and a later expiry beats an earlier one.</summary>
        public void Add(uint ipv4, string name, long expiresUnix, string reason, ModerationKind kind)
            => Add(ipv4, "", name, expiresUnix, reason, kind);

        public void Add(uint ipv4, string steamId, string name, long expiresUnix, string reason, ModerationKind kind)
        {
            // What the matching rows already carry, before they go.
            bool hadPermanent = false;
            long longest = long.MinValue;
            foreach (var e in _entries)
            {
                if (!Matches(e, ipv4, steamId, name)) continue;
                if (e.IsPermanent) hadPermanent = true;
                else if (e.ExpiresUnix > longest) longest = e.ExpiresUnix;
            }

            Remove(ipv4, steamId, name);

            long keep = expiresUnix;
            if (hadPermanent || expiresUnix == 0) keep = 0;              // permanent, either side, stays permanent
            else if (longest > keep) keep = longest;                     // never shorten an existing sentence

            _entries.Add(new BanEntry
            {
                Ipv4 = ipv4,
                SteamId = steamId ?? "",
                Name = name ?? "",
                ExpiresUnix = keep,
                Reason = string.IsNullOrEmpty(reason) ? "" : reason,
                Kind = kind,
            });
        }

        /// <summary>Lift by address and/or name. Returns how many rows went.</summary>
        public int Remove(uint ipv4, string name) => Remove(ipv4, "", name);

        public int Remove(uint ipv4, string steamId, string name)
        {
            int n = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (Matches(_entries[i], ipv4, steamId, name)) { _entries.RemoveAt(i); n++; }
            return n;
        }

        public int Prune(long nowUnix)
        {
            int n = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].HasExpired(nowUnix)) { _entries.RemoveAt(i); n++; }
            return n;
        }

        public void Clear() => _entries.Clear();
        public void Load(IEnumerable<BanEntry> rows) { _entries.Clear(); if (rows != null) _entries.AddRange(rows); }

        // ---- duration parsing -------------------------------------------------------------------------
        /// <summary>Parse "10m" / "2h" / "3d" / "45s" / "perm" / a bare number of MINUTES.
        ///
        /// Bare numbers are minutes because that is what an admin typing `kick griefer 10` means; seconds would
        /// make the common case a ten-second kick that reads as "the command did nothing". Returns false rather
        /// than guessing on anything else -- a mistyped duration must not silently become a permanent ban.</summary>
        public static bool TryParseDuration(string s, out long seconds, out bool permanent)
        {
            seconds = 0; permanent = false;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().ToLowerInvariant();
            if (s == "perm" || s == "permanent" || s == "forever") { permanent = true; return true; }

            char unit = s[s.Length - 1];
            string num = char.IsDigit(unit) ? s : s.Substring(0, s.Length - 1);
            if (!long.TryParse(num, out long v) || v < 0) return false;
            long mult = char.IsDigit(unit) ? 60L        // bare number = minutes
                      : unit == 's' ? 1L
                      : unit == 'm' ? 60L
                      : unit == 'h' ? 3600L
                      : unit == 'd' ? 86400L
                      : unit == 'w' ? 604800L
                      : -1L;
            if (mult < 0) return false;
            // 0 would collide with the PERMANENT sentinel, so an explicit zero is rejected rather than silently
            // becoming a forever-ban -- which is the one mistake here that cannot be undone by waiting.
            if (v == 0) return false;
            // AND SO WOULD AN OVERFLOW. `long.TryParse` happily accepts 9223372036854775807, and multiplying
            // that by 604800 wraps: measured, "9223372036854775807w" produced -604800 seconds and
            // "153722867280912931h" produced a 52-minute ban out of nonsense. Both announce a sentence that
            // is not the one applied. A century is past every real use and far from the wrap point.
            const long MaxSeconds = 100L * 365L * 86400L;
            if (v > MaxSeconds / mult) return false;
            seconds = v * mult;
            return true;
        }

        /// <summary>"permanent" or a compact "2h 30m", for the console reply and the kick message.</summary>
        public static string DescribeDuration(long seconds, bool permanent)
        {
            if (permanent) return "permanently";
            if (seconds <= 0) return "for no time at all";
            long d = seconds / 86400, h = seconds % 86400 / 3600, m = seconds % 3600 / 60, s = seconds % 60;
            var parts = new List<string>();
            if (d > 0) parts.Add(d + "d");
            if (h > 0) parts.Add(h + "h");
            if (m > 0) parts.Add(m + "m");
            if (s > 0 && d == 0 && h == 0) parts.Add(s + "s");   // seconds only matter on short kicks
            return "for " + string.Join(" ", parts);
        }
    }
}
