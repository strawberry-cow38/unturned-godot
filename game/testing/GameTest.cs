using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Phase 2 of docs/TESTING_PROPOSAL.md -- the L1 in-engine test layer. A GameTest is a coroutine that builds a small
    // world into its per-test `World` sandbox and asserts via `T.Check`. The host (TestHost) advances the coroutine one
    // PHYSICS TICK at a time, so `yield return Ticks(n)` consumes n fixed 50Hz ticks and `yield return Until(cond)` polls
    // each tick with a sim-time cap -- deterministic, no wall-clock. Many tests share ONE engine boot.
    public abstract class GameTest
    {
        public abstract string Name { get; }        // dotted, e.g. "power.chain_passthrough" (used for --tests globbing + ordering)
        public virtual int Tier => 1;               // 0 = smoke, 1 = feature, 2 = slow/composite -> run order (simplest first)
        public virtual double TimeoutSimSeconds => 15;   // per-test watchdog (sim time), the host aborts past this

        public Node3D World;                         // per-test sandbox, provided + torn down by the host
        public TestContext T;                        // Check/Fail + seeded RNG, provided by the host
        protected SceneTree Tree => World?.GetTree();

        public abstract IEnumerable<Step> Run();     // the test body

        protected Step Ticks(int n) => Step.Ticks(n);
        protected Step Until(Func<bool> cond, double maxSimSeconds = 5) => Step.Until(cond, maxSimSeconds);
    }

    // A yield instruction: advance N physics ticks, or poll a condition until true (or a sim-time cap trips a timeout).
    public readonly struct Step
    {
        public enum Mode { Ticks, Until }
        public readonly Mode Kind;
        public readonly int N;
        public readonly Func<bool> Cond;
        public readonly double MaxSimSeconds;
        Step(Mode k, int n, Func<bool> c, double max) { Kind = k; N = n; Cond = c; MaxSimSeconds = max; }
        public static Step Ticks(int n) => new(Mode.Ticks, Math.Max(1, n), null, 0);
        public static Step Until(Func<bool> c, double max) => new(Mode.Until, 0, c, max);
    }

    // Per-test result recorder. Each Check appends a line; any false marks the test failed. Fail-fast is the host's job.
    public sealed class TestContext
    {
        public readonly List<(string desc, bool ok)> Checks = new();
        public bool Failed { get; private set; }
        public string FirstFailure { get; private set; }
        public RandomNumberGenerator Rng;            // seeded from the test name (printed on failure, override via UG_SEED)

        public void Check(string desc, bool ok)
        {
            Checks.Add((desc, ok));
            if (!ok && !Failed) { Failed = true; FirstFailure = desc; }
        }
        public void Fail(string desc) => Check(desc, false);
    }
}
