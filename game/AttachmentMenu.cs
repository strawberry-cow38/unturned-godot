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
                if (slot == "Sight" && VM.IntegralSight) continue;   // aug: integral scope, no detachable/replaceable Sight slot (master)
                bool isMag = slot == "Magazine";
                // Loose-shell shotgun: the Magazine slot picks an AMMO TYPE (buckshot / slug), not a magazine item. A
                // pump/break shotgun has no magazine MESH, so this runs BEFORE the SlotHasModel guard that would skip it
                // (master: "in the attachment menu for the magazine slot show the relevant ammo types to load"). The ring
                // still positions -- TryGetSlotScreen uses a fixed Magazine hook offset every gun has. Clicking a type
                // selects + reloads it (ChooseShellType); pellets follow (slug=1, buckshot=6-8). The loaded type tints green.
                if (isMag && (Player?.CanChooseShellType ?? false))
                {
                    var shells = new System.Collections.Generic.List<Button>();
                    foreach (var (asset, count, selected) in Player.ShellTypeChoices())
                    {
                        var a = asset; ushort aid = (ushort)a.id;
                        shells.Add(AddOption(slot, count > 0 ? $"{PlayerController.PluralAmmo(a.itemName, count)} — {count}" : $"{a.itemName} — none", aid,
                            count > 0 ? () => { Player.ChooseShellType(aid); Refresh(); } : (System.Action)null,
                            tint: selected ? new Color(0.72f, 1f, 0.75f) : (Color?)null, rounds: count > 0 ? count : -1, vertical: true));
                    }
                    if (Player.HasLoadedShells)   // unload option, matching the radial's eject segment
                        shells.Add(AddOption(slot, "Unload", null, () => { Player.UnloadShells(); Refresh(); }, tint: new Color(1f, 0.62f, 0.56f)));
                    if (shells.Count > 0) _rings[slot] = shells;
                    continue;
                }
                if (!VM.SlotHasModel(slot)) continue;   // a slot this gun cannot take shows nothing at all -- its icon is already greyed
                var list = new System.Collections.Generic.List<Button>();
                // ONE ICON PER PHYSICAL ITEM (master: "if we have multiple of the same relevant item, show it that
                // many times in the orbit"). InBag collapses duplicates to (asset, count) because the old fan was a
                // text list where "x6" was the readable answer; a ring of icons has nowhere to put a multiplier, and
                // six magazines drawn six times is the point -- the ring IS the count.
                foreach (var (asset, item, _, _) in AttachmentFit.InBagInstances(Player?.Inventory, slot, caliber))
                {
                    var a = asset; var inst = item;
                    int rounds = isMag ? item.amount : -1;   // Item.amount IS the rounds left in THAT magazine
                    string mesh = AttachmentFit.MeshFor(a.id);
                    string sl = slot;
                    string label = rounds >= 0 ? $"{a.itemName} — {rounds} rounds" : a.itemName;
                    list.Add(AddOption(sl, label, a.id, () =>
                    {
                        if (!FitAttachment(sl, a.id, inst)) return;
                        // An attachment with no ripped mesh still ATTACHES -- it just renders nothing. Hiding those
                        // would hide most of the arsenal from a menu whose whole job is showing what you own.
                        if (mesh != null) VM.SetSlotMesh(sl, mesh);
                        else if (VM.SlotHasModel(sl)) VM.SetSlotAttached(sl, true);
                        Player?.PlaySelectorSwitchSound();   // attach click (source: shared firemode/selector sound)
                        Refresh();
                    }, rounds: rounds, vertical: isMag));
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
            if (slot == "Sight" && VM.IntegralSight) return false;   // aug: integral scope can't be detached (master)
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
            Player?.PlaySelectorSwitchSound();   // detach click (source: shared firemode/selector sound)
            Refresh();
            return true;
        }

        /// <summary>Fit `newId` into `slot`, consuming the exact instance the player clicked and returning
        /// whatever it displaces. Returns false, having changed NOTHING, if either half cannot be done.
        ///
        /// ORDER MATTERS AND USED TO BE WRONG. The old path gave the outgoing attachment back to the bag FIRST and
        /// consumed the incoming one second -- and the consume can legitimately fail ("gone from under us", a stale
        /// ring after the bag changed underneath). On that path the give-back had already happened and the early
        /// return left the slot untouched, so the attachment was on the gun AND back in the bag. One item, two
        /// places. Moving any item was enough to stale the ring and trigger it, and the next inventory refresh
        /// showed the extra copy, which is what it looked like from the outside: things reappearing after a move.
        ///
        /// ALWAYS returns the outgoing attachment, INCLUDING on a same-id swap (master: "should swap between the
        /// ones we select"). An older guard of `prev != a.id` skipped the give-back there while still consuming,
        /// which DESTROYED one instead of duplicating it -- the same ordering mistake pointing the other way.
        ///
        /// If the bag cannot take what we displaced, the consume is undone rather than the attachment destroyed,
        /// matching DetachSlot's rule: a full bag refuses the swap, it does not eat the difference.</summary>
        public bool FitAttachment(string slot, ushort newId, Item clicked)
        {
            bool ok = FitAttachmentTo(Player?.HeldItemForTest, Player?.Inventory, slot, newId, clicked, out var refusal);
            if (!ok && refusal != null) HUD.Alert(refusal);
            return ok;
        }

        /// <summary>The swap itself, over a bare (held item, inventory) pair rather than a live player.
        ///
        /// Static and parameterised ON PURPOSE. The old version lived inside the ring's click lambda, which is why
        /// the suite that covers this could not call it and re-implemented the sequence inline instead -- a test
        /// that mirrors the code it is checking agrees with it by construction, and this bug sat underneath one
        /// for as long as it existed. Now the test drives the real thing.</summary>
        public static bool FitAttachmentTo(Item held, PlayerInventory inv, string slot, ushort newId, Item clicked,
                                           out string refusal)
        {
            refusal = null;
            int prev = AttachmentFit.InstalledId(held, slot);
            if (!TakeFromBagInstanceIn(inv, clicked)) return false;   // consume THE ONE CLICKED, not any of its id
            if (prev >= 0 && inv != null && !inv.tryAddItem(new Item((ushort)prev)))
            {
                if (clicked != null) inv.tryAddItem(clicked);   // undo: never destroy what we took
                refusal = "No room to swap that — free a slot first";
                return false;
            }
            AttachmentFit.SetInstalledId(held, slot, newId);
            return true;
        }

        /// <summary>Remove the EXACT clicked object from `inv`, by reference. See TakeFromBagInstance.</summary>
        static bool TakeFromBagInstanceIn(PlayerInventory inv, Item want)
        {
            if (inv == null) return true;    // no bag (the --attach viewmodel harness): nothing to consume, still fit it
            if (want == null) return false;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (ReferenceEquals(pg.getItem(i)?.item, want)) { pg.removeItem(i); return true; }
            }
            return false;   // gone from under us -> fit nothing rather than an item the player no longer owns
        }

        /// <summary>Run the DETACH exactly as a slot click does. A test seam because the reported bug ("won't drag
        /// after I take them off a gun") is specific to items that came out of THIS path, and reproducing it by
        /// calling tryAddItem directly proved nothing: that passes.</summary>
        public bool DebugDetach(string slot) => DetachSlot(slot);

        // Remove ONE of `id` from the bag -- the attachment is now on the gun, so it must not still be in your
        // pockets. Scans the same page range the options came from; false = it wasn't there (a stale fan after the
        // bag changed underneath), in which case the caller does nothing rather than fitting an item you don't own.
        /// <summary>Remove the EXACT object the player clicked, matched by reference rather than by id or by the
        /// page/index it was found at. Ids are not unique enough -- two magazines of the same id hold different
        /// rounds, so taking "any one of that id" lets you click the full magazine and receive the empty one. Indices
        /// are not stable either: anything that removed an item since the ring was built shifts them.</summary>
        bool TakeFromBagInstance(Item want)
        {
            var inv = Player?.Inventory;
            if (inv == null) return true;    // no bag (the --attach viewmodel harness): nothing to consume, still fit it
            if (want == null) return false;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (ReferenceEquals(pg.getItem(i)?.item, want)) { pg.removeItem(i); return true; }
            }
            return false;   // gone from under us -> fit nothing rather than an item the player no longer owns
        }

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

        Button AddOption(string slot, string label, ushort? iconId, System.Action onPress, Color? tint = null,
                         int rounds = -1, bool vertical = false)
        {
            // Magazines stand UP (master: "with magazines, show the icon vertically"). A portrait box plus a
            // content-orientation fix in LoadItemIcon, because the source art is not consistently oriented -- of four
            // real magazine icons, two are drawn wide (ids 6, 17) and two tall (20, 98). Sizing the box alone would
            // letterbox half of them; rotating everything would lay the other half on its side.
            var sz = vertical ? new Vector2(OptSize * 0.78f, OptSize * 1.34f) : new Vector2(OptSize, OptSize);
            var b = new Button
            {
                CustomMinimumSize = sz,
                Size = sz,
                Icon = iconId.HasValue ? LoadItemIcon(iconId.Value, vertical) : null,
                ExpandIcon = true,
                TooltipText = label,
                Disabled = onPress == null,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            b.AddThemeConstantOverride("icon_max_width", (int)sz.X - 6);
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
            // ROUNDS IN EACH MAG (master). Drawn as an overlay label rather than Button.Text so it does not fight the
            // icon for the button's content box -- and it is per-INSTANCE, which is the whole reason the ring stopped
            // collapsing duplicates: two mags of the same id can hold different amounts.
            if (rounds >= 0)
            {
                var lbl = new Label
                {
                    Text = rounds.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,   // the label must not eat the click meant for the button
                    Size = new Vector2(sz.X, 14),
                    Position = new Vector2(0, sz.Y - 15),
                };
                lbl.AddThemeFontSizeOverride("font_size", 11);
                lbl.AddThemeColorOverride("font_color", Colors.White);
                lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
                lbl.AddThemeConstantOverride("outline_size", 4);   // readable over any icon art underneath
                b.AddChild(lbl);
            }
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
        static readonly System.Collections.Generic.Dictionary<(ushort, bool), Texture2D> _itemIcons = new();
        internal static Texture2D LoadItemIcon(ushort id, bool standUp = false)
        {
            if (_itemIcons.TryGetValue((id, standUp), out var hit)) return hit;
            string p = ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png");
            Texture2D tex = null;
            if (System.IO.File.Exists(p))
            {
                var img = Image.LoadFromFile(p);
                if (img != null)
                {
                    if (standUp && DrawnWiderThanTall(img)) { img.Rotate90(ClockDirection.Counterclockwise); img.FlipX(); }   // stand mags UP + un-mirror horizontally (master)
                    tex = ImageTexture.CreateFromImage(img);
                }
            }
            _itemIcons[(id, standUp)] = tex;
            return tex;
        }

        /// <summary>Is the icon's DRAWN CONTENT wider than it is tall? Measured from the alpha bounding box, not the
        /// canvas: real magazine icons are mostly 256x256 sheets with the art occupying a slice of it, so canvas
        /// aspect says "square" for both a magazine lying down and one standing up. Measured on the shipped files --
        /// id 6 is 246x126 of content, id 98 is 98x250 -- which is why this cannot be a per-id table either.</summary>
        static bool DrawnWiderThanTall(Image img)
        {
            int w = img.GetWidth(), h = img.GetHeight();
            int x0 = w, x1 = -1, y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (img.GetPixel(x, y).A > 0.03f)
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
            if (x1 < x0 || y1 < y0) return false;   // fully transparent: nothing to orient
            // > 1 alone misfires on the Military Drum (id 17): drawn UPRIGHT but its twin lobes span wide, so its content is
            // 1.36x wider than tall and it got wrongly stood-up (rotated 90). Measured all functional mag icons -- every one
            // is <=0.93 (tall) except the drum (1.36) and the genuinely lying-down STANAG (1.95). 1.65x sits dead-center of
            // that gap: only a clearly-elongated (lying-down) mag is stood up; an upright-but-wide drum is left alone (master).
            return (x1 - x0) > (y1 - y0) * 1.65f;
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
                if (slot == "Sight" && VM.IntegralSight) { btn.Visible = false; continue; }   // aug: integral scope -> no Sight slot icon at all (master)
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
