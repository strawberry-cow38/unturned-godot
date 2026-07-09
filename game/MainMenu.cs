using Godot;

namespace UnturnedGodot
{
    // A tiny main menu shown on the default (exported-build) launch: title + Play / Play (No Zombies) / Quit.
    // OnPlay(noZombies) hands control back to Main to build the survival game with or without the horde.
    public partial class MainMenu : CanvasLayer
    {
        public System.Action<bool> OnPlay;

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

            AddButton(box, "Play", () => OnPlay?.Invoke(false));
            AddButton(box, "Play — No Zombies", () => OnPlay?.Invoke(true));
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
