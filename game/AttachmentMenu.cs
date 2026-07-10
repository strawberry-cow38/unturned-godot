using Godot;

namespace UnturnedGodot
{
    // T weapon-attachment menu. Shows the equipped gun's attachment slots (Sight / Tactical / Grip / Barrel / Magazine)
    // and lets you detach/re-attach the ones that ship a model. The default iron sights ARE the Sight attachment, so
    // they can be removed (and later replaced), same as the source. Chunk 1 toggles the existing iron-sights/magazine
    // models; swapping for other attachment ITEMS (extra sights/barrels/grips, and inventory integration) comes next.
    public partial class AttachmentMenu : CanvasLayer
    {
        public Viewmodel VM;
        static readonly string[] Slots = { "Sight", "Tactical", "Grip", "Barrel", "Magazine" };
        static readonly System.Collections.Generic.Dictionary<string, string> SlotLabel =
            new() { { "Sight", "Iron Sights" }, { "Magazine", "Military Mag" } };
        VBoxContainer _rowsBox;

        public override void _Ready()
        {
            Layer = 58;
            Visible = false;

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.45f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            var panel = new PanelContainer();
            center.AddChild(panel);

            var margin = new MarginContainer();
            foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(s, 22);
            panel.AddChild(margin);

            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) };
            vbox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(vbox);

            var title = new Label { Text = "ATTACHMENTS", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontSizeOverride("font_size", 24);
            vbox.AddChild(title);

            _rowsBox = new VBoxContainer();
            _rowsBox.AddThemeConstantOverride("separation", 8);
            vbox.AddChild(_rowsBox);

            vbox.AddChild(new Label { Text = "T to close", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1f, 1f, 1f, 0.5f) });
        }

        void Rebuild()
        {
            foreach (var c in _rowsBox.GetChildren()) c.Free();
            foreach (var slot in Slots)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                _rowsBox.AddChild(row);

                row.AddChild(new Label { Text = slot, CustomMinimumSize = new Vector2(90, 0) });

                bool hasModel = VM != null && VM.SlotHasModel(slot);
                bool attached = hasModel && VM.SlotAttached(slot);
                string text = !hasModel ? "— empty —"
                            : attached ? (SlotLabel.TryGetValue(slot, out var l) ? l : slot)
                            : "— (removed) —";
                var name = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                if (!hasModel) name.Modulate = new Color(1f, 1f, 1f, 0.4f);
                row.AddChild(name);

                if (hasModel)
                {
                    var btn = new Button { Text = attached ? "Detach" : "Attach", CustomMinimumSize = new Vector2(90, 0) };
                    string s = slot;
                    btn.Pressed += () => { VM.SetSlotAttached(s, !VM.SlotAttached(s)); Rebuild(); };
                    row.AddChild(btn);
                }
            }
        }

        public void Toggle() { Visible = !Visible; if (Visible) Rebuild(); }
        public bool IsOpen => Visible;
    }
}
