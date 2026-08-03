using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // T weapon-attachment menu (true to source: UseableGun's isAttaching hook buttons). The gun presents in its real
    // Attach_Start pose, and each attachment slot shows the game's own SleekButtonIcon sprite (Sight/Tactical/Grip/
    // Barrel/Magazine, ripped from UI/Player/Icons/Useable/PlayerUseableGun) positioned OVER the gun's real hook point,
    // projected through the viewmodel camera so it tracks the gun.
    //
    // Clicking a slot used to CYCLE a hardcoded list -- four sights, in a fixed order, on every gun, regardless of
    // what the player was carrying (strawberry: "instead of pressing each slot to cycle random attachments or
    // whatever, actually consider player inventory and which attachments they have and which apply to each slot,
    // showing quick attach buttons for each relevant thing around its relevant attachment slot. like source does").
    //
    // Now: clicking a slot fans out one QUICK-ATTACH button per attachment you are actually carrying that fits that
    // slot on this gun, plus a Detach. The fit rule lives in AttachmentFit (the retail Items.SearchContents filter)
    // rather than here, so the menu can't drift from what the reload path considers a valid magazine.
    public partial class AttachmentMenu : CanvasLayer
    {
        public Viewmodel VM;
        public PlayerController Player;      // the bag the options are drawn from; null -> slots still detach/re-attach
        static string[] Slots => AttachmentFit.Slots;

        readonly System.Collections.Generic.Dictionary<string, Button> _icons = new();
        readonly System.Collections.Generic.List<Button> _options = new();   // the fanned-out quick-attach buttons
        string _openSlot;                                                    // which slot's fan is showing, null = none

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
                    Icon = LoadIcon($"attach_{slot.ToLower()}.png"),   // the real PlayerUseableGun slot sprite
                    ExpandIcon = true,
                };
                btn.AddThemeConstantOverride("icon_max_width", 40);
                btn.AddThemeStyleboxOverride("normal", Box(0.10f, 0.10f, 0.12f, 0.55f));
                btn.AddThemeStyleboxOverride("hover", Box(0.24f, 0.30f, 0.40f, 0.80f));
                btn.AddThemeStyleboxOverride("pressed", Box(0.30f, 0.46f, 0.62f, 0.90f));
                string s = slot;
                btn.Pressed += () => ToggleFan(s);
                AddChild(btn);
                _icons[slot] = btn;
            }
        }

        // Open (or close) the quick-attach fan for one slot. Only one fan at a time -- five slots' worth of options
        // over a pistol would cover the gun the player is trying to look at.
        void ToggleFan(string slot)
        {
            if (_openSlot == slot) { ClearFan(); return; }
            ClearFan();
            if (VM == null) return;
            _openSlot = slot;

            int caliber = Player?.Gun?.Caliber ?? 0;
            var opts = AttachmentFit.InBag(Player?.Inventory, slot, caliber);

            // DETACH first, and only when something is actually on the slot. Removing an attachment RETURNS THE ITEM
            // TO THE BAG (strawberry) -- it's a real object, not a flag, so taking a scope off one gun has to leave
            // you holding a scope you can put on another.
            int installed = AttachmentFit.InstalledId(Player?.HeldItemForTest, slot);
            if (installed >= 0 || (VM.SlotHasModel(slot) && VM.SlotAttached(slot)))
            {
                var held = Player?.HeldItemForTest;
                AddOption(slot, installed >= 0 ? $"Detach {Assets.find((ushort)installed)?.itemName ?? "attachment"}" : "Detach",
                    installed >= 0 ? (ushort)installed : null, 0, () =>
                {
                    // Give it back BEFORE clearing the slot, and only clear if the bag actually took it -- a full bag
                    // must refuse the detach rather than delete the attachment. Dropping it on the floor instead would
                    // be the other defensible answer; silently destroying it is not.
                    if (installed >= 0)
                    {
                        if (Player?.Inventory != null && !Player.Inventory.tryAddItem(new Item((ushort)installed)))
                        {
                            HUD.Alert("No room to remove that — free a slot first");
                            return;
                        }
                        AttachmentFit.SetInstalledId(held, slot, -1);
                    }
                    VM.SetSlotAttached(slot, false);
                    Refresh();
                });
            }

            foreach (var (asset, count) in opts)
            {
                var a = asset;
                string label = count > 1 ? $"{a.itemName} x{count}" : a.itemName;
                string mesh = AttachmentFit.MeshFor(a.id);
                AddOption(slot, label, a.id, count, () =>
                {
                    var held = Player?.HeldItemForTest;
                    // Swapping onto an occupied slot returns the OUTGOING attachment first, so a swap is a swap and
                    // not a quiet destruction of whatever was already fitted.
                    int prev = AttachmentFit.InstalledId(held, slot);
                    if (prev >= 0 && prev != a.id) Player?.Inventory?.tryAddItem(new Item((ushort)prev));
                    if (!TakeFromBag(a.id)) return;                  // consume the one being installed
                    AttachmentFit.SetInstalledId(held, slot, a.id);
                    // An attachment with no ripped mesh still ATTACHES -- it just renders nothing. Hiding those would
                    // hide most of the arsenal from a menu whose whole job is showing what you own.
                    if (mesh != null) VM.SetSlotMesh(slot, mesh);
                    else if (VM.SlotHasModel(slot)) VM.SetSlotAttached(slot, true);
                    Refresh();
                });
            }

            if (_options.Count == 0)   // carrying nothing for this slot: say so instead of opening an empty fan
                AddOption(slot, "— nothing that fits —", null, 0, null);

            LayoutFan();
        }

        // Remove ONE of `id` from the bag -- the attachment is now on the gun, so it must not still be in your
        // pockets. Scans the same page range the options came from; false = it wasn't there (a stale fan after the
        // bag changed underneath), in which case the caller does nothing rather than fitting an item you don't own.
        bool TakeFromBag(ushort id)
        {
            var inv = Player?.Inventory;
            if (inv == null) return true;   // no bag (the --attach viewmodel harness): nothing to consume, still fit it
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == id) { pg.removeItem(i); return true; }
            }
            return false;
        }

        void AddOption(string slot, string label, ushort? iconId, int count, System.Action onPress)
        {
            var b = new Button
            {
                Text = label,
                CustomMinimumSize = new Vector2(0, 30),
                Icon = iconId.HasValue ? LoadItemIcon(iconId.Value) : null,
                ExpandIcon = false,
                Disabled = onPress == null,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.AddThemeStyleboxOverride("normal", Box(0.08f, 0.09f, 0.11f, 0.88f));
            b.AddThemeStyleboxOverride("hover", Box(0.24f, 0.34f, 0.46f, 0.94f));
            b.AddThemeStyleboxOverride("pressed", Box(0.30f, 0.46f, 0.62f, 0.96f));
            b.AddThemeStyleboxOverride("disabled", Box(0.08f, 0.09f, 0.11f, 0.55f));
            if (onPress != null) b.Pressed += () => { onPress(); ClearFan(); };
            AddChild(b);
            _options.Add(b);
        }

        // Stack the fan beside its slot icon. Right of the slot normally; flipped to the left when the slot sits in
        // the right third of the screen, so the options never run off the edge on a gun held to the right.
        void LayoutFan()
        {
            if (_openSlot == null || VM == null || !VM.TryGetSlotScreen(_openSlot, out var anchor)) return;
            var vp = GetViewport().GetVisibleRect().Size;
            bool flip = anchor.X > vp.X * 0.66f;
            float w = 210f, gap = 4f, h = 30f;
            float total = _options.Count * h + (_options.Count - 1) * gap;
            float y = Mathf.Clamp(anchor.Y - total / 2f, 8f, Mathf.Max(8f, vp.Y - total - 8f));
            for (int i = 0; i < _options.Count; i++)
            {
                _options[i].Size = new Vector2(w, h);
                _options[i].Position = new Vector2(flip ? anchor.X - w - 28f : anchor.X + 28f, y + i * (h + gap));
            }
        }

        void ClearFan()
        {
            foreach (var b in _options) b.QueueFree();
            _options.Clear();
            _openSlot = null;
        }

        static StyleBoxFlat Box(float r, float g, float b, float a)
        {
            var sb = new StyleBoxFlat { BgColor = new Color(r, g, b, a) };
            sb.SetCornerRadiusAll(4);
            sb.SetBorderWidthAll(1);
            sb.BorderColor = new Color(0f, 0f, 0f, 0.6f);
            return sb;
        }

        static Texture2D LoadIcon(string file)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/{file}");
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // the real ground-truth item icon (content/items/icons/<id>.png), same source the inventory grid uses
        static Texture2D LoadItemIcon(ushort id)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png");
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // colour each slot icon by state: white = attached, red-ish = detached, faded = the gun has no model there.
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
            if (_openSlot != null) LayoutFan();   // the fan tracks its slot as the gun sways
        }

        public void Open()  { if (Visible) return; Visible = true;  VM?.EnterAttachView(); Refresh(); }
        public void Close() { if (!Visible) return; ClearFan(); Visible = false; VM?.ExitAttachView(); }
        public void Toggle() { if (Visible) Close(); else Open(); }
        public bool IsOpen => Visible;
    }
}
