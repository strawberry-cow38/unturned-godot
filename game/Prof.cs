using Godot;

namespace UnturnedGodot
{
    // Lightweight per-system CPU timing for the F3 profiler (master: help ME find savings). A system wraps its per-frame
    // work with `ulong t = Time.GetTicksUsec();` ... `Prof.Add("name", t);`. The profiler sums per window and shows the
    // top spenders in ms, so a CPU process SPIKE points straight at the system responsible. Near-zero overhead (a dict add).
    public static class Prof
    {
        public static readonly System.Collections.Generic.Dictionary<string, long> Us = new();
        public static void Add(string key, ulong startUsec)
        {
            long e = (long)(Time.GetTicksUsec() - startUsec);
            Us.TryGetValue(key, out var v); Us[key] = v + e;
        }
        public static void Reset() => Us.Clear();
    }
}
