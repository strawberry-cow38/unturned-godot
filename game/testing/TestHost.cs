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
                if (GlobMatch(Filter, inst.Name)) _tests.Add(inst);
            }
            _tests.Sort((a, b) => a.Tier != b.Tier ? a.Tier - b.Tier : string.CompareOrdinal(a.Name, b.Name));
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_cooldown > 0) { _cooldown--; return; }        // let the previous sandbox's QueueFree flush through the groups
            if (_cur == null) { if (!StartNext()) return; }    // StartNext false => all done + quit already issued

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
            WorldItem.NoDropRotation = false;
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
