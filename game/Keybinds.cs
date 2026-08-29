using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Every rebindable action in the game.
    ///
    /// The enum is the contract: adding a member without adding a default in Defaults below is a compile-time
    /// omission you will not notice, so KeybindTests asserts every member has one. Names are stable -- they are
    /// what gets written to user://keybinds.cfg, so RENAMING one silently orphans that user's binding and
    /// resets it to default. Add, don't rename.</summary>
    public enum GameAction
    {
        MoveForward, MoveBack, MoveLeft, MoveRight,
        Jump, Sprint, Crouch, CrouchToggle, Prone, LeanLeft, LeanRight,
        Fire, Aim, Reload, Firemode, Melee, Grenade, Interact, AttachMenu, ToggleFirstPerson, Flashlight,
        Inventory, Map, Craft, Skills, Console,
        Hotbar1, Hotbar2, Hotbar3, Hotbar4, Hotbar5, Hotbar6, Hotbar7, Hotbar8, Hotbar9,
        VehicleHandbrake,
        BugReport,
    }

    /// <summary>When an action's control is live. Two actions in DIFFERENT non-Anywhere contexts never fire in the
    /// same frame (the on-foot movement poll is skipped while driving), so they may legally share a control -- Jump
    /// and VehicleHandbrake both default to Space. Anywhere (default for anything unlisted) is the SAFE default: a
    /// wrongly-exclusive action lets a real double-bind through silently, a wrongly-Anywhere one only over-reports a
    /// conflict the player can see and clear.
    ///
    /// ⚠ This tag is CONFLICT-CHECK METADATA ONLY -- it has no runtime consumer. What actually keeps Jump and
    /// VehicleHandbrake from both firing on Space is the `if (_driving != null) { ...; return; }` early-out in
    /// PlayerController._PhysicsProcess (on-foot polls are skipped while seated). Tagging a future pair
    /// OnFoot/Driving does NOT create that exclusivity; the frame-exclusive structure must already exist, or the
    /// conflict check will admit a bind that really can double-fire.</summary>
    public enum BindContext { OnFoot, Driving, Anywhere }

    /// <summary>One physical control: a keyboard key OR a mouse button.
    ///
    /// Mouse buttons are first-class rather than bolted on, because the request that prompted this was
    /// specifically "rebind the report hotkey to mouse 5". A binding system that only does keyboard would
    /// have answered the letter of the request and not the point of it.
    ///
    /// Keyboard bindings store the PHYSICAL keycode. A layout-dependent Keycode means a binding made on
    /// QWERTY lands on a different physical key on AZERTY, which is exactly backwards: the player bound a
    /// position under their finger, not a letter.</summary>
    public readonly struct Bind
    {
        public readonly Key Key;
        public readonly MouseButton Mouse;

        public Bind(Key key) { Key = key; Mouse = MouseButton.None; }
        public Bind(MouseButton mouse) { Key = Key.None; Mouse = mouse; }

        public bool IsMouse => Mouse != MouseButton.None;
        public bool IsBound => Key != Key.None || Mouse != MouseButton.None;

        public bool Pressed => IsMouse
            ? Input.IsMouseButtonPressed(Mouse)
            : Key != Key.None && Input.IsPhysicalKeyPressed(Key);

        /// <summary>Does this event refer to this control? Used for edge-triggered actions, where polling
        /// would double-fire across a frame.</summary>
        public bool Matches(InputEvent e) => IsMouse
            ? e is InputEventMouseButton mb && mb.ButtonIndex == Mouse
            // `Key != Key.None` is load-bearing, not defensive. Without it an UNBOUND bind degenerates to
            // `k.PhysicalKeycode == Key.None`, i.e. == 0 -- and a synthetic or IME-sourced InputEventKey that
            // sets only Keycode leaves PhysicalKeycode at 0, so the unbound action would swallow it. That makes
            // an unbound control a WILDCARD rather than inert. `Pressed` above already guards this; Matches
            // did not, and the two must agree about what "unbound" means.
            : Key != Key.None && e is InputEventKey k && k.PhysicalKeycode == Key;

        public string Label
        {
            get
            {
                if (IsMouse)
                    return Mouse switch
                    {
                        MouseButton.Left => "Mouse 1",
                        MouseButton.Right => "Mouse 2",
                        MouseButton.Middle => "Mouse 3",
                        MouseButton.Xbutton1 => "Mouse 4",
                        MouseButton.Xbutton2 => "Mouse 5",
                        MouseButton.WheelUp => "Wheel Up",
                        MouseButton.WheelDown => "Wheel Down",
                        _ => "Mouse " + (int)Mouse,
                    };
                if (Key == Key.None) return "—";
                // OS.GetKeycodeString on the PHYSICAL code gives the label for the key in that position on
                // the user's actual layout, which is what they should be reading back.
                string s = OS.GetKeycodeString(DisplayServer.KeyboardGetKeycodeFromPhysical(Key));
                return string.IsNullOrEmpty(s) ? Key.ToString() : s;
            }
        }

        // Serialised as "k:<int>" / "m:<int>" -- explicit about which space the number lives in, because a
        // bare integer would collide (Key.None is 0 and so is MouseButton.None).
        public string Serialize() => IsMouse ? $"m:{(int)Mouse}" : Key == Key.None ? "" : $"k:{(int)Key}";

        public static Bind Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return default;
            // Check the SEPARATOR and the tag, not just the number. Without this any first char that is not
            // 'm' fell through to the key branch, so a corrupt "x:65" silently became Key.A -- a plausible
            // binding rather than the fallback-to-default the caller in Load() promises.
            if (s[1] != ':' || (s[0] != 'k' && s[0] != 'm')) return default;
            // long, not int: Godot's Key and MouseButton are long-backed, and Enum.IsDefined throws rather
            // than returning false when the boxed value's type does not match the enum's underlying type.
            if (!long.TryParse(s[2..], out long v) || v <= 0) return default;   // v<=0: 0 is None, negatives are unreachable
            if (s[0] == 'm') return System.Enum.IsDefined(typeof(MouseButton), (long)(MouseButton)v) ? new Bind((MouseButton)v) : default;
            // An undefined Key value passes `IsBound` (it only tests != None) and would be STORED, giving a row
            // that reads as bound and an action that can never fire, with nothing offering a way out.
            return System.Enum.IsDefined(typeof(Key), (long)(Key)v) ? new Bind((Key)v) : default;
        }
    }

    /// <summary>The binding table: defaults, overrides, persistence, and the query the game asks.
    ///
    /// Static because input is global and threading a settings object through every consumer would be a
    /// worse cure than the disease. Loaded once at startup; the rebind UI writes through it.</summary>
    public static class Keybinds
    {
        const string ConfigPath = "user://keybinds.cfg";

        // The stock layout. Every GameAction needs an entry -- KeybindTests fails if one is missing, because
        // a forgotten default silently gives that action an unbound control and it simply stops working.
        static readonly Dictionary<GameAction, Bind> Defaults = new()
        {
            [GameAction.MoveForward] = new Bind(Key.W),
            [GameAction.MoveBack] = new Bind(Key.S),
            [GameAction.MoveLeft] = new Bind(Key.A),
            [GameAction.MoveRight] = new Bind(Key.D),
            [GameAction.Jump] = new Bind(Key.Space),
            [GameAction.Sprint] = new Bind(Key.Shift),
            [GameAction.Crouch] = new Bind(Key.C),              // hold-to-crouch (master); the stand<->crouch toggle is CrouchToggle
            [GameAction.CrouchToggle] = new Bind(Key.X),        // stand<->crouch TOGGLE (master) -- a separate control from hold-crouch, both bindable
            [GameAction.Prone] = new Bind(Key.Z),
            [GameAction.LeanLeft] = new Bind(Key.Q),
            [GameAction.LeanRight] = new Bind(Key.E),
            [GameAction.Fire] = new Bind(MouseButton.Left),
            [GameAction.Aim] = new Bind(MouseButton.Right),
            [GameAction.Reload] = new Bind(Key.R),
            [GameAction.Firemode] = new Bind(Key.V),
            [GameAction.Melee] = new Bind(Key.G),
            [GameAction.Grenade] = new Bind(Key.None),   // UNBOUND (strawberry 2026-08-24). Not deleted: the action still
                                                        // exists, so it appears in the rebind menu as "—" and anyone who
                                                        // wants it can put it back. Deleting the action would remove the
                                                        // ability to bind it at all, which is a different request.
            [GameAction.Interact] = new Bind(Key.F),
            [GameAction.AttachMenu] = new Bind(Key.T),          // hold to open the weapon-attachment menu (code reality; supersedes the guessed Inspect)
            [GameAction.ToggleFirstPerson] = new Bind(Key.H),   // BACK ON H (strawberry 2026-08-24: "H should be 3p by default").
                                                                // It had been moved to K precisely because H was double-booked with
                                                                // Grenade; unbinding Grenade above is what frees H to come back, so
                                                                // these two changes are one change and neither works alone.
            [GameAction.Flashlight] = new Bind(Key.B),          // held tactical light (source TACTICAL key) -- a GameAction so ConflictWith can SEE B, not a hidden literal in the chain
            [GameAction.Inventory] = new Bind(Key.Tab),
            [GameAction.Map] = new Bind(Key.M),
            [GameAction.Craft] = new Bind(Key.Y),
            [GameAction.Skills] = new Bind(Key.J),              // code opens the skills menu on J, not U
            [GameAction.Console] = new Bind(Key.Quoteleft),
            [GameAction.Hotbar1] = new Bind(Key.Key1),
            [GameAction.Hotbar2] = new Bind(Key.Key2),
            [GameAction.Hotbar3] = new Bind(Key.Key3),
            [GameAction.Hotbar4] = new Bind(Key.Key4),
            [GameAction.Hotbar5] = new Bind(Key.Key5),
            [GameAction.Hotbar6] = new Bind(Key.Key6),
            [GameAction.Hotbar7] = new Bind(Key.Key7),
            [GameAction.Hotbar8] = new Bind(Key.Key8),
            [GameAction.Hotbar9] = new Bind(Key.Key9),
            [GameAction.VehicleHandbrake] = new Bind(Key.Space), // defaults to Space like Jump but its OWN action -- rebinding Jump must not strand the handbrake
            [GameAction.BugReport] = new Bind(Key.Backslash),
        };

        // Only the context-EXCLUSIVE actions are listed; everything unlisted is Anywhere. The on-foot-only actions are
        // OnFoot (their poll is skipped while seated); the handbrake is Driving (only read while driving). Movement
        // (MoveForward/etc) is deliberately Anywhere -- vehicles reuse it for throttle/steer, so it IS live in both.
        static readonly Dictionary<GameAction, BindContext> Contexts = new()
        {
            [GameAction.Jump] = BindContext.OnFoot, [GameAction.Sprint] = BindContext.OnFoot,
            [GameAction.Crouch] = BindContext.OnFoot, [GameAction.CrouchToggle] = BindContext.OnFoot,
            [GameAction.Prone] = BindContext.OnFoot, [GameAction.LeanLeft] = BindContext.OnFoot,
            [GameAction.LeanRight] = BindContext.OnFoot,
            [GameAction.VehicleHandbrake] = BindContext.Driving,
        };

        public static BindContext Context(GameAction a) => Contexts.TryGetValue(a, out var c) ? c : BindContext.Anywhere;

        /// <summary>Short row suffix so a control shared across contexts (Space on Jump AND Handbrake) reads as
        /// intentional in the rebind list. Empty for Anywhere actions.</summary>
        public static string ContextLabel(GameAction a) => Context(a) switch
        {
            BindContext.OnFoot => "on foot",
            BindContext.Driving => "in vehicle",
            _ => "",
        };

        static readonly Dictionary<GameAction, Bind> Current = new();
        static bool _loaded;

        public static Bind Get(GameAction a)
        {
            if (!_loaded) Load();
            return Current.TryGetValue(a, out var b) ? b : Default(a);
        }

        public static Bind Default(GameAction a) => Defaults.TryGetValue(a, out var b) ? b : default;

        /// <summary>Is the control for this action held right now? The ordinary query.</summary>
        public static bool Pressed(GameAction a) => Get(a).Pressed;

        /// <summary>Does this event belong to this action? For edge-triggered handling in _Input.</summary>
        public static bool Matches(GameAction a, InputEvent e) => Get(a).Matches(e);

        /// <summary>Is this event a control-DOWN, key or mouse? Callers add Matches/Echo as needed.</summary>
        public static bool IsDown(InputEvent e) => e switch
        {
            InputEventKey k => k.Pressed,
            InputEventMouseButton m => m.Pressed,
            _ => false,
        };

        /// <summary>A fresh PRESS of action a's control -- key (ignoring auto-repeat) OR mouse. The edge query that
        /// works for BOTH binding types, so a key action bound to a mouse button (BugReport on Mouse 5) and Fire
        /// bound to a key both fire. Replaces the `is InputEventKey { Pressed: true }` pre-filters that made cross-type
        /// binds inert.</summary>
        public static bool JustPressed(GameAction a, InputEvent e) => Matches(a, e) && IsDown(e) && e is not InputEventKey { Echo: true };

        /// <summary>A RELEASE of action a's control (key-up or mouse-up).</summary>
        public static bool JustReleased(GameAction a, InputEvent e) => Matches(a, e) && !IsDown(e);

        /// <summary>Which hotbar slot (1..9) this event's control maps to, or null. Shared by the equip poll and the
        /// bind-item UI so both read the SAME key space (Hotbar1..Hotbar9 are consecutive in the enum).</summary>
        public static int? HotbarSlot(InputEvent e)
        {
            for (int h = 0; h < 9; h++)
                if (Matches(GameAction.Hotbar1 + h, e)) return h + 1;
            return null;
        }

        /// <summary>Which action, if any, is already using this control. The rebind UI needs this BEFORE it
        /// writes, because silently double-binding produces a control that does two things at once and
        /// leaves the player with no way to see why.</summary>
        public static GameAction? ConflictWith(Bind b, GameAction ignoring)
        {
            if (!b.IsBound) return null;
            var ignCtx = Context(ignoring);
            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                if (a == ignoring) continue;
                // Two actions in DIFFERENT non-Anywhere contexts (on-foot Jump vs in-vehicle Handbrake) never fire in
                // the same frame, so sharing a control is not a conflict. Anywhere on either side means it might.
                var aCtx = Context(a);
                if (aCtx != BindContext.Anywhere && ignCtx != BindContext.Anywhere && aCtx != ignCtx) continue;
                var cur = Get(a);
                if (cur.Key == b.Key && cur.Mouse == b.Mouse) return a;
            }
            return null;
        }

        public static void Set(GameAction a, Bind b)
        {
            if (!_loaded) Load();
            Current[a] = b;
            Save();
        }

        public static void ResetAll()
        {
            Current.Clear();
            foreach (var kv in Defaults) Current[kv.Key] = kv.Value;
            Save();
        }

        public static void Load()
        {
            _loaded = true;
            Current.Clear();
            foreach (var kv in Defaults) Current[kv.Key] = kv.Value;
            try
            {
                var cfg = new ConfigFile();
                if (cfg.Load(ConfigPath) != Error.Ok) return;
                foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
                {
                    var v = cfg.GetValue("binds", a.ToString(), "");
                    if (v.VariantType != Variant.Type.String) continue;
                    string s = (string)v;
                    if (string.IsNullOrEmpty(s)) continue;
                    var b = Bind.Parse(s);
                    // An unparseable or empty entry falls back to the default rather than leaving the action
                    // unbound: a corrupt config should cost you your CUSTOM binding, not the ability to walk.
                    if (b.IsBound) Current[a] = b;
                }
            }
            catch (System.Exception e) { GD.PushWarning($"[keybinds] bad config, using defaults: {e.Message}"); }
        }

        public static void Save()
        {
            // A test that rebinds anything must not reach the developer's real user://keybinds.cfg. This was
            // not hypothetical: Set() calls Save() unconditionally, so a single rebind assertion permanently
            // overwrote the config of whoever ran the suite.
            if (_testMode) return;
            try
            {
                var cfg = new ConfigFile();
                foreach (var kv in Current)
                    cfg.SetValue("binds", kv.Key.ToString(), kv.Value.Serialize());

                // Temp-and-rename, matching StructureManager's save (game/StructureManager.cs:569-575). A plain
                // cfg.Save(ConfigPath) truncates the file in place before a byte lands; a crash or power loss
                // mid-write leaves ConfigFile a partial document, and Load() (below) treats ANY parse error as
                // "no valid config" and falls back to pure defaults -- so a torn write costs the player all 36
                // customised bindings, not just the one they were making when the machine went down.
                string tmp = ConfigPath + ".tmp";
                var err = cfg.Save(tmp);
                if (err != Error.Ok) { GD.PushWarning($"[keybinds] save (tmp) failed: {err}"); return; }
                err = DirAccess.RenameAbsolute(ProjectSettings.GlobalizePath(tmp), ProjectSettings.GlobalizePath(ConfigPath));
                if (err != Error.Ok) GD.PushWarning($"[keybinds] save rename failed: {err}");
            }
            catch (System.Exception e) { GD.PushWarning($"[keybinds] could not save: {e.Message}"); }
        }

        /// <summary>Human label for the settings list. Splits the enum name on case so the UI does not need a
        /// parallel table that can drift out of step with the enum.</summary>
        public static string DisplayName(GameAction a)
        {
            string n = a.ToString();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsDigit(n[i - 1])) sb.Append(' ');
                sb.Append(n[i]);
            }
            return sb.ToString();
        }

        static bool _testMode;

        /// <summary>Test hook: put the table in a KNOWN state and stop it touching the developer's real config.
        ///
        /// The previous version cleared Current and set `_loaded = false`, which did the OPPOSITE of what its
        /// comment claimed: the next Get() saw !_loaded and called Load(), which read the real
        /// user://keybinds.cfg off disk. So a developer with a customised Jump key ran a different suite from
        /// CI, and the failure would look like a product bug rather than a harness one.
        ///
        /// Now: load the defaults directly, mark loaded so nothing re-reads disk, and latch _testMode so Save()
        /// is a no-op for the rest of the process.</summary>
        public static void ResetForTests()
        {
            _testMode = true;
            Current.Clear();
            foreach (var kv in Defaults) Current[kv.Key] = kv.Value;
            _loaded = true;
        }
    }
}
