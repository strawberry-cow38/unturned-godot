using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UnturnedGodot.Testing
{
    // Runs every GameTest in ONE engine boot: discovers subclasses by reflection, sorts simplest-first (Tier then name),
    // and drives each one's coroutine a physics tick at a time in _PhysicsProcess. Between tests it frees the sandbox +
    // resets known global statics, then quits with exit 0 (all pass) / 1 (any fail). Added by Main on `--tests[=glob]`.
    public partial class TestHost : Node
    {
        public string Filter = "*";

        readonly List<GameTest> _tests = new();
        int _idx = -1;
        GameTest _cur; TestContext _ctx; Node3D _sandbox;
        IEnumerator<Step> _co; Step _step; int _ticksLeft; double _untilElapsed, _testSim;
        int _cooldown;             // ticks to wait after a QueueFree so freed nodes leave the global groups before the next test
        int _passed, _failed; double _t0;
        readonly Stopwatch _sw = new();

        public override void _Ready()
        {
            // SURVIVE A PAUSED TREE. This host drives every test from _PhysicsProcess, so as a pausable node it
            // would stop the instant a test called GetTree().Paused = true -- and the test would then sit there
            // until the watchdog killed it, reporting a TIMEOUT that looks like a hang in the code under test.
            // Freeze mode is exactly such a feature, and it cannot be tested at all without this.
            ProcessMode = Node.ProcessModeEnum.Always;

            StructureManager.PersistenceEnabled = false;   // L1 must never touch the real user://structures.json -- see that field
            BugReporter.EnterTestMode();   // L1 must never upload the developer's REAL queued bug reports to production -- see that method
            Deployable.InstantRampForTests = true;   // L1: generators settle their spin-up/cooldown instantly so power-flow checks see steady state (the gradual ramp is gameplay-verified in-render)
            Discover();
            _t0 = Time.GetTicksMsec();
            if (_tests.Count == 0) { GD.Print($"[L1] no tests match filter '{Filter}'"); GetTree().Quit(0); return; }
            GD.Print($"[L1] running {_tests.Count} in-engine test(s), filter='{Filter}'");
        }

        void Discover()
        {
            var baseT = typeof(GameTest);
            foreach (var t in baseT.Assembly.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract || !baseT.IsAssignableFrom(t)) continue;
                GameTest inst;
                try { inst = (GameTest)Activator.CreateInstance(t); }
                catch (Exception e) { GD.PrintErr($"[L1] cannot construct {t.Name}: {e.Message}"); continue; }
                if (!GlobMatchAny(Filter, inst.Name)) continue;
                // UG_L1_SKIP: exclude tests matching a glob. Hunting order-dependence means asking "does X still
                // fail if Y never ran", and there is no way to say that with an include-filter alone.
                var _skip = System.Environment.GetEnvironmentVariable("UG_L1_SKIP");
                if (!string.IsNullOrEmpty(_skip) && GlobMatchAny(_skip, inst.Name)) continue;
                _tests.Add(inst);
            }
            _tests.Sort((a, b) => a.Tier != b.Tier ? a.Tier - b.Tier : string.CompareOrdinal(a.Name, b.Name));
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_cooldown > 0) { _cooldown--; return; }        // let the previous sandbox's QueueFree flush through the groups
            if (_cur == null) { if (!StartNext()) return; }    // StartNext false => all done + quit already issued
            // ...and StartNext returning TRUE does not mean a test is now running: it pulls the first step, so
            // a body that reaches its end without a pending yield finishes inside it and clears _cur. Falling
            // through then dereferenced null and threw, once per such test -- 31 NullReferenceExceptions in a
            // 201-test run, all logged, none failing anything. Harmless on its own; corrosive because grepping
            // l1.log for NREs is how a REAL one gets noticed, and 31 of these drown it.
            if (_cur == null) return;

            _testSim += delta;
            if (_testSim > _cur.TimeoutSimSeconds) { _ctx.Fail($"TIMEOUT after {_testSim:0.0}s sim (watchdog)"); FinishTest(); return; }

            switch (_step.Kind)
            {
                case Step.Mode.Ticks:
                    if (--_ticksLeft <= 0) AdvanceStep();
                    break;
                case Step.Mode.Until:
                    _untilElapsed += delta;
                    bool ok;
                    try { ok = _step.Cond(); }
                    catch (Exception e) { _ctx.Fail($"EXCEPTION in Until predicate: {e.Message}"); FinishTest(); return; }
                    if (ok) AdvanceStep();
                    else if (_untilElapsed >= _step.MaxSimSeconds) { _ctx.Fail($"UNTIL timed out ({_step.MaxSimSeconds:0.#}s): condition never held"); FinishTest(); }
                    break;
            }
        }

        bool StartNext()
        {
            _idx++;
            if (_idx >= _tests.Count) { Summarize(); return false; }
            _cur = _tests[_idx];
            _ctx = new TestContext { Rng = SeededRng(_cur.Name) };
            _sandbox = new Node3D { Name = $"Sandbox_{_cur.Name}" };
            AddChild(_sandbox);
            _cur.World = _sandbox; _cur.T = _ctx;
            _testSim = 0; _untilElapsed = 0; _sw.Restart();
            try { _co = _cur.Run().GetEnumerator(); }
            catch (Exception e) { _ctx.Fail($"EXCEPTION building test: {e.Message}"); FinishTest(); return true; }
            AdvanceStep();   // pull the first step (runs the test body up to its first yield)
            return true;
        }

        void AdvanceStep()
        {
            bool has;
            try { has = _co.MoveNext(); }
            catch (Exception e) { _ctx.Fail($"EXCEPTION: {e.Message}"); FinishTest(); return; }
            if (!has) { FinishTest(); return; }   // coroutine ran to completion
            _step = _co.Current;
            _ticksLeft = _step.N; _untilElapsed = 0;
        }

        void FinishTest()
        {
            // A test that paused the tree and did not restore it would leave EVERY later test running frozen, which
            // surfaces as a cascade of unrelated timeouts. Cheap to make impossible; expensive to debug.
            if (GetTree().Paused) GetTree().Paused = false;
            double secs = _sw.Elapsed.TotalSeconds;
            bool failed = _ctx.Failed;
            if (failed)
            {
                _failed++;
                GD.Print($"[TEST] {_cur.Name,-42} | FAIL | {_ctx.FirstFailure} ({secs:0.00}s)");
                foreach (var (desc, ok) in _ctx.Checks) if (!ok) GD.Print($"         ✗ {desc}");
                GD.Print($"         repro: ./test.sh --l1 --only {_cur.Name}   (seed {_ctx.Rng.Seed})");
            }
            else { _passed++; GD.Print($"[TEST] {_cur.Name,-42} | PASS | {secs:0.00}s ({_ctx.Checks.Count} checks)"); }

            _sandbox?.QueueFree();
            _sandbox = null; _cur = null; _co = null;
            ResetGlobals();
            _cooldown = 2;   // 2 ticks so QueueFree flushes + the "deployables"/"wires"/"powermgr" groups empty before the next test
        }

        static void ResetGlobals()
        {
            PowerNet.ResetForTests();
            PlayerRegistry.ResetForTests();   // _ExitTree self-cleans, this is belt-and-braces vs a leaked node
            LootTables.ResetForTests();       // a loot-injection test must not leak its table into the next test's rolls
            WorldItem.NoDropRotation = false;
            WorldItem.SuppressLocalVisual = false;   // P2b: leaked global under --spconsume tests -> reset between tests
            BugReporter.EnterTestMode();   // re-assert every test: bugreport.* deliberately toggles MicEnabled, and if it
                                           // dies on a failed check or a watchdog timeout its own restore never runs
            Engine.TimeScale = 1.0;
        }

        void Summarize()
        {
            double secs = (Time.GetTicksMsec() - _t0) / 1000.0;
            GD.Print($"[L1] passed={_passed} failed={_failed} duration={secs:0.0}s");
            GetTree().Quit(_failed == 0 ? 0 : 1);
        }

        // deterministic per-test seed (string.GetHashCode is process-randomized -> use a stable FNV-1a); UG_SEED overrides
        static RandomNumberGenerator SeededRng(string name)
        {
            var rng = new RandomNumberGenerator();
            var ov = System.Environment.GetEnvironmentVariable("UG_SEED");
            if (ov != null && ulong.TryParse(ov, out var s)) { rng.Seed = s; return rng; }
            ulong h = 1469598103934665603UL;
            foreach (char c in name) { h ^= c; h *= 1099511628211UL; }
            rng.Seed = h;
            return rng;
        }

        // minimal glob: '*' matches any run of chars; everything else literal (case-sensitive dotted names)
        /// <summary>Filter accepting a COMMA-SEPARATED list of globs, matching if any does. test.sh's own header
        /// warns that --only takes one glob and that an alternation like `player.lean|tv.*` silently matches
        /// NOTHING -- which reads as a clean run. Finding an order-dependent failure needs "this whole family AND
        /// that one test" in a single boot, which a single glob cannot express; the console-container leak below
        /// was bisected with exactly this.</summary>
        static bool GlobMatchAny(string pats, string s)
        {
            if (string.IsNullOrEmpty(pats) || pats == "*") return true;
            foreach (var p in pats.Split(','))
                if (GlobMatch(p.Trim(), s)) return true;
            return false;
        }

        static bool GlobMatch(string pat, string s)
        {
            if (string.IsNullOrEmpty(pat) || pat == "*") return true;
            int pi = 0, si = 0, star = -1, mark = 0;
            while (si < s.Length)
            {
                if (pi < pat.Length && (pat[pi] == s[si])) { pi++; si++; }
                else if (pi < pat.Length && pat[pi] == '*') { star = pi++; mark = si; }
                else if (star != -1) { pi = star + 1; si = ++mark; }
                else return false;
            }
            while (pi < pat.Length && pat[pi] == '*') pi++;
            return pi == pat.Length;
        }
    }
}
