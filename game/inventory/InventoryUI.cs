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


        // --- retail palette: the dashboard is a translucent overlay, NOT an opaque dark panel ---
        static readonly Color UI_PANEL = new Color(0.10f, 0.13f, 0.18f, 0.42f);   // char panel / backdrop: world shows through
        static readonly Color UI_BAR   = new Color(0.17f, 0.24f, 0.32f, 0.78f);   // page header bars (blue-grey)
        static readonly Color UI_NAV   = new Color(0.13f, 0.18f, 0.24f, 0.80f);   // navbar strip
        static readonly Color UI_CELL  = new Color(0.62f, 0.72f, 0.84f, 0.30f);   // empty grid cell: LIGHT + see-through
        static readonly Color UI_TAB_ON  = new Color(0.55f, 0.62f, 0.70f, 0.72f);   // lit/open tab
        static readonly Color UI_TAB_OFF = new Color(0.22f, 0.29f, 0.37f, 0.62f);   // the other tabs + icon buttons
        static readonly Color UI_STAGE = new Color(0.08f, 0.11f, 0.15f, 0.30f);   // paperdoll backing
        const int CELL = 50;         // SleekItems cell size
        const int HEADER = 30;       // legacy per-page strip (kept for the char-panel slots)
        // --- the source's ACTUAL page-stacking metrics (PlayerDashboardInventoryUI.updateBoxAreas) ---
        // Each visible page is a HEADER BAR with its grid directly beneath it -- strawberry's annotation
        // "clothing slots show above the storage slots they provide" is literally this loop:
        //     header.PositionOffset_Y = y;  items.PositionOffset_Y = y + 70;  y += gridHeight + 80;
        // Bare clothing (hat/mask/glasses) has no grid and advances only 70.
        const int HDRH = 60;         // headers[i].SizeOffset_Y = 60
        const int HDRGAP = 70;       // grid sits 70px below its own header
        const int PAGEADV = 80;      // advance = gridHeight + 80 (=> 10px between grid bottom and next header)
        const int GRIDPAD = 30;      // SleekItems.SizeOffset_Y = rows*50 + 30
        const int BOXX = 430;        // box.PositionOffset_X = 430 (410 char panel + margins)
        const int BOXINSET = 440;    // box.SizeOffset_X = -440
        const int SPLITMIN = 1350;   // isSplitClothingArea kicks in at this screen width
        const int PAD = 12;
        // SOURCE-ACCURATE layout (PlayerDashboardInventoryUI): top navbar (60px), a fixed 410px CHARACTER panel on
        // the left (3D paperdoll + worn slots + the two weapon slots at its bottom), and a storage BOX filling the
        // rest of the screen to the right. The dashboard FILLS the screen (source container = full rect - margin),
        // NOT a centred blob.
        const int NAVH = 60;         // top navbar strip (source backdropBox starts at Y=60, below the nav)
        const int MARGIN = 12;       // screen-edge margin
        const int CHARW = 410;       // character panel width (source characterBox SizeOffset_X = 410)
        const int GUTTER = 20;       // gap between the character panel and the storage box
        const int PDTOP = 58;        // paperdoll y inside the character panel (below the name/faction badge)
        const int PDW = CHARW - 40;  // paperdoll fills the panel width (370)
        const int PDH = 440;         // paperdoll display height (portrait, fills the upper panel)
        const int COSMH = 44;        // reserved strip under the paperdoll: rotation slider + cosmetic-swap buttons

        Control _root, _dash, _storageCol, _weaponRow, _cosmeticRow;
        Control _clothingCol, _areaCol;   // source clothingBox (player pages) + areaBox (STORAGE/Nearby); split 50/50 at >=1350px
        Panel _charBox;
        // clothing paperdoll: an isolated SubViewport (own world) renders a preview RiggedCharacter clothed off the SAME
        // inventory's worn slots (PlayerClothingController.Refresh is read-only), lit + framed by a camera. Built once;
        // Refresh() repaints its clothing; drag on its view spins it. Held weapon deferred (needs 3P gun anims).
        SubViewport _pdVp;
        Control _pdHit;   // the paperdoll's on-screen rect -- retail's equip drop zone (drag a garment onto the model)
        Panel _pdStage;   // the framed backing behind the model; grown to fill the character panel in LayoutDash
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
            _dash.SetAnchorsPreset(Control.LayoutPreset.FullRect);   // the dashboard FILLS the screen (source container = full rect)
            _root.AddChild(_dash);

            BuildNavbar();            // top 60px tab strip (Inventory / Crafting / Skills / Information)
            BuildCharacterPanel();    // left 410px: paperdoll + worn slots + the two weapon slots at the bottom
            // source: box at PositionOffset_X 430, holding clothingBox (player pages) and areaBox (STORAGE/Nearby).
            _storageCol = new Control { Position = new Vector2(BOXX, NAVH + MARGIN) };
            _dash.AddChild(_storageCol);
            _clothingCol = new Control();                 // left half (or full width when not split)
            _areaCol = new Control();                     // right half; hidden when the screen is too narrow to split
            _storageCol.AddChild(_clothingCol);
            _storageCol.AddChild(_areaCol);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        // The Nearby/AREA refresh lives HERE, not at the call sites, because there are four ways to open the bag
        // (the G keybind via Toggle, OpenInventory, crate-open, and the replicated storage-open fact) and only
        // ONE of them was scanning. The keybind -- i.e. every time a player actually opens their inventory --
        // went through Toggle -> Open and never populated the page, so Nearby was permanently empty in-game
        // while the demo path in Main.cs worked fine. Scanning on Open closes the whole class instead of the
        // one call site that got noticed.
        public void Open() { Player?.ScanNearbyItems(); _open = true; Visible = true; if (_pdVp != null) _pdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always; Refresh(); _lastSig = InventorySignature(); }
        public void Close() { _open = false; Visible = false; _pdDragging = false; if (_pdVp != null) _pdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; }   // stop rendering the paperdoll while the bag is closed
        public void DebugSelect(byte page, byte x, byte y) { Open(); OpenSelection(page, x, y); }   // demo/verify only
        // demo/verify: run the modifier quick-action on a cell (headless can't hold ctrl and click)
        public bool DebugQuickAction(byte page, byte x, byte y) => QuickAction(page, x, y);
        // demo/verify: advance the held-item rotation one step and report it (proves 4 states, not a toggle)
        public int DebugCycleRot() { _dragRot = (byte)((_dragRot + 1) % 4); return _dragRot; }
        // #9 seam: headless can't hold Ctrl and click, so drive the Ctrl+LMB branch (drop from own pages / take from AREA)
        // directly. Ctrl+RMB is already covered by DebugQuickAction, which is the same QuickAction the RMB branch calls.
        public bool DebugCtrlGrab(byte page, byte x, byte y) => CtrlGrab(page, x, y);

        // #3: cancel an in-progress drag (RMB during a drag; source onRightClickedDuringDrag -> stopDrag). The dragged
        // item is NEVER moved out of its page (the drag only previewed a floating tile) -> cancel = drop tile + repaint.
        bool CancelDrag()
        {
            if (!_dragging) return false;
            _dragFromCloth = false;
            _dragging = false;
            _dragTile?.QueueFree(); _dragTile = null;
            PlayInventoryAudio();   // source stopDrag plays a foley
            Refresh();
            return true;
        }
        public bool DebugIsDragging => _dragging;                                    // #3 test seam
        public bool DebugRmbCancel() => CancelDrag();                                // #3 test: exercise the real cancel path
        public bool DebugStartDrag(byte page, byte x, byte y)                        // #3 test: set up a grid drag headless (StartDrag's grid branch)
        {
            if (Inv == null) return false;
            var pg = Inv.items[page]; byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return false;
            _dragFromCloth = false; _dragJar = pg.getItem(idx);
            _dragPage = page; _dragX0 = _dragJar.x; _dragY0 = _dragJar.y; _dragRot = _dragJar.rot;
            _grab = Vector2.Zero; _dragging = true;
            return _dragging;
        }

        long _lastSig = -1;
        public override void _Process(double delta)
        {
            if (!_open) return;
            LayoutDash();   // keep the panels sized/placed to the screen as the viewport settles
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
                    // #9: MODIFIER + LMB is the source's onGrabbedItem modifier branch -- DROP to the ground from any of
                    // your own pages, TAKE from the nearby/AREA page. It is NOT the quick transfer; that's Ctrl+RMB
                    // (onSelectedItem) below. Checked BEFORE StartDrag so the modifier click never begins a drag it
                    // won't finish. Modifier is Ctrl because ControlsSettings binds OTHER to KeyCode.LeftControl.
                    if (!_dragging && Input.IsKeyPressed(Key.Ctrl)
                        && PointToCell(mb.GlobalPosition, out byte qp, out byte qx, out byte qy, out _, out _)
                        && CtrlGrab(qp, qx, qy))
                    { GetViewport().SetInputAsHandled(); return; }

                    // Press on the paperdoll rect (when NOT carrying an item) begins a SPIN, not an item grab. This MUST
                    // live here rather than on _pdHit's Control.GuiInput: _Input runs before gui_input, and the StartDrag
                    // branch just below calls SetInputAsHandled() on every press -> it was swallowing the paperdoll's
                    // GuiInput so the old PaperdollDrag handler never fired (that's why click-spin looked dead).
                    if (!_dragging && OverPaperdoll(mb.GlobalPosition))
                    { _pdDragging = true; GetViewport().SetInputAsHandled(); return; }

                    if (!_dragging) { StartDrag(mb.GlobalPosition); GetViewport().SetInputAsHandled(); }
                }
                else
                {
                    if (_pdDragging) { _pdDragging = false; GetViewport().SetInputAsHandled(); }        // end a paperdoll spin
                    else if (_dragging) { Drop(mb.GlobalPosition); GetViewport().SetInputAsHandled(); }
                }
            }
            else if (e is InputEventMouseButton rmb && rmb.ButtonIndex == MouseButton.Right && rmb.Pressed)
            {
                if (_dragging) { CancelDrag(); GetViewport().SetInputAsHandled(); return; }   // #3: RMB during a drag CANCELS it (source onRightClickedDuringDrag -> stopDrag)
                // #9: MODIFIER + RMB is the source's onSelectedItem modifier branch -- the storage-aware quick
                // transfer (AREA -> STORAGE, STORAGE -> tryFindSpace in your pages, your page -> STORAGE). Checked
                // before the action menu so the modifier click transfers instead of opening a panel.
                if (Input.IsKeyPressed(Key.Ctrl)
                    && PointToCell(rmb.GlobalPosition, out byte tp, out byte tx, out byte ty, out _, out _)
                    && QuickAction(tp, tx, ty))
                { GetViewport().SetInputAsHandled(); return; }

                // RIGHT-click opens the item action menu (master: RMB only, not a left-click)
                CloseSelection();
                if (PointToCell(rmb.GlobalPosition, out byte page, out byte cx, out byte cy, out _, out _))
                {
                    byte idx = Inv.items[page].getIndex(cx, cy);
                    if (idx != byte.MaxValue) { var j = Inv.items[page].getItem(idx); OpenSelection(page, j.x, j.y); }
                }
                GetViewport().SetInputAsHandled();
            }
            else if (e is InputEventMouseMotion mm)
            {
                if (_pdDragging)
                {
                    _pdYaw -= mm.Relative.X * 0.012f;                          // horizontal drag spins the rig around Y
                    if (_pdBody != null) _pdBody.Rotation = new Vector3(0f, Mathf.Pi + _pdYaw, 0f);   // _pdYaw stays authoritative even pre-rig
                    GetViewport().SetInputAsHandled();
                }
                else if (_dragging) { _dragTile.GlobalPosition = mm.GlobalPosition - _grab; }
            }
            else if (e is InputEventKey { Pressed: true, Keycode: Key.R } && _dragging)
            {
                // Source: `dragJar.rot++; dragJar.rot %= 4` -- FOUR orientations, not a 90-degree toggle. This is
                // not cosmetic: a 2-state toggle can never place a non-square item at 180/270, which changes what
                // physically fits in a grid. (Rendering already keys off `rot % 2`, so 2/3 draw correctly.)
                _dragRot = (byte)((_dragRot + 1) % 4);
                RebuildDragTile();
                PlayInventoryAudio();   // #4: source plays inventory audio on rotate
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
            PlayInventoryAudio();   // #4: source startDrag plays inventory audio on grab
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

        AudioStreamPlayer _invAudio;
        /// <summary>#4 seam: the clip path PlayInventoryAudio last resolved, or null if it never fired. Lets a test
        /// assert WHICH foley a path routes to (and that the silent paths stay silent) without an audio device.</summary>
        public string DebugLastAudio;
        // #4: source PlayInventoryAudio (ItemAsset.GetDefaultInventoryAudio :2409) -- a per-grab foley on grab/drop/rotate/
        // cancel. Retail splits LIGHT (a 1x1 item, size_x<2 && size_y<2) vs ROUGH (bigger) and picks a RANDOM variant per
        // grab (7 light / 6 rough real clips ripped from core.masterbundle) so a fast loot run doesn't machine-gun one clip.
        // `jar` names the item the sound is FOR. Defaults to the dragged one, but non-drag callers (the quick
        // transfer) must pass theirs explicitly -- _dragJar is stale or null on those paths.
        void PlayInventoryAudio(ItemJar jar = null)
        {
            var j = jar ?? _dragJar;
            bool light = j == null || (j.size_x < 2 && j.size_y < 2);
            int v = (int)(GD.Randi() % (uint)(light ? 7 : 6)) + 1;
            string path = $"res://content/sounds/inv_{(light ? "light" : "heavy")}_{v:00}.wav";
            // Recorded BEFORE the load so the routing is assertable headlessly, where the stream may not resolve.
            DebugLastAudio = path;
            var stream = PlayerController.LoadWavOneShot(path);
            if (stream == null) return;
            if (_invAudio == null || !IsInstanceValid(_invAudio)) { _invAudio = new AudioStreamPlayer(); AddChild(_invAudio); }
            _invAudio.Stream = stream;
            _invAudio.Play();
        }

        void Drop(Vector2 global)
        {
            byte sp = _dragPage, sx = _dragX0, sy = _dragY0, srot = _dragRot;
            bool fromCloth = _dragFromCloth; EItemType fromType = _dragClothType;
            _dragFromCloth = false;
            _dragging = false;
            _dragTile?.QueueFree(); _dragTile = null;
            PlayInventoryAudio();   // #4: source stopDrag plays inventory audio on every drop
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

        // Hit-test a clothing equip target under a screen point. Retail has NO slot list -- you drag a garment onto
        // the CHARACTER MODEL and it lands in the slot its own type dictates (PlayerDashboardInventoryUI drops onto
        // characterPlayer). So the paperdoll is the drop zone, and the target slot is resolved from the dragged
        // item's EItemType rather than from where the cursor happens to be.

        // Retail's quick-action: hold the "other" modifier and click an item instead of dragging it
        // (source PlayerDashboardInventoryUI.onSelectedItem -> checkAction). This is how loot actually moves in
        // retail -- without it every single item is a manual drag. Ctrl stands in for ControlsSettings.other.
        //
        //   crate open + item in Nearby   -> into the crate
        //   crate open + item in crate    -> into your pages (first with room)
        //   crate open + item on you      -> into the crate
        //   crate closed + item in Nearby -> pick it up
        //   crate closed + wearable       -> equip that slot
        bool QuickAction(byte page, byte cx, byte cy)
        {
            if (Inv == null) return false;
            byte idx = Inv.items[page].getIndex(cx, cy);
            if (idx == byte.MaxValue) return false;
            var jar = Inv.items[page].getItem(idx);
            if (jar?.item == null) return false;

            bool crateOpen = Inv.items[PlayerInventory.STORAGE].width > 0 && Inv.items[PlayerInventory.STORAGE].height > 0;

            // where should it go?
            byte dest;
            if (crateOpen) dest = page == PlayerInventory.STORAGE ? (byte)255 : PlayerInventory.STORAGE;   // 255 = "any of my pages"
            else if (page == PlayerInventory.AREA) dest = 255;                                            // pick up off the ground
            else { return QuickEquip(page, cx, cy, jar); }                                                // wear/equip it

            return MoveTo(page, idx, jar, dest);
        }

        // Move a jar out of `page` into `dest` (255 = first of my own pages with room). Puts it back if the
        // destination has no room, so a failed quick-move can never eat an item.
        bool MoveTo(byte page, byte idx, ItemJar jar, byte dest)
        {
            Inv.items[page].removeItem(idx);
            bool ok = dest == 255 ? Inv.tryAddItem(jar.item) : Inv.items[dest].tryAddItem(jar.item);
            if (!ok) Inv.items[page].tryAddItem(jar.item);   // no room -> restore, no-op rather than a loss
            if (ok)
            {
                // Source onSelectedItem plays the foley on BOTH grid<->storage transfer branches (:887, :898) but NOT
                // on the AREA take (:872-877 is a bare takeItem). Pass the jar explicitly: PlayInventoryAudio picks
                // light-vs-heavy off item size, and nothing is being DRAGGED here, so _dragJar would be stale or null.
                if (page != PlayerInventory.AREA) PlayInventoryAudio(jar);
                CloseSelection(); Refresh();
            }
            return ok;
        }

        // #9: the source's onGrabbedItem modifier branch. Ctrl+LMB on the nearby/AREA page TAKES the item into your
        // pages (`ItemManager.takeItem`); on any page of your own it DROPS the item to the ground (`sendDropItem`).
        // Distinct from Ctrl+RMB, which is the storage-aware transfer. DropSelected keys off the _sel* triple and
        // already does its own CloseSelection+Refresh, so seeding the triple first is the same pattern DebugEquip uses.
        bool CtrlGrab(byte page, byte cx, byte cy)
        {
            byte idx = Inv.items[page].getIndex(cx, cy);
            if (idx == byte.MaxValue) return false;
            var jar = Inv.items[page].getItem(idx);
            if (page == PlayerInventory.AREA) return MoveTo(page, idx, jar, 255);   // ground -> first of my pages with room
            _selPage = page; _selX = jar.x; _selY = jar.y;
            // No foley here: the source's onGrabbedItem modifier branch is a bare sendDropItem / takeItem with no
            // PlayInventoryAudio call. (It also can't be correct here -- nothing is being dragged, so the light-vs-heavy
            // pick would read a stale _dragJar from the previous drag, or null on a fresh session.)
            DropSelected();
            return true;
        }

        // Wearable -> equip into its own slot, mirroring checkAction's per-type sendSwap* calls.
        bool QuickEquip(byte page, byte cx, byte cy, ItemJar jar)
        {
            var t = jar.GetAsset()?.type;
            if (t == null) return false;
            int ci = _clothing.FindIndex(c => c.type == t.Value);
            if (ci < 0) return false;
            WearFromGrid(_clothing[ci].type, page, cx, cy);
            CloseSelection();
            Refresh();
            return true;
        }
        bool PointToClothSlot(Vector2 global, out int idx)
        {
            for (int i = 0; i < _clothing.Count; i++)
                if (_clothing[i].slot.Visible &&
                    new Rect2(_clothing[i].slot.GlobalPosition, _clothing[i].slot.Size).HasPoint(global)) { idx = i; return true; }

            if (_pdHit != null && new Rect2(_pdHit.GlobalPosition, _pdHit.Size).HasPoint(global) && _dragJar != null)
            {
                var t = _dragJar.GetAsset()?.type;
                if (t != null)
                {
                    int m = _clothing.FindIndex(c => c.type == t.Value);
                    if (m >= 0) { idx = m; return true; }
                }
            }
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
            // The equip drop TARGET moved: the per-slot boxes are now built hidden (the worn item shows on the
            // paperdoll + in its header instead), and PointToClothSlot gates its slot loop on `.Visible`. So a drop
            // at a hidden slot's centre matches nothing and falls through to the _pdHit paperdoll branch, which does
            // not contain that point -- the gesture silently stopped equipping. Aim at whichever target is actually
            // live so this keeps testing a REAL Drop() through the REAL hit-test rather than a control nobody can hit.
            Control s = _clothing[ci].slot.Visible ? (Control)_clothing[ci].slot : _pdHit;
            // NB: must not be `Size > 1` on a hidden slot -- a hidden Panel keeps its explicit 50x50, so the old check
            // passed and the test FAILED instead of taking the "not laid out headless" skip.
            layoutValid = s != null && s.Size.X > 1f && s.Size.Y > 1f;   // Control laid out (GlobalPosition/Size meaningful)
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
            StyleBox(panel, UI_PANEL);
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
            if (Inv.items[PlayerInventory.STORAGE].width > 0 && Inv.items[PlayerInventory.STORAGE].height > 0)   // #7: a crate is open -> Store/Take quick-move (source onClickedStore; reuses QuickAction's crate<->pages logic)
            {
                string smove = _selPage == PlayerInventory.STORAGE ? "Take" : "Store";
                AddActionButton(panel, smove, new Vector2(228, by), () => { QuickAction(_selPage, _selX, _selY); CloseSelection(); }); by += 44;
            }
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
            bool equipped = false;
            if (asset?.gunName != null) { Player?.EquipHeldGun(asset.gunName, pg.getItem(idx).item); equipped = true; }   // equipping a gun makes it the held weapon; the item carries its saved ammo/firemode/mag (master)
            else if (asset?.meleeName != null) { Player?.EquipHeldMelee(asset.meleeName); equipped = true; }   // a melee weapon -> the melee viewmodel + weapon-specific swings
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
            // #8: source closes the dashboard on EVERY successful weapon equip -- checkSlot (both branches, :928/:955)
            // and checkEquip (:988) each run `PlayerDashboardUI.close(); PlayerLifeUI.open();`. Equipping a gun puts you
            // back in the game rather than leaving you sitting in the bag. Gated on `equipped` because the source only
            // closes on the success path; the clothing route (checkAction's sendSwap*) deliberately does NOT close.
            if (equipped) { Close(); Input.MouseMode = Input.MouseModeEnum.Captured; }
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
        // Top navbar: a full-width 60px strip with the dashboard tab labels (source PlayerDashboardUI nav, above the
        // inventory content). The Inventory tab reads active; the rest are placeholders for the sibling dashboards.
        void BuildNavbar()
        {
            var nav = new Panel();
            nav.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            nav.OffsetBottom = NAVH;
            StyleBox(nav, UI_NAV);
            _dash.AddChild(nav);
            // Retail's navbar is four WIDE TAB BUTTONS spanning the bar with their keybind in the label
            // ("Inventory [G]"), separated by small square icon buttons -- not left-aligned plain text.
            (string label, string key)[] tabs =
            {
                ("Inventory", "G"), ("Craft", "Y"), ("Skills", "U"), ("Information", "M"),
            };
            float vpw = GetViewport().GetVisibleRect().Size.X;
            const float ICONW = 44f;                                  // the little square buttons between tabs
            float tabW = (vpw - MARGIN * 2 - ICONW * tabs.Length) / tabs.Length;
            float tx2 = MARGIN;
            for (int i = 0; i < tabs.Length; i++)
            {
                var btn = new Panel { Position = new Vector2(tx2, 8), Size = new Vector2(tabW, NAVH - 16) };
                StyleBox(btn, i == 0 ? UI_TAB_ON : UI_TAB_OFF);        // the open page reads as the lit tab
                _dash.AddChild(btn);

                var t = new Label { Text = $"{tabs[i].label} [{tabs[i].key}]", Position = new Vector2(tx2, 8),
                                    Size = new Vector2(tabW, NAVH - 16),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center };
                t.AddThemeColorOverride("font_color", i == 0 ? new Color(1f, 1f, 1f) : new Color(0.78f, 0.82f, 0.88f));
                t.AddThemeFontSizeOverride("font_size", 15);
                _dash.AddChild(t);
                tx2 += tabW;

                var ico = new Panel { Position = new Vector2(tx2 + 4, 8), Size = new Vector2(ICONW - 8, NAVH - 16) };
                StyleBox(ico, UI_TAB_OFF);
                _dash.AddChild(ico);
                tx2 += ICONW;
            }
        }

        // Name + faction badge at the character panel's top (source characterPlayer / SleekPlayer @ (10,10), 410x50):
        // an avatar chip, username (yellow), faction "Neutral [0]" under it, and a yellow + on the right. Themed to match.
        void BuildNameBadge(Panel box)
        {
            var badge = new Panel { Position = new Vector2(8, 6), Size = new Vector2(CHARW - 16, 46) };
            StyleBox(badge, UI_BAR);
            box.AddChild(badge);
            var av = new Panel { Position = new Vector2(8, 7), Size = new Vector2(32, 32) };
            StyleBox(av, UI_TAB_OFF);
            badge.AddChild(av);
            var uname = new Label { Text = "Survivor", Position = new Vector2(50, 4) };
            uname.AddThemeColorOverride("font_color", new Color(1f, 0.84f, 0.22f));   // yellow username
            badge.AddChild(uname);
            var fac = new Label { Text = "Neutral [0]", Position = new Vector2(50, 23) };
            fac.AddThemeColorOverride("font_color", new Color(0.72f, 0.77f, 0.83f));
            badge.AddChild(fac);
            var plus = new Label { Text = "+", Position = new Vector2(CHARW - 44, 12) };
            plus.AddThemeColorOverride("font_color", new Color(1f, 0.84f, 0.22f));
            badge.AddChild(plus);
        }

        // The left CHARACTER panel (source characterBox, 410px): the 3D paperdoll at the top, the worn-clothing equip
        // slots below it, and the two weapon slots pinned to the BOTTOM (repositioned in LayoutDash). Built once.
        void BuildCharacterPanel()
        {
            _charBox = new Panel { Position = new Vector2(MARGIN, NAVH + MARGIN), Size = new Vector2(CHARW, 600) };   // height fixed up in LayoutDash
            StyleBox(_charBox, UI_PANEL);
            _dash.AddChild(_charBox);
            BuildNameBadge(_charBox);   // name/faction badge strip (source characterPlayer @ (10,10), 410x50)

            BuildPaperdoll(_charBox);   // the 3D worn-character render at the top of the panel

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
                // Retail has no worn-slot LIST -- the worn garments are the header bars in the centre column, and
                // the model itself is the equip target. These stay in the tree (Refresh repaints them and the drag
                // code resolves types through them) but are HIDDEN, so the panel matches the reference.
                var slot = new Panel { Position = new Vector2(12, y), Size = new Vector2(CELL, CELL), Visible = false };
                StyleBox(slot, new Color(0f, 0f, 0f, 0.5f));
                _charBox.AddChild(slot);
                var lbl = new Label { Text = name, Position = new Vector2(CELL + 20, y + 15), Visible = false };
                lbl.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.75f));
                _charBox.AddChild(lbl);
                _clothing.Add((slot, lbl, worn, type));
                y += CELL + 8;
            }

            BuildCosmeticRow();   // rotation slider + 3 cosmetic-swap buttons in the strip under the paperdoll

            _weaponRow = new Control { Position = new Vector2(12, 540) };   // PRIMARY/SECONDARY, pinned to the panel bottom in LayoutDash
            _charBox.AddChild(_weaponRow);
        }

        // Rotation slider + 3 cosmetic-swap buttons under the paperdoll (source characterSlider + swapCosmetics/Skins/Mythics
        // buttons at the bottom of characterBox). Positioned each LayoutDash into the reserved COSMH strip above the weapons.
        void BuildCosmeticRow()
        {
            _cosmeticRow = new Control();
            _charBox.AddChild(_cosmeticRow);
            string[] tips = { "Cosmetics", "Skins", "Mythics" };
            for (int i = 0; i < 3; i++)
            {
                var b = new Button { Text = tips[i].Substring(0, 1), Position = new Vector2(10 + i * 36, 4), Size = new Vector2(32, 32), TooltipText = "Swap " + tips[i] };
                _cosmeticRow.AddChild(b);
            }
            var slider = new HSlider { Position = new Vector2(10 + 3 * 36 + 10, 12), Size = new Vector2(CHARW - (10 + 3 * 36 + 10) - 20, 18), MinValue = -Mathf.Pi, MaxValue = Mathf.Pi, Step = 0.01 };
            slider.ValueChanged += (double v) => { _pdYaw = (float)v; if (_pdBody != null) _pdBody.Rotation = new Vector3(0f, Mathf.Pi + _pdYaw, 0f); };
            _cosmeticRow.AddChild(slider);
        }

        // Build the 3D paperdoll: a dark stage + an isolated SubViewport rendering a preview character clothed off the
        // player's worn slots, surfaced in a SubViewportContainer you can drag to spin. Built once (BuildCharacterPanel runs once).
        void BuildPaperdoll(Panel box)
        {
            var stage = new Panel { Position = new Vector2(8, PDTOP), Size = new Vector2(PDW, PDH) };
            _pdStage = stage;
            StyleBox(stage, UI_STAGE);   // translucent backing -- retail shows the world behind the model
            box.AddChild(stage);

            // a SubViewportContainer shows the viewport clipped to the stage. Stretch off -> the viewport keeps its own
            // LOCKED size (so the render aspect is deterministic regardless of layout timing). Dragging its surface spins the rig.
            var vpc = new SubViewportContainer
            {
                Position = new Vector2(8, PDTOP), Size = new Vector2(PDW, PDH),
                Stretch = false, MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "drag to rotate",
            };
            _pdHit = vpc;   // this rect IS the equip drop target (PointToClothSlot) AND the click-spin hit-rect (OverPaperdoll in _Input)
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
            // Keep-HEIGHT (Godot's default) is correct for a standing figure in a portrait panel -- the body's
            // height is the binding constraint. The earlier crop wasn't the aspect mode, it was that the camera
            // framed once at the ORIGINAL viewport size and never re-framed after the panel grew; LayoutDash now
            // clears _pdFramed on a resize so FramePaperdoll recomputes the distance for the real aspect.
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
        // The press/motion handling lives in _Input (see the spin branch there), because _Input consumes the press before
        // a Control.GuiInput would ever see it; this is just the hit-test for "is the cursor over the paperdoll rect".
        bool OverPaperdoll(Vector2 global) => _pdHit != null && new Rect2(_pdHit.GlobalPosition, _pdHit.Size).HasPoint(global);

        // TEST SEAM: drive a REAL click-drag on the paperdoll through _Input (the exact path the fix repairs -- the press
        // must reach the spin branch instead of being swallowed by the item-drag StartDrag/SetInputAsHandled). Returns the
        // applied yaw delta in radians; float.NaN if the press failed to start a spin (routing still broken). +relX -> -delta.
        public float DebugPaperdollDragSpin(float relX)
        {
            if (_pdHit == null) return float.NaN;
            bool wasOpen = _open; _open = true;
            var c = _pdHit.GlobalPosition + _pdHit.Size * 0.5f;
            _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = c, GlobalPosition = c });
            if (!_pdDragging) { _open = wasOpen; return float.NaN; }
            float y0 = _pdYaw;
            _Input(new InputEventMouseMotion { Relative = new Vector2(relX, 0f), Position = c, GlobalPosition = c });
            float delta = _pdYaw - y0;
            _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = c, GlobalPosition = c });
            _open = wasOpen;
            return delta;
        }
        public bool DebugPaperdollSpinning => _pdDragging;   // true only mid-drag; a completed DebugPaperdollDragSpin leaves it false

        // TEST SEAMS (master: primary/secondary slots function as inv slots). A weapon slot is a working drag target only
        // if it's registered in _drop AND PointToCell resolves its on-screen rect to that page. The bug: _drop.Clear() ran
        // AFTER the slots were added, wiping them -> reverting the fix makes both of these return false.
        public bool DebugSlotIsDropTarget(byte page) => _drop.Exists(t => t.Item1 == page && t.Item3);
        public bool DebugSlotHitTest(byte slotPage)
        {
            foreach (var (p, box, isSlot) in _drop)
                if (p == slotPage && isSlot)
                    return PointToCell(box.GlobalPosition + box.Size * 0.5f, out byte pg, out _, out _, out _, out bool s) && pg == slotPage && s;
            return false;
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

            // Storage side, 1:1 with source updateBoxAreas: pages STACK VERTICALLY as [header bar -> its own grid]
            // pairs, NOT side by side. Strawberry's annotation ("clothing slots show above the storage slots they
            // provide") is exactly this loop. When the screen is >= 1350 wide the source splits into two columns:
            // the player's own pages on the left (clothingBox), STORAGE + Nearby on the right (areaBox).
            foreach (Node c in _weaponRow.GetChildren()) c.QueueFree();
            foreach (Node c in _clothingCol.GetChildren()) c.QueueFree();
            foreach (Node c in _areaCol.GetChildren()) c.QueueFree();
            _drop.Clear();

            // weapon slots -> the character panel's bottom row (source: primary/secondary sit under the character). They
            // register as page-0/1 drop targets inside AddSlotAt, so they MUST be built AFTER _drop.Clear() -- previously
            // they were added BEFORE the clear, so their drag targets got wiped every refresh and the primary/secondary
            // slots silently rejected any dragged weapon (master: "make primary/secondary function as inv slots"). The 1/2
            // keys already equip pages 0/1 (PlayerController.EquipHotbar), so registering the drop targets completes it.
            AddSlotAt(_weaponRow, "PRIMARY",   0, new Vector2(0, 0),             3 * CELL);
            AddSlotAt(_weaponRow, "SECONDARY", 1, new Vector2(3 * CELL + 16, 0), 3 * CELL);
            Vector2 vpsz = GetViewport().GetVisibleRect().Size;
            float boxW = vpsz.X - BOXINSET;                       // source box.SizeOffset_X = -440
            bool split = vpsz.X >= SPLITMIN;                      // source isSplitClothingArea
            float colW = split ? boxW * 0.5f - 5f : boxW;         // clothingBox: SizeScale_X 0.5, SizeOffset_X -5
            _clothingCol.Position = Vector2.Zero;
            _areaCol.Position = new Vector2(boxW * 0.5f + 5f, 0f);   // areaBox: PositionScale_X 0.5, PositionOffset_X 5
            _areaCol.Visible = split;

            // the player's OWN pages, in source page order: Hands, then each worn bag under its item name
            float yC = 0f;
            foreach (var (page, fallback) in new (byte, string)[] {
                         ((byte)2, "Hands"), (PlayerInventory.BACKPACK, "Backpack"),
                         (PlayerInventory.VEST, "Vest"), (PlayerInventory.SHIRT, "Shirt"),
                         (PlayerInventory.PANTS, "Pants") })
            {
                var pg = Inv.items[page];
                if (pg.width == 0 || pg.height == 0) continue;   // source: header hidden when newHeight == 0
                AddGridAt(WornName(page, fallback), pg, new Vector2(0, yC), _clothingCol, colW, WornItem(page));
                yC += pg.height * CELL + GRIDPAD + PAGEADV;      // source: y += items.SizeOffset_Y + 80
            }
            // Worn clothing that grants NO storage still gets a bar (source headers[7]/[8]/[9] = hat/mask/glasses:
            // visible only when that item is worn, advance 70, and NO grid beneath).
            foreach (var worn in new[] { Inv.wornHat, Inv.wornMask, Inv.wornGlasses })
            {
                if (worn == null) continue;
                _clothingCol.AddChild(HeaderBar(worn.GetAsset()?.itemName ?? "Worn", new Vector2(0, yC), colW, worn));
                yC += HDRGAP;   // source: offsetClothing_y += 70
            }

            // STORAGE + Nearby live in the right column when split, else continue down the single column
            Control aCol = split ? _areaCol : _clothingCol;
            float yA = split ? 0f : yC;
            foreach (var (page, name) in new (byte, string)[] {
                         (PlayerInventory.STORAGE, "Storage"), (PlayerInventory.AREA, "Nearby") })
            {
                var pg = Inv.items[page];
                bool always = page == PlayerInventory.AREA;   // source: headers[AREA].IsVisible = true, always
                if ((pg.width == 0 || pg.height == 0) && !always) continue;
                if (pg.width == 0 || pg.height == 0)
                {
                    aCol.AddChild(HeaderBar(name, new Vector2(0, yA), colW));   // bar with no grid under it
                    yA += HDRGAP;
                    continue;
                }
                AddGridAt(name, pg, new Vector2(0, yA), aCol, colW);
                yA += pg.height * CELL + GRIDPAD + PAGEADV;
            }
            _storageW = boxW;
            _storageH = Mathf.Max(yC, split ? yA : yA) - 10f;   // source ContentSizeOffset = y - 10

            LayoutDash();
        }

        void LayoutDash()
        {
            Vector2 vp = GetViewport().GetVisibleRect().Size;
            if (_charBox != null)
            {
                _charBox.Size = new Vector2(CHARW, Mathf.Max(600f, vp.Y - NAVH - 2 * MARGIN));   // fill the height below the navbar
                if (_weaponRow != null) _weaponRow.Position = new Vector2(12, _charBox.Size.Y - CELL - (HEADER - 6) - MARGIN);
                // rotation slider + cosmetic buttons sit in the reserved COSMH strip just above the weapon slots
                if (_cosmeticRow != null) _cosmeticRow.Position = new Vector2(0, _charBox.Size.Y - CELL - (HEADER - 6) - MARGIN - COSMH);

                // The model DOMINATES the left column in the reference -- it isn't a small portrait pinned to the
                // top. Stretch it to the space between the header and the weapon row.
                // Reference measurement: retail's model area is ~260x590 => aspect ~0.44. Match that rather than
                // stretching to the full column, which produced an extreme aspect the camera can't frame.
                float avail = _charBox.Size.Y - PDTOP - CELL - (HEADER - 6) - 2 * MARGIN - 10f - COSMH;
                float pdH = Mathf.Clamp(avail, PDH, PDW / 0.44f);
                if (_pdVp != null && Mathf.Abs(_pdVp.Size.Y - pdH) > 1f) _pdFramed = false;   // re-frame for the new aspect
                if (_pdStage != null) _pdStage.Size = new Vector2(PDW, pdH);
                if (_pdHit != null) _pdHit.Size = new Vector2(PDW, pdH);
                if (_pdVp != null) _pdVp.Size = new Vector2I(PDW, Mathf.RoundToInt(pdH));
            }
        }

        // a wide single-row slot (PRIMARY / SECONDARY) placed at an explicit position inside `parent`
        void AddSlotAt(Control parent, string name, byte page, Vector2 pos, float w)
        {
            var pg = Inv.items[page];
            parent.AddChild(Header(name, pos, w));
            var box = new Panel { Position = pos + new Vector2(0, HEADER - 6), Size = new Vector2(w, CELL) };
            StyleBox(box, new Color(0f, 0f, 0f, 0.45f));
            parent.AddChild(box);
            _drop.Add((page, box, true));
            if (pg.getItemCount() > 0)
            {
                var tile = MakeTile(pg.getItem(0), (int)w, CELL);
                tile.Position = Vector2.Zero;
                box.AddChild(tile);
            }
        }

        // one storage grid page placed at an explicit position inside the storage box (Refresh lays the columns out)
        // One page = a full-width HEADER BAR with its grid 70px beneath it (source updateBoxAreas). `col` is the
        // clothingBox or areaBox equivalent; `colW` is that column's width so the bar spans it like SizeScale_X = 1.

        // Source shows the WORN ITEM'S NAME as the page header ("White T-Shirt", "Trouser Pants"), not the slot
        // name -- that's why retail reads as a list of your clothes rather than a list of categories.
        Item WornItem(byte page) =>
            page == PlayerInventory.BACKPACK ? Inv.wornBackpack
          : page == PlayerInventory.VEST     ? Inv.wornVest
          : page == PlayerInventory.SHIRT    ? Inv.wornShirt
          : page == PlayerInventory.PANTS    ? Inv.wornPants : null;

        string WornName(byte page, string fallback)
        {
            Item worn = page == PlayerInventory.BACKPACK ? Inv.wornBackpack
                      : page == PlayerInventory.VEST     ? Inv.wornVest
                      : page == PlayerInventory.SHIRT    ? Inv.wornShirt
                      : page == PlayerInventory.PANTS    ? Inv.wornPants : null;
            return worn?.GetAsset()?.itemName ?? fallback;
        }
        void AddGridAt(string name, Items page, Vector2 pos, Control col = null, float colW = 0f, Item worn = null)
        {
            col ??= _clothingCol;
            if (colW <= 0f) colW = page.width * CELL;
            col.AddChild(HeaderBar(name, pos, colW, worn));
            var grid = new GridPanel { Cells = new Vector2I(page.width, page.height), Cell = CELL,
                                       Position = pos + new Vector2(0, HDRGAP), Size = new Vector2(page.width * CELL, page.height * CELL) };
            col.AddChild(grid);
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
        // A 6-arm snowflake drawn CENTERED into a small texture -- so the "cold" badge never depends on a font glyph's
        // off-centre metrics (U+2744 seats crooked in its line box; no offset nudge centres it cleanly). One-time build.
        static ImageTexture _snowTex;
        static ImageTexture SnowflakeTex()
        {
            if (_snowTex != null) return _snowTex;
            const int N = 40;
            var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
            img.Fill(new Color(1f, 1f, 1f, 0f));
            var col = new Color(0.97f, 0.99f, 1f);
            float c = (N - 1) / 2f, len = 16f;
            void Dot(float x, float y)
            {
                int xi = Mathf.RoundToInt(x), yi = Mathf.RoundToInt(y);
                for (int oy = 0; oy <= 1; oy++) for (int ox = 0; ox <= 1; ox++)
                { int px = xi + ox, py = yi + oy; if (px >= 0 && px < N && py >= 0 && py < N) img.SetPixel(px, py, col); }
            }
            for (int a = 0; a < 6; a++)
            {
                float ang = a * Mathf.Pi / 3f, dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);
                for (float t = 0; t <= len; t += 0.5f) Dot(c + dx * t, c + dy * t);
                foreach (float f in new[] { 0.5f, 0.78f })
                    foreach (int s in new[] { 1, -1 })
                    {
                        float bx = c + dx * len * f, by = c + dy * len * f, ba = ang + s * 0.85f;
                        float bdx = Mathf.Cos(ba), bdy = Mathf.Sin(ba);
                        for (float t = 0; t <= 5f; t += 0.5f) Dot(bx + bdx * t, by + bdy * t);
                    }
            }
            _snowTex = ImageTexture.CreateFromImage(img);
            return _snowTex;
        }

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
                    var badge = new Panel { Position = new Vector2(2, 2), Size = new Vector2(21, 21), MouseFilter = Control.MouseFilterEnum.Ignore };
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
                var bs = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.72f) };
                bs.SetCornerRadiusAll(3); bs.BorderColor = qcol; bs.SetBorderWidthAll(1);   // dark chip, outlined in the condition colour so it reads on any icon
                bs.ContentMarginLeft = 4; bs.ContentMarginRight = 4; bs.ContentMarginTop = 0; bs.ContentMarginBottom = 0;   // even breathing room L/R so the text sits centred in the card
                // A PanelContainer SIZES ITSELF to the label, so the chip always wraps "{q}%" exactly (5% / 85% / 100%) and
                // the text is centred inside its card by construction. The old fixed-width Panel let a wide "100%" spill past
                // the card's edges no matter the width I picked (master: "the %s werent centered in their mini card").
                var lbl = new Label { Text = $"{q}%", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeColorOverride("font_color", qcol.Lerp(Colors.White, 0.45f));   // brighten the text so even the dark-red (spoiled) end reads on the chip; the border keeps the pure hue
                lbl.AddThemeFontSizeOverride("font_size", 11);
                var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                card.AddThemeStyleboxOverride("panel", bs);
                card.AddChild(lbl);
                tile.AddChild(card);
                card.SetAnchorsPreset(Control.LayoutPreset.BottomRight);   // pin the auto-sized chip to the tile's bottom-right corner,
                card.GrowHorizontal = Control.GrowDirection.Begin;         // growing LEFT / UP as the text widens so it never clips the icon edge
                card.GrowVertical = Control.GrowDirection.Begin;
                card.OffsetRight = -2; card.OffsetBottom = -2;             // 2 px inset from the corner
            }

            if (asset?.type == EItemType.FOOD && jar.item != null && jar.item.preserved)   // a snowflake badge marks food actively preserved by a powered fridge
            {
                var badge = new Panel { Position = new Vector2(2, 2), Size = new Vector2(21, 21), MouseFilter = Control.MouseFilterEnum.Ignore };
                var bs = new StyleBoxFlat { BgColor = new Color(0.14f, 0.44f, 0.86f) };
                bs.BorderColor = new Color(0.05f, 0.16f, 0.38f); bs.SetBorderWidthAll(2); bs.SetCornerRadiusAll(3);
                badge.AddThemeStyleboxOverride("panel", bs);
                var snow = new TextureRect { Texture = SnowflakeTex(), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
                snow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                snow.AddThemeColorOverride("font_color", new Color(0.97f, 0.99f, 1f));
                snow.AddThemeFontSizeOverride("font_size", 13);
                badge.AddChild(snow);
                tile.AddChild(badge);
            }
            return tile;
        }


        // A page header the way retail draws it: a full-width 60px BAR (source headers[] are ISleekButton,
        // SizeOffset_Y = 60, SizeScale_X = 1) with the worn item's icon on the left, its name centred, and the
        // item's condition on the right. The old plain text label was the single biggest visual gap vs the
        // reference -- retail's inventory reads as a stack of BARS, not a list of captions.
        Control HeaderBar(string text, Vector2 pos, float width, Item worn = null)
        {
            var bar = new Panel { Position = pos, Size = new Vector2(width, HDRH), MouseFilter = Control.MouseFilterEnum.Ignore };
            StyleBox(bar, UI_BAR);

            if (worn != null)
            {
                var icon = MakeTile(new ItemJar(worn), HDRH - 12, HDRH - 12);
                icon.Position = new Vector2(6, 6);
                icon.MouseFilter = Control.MouseFilterEnum.Ignore;
                bar.AddChild(icon);
            }

            var name = new Label { Text = text, Position = new Vector2(0, 0), Size = new Vector2(width, HDRH),
                                   HorizontalAlignment = HorizontalAlignment.Center,
                                   VerticalAlignment = VerticalAlignment.Center,
                                   MouseFilter = Control.MouseFilterEnum.Ignore };
            name.AddThemeColorOverride("font_color", new Color(0.88f, 0.88f, 0.91f));
            name.AddThemeFontSizeOverride("font_size", 15);
            bar.AddChild(name);

            if (worn != null)
            {
                var pct = new Label { Text = $"{worn.quality}%", Position = new Vector2(width - 74, 0),
                                      Size = new Vector2(64, HDRH), HorizontalAlignment = HorizontalAlignment.Right,
                                      VerticalAlignment = VerticalAlignment.Center,
                                      MouseFilter = Control.MouseFilterEnum.Ignore };
                pct.AddThemeColorOverride("font_color", new Color(0.95f, 0.72f, 0.25f));
                pct.AddThemeFontSizeOverride("font_size", 13);
                bar.AddChild(pct);
            }
            return bar;
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

        // Retail draws each empty cell as its OWN light, translucent, rounded tile with a bright border -- the
        // grid reads as a sheet of pale squares you can see the world through, not a dark slab with faint
        // gridlines. Verified off the reference: cells there sample as light blue (the sky behind them).
        public static Color CellFill = new(0.62f, 0.72f, 0.84f, 0.30f);
        public static Color CellEdge = new(0.86f, 0.92f, 1f, 0.55f);

        public override void _Draw()
        {
            for (int y = 0; y < Cells.Y; y++)
                for (int x = 0; x < Cells.X; x++)
                {
                    var r = new Rect2(x * Cell + 1, y * Cell + 1, Cell - 2, Cell - 2);
                    DrawRect(r, CellFill, true);
                    DrawRect(r, CellEdge, false, 1f);
                }
        }
    }
}
