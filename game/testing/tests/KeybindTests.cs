using Godot;
using System.Collections.Generic;
using UnturnedGodot.Testing;

namespace UnturnedGodot.Testing.Tests
{
    /// <summary>The tests game/Keybinds.cs has claimed since it was written.
    ///
    /// Two comments in that file (on the GameAction enum, and above Defaults) both assert "KeybindTests fails
    /// if a member has no default". No such file existed -- the enum grew from 28 members to 36 under a
    /// guarantee that nothing enforced. This is that file. If you are adding a GameAction, the first check
    /// below is the one that will catch you.</summary>
    public class KeybindDefaultsComplete : GameTest
    {
        public override string Name => "keybind.defaults_complete";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            // DELIBERATELY UNBOUND actions. An action can ship with no key on purpose -- Grenade was unbound on
            // 2026-08-24 (strawberry) to give H back to the camera toggle -- and it must stay REACHABLE in the
            // rebind menu, which is why the action still exists rather than being deleted.
            //
            // This list is the point of the exercise: the original rule was "every action is bound", which is a
            // fine rule right up until an intentional unbind, at which point it fails for a correct change and
            // says nothing about the case it was written to catch (an action somebody forgot to give a key).
            // Naming the exceptions keeps the real check -- a NEW action with no default still fails here --
            // while letting a deliberate one through. An entry that stops being unbound also fails, so this
            // cannot rot into a blanket exemption.
            var intentionallyUnbound = new HashSet<GameAction> { GameAction.Grenade };

            var missing = new List<string>();
            var unbound = new List<string>();
            var wronglyListed = new List<string>();
            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                bool exempt = intentionallyUnbound.Contains(a);
                var d = Keybinds.Default(a);
                if (!d.IsBound && !exempt) missing.Add(a.ToString());
                if (!Keybinds.Get(a).IsBound && !exempt) unbound.Add(a.ToString());
                if (exempt && d.IsBound) wronglyListed.Add(a.ToString());
            }
            T.Check($"every GameAction has a default (missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))})",
                    missing.Count == 0);
            // Separate check on purpose: Default() reads the table, Get() goes through the load path. An action
            // can have a default and still come back unbound if Load mis-restores it.
            T.Check($"every GameAction resolves to a BOUND control (unbound: {(unbound.Count == 0 ? "none" : string.Join(", ", unbound))})",
                    unbound.Count == 0);
            // The exemption list has to stay honest in BOTH directions, or it quietly becomes a place where
            // actions go to stop being checked.
            T.Check($"the intentionally-unbound list still matches reality (now bound: {(wronglyListed.Count == 0 ? "none" : string.Join(", ", wronglyListed))})",
                    wronglyListed.Count == 0);

            // A default the rebind UI would REFUSE is a shipped conflict: the player cannot reproduce it, and
            // cannot restore it once they move off it.
            //
            // Ask ConflictWith rather than comparing Serialize() strings. Those two are not the same question,
            // and this test learned it the hard way -- a raw string compare flagged VehicleHandbrake and Jump
            // both on Space, which is DELIBERATE and legal: they live in different BindContexts and can never
            // fire in the same frame (the on-foot poll sits behind the `_driving != null` early return). The
            // check has to mirror the rule the product actually enforces, not restate a stricter one I assumed.
            var dupes = new List<string>();
            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                var clash = Keybinds.ConflictWith(Keybinds.Default(a), a);
                if (clash.HasValue) dupes.Add($"{a} vs {clash.Value} on {Keybinds.Default(a).Label}");
            }
            T.Check($"no shipped default is one the rebind UI would refuse ({(dupes.Count == 0 ? "none" : string.Join("; ", dupes))})",
                    dupes.Count == 0);
            yield break;
        }
    }

    /// <summary>The harness must not read, and must not WRITE, the real user://keybinds.cfg.
    ///
    /// This is the defect this file was written for. `Set()` called `Save()` unconditionally, so any test that
    /// rebound anything permanently overwrote the config of whoever ran the suite; and `ResetForTests()` set
    /// `_loaded = false`, which made the next `Get()` re-read that same real file -- the opposite of the
    /// "known state" its own comment promised.
    ///
    /// TEETH: the mtime check below FAILS against the old implementation, because Save() would create or
    /// rewrite the file. It is not a check whose pass looks like its failure.</summary>
    public class KeybindTestModeIsolation : GameTest
    {
        public override string Name => "keybind.testmode_isolation";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            const string cfg = "user://keybinds.cfg";
            bool existedBefore = FileAccess.FileExists(cfg);
            ulong mtimeBefore = existedBefore ? FileAccess.GetModifiedTime(cfg) : 0;

            // TestHost.ResetGlobals() has already called ResetForTests(). Assert it actually landed defaults
            // rather than whatever this developer has bound.
            var mismatched = new List<string>();
            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                var got = Keybinds.Get(a); var def = Keybinds.Default(a);
                if (got.Key != def.Key || got.Mouse != def.Mouse) mismatched.Add($"{a} is {got.Label}, default {def.Label}");
            }
            T.Check($"under test mode every action reads its DEFAULT, not the dev's config ({(mismatched.Count == 0 ? "clean" : string.Join("; ", mismatched))})",
                    mismatched.Count == 0);

            // Now rebind something and prove it did not reach the disk.
            Keybinds.Set(GameAction.Jump, new Bind(Key.F13));
            T.Check("Set() still updates the in-memory table", Keybinds.Get(GameAction.Jump).Key == Key.F13);

            bool existsAfter = FileAccess.FileExists(cfg);
            T.Check("Set() did not CREATE the real user://keybinds.cfg",
                    existedBefore || !existsAfter);
            if (existedBefore && existsAfter)
                T.Check("Set() did not REWRITE the real user://keybinds.cfg",
                        FileAccess.GetModifiedTime(cfg) == mtimeBefore);

            // Leave the table as we found it for the next test in the shared boot.
            Keybinds.ResetForTests();
            T.Check("ResetForTests() restores the default", Keybinds.Get(GameAction.Jump).Key == Keybinds.Default(GameAction.Jump).Key);
            yield break;
        }
    }

    /// <summary>Serialize/Parse is the persistence contract. A binding that does not survive the round trip is
    /// a setting the player sets once and loses on the next launch.</summary>
    public class KeybindSerializeRoundTrip : GameTest
    {
        public override string Name => "keybind.serialize_roundtrip";
        public override int Tier => 0;

        static bool Same(Bind a, Bind b) => a.Key == b.Key && a.Mouse == b.Mouse;

        public override IEnumerable<Step> Run()
        {
            var keys = new[] { Key.W, Key.Space, Key.Shift, Key.Tab, Key.Quoteleft, Key.Backslash, Key.F13, Key.Key1 };
            var bad = new List<string>();
            foreach (var k in keys)
            {
                var b = new Bind(k);
                if (!Same(b, Bind.Parse(b.Serialize()))) bad.Add(k.ToString());
            }
            T.Check($"every keyboard bind round-trips ({(bad.Count == 0 ? "ok" : string.Join(", ", bad))})", bad.Count == 0);

            var mice = new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle,
                               MouseButton.Xbutton1, MouseButton.Xbutton2, MouseButton.WheelUp, MouseButton.WheelDown };
            bad.Clear();
            foreach (var m in mice)
            {
                var b = new Bind(m);
                if (!Same(b, Bind.Parse(b.Serialize()))) bad.Add(m.ToString());
            }
            T.Check($"every mouse bind round-trips ({(bad.Count == 0 ? "ok" : string.Join(", ", bad))})", bad.Count == 0);

            // Every SHIPPED default, which is the set that actually matters.
            bad.Clear();
            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                var d = Keybinds.Default(a);
                if (!Same(d, Bind.Parse(d.Serialize()))) bad.Add(a.ToString());
            }
            T.Check($"every shipped default round-trips ({(bad.Count == 0 ? "ok" : string.Join(", ", bad))})", bad.Count == 0);

            // A corrupt entry must fall back to the default, which means Parse has to return UNBOUND -- Load's
            // guard is `if (b.IsBound)`. "x:65" used to parse as Key.A because any non-'m' tag fell through to
            // the key branch, and "k:-1" used to install a control that can never fire and cannot be reset.
            foreach (var junk in new[] { "", "k", "k:", "x:65", "q:1", "k:-1", "m:-1", "k:0", "m:0", "k:abc", "k99", "k:999999999" })
                T.Check($"corrupt entry \"{junk}\" parses as UNBOUND so Load falls back to the default",
                        !Bind.Parse(junk).IsBound);
            yield break;
        }
    }

    /// <summary>An UNBOUND bind must be inert, not a wildcard.
    ///
    /// `Matches` compared `k.PhysicalKeycode == Key` with no guard, so an unbound bind (Key.None == 0) matched
    /// any InputEventKey whose PhysicalKeycode was unset -- which is exactly what a synthetic or IME-sourced
    /// event looks like. `Pressed` had the guard; `Matches` did not, and the two disagreed about what unbound
    /// means. Same mechanism that made three L1 tests fail when the game migrated to PhysicalKeycode.</summary>
    public class KeybindUnboundIsInert : GameTest
    {
        public override string Name => "keybind.unbound_is_inert";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            var unbound = default(Bind);
            T.Check("a default(Bind) reports itself unbound", !unbound.IsBound);

            // The exact shape that used to slip through: Keycode set, PhysicalKeycode left at 0.
            var keycodeOnly = new InputEventKey { Pressed = true, Keycode = Key.H };
            T.Check("an unbound bind does NOT match an event with an unset PhysicalKeycode",
                    !unbound.Matches(keycodeOnly));

            var physical = new InputEventKey { Pressed = true, PhysicalKeycode = Key.H };
            T.Check("an unbound bind does NOT match a properly-formed event either", !unbound.Matches(physical));
            T.Check("a bound bind DOES match its own physical key", new Bind(Key.H).Matches(physical));
            // CONTROL: the positive case must fail on the keycode-only event, or the check above proves nothing
            // about the guard -- it would just be proving that nothing matches anything.
            T.Check("a bound bind does NOT match a keycode-only event (this is why the 3 L1 sites broke)",
                    !new Bind(Key.H).Matches(keycodeOnly));

            var mouseUnbound = new Bind(MouseButton.None);
            T.Check("an unbound MOUSE bind is inert too",
                    !mouseUnbound.Matches(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left }));
            yield break;
        }
    }

    /// <summary>ConflictWith is what stops the rebind UI creating a control that does two things at once.</summary>
    public class KeybindConflictDetection : GameTest
    {
        public override string Name => "keybind.conflict_detection";
        public override int Tier => 0;

        public override IEnumerable<Step> Run()
        {
            var jump = Keybinds.Default(GameAction.Jump);
            T.Check("a control already in use is reported against the action holding it",
                    Keybinds.ConflictWith(jump, GameAction.Prone) == GameAction.Jump);
            T.Check("an action never conflicts with itself",
                    Keybinds.ConflictWith(jump, GameAction.Jump) == null);
            T.Check("a free control reports no conflict",
                    Keybinds.ConflictWith(new Bind(Key.F13), GameAction.Jump) == null);
            T.Check("an UNBOUND control reports no conflict (it is not a control)",
                    Keybinds.ConflictWith(default, GameAction.Jump) == null);
            T.Check("a mouse bind does not collide with a keyboard bind of the same numeric value",
                    Keybinds.ConflictWith(new Bind(MouseButton.Xbutton2), GameAction.Jump) == null);
            yield break;
        }
    }
}
