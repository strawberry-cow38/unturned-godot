using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Port of PlayerDashboardInventoryUI's inventory tab: the left CLOTHING column (the worn-item equip slots) and
    // the right storage area (the two hand slots + the grid pages), on the source's 50px cell (SleekItems: a page is
    // width*50 x height*50, an item sits at x*50,y*50 sized size_x*50 x size_y*50, +30px page header). Item tiles use
    // the real ItemTool rarity colours (dark rarity-tinted background + rarity border/name, like SleekItem's
    // BackgroundIfLight(rarityColorUI)). The whole dashboard is centred over the dimmed game; the model is PlayerInventory.
    public partial class InventoryUI : CanvasLayer
    {
        public PlayerInventory Inv;
        public PlayerController Player;   // for Use -> apply consumable effects to the vitals
        public PlayerClothingController Clothing;   // P5: equip/unequip drives BOTH worn-slot state AND the on-body visual (RiggedCharacter) through this controller

        const int CELL = 50;         // SleekItems cell size
        const int HEADER = 30;       // per-page header strip (source SizeOffset_Y = height*50 + 30)
        const int PAD = 12;
        const int CLOTHW = 190;      // clothing column width
        const int GUTTER = 24;       // gap between clothing column and storage
        const int NAVH = 46;         // top navbar strip (Inventory / Crafting / Skills / Information tabs) -- retail dashboard header
        const int NAMEH = 44;        // name + faction badge under the navbar (top-left)
        const int BODYTOP = NAVH + 10 + NAMEH + 12;   // y where the columns (clothing / storage) start, below the navbar + name
        const int NEARW = 6 * CELL;  // Nearby/proximity column width (6-wide grid) on the right
        // clothing PAPERDOLL (strawberry): a live 3D render of the worn character at the top of the clothing column.
        const int PDW = CLOTHW - 16; // paperdoll display width (174)
        const int PDH = 232;         // paperdoll display height
        const int PDTOP = 30;        // y of the paperdoll inside the clothing box (below the CLOTHING header)
        const int CLOTHH = PDTOP + PDH + 14 + 7 * (CELL + 10) + 10;   // header + paperdoll + 7 equip slots

        Control _root, _dash, _storageCol, _topBar, _nearbyCol;
        // clothing paperdoll: an isolated SubViewport (own world) renders a preview RiggedCharacter clothed off the SAME
        // inventory's worn slots (PlayerClothingController.Refresh is read-only), lit + framed by a camera. Built once;
        // Refresh() repaints its clothing; drag on its view spins it. Held weapon deferred (needs 3P gun anims).
        SubViewport _pdVp;
        Camera3D _pdCam;
        RiggedCharacter _pdBody;
        PlayerClothingController _pdClothing;
        float _pdYaw = 0.5f;         // spin offset from facing the camera (drag adjusts it)
        bool _pdDragging, _pdFramed;
        // each clothing equip slot carries its EItemType so a drop can be matched to it (shirt->SHIRT slot) and its worn
        // garment grabbed for a drag-out unequip. These Controls are the clothing drop targets (worn state lives in Inv.worn*,
        // NOT a page grid, so they can't go in _drop which is page-indexed -- they're hit-tested via PointToClothSlot).
        readonly List<(Control slot, Label label, System.Func<Item> worn, EItemType type)> _clothing = new();
        bool _open;
        float _storageW, _storageH;

        // drag-drop: registered drop zones (a page + the Control whose global rect maps to its cells) and the live drag
        readonly List<(byte page, Control ctl, bool isSlot)> _drop = new();
        bool _dragging;
        byte _dragPage, _dragX0, _dragY0, _dragRot;
        bool _dragFromCloth;          // the drag started on a clothing equip slot (the worn garment, not a page cell)
        EItemType _dragClothType;     // which clothing slot it was grabbed from (only meaningful when _dragFromCloth)
        ItemJar _dragJar;
        Vector2 _grab;          // cursor offset within the grabbed item's top-left cell
        Control _dragTile;      // the floating tile that follows the cursor

        // selection: clicking an item (press+release on its own cell, no drag) opens a description/actions panel
        Control _selPanel;
        byte _selPage, _selX, _selY;

        public bool IsOpen => _open;

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;

            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);

            _dash = new Control();
            _root.AddChild(_dash);

            BuildClothingColumn();
            _storageCol = new Control { Position = new Vector2(CLOTHW + GUTTER, BODYTOP) };
            _dash.AddChild(_storageCol);
            _topBar = new Control();   // navbar + name badge across the top (rebuilt at the dash width in Refresh)
            _dash.AddChild(_topBar);
            _nearbyCol = new Control();   // Nearby/proximity column on the right (positioned + filled in Refresh)
            _dash.AddChild(_nearbyCol);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; if (_pdVp != null) _pdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always; Refresh(); _lastSig = InventorySignature(); }
        public void Close() { _open = false; Visible = false; if (_pdVp != null) _pdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; }   // stop rendering the paperdoll while the bag is closed
        public void DebugSelect(byte page, byte x, byte y) { Open(); OpenSelection(page, x, y); }   // demo/verify only

        long _lastSig = -1;
        public override void _Process(double delta)
        {
            if (!_open) return;
            CenterDash();   // keep centred as the viewport settles
            _pdBody?.Tick(delta);   // advance the paperdoll's idle so it breathes instead of a frozen T-pose
            FramePaperdoll();       // one-time: aim + distance the camera at the rig's real bounds (needs it in-tree)
            // LIVE update (master): if the inventory changed in the background (e.g. a consume finishing while the bag's
            // open), rebuild the grid -- but NOT mid drag / selection, so it doesn't yank the item out from under you.
            if (!_dragging && _selPanel == null)
            {
                long sig = InventorySignature();
                if (sig != _lastSig) { _lastSig = sig; Refresh(); }
            }
        }

        // Cheap rolling hash of every jar (id/amount/pos) + the page dims (an MP crate open/close resizes
        // STORAGE with zero jars moving) -> detects any background change without rebuilding each frame.
        long InventorySignature()
        {
            if (Inv == null) return 0;
            long h = 1469598103934665603L;
            foreach (var pg in Inv.items)
            {
                h = (h ^ ((long)pg.width << 8 | pg.height)) * 1099511628211L;
                byte cnt = pg.getItemCount();
                for (byte i = 0; i < cnt; i++)
                {
                    var j = pg.getItem(i);
                    long v = ((long)j.item.id << 24) ^ ((long)j.item.amount << 8) ^ ((long)j.x << 4) ^ j.y;
                    h = (h ^ v) * 1099511628211L;
                }
            }
            return h;
        }

        // --- drag-drop: pick an item up on left-press, drop it on a cell (TryDrag = the ported move/swap), R rotates ---
        public override void _Input(InputEvent e)
        {
            if (!_open || Inv == null) return;
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    // clicks inside an open selection panel belong to its buttons -- let them through
                    if (_selPanel != null)
                    {
                        if (new Rect2(_selPanel.GlobalPosition, _selPanel.Size).HasPoint(mb.GlobalPosition)) return;
                        CloseSelection();   // clicked outside -> dismiss, then fall through to grab
                    }
                    if (!_dragging) { StartDrag(mb.GlobalPosition); GetViewport().SetInputAsHandled(); }
                }
                else if (_dragging) { Drop(mb.GlobalPosition); GetViewport().SetInputAsHandled(); }
            }
            else if (e is InputEventMouseButton rmb && rmb.ButtonIndex == MouseButton.Right && rmb.Pressed)
            {
                // RIGHT-click opens the item action menu (master: RMB only, not a left-click)
                CloseSelection();
                if (PointToCell(rmb.GlobalPosition, out byte page, out byte cx, out byte cy, out _, out _))
                {
                    byte idx = Inv.items[page].getIndex(cx, cy);
                    if (idx != byte.MaxValue) { var j = Inv.items[page].getItem(idx); OpenSelection(page, j.x, j.y); }
                }
                GetViewport().SetInputAsHandled();
            }
            else if (e is InputEventMouseMotion mm && _dragging)
            {
                _dragTile.GlobalPosition = mm.GlobalPosition - _grab;
            }
            else if (e is InputEventKey { Pressed: true, Keycode: Key.R } && _dragging)
            {
                _dragRot = (byte)(_dragRot ^ 1);   // toggle a 90-degree rotation of the held item
                RebuildDragTile();
                GetViewport().SetInputAsHandled();
            }
            else if (e is InputEventKey { Pressed: true } bk && _selPanel != null && bk.Keycode >= Key.Key3 && bk.Keycode <= Key.Key9)
            {
                // RMB'd an item (its selection panel is open) + 3-9 -> BIND that number key to equip this item (master)
                Player?.BindHotbar((int)bk.Keycode - (int)Key.Key0, _selPage, _selX, _selY);
                CloseSelection();
                GetViewport().SetInputAsHandled();
            }
        }

        void StartDrag(Vector2 global)
        {
            // grabbing a WORN garment off a clothing equip slot (the worn item lives in Inv.worn*, not a page) -> a drag-out
            // unequip if dropped on a grid, or a re-equip if dropped back on a slot. The tile is a transient jar over the item.
            if (PointToClothSlot(global, out int ci))
            {
                var wornIt = _clothing[ci].worn();
                if (wornIt == null) return;   // empty slot -> nothing to grab
                _dragFromCloth = true; _dragClothType = _clothing[ci].type;
                _dragJar = new ItemJar(wornIt); _dragRot = 0;
                _grab = global - _clothing[ci].slot.GlobalPosition;
                _dragging = true;
                RebuildDragTile();
                return;
            }
            if (!PointToCell(global, out byte page, out byte cx, out byte cy, out Control ctl, out bool isSlot)) return;
            var pg = Inv.items[page];
            byte idx = pg.getIndex(cx, cy);
            if (idx == byte.MaxValue) return;
            _dragFromCloth = false;
            _dragJar = pg.getItem(idx);
            _dragPage = page; _dragX0 = _dragJar.x; _dragY0 = _dragJar.y; _dragRot = _dragJar.rot;
            Vector2 itemTopLeft = ctl.GlobalPosition + (isSlot ? Vector2.Zero : new Vector2(_dragJar.x * CELL, _dragJar.y * CELL));
            _grab = global - itemTopLeft;
            _dragging = true;
            RebuildDragTile();
        }

        void RebuildDragTile()
        {
            _dragTile?.QueueFree();
            bool rot = _dragRot % 2 == 1;
            int w = (rot ? _dragJar.size_y : _dragJar.size_x) * CELL;
            int h = (rot ? _dragJar.size_x : _dragJar.size_y) * CELL;
            _dragTile = MakeTile(_dragJar, w, h, _dragRot);   // preview the LIVE rotation (R toggles _dragRot), so the icon spins as you turn it
            _dragTile.Modulate = new Color(1f, 1f, 1f, 0.8f);
            _dragTile.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(_dragTile);   // on top of the dashboard
            _dragTile.GlobalPosition = GetViewport().GetMousePosition() - _grab;
        }

        void Drop(Vector2 global)
        {
            byte sp = _dragPage, sx = _dragX0, sy = _dragY0, srot = _dragRot;
            bool fromCloth = _dragFromCloth; EItemType fromType = _dragClothType;
            _dragFromCloth = false;
            _dragging = false;
            _dragTile?.QueueFree(); _dragTile = null;
            // the held item's top-left lands where the cursor is minus the grab; +half a cell so it snaps to the nearest
            Vector2 topLeft = global - _grab + new Vector2(CELL / 2f, CELL / 2f);

            // TARGET is a clothing equip slot -> EQUIP (wear) the dropped item, if its type matches the slot (source:
            // PlayerDashboardInventoryUI.checkAction -> PlayerClothing.sendSwap<Slot>). A garment dragged from ANOTHER
            // slot (a type mismatch, since a slot only ever holds its own type) is rejected -> snaps home.
            // hit-test the clothing slots with the CURSOR (global), not the item top-left: the slots are small
            // (~40px) and you aim the cursor at them -- symmetric with StartDrag's grab. Using topLeft (offset
            // by the grab) made drops miss the slot, so equip silently failed while dequip (cursor-grab) worked.
            if (PointToClothSlot(global, out int tci))
            {
                if (fromCloth)
                {
                    // dropped a worn garment back onto its own slot -> no-op; onto a different-type slot -> just repaint (mismatch)
                    Refresh();
                    return;
                }
                WearFromGrid(_clothing[tci].type, sp, sx, sy);   // type-checked inside; a mismatch is a no-op -> snaps home
                CloseSelection();
                Refresh();
                return;
            }

            // ORIGIN was a clothing slot + it wasn't dropped on a slot -> UNEQUIP into the grid (source: dragging the worn
            // piece off the paperdoll -> sendSwap<Slot>(255,255,255) -> the garment forceAddItem's back to the inventory).
            if (fromCloth)
            {
                TakeOff(fromType);
                CloseSelection();
                Refresh();
                return;
            }

            if (!PointToCell(topLeft, out byte page, out byte x1, out byte y1, out _, out _)) return;
            if (page == sp && x1 == sx && y1 == sy) return;   // released in place -> no-op (the item menu is RMB now)
            // MP: the move is a REQUEST -- the server's TryDrag validates+applies and the owner echo
            // repaints (the item snaps home until it lands). SP: the direct local drag, unchanged.
            if (Player != null && Player.RequestMoveItem(sp, sx, sy, page, x1, y1, srot)) { CloseSelection(); Refresh(); }
            else if (Inv.TryDrag(sp, sx, sy, page, x1, y1, srot)) { CloseSelection(); Refresh(); }
        }

        // map a screen point to (page, cellX, cellY) over a registered drop zone
        bool PointToCell(Vector2 global, out byte page, out byte cx, out byte cy, out Control ctl, out bool isSlot)
        {
            foreach (var (p, c, slot) in _drop)
            {
                if (new Rect2(c.GlobalPosition, c.Size).HasPoint(global))
                {
                    page = p; ctl = c; isSlot = slot;
                    if (slot) { cx = 0; cy = 0; }
                    else
                    {
                        Vector2 local = global - c.GlobalPosition;
                        cx = (byte)Mathf.FloorToInt(local.X / CELL);
                        cy = (byte)Mathf.FloorToInt(local.Y / CELL);
                    }
                    return true;
                }
            }
            page = cx = cy = 0; ctl = null; isSlot = false; return false;
        }

        // hit-test a clothing equip slot (Hat/Shirt/... in the left column) under a screen point -> its index in _clothing
        bool PointToClothSlot(Vector2 global, out int idx)
        {
            for (int i = 0; i < _clothing.Count; i++)
                if (new Rect2(_clothing[i].slot.GlobalPosition, _clothing[i].slot.Size).HasPoint(global)) { idx = i; return true; }
            idx = -1; return false;
        }

        // --- clothing equip/unequip: the port of PlayerClothing.ReceiveSwap<Slot> + askWear<Slot>. Equip/unequip goes
        // through PlayerClothingController (Clothing) so it drives BOTH the worn-slot STATE (Inv.wear*) AND the on-body
        // VISUAL (RiggedCharacter). The previously-worn garment returns to the grid (source forceAddItem). ---

        // the item currently worn in a given clothing slot
        Item WornFor(EItemType t) => t switch
        {
            EItemType.HAT      => Inv?.wornHat,
            EItemType.GLASSES  => Inv?.wornGlasses,
            EItemType.MASK     => Inv?.wornMask,
            EItemType.SHIRT    => Inv?.wornShirt,
            EItemType.VEST     => Inv?.wornVest,
            EItemType.BACKPACK => Inv?.wornBackpack,
            EItemType.PANTS    => Inv?.wornPants,
            _ => null,
        };

        void WearVisual(Item it) { if (Clothing != null) Clothing.Wear(it); else Player?.WearClothing(it); }
        void UnwearVisual(EItemType t) { if (Clothing != null) Clothing.Unwear(t); else Player?.UnwearClothing(t); }

        // return a garment to the inventory grid (source: forceAddItem) -> auto-place in the first page with room, else drop it in the world
        void ReturnToGrid(Item it)
        {
            if (it == null || Inv == null) return;
            if (Inv.tryAddItem(it)) return;
            if (Player != null) Player.DropWorldItem(it, Player.GlobalPosition - Player.GlobalTransform.Basis.Z * 0.6f + Vector3.Up * 0.1f);
        }

        // WEAR the item at grid (page,x,y) into clothing slot `slotType` (must match the item's type). Mirrors
        // ReceiveSwap<Slot>Request(page,x,y): remove the item from the grid, wear it (state+visual, +bag-page resize),
        // then forceAddItem the previously-worn garment back to the grid. Returns true if it equipped.
        public bool WearFromGrid(EItemType slotType, byte page, byte x, byte y)
        {
            if (Inv == null || page >= Inv.items.Length) return false;
            var pg = Inv.items[page];
            byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return false;
            var jar = pg.getItem(idx);
            var asset = jar?.GetAsset();
            if (asset == null || asset.type != slotType) return false;   // reject a mismatched type (a hat onto the shirt slot)
            var item = jar.item;
            var old = WornFor(slotType);
            if (ReferenceEquals(old, item)) return false;                // already worn here -> nothing to do
            pg.removeItem(idx);                                          // out of the grid (source: inventory.removeItem)
            WearVisual(item);                                            // state + on-body visual (+ resize this slot's bag page)
            if (old != null && !ReferenceEquals(old, item)) ReturnToGrid(old);   // the displaced garment goes back to the grid
            return true;
        }

        // UNEQUIP clothing slot `slotType` -> clear its state+visual and drop the garment back into the grid. Mirrors
        // ReceiveSwap<Slot>Request(255,255,255): wear nothing; the old garment forceAddItem's to the inventory. Returns true if it removed something.
        public bool TakeOff(EItemType slotType)
        {
            var old = WornFor(slotType);
            if (old == null) return false;
            UnwearVisual(slotType);   // clears the worn slot + the on-body visual (+ resizes a bag page to 0x0)
            ReturnToGrid(old);
            return true;
        }

        // demo/verify seams (headless can't drive the mouse): run the SAME equip/unequip core the drop handler uses.
        public bool DebugWearFromGrid(EItemType slotType, byte page, byte x, byte y) { bool r = WearFromGrid(slotType, page, x, y); Refresh(); return r; }
        public bool DebugTakeOff(EItemType slotType) { bool r = TakeOff(slotType); Refresh(); return r; }

        /// <summary>Verify the REAL mouse gesture (not the WearFromGrid bypass): set up the drag from the grid
        /// item at (page,x,y) as StartDrag's grid branch does, then call the actual Drop() at the clothing
        /// slot's screen center -- exercising Drop's PointToClothSlot(global) hit-test. Returns true if the
        /// drop equipped it. This is the path the DebugWearFromGrid bypass could NOT cover (it caught the
        /// topLeft-vs-cursor hit-test regression that shipped equip broken in-game).</summary>
        public bool DebugDropGestureOnSlot(EItemType slotType, byte page, byte x, byte y, out bool layoutValid)
        {
            layoutValid = false;
            if (Inv == null || page >= Inv.items.Length) return false;
            var pg = Inv.items[page]; byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return false;
            int ci = _clothing.FindIndex(c => c.type == slotType);
            if (ci < 0) return false;
            var s = _clothing[ci].slot;
            layoutValid = s.Size.X > 1f && s.Size.Y > 1f;   // Control laid out (GlobalPosition/Size meaningful)
            if (!layoutValid) return false;
            _dragFromCloth = false; _dragJar = pg.getItem(idx);
            _dragPage = page; _dragX0 = _dragJar.x; _dragY0 = _dragJar.y; _dragRot = _dragJar.rot;
            _grab = Vector2.Zero; _dragging = true;
            Drop(s.GlobalPosition + s.Size * 0.5f);         // the actual Drop handler + the fixed cursor hit-test
            Refresh();
            return WornFor(slotType) != null;
        }

        // --- selection panel (openSelection): the item's big tile + name/info + Equip/Drop actions ---
        void OpenSelection(byte page, byte x, byte y)
        {
            CloseSelection();
            var pg = Inv.items[page];
            byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null) return;
            _selPage = page; _selX = x; _selY = y;

            var panel = new Panel { Size = new Vector2(500, 300) };
            StyleBox(panel, new Color(0.05f, 0.05f, 0.06f, 0.98f));
            _root.AddChild(panel);
            _selPanel = panel;
            Vector2 vp = GetViewport().GetVisibleRect().Size;
            panel.Position = new Vector2(Mathf.Round((vp.X - 500) / 2f), Mathf.Round((vp.Y - 300) / 2f));

            // left: the item's tile, fit into a 200x280 icon box
            bool rot = jar.rot % 2 == 1;
            int iw = (rot ? jar.size_y : jar.size_x) * CELL, ih = (rot ? jar.size_x : jar.size_y) * CELL;
            float scale = Mathf.Min(Mathf.Min(200f / iw, 280f / ih), 2f);
            var iconBox = new Control { Position = new Vector2(10, 10), Size = new Vector2(200, 280) };
            panel.AddChild(iconBox);
            var tile = MakeTile(jar, iw, ih);
            tile.Scale = new Vector2(scale, scale);
            tile.Position = new Vector2((200 - iw * scale) / 2f, (280 - ih * scale) / 2f);
            iconBox.AddChild(tile);

            // right-top: name (rarity-coloured) + info line
            Color rar = ItemTool.RarityColorUI(asset.rarity);
            var name = new Label { Text = asset.itemName, Position = new Vector2(228, 14), Size = new Vector2(258, 28) };
            name.AddThemeColorOverride("font_color", rar);
            name.AddThemeFontSizeOverride("font_size", 19);
            panel.AddChild(name);
            var info = new Label { Text = $"{asset.rarity}  ·  {asset.type}  ·  {asset.size_x}x{asset.size_y}",
                                   Position = new Vector2(228, 46), Size = new Vector2(258, 20) };
            info.AddThemeColorOverride("font_color", rar.Lerp(new Color(0.6f, 0.6f, 0.62f), 0.5f));
            info.AddThemeFontSizeOverride("font_size", 12);
            panel.AddChild(info);
            // the real localized Description (from the item's English.dat)
            var desc = new Label { Text = asset.description, Position = new Vector2(228, 72), Size = new Vector2(258, 70) };
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            desc.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.8f));
            desc.AddThemeFontSizeOverride("font_size", 13);
            panel.AddChild(desc);
            // a fluid CONTAINER shows its live contents (the "tooltip" strawberry wanted back): type + amount + capacity
            if (asset.IsFluidContainer && jar.item != null)
            {
                FluidItem.Read(jar.item, asset, out var ct, out var ca, out var cq);
                string contents = (ca <= 0.001f || ct == FluidType.None)
                    ? $"Contents: empty  ·  holds {FluidDef.Litres(asset.fluidCapacity)}"
                    : $"Contents: {FluidDef.Litres(ca)} {FluidDef.WaterName(ct, cq)}  ·  of {FluidDef.Litres(asset.fluidCapacity)}";
                var ccol = ct == FluidType.None ? new Color(0.72f, 0.74f, 0.77f) : FluidDef.WaterColor(ct, cq).Lerp(Colors.White, 0.3f);
                var cl = new Label { Text = contents, Position = new Vector2(228, 120), Size = new Vector2(258, 22) };
                cl.AddThemeColorOverride("font_color", ccol);
                cl.AddThemeFontSizeOverride("font_size", 13);
                panel.AddChild(cl);
            }
            // a FOOD item shows its CONDITION (freshness) as a % coloured red->yellow->green (source getQualityColor);
            // under the sick threshold it's flagged spoiled -- eating it feeds you less + raises infection (FoodSpoil).
            if (asset.type == EItemType.FOOD && jar.item != null)
            {
                int q = jar.item.quality;
                string tag = q < FoodSpoil.SickThreshold ? "  ·  spoiled" : q >= 90 ? "  ·  fresh" : "";
                var fl = new Label { Text = $"Condition: {q}%{tag}", Position = new Vector2(228, 120), Size = new Vector2(258, 22) };
                fl.AddThemeColorOverride("font_color", ItemTool.QualityColor(q / 100f).Lerp(Colors.White, 0.3f));   // brighten so the spoiled (dark-red) end stays legible on the dark panel
                fl.AddThemeFontSizeOverride("font_size", 13);
                panel.AddChild(fl);
            }

            // right-bottom: actions. ONE state-aware hand button (strawberry): if this item is the one in hand
            // -> "Dequip" (back to fists); else "Equip" (gun/melee/deployable) or "Hold" (consumable). Then Drop + Close.
            float by = 150;
            bool isDeploy = DeployableDef.ById(asset.id) != null;   // generator/spotlight -> equip into placement mode
            ToolDef tool = ToolDef.ById(asset.id);   // held tools (Wire 65 / Rope 64 / future) -> equip into that tool's mode. Data-driven: was `id == 65`, so the ROPE had no branch and its menu showed only Drop/Close (master: "the option to hold is NOT THERE")
            if (HasHandAction(asset))
            {
                if (Player != null && Player.IsHeld(asset, jar.item))
                    AddActionButton(panel, "Dequip", new Vector2(228, by), () => { Player?.Dequip(); CloseSelection(); });
                else if (asset.IsFluidContainer)
                    AddActionButton(panel, "Hold", new Vector2(228, by), HoldFluidSelected);   // a bottle/canteen: hold as a CONTAINER (LMB sip, RMB fill) -- BEFORE IsConsumable, since water-type containers are also consumable
                else if (asset.IsConsumable)
                    AddActionButton(panel, "Hold", new Vector2(228, by), HoldSelected);   // hold it in-hand -> LMB to eat/drink
                else if (asset.IsFuelContainer)
                    AddActionButton(panel, "Hold", new Vector2(228, by), HoldFuelSelected);   // equip the gas can -> LMB pours into a gen/vehicle, RMB sucks from a pump
                else if (isDeploy)
                    AddActionButton(panel, "Equip", new Vector2(228, by), PlaceSelected);   // equip the deployable -> close inventory, aim the ghost, LMB plants it
                else if (tool != null)
                    AddActionButton(panel, "Equip", new Vector2(228, by), ToolSelected);   // wire -> wiring mode, rope -> tow mode, any ToolDef -> its mode
                else
                    AddActionButton(panel, "Equip", new Vector2(228, by), EquipSelected);
                by += 44;
            }
            if (asset.IsFuelContainer)   // a gas can gets an extra "Empty" action -> dump its fuel (master)
            { AddActionButton(panel, "Empty", new Vector2(228, by), EmptyFuelSelected); by += 44; }
            if (asset.IsFluidContainer && jar.item != null)   // a fluid container: toggle autodrink (default on -> passive 50 mL sips of safe liquid)
            { AddActionButton(panel, jar.item.autoDrink ? "Autodrink: ON" : "Autodrink: OFF", new Vector2(228, by), ToggleAutoDrinkSelected); by += 44; }
            AddActionButton(panel, "Drop", new Vector2(228, by), DropSelected); by += 44;
            AddActionButton(panel, "Close", new Vector2(228, by), CloseSelection);
        }

        void CloseSelection() { _selPanel?.QueueFree(); _selPanel = null; }

        void AddActionButton(Control parent, string text, Vector2 pos, System.Action onClick)
        {
            var b = new Button { Text = text, Position = pos, Size = new Vector2(258, 36) };
            b.Pressed += onClick;
            parent.AddChild(b);
        }

        // The holdable predicate -- the single source of truth for "does this item get a hand action (Equip/Hold/Dequip)
        // in its menu?" Centralized + data-driven so an item can't have the equip code but NO menu option to reach it
        // (master 2026-07-20: the Rope did exactly that -- holdable in code, but its item menu showed only Drop/Close).
        // A new holdable type is added HERE + the button dispatch in openSelection; regressed by InventoryTests.HandActions.
        public static bool HasHandAction(ItemAsset asset) =>
            asset != null && (asset.gunName != null || asset.meleeName != null || asset.IsConsumable
                || DeployableDef.ById(asset.id) != null || ToolDef.ById(asset.id) != null || asset.IsFuelContainer || asset.IsFluidContainer);

        void EquipSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var asset = pg.getItem(idx).GetAsset();
            if (asset?.gunName != null) Player?.EquipHeldGun(asset.gunName, pg.getItem(idx).item);   // equipping a gun makes it the held weapon; the item carries its saved ammo/firemode/mag (master)
            else if (asset?.meleeName != null) Player?.EquipHeldMelee(asset.meleeName);   // a melee weapon -> the melee viewmodel + weapon-specific swings
            // holster a grid gun into the first empty hand slot; an already-slotted gun just stays put.
            // MP: the slot pick is computed on the mirrored grid and the server runs the same TryDrag
            // (the echo re-seats the jar); the in-hand equip above stays local either way.
            if (_selPage >= PlayerInventory.SLOTS)
                for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
                    if (Inv.items[slot].getItemCount() == 0)
                    {
                        if (Player == null || !Player.RequestEquipItem(_selPage, _selX, _selY, slot))
                            Inv.TryDrag(_selPage, _selX, _selY, slot, 0, 0, 0);
                        break;
                    }
            CloseSelection();
            Refresh();
        }

        // Equip a consumable INTO the hands (like a gun) -> close the inventory so LMB begins eating/drinking.
        // The item is NOT spent here; it's decremented when the eat/drink actually completes (PlayerController.TickConsume).
        void HoldSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var asset = pg.getItem(idx).GetAsset();
            if (asset == null || !asset.IsConsumable) return;
            string mesh = ConsumableRegistry.Mesh(asset.id);   // id -> held-mesh name (content/<mesh>.txt); null = no ripped mesh
            Player?.EquipHeldConsumable(asset, mesh);
            CloseSelection();
            Close();   // leave the inventory so the player can click to eat/drink
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // Equip a gas can INTO the hands -> close the inventory. LMB pours it into a gen/vehicle, RMB sucks from a pump.
        void HoldFuelSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || !asset.IsFuelContainer) return;
            Player?.EquipHeldFuelCan(asset, jar.item);
            CloseSelection();
            Close();   // leave the inventory so LMB pours / RMB sucks
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        void HoldFluidSelected()   // hold a fluid CONTAINER (bottle/canteen): LMB sips clean water/soda/…, RMB fills from a tank
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || !asset.IsFluidContainer) return;
            Player?.EquipHeldFluidContainer(asset, jar.item);
            CloseSelection();
            Close();   // leave the inventory so LMB sips / RMB fills
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // Dump a gas can's contents (master): set its fuel to 0. Works from the bag or while held.
        void EmptyFuelSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || !asset.IsFuelContainer || jar.item == null) return;
            jar.item.fuelLevel = 0f;
            Refresh();
        }

        void ToggleAutoDrinkSelected()   // flip a fluid container's autodrink; reopen the panel so the button label updates
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || !asset.IsFluidContainer || jar.item == null) return;
            jar.item.autoDrink = !jar.item.autoDrink;
            OpenSelection(_selPage, _selX, _selY);   // re-render with the new ON/OFF label
        }

        // Equip a deployable (generator/spotlight) -> close the inventory so the player aims the placement ghost and
        // LMB plants it. The item is NOT spent here; it's decremented when a placement actually lands (TickDeploy).
        void PlaceSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var def = DeployableDef.ById(jar.GetAsset()?.id ?? 0);
            if (def == null) return;
            Player?.EquipHeldDeployable(def, jar.item);
            CloseSelection();
            Close();   // leave the inventory so the player can aim + click to place
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // Equip a held TOOL into the hands -> close the inventory so the tool's mode is active (Wire -> wiring, Rope -> towing).
        // Data-driven off ToolDef (master: "is there no standard holdable flag / are u hard coding holds for each thing"):
        // a new held tool is a ToolDef entry + this one method, NOT another per-id branch here.
        void ToolSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var tool = ToolDef.ById(jar.GetAsset()?.id ?? 0);
            if (tool == null) return;
            Player?.EquipTool(tool, jar.item);
            CloseSelection();
            Close();
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // demo/verify: select an item and immediately run its Equip (headless can't click the button)
        public void DebugEquip(byte page, byte x, byte y) { _selPage = page; _selX = x; _selY = y; EquipSelected(); }
        // demo/verify: select a consumable and equip it to the hands (headless can't click the button)
        public void DebugHold(byte page, byte x, byte y) { _selPage = page; _selX = x; _selY = y; HoldSelected(); }

        public void DebugTool(byte page, byte x, byte y) { _selPage = page; _selX = x; _selY = y; ToolSelected(); }   // rope/wire equip via the menu path (regression: the rope had no menu Hold option)
        // demo/verify: select a consumable and run its Use (the Use button) headlessly -- drives the same
        // UseSelected core, so an L1 can prove the consume routes NetConsume + skips the local decrement.
        public void DebugUse(byte page, byte x, byte y) { _selPage = page; _selX = x; _selY = y; UseSelected(); }

        void DropSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx != byte.MaxValue)
            {
                var jar = pg.getItem(idx); var item = jar.item;
                bool wasHeld = Player != null && Player.IsHeld(jar.GetAsset(), item);   // dropping the HELD item -> go unarmed (strawberry)
                if (Player != null && Player.RequestDropItem(_selPage, _selX, _selY))
                {   // MP: the server removes the jar + tosses the world item (the echo empties the cell,
                    // the item puppet renders the drop); the hand state below is client-local either way
                }
                else
                {
                    pg.removeItem(idx);
                    if (Player != null && item != null)   // spawn it in the world just in front of the player
                        Player.DropWorldItem(item, Player.GlobalPosition - Player.GlobalTransform.Basis.Z * 0.6f + Vector3.Up * 0.1f);
                }
                if (wasHeld) Player?.EquipUnarmed();
            }
            CloseSelection();
            Refresh();
        }

        // Use a consumable: apply its effects to the player's vitals, then consume the item
        void UseSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            Player?.Consume(jar.GetAsset(), jar.item?.quality ?? 100);   // pass the eaten instance's CONDITION -> moldy-food penalty (vitals stay client-led; HP server-adopted, food/water local)
            // MP: the DELETE is a REQUEST -- the server removes by id (the cell just names one) and the owner
            // echo repaints the decremented grid, so SKIP the local decrement (mirror DropSelected @558-567 /
            // TickConsume @1038-1050). SP: the direct local decrement, unchanged.
            if (Player != null && Player.RequestConsume(_selPage, _selX, _selY))
            {   // server owns the delete; the owner echo empties/decrements the cell
            }
            else
            {
                var item = jar.item;
                if (item != null && item.amount > 1) item.amount--;   // consume one from the stack
                else pg.removeItem(idx);                              // or the whole item
            }
            CloseSelection();
            Refresh();
        }

        // left column: the equip slots (hat/glasses/mask/shirt/vest/backpack/pants), each showing the worn item
        void BuildClothingColumn()
        {
            var box = new Panel { Position = new Vector2(0, BODYTOP), Size = new Vector2(CLOTHW, CLOTHH) };
            StyleBox(box, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            _dash.AddChild(box);
            box.AddChild(Header("CLOTHING", new Vector2(10, 8), CLOTHW - 20));

            BuildPaperdoll(box);   // the 3D worn-character render at the top of the column

            (string name, System.Func<Item> worn, EItemType type)[] rows =
            {
                ("Hat",      () => Inv?.wornHat,      EItemType.HAT),      ("Glasses",  () => Inv?.wornGlasses,  EItemType.GLASSES),
                ("Mask",     () => Inv?.wornMask,     EItemType.MASK),     ("Shirt",    () => Inv?.wornShirt,    EItemType.SHIRT),
                ("Vest",     () => Inv?.wornVest,     EItemType.VEST),     ("Backpack", () => Inv?.wornBackpack, EItemType.BACKPACK),
                ("Pants",    () => Inv?.wornPants,    EItemType.PANTS),
            };
            float y = PDTOP + PDH + 14;   // stack the equip slots BELOW the paperdoll
            foreach (var (name, worn, type) in rows)
            {
                var slot = new Panel { Position = new Vector2(12, y), Size = new Vector2(CELL, CELL) };
                StyleBox(slot, new Color(0f, 0f, 0f, 0.5f));
                box.AddChild(slot);
                var lbl = new Label { Text = name, Position = new Vector2(CELL + 14, y + 14) };
                lbl.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.75f));
                box.AddChild(lbl);
                _clothing.Add((slot, lbl, worn, type));
                y += CELL + 10;
            }
        }

        // Build the 3D paperdoll: a dark stage + an isolated SubViewport rendering a preview character clothed off the
        // player's worn slots, surfaced in a SubViewportContainer you can drag to spin. Built once (BuildClothingColumn runs once).
        void BuildPaperdoll(Panel box)
        {
            var stage = new Panel { Position = new Vector2(8, PDTOP), Size = new Vector2(PDW, PDH) };
            StyleBox(stage, new Color(0.05f, 0.06f, 0.08f, 0.95f));   // dark backdrop so the character reads against it
            box.AddChild(stage);

            // a SubViewportContainer shows the viewport clipped to the stage. Stretch off -> the viewport keeps its own
            // LOCKED size (so the render aspect is deterministic regardless of layout timing). Dragging its surface spins the rig.
            var vpc = new SubViewportContainer
            {
                Position = new Vector2(8, PDTOP), Size = new Vector2(PDW, PDH),
                Stretch = false, MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "drag to rotate",
            };
            vpc.GuiInput += PaperdollDrag;
            box.AddChild(vpc);
            _pdVp = new SubViewport
            {
                Size = new Vector2I(PDW, PDH),                        // LOCK the render aspect (0.75 portrait)
                OwnWorld3D = true,                                    // isolated from the game world (like the viewmodel)
                TransparentBg = true,
                Msaa3D = Viewport.Msaa.Msaa4X,                        // antialias the character edges
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
            };
            vpc.AddChild(_pdVp);

            // straight-on camera; its exact distance/height is computed from the rig's real AABB in _Process once it's
            // in-tree (FramePaperdoll) -- avoids guessing at the mesh's origin/height. A rough start avoids a bad first frame.
            _pdCam = new Camera3D { Fov = 34f, Current = true, Position = new Vector3(0f, 0.98f, 3.8f) };
            _pdVp.AddChild(_pdCam);

            _pdVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25f, 155f, 0f), LightEnergy = 1.2f });                                          // key
            _pdVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-8f, -35f, 0f), LightEnergy = 0.55f, LightColor = new Color(0.78f, 0.84f, 1f) }); // cool fill (the world env doesn't reach an isolated SubViewport)
            _pdVp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.52f, 0.56f), AmbientLightEnergy = 1.0f,
                    TonemapMode = Godot.Environment.ToneMapper.Aces,
                },
            });

            _pdBody = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));   // same rig + skin as the live 3P body
            if (_pdBody != null)
            {
                _pdVp.AddChild(_pdBody);
                _pdBody.Rotation = new Vector3(0f, Mathf.Pi + _pdYaw, 0f);   // face the camera (rig forward is -Z; the cam sits at +Z)
                _pdBody.PlayLoop("Idle_Stand");
                if (Inv != null) { _pdClothing = new PlayerClothingController(_pdBody, Inv); _pdClothing.Refresh(); }   // Inv may not be wired yet -> lazily created in Refresh()
            }
        }

        // One-time deterministic frame: read the rig's actual world AABB (only valid once in-tree) and set the camera's
        // height + distance to fit the character's full HEIGHT (ignoring the wide bind-pose arm span) with a margin. This
        // SubViewport's effective vertical extent runs ~15% tighter than the FOV math with a slight upward bias (measured
        // off the render) -> pad + drop the aim so the WHOLE body fits. LookAt needs the node in-tree -> done here.
        void FramePaperdoll()
        {
            if (_pdFramed || _pdCam == null || _pdBody?.Body == null || !_pdBody.Body.IsInsideTree()) return;
            var mi = _pdBody.Body;
            Aabb ab = mi.GlobalTransform * mi.GetAabb();          // world-space bounds of the body mesh
            if (ab.Size.Y < 0.1f) return;                         // not skinned/built yet -> wait a frame
            float cy = ab.Position.Y + ab.Size.Y * 0.5f;          // vertical centre of the body
            float frameH = ab.Size.Y * 1.36f;                     // ~85% fill after the tightening
            float dist = frameH * 0.5f / Mathf.Tan(Mathf.DegToRad(_pdCam.Fov * 0.5f));
            float aimY = cy - 0.15f;
            _pdCam.Position = new Vector3(0f, aimY, dist);
            _pdCam.LookAt(new Vector3(0f, aimY, 0f), Vector3.Up);
            _pdFramed = true;
        }

        // Drag left/right on the paperdoll to spin the character (source has a rotation slider; a drag is the same intent).
        void PaperdollDrag(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left) _pdDragging = mb.Pressed;
            else if (e is InputEventMouseMotion mm && _pdDragging && _pdBody != null)
            {
                _pdYaw -= mm.Relative.X * 0.012f;
                _pdBody.Rotation = new Vector3(0f, Mathf.Pi + _pdYaw, 0f);
            }
        }

        public void Refresh()
        {
            if (Inv == null || _storageCol == null) return;
            CloseSelection();   // the panel points at a specific item; drop it when the layout rebuilds

            // repaint the paperdoll's worn clothing off the current slots (any inventory change can wear/unwear)
            if (_pdClothing == null && _pdBody != null) _pdClothing = new PlayerClothingController(_pdBody, Inv);   // Inv wasn't ready at build time
            _pdClothing?.Refresh();

            // worn clothing into the equip slots
            foreach (var (slot, lbl, worn, _) in _clothing)
            {
                foreach (Node c in slot.GetChildren()) c.QueueFree();
                var it = worn();
                if (it != null)
                {
                    var t = MakeTile(new ItemJar(it), CELL, CELL);
                    t.Position = Vector2.Zero;
                    slot.AddChild(t);
                    lbl.Text = it.GetAsset()?.itemName ?? lbl.Text;
                }
            }

            // storage side
            foreach (Node c in _storageCol.GetChildren()) c.QueueFree();
            _drop.Clear();
            float y = 0;
            _storageW = 5 * CELL;
            y = AddSlot("PRIMARY", 0, y);
            y = AddSlot("SECONDARY", 1, y);
            (byte page, string name)[] grids =
            {
                (PlayerInventory.STORAGE, "CRATE"),   // shown only when a storage crate is open (size > 0)
                (2, "POCKETS"), (PlayerInventory.BACKPACK, "BACKPACK"), (PlayerInventory.VEST, "VEST"),
                (PlayerInventory.SHIRT, "SHIRT"), (PlayerInventory.PANTS, "PANTS"),
            };
            foreach (var (page, name) in grids)
            {
                var pg = Inv.items[page];
                if (pg.width == 0 || pg.height == 0) continue;
                _storageW = Mathf.Max(_storageW, pg.width * CELL);
                y = AddGrid(name, pg, y);
            }
            _storageH = y;

            _nearbyCol.Position = new Vector2(CLOTHW + GUTTER + _storageW + GUTTER, BODYTOP);   // Nearby column to the RIGHT of storage
            BuildNearby();
            BuildTopBar(CLOTHW + GUTTER + _storageW + GUTTER + NEARW);   // navbar + name span the whole dashboard (incl. Nearby)
            CenterDash();
        }

        void BuildTopBar(float w)
        {
            if (_topBar == null) return;
            foreach (Node c in _topBar.GetChildren()) c.QueueFree();
            // navbar tabs (retail: Inventory / Crafting / Skills / Information) -- Inventory highlighted as the current tab
            string[] tabs = { "Inventory", "Crafting", "Skills", "Information" };
            float tw = (w - (tabs.Length - 1) * 6) / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = new Panel { Position = new Vector2(i * (tw + 6), 0), Size = new Vector2(tw, NAVH) };
                StyleBox(tab, i == 0 ? new Color(0.22f, 0.34f, 0.52f, 0.95f) : new Color(0.10f, 0.13f, 0.19f, 0.9f));
                _topBar.AddChild(tab);
                var l = new Label { Text = tabs[i], Position = new Vector2(0, 12), Size = new Vector2(tw, 24), HorizontalAlignment = HorizontalAlignment.Center };
                l.AddThemeColorOverride("font_color", new Color(0.86f, 0.89f, 0.93f));
                tab.AddChild(l);
            }
            // name + faction badge, top-left under the navbar
            var badge = new Panel { Position = new Vector2(0, NAVH + 10), Size = new Vector2(CLOTHW, NAMEH) };
            StyleBox(badge, new Color(0.10f, 0.13f, 0.19f, 0.92f));
            _topBar.AddChild(badge);
            var nm = new Label { Text = "Survivor", Position = new Vector2(12, 5), Size = new Vector2(CLOTHW - 20, 20) };
            nm.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
            badge.AddChild(nm);
            var fac = new Label { Text = "Neutral", Position = new Vector2(12, 24), Size = new Vector2(CLOTHW - 20, 16) };
            fac.AddThemeColorOverride("font_color", new Color(0.58f, 0.6f, 0.64f));
            badge.AddChild(fac);
        }

        void CenterDash()   // LEFT-anchored + SCALED to fill the screen (retail dashboard is large, not a small centered blob)
        {
            Vector2 vp = GetViewport().GetVisibleRect().Size;
            float contentW = CLOTHW + GUTTER + _storageW + GUTTER + NEARW;
            float contentH = BODYTOP + Mathf.Max(CLOTHH, _storageH);
            float s = Mathf.Clamp(Mathf.Min(vp.X * 0.72f / contentW, vp.Y * 0.92f / contentH), 1.0f, 1.75f);
            _dash.Scale = new Vector2(s, s);
            _dash.Position = new Vector2(Mathf.Round(vp.X * 0.03f), Mathf.Round(vp.Y * 0.04f));
        }

        void BuildNearby()   // Nearby/proximity column on the right -- a "Nearby" header + a grid (proximity population is a follow-up; this is the retail LAYOUT slot)
        {
            if (_nearbyCol == null) return;
            foreach (Node c in _nearbyCol.GetChildren()) c.QueueFree();
            _nearbyCol.AddChild(Header("Nearby", new Vector2(0, 0), NEARW));
            var grid = new GridPanel { Cells = new Vector2I(6, 5), Cell = CELL, Position = new Vector2(0, HEADER - 6), Size = new Vector2(NEARW, 5 * CELL) };
            _nearbyCol.AddChild(grid);
        }

        float AddSlot(string name, byte page, float y)
        {
            var pg = Inv.items[page];
            _storageCol.AddChild(Header(name, new Vector2(0, y), 5 * CELL));
            y += HEADER - 6;
            var box = new Panel { Position = new Vector2(0, y), Size = new Vector2(5 * CELL, CELL) };
            StyleBox(box, new Color(0f, 0f, 0f, 0.45f));
            _storageCol.AddChild(box);
            _drop.Add((page, box, true));
            if (pg.getItemCount() > 0)
            {
                var tile = MakeTile(pg.getItem(0), 5 * CELL, CELL);
                tile.Position = Vector2.Zero;
                box.AddChild(tile);
            }
            return y + CELL + PAD;
        }

        float AddGrid(string name, Items page, float y)
        {
            _storageCol.AddChild(Header($"{name}  {page.width}x{page.height}", new Vector2(0, y), page.width * CELL));
            y += HEADER - 6;
            var grid = new GridPanel { Cells = new Vector2I(page.width, page.height), Cell = CELL,
                                       Position = new Vector2(0, y), Size = new Vector2(page.width * CELL, page.height * CELL) };
            _storageCol.AddChild(grid);
            _drop.Add((page.page, grid, false));
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var jar = page.getItem(i);
                bool rotated = jar.rot % 2 == 1;
                int w = (rotated ? jar.size_y : jar.size_x) * CELL;
                int h = (rotated ? jar.size_x : jar.size_y) * CELL;
                var tile = MakeTile(jar, w, h);
                tile.Position = new Vector2(jar.x * CELL, jar.y * CELL);
                grid.AddChild(tile);
            }
            return y + page.height * CELL + PAD;
        }

        // real ground-truth item icons (the game's Extras/Icons, matched by id + downscaled) -> content/items/icons/<id>.png.
        // SleekItem draws the rendered item ICON on the rarity tile, not a name -> load once, cache, fall back to the name label.
        static readonly Dictionary<int, Texture2D> _iconCache = new();
        static Texture2D Icon(int id)
        {
            if (_iconCache.TryGetValue(id, out var t)) return t;
            t = null;
            var p = ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png");
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) t = ImageTexture.CreateFromImage(img); }
            _iconCache[id] = t;
            return t;
        }

        // one item tile: dark rarity-tinted background + rarity border + real ICON (name fallback) + amount badge
        Control MakeTile(ItemJar jar, int w, int h, int rotParam = -1)
        {
            var asset = jar.GetAsset();
            bool rotated = ((rotParam >= 0 ? rotParam : jar.rot) % 2) == 1;   // drawn rotated? (the drag preview passes the live _dragRot)
            Color rar = asset != null ? ItemTool.RarityColorUI(asset.rarity) : Colors.White;
            Color bg = new Color(rar.R * 0.22f, rar.G * 0.22f, rar.B * 0.22f, 0.97f);   // BackgroundIfLight(rarity)

            var tile = new Panel { Size = new Vector2(w, h), ClipContents = true };
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = rar };
            sb.SetBorderWidthAll(2);
            tile.AddThemeStyleboxOverride("panel", sb);

            var tex = asset != null ? Icon(asset.id) : null;
            if (tex != null)   // the real item icon fills the tile (like SleekItem's rendered item image)
            {
                var ic = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                ic.MouseFilter = Control.MouseFilterEnum.Ignore;
                int pad = (int)(CELL * 0.12f);   // breathing room around every icon inside its cell(s) (master: pad the icons)
                if (rotated)   // SleekItemIcon.rot spins the icon with the jar (internalImage.RotationAngle = rot*90). Draw it at its
                {              // NATURAL un-rotated (h-2pad) x (w-2pad) box (KeepAspect), then turn 90 clockwise and re-centre in the w x h tile.
                    float a = h - 2 * pad, b = w - 2 * pad;
                    ic.Size = new Vector2(a, b);
                    ic.PivotOffset = new Vector2(a / 2f, b / 2f);
                    ic.RotationDegrees = 90f;
                    ic.Position = new Vector2((w - a) / 2f, (h - b) / 2f);
                }
                else
                {
                    ic.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    ic.SetOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, pad);   // inset by pad on all sides
                }
                tile.AddChild(ic);
            }
            else   // no icon on disk -> the old rarity-tinted name label
            {
                var lbl = new Label { Text = asset?.itemName ?? "?" };
                lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                lbl.HorizontalAlignment = HorizontalAlignment.Center;
                lbl.VerticalAlignment = VerticalAlignment.Center;
                lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                lbl.AddThemeColorOverride("font_color", rar.Lerp(Colors.White, 0.35f));
                lbl.AddThemeFontSizeOverride("font_size", w <= CELL ? 9 : 12);
                lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                tile.AddChild(lbl);
            }

            if (jar.item != null && (jar.item.amount > 1 || asset?.IsMagazine == true))   // stacks show >1; a magazine ALWAYS shows its round count, incl. x0 when empty (master)
            {
                var amt = new Label { Text = "x" + jar.item.amount, Position = new Vector2(0, h - 20), Size = new Vector2(w - 4, 18) };
                amt.HorizontalAlignment = HorizontalAlignment.Right;
                amt.AddThemeColorOverride("font_color", Colors.White);
                amt.AddThemeColorOverride("font_outline_color", Colors.Black);
                amt.AddThemeConstantOverride("outline_size", 3);
                amt.AddThemeFontSizeOverride("font_size", 13);
                amt.MouseFilter = Control.MouseFilterEnum.Ignore;
                tile.AddChild(amt);
            }

            if (asset?.IsFuelContainer == true && jar.item != null)   // a gas can ALWAYS shows a fuel-level bar on its icon, even at 0 (master)
            {
                float frac = asset.fuelCapacity > 0f ? Mathf.Clamp(Mathf.Max(0f, jar.item.fuelLevel) / asset.fuelCapacity, 0f, 1f) : 0f;
                tile.AddChild(new ColorRect { Color = new Color(0f, 0f, 0f, 0.85f), Position = new Vector2(2, h - 10), Size = new Vector2(w - 4, 8), MouseFilter = Control.MouseFilterEnum.Ignore });   // black outline -> visible on any icon
                tile.AddChild(new ColorRect { Color = new Color(0.32f, 0.32f, 0.35f, 1f), Position = new Vector2(3, h - 9), Size = new Vector2(w - 6, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // empty track (grey) -> the bar reads even at 0
                if (frac > 0f) tile.AddChild(new ColorRect { Color = new Color(0.95f, 0.78f, 0.2f), Position = new Vector2(3, h - 9), Size = new Vector2((w - 6) * frac, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // fuel fill (yellow)
            }

            if (asset?.IsFluidContainer == true && jar.item != null)   // a fluid container (bottle/canteen) shows a fill bar tinted by its fluid, even at 0 (strawberry)
            {
                FluidItem.Read(jar.item, asset, out var ftype, out var famt, out var fq);
                float frac = asset.fluidCapacity > 0f ? Mathf.Clamp(famt / asset.fluidCapacity, 0f, 1f) : 0f;
                var fcol = ftype == FluidType.None ? new Color(0.45f, 0.48f, 0.52f) : FluidDef.WaterColor(ftype, fq);   // water folds its quality into the colour
                tile.AddChild(new ColorRect { Color = new Color(0f, 0f, 0f, 0.85f), Position = new Vector2(2, h - 10), Size = new Vector2(w - 4, 8), MouseFilter = Control.MouseFilterEnum.Ignore });   // black outline
                tile.AddChild(new ColorRect { Color = new Color(0.30f, 0.31f, 0.34f, 1f), Position = new Vector2(3, h - 9), Size = new Vector2(w - 6, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // empty track (grey) -> reads even at 0
                if (frac > 0f) tile.AddChild(new ColorRect { Color = fcol, Position = new Vector2(3, h - 9), Size = new Vector2((w - 6) * frac, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // fluid fill, tinted by type
                // "autodrink ON" badge (strawberry): a cyan droplet-dot in the top-left — shown ONLY on the one ACTIVE
                // autodrink bottle (first enabled+safe+non-empty), so exactly one bottle is marked at a time.
                if (ReferenceEquals(jar.item, FluidItem.ActiveAutoDrink(Inv)))
                {
                    var badge = new Panel { Position = new Vector2(3, 3), Size = new Vector2(15, 15), MouseFilter = Control.MouseFilterEnum.Ignore };
                    var bs = new StyleBoxFlat { BgColor = new Color(0.25f, 0.72f, 0.95f) };
                    bs.BorderColor = new Color(0f, 0f, 0f, 0.85f); bs.SetBorderWidthAll(2); bs.SetCornerRadiusAll(7);   // ~circular droplet-dot
                    badge.AddThemeStyleboxOverride("panel", bs);
                    badge.AddChild(new ColorRect { Color = new Color(0.92f, 0.98f, 1f, 0.95f), Position = new Vector2(4, 3), Size = new Vector2(4, 4), MouseFilter = Control.MouseFilterEnum.Ignore });   // a little highlight so it reads as a drop
                    tile.AddChild(badge);
                }
            }

            if (asset?.type == EItemType.FOOD && jar.item != null)   // FOOD shows its CONDITION as a coloured % in the bottom-right corner (source SleekItem quality box: red->yellow->green)
            {
                int q = jar.item.quality;
                var qcol = ItemTool.QualityColor(q / 100f);
                var box = new Panel { Position = new Vector2(w - 32, h - 17), Size = new Vector2(30, 15), MouseFilter = Control.MouseFilterEnum.Ignore };
                var bs = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.72f) };
                bs.SetCornerRadiusAll(3); bs.BorderColor = qcol; bs.SetBorderWidthAll(1);   // dark chip, outlined in the condition colour so it reads on any icon
                box.AddThemeStyleboxOverride("panel", bs);
                var lbl = new Label { Text = $"{q}%", Size = new Vector2(30, 15), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeColorOverride("font_color", qcol.Lerp(Colors.White, 0.45f));   // brighten the text so even the dark-red (spoiled) end reads on the chip; the border keeps the pure hue
                lbl.AddThemeFontSizeOverride("font_size", 10);
                box.AddChild(lbl);
                tile.AddChild(box);
            }
            return tile;
        }

        static Label Header(string text, Vector2 pos, float width)
        {
            var l = new Label { Text = text, Position = pos, Size = new Vector2(width, HEADER - 8) };
            l.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.88f));
            l.AddThemeFontSizeOverride("font_size", 13);
            l.MouseFilter = Control.MouseFilterEnum.Ignore;
            return l;
        }

        static void StyleBox(Panel p, Color c)
        {
            var sb = new StyleBoxFlat { BgColor = c };
            sb.SetCornerRadiusAll(3);
            p.AddThemeStyleboxOverride("panel", sb);
        }
    }

    // a grid backdrop that draws the 50px cell lines (the empty inventory grid look)
    public partial class GridPanel : Control
    {
        public Vector2I Cells = new(1, 1);
        public int Cell = 50;

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, 0.5f), true);
            var line = new Color(1f, 1f, 1f, 0.10f);
            for (int x = 0; x <= Cells.X; x++)
                DrawLine(new Vector2(x * Cell, 0), new Vector2(x * Cell, Cells.Y * Cell), line, 1f);
            for (int y = 0; y <= Cells.Y; y++)
                DrawLine(new Vector2(0, y * Cell), new Vector2(Cells.X * Cell, y * Cell), line, 1f);
        }
    }
}
