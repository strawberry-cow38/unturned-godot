using Godot;

namespace UnturnedGodot
{
    // A tiny main menu shown on the default (exported-build) launch: title + Play / Play (No Zombies) / Quit.
    // "Play" now launches the real PEI world (OnDrivePEI); the old flat-terrain survival mode (OnPlay) was dropped
    // from the menu but its handler is kept for --flag test harnesses.
    public partial class MainMenu : CanvasLayer
    {
        public System.Action<bool> OnPlay;        // legacy flat-terrain survival build -- no longer on the menu, still driven by test flags
        public System.Action<bool> OnDrivePEI;   // bool = noZombies -- the real PEI world; this is what the menu's "Play" buttons call now

        public override void _Ready()
        {
            Layer = 50;

            var bg = new ColorRect { Color = new Color(0.06f, 0.07f, 0.09f) };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(bg);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 12);
            center.AddChild(box);

            var title = new Label { Text = "UNTURNED", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 68);
            box.AddChild(title);
            var sub = new Label { Text = "Godot Port", HorizontalAlignment = HorizontalAlignment.Center };
            sub.Modulate = new Color(1, 1, 1, 0.45f);
            box.AddChild(sub);
            box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });   // spacer

            AddButton(box, "Play", () => OnDrivePEI?.Invoke(false));                 // real PEI world (was "Drive PEI") -- now the primary play mode
            AddButton(box, "Play — No Zombies", () => OnDrivePEI?.Invoke(true));      // real PEI, horde off (was "Drive PEI — No Zombies")
            AddButton(box, "Quit", () => GetTree().Quit());
        }

        void AddButton(VBoxContainer box, string text, System.Action onPressed)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(280, 46) };
            b.Pressed += () => onPressed();
            box.AddChild(b);
        }
    }
}
