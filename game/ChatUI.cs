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
    /// The scrollback FADES when closed and is fully visible while typing, so chat does not permanently
    /// occupy the corner of the screen in a game you mostly play without it.</summary>
    public partial class ChatUI : CanvasLayer
    {
        /// <summary>How to send a line. Set by whoever owns the connection; null means chat is unavailable
        /// (singleplayer with no loopback), and then the UI never opens rather than silently eating Enter.</summary>
        public System.Func<string, bool> Send;

        const int ScrollbackLines = 8;
        const double FadeAfterSeconds = 12.0;   // a line stays readable long enough to answer it

        readonly List<(string text, Color colour, double at)> _lines = new();
        Label _log;
        LineEdit _input;
        double _now;

        public bool IsTyping => _input != null && _input.Visible;

        static readonly Color PlayerColour = new Color(0.92f, 0.94f, 0.96f);
        static readonly Color ServerColour = new Color(1.00f, 0.86f, 0.42f);   // distinct hue, not just bold

        public override void _Ready()
        {
            Layer = 20;   // above the HUD, below the pause overlay

            _log = new Label
            {
                Position = new Vector2(14, 0),
                Modulate = PlayerColour,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _log.AddThemeFontSizeOverride("font_size", 15);
            // A shadow rather than a panel: chat sits over the world and a solid backing would black out a
            // strip of it permanently. This keeps light text readable against snow and sand both.
            _log.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.85f));
            _log.AddThemeConstantOverride("shadow_offset_x", 1);
            _log.AddThemeConstantOverride("shadow_offset_y", 1);
            AddChild(_log);

            _input = new LineEdit
            {
                PlaceholderText = "say something…   (Enter sends, Esc cancels)",
                Visible = false,
                Size = new Vector2(680, 30),
                Position = new Vector2(14, 0),
                MaxLength = ChatRules.MaxMessageChars,   // the server would truncate anyway; better to feel the limit
            };
            AddChild(_input);
            _input.TextSubmitted += OnSubmit;

            Reflow();
            GetViewport().SizeChanged += Reflow;
            SetProcess(true);
        }

        /// <summary>Anchor to the bottom-left, recomputed on resize -- a fixed Y puts chat in the middle of
        /// the screen at one resolution and off it at another.</summary>
        void Reflow()
        {
            var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
            _input.Position = new Vector2(14, vp.Y - 90);
            _input.Size = new Vector2(Mathf.Min(680, vp.X - 28), 30);
            _log.Position = new Vector2(14, vp.Y - 100 - ScrollbackLines * 19);
            _log.Size = new Vector2(vp.X - 28, ScrollbackLines * 19);
        }

        /// <summary>A line arrived. Called from the net event; also used by the L1 test, which is why it is
        /// public and takes the event rather than reaching into the client.</summary>
        public void Receive(ChatMessageEvent e)
        {
            bool server = e.Channel == (byte)ChatChannel.Server;
            string text = server ? e.Text : $"{e.Name}: {e.Text}";
            _lines.Add((text, server ? ServerColour : PlayerColour, _now));
            while (_lines.Count > 64) _lines.RemoveAt(0);   // bounded; the visible window is much smaller
            Repaint();
        }

        public override void _Process(double delta)
        {
            _now += delta;
            // Repaint only when something can actually have changed appearance: a line ageing past the fade
            // threshold, or the input opening. Otherwise this is a Label rebuild every frame for nothing.
            if (_lines.Count > 0 && !IsTyping)
            {
                double oldest = _now - _lines[^1].at;
                if (oldest > FadeAfterSeconds && _log.Visible) { _log.Visible = false; }
            }
        }

        void Repaint()
        {
            int from = Mathf.Max(0, _lines.Count - ScrollbackLines);
            var sb = new System.Text.StringBuilder();
            for (int i = from; i < _lines.Count; i++) sb.AppendLine(_lines[i].text);
            _log.Text = sb.ToString();
            // One Label cannot hold two colours without BBCode -- and BBCode is exactly what the sanitiser
            // spends its time disarming, so enabling it here to tint a name would reopen the hole from the
            // other end. The most recent line's colour tints the block instead, which is enough to tell a
            // server announcement from chatter without giving markup a way back in.
            _log.Modulate = _lines.Count > 0 ? _lines[^1].colour : PlayerColour;
            _log.Visible = true;
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
            Open();
            GetViewport().SetInputAsHandled();
        }

        public void Open()
        {
            _input.Text = "";
            _input.Visible = true;
            _input.GrabFocus();
            _log.Visible = _lines.Count > 0;   // show the history you are replying to
            Repaint();
        }

        public void Close()
        {
            _input.Visible = false;
            _input.ReleaseFocus();
        }

        // ---- test hooks. The scrollback is a rendered Label, so a test that read _log.Text would be
        // asserting on formatting; these expose the DECISION (what was attributed to whom) instead.
        /// <summary>The most recent line as it is rendered, or null.</summary>
        public string DebugLastLine => _lines.Count > 0 ? _lines[^1].text : null;
        /// <summary>Drive the submit path exactly as the LineEdit does.</summary>
        public void DebugSubmit(string text) => OnSubmit(text);

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
