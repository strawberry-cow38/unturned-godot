using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    /// <summary>The F panel for an item SPLITTER or MOVER (v56, strawberry 2026-10-06: "allow configuring the
    /// splitter to distribute in various ways" / "allow the items/s to be configured on the mover, 1/s - 32/s").
    ///
    /// ONE VALUE, SEVERAL VIEWS (the WalkiePanel rule): every widget is rendered FROM <see cref="Config"/> by
    /// Render(), and a widget change goes widget -> Config -> Changed -> all widgets. Nothing writes one widget
    /// from another, so the slider and the box beside it cannot disagree.
    ///
    /// It only ASKS. Changed is wired to the configure command; the server range-checks, applies and broadcasts,
    /// and the next open reads the replicated value back -- a panel that applied locally would show a rate the
    /// server never agreed to.</summary>
    public partial class ItemDevicePanel : CanvasLayer
    {
        public ItemDeviceKind Kind;
        public string Title = "";
        public uint NetId;
        public ItemDeviceConfig Config = new ItemDeviceConfig();
        public System.Action<ItemDeviceConfig> Changed;

        static readonly (SplitterMode mode, string label, string hint)[] Modes =
        {
            (SplitterMode.RoundRobin, "Round-robin", "Takes turns between the connected outputs."),
            (SplitterMode.Overflow, "Overflow", "Fills output 1 first; the next only gets what 1 cannot take."),
            (SplitterMode.Weighted, "Weighted", "Shares by weight -- 2:1 sends two to the first for every one to the second. 0 = off."),
        };

        readonly Button[] _modeBtns = new Button[Modes.Length];
        readonly SpinBox[] _weights = new SpinBox[ItemDeviceConfig.Ways];
        Control _weightRow;
        Label _hint, _rateRead;
        SpinBox _rate;
        HSlider _rateSlider;
        bool _syncing;

        public override void _Ready()
        {
            Layer = 11;   // menus
            var root = new PanelContainer { AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
                                            OffsetLeft = -210, OffsetTop = -120, OffsetRight = 210, OffsetBottom = 120 };
            UITheme.Panel(root, solid: true);
            AddChild(root);
            var pad = new MarginContainer();
            foreach (var side in new[] { "left", "right", "top", "bottom" }) pad.AddThemeConstantOverride("margin_" + side, 14);
            root.AddChild(pad);
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 10);
            pad.AddChild(col);
            col.AddChild(UITheme.Label(new Label { Text = Title.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center }, UITheme.FontTitle));

            if (Kind == ItemDeviceKind.Splitter)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                col.AddChild(row);
                for (int i = 0; i < Modes.Length; i++)
                {
                    var m = Modes[i].mode;
                    var b = new Button { Text = Modes[i].label, ToggleMode = true, CustomMinimumSize = new Vector2(120, 30) };
                    UITheme.Button(b);
                    b.Pressed += () => SetMode(m);
                    row.AddChild(b);
                    _modeBtns[i] = b;
                }
                _hint = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0) }, UITheme.FontBody, UITheme.TextDim);
                col.AddChild(_hint);
                var wrow = new HBoxContainer();
                wrow.AddThemeConstantOverride("separation", 10);
                col.AddChild(wrow);
                _weightRow = wrow;
                for (int i = 0; i < ItemDeviceConfig.Ways; i++)
                {
                    int k = i;
                    wrow.AddChild(UITheme.Label(new Label { Text = $"Out {i + 1}" }, UITheme.FontBody));
                    var sb = new SpinBox { MinValue = 0, MaxValue = ItemDeviceConfig.MaxWeight, Step = 1, CustomMinimumSize = new Vector2(70, 0) };
                    sb.ValueChanged += v => { if (!_syncing) SetWeight(k, (int)v); };
                    wrow.AddChild(sb);
                    _weights[i] = sb;
                }
            }
            else
            {
                _rateRead = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Center }, UITheme.FontHeading);
                col.AddChild(_rateRead);
                _rateSlider = new HSlider { MinValue = ItemDeviceConfig.MinRate, MaxValue = ItemDeviceConfig.MaxRate, Step = 1, CustomMinimumSize = new Vector2(380, 0) };
                _rateSlider.ValueChanged += v => { if (!_syncing) SetRate((int)v); };
                col.AddChild(_rateSlider);
                _rate = new SpinBox { MinValue = ItemDeviceConfig.MinRate, MaxValue = ItemDeviceConfig.MaxRate, Step = 1, Suffix = "/s" };
                _rate.ValueChanged += v => { if (!_syncing) SetRate((int)v); };
                col.AddChild(_rate);
            }
            col.AddChild(UITheme.Label(new Label { Text = "F or Esc to close", HorizontalAlignment = HorizontalAlignment.Center }, UITheme.FontSmall, UITheme.TextDim));
            Render();
        }

        // L1 handles on the real widgets: a test presses the BUTTON / sets the BOX, so it travels the same signal a
        // click does rather than calling the handler behind it.
        public Button DebugModeButton(int i) => _modeBtns[i];
        public SpinBox DebugWeightBox(int i) => _weights[i];
        public SpinBox DebugRateBox => _rate;

        // The ONLY writers of Config. The widgets call these; so do the test seams, so a test drives what a click drives.
        public void SetMode(SplitterMode m) { Config.Mode = m; Render(); Changed?.Invoke(Config.Clone()); }
        public void SetWeight(int i, int w) { Config.Weights[i] = (byte)Mathf.Clamp(w, 0, ItemDeviceConfig.MaxWeight); Render(); Changed?.Invoke(Config.Clone()); }
        public void SetRate(int r) { Config.Rate = (byte)Mathf.Clamp(r, ItemDeviceConfig.MinRate, ItemDeviceConfig.MaxRate); Render(); Changed?.Invoke(Config.Clone()); }

        /// <summary>Push Config into every widget. The only writer of any widget's value.</summary>
        void Render()
        {
            _syncing = true;
            if (Kind == ItemDeviceKind.Splitter)
            {
                for (int i = 0; i < Modes.Length; i++)
                    if (_modeBtns[i] != null) _modeBtns[i].ButtonPressed = Modes[i].mode == Config.Mode;
                if (_hint != null) foreach (var m in Modes) if (m.mode == Config.Mode) _hint.Text = m.hint;
                if (_weightRow != null) _weightRow.Visible = Config.Mode == SplitterMode.Weighted;   // weights only mean something in weighted mode
                for (int i = 0; i < _weights.Length; i++) if (_weights[i] != null) _weights[i].Value = Config.Weights[i];
            }
            else
            {
                if (_rateSlider != null) _rateSlider.Value = Config.Rate;
                if (_rate != null) _rate.Value = Config.Rate;
                if (_rateRead != null) _rateRead.Text = $"{Config.Rate} items / second";
            }
            _syncing = false;
        }

        /// <summary>Open on a device's CURRENT (replicated) config, releasing the mouse so the widgets work.</summary>
        public void Open(uint netId, string title, ItemDeviceKind kind, ItemDeviceConfig current)
        {
            NetId = netId; Title = title; Kind = kind;
            Config = current?.Clone() ?? new ItemDeviceConfig();
            Visible = true;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Render();
        }

        public void Close()
        {
            Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }
}
