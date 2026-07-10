using Godot;

namespace UnturnedGodot
{
    // T weapon-attachment menu (true to source: UseableGun's isAttaching hook buttons). The gun presents in its real
    // Attach_Start pose, and each attachment slot shows the game's own SleekButtonIcon sprite (Sight/Tactical/Grip/
    // Barrel/Magazine, ripped from UI/Player/Icons/Useable/PlayerUseableGun) positioned OVER the gun's real hook point,
    // projected through the viewmodel camera so it tracks the gun. Click a slot to detach / re-attach it.
    public partial class AttachmentMenu : CanvasLayer
    {
        public Viewmodel VM;
        static readonly string[] Slots = { "Sight", "Tactical", "Grip", "Barrel", "Magazine" };
        readonly System.Collections.Generic.Dictionary<string, Button> _icons = new();
        // real attachment options per slot (interim: clicking the slot cycles through them; the source jars-grid
        // presentation is the next pass). null = detached. Only the eaglefire sight is wired so far.
        static readonly System.Collections.Generic.Dictionary<string, string[]> _cycle =
            new() { { "Sight", new[] { "eaglefire_iron_sights.txt", "red_dot_sight.txt", null } } };
        readonly System.Collections.Generic.Dictionary<string, int> _cycleIdx = new();

        public override void _Ready()
        {
            Layer = 58;
            Visible = false;

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.30f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;   // clicks pass through the dim to the slot buttons
            AddChild(dim);

            foreach (var slot in Slots)
            {
                var btn = new Button
                {
                    CustomMinimumSize = new Vector2(40, 40),
                    Size = new Vector2(40, 40),
                    Visible = false,
                    TooltipText = slot,
                    Icon = LoadIcon(slot),      // the real PlayerUseableGun slot sprite
                    ExpandIcon = true,
                };
                btn.AddThemeConstantOverride("icon_max_width", 40);
                // source SleekButtonIcon look: a dark semi-transparent box, brighter on hover/press
                btn.AddThemeStyleboxOverride("normal", Box(0.10f, 0.10f, 0.12f, 0.55f));
                btn.AddThemeStyleboxOverride("hover", Box(0.24f, 0.30f, 0.40f, 0.80f));
                btn.AddThemeStyleboxOverride("pressed", Box(0.30f, 0.46f, 0.62f, 0.90f));
                string s = slot;
                btn.Pressed += () =>
                {
                    if (VM == null) return;
                    if (_cycle.TryGetValue(s, out var opts))          // slot with real options: cycle through them
                    {
                        int cur = _cycleIdx.TryGetValue(s, out var ci) ? ci : 0;
                        int i = (cur + 1) % opts.Length;
                        _cycleIdx[s] = i;
                        VM.SetSlotMesh(s, opts[i]);
                    }
                    else if (VM.SlotHasModel(s))                       // other slots: just detach/re-attach the default
                        VM.SetSlotAttached(s, !VM.SlotAttached(s));
                    Refresh();
                };
                AddChild(btn);
                _icons[slot] = btn;
            }
        }

        static StyleBoxFlat Box(float r, float g, float b, float a)
        {
            var sb = new StyleBoxFlat { BgColor = new Color(r, g, b, a) };
            sb.SetCornerRadiusAll(4);
            sb.SetBorderWidthAll(1);
            sb.BorderColor = new Color(0f, 0f, 0f, 0.6f);
            return sb;
        }

        static Texture2D LoadIcon(string slot)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/attach_{slot.ToLower()}.png");
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // colour each icon by state: white = attached, red-ish = detached, faded = the gun has no model for that slot.
        void Refresh()
        {
            foreach (var slot in Slots)
            {
                bool hasModel = VM != null && VM.SlotHasModel(slot);
                bool attached = hasModel && VM.SlotAttached(slot);
                _icons[slot].Modulate = !hasModel ? new Color(1f, 1f, 1f, 0.35f) : attached ? Colors.White : new Color(1f, 0.55f, 0.55f);
            }
        }

        public override void _Process(double delta)
        {
            if (!Visible || VM == null) return;
            foreach (var slot in Slots)   // follow the gun: reposition each icon on its projected hook every frame
            {
                var btn = _icons[slot];
                if (VM.TryGetSlotScreen(slot, out var screen))
                {
                    btn.Visible = true;
                    btn.Position = screen - btn.Size / 2f;
                }
                else btn.Visible = false;
            }
        }

        public void Open()  { if (Visible) return; Visible = true;  VM?.EnterAttachView(); Refresh(); }
        public void Close() { if (!Visible) return; Visible = false; VM?.ExitAttachView(); }
        public void Toggle() { if (Visible) Close(); else Open(); }
        public bool IsOpen => Visible;
    }
}
