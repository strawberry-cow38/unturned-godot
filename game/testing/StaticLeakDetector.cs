using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnturnedGodot.Testing
{
    /// <summary>
    /// UG_LEAKSCAN=1: name the process-globals that survive ResetGlobals, so an order-dependent test can be
    /// diagnosed mechanically instead of bisected.
    ///
    /// WHY THIS EXISTS. Three tests were found order-dependent in one day -- vehicle.trailers,
    /// tank.differential_steer, inv.hold_transfer -- all with the identical signature: GREEN alone at the very
    /// seed they fail at in-suite, so the rng is controlled and the only variable is what ran before them.
    /// That is a leaked static. Bisecting costs about an hour EACH and finds one instance; the suite is
    /// deterministic and single-process, which is exactly the condition that makes the CLASS findable by
    /// machine. (cow tools proposed the approach, 2026-09-16.)
    ///
    /// ⚠ THE SNAPSHOT IS TAKEN AFTER ResetGlobals, NOT AFTER THE TEST BODY, and that is the whole design.
    /// "Did this test change a static" is a question with hundreds of boring yes answers -- caches fill,
    /// counters count, lazy tables build. The question worth asking is "did a static survive the reset that
    /// is supposed to clear it", which is a much smaller set and is precisely the list of things that should
    /// have been in ResetGlobals and are not.
    ///
    /// It is still a TRIAGE LIST, not a verdict: a legitimately-lazy cache will show up every time and belongs
    /// on the ignore list below rather than in ResetGlobals. Read it as "these are the candidates", and take
    /// the one whose setter runs alphabetically before every affected test.
    /// </summary>
    public static class StaticLeakDetector
    {
        public static bool Enabled => System.Environment.GetEnvironmentVariable("UG_LEAKSCAN") == "1";

        // Types whose statics change legitimately and are not leaks. Kept SHORT and justified on purpose --
        // a long ignore list is how a detector stops detecting. Add only with a reason.
        static readonly HashSet<string> IgnoredTypes = new()
        {
            "UnturnedGodot.Testing.StaticLeakDetector",   // us
            "UnturnedGodot.Testing.TestHost",             // the harness's own counters ARE the run
        };

        // Field names that are caches/counters by contract. Same rule: each one earns its place.
        static readonly HashSet<string> IgnoredFieldSuffixes = new() { "Cache", "_cache", "Count", "_count", "Pool", "_pool" };

        readonly struct Slot
        {
            public readonly FieldInfo Field; public readonly string Owner;
            public Slot(FieldInfo f, string owner) { Field = f; Owner = owner; }
        }

        static List<Slot> _slots;
        static Dictionary<string, string> _baseline;      // "Type.Field" -> rendered value after the previous reset
        static readonly Dictionary<string, List<string>> _suspects = new();   // "Type.Field" -> tests after which it moved

        static void Discover()
        {
            if (_slots != null) return;
            _slots = new List<Slot>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name ?? "";
                // Only OUR code. Scanning Godot/System statics is noise we can neither fix nor interpret.
                if (!an.StartsWith("UnturnedGodot") && !an.StartsWith("SDG") && !an.StartsWith("Unturned")) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = Array.Empty<Type>(); GD.Print($"[leakscan] skipped {an}: {e.Message}"); }
                foreach (var t in types)
                {
                    if (t.FullName == null || IgnoredTypes.Contains(t.FullName)) continue;
                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var f in fields)
                    {
                        if (f.IsLiteral) continue;                 // const: cannot change
                        if (f.IsInitOnly && !IsMutableRef(f)) continue;   // readonly VALUE cannot change; readonly COLLECTION can
                        bool ignored = false;
                        foreach (var sfx in IgnoredFieldSuffixes) if (f.Name.EndsWith(sfx)) { ignored = true; break; }
                        if (ignored) continue;
                        _slots.Add(new Slot(f, t.FullName));
                    }
                }
            }
            GD.Print($"[leakscan] watching {_slots.Count} static field(s) across our assemblies");
        }

        // A `static readonly List<T>` is the classic leak: the REFERENCE never changes, the CONTENTS do, and a
        // reference comparison would call it clean forever.
        static bool IsMutableRef(FieldInfo f) =>
            !f.FieldType.IsValueType && f.FieldType != typeof(string);

        static string Render(object v)
        {
            if (v == null) return "<null>";
            switch (v)
            {
                case string s: return "\"" + (s.Length > 40 ? s.Substring(0, 40) + "…" : s) + "\"";
                case System.Collections.ICollection c: return $"{v.GetType().Name}[{c.Count}]";   // count, so contents changing is visible
                default: break;
            }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum) return v.ToString();
            return t.Name;   // opaque object: identity changes are what we can see, and that is enough for triage
        }

        static Dictionary<string, string> Snapshot()
        {
            var d = new Dictionary<string, string>(_slots.Count);
            foreach (var s in _slots)
            {
                string key = s.Owner + "." + s.Field.Name;
                string val;
                try { val = Render(s.Field.GetValue(null)); }
                catch (Exception e) { val = "<threw:" + e.GetType().Name + ">"; }   // a throwing getter must not kill the run
                d[key] = val;
            }
            return d;
        }

        /// <summary>Call AFTER ResetGlobals for the test that just finished. Anything that moved survived the reset.</summary>
        public static void AfterReset(string testName)
        {
            if (!Enabled) return;
            Discover();
            var now = Snapshot();
            if (_baseline != null)
            {
                foreach (var kv in now)
                {
                    if (!_baseline.TryGetValue(kv.Key, out var was) || was == kv.Value) continue;
                    if (!_suspects.TryGetValue(kv.Key, out var list)) _suspects[kv.Key] = list = new List<string>();
                    if (list.Count < 6) list.Add($"{testName}: {was} -> {kv.Value}");
                }
            }
            _baseline = now;
        }

        /// <summary>Print the triage list. Ordered by how many tests moved each field -- the widest blast radius first.</summary>
        public static void Report()
        {
            if (!Enabled) return;
            if (_suspects.Count == 0) { GD.Print("[leakscan] no static survived a reset having changed -- nothing to triage"); return; }
            var keys = new List<string>(_suspects.Keys);
            keys.Sort((a, b) => _suspects[b].Count.CompareTo(_suspects[a].Count));
            GD.Print($"[leakscan] {keys.Count} field(s) changed and were NOT put back by ResetGlobals -- TRIAGE LIST, not a verdict:");
            foreach (var k in keys)
            {
                var l = _suspects[k];
                GD.Print($"[leakscan]   {k}  (moved after {l.Count} test(s))");
                foreach (var line in l) GD.Print($"[leakscan]       {line}");
            }
            GD.Print("[leakscan] the one you want is set by a test that sorts BEFORE every affected test; caches belong in the ignore list, not in ResetGlobals.");
        }
    }
}
