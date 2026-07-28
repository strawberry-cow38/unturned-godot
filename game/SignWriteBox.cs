using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// The in-game box for writing on a sign. Deliberately a real UI rather than a console command:
    /// a feature only reachable from a debug console is not reachable by a player, and this codebase
    /// has already been bitten by shipping systems nobody could actually get at.
    ///
    /// Opening it releases the mouse and stops the player acting, so typing "w" writes a letter rather
    /// than walking into the sign.
    /// </summary>
    public partial class SignWriteBox : Control
    {
        public static SignWriteBox Instance;

        LineEdit _edit;
        Label _hint;
        Sign _target;

        /// <summary>Raised with the text the player committed. The caller decides whether that means
        /// "set it locally" (singleplayer) or "ask the server" (multiplayer) -- this widget does not
        /// know which world it is in.</summary>
        public event System.Action<Sign, string> Submitted;

        public bool IsOpen => Visible;

        public override void _Ready()
        {
            Instance = this;
            Visible = false;
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Stop;

            var panel = new PanelContainer
            {
                AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
                OffsetLeft = -230, OffsetRight = 230, OffsetTop = -70, OffsetBottom = 70,
            };
            AddChild(panel);

            var box = new VBoxContainer();
            panel.AddChild(box);
            box.AddChild(new Label { Text = "Write on the sign" });

            _edit = new LineEdit
            {
                // Capped at the same number the rules cap at, so the box cannot accept text the server
                // will silently trim. The rule still runs on store -- this is a courtesy, not the gate.
                MaxLength = SignText.MaxChars,
                CustomMinimumSize = new Vector2(440, 0),
                PlaceholderText = "...",
            };
            _edit.TextSubmitted += OnSubmit;
            box.AddChild(_edit);

            _hint = new Label { Text = "Enter to save   Esc to cancel", Modulate = new Color(1, 1, 1, 0.55f) };
            box.AddChild(_hint);
        }

        public void Open(Sign sign)
        {
            if (sign == null || !IsInstanceValid(sign)) return;
            _target = sign;
            _edit.Text = sign.Text;
            Visible = true;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _edit.GrabFocus();
            _edit.CaretColumn = _edit.Text.Length;
        }

        public void Close()
        {
            Visible = false;
            _target = null;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        void OnSubmit(string text)
        {
            var sign = _target;
            Close();
            if (sign != null && IsInstanceValid(sign)) Submitted?.Invoke(sign, text);
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (!Visible) return;
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }
    }
}
