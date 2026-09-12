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
    /// would be the lie. When identities arrive, match on those and keep these as a fallback.</summary>
    public sealed class ServerModeration
    {
        readonly List<BanEntry> _entries = new List<BanEntry>();

        public IReadOnlyList<BanEntry> Entries => _entries;
        public int Count => _entries.Count;

        /// <summary>Is this peer barred right now? Expired entries are dropped as they are found, so a list that
        /// is never explicitly pruned still cannot accumulate dead rows across a long uptime.</summary>
        public bool IsBanned(uint ipv4, string name, long nowUnix, out BanEntry hit)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.HasExpired(nowUnix)) { _entries.RemoveAt(i); continue; }
                // An entry with no address only matches by name, or an unaddressed peer (ip 0) would match it
                // and every other unaddressed peer at once -- one loopback ban locking out the whole box.
                bool byIp = e.Ipv4 != 0 && ipv4 != 0 && e.Ipv4 == ipv4;
                bool byName = !string.IsNullOrEmpty(e.Name) && NameMatches(e.Name, name);
                if (byIp || byName) { hit = e; return true; }
            }
            hit = default;
            return false;
        }

        /// <summary>Ordinal, case-insensitive, trimmed. Ordinal on purpose: a culture-aware compare makes
        /// "different string, same meaning" decisions that a ban list should not be making on its own.</summary>
        public static bool NameMatches(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Record a removal. Replaces any existing entry for the same handle rather than stacking, so
        /// re-banning someone extends their sentence instead of leaving two rows that disagree.</summary>
        public void Add(uint ipv4, string name, long expiresUnix, string reason, ModerationKind kind)
        {
            Remove(ipv4, name);
            _entries.Add(new BanEntry
            {
                Ipv4 = ipv4,
                Name = name ?? "",
                ExpiresUnix = expiresUnix,
                Reason = string.IsNullOrEmpty(reason) ? "" : reason,
                Kind = kind,
            });
        }

        /// <summary>Lift by address and/or name. Returns how many rows went.</summary>
        public int Remove(uint ipv4, string name)
        {
            int n = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                bool byIp = e.Ipv4 != 0 && ipv4 != 0 && e.Ipv4 == ipv4;
                bool byName = !string.IsNullOrEmpty(name) && NameMatches(e.Name, name);
                if (byIp || byName) { _entries.RemoveAt(i); n++; }
            }
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
