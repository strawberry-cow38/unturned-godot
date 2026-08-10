using Godot;

namespace UnturnedGodot
{
    // Per-system CPU timing for the F3 profiler (master: help ME find savings).
    //
    // WHAT CHANGED AND WHY (2026-08-10, strawberry: "our issue is cpu. its always gonna be cpu").
    // The old version summed microseconds per key per window and printed the biggest. That is enough to rank
    // what you already instrumented and nothing else, and it quietly implied the ranking was the frame. On the
    // real map it read `lookat 2.7  salvage 0.0` against a `cpu process 6.4 ms` line two rows above -- so more
    // than half the frame was unattributed and NOTHING on screen said so. A profiler that cannot state its own
    // coverage invites you to optimise the only thing it can see. So this now tracks, per key:
    //
    //   Us     total microseconds in the window   -- what it always had
    //   Calls  how many times it ran              -- 2 ms in one call and 2 ms across 300 are different bugs,
    //                                                and 300 is the shape of per-node _Process on 148 lamps
    //   Peak   the worst SINGLE FRAME             -- a 20 ms hitch inside a 250 ms window is 8% of the sum and
    //                                                invisible in an average; stutter is the thing being hunted
    //   PhysUs the part that ran in a physics step -- so attribution can be checked against TimePhysicsProcess
    //          separately from TimeProcess, which matters when physics is half the frame
    //
    // Peak needs frame boundaries, and taking them from the profiler's own _Process would depend on node order.
    // The engine's own frame counters are asked instead (see RollFrame), so a key folds its own frame when the
    // count moves and no caller has to cooperate.
    public static class Prof
    {
        public static readonly System.Collections.Generic.Dictionary<string, long> Us = new();
        public static readonly System.Collections.Generic.Dictionary<string, long> PhysUs = new();
        public static readonly System.Collections.Generic.Dictionary<string, int> Calls = new();
        public static readonly System.Collections.Generic.Dictionary<string, long> Peak = new();
        static readonly System.Collections.Generic.Dictionary<string, long> _frameUs = new();
        static ulong _frame;

        /// Plain tallies (rays cast, path queries issued) -- deliberately NOT in Us, because the overlay
        /// renders Us as milliseconds by dividing by 1000. A count of 40 parked in Us prints as "0.0",
        /// which is indistinguishable from zero: that is exactly how "z.rays 0.0" got read as "the sight
        /// path never runs" when it only meant "fewer than about fifty". Counts print as counts.
        public static readonly System.Collections.Generic.Dictionary<string, long> Counts = new();

        /// <summary>Scoped timing: `using var _ = Prof.Scope("name");`. Preferred over Add -- it cannot be
        /// left unclosed on an early return or an exception, which is how a hot path ends up looking free.</summary>
        public static Timer Scope(string key) => new(key);

        public readonly struct Timer : System.IDisposable
        {
            readonly string _key; readonly ulong _t0;
            public Timer(string key) { _key = key; _t0 = Time.GetTicksUsec(); _depth++; }
            public void Dispose() { _depth--; Add(_key, _t0); }   // decrement FIRST so the outermost scope sees depth 0
        }

        // NESTING DEPTH, and why coverage would be nonsense without it. PlayerController._Process is timed as a
        // whole AND contains its own `lookat` and `salvage` timers. Summing every key would count that inner
        // time twice and report attribution ABOVE 100% -- a coverage number that overshoots is worse than none,
        // because the one thing this line exists to say is "how much is still unexplained".
        //
        // So per-key totals keep recording everything (the breakdown wants both the container and its parts),
        // while the coverage total only takes scopes that closed at depth 0. A bare Add() inside a timed scope
        // is likewise not a root, which is exactly the lookat/salvage case.
        static int _depth;
        static long _rootUs, _rootPhysUs;

        public static void Add(string key, ulong startUsec)
        {
            long e = (long)(Time.GetTicksUsec() - startUsec);
            RollFrame();
            Us.TryGetValue(key, out var v); Us[key] = v + e;
            Calls.TryGetValue(key, out var c); Calls[key] = c + 1;
            _frameUs.TryGetValue(key, out var f); _frameUs[key] = f + e;
            // Engine.IsInPhysicsFrame() is true inside a physics step, so the split is free and needs no
            // naming convention the caller could get wrong.
            bool phys = Engine.IsInPhysicsFrame();
            if (phys) { PhysUs.TryGetValue(key, out var p); PhysUs[key] = p + e; }
            if (_depth <= 0) { _rootUs += e; if (phys) _rootPhysUs += e; }
        }

        /// <summary>Fold the frame that just ended into the per-key peak. Driven by the engine's own counters
        /// rather than a call from the overlay, so it does not depend on which node processes first.
        ///
        /// PROCESS + PHYSICS frames summed, not process alone: in a headless boot (the L1 host, and the
        /// dedicated server) process frames never advance at all -- measured, `procFrames=0` against
        /// `physFrames=3` -- so keying on them alone means the fold never happens and every Peak silently
        /// stays 0. Summing both advances in either context. No key straddles the two callback kinds (a class
        /// times `_Process` as "Foo" and `_PhysicsProcess` as "Foo.phys"), so nothing gets split by this.</summary>
        static void RollFrame()
        {
            ulong f = Engine.GetProcessFrames() + Engine.GetPhysicsFrames();
            if (f == _frame) return;
            foreach (var kv in _frameUs)
            {
                Peak.TryGetValue(kv.Key, out var m);
                if (kv.Value > m) Peak[kv.Key] = kv.Value;
            }
            _frameUs.Clear();
            _frame = f;
        }

        public static void Count(string key, long n)
        {
            Counts.TryGetValue(key, out var v); Counts[key] = v + n;
        }

        /// <summary>Instrumented microseconds this window counting each scope ONCE -- outermost only, so a
        /// timed system containing timed sub-parts is not counted twice. This is the coverage numerator; do
        /// not substitute the sum of Us, which deliberately double-counts so the breakdown can show both.</summary>
        public static (long Total, long Phys) Totals() => (_rootUs, _rootPhysUs);

        public static void Reset()
        {
            Us.Clear(); PhysUs.Clear(); Calls.Clear(); Peak.Clear(); Counts.Clear(); _frameUs.Clear();
            _rootUs = 0; _rootPhysUs = 0;
        }
    }
}
