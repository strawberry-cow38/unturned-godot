using Godot;

namespace UnturnedGodot
{
    // The build-mode readout: what you are about to place, what it is made of, and -- when the slot is refused
    // -- why. Deliberately its own CanvasLayer rather than a block inside HUD.cs: the build surface is a
    // self-contained mode, and HUD.cs is a careful 1:1 port of PlayerLifeUI that nothing here should be
    // wedged into.
    //
    // Showing the REFUSAL REASON is the point. A ghost that just turns red teaches you nothing -- "occupied"
    // and "no support" are different mistakes with different fixes, and a player who cannot tell them apart
    // concludes the build system is broken rather than that they need a floor underneath first.
    public partial class BuildHud : CanvasLayer
    {
        public BuildTool Tool;

        Label _line;
        Label _reason;
        PanelContainer _panel;

        public override void _Ready()
        {
            Layer = 10;
            _panel = new PanelContainer
            {
                AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
                OffsetLeft = -170f, OffsetRight = 170f, OffsetTop = -92f, OffsetBottom = -34f,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            var style = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.55f) };
            style.SetContentMarginAll(8f);
            style.SetCornerRadiusAll(4);
            _panel.AddThemeStyleboxOverride("panel", style);

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _line = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _reason = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            _reason.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.4f));
            col.AddChild(_line);
            col.AddChild(_reason);
            _panel.AddChild(col);
            AddChild(_panel);
            Visible = false;
        }

        public override void _Process(double delta)
        {
            bool on = Tool != null && Tool.Active;
            if (Visible != on) Visible = on;
            if (!on) return;

            var c = Tool.Construct;
            var t = StructureCatalog.TierAt(Tool.Tier);
            _line.Text = $"{c}  ·  {t.Name}  ·  {t.Health} hp     [C] type  [V] tier  [R] salvage";

            string why = Tool.BlockedReason;
            _reason.Visible = why != null;
            // Say what to DO about it, not just what is wrong -- "no support" alone reads as a bug report
            // about the game rather than an instruction to the player.
            _reason.Text = why switch
            {
                "occupied" => "slot taken — aim at an empty one",
                "no support" => t.RequiresPillars
                    ? $"no support — {t.Name} needs a neighbour (metal places free-standing)"
                    : "no support",
                null => "",
                _ => why,
            };
        }
    }
}
