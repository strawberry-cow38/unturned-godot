using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    /// <summary>Global chat: the scrollback, and the line you type into.
    ///
    /// Enter opens the input, Enter again sends it, Escape abandons it (strawberry 2026-09-12: "a global
    /// chat system for servers (enter to open). the server itself can send messages to the global chat
    /// too"). Modelled on DevConsole, which already solved the same problems -- a LineEdit that swallows
    /// every key while focused, and a scrollback that must not repaint at 60 fps.
    ///
    /// ⚠ THE CHANNEL IS RENDERED, NOT THE NAME. A server line is drawn without a name and in its own
    /// colour; a player line always shows a name. Deciding from the text or the name instead would make a
    /// player called "SERVER" indistinguishable from the real server, which is exactly the impersonation
    /// the wire is shaped to prevent -- and it would be defeated here, at the last step.
    ///
    /// ⚠ STILL NO BBCODE, and the per-row layout is what finally made that free. Tinting a name used to
    /// mean either markup -- reopening the hole the sanitiser spends its whole life closing -- or the old
    /// compromise of tinting the WHOLE block by the last line's colour. Separate Label nodes per field
    /// give every row two colours with no markup anywhere near untrusted text.
    ///
    /// TOP-LEFT, ON THE SHARED THEME (strawberry 2026-09-17: "fix the chat ui and ux to be more in line
    /// with the inventory, crafting, skills, information ui and align it to the top left").
    ///
    /// ⚠ That partly reverses a deliberate earlier call. This file used to argue for a text shadow and NO
    /// panel, because "chat sits over the world and a solid backing would black out a strip of it
    /// permanently" -- a real objection, and it is answered rather than ignored: the panel is UITheme.Bg,
    /// which is translucent, and it FADES OUT WITH THE LINES. So it matches the other screens while it has
    /// something to say and leaves the corner of the world alone when it does not. A permanent opaque slab
    /// would still be the wrong answer.</summary>
    public partial class ChatUI : CanvasLayer
    {
        /// <summary>How to send a line. Set by whoever owns the connection; null means chat is unavailable
        /// (singleplayer with no loopback), and then the UI never opens rather than silently eating Enter.</summary>
        public System.Func<string, bool> Send;

        /// <summary>A speaker's profile picture as raw PNG, or null. A HOOK rather than a direct reach into
        /// the net client, for the same reason Send is one: ChatUI is built and driven by the L1 test with
        /// no connection behind it, and a hard dependency here would make the UI untestable to add a
        /// decoration to it. Null hook, null bytes and an undecodable PNG all mean the same thing -- draw
        /// the row without a picture -- so a missing avatar is never an error path.</summary>
        public System.Func<ushort, byte[]> AvatarFor;

        const int ScrollbackLines = 8;
        const double FadeAfterSeconds = 12.0;   // a line stays readable long enough to answer it
        const int AvatarPx = 18;                // matches the cap height of FontBody, so a row reads as one line
        const int PanelWidth = 680;

        readonly List<(ushort speaker, string name, string text, bool server, double at)> _lines = new();
        // Decoded once per speaker, not once per line: the same person talking twenty times is one texture,
        // and a chat flood must not turn into twenty PNG decodes.
        readonly Dictionary<ushort, Texture2D> _avatars = new();

        PanelContainer _panel;
        VBoxContainer _rows;
        LineEdit _input;
        double _now;

        public bool IsTyping => _input != null && _input.Visible;

        public override void _Ready()
        {
            Layer = 20;   // above the HUD, below the pause overlay

            _panel = new PanelContainer { Position = new Vector2(14, 14) };
            _panel.AddThemeStyleboxOverride("panel", UITheme.Box(UITheme.Bg, UITheme.RadiusPanel));
            AddChild(_panel);

            var pad = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, UITheme.PadPanel);
            _panel.AddChild(pad);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", UITheme.Gap);
            pad.AddChild(col);

            _rows = new VBoxContainer();
            _rows.AddThemeConstantOverride("separation", 2);
            col.AddChild(_rows);

            _input = new LineEdit
            {
                PlaceholderText = "say something…   (Enter sends, Esc cancels)",
                Visible = false,
                CustomMinimumSize = new Vector2(PanelWidth, 28),
                MaxLength = ChatRules.MaxMessageChars,   // the server would truncate anyway; better to feel the limit
            };
            UITheme.Field(_input);   // the one call that stops it rendering in Godot's default light chrome
            col.AddChild(_input);
            _input.TextSubmitted += OnSubmit;

            Reflow();
            GetViewport().SizeChanged += Reflow;
            SetProcess(true);
        }

        /// <summary>Top-left, and only the WIDTH tracks the viewport -- the position is a fixed inset now, so
        /// unlike the old bottom-anchored version there is no resolution at which the panel lands somewhere
        /// unintended. Narrow windows still get a panel that fits.</summary>
        void Reflow()
        {
            var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
            float w = Mathf.Min(PanelWidth, vp.X - 28);
            _input.CustomMinimumSize = new Vector2(w, 28);
            _panel.CustomMinimumSize = new Vector2(w + UITheme.PadPanel * 2, 0);
        }

        /// <summary>A line arrived. Called from the net event; also used by the L1 test, which is why it is
        /// public and takes the event rather than reaching into the client.</summary>
        public void Receive(ChatMessageEvent e)
        {
            bool server = e.Channel == (byte)ChatChannel.Server;
            _lines.Add((e.SpeakerId, e.Name ?? "", e.Text ?? "", server, _now));
            while (_lines.Count > 64) _lines.RemoveAt(0);   // bounded; the visible window is much smaller
            Repaint();
        }

        public override void _Process(double delta)
        {
            _now += delta;
            // Repaint only when something can actually have changed appearance: a line ageing past the fade
            // threshold, or the input opening. Otherwise this is a rebuild every frame for nothing.
            if (_lines.Count > 0 && !IsTyping)
            {
                double oldest = _now - _lines[^1].at;
                if (oldest > FadeAfterSeconds && _panel.Visible) _panel.Visible = false;
            }
        }

        /// <summary>The speaker's picture, decoded once and cached. Server lines (id 0) never have one.</summary>
        Texture2D Avatar(ushort speaker)
        {
            if (speaker == 0 || AvatarFor == null) return null;
            if (_avatars.TryGetValue(speaker, out var hit)) return hit;
            Texture2D tex = null;
            var png = AvatarFor(speaker);
            if (png != null) tex = PlayerProfile.DecodeAvatar(png);   // validates 128x128 + rejects anything odd
            _avatars[speaker] = tex;                                  // cache the NULL too: a speaker with no
            return tex;                                               // avatar must not be re-decoded per line
        }

        void Repaint()
        {
            // ⚠ REMOVE, then free. QueueFree() is DEFERRED to the end of the frame, so the old rows are
            // still children while the new ones are being added -- two messages arriving in the same frame
            // rebuild the scrollback on top of itself and every line appears twice. Caught by the avatar
            // test counting 6 rows where 3 were expected, which is the whole reason it asserts on the ROW
            // rather than on the lookup having been called.
            foreach (var old in _rows.GetChildren()) { _rows.RemoveChild(old); old.QueueFree(); }

            int from = Mathf.Max(0, _lines.Count - ScrollbackLines);
            for (int i = from; i < _lines.Count; i++)
            {
                var ln = _lines[i];
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", UITheme.PadCell);

                // ⚠ The avatar slot is reserved even when empty, so names line up down the left edge instead
                // of stepping in and out as people with and without pictures talk.
                if (!ln.server)
                {
                    // ⚠ IgnoreSize, and it is load-bearing. CustomMinimumSize is a FLOOR, not a ceiling, so
                    // without this the TextureRect reports the texture's own 128x128 as its minimum and a chat
                    // row becomes 128 px tall -- the avatars came out bigger than the messages first time and
                    // the only reason I know is that I rendered it.
                    var pic = new TextureRect
                    {
                        CustomMinimumSize = new Vector2(AvatarPx, AvatarPx),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                        Texture = Avatar(ln.speaker),
                    };
                    row.AddChild(pic);
                }

                // A server line carries NO name -- rendered from the CHANNEL, never from the text.
                if (!ln.server && ln.name.Length > 0)
                    row.AddChild(UITheme.Label(new Label { Text = ln.name + ":" }, UITheme.FontBody, UITheme.Text));

                row.AddChild(UITheme.Label(new Label { Text = ln.text }, UITheme.FontBody,
                                           ln.server ? UITheme.Accent : UITheme.TextBody));
                _rows.AddChild(row);
            }
            _panel.Visible = true;
        }

        public override void _Input(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            if (Send == null) return;   // no connection: never capture the key

            if (IsTyping)
            {
                if (k.Keycode == Key.Escape) { Close(); GetViewport().SetInputAsHandled(); }
                return;   // Enter is handled by TextSubmitted; everything else belongs to the LineEdit
            }

            // Only open on the BOUND chat control, and only when nothing else owns the keyboard. A LineEdit
            // elsewhere with focus (the console, a rename field) must keep its Enter.
            if (!Keybinds.Matches(GameAction.Chat, k)) return;
            if (GetViewport().GuiGetFocusOwner() != null) return;
            if (OtherUiOwnsEnter?.Invoke() ?? false) return;
            Open();
            GetViewport().SetInputAsHandled();
        }

        /// <summary>Who to ask whether some OTHER UI still wants the cursor, so closing chat does not
        /// recapture it out from under an open inventory. Same contract DevConsole uses.</summary>
        public System.Func<bool> OtherUiWantsCursor;

        /// <summary>True when some other UI is mid-interaction and Enter belongs to IT. Dialogue is the
        /// case that caught this: it pages on Enter, and on the LAST page the keypress falls through and
        /// opens chat over the conversation.</summary>
        public System.Func<bool> OtherUiOwnsEnter;

        public void Open()
        {
            _input.Text = "";
            _input.Visible = true;
            _input.GrabFocus();
            // ⚠ RELEASE THE MOUSE, and this is not cosmetic. PlayerController gates ALL polled input on
            // `Input.MouseMode != Captured`, and keyboard focus is independent of mouse mode -- so without
            // this, typing "sss" also walks the player backwards, Space jumps, and a click fires the gun
            // through the chat box. DevConsole does exactly this for exactly this reason (its 2026-08-16
            // review note); chat is the second text field in the game and inherited the same requirement.
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _panel.Visible = true;   // show the history you are replying to, and the box even with no history
            Repaint();
        }

        public void Close()
        {
            if (!_input.Visible) return;   // idempotent: don't recapture a cursor we never released
            _input.Visible = false;
            _input.ReleaseFocus();
            _panel.Visible = _lines.Count > 0;
            // Only recapture if nothing else still wants it -- closing chat over an open inventory must
            // not steal the cursor back and re-enable walking through the grid.
            if (!(OtherUiWantsCursor?.Invoke() ?? false)) Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // ---- test hooks. The scrollback is rendered nodes, so a test that read them would be asserting on
        // layout; these expose the DECISION (what was attributed to whom) instead.
        /// <summary>The most recent line as it is attributed, or null. Composed the same way the row is:
        /// a server line has no name, a player line is "name: text".</summary>
        public string DebugLastLine => _lines.Count > 0
            ? (_lines[^1].server ? _lines[^1].text : $"{_lines[^1].name}: {_lines[^1].text}")
            : null;
        /// <summary>Drive the submit path exactly as the LineEdit does.</summary>
        public void DebugSubmit(string text) => OnSubmit(text);
        /// <summary>How many rows the scrollback is currently rendering, and how many of those drew a
        /// picture -- so a test can assert the avatar actually reached the row rather than that the lookup
        /// was merely called.</summary>
        public int DebugRowCount => _rows?.GetChildCount() ?? 0;
        public int DebugAvatarsShown
        {
            get
            {
                int n = 0;
                if (_rows == null) return 0;
                foreach (var r in _rows.GetChildren())
                    foreach (var c in r.GetChildren())
                        if (c is TextureRect { Texture: not null }) n++;
                return n;
            }
        }

        void OnSubmit(string text)
        {
            // Close FIRST. Send can fail (disconnected mid-type) and an input box left open after a failed
            // send reads as a frozen game -- and worse, keeps eating movement keys.
            Close();
            if (string.IsNullOrWhiteSpace(text)) return;
            Send?.Invoke(text);
        }
    }
}
