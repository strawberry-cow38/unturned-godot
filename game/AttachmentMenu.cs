using Godot;

namespace UnturnedGodot
{
    // T weapon-attachment menu (true to source): the gun presents in its real Attach_Start pose, and the attachment
    // slots show as icons positioned OVER the gun's actual hook points (sight on top, barrel out front, grip / mag /
    // tactical), projected through the viewmodel camera so they track the gun. Click a slot to detach / re-attach it.
    // Chunk 2 = the presented pose + positioned slot icons for the current gun's models; the item-swap grid comes next.
    public partial class AttachmentMenu : CanvasLayer
    {
        public Viewmodel VM;
        static readonly string[] Slots = { "Sight", "Tactical", "Barrel", "Grip", "Magazine" };
        readonly System.Collections.Generic.Dictionary<string, Button> _icons = new();

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
                var btn = new Button { Text = slot.Substring(0, 1), CustomMinimumSize = new Vector2(40, 40), Size = new Vector2(40, 40), Visible = false, TooltipText = slot };
                string s = slot;
                btn.Pressed += () => { if (VM != null && VM.SlotHasModel(s)) { VM.SetSlotAttached(s, !VM.SlotAttached(s)); Refresh(); } };
                AddChild(btn);
                _icons[slot] = btn;
            }
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

        public void Toggle()
        {
            Visible = !Visible;
            if (Visible) { VM?.EnterAttachView(); Refresh(); }
            else VM?.ExitAttachView();
        }
        public bool IsOpen => Visible;
    }
}
