using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    // The F3 profiler's COVERAGE number -- "how much of the frame is actually attributed" -- is only worth
    // anything if it counts each scope once. PlayerController._Process is timed as a whole and contains its
    // own `lookat` and `salvage` timers; summing every key would count that inner time twice and report
    // attribution above 100%. An overcounting coverage line is worse than none, because the single thing it
    // exists to say is how much is still UNexplained.
    //
    // Timings are compared to each other, never to wall-clock thresholds: a shared ARM box under a test run
    // makes "took about 2 ms" a coin flip, while "the outer scope is at least the inner scope" and "the root
    // total equals the outer scope exactly" hold whatever the machine is doing.
    public class ProfilerNestingContract : GameTest
    {
        public override string Name => "prof.coverage_counts_each_scope_once";

        /// Burn a little CPU without sleeping -- a sleep would not accumulate ticks the way real work does.
        static void Spin(ulong usec)
        {
            ulong t0 = Time.GetTicksUsec();
            while (Time.GetTicksUsec() - t0 < usec) { }
        }

        public override IEnumerable<Step> Run()
        {
            Prof.Reset();
            using (Prof.Scope("outer"))
            {
                Spin(600);
                using (Prof.Scope("inner")) Spin(600);
                // A bare Add inside a timed scope is the lookat/salvage shape: it must show in the breakdown
                // but must NOT add to the root total, or the container's own time is counted twice over.
                ulong t = Time.GetTicksUsec(); Spin(300); Prof.Add("bare", t);
            }
            yield return Ticks(1);

            Prof.Us.TryGetValue("outer", out long outer);
            Prof.Us.TryGetValue("inner", out long inner);
            Prof.Us.TryGetValue("bare", out long bare);
            var (total, _) = Prof.Totals();

            T.Check("all three scopes recorded time", outer > 0 && inner > 0 && bare > 0);
            T.Check("the container is at least as big as what it contains", outer >= inner + bare);
            // THE ONE THAT MATTERS. Remove the depth guard in Prof.Add and this becomes outer+inner+bare.
            T.Check("root total counts ONLY the outermost scope (no double-count)", total == outer);
            T.Check("...and is strictly less than the naive sum of every key", total < outer + inner + bare);
            T.Check("nested keys still appear in the breakdown", Prof.Calls["inner"] == 1 && Prof.Calls["bare"] == 1);

            // A second, separate top-level scope DOES add to the root total -- otherwise the guard would be
            // suppressing real attribution rather than just the nesting, and coverage would read too low.
            Prof.Reset();
            using (Prof.Scope("a")) Spin(400);
            using (Prof.Scope("b")) Spin(400);
            var (two, _) = Prof.Totals();
            Prof.Us.TryGetValue("a", out long ua); Prof.Us.TryGetValue("b", out long ub);
            T.Check("two sibling scopes both count toward coverage", two == ua + ub && ua > 0 && ub > 0);

            // Peak is per FRAME, not per call: two calls in one frame add up rather than taking the max, which
            // is what makes a hitch attributable to the system that caused it.
            Prof.Reset();
            using (Prof.Scope("twice")) Spin(300);
            using (Prof.Scope("twice")) Spin(300);
            yield return Ticks(2);   // let the frame counter move so the frame folds into Peak
            using (Prof.Scope("twice")) Spin(100);
            Prof.Peak.TryGetValue("twice", out long peak);
            Prof.Us.TryGetValue("twice", out long twiceTotal);
            T.Check("peak folds a whole frame's calls together", peak > 0 && peak <= twiceTotal);
            T.Check("peak counts the busy frame, not the quiet one", Prof.Calls["twice"] == 3);
            Prof.Reset();
        }
    }
}
