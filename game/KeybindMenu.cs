using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The rebind screen: one row per action, click to capture the next control pressed.
    ///
    /// CAPTURE, not a dropdown. A list of every key on a keyboard plus every mouse button is unusable and
    /// gets out of date the moment someone plugs in a mouse with more buttons; "press the thing you want"
    /// is both smaller code and the interaction people already expect.
    ///
    /// While capturing, this node takes input at the highest priority and marks it handled, so binding a key
    /// cannot also trigger whatever that key currently does -- rebinding Jump should not make you jump.</summary>
    public partial class KeybindMenu : MarginContainer
    {
        readonly Dictionary<GameAction, Button> _rows = new();
        Label _hint;
        GameAction? _capturing;

        public override void _Ready()
        {
            // No own panel/Back button: this is embedded as the "Key Binds" TAB of the Settings screen, which
            // provides the frame and the single Back (strawberry). Nesting a panel inside a panel would double the
            // border; the standalone Back would be a second way out of one tab.
            ProcessMode = ProcessModeEnum.Always;   // reachable from the pause menu, which pauses the tree

            var outer = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
            outer.AddThemeConstantOverride("separation", UITheme.Gap);
            AddChild(outer);

            outer.AddChild(UITheme.Label(new Label { Text = "CONTROLS" }, UITheme.FontTitle));
            _hint = UITheme.Label(new Label { Text = "click a binding, then press any key or mouse button  ·  Esc cancels" },
                                  UITheme.FontLabel, UITheme.TextDim);
            outer.AddChild(_hint);

            var vehNote = UITheme.Label(new Label { Text = "Vehicles use your Move Forward/Back/Left/Right binds for throttle & steering — rebinding movement moves them too. Handbrake is bindable on its own, below." },
                                        UITheme.FontLabel, UITheme.TextDim);
            vehNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            outer.AddChild(vehNote);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 420), SizeFlagsVertical = SizeFlags.ExpandFill };
            outer.AddChild(scroll);
            var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(list);

            foreach (GameAction a in System.Enum.GetValues(typeof(GameAction)))
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", UITheme.Gap);
                var ctx = Keybinds.ContextLabel(a);
                var name = UITheme.Label(new Label { Text = ctx == "" ? Keybinds.DisplayName(a) : $"{Keybinds.DisplayName(a)}  ·  {ctx}" }, UITheme.FontBody);
                name.CustomMinimumSize = new Vector2(240, 30);
                row.AddChild(name);

                var btn = new Button { Text = Keybinds.Get(a).Label, CustomMinimumSize = new Vector2(180, 30) };
                UITheme.Button(btn);
                var captured = a;                       // capture per-iteration, not the loop variable
                btn.Pressed += () => BeginCapture(captured);
                _rows[a] = btn;
                row.AddChild(btn);
                list.AddChild(row);
            }

            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", UITheme.Gap);
            var reset = new Button { Text = "Reset to defaults", CustomMinimumSize = new Vector2(180, 36) };
            UITheme.Button(reset);
            reset.Pressed += () => { Keybinds.ResetAll(); RefreshAll(); };
            footer.AddChild(reset);
            outer.AddChild(footer);
        }

        void BeginCapture(GameAction a)
        {
            _capturing = a;
            _rows[a].Text = "press…";
            _hint.Text = $"press a key or mouse button for {Keybinds.DisplayName(a)}  ·  Esc cancels";
            UITheme.Label(_hint, UITheme.FontLabel, UITheme.Accent);
        }

        void RefreshAll()
        {
            foreach (var kv in _rows) kv.Value.Text = Keybinds.Get(kv.Key).Label;
        }

        public override void _Input(InputEvent e)
        {
            if (_capturing is not GameAction target) return;

            Bind bind;
            if (e is InputEventKey k && k.Pressed && !k.Echo)
            {
                // Esc cancels rather than binding. Binding Esc would be legal and is a trap: it is the only
                // way out of most menus, so a player who bound it would have no way to unbind it.
                if (k.PhysicalKeycode == Key.Escape) { Cancel(); GetViewport().SetInputAsHandled(); return; }
                bind = new Bind(k.PhysicalKeycode);
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                bind = new Bind(mb.ButtonIndex);
            }
            else return;

            var clash = Keybinds.ConflictWith(bind, target);
            if (clash.HasValue)
            {
                // REFUSE rather than silently steal it. Both alternatives are worse: double-binding makes
                // one control do two things with nothing on screen explaining it, and auto-unbinding the
                // other action leaves the player with a control that stopped working and no idea why.
                _rows[target].Text = Keybinds.Get(target).Label;
                UITheme.Label(_hint, UITheme.FontLabel, UITheme.Bad);
                _hint.Text = $"{bind.Label} is already bound to {Keybinds.DisplayName(clash.Value)} — free it first";
                _capturing = null;
                GetViewport().SetInputAsHandled();
                return;
            }

            Keybinds.Set(target, bind);
            _capturing = null;
            RefreshAll();
            UITheme.Label(_hint, UITheme.FontLabel, UITheme.Good);
            _hint.Text = $"{Keybinds.DisplayName(target)} → {bind.Label}";
            // Swallow it: the key that was just bound must not also fire the action it was bound to.
            GetViewport().SetInputAsHandled();
        }

        void Cancel()
        {
            if (_capturing is GameAction a) _rows[a].Text = Keybinds.Get(a).Label;
            _capturing = null;
            UITheme.Label(_hint, UITheme.FontLabel, UITheme.TextDim);
            _hint.Text = "click a binding, then press any key or mouse button  ·  Esc cancels";
        }
    }
}
