using Godot;

namespace UnturnedGodot
{
    /// <summary>The walkie-talkie's frequency panel (strawberry 2026-09-11: "pressing r opens a menu to set a
    /// frequency. number input box as well as a slider").
    ///
    /// ⚠ ONE VALUE, TWO VIEWS, AND NEITHER WIDGET OWNS IT. A box and a slider bound to the same number is a
    /// standard trap: drive the slider from the box but not the box from the slider (or vice versa) and the
    /// one you forgot goes stale, which reads to the player as "the input didn't apply" rather than as a UI
    /// bug. Both widgets here are rendered FROM <see cref="Frequency"/> by Render(), and neither ever writes
    /// to the other -- a change goes widget -> Frequency -> both widgets. The _syncing guard exists because
    /// setting a widget's Value fires its own ValueChanged, which would otherwise recurse.
    /// (Trap named by cow tools before I wrote it.)</summary>
    public partial class WalkiePanel : CanvasLayer
    {
        /// <summary>Frequency in kHz. INTEGER kHz rather than float MHz on purpose: the player is picking a
        /// channel, and a float would let two radios be 0.0001 apart and not match while looking identical.
        ///
        /// ⚠ THE RANGE IS MY CHOICE, NOT A MEASURED ONE, and I would rather say so than imply otherwise. The
        /// walkie-talkie's frequency range is not on this box -- Walkie_Talkie.dat carries only
        /// `Useable Walkie_Talkie`, and the range lives in the game assembly we do not have. 300-900 MHz at
        /// 125 kHz spacing is chosen to be plausible for a handheld transceiver (real FRS/GMRS sits at
        /// 462-467) and to give the slider ~4800 steps, which is fine granularity without being unusable.
        /// If the real numbers turn up, this is one constant each.</summary>
        public const int MinKHz = 300_000, MaxKHz = 900_000, StepKHz = 125;
        public const int DefaultKHz = 462_562;   // mid-band, on-step: 462.5625 MHz

        public static int Snap(int kHz)
        {
            int c = Mathf.Clamp(kHz, MinKHz, MaxKHz);
            int steps = Mathf.RoundToInt((float)(c - MinKHz) / StepKHz);
            return Mathf.Clamp(MinKHz + steps * StepKHz, MinKHz, MaxKHz);
        }

        public static string Format(int kHz) => $"{kHz / 1000.0:0.000} MHz";

        int _freq = DefaultKHz;
        public int Frequency
        {
            get => _freq;
            set { _freq = Snap(value); Render(); Changed?.Invoke(_freq); }
        }

        public System.Action<int> Changed;

        SpinBox _box; HSlider _slider; Label _read;
        bool _syncing;

        public override void _Ready()
        {
            Layer = 11;   // menus
            var root = new PanelContainer { AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
                                            OffsetLeft = -190, OffsetTop = -85, OffsetRight = 190, OffsetBottom = 85 };
            AddChild(root);
            var col = new VBoxContainer();
            root.AddChild(col);
            col.AddThemeConstantOverride("separation", 10);
            col.AddChild(new Label { Text = "WALKIE-TALKIE", HorizontalAlignment = HorizontalAlignment.Center });

            _read = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            col.AddChild(_read);

            // The box takes MHz, because that is what the player reads off the label; it converts to kHz here
            // so there is still exactly one canonical unit underneath.
            _box = new SpinBox { MinValue = MinKHz / 1000.0, MaxValue = MaxKHz / 1000.0,
                                 Step = StepKHz / 1000.0, CustomArrowStep = StepKHz / 1000.0 };
            col.AddChild(_box);
            _box.ValueChanged += v => { if (!_syncing) Frequency = Mathf.RoundToInt((float)(v * 1000.0)); };

            _slider = new HSlider { MinValue = MinKHz, MaxValue = MaxKHz, Step = StepKHz, CustomMinimumSize = new Vector2(340, 0) };
            col.AddChild(_slider);
            _slider.ValueChanged += v => { if (!_syncing) Frequency = (int)v; };

            col.AddChild(new Label { Text = "R or Esc to close", HorizontalAlignment = HorizontalAlignment.Center });
            Render();
        }

        /// <summary>Push Frequency into BOTH widgets. The only writer of either widget's Value.</summary>
        void Render()
        {
            if (_box == null || _slider == null) return;
            _syncing = true;
            _box.Value = _freq / 1000.0;
            _slider.Value = _freq;
            if (_read != null) _read.Text = Format(_freq);
            _syncing = false;
        }

        /// <summary>Open/close, releasing the mouse while up so the widgets can be clicked. Returns the new
        /// open state.</summary>
        public bool Toggle()
        {
            Visible = !Visible;
            Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            return Visible;
        }
    }
}
