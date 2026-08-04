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
        // Every slot's ring, all live at once. Keyed by slot because each ring is positioned around its own hook.
        readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Button>> _rings = new();

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
                btn.Pressed += () => DetachSlot(s);   // the slot IS the detach button now
                AddChild(btn);
                _icons[slot] = btn;
            }
        }

        // EVERY slot's ring is live the whole time you are inspecting (master: "the orbitting icons always show when
        // inspecting, dont need to click the slot"). No open/close, no selected slot -- rebuilt whenever the bag or
        // the gun changes, positioned every frame as the weapon sways.
        void RebuildRings()
        {
            ClearRings();
            if (VM == null) return;
            int caliber = Player?.Gun?.Caliber ?? 0;
            foreach (var slot in Slots)
            {
                if (!VM.SlotHasModel(slot)) continue;   // a slot this gun cannot take shows nothing at all -- its icon is already greyed
                var list = new System.Collections.Generic.List<Button>();
                // ONE ICON PER PHYSICAL ITEM (master: "if we have multiple of the same relevant item, show it that
                // many times in the orbit"). InBag collapses duplicates to (asset, count) because the old fan was a
                // text list where "x6" was the readable answer; a ring of icons has nowhere to put a multiplier, and
                // six magazines drawn six times is the point -- the ring IS the count.
                foreach (var (asset, count) in AttachmentFit.InBag(Player?.Inventory, slot, caliber))
                for (int dup = 0; dup < count; dup++)
                {
                    var a = asset;
                    string mesh = AttachmentFit.MeshFor(a.id);
                    string sl = slot;
                    list.Add(AddOption(sl, a.itemName, a.id, () =>
                    {
                        var held = Player?.HeldItemForTest;
                        // Swapping onto an occupied slot returns the OUTGOING attachment first, so a swap is a swap
                        // and not a quiet destruction of whatever was already fitted.
                        int prev = AttachmentFit.InstalledId(held, sl);
                        if (prev >= 0 && prev != a.id) Player?.Inventory?.tryAddItem(new Item((ushort)prev));
                        if (!TakeFromBag(a.id)) return;                  // consume the one being installed
                        AttachmentFit.SetInstalledId(held, sl, a.id);
                        // An attachment with no ripped mesh still ATTACHES -- it just renders nothing. Hiding those
                        // would hide most of the arsenal from a menu whose whole job is showing what you own.
                        if (mesh != null) VM.SetSlotMesh(sl, mesh);
                        else if (VM.SlotHasModel(sl)) VM.SetSlotAttached(sl, true);
                        Refresh();
                    }));
                }
                if (list.Count > 0) _rings[slot] = list;
            }
            LayoutRings();
        }

        /// <summary>Clicking the SLOT takes its attachment off (master: "detach shouldnt be part of the ring,
        /// clicking the slot itself should remove the attachment"). Returns false when there was nothing to remove,
        /// so a click on an empty slot is a no-op rather than a silent state change.
        ///
        /// The item goes back in the bag BEFORE the slot is cleared, and the slot is only cleared if the bag actually
        /// took it -- a full bag has to refuse the detach rather than delete the attachment. Dropping it on the floor
        /// would be the other defensible answer; destroying it is not.</summary>
        public bool DetachSlot(string slot)
        {
            if (VM == null) return false;
            int installed = AttachmentFit.InstalledId(Player?.HeldItemForTest, slot);
            if (installed < 0 && !(VM.SlotHasModel(slot) && VM.SlotAttached(slot))) return false;
            if (installed >= 0)
            {
                if (Player?.Inventory != null && !Player.Inventory.tryAddItem(new Item((ushort)installed)))
                {
                    HUD.Alert("No room to remove that — free a slot first");
                    return false;
                }
                AttachmentFit.SetInstalledId(Player?.HeldItemForTest, slot, -1);
            }
            VM.SetSlotAttached(slot, false);
            Refresh();
            return true;
        }

        /// <summary>Run the DETACH exactly as a slot click does. A test seam because the reported bug ("won't drag
        /// after I take them off a gun") is specific to items that came out of THIS path, and reproducing it by
        /// calling tryAddItem directly proved nothing: that passes.</summary>
        public bool DebugDetach(string slot) => DetachSlot(slot);

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

        // ICONS ONLY (master: "we only need to show the icons of attachments, not the names too, at a reasonable
        // size"). The name moves to the TOOLTIP rather than being dropped -- an icon grid is unreadable for anyone who
        // has not memorised the sprites, and hover costs nothing on screen.
        const float OptSize = 44f, OptGap = 10f;

        Button AddOption(string slot, string label, ushort? iconId, System.Action onPress, Color? tint = null)
        {
            var b = new Button
            {
                CustomMinimumSize = new Vector2(OptSize, OptSize),
                Size = new Vector2(OptSize, OptSize),
                Icon = iconId.HasValue ? LoadItemIcon(iconId.Value) : null,
                ExpandIcon = true,
                TooltipText = label,
                Disabled = onPress == null,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            b.AddThemeConstantOverride("icon_max_width", (int)OptSize - 8);
            if (tint.HasValue) b.Modulate = tint.Value;
            // An option with no ripped icon would be a blank square with no way to tell it apart from its neighbours,
            // so those keep the text. Rare, and better than an unlabelled hole in the ring.
            if (!iconId.HasValue || b.Icon == null) { b.Text = label; b.ExpandIcon = false; b.CustomMinimumSize = new Vector2(0, 30); b.AddThemeFontSizeOverride("font_size", 12); }
            b.AddThemeStyleboxOverride("normal", Box(0.08f, 0.09f, 0.11f, 0.88f));
            b.AddThemeStyleboxOverride("hover", Box(0.24f, 0.34f, 0.46f, 0.94f));
            b.AddThemeStyleboxOverride("pressed", Box(0.30f, 0.46f, 0.62f, 0.96f));
            b.AddThemeStyleboxOverride("disabled", Box(0.08f, 0.09f, 0.11f, 0.55f));
            if (onPress != null) b.Pressed += () => onPress();
            AddChild(b);
            return b;
        }

        /// <summary>Radius that fits <paramref name="n"/> icons of <paramref name="size"/> around a ring without any
        /// two touching (master: "orbitting the attachment slot, making sure they arent overlapping").
        ///
        /// Adjacent centres on a circle of radius r are a CHORD apart, 2r*sin(pi/n) -- not an arc, which is the easy
        /// thing to reach for and always overestimates the gap, so a ring sized by arc length overlaps at exactly the
        /// small counts a gun actually produces. Solving the chord for r is what makes "not overlapping" a property
        /// rather than a tuned constant that breaks the first time somebody carries seven magazines.
        ///
        /// Floored so a one- or two-icon ring still clears the slot sprite underneath it.</summary>
        internal static float OrbitRadius(int n, float size, float gap, float minR)
        {
            if (n <= 1) return minR;
            float need = (size + gap) / (2f * Mathf.Sin(Mathf.Pi / n));
            return Mathf.Max(minR, need);
        }

        // Ring the options around their slot icon. The ring's CENTRE is pulled inward so the whole circle fits on
        // screen -- clamping each button individually would fix the edge case by stacking two of them on top of each
        // other, which is the exact thing being fixed here.
        void LayoutRings()
        {
            if (VM == null) return;
            var vp = GetViewport().GetVisibleRect().Size;
            foreach (var kv in _rings)
            {
                var opts = kv.Value;
                int n = opts.Count;
                if (n == 0) continue;
                bool on = VM.TryGetSlotScreen(kv.Key, out var anchor);
                foreach (var b in opts) b.Visible = on;   // slot off-screen (gun turned away) -> its whole ring goes with it
                if (!on) continue;
                float r = OrbitRadius(n, OptSize, OptGap, 56f);
                float pad = r + OptSize * 0.5f + 6f;
                var centre = new Vector2(Mathf.Clamp(anchor.X, pad, Mathf.Max(pad, vp.X - pad)),
                                         Mathf.Clamp(anchor.Y, pad, Mathf.Max(pad, vp.Y - pad)));
                // Start at the top and go clockwise: options land somewhere predictable instead of wherever angle 0
                // happens to be, which matters when this reruns every frame as the gun sways.
                for (int i = 0; i < n; i++)
                {
                    float th = -Mathf.Pi / 2f + Mathf.Tau * i / n;
                    opts[i].Position = centre + new Vector2(Mathf.Cos(th), Mathf.Sin(th)) * r - opts[i].Size / 2f;
                }
            }
        }

        void ClearRings()
        {
            foreach (var kv in _rings) foreach (var b in kv.Value) b.QueueFree();
            _rings.Clear();
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

        /// <summary>A slot icon's colour (master: "the slot icons change color depending on if they can accept an
        /// attachment in that slot (gray for no, white for yes) and if they have an attachment show the slot icon in
        /// the rarity of the attachment in that slot's color").
        ///
        /// Three states, in that order: can't take one -> GREY, can and is empty -> WHITE, filled -> the fitted
        /// attachment's own RARITY colour, straight from ItemTool.RarityColorUI so it matches the tile in the bag and
        /// the look-at rim on the ground. Pure so the mapping is testable without a gun, a viewmodel or a camera --
        /// none of which exist in the harness.</summary>
        internal static Color SlotColor(bool canAccept, ItemAsset installed)
        {
            if (!canAccept) return new Color(0.45f, 0.45f, 0.48f, 0.6f);   // grey, and dimmed: a dead slot should not draw the eye
            if (installed == null) return Colors.White;
            return ItemTool.RarityColorUI(installed.rarity);
        }

        void Refresh()
        {
            foreach (var slot in Slots)
            {
                bool canAccept = VM != null && VM.SlotHasModel(slot);
                int id = AttachmentFit.InstalledId(Player?.HeldItemForTest, slot);
                var asset = id >= 0 ? Assets.find((ushort)id) : null;
                // A slot can be attached without the gun's item recording an id (the --attach viewmodel harness, and
                // guns that ship with a fitted part). Falling back to the slot's own attached flag keeps those white
                // rather than reporting them empty; only a REAL id can produce a rarity colour.
                if (asset == null && canAccept && VM.SlotAttached(slot)) { _icons[slot].Modulate = Colors.White; continue; }
                _icons[slot].Modulate = SlotColor(canAccept, asset);
            }
            RebuildRings();   // the bag changed under the rings whenever anything is attached or detached
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
            LayoutRings();   // every ring tracks its own slot as the gun sways
        }

        public void Open()  { if (Visible) return; Visible = true;  VM?.EnterAttachView(); Refresh(); }
        public void Close() { if (!Visible) return; ClearRings(); Visible = false; VM?.ExitAttachView(); }
        public void Toggle() { if (Visible) Close(); else Open(); }
        public bool IsOpen => Visible;
    }
}
