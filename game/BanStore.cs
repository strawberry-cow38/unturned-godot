using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>Reads and writes the ban list.
    ///
    /// DELIBERATELY NOT PART OF THE WORLD SAVE. `wipe` exists to reset the world, and a wipe that also
    /// unbans everyone hands the server back to whoever it was defended against at the worst moment. Bans
    /// outlive worlds, so they live in their own file and neither save nor wipe touches them.
    ///
    /// Plain TSV rather than JSON, for the same reason the map data uses it: an admin fixing a mistaken ban
    /// at 3am should be able to open the file and delete a line. One entry per line:
    ///
    ///     ipv4 &lt;TAB&gt; expiresUnix &lt;TAB&gt; kind &lt;TAB&gt; name &lt;TAB&gt; reason
    ///
    /// expiresUnix 0 means PERMANENT, matching ServerModeration's sentinel -- and the file is where that
    /// choice pays off, because 0 is obvious to a human editing it in a way that 9223372036854775807 is not.
    /// The name and reason come LAST because they are the only free-text fields; a tab inside a reason can
    /// then only corrupt the reason, not shift a date into a name.</summary>
    public static class BanStore
    {
        public static string Path = "user://bans.tsv";

        /// <summary>Load into a moderation list, replacing whatever it held. Returns how many entries
        /// landed. A missing file is normal (no bans yet), not an error.</summary>
        public static int Load(ServerModeration mod)
        {
            if (mod == null) return 0;
            string p = ProjectSettings.GlobalizePath(Path);
            if (!System.IO.File.Exists(p)) return 0;

            var rows = new List<BanEntry>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int lineNo = 0;
            foreach (var line in System.IO.File.ReadAllLines(p))
            {
                lineNo++;
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 5)
                { Log.Err($"[bans] {Path}:{lineNo} has {c.Length} fields, want 5 -- skipped"); continue; }
                if (!uint.TryParse(c[0], System.Globalization.NumberStyles.Integer, ci, out uint ip) ||
                    !long.TryParse(c[1], System.Globalization.NumberStyles.Integer, ci, out long expires))
                { Log.Err($"[bans] {Path}:{lineNo} has an unparseable address or expiry -- skipped"); continue; }
                var kind = c[2].Trim().Equals("Ban", System.StringComparison.OrdinalIgnoreCase)
                    ? ModerationKind.Ban : ModerationKind.Kick;
                rows.Add(new BanEntry { Ipv4 = ip, ExpiresUnix = expires, Kind = kind, Name = c[3], Reason = c[4] });
            }
            mod.Load(rows);
            // Prune on load rather than carrying dead rows: a server that ran a week ago should not start
            // holding bans that expired while it was down.
            int dropped = mod.Prune(System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (rows.Count > 0) Log.Print($"[bans] loaded {mod.Count} active from {Path}" + (dropped > 0 ? $" ({dropped} expired)" : ""));
            return mod.Count;
        }

        /// <summary>Write the list out. Called on every change, because the alternative is losing a ban to
        /// a crash -- and a ban that did not survive the restart is the one case where the admin thinks the
        /// problem is handled and it is not.</summary>
        public static void Save(ServerModeration mod)
        {
            if (mod == null) return;
            string p = ProjectSettings.GlobalizePath(Path);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# ipv4\texpiresUnix (0 = permanent)\tkind\tname\treason");
                foreach (var e in mod.Entries)
                {
                    // Strip tabs and newlines from the free text. They cannot arrive from chat (the
                    // sanitiser removes them) but a reason typed at the server console never passed
                    // through it, and one tab would shift every later field by one.
                    string name = Flatten(e.Name), reason = Flatten(e.Reason);
                    sb.Append(e.Ipv4).Append('\t').Append(e.ExpiresUnix).Append('\t')
                      .Append(e.Kind).Append('\t').Append(name).Append('\t').Append(reason).Append('\n');
                }
                System.IO.File.WriteAllText(p, sb.ToString());
            }
            catch (System.Exception ex)
            {
                // Loud, and NOT fatal. A server that cannot write its ban file should keep running with the
                // ban live in memory rather than refuse to boot -- but the admin has to know it will not
                // survive a restart.
                Log.Err($"[bans] could not write {Path}: {ex.Message}. Bans are in effect but will NOT survive a restart.");
            }
        }

        static string Flatten(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
