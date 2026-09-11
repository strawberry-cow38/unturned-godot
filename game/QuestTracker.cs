using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>The tracked quest, top-right (master 2026-09-11: "a summary appears in the top right").
    ///
    /// A SUMMARY, not the log. One quest, its objectives, and how far along each is -- the thing you glance at
    /// while playing to remember what you are doing. The full list lives in the Information page, and the split
    /// is the point: a corner widget that tried to show thirty quests would be a wall you learn to ignore.
    ///
    /// It draws NOTHING when you have no quest. An empty panel that says "no quests" is a permanent reminder of
    /// an absence, and this screen already has five bars and a crosshair competing for the same attention.</summary>
    public partial class QuestTracker : CanvasLayer
    {
        Control _root;
        PanelContainer _panel;
        Label _title;
        VBoxContainer _lines;
        PlayerController _player;

        int _shownQuest = -1;
        string _shownState = "";
        double _t;

        const int PanelW = 420, TopGap = 34;   // TopGap clears the FPS counter, which owns the very top-right

        public override void _Ready()
        {
            Layer = 11;   // over the HUD (10), under the vitals layer (12)
            _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            _panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            UITheme.Panel(_panel);   // translucent, not solid: this sits over the world all the time
            _root.AddChild(_panel);

            var pad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, 10);
            _panel.AddChild(pad);

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 3);
            pad.AddChild(col);

            _title = UITheme.LabelOutlined(new Label
            {
                // ⚠ A WIDTH, or autowrap measures itself against nothing and invents a height. The trade
                // window's description line claimed 637 px that way.
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(PanelW - 20, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            }, UITheme.FontHeading, UITheme.Accent);
            col.AddChild(_title);

            _lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _lines.AddThemeConstantOverride("separation", 2);
            col.AddChild(_lines);

            GetViewport().SizeChanged += Layout;
            _panel.Resized += Layout;
        }

        public void Bind(PlayerController p) => _player = p;

        void Layout()
        {
            if (_panel == null) return;
            var vp = GetViewport().GetVisibleRect().Size;
            _panel.Position = new Vector2(vp.X - _panel.Size.X - 14f, TopGap);
            if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
                Log.Print($"[questtrack] viewport {vp.X}x{vp.Y} panel {_panel.Size.X}x{_panel.Size.Y} at {_panel.Position}");
        }

        /// <summary>Repaint only when the ANSWER changed, not every frame. Progress is read from flags, the bag
        /// and kill counters, so recomputing it 60 times a second to write the same string is work nobody asked
        /// for -- but it does have to notice the frame an item lands in your inventory, so the check is cheap
        /// and the rebuild is gated on its result rather than on a timer alone.</summary>
        public override void _Process(double delta)
        {
            _t += delta;
            if (_t < 0.2) return;
            _t = 0;
            if (_player == null || !GodotObject.IsInstanceValid(_player)) { _panel.Visible = false; return; }

            // ⚠ NOT WHILE A MENU IS OPEN. The navbar owns the top strip at exactly this height, so the summary
            // rendered UNDERNEATH it -- and the Information page has the whole log on screen anyway, so a corner
            // duplicate of one row of it is noise sitting on top of a tab label.
            if (_player.AnyMenuOpen) { _panel.Visible = false; return; }

            var q = _player.TrackedQuestDef;
            if (q == null)
            {
                if (_panel.Visible) { _panel.Visible = false; _shownQuest = -1; _shownState = ""; }
                return;
            }

            var w = _player.NpcState;
            var objectives = QuestRules.Objectives(q, w);
            // The whole visible state as one string: if it matches, nothing on screen would differ.
            var sb = new System.Text.StringBuilder(QuestRules.StatusOf(q, w).ToString());
            foreach (var (text, done) in objectives) sb.Append('\u001F').Append(done ? '1' : '0').Append(text);
            string state = sb.ToString();
            if (q.Id == _shownQuest && state == _shownState) { _panel.Visible = true; return; }
            _shownQuest = q.Id;
            _shownState = state;

            bool ready = QuestRules.StatusOf(q, w) == ENpcQuestStatus.Ready;
            _title.Text = ready ? TradeRules.PlainText(q.Name) + "  —  ready" : TradeRules.PlainText(q.Name);
            _title.AddThemeColorOverride("font_color", ready ? UITheme.Good : UITheme.Accent);

            foreach (var c in _lines.GetChildren()) ((Node)c).QueueFree();
            foreach (var (text, done) in objectives)
                _lines.AddChild(UITheme.LabelOutlined(new Label
                {
                    Text = (done ? "✓  " : "•  ") + text,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    CustomMinimumSize = new Vector2(PanelW - 20, 0),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                }, UITheme.FontBody, done ? UITheme.Good : UITheme.TextBody));

            _panel.Visible = true;
            CallDeferred(nameof(Layout));
        }

        // ---- test seams ----
        public bool DebugVisible => _panel != null && _panel.Visible;
        public string DebugTitle => _title?.Text ?? "";
        public int DebugLineCount => _lines?.GetChildCount() ?? 0;
        public string DebugLine(int i) => _lines != null && i < _lines.GetChildCount() && _lines.GetChild(i) is Label l ? l.Text : "";
    }
}
