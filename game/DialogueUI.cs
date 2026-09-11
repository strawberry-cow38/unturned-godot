using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The conversation panel (master 2026-09-11: "RPG style dialogue, multiple options").
    ///
    /// A VIEW, and nothing else. Every decision -- which message shows, which responses this player can see,
    /// what a choice grants -- belongs to PlayerController and DialogueRules; this draws what they say and
    /// hands clicks back. That split is why the trade window can open over the top without ending the
    /// conversation, and why closing the panel does not lose your place.
    ///
    /// PAGES FIRST, THEN OPTIONS. Retail messages carry Message_i_Pages and the responses are not offered
    /// until the speaker has finished: showing both at once lets you answer a question that has not been asked
    /// yet, which reads as broken even when the branch is right.</summary>
    public partial class DialogueUI : CanvasLayer
    {
        Control _root;
        Label _speaker, _body, _hint;
        VBoxContainer _options;
        PanelContainer _panel;
        PlayerController _player;

        NpcDialogue _dialogue;
        NpcMessage _message;
        int _page;
        readonly List<int> _shown = new();   // response index in the DIALOGUE's array, per drawn button

        /// <summary>The width this reads best at -- and a MAXIMUM, not a fixed size: on a 1024-wide window 860
        /// is the entire screen. <see cref="Layout"/> clamps it against the viewport.</summary>
        public const int PanelWidth = 860;
        const int SideMargin = 64;     // kept clear either side at any width
        const int BottomMargin = 56;   // panel bottom -> screen bottom
        const int Pad = 18;            // inside the panel. UITheme.PadPanel (10) is a CELL pad; this is the big surface.

        public override void _Ready()
        {
            Layer = 91;   // the note reader's layer: above the map, under the console
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            // A LIGHT scrim, not the note reader's 0.78. You are standing in front of somebody and the world
            // behind them is part of the scene -- blacking it out turns a conversation into a menu.
            var dim = new ColorRect { Color = UITheme.ScrimLight, MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);

            _panel = new PanelContainer();
            UITheme.Panel(_panel, solid: true);
            _root.AddChild(_panel);

            // ⚠ A PanelContainer gives its child the WHOLE box -- UITheme.Panel is a plain StyleBoxFlat with no
            // content margins, so without this the speaker name sits on the rounded corner. Every other panel in
            // the UI hand-places its children and pads by hand; a container has to say it.
            var pad = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, Pad);
            _panel.AddChild(pad);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 10);
            pad.AddChild(col);

            _speaker = UITheme.Label(new Label(), UITheme.FontTitle, UITheme.Accent);
            col.AddChild(_speaker);
            col.AddChild(new HSeparator());

            // The body carries the panel's width: autowrap needs something to wrap AGAINST, and the
            // PanelContainer then derives its own width from this. Layout keeps it in step with the viewport.
            _body = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart,
                                              CustomMinimumSize = new Vector2(PanelWidth - Pad * 2, 54) },
                                  UITheme.FontHeading, UITheme.TextBody);
            col.AddChild(_body);

            _options = new VBoxContainer();
            _options.AddThemeConstantOverride("separation", 6);
            col.AddChild(_options);

            _hint = UITheme.Label(new Label(), UITheme.FontLabel, UITheme.TextDim);
            col.AddChild(_hint);

            GetViewport().SizeChanged += Layout;
            // ⚠ AND ON THE PANEL'S OWN RESIZE. Layout reads _panel.Size, and a PanelContainer does not KNOW its
            // size until its children have been laid out -- so positioning it straight after a Draw uses a
            // stale (often zero) height and parks it off the bottom of the screen. The first render did exactly
            // that: the hint line was clipped by the screen edge. Re-running on Resized is the fix that does not
            // depend on guessing how many frames to wait.
            _panel.Resized += Layout;
        }

        /// <summary>Bottom-centre, the way a conversation sits in every RPG that has one: the speaker stays
        /// visible above it. Anchored to the BOTTOM rather than centred, so a tall list of options grows
        /// upward into empty screen instead of pushing the person you are talking to off the top.</summary>
        void Layout()
        {
            if (_panel == null) return;
            var vp = GetViewport().GetVisibleRect().Size;

            // Width first: 860 is a ceiling, not a promise. Setting the body's minimum re-runs the container's
            // layout, which fires _panel.Resized, which re-enters here with the size that actually happened --
            // so position below against whatever we have now and let that second pass correct it. Idempotent,
            // and it never has to guess how many frames a PanelContainer takes to settle.
            float inner = Mathf.Min(PanelWidth, vp.X - SideMargin * 2f) - Pad * 2f;
            if (Mathf.Abs(_body.CustomMinimumSize.X - inner) > 0.5f)
                _body.CustomMinimumSize = new Vector2(inner, _body.CustomMinimumSize.Y);

            var sz = _panel.Size;
            _panel.Position = new Vector2((vp.X - sz.X) * 0.5f, vp.Y - sz.Y - BottomMargin);
            // ⚠ PRINT THE REAL GEOMETRY. A --shot PNG is downscaled, so judging a panel's size or margin from
            // one is judging a resample -- the numbers are the only honest way to know whether 860 wide and a
            // 48 px gap actually happened.
            if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
                Log.Print($"[dlgui] viewport {vp.X}x{vp.Y}  panel {sz.X}x{sz.Y} at {_panel.Position}  bottomGap {vp.Y - (_panel.Position.Y + sz.Y):0.#}");
        }

        public bool IsOpen => _root != null && _root.Visible;

        public void Open(PlayerController player, string speakerName, NpcDialogue d, NpcMessage msg, List<int> available)
        {
            _player = player;
            _dialogue = d;
            _message = msg;
            _page = 0;
            _speaker.Text = speakerName ?? "";
            _root.Visible = true;
            Draw(available);
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        void Draw(List<int> available)
        {
            foreach (var c in _options.GetChildren()) ((Node)c).QueueFree();
            _shown.Clear();

            string[] pages = _message?.Pages ?? System.Array.Empty<string>();
            _body.Text = pages.Length > 0 ? pages[Mathf.Clamp(_page, 0, pages.Length - 1)] : "...";

            bool more = _page < pages.Length - 1;
            if (more)
            {
                // Mid-message: one way forward and no options. See the class note -- offering answers before the
                // question is finished is the thing this ordering exists to prevent.
                _hint.Text = $"Space / click to continue   ({_page + 1}/{pages.Length})";
                CallDeferred(nameof(Layout));   // ⚠ was a bare `return`: a multi-page message never repositioned
                return;
            }

            int n = 1;
            foreach (int idx in available ?? new List<int>())
            {
                var r = _dialogue.Responses[idx];
                // Autowrap so a long line wraps INSIDE the panel. Without it the button's minimum width is the
                // whole string and one chatty response drags the panel wider than the clamp Layout just applied.
                var b = new Button { Text = $"{n}.  {r.Text}", Alignment = HorizontalAlignment.Left, Flat = true,
                                     AutowrapMode = TextServer.AutowrapMode.WordSmart };
                UITheme.Label(b, UITheme.FontHeading, r.EndsConversation ? UITheme.TextDim : UITheme.Text);
                int captured = idx;   // ⚠ the DIALOGUE's index, never the button's position -- see PlayerController.ChooseResponse
                b.Pressed += () => Choose(captured);
                _options.AddChild(b);
                _shown.Add(idx);
                n++;
            }
            _hint.Text = _shown.Count > 0 ? "1-9 or click   ·   Esc to leave" : "Esc to leave";
            CallDeferred(nameof(Layout));
        }

        void Choose(int responseIndex)
        {
            if (_player == null || !GodotObject.IsInstanceValid(_player)) { Close(); return; }
            _player.ChooseResponse(responseIndex);   // the player owns what that MEANS; this only reports the click
        }

        public override void _Input(InputEvent e)
        {
            if (!IsOpen) return;
            if (e is InputEventKey { Pressed: true, Echo: false } k)
            {
                if (k.Keycode == Key.Escape) { _player?.CloseDialogue(); GetViewport().SetInputAsHandled(); return; }
                string[] pages = _message?.Pages ?? System.Array.Empty<string>();
                if (_page < pages.Length - 1 && (k.Keycode == Key.Space || k.Keycode == Key.Enter))
                {
                    _page++; Draw(_player?.AvailableResponseIndices()); GetViewport().SetInputAsHandled(); return;
                }
                int pick = (int)(k.Keycode - Key.Key1);   // 1-9 pick the nth SHOWN option (Key is a long-backed enum)
                if (pick >= 0 && pick < _shown.Count && pick < 9)
                {
                    Choose(_shown[pick]); GetViewport().SetInputAsHandled();
                }
            }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                string[] pages = _message?.Pages ?? System.Array.Empty<string>();
                if (_page < pages.Length - 1) { _page++; Draw(_player?.AvailableResponseIndices()); GetViewport().SetInputAsHandled(); }
            }
        }

        public void Close()
        {
            if (_root != null) _root.Visible = false;
            _dialogue = null; _message = null; _shown.Clear();
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // ---- test seams: the panel's job is WHAT IT SHOWS, so that is what a test has to be able to read ----
        public string DebugSpeaker => _speaker?.Text ?? "";
        public string DebugBody => _body?.Text ?? "";
        public int DebugOptionCount => _shown.Count;
        public string DebugOption(int i) => (uint)i < (uint)_options.GetChildCount() && _options.GetChild(i) is Button b ? b.Text : "";
    }
}
