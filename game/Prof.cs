using Godot;

namespace UnturnedGodot
{
    // Lightweight per-system CPU timing for the F3 profiler (master: help ME find savings). A system wraps its per-frame
    // work with `ulong t = Time.GetTicksUsec();` ... `Prof.Add("name", t);`. The profiler sums per window and shows the
    // top spenders in ms, so a CPU process SPIKE points straight at the system responsible. Near-zero overhead (a dict add).
    public static class Prof
    {
        public static readonly System.Collections.Generic.Dictionary<string, long> Us = new();

        /// Plain tallies (rays cast, path queries issued) -- deliberately NOT in Us, because the overlay
        /// renders Us as milliseconds by dividing by 1000. A count of 40 parked in Us prints as "0.0",
        /// which is indistinguishable from zero: that is exactly how "z.rays 0.0" got read as "the sight
        /// path never runs" when it only meant "fewer than about fifty". Counts print as counts.
        public static readonly System.Collections.Generic.Dictionary<string, long> Counts = new();

        public static void Add(string key, ulong startUsec)
        {
            long e = (long)(Time.GetTicksUsec() - startUsec);
            Us.TryGetValue(key, out var v); Us[key] = v + e;
        }
        public static void Count(string key, long n)
        {
            Counts.TryGetValue(key, out var v); Counts[key] = v + n;
        }
        public static void Reset() { Us.Clear(); Counts.Clear(); }
    }
}
