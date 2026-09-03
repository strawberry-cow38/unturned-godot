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
        //
        // NEUTRAL AND LIGHTER (strawberry: "increase the transparency of the entire inventory ui, nd remove the
        // blue tint and tint it more white-ish/ very light gray"). The desaturation and the two tuning knobs that
        // implement it now live in UITheme, because this panel's look is the reference every OTHER screen is
        // standardising onto -- keeping a second copy of UiLighten here would be the exact drift being removed.
        // See UITheme for why the treatment is derived rather than hand-picked.
        // Neutral() and the two knobs now live in UITheme so every screen gets the same treatment -- this
        // panel's look IS the reference (strawberry: "standardize ... based off the inventory ui"), so the
        // definition belongs where the other screens can reach it, not here.
        static readonly Color UI_PANEL = UITheme.Bg;
        static readonly Color UI_BAR   = UITheme.Bar;
        static readonly Color UI_NAV   = UITheme.Nav;
        static readonly Color UI_CELL  = UITheme.SlotEmpty;
        static readonly Color UI_TAB_ON  = UITheme.Selected;
        static readonly Color UI_TAB_OFF = UITheme.Slot;
        static readonly Color UI_STAGE = UITheme.Stage;

        // Frosted-glass backdrop for the whole dashboard: sample the game framebuffer behind the UI and BLUR it, with
        // only a whisper of darkening -- master 2026-08-26: "change the background from a darkening tint to a slight
        // blur of what's behind." Replaces the flat 72% black scrim. 5x5 weighted taps + a mip-LOD bias so the blur is
        // robust even if the screen copy's mipmaps are shallow. The panels are translucent, so this shows through the
        // entire dashboard (paperdoll included).
        internal const string BACKDROP_BLUR = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float lod = 1.5;
uniform float spread = 3.0;
uniform vec4 tint : source_color = vec4(0.03, 0.04, 0.06, 0.40);
void fragment() {
    vec2 px = SCREEN_PIXEL_SIZE;
    vec3 c = vec3(0.0);
    float total = 0.0;
    for (int x = -2; x <= 2; x++) {
        for (int y = -2; y <= 2; y++) {
            float w = 1.0 / (1.0 + float(x*x + y*y));
            c += textureLod(screen_tex, SCREEN_UV + vec2(float(x), float(y)) * px * spread, lod).rgb * w;
            total += w;
        }
    }
    c /= total;
    COLOR = vec4(mix(c, tint.rgb, tint.a), 1.0);
}";

        const int CELL = 72;         // SleekItems cell size
        const int HEADER = 30;       // legacy per-page strip (kept for the char-panel slots)
        // --- the source's ACTUAL page-stacking metrics (PlayerDashboardInventoryUI.updateBoxAreas) ---
        // Each visible page is a HEADER BAR with its grid directly beneath it -- strawberry's annotation
        // "clothing slots show above the storage slots they provide" is literally this loop:
        //     header.PositionOffset_Y = y;  items.PositionOffset_Y = y + 70;  y += gridHeight + 80;
        // Bare clothing (hat/mask/glasses) has no grid and advances only 70.
        const int HDRH = 76;         // headers[i].SizeOffset_Y = 60
        const int HDRGAP = 86;       // grid sits 70px below its own header
        const int PAGEADV = 96;      // advance = gridHeight + 80 (=> 10px between grid bottom and next header)
        const int GRIDPAD = 30;      // SleekItems.SizeOffset_Y = rows*50 + 30
        const int BOXX = 580;        // box start = MARGIN + CHARW + 8 (follows the tighter char panel; was 700)
        const int BOXINSET = 590;    // BOXX + 10 -> 10px right margin (was 710)
        const int SPLITMIN = 1350;   // isSplitClothingArea kicks in at this screen width
        const int PAD = 12;
        // SOURCE-ACCURATE layout (PlayerDashboardInventoryUI): top navbar (60px), a fixed 410px CHARACTER panel on
        // the left (3D paperdoll + worn slots + the two weapon slots at its bottom), and a storage BOX filling the
        // rest of the screen to the right. The dashboard FILLS the screen (source container = full rect - margin),
        // NOT a centred blob.
        const int NAVH = 60;         // top navbar strip (source backdropBox starts at Y=60, below the nav)
        const int MARGIN = 12;       // screen-edge margin
        const int CHARW = 560;       // character panel width -- sized to hug the SHRUNK paperdoll (master 2026-08-26: "shrink the stuff around to fit the new scale"; was 680).
        const int GUTTER = 20;       // gap between the character panel and the storage box
        const int PDTOP = 88;        // paperdoll y inside the character panel (below the name/faction badge)
        const int PDW = CHARW - 40;  // paperdoll fills the panel width (370)
        const int PDH = 440;         // paperdoll display height (portrait, fills the upper panel)
        const float PD_ASPECT = 0.585f;  // paperdoll viewport w/h -- wide enough his arm span clears the frame (measured off the widened render)
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
        static readonly HashSet<byte> _collapsed = new();                    // clothing pages whose grid is folded away (items stay, header stays); STATIC so it survives inventory open/close and a rebuilt dashboard (master)
        readonly List<(Control icon, EItemType type)> _headerIcons = new();  // the small worn-item icon on each page header: draggable = take it off
        VScrollBar _vscroll; float _scrollY; bool _scrollTestApplied, _foldTestApplied;                                 // clothing column scroll (master 2026-09-03: "scrollbar to the right of the main inventory grid")

        // quick-craft: a dashboard SECTION under the bags (icons of recipes you can afford); LMB queues 1, RMB queues 5.
        readonly List<(Control tile, BlueprintDef bp)> _quickTiles = new();
        const int QUICK_MAX = 18;   // recipes shown in the quick-craft section (6-wide -> up to 3 rows)

        // drag-drop: registered drop zones (a page + the Control whose global rect maps to its cells) and the live drag
        readonly List<(byte page, Control ctl, bool isSlot)> _drop = new();
        bool _dragging;
        byte _dragPage, _dragX0, _dragY0, _dragRot;
        bool _dragFromCloth;          // the drag started on a clothing equip slot (the worn garment, not a page cell)
        EItemType _dragClothType;     // which clothing slot it was grabbed from (only meaningful when _dragFromCloth)
        ItemJar _dragJar;
        Vector2 _grab;          // cursor offset within the grabbed item's top-left cell
        Control _dragTile;      // the floating tile that follows the cursor

        // --- drag-load rounds into a magazine (strawberry): drop a loose round onto a compatible mag -> a fill wheel
        //     (one segment per capacity) fills a round every LOAD_INTERVAL, pulling a bullet from the stack into the
        //     mag; the RMB "Unload" menu action reverses it. Both items keep their inventory slots -- it's a LOAD, not
        //     a move/swap. A mag LOCKS to the first cartridge loaded (no mixing) until it's emptied.
        Control _magFx;                       // overlay: draws the fill wheel(s) + the drag-over compat hint
        readonly List<MagOp> _magOps = new();
        bool _magDemoFired;                   // UG_MAGLOAD render harness: fire the demo load once
        Vector2 _dragMouse;                   // last cursor pos during a drag (for the over-a-mag hint in _Draw)
        const float LOAD_INTERVAL = 0.5f;     // seconds per round -- a 0.5s cooldown between each round (master)
        bool _magFxWasActive;                 // so the overlay gets ONE final clear-redraw when the wheel finishes (else the last frame lingers)
        // Same ORDER as SDG.Unturned.MagLoadResult -- the casts below are ordinal. Kept as a local alias
        // rather than using the core enum directly only because the drawing code reads better with the
        // short name; if you reorder either one, reorder both.
        enum MagLoad { Ok, Full, WrongCaliber, WouldMix }
        class MagOp { public byte page, x, y; public Item mag; public int bulletId; public bool unloading; public float t; public int batch; public int done; }   // batch = rounds THIS op moves (the amount dragged / to eject); done = moved so far. The wheel is done/batch, not amount/cap (master)

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

            var dim = new ColorRect();   // frosted-glass backdrop: blur the world behind the UI instead of flat-darkening it (master)
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = BACKDROP_BLUR } };
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

            _magFx = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };   // mag load/unload fill wheel + the drag-over compat hint, drawn ON TOP of the grid
            _magFx.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _magFx.Draw += DrawMagFx;
            _root.AddChild(_magFx);
        }

        // quick-craft: rebuild the bottom-right bar with the recipes you can afford right now (icon per recipe).
        // QUICK CRAFT is a dashboard SECTION under the bags (master: "integrate it as another tab below 'nearby'"), not a
        // floating corner panel: same HeaderBar + GridPanel + Slot tiles a bag uses, so it reads as one of them. The
        // craftable set is recomputed each Refresh. LMB a tile = queue 1, RMB = queue 5 (caught in _Input via QuickCraftHit).
        // Returns the y just past the section so the caller keeps stacking.
        float BuildQuickCraftSection(Control col, float yA, float colW)
        {
            _quickTiles.Clear();
            if (Inv == null) return yA;

            var inv = new Crafting.PlayerInvAdapter(Inv);
            var stations = Player?.CraftingStationTags() ?? new System.Collections.Generic.HashSet<string>();
            var show = new List<BlueprintDef>();
            foreach (var bp in BlueprintRegistry.Applicable(inv))   // Applicable = every input present (consumables AND tools)
            {
                if (BlueprintRegistry.IsRecolour(bp)) continue;         // skip the 126 dye repaints
                if (!Crafting.HasStations(bp, stations)) continue;      // only if the recipe's workbench/station is satisfied (in range + LOS)
                if (!Crafting.MeetsSkill(bp, Player?.Skills)) continue;
                show.Add(bp);
                if (show.Count >= QUICK_MAX) break;
            }
            if (show.Count == 0) return yA;   // nothing craftable right now -> no section, no empty header

            col.AddChild(HeaderBar("Quick Craft", new Vector2(0, yA), colW, null, show.Count));
            const int COLS = 6;
            int rows = System.Math.Max(1, Mathf.CeilToInt(show.Count / (float)COLS));
            var gp = new GridPanel { Cells = new Vector2I(COLS, rows), Cell = CELL, Position = new Vector2(0, yA + HDRGAP), Size = new Vector2(COLS * CELL, rows * CELL) };
            col.AddChild(gp);
            for (int i = 0; i < show.Count; i++)
            {
                var bp = show[i];
                var a = CraftingMenu.OutAsset(bp);
                var tile = new Panel { Position = new Vector2((i % COLS) * CELL, (i / COLS) * CELL), Size = new Vector2(CELL, CELL) };
                tile.AddThemeStyleboxOverride("panel", UITheme.Box(UITheme.Slot, UITheme.RadiusCell));   // filled cell = the same Slot bg an item tile sits on
                tile.TooltipText = $"{CraftingMenu.Title(bp)}\nLMB +1   RMB +5";   // MouseFilter left Stop so the tooltip shows; the click is caught in _Input
                var tex = a != null ? IconFor(a.id) : null;
                if (tex != null)
                {
                    var ico = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
                    ico.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    ico.OffsetLeft = 6; ico.OffsetTop = 6; ico.OffsetRight = -6; ico.OffsetBottom = -6;
                    tile.AddChild(ico);
                }
                else
                {
                    var lbl = new Label { Text = CraftingMenu.Title(bp), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                    lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
                    tile.AddChild(lbl);
                }
                gp.AddChild(tile);
                _quickTiles.Add((tile, bp));
            }
            return yA + rows * CELL + GRIDPAD + PAGEADV;
        }

        // a press over a quick-craft tile queues a craft (qty 1 on LMB, 5 on RMB). Returns true if it hit one.
        bool QuickCraftHit(Vector2 global, int qty)
        {
            if (_quickTiles == null) return false;
            foreach (var (tile, bp) in _quickTiles)
                if (GodotObject.IsInstanceValid(tile) && new Rect2(tile.GlobalPosition, tile.Size).HasPoint(global))
                {
                    Player?.QuickCraft(bp, qty);
                    Refresh();   // ingredients consumed -> the craftable set + counts changed
                    return true;
                }
            return false;
        }

        /// <summary>A navbar tab was clicked. Only Craft is wired so far -- Skills/Information have no page yet,
        /// and silently doing nothing is better than pretending. Inventory is the page you are already on.</summary>
        void OnTab(string label) { }   // tabs route through PlayerController.ShowMenu now (MenuNavbar)

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
            if (!_magDemoFired && System.Environment.GetEnvironmentVariable("UG_MAGLOAD") == "1") { _magDemoFired = true; DebugStartLoadFirstMag(5004); }   // render harness: auto-load the first mag with 5.56
            else if (!_magDemoFired && System.Environment.GetEnvironmentVariable("UG_MAGUNLOAD") == "1") { _magDemoFired = true; DebugStartUnloadFirstMag(); }   // render harness: auto-unload the first loaded mag
            else if (!_magDemoFired && System.Environment.GetEnvironmentVariable("UG_MAGVERT") == "1") { _magDemoFired = true; DebugRotateFirstMag(); DebugStartLoadFirstMag(5004); }   // render harness: rotate the mag vertical + load it
            TickMagOps((float)delta);   // advance any active mag load/unload (one round every LOAD_INTERVAL)
            bool magFxActive = _magOps.Count > 0 || (_dragging && _dragJar != null && _dragJar.GetAsset()?.isAmmo == true);
            if (magFxActive || _magFxWasActive) _magFx?.QueueRedraw();   // one extra redraw on the falling edge -> the wheel CLEARS when the op finishes (full/empty/out), not lingers (master)
            _magFxWasActive = magFxActive;
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
            if (e is InputEventMouseButton wh && wh.Pressed && (wh.ButtonIndex == MouseButton.WheelUp || wh.ButtonIndex == MouseButton.WheelDown)
                && _storageCol != null && new Rect2(_storageCol.GlobalPosition, _storageCol.Size).HasPoint(wh.GlobalPosition) && _vscroll != null && _vscroll.Visible)
            {
                _vscroll.Value = Mathf.Clamp(_vscroll.Value + (wh.ButtonIndex == MouseButton.WheelUp ? -60 : 60), 0, _vscroll.MaxValue - _vscroll.Page);   // wheel over the box scrolls the clothing column
                GetViewport().SetInputAsHandled(); return;
            }
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
                    if (QuickCraftHit(mb.GlobalPosition, 1)) { GetViewport().SetInputAsHandled(); return; }   // LMB a quick-craft tile -> queue 1
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
                if (QuickCraftHit(rmb.GlobalPosition, 5)) { GetViewport().SetInputAsHandled(); return; }   // RMB a quick-craft tile -> queue 5
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
                else if (_dragging) { _dragTile.GlobalPosition = mm.GlobalPosition - _grab; _dragMouse = mm.GlobalPosition; }
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
            else if (Keybinds.IsDown(e) && _selPanel != null && Keybinds.HotbarSlot(e) is int hbNum && hbNum >= 3)
            {
                // RMB'd an item (its selection panel is open) + a hotbar 3-9 control -> BIND it to equip this item (master).
                // Keybinds.HotbarSlot so assign + equip share ONE rebindable key space (slots 1/2 = primary/secondary, not bound here).
                Player?.BindHotbar(hbNum, _selPage, _selX, _selY);
                CloseSelection();
                GetViewport().SetInputAsHandled();
            }
        }

        bool PointToHeaderIcon(Vector2 global, out EItemType type, out Control icon)
        {
            foreach (var (ic, t) in _headerIcons)
                if (GodotObject.IsInstanceValid(ic) && ic.IsVisibleInTree() && new Rect2(ic.GlobalPosition, ic.Size).HasPoint(global)) { type = t; icon = ic; return true; }
            type = default; icon = null; return false;
        }

        void StartDrag(Vector2 global)
        {
            if (PointToHeaderIcon(global, out var hType, out var hIcon))   // the small icon on a clothing tab: the same drag as pulling it off the paperdoll
            {
                var wornIt = WornFor(hType);
                if (wornIt == null) return;
                _dragFromCloth = true; _dragClothType = hType;
                _dragJar = new ItemJar(wornIt); _dragRot = 0;
                _grab = global - hIcon.GlobalPosition;
                _dragging = true;
                RebuildDragTile();
                return;
            }
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
            if (TryStartLoad(sp, sx, sy, page, x1, y1)) { CloseSelection(); Refresh(); return; }   // a loose round dropped onto a mag -> LOAD it (timed wheel), never a move/swap; both keep their slots (strawberry)
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

        // ===================== drag-load loose rounds into a magazine (strawberry) =====================
        // Drop a loose round onto a compatible mag -> a fill wheel adds a round every LOAD_INTERVAL until the mag is
        // full or the stack runs out. Compatibility is by the mag BODY (magCaliber): a STANAG body feeds 5.56 AND .300
        // BLK, an AUG body only 5.56, etc. A mag LOCKS to its first-loaded cartridge (no mixing) until it is emptied.

        // The RULE lives in core (SDG.Unturned.MagRules) so the server applies the identical gate. It used
        // to live here, and the server physically could not reach it -- core/UnturnedNet references
        // core/UnturnedSim and nothing references the game layer -- so giving the server the same check
        // would have meant writing it twice. Two copies of a validation rule is a worse version of the bug
        // this command exists to fix: instead of disagreeing about the magazine's STATE, the two sides
        // would disagree about the RULE, and the owner-inventory echo would silently revert whichever one
        // was wrong. These forward so the call sites below read unchanged.
        static int MagCap(ItemAsset ma) => MagRules.Capacity(ma);
        static string MagEffRound(Item mag, ItemAsset ma) => MagRules.EffectiveRound(mag, ma);
        static MagLoad CheckLoad(Item mag, ItemAsset ma, ItemAsset bullet) => (MagLoad)MagRules.CheckLoad(mag, ma, bullet);
        static string MagLoadMsg(MagLoad r) => MagRules.Message((MagLoadResult)r);

        ItemJar JarAt(byte page, byte x, byte y)
        {
            if (Inv?.items == null || page >= Inv.items.Length) return null;
            byte idx = Inv.items[page].getIndex(x, y);
            return idx == byte.MaxValue ? null : Inv.items[page].getItem(idx);
        }
        // Returns the page INDEX alongside the page object: the server addresses a slot by (page,x,y), and
        // recovering the index afterwards would mean a second search that can disagree with this one.
        (ItemJar jar, Items page, byte pageIndex) FindStack(ushort id)
        {
            if (Inv?.items != null)
                for (byte p = 0; p < Inv.items.Length; p++)
                {
                    var pg = Inv.items[p];
                    byte cnt = pg.getItemCount();
                    for (byte i = 0; i < cnt; i++)
                    {
                        var j = pg.getItem(i);
                        if (j?.item != null && j.item.id == id && j.item.amount > 0) return (j, pg, p);
                    }
                }
            return (null, null, byte.MaxValue);
        }

        // a loose round dropped on a cell -> if it holds a compatible mag, START a timed load (so the drop is CONSUMED,
        // not moved/swapped). Returns true for ANY bullet-onto-mag drop (even a refused one), so the caller skips the move.
        bool TryStartLoad(byte bp, byte bx, byte by, byte mp, byte mx, byte my)
        {
            var bJar = JarAt(bp, bx, by);
            var mJar = JarAt(mp, mx, my);
            if (bJar == null || mJar == null) return false;
            var bA = bJar.GetAsset(); var mA = mJar.GetAsset();
            if (bA == null || mA == null || !bA.isAmmo || !mA.IsMagazine) return false;   // only a loose-round-onto-magazine drop loads
            if (CheckLoad(mJar.item, mA, bA) == MagLoad.Ok)
            {
                _magOps.RemoveAll(o => o.page == mp && o.x == mJar.x && o.y == mJar.y);
                int batch = System.Math.Min((int)bJar.item.amount, MagCap(mA) - mJar.item.amount);   // load what we DRAGGED, capped by the mag's free space -> the wheel total is the dragged amount, not the capacity (master)
                _magOps.Add(new MagOp { page = mp, x = mJar.x, y = mJar.y, mag = mJar.item, bulletId = bA.id, unloading = false, t = 0f, batch = batch, done = 0 });
            }
            return true;   // consume the drop either way -- a refused load just snaps home, never swaps
        }

        void TickMagOps(float delta)
        {
            for (int i = _magOps.Count - 1; i >= 0; i--)
            {
                var op = _magOps[i];
                op.t += delta;
                int guard = 0;
                while (op.t >= LOAD_INTERVAL && guard++ < 64)
                {
                    op.t -= LOAD_INTERVAL;
                    if (!StepMagOp(op)) { _magOps.RemoveAt(i); break; }
                }
            }
        }
        // move ONE round; false when the op finishes (mag full/empty / out of rounds / bag full / no longer compatible).
        bool StepMagOp(MagOp op)
        {
            var mA = op.mag?.GetAsset();
            if (mA == null || op.done >= op.batch) return false;   // whole batch moved -> done
            if (op.unloading)   // eject a round back to the bag; stop cleanly if there's nowhere for it (never lose one)
            {
                if (op.mag.amount <= 0) return false;
                int bid = BulletIdForRound(MagEffRound(op.mag, mA) ?? mA.magRound);
                if (bid <= 0 || Inv == null || !Inv.tryAddItem(new SDG.Unturned.Item((ushort)bid, 1))) return false;
                op.mag.amount = (byte)(op.mag.amount - 1);
                if (op.mag.amount <= 0) op.mag.magLoadedRound = null;   // emptied -> unlock the cartridge
                // TELL THE SERVER. Without this the mutation above is local-only: the authoritative
                // inventory still holds a full magazine, and the next move of ANY item echoes it back and
                // undoes the unload in front of the player.
                Player?.NetMagLoad?.Invoke(op.page, op.x, op.y, op.mag.id,
                                           0, 0, 0, (ushort)bid, true);
                op.done++;
                return op.done < op.batch && op.mag.amount > 0;
            }
            // LOAD: pull a round from the stack into the mag
            if (op.mag.amount >= MagCap(mA)) return false;   // mag full (safety; batch is already capped to the free space)
            var bA = SDG.Unturned.Assets.find((ushort)op.bulletId);
            if (bA == null || CheckLoad(op.mag, mA, bA) != MagLoad.Ok) return false;
            var (jar, page, pageIdx) = FindStack((ushort)op.bulletId);
            if (jar == null) return false;   // out of that round
            if (op.mag.amount <= 0) op.mag.magLoadedRound = bA.magRound;   // empty -> LOCK to this cartridge
            op.mag.amount = (byte)(op.mag.amount + 1);
            // Sent BEFORE the stack is decremented, while jar still names the slot the round came from --
            // the server addresses the source by grid position, and removeItem below can free it.
            Player?.NetMagLoad?.Invoke(op.page, op.x, op.y, op.mag.id,
                                       pageIdx, jar.x, jar.y, (ushort)op.bulletId, false);
            jar.item.amount = (byte)(jar.item.amount - 1);
            if (jar.item.amount <= 0) { byte ri = page.getIndex(jar.x, jar.y); if (ri != byte.MaxValue) page.removeItem(ri); }
            op.done++;
            return op.done < op.batch;
        }
        int BulletIdForRound(string round)   // the loose-round item id for a cartridge (reverse of bullet.magRound)
        {
            if (string.IsNullOrEmpty(round)) return 0;
            foreach (var a in SDG.Unturned.Assets.all())
                if (a.isAmmo && a.magRound == round) return a.id;
            return 0;
        }

        // ---- the fill wheel + drag-over compat hint, drawn on _magFx ----
        bool MagRect(byte page, byte x, byte y, ItemAsset ma, int rot, out Rect2 r)
        {
            r = default;
            if (page >= (Inv?.items?.Length ?? 0)) return false;
            foreach (var (p, c, slot) in _drop)
                if (p == page && IsInstanceValid(c))
                {
                    Vector2 tl = c.GlobalPosition + (slot ? Vector2.Zero : new Vector2(x * CELL, y * CELL));
                    int sxc = ma != null ? System.Math.Max(1, (int)ma.size_x) : 1;
                    int syc = ma != null ? System.Math.Max(1, (int)ma.size_y) : 1;
                    if (rot % 2 == 1) { int t = sxc; sxc = syc; syc = t; }   // rotated mag -> swapped footprint (2x1 becomes 1x2) so the wheel centres on the real cells
                    r = new Rect2(tl, new Vector2(sxc * CELL, syc * CELL));
                    return true;
                }
            return false;
        }
        void DrawMagFx()
        {
            foreach (var op in _magOps)
            {
                var jar = JarAt(op.page, op.x, op.y);
                if (jar == null || jar.GetAsset()?.IsMagazine != true) continue;
                if (MagRect(op.page, op.x, op.y, jar.GetAsset(), jar.rot, out Rect2 r))
                    DrawWheel(r, op.unloading ? op.batch - op.done : op.done, op.batch, op.unloading);   // load fills done/batch; unload empties (remaining = batch-done)/batch
            }
            if (_dragging && _dragJar != null)
            {
                var bA = _dragJar.GetAsset();
                if (bA != null && bA.isAmmo && PointToCell(_dragMouse, out byte hp, out byte hx, out byte hy, out _, out _))
                {
                    var mJar = JarAt(hp, hx, hy);
                    var mA = mJar?.GetAsset();
                    if (mJar != null && mA != null && mA.IsMagazine && MagRect(hp, mJar.x, mJar.y, mA, mJar.rot, out Rect2 hr))
                        DrawLoadHint(hr, CheckLoad(mJar.item, mA, bA));
                }
            }
        }
        void DrawWheel(Rect2 area, int filled, int total, bool unloading)
        {
            if (total <= 0) return;
            Vector2 c = area.Position + area.Size / 2f;
            float rOut = Mathf.Min(area.Size.X, area.Size.Y) * 0.40f;   // 10% smaller (master); no centre counter
            float rIn = rOut * 0.58f;
            float top = -Mathf.Pi / 2f;   // ALWAYS start at 12 o'clock (master); the wheel is POSITIONED on the rotated mag (MagRect), but the fill start itself doesn't rotate
            _magFx.DrawCircle(c, rOut + 3f, new Color(0f, 0f, 0f, 0.55f));   // dim backing so it reads over the icon
            DrawAnnularSector(c, rIn, rOut, top, top + Mathf.Tau, new Color(1f, 1f, 1f, 0.16f));   // the empty ring (full, dim)
            float frac = Mathf.Clamp(filled / (float)total, 0f, 1f);   // a CONTINUOUS filled arc that grows one round-step (1/total) at a time (master)
            if (frac > 0f)
                DrawAnnularSector(c, rIn, rOut, top, top + Mathf.Tau * frac, unloading ? UITheme.WheelUnload : UITheme.WheelLoad);
        }
        void DrawAnnularSector(Vector2 c, float rIn, float rOut, float a0, float a1, Color col)
        {
            int steps = System.Math.Max(2, (int)((a1 - a0) / 0.12f) + 1);
            var pts = new Vector2[(steps + 1) * 2];
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)steps);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                pts[i] = c + dir * rOut;
                pts[(steps + 1) * 2 - 1 - i] = c + dir * rIn;
            }
            _magFx.DrawColoredPolygon(pts, col);
        }
        void DrawLoadHint(Rect2 area, MagLoad res)
        {
            bool ok = res == MagLoad.Ok;
            _magFx.DrawRect(area, ok ? UITheme.DropOkFill : UITheme.DropBlockedFill, true);
            _magFx.DrawRect(area, ok ? UITheme.DropOkEdge : UITheme.DropBlockedEdge, false, 2f);
            string msg = ok ? "Load" : MagLoadMsg(res);
            var font = _magFx.GetThemeDefaultFont();
            if (font == null || string.IsNullOrEmpty(msg)) return;
            int fs = 15;
            Vector2 sz = font.GetStringSize(msg, HorizontalAlignment.Center, -1, fs);
            Vector2 p = area.Position + new Vector2((area.Size.X - sz.X) / 2f, area.Size.Y / 2f + sz.Y / 2f);
            _magFx.DrawString(font, p + new Vector2(1, 1), msg, HorizontalAlignment.Left, -1, fs, new Color(0f, 0f, 0f, 0.85f));
            _magFx.DrawString(font, p, msg, HorizontalAlignment.Left, -1, fs, ok ? new Color(0.72f, 1f, 0.78f) : new Color(1f, 0.72f, 0.68f));
        }

        // seam: start a load on the mag at a cell (render harness + tests -- headless can't drag-drop).
        public bool DebugStartLoad(byte page, byte x, byte y, ushort bulletId)
        {
            var mJar = JarAt(page, x, y);
            var mA = mJar?.GetAsset();
            if (mJar == null || mA == null || !mA.IsMagazine) return false;
            _magOps.RemoveAll(o => o.page == page && o.x == mJar.x && o.y == mJar.y);
            var (bjar, _, _) = FindStack(bulletId);   // FindStack also returns the page INDEX now (the server addresses slots by it)
            int batch = System.Math.Min(bjar != null ? (int)bjar.item.amount : int.MaxValue, MagCap(mA) - mJar.item.amount);
            _magOps.Add(new MagOp { page = page, x = mJar.x, y = mJar.y, mag = mJar.item, bulletId = bulletId, unloading = false, t = 0f, batch = batch, done = 0 });
            return true;
        }
        public int DebugMagRounds(byte page, byte x, byte y) => JarAt(page, x, y)?.item.amount ?? -1;
        public bool DebugLoadActive => _magOps.Count > 0;
        // RMB "Unload": start an unload op on the selected mag -> the wheel empties, rounds return to the bag.
        void UnloadSelected()
        {
            var jar = JarAt(_selPage, _selX, _selY);
            var ma = jar?.GetAsset();
            if (jar != null && ma != null && ma.IsMagazine && jar.item.amount > 0)
            {
                _magOps.RemoveAll(o => o.page == _selPage && o.x == jar.x && o.y == jar.y);
                _magOps.Add(new MagOp { page = _selPage, x = jar.x, y = jar.y, mag = jar.item, bulletId = 0, unloading = true, t = 0f, batch = jar.item.amount, done = 0 });   // eject the WHOLE mag
            }
            CloseSelection();
        }
        public bool DebugStartLoadFirstMag(ushort bulletId)   // scan for the first magazine + start loading it
        {
            if (Inv?.items == null) return false;
            for (byte p = 0; p < Inv.items.Length; p++)
            {
                byte cnt = Inv.items[p].getItemCount();
                for (byte i = 0; i < cnt; i++)
                {
                    var j = Inv.items[p].getItem(i);
                    if (j?.GetAsset()?.IsMagazine == true) return DebugStartLoad(p, j.x, j.y, bulletId);
                }
            }
            return false;
        }
        public bool DebugRotateFirstMag()   // render harness: rotate the first mag to vertical to eyeball its icon + the wheel
        {
            if (Inv?.items == null) return false;
            for (byte p = 0; p < Inv.items.Length; p++)
            {
                byte cnt = Inv.items[p].getItemCount();
                for (byte i = 0; i < cnt; i++)
                {
                    var j = Inv.items[p].getItem(i);
                    if (j?.GetAsset()?.IsMagazine == true) { j.rot = 1; Refresh(); return true; }
                }
            }
            return false;
        }
        public bool DebugStartUnloadFirstMag()   // scan for the first LOADED mag + start unloading it (render harness)
        {
            if (Inv?.items == null) return false;
            for (byte p = 0; p < Inv.items.Length; p++)
            {
                byte cnt = Inv.items[p].getItemCount();
                for (byte i = 0; i < cnt; i++)
                {
                    var j = Inv.items[p].getItem(i);
                    if (j?.GetAsset()?.IsMagazine == true && j.item.amount > 0)
                    {
                        _magOps.Add(new MagOp { page = p, x = j.x, y = j.y, mag = j.item, bulletId = 0, unloading = true, t = 0f, batch = j.item.amount, done = 0 });
                        return true;
                    }
                }
            }
            return false;
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

        /// <summary>Turn the quick-move's "somewhere with room" into the explicit (page, x, y, rot) the wire
        /// needs. dest 255 = the first of my OWN pages with space, walked in the same order tryAddItem uses
        /// (SLOTS..PAGES-2), so the routed result lands where the local path would have put it.
        ///
        /// Holster pages are skipped for a 255 search on purpose: tryAddItem does not auto-fill them either, and
        /// quick-moving a rifle should not silently holster it.</summary>
        bool ResolveMoveDest(ItemJar jar, byte dest, out byte page, out byte x, out byte y, out byte rot)
        {
            page = x = y = rot = 0;
            if (jar?.item == null) return false;
            if (dest != 255)
            {
                var pg = Inv.items[dest];
                if (pg == null || !pg.tryFindSpace(jar.size_x, jar.size_y, out x, out y, out rot)) return false;
                page = dest;
                return true;
            }
            for (byte p = PlayerInventory.SLOTS; p < (byte)(PlayerInventory.PAGES - 2); p++)
            {
                var pg = Inv.items[p];
                if (pg != null && pg.tryFindSpace(jar.size_x, jar.size_y, out x, out y, out rot)) { page = p; return true; }
            }
            return false;
        }

        // Move a jar out of `page` into `dest` (255 = first of my own pages with room). Puts it back if the
        // destination has no room, so a failed quick-move can never eat an item.
        bool MoveTo(byte page, byte idx, ItemJar jar, byte dest)
        {
            // SERVER-OWNED BAG: the transfer has to be a REQUEST, exactly like the drag path above. This used to
            // be the bare local remove+add below on every path, so "Store" visually moved a medkit into a crate,
            // the server never heard about it, and CloseStorage wrote the crate's UNCHANGED page back -- the item
            // had never left your bag. Ctrl+RMB and the ground-take branch of CtrlGrab route through here too, so
            // all three were inert. The drag immediately above it always routed, which is what marks this as an
            // oversight rather than a decision. Review 2026-08-16.
            //
            // The wire needs an EXPLICIT destination cell where the quick-move only has "somewhere with room", so
            // resolve one against the local grid first. If the server disagrees (someone else took the cell) the
            // move simply fails and the echo repaints -- the same losing race the drag path already accepts.
            if (Player != null && Player.InventoryIsServerOwned)
            {
                if (!ResolveMoveDest(jar, dest, out byte dp, out byte dx, out byte dy, out byte drot)) return false;
                if (!Player.RequestMoveItem(page, jar.x, jar.y, dp, dx, dy, drot)) return false;
                if (page != PlayerInventory.AREA) PlayInventoryAudio(jar);
                CloseSelection(); Refresh();
                return true;
            }
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
            // SERVER-OWNED BAG: the whole swap is an INTENT, and the echo brings back the result. Done locally
            // it was reverted a moment later -- the backpack went back into the bag and its page re-sized to
            // 0x0 -- so a dragged-on bag un-equipped itself. The on-body VISUAL still updates locally off the
            // adopted worn refs (PlayerClothingController reads them), so the paperdoll does not wait on the
            // round trip. Review 2026-08-16.
            if (Player != null && Player.InventoryIsServerOwned && Player.NetWearClothing != null)
            {
                Player.NetWearClothing(page, x, y, (byte)slotType);
                return true;
            }
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
            // Server-owned: an intent, same as WearFromGrid. Locally this resized the bag page to 0x0 and
            // DISCARDED every jar in it (Items.loadSize drops what no longer fits) until the echo restored them.
            if (Player != null && Player.InventoryIsServerOwned && Player.NetUnwearClothing != null)
            {
                Player.NetUnwearClothing((byte)slotType);
                return true;
            }
            // Everything INSIDE the garment first (master 2026-09-03): pull the page's items out, then unwear (the page
            // goes 0x0), then each item tries every remaining page and anything that doesn't fit drops at your feet.
            var spill = new List<Item>();
            byte spillPage = slotType switch { EItemType.BACKPACK => PlayerInventory.BACKPACK, EItemType.VEST => PlayerInventory.VEST, EItemType.SHIRT => PlayerInventory.SHIRT, EItemType.PANTS => PlayerInventory.PANTS, _ => byte.MaxValue };
            if (spillPage != byte.MaxValue && spillPage < Inv.items.Length)
            {
                var pg = Inv.items[spillPage];
                for (int i = pg.getItemCount() - 1; i >= 0; i--) { var j = pg.getItem((byte)i); if (j?.item != null) spill.Add(j.item); pg.removeItem((byte)i); }
            }
            UnwearVisual(slotType);   // clears the worn slot + the on-body visual (+ resizes a bag page to 0x0)
            ReturnToGrid(old);        // the garment itself: a free slot, else the ground
            foreach (var it in spill) ReturnToGrid(it);   // ReturnToGrid = tryAddItem across the pages, else DropWorldItem
            return true;
        }

        // demo/verify seams (headless can't drive the mouse): run the SAME equip/unequip core the drop handler uses.
        public bool DebugWearFromGrid(EItemType slotType, byte page, byte x, byte y) { bool r = WearFromGrid(slotType, page, x, y); Refresh(); return r; }
        public bool DebugTakeOff(EItemType slotType) { bool r = TakeOff(slotType); Refresh(); return r; }

        /// <summary>Drive a REAL grid-to-grid drag+drop through the actual Drop() handler and its PointToCell
        /// hit-test -- the gesture a player makes, not the Inv.TryDrag model call underneath it.
        ///
        /// This existed only for clothing slots (DebugDropGestureOnSlot), which is why an entire class of bug had no
        /// instrument: a drag that the model accepts but the UI silently drops lands exactly in the gap between
        /// inv.* (model-only) and nothing at all.
        ///
        /// `layoutValid` false = the target grid Control has no real size, so the hit-test is meaningless and the
        /// caller must SKIP rather than record a pass. Reported rather than asserted for the same reason the
        /// clothing one does it: a headless "false" would otherwise read as a passing test of nothing.</summary>
        public bool DebugDropGestureOnCell(byte page, byte x, byte y, byte tPage, byte tx, byte ty, out bool layoutValid)
        {
            layoutValid = false;
            if (Inv == null || page >= Inv.items.Length || tPage >= Inv.items.Length) return false;
            var pg = Inv.items[page];
            byte idx = pg.getIndex(x, y);
            if (idx == byte.MaxValue) return false;
            Control grid = null;
            foreach (var (p, c, slot) in _drop) if (p == tPage && !slot) { grid = c; break; }
            if (grid == null) return false;
            layoutValid = grid.Size.X > 1f && grid.Size.Y > 1f;
            if (!layoutValid) return false;
            _dragFromCloth = false; _dragJar = pg.getItem(idx);
            _dragPage = page; _dragX0 = _dragJar.x; _dragY0 = _dragJar.y; _dragRot = _dragJar.rot;
            _grab = Vector2.Zero; _dragging = true;
            // Drop() adds half a cell to snap to the nearest, so hand it the target cell's TOP-LEFT: top-left +
            // CELL/2 lands mid-cell and PointToCell floors back to (tx,ty). Passing the centre would land in the
            // next cell over and silently test the wrong target.
            Drop(grid.GlobalPosition + new Vector2(tx * CELL, ty * CELL));
            Refresh();
            return true;
        }

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
            name.AddThemeFontSizeOverride("font_size", UITheme.FontTitle);
            panel.AddChild(name);
            var info = new Label { Text = $"{asset.rarity}  ·  {asset.type}  ·  {asset.size_x}x{asset.size_y}",
                                   Position = new Vector2(228, 46), Size = new Vector2(258, 20) };
            info.AddThemeColorOverride("font_color", rar.Lerp(UITheme.TextDim, 0.5f));
            info.AddThemeFontSizeOverride("font_size", UITheme.FontLabel);
            panel.AddChild(info);
            // the real localized Description (from the item's English.dat)
            var desc = new Label { Text = asset.description, Position = new Vector2(228, 72), Size = new Vector2(258, 70) };
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            desc.AddThemeColorOverride("font_color", UITheme.TextBody);
            desc.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            panel.AddChild(desc);
            // a fluid CONTAINER shows its live contents (the "tooltip" strawberry wanted back): type + amount + capacity
            if (asset.IsFluidContainer && jar.item != null)
            {
                FluidItem.Read(jar.item, asset, out var ct, out var ca, out var cq);
                string contents = (ca <= 0.001f || ct == FluidType.None)
                    ? $"Contents: empty  ·  holds {FluidDef.Litres(asset.fluidCapacity)}"
                    : $"Contents: {FluidDef.Litres(ca)} {FluidDef.WaterName(ct, cq)}  ·  of {FluidDef.Litres(asset.fluidCapacity)}";
                var ccol = ct == FluidType.None ? new Color(0.75f, 0.75f, 0.75f) : FluidDef.WaterColor(ct, cq).Lerp(Colors.White, 0.3f);
                var cl = new Label { Text = contents, Position = new Vector2(228, 120), Size = new Vector2(258, 22) };
                cl.AddThemeColorOverride("font_color", ccol);
                cl.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
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
                fl.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
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
                else if (asset.type == EItemType.FISHER)
                    AddActionButton(panel, "Equip", new Vector2(228, by), FisherSelected);   // a fishing rod -> hold it, LMB casts (UseableFisher)
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
            if (asset.IsMagazine && jar.item != null && jar.item.amount > 0)   // a loaded mag: RMB Unload -> eject its rounds back to the bag, the wheel emptying (strawberry: rmb menu, not drag)
            { AddActionButton(panel, "Unload", new Vector2(228, by), UnloadSelected); by += 44; }
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
                || DeployableDef.ById(asset.id) != null || ToolDef.ById(asset.id) != null || asset.IsFuelContainer || asset.IsFluidContainer
                || asset.type == EItemType.FISHER);   // a rod is holdable (EquipHeldFisher); without this it has the equip code but NO menu option (the Rope bug)

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
            // TO ITS PREFERRED SLOT, SWAPPING WHAT IS THERE (strawberry 2026-08-16: "equipping a gun via the
            // rmb context menu will send it to its preferred slot, swapping whatever is in that slot"). The
            // preference comes from the .dat's Slot key: a sidearm goes to the hip, a rifle to the primary, and
            // a rifle is never put in the secondary at all.
            //
            // An EMPTY compatible slot still wins over evicting someone. A sidearm with a full secondary and an
            // empty primary should holster into the primary rather than throwing the other sidearm out -- the
            // instruction is about where it PREFERS to go, not about always displacing.
            if (_selPage >= PlayerInventory.SLOTS && asset != null)
            {
                int want = asset.slot.PreferredSlot();
                if (want >= 0)
                {
                    byte slot = (byte)want;
                    if (Inv.items[slot].getItemCount() > 0)
                        for (byte alt = 0; alt < PlayerInventory.SLOTS; alt++)
                            if (asset.slot.CanEquipInPage(alt) && Inv.items[alt].getItemCount() == 0) { slot = alt; break; }
                    if (Player == null || !Player.RequestEquipItem(_selPage, _selX, _selY, slot))
                        Inv.TryDrag(_selPage, _selX, _selY, slot, 0, 0, 0);   // TryDrag SWAPS when the destination is occupied
                    // Address, not just the page: a holster is single-item so the item lands at (0,0). The cell is
                    // what lets the held reference survive an owner echo (PlayerController.RebindHeldRefs).
                    Player?.NoteHeldFrom(slot, 0, 0);   // so emptying that slot later pulls it out of the hands
                }
            }
            else if (_selPage < PlayerInventory.SLOTS) Player?.NoteHeldFrom(_selPage, _selX, _selY);
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


        // Equip a fishing rod INTO the hands -> close the inventory so LMB casts. Routes through EquipItemAsset (the
        // unified dispatch that owns the EItemType.FISHER -> EquipHeldFisher branch), like the other hand actions.
        void FisherSelected()
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || asset.type != EItemType.FISHER) return;
            Player?.EquipHeldFisher(asset, jar.item);
            CloseSelection();
            Close();   // leave the inventory so LMB begins the cast
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        void ToggleAutoDrinkSelected()   // flip a fluid container's autodrink; reopen the panel so the button label updates
        {
            var pg = Inv.items[_selPage];
            byte idx = pg.getIndex(_selX, _selY);
            if (idx == byte.MaxValue) return;
            var jar = pg.getItem(idx);
            var asset = jar.GetAsset();
            if (asset == null || !asset.IsFluidContainer || jar.item == null) return;
            bool want = !jar.item.autoDrink;
            jar.item.autoDrink = want;               // locally first, so the label flips on this frame either way
            // ...then tell the server, or the next owner echo hands the old value straight back. autoDrink is on
            // the item wire but had no server-side writer at all, so the server's copy was the `= true` field
            // initialiser for the life of the session and any inventory move switched autodrink back ON.
            Player?.RequestSetAutoDrink(_selPage, _selX, _selY, jar.item.id, want);
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
        MenuNavbar _navbar;
        void BuildNavbar()   // the SHARED strip (MenuNavbar): identical geometry on every menu, live key labels
        {
            _navbar = MenuNavbar.Build(_dash, MenuNavbar.Tab.Inventory, t => Player?.ShowMenu(t), () => { Close(); Input.MouseMode = Input.MouseModeEnum.Captured; });
        }

        void BuildNameBadge(Panel box)
        {
            var badge = new Panel { Position = new Vector2(8, 6), Size = new Vector2(CHARW - 16, 76) };
            StyleBox(badge, UI_BAR);
            box.AddChild(badge);
            var av = new Panel { Position = new Vector2(10, 10), Size = new Vector2(56, 56) };
            StyleBox(av, UI_TAB_OFF);
            badge.AddChild(av);
            if (PlayerProfile.HasAvatar)   // the launcher's picture (UG_PROFILE_PNG, already size/format-checked by PlayerProfile) fills the square
            {
                var img = new Image();
                if (img.LoadPngFromBuffer(PlayerProfile.AvatarPng) == Error.Ok)
                {
                    // ExpandMode BEFORE Size: with the default expand mode the 128 px texture clamps the control's minimum size, and a
                    // Size set first stays 128 (the picture spilled out of the 56 px square in the first render).
                    var pic = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
                    pic.Texture = ImageTexture.CreateFromImage(img);
                    pic.Position = new Vector2(2, 2); pic.Size = new Vector2(52, 52);
                    av.AddChild(pic);
                }
            }
            // Survivor + faction/rank[rep], vertically CENTERED as a block in the badge (master 2026-08-26).
            var textCol = new VBoxContainer { Position = new Vector2(78, 0), Size = new Vector2(CHARW - 16 - 78 - 40, 76) };
            textCol.Alignment = BoxContainer.AlignmentMode.Center; textCol.AddThemeConstantOverride("separation", 0);
            textCol.MouseFilter = Control.MouseFilterEnum.Ignore;
            badge.AddChild(textCol);
            var uname = new Label { Text = string.IsNullOrEmpty(PlayerProfile.Name) ? "Survivor" : PlayerProfile.Name };   // the launcher name (UG_USERNAME); "Survivor" only when none is set
            uname.AddThemeColorOverride("font_color", UITheme.Accent); uname.AddThemeFontSizeOverride("font_size", 28);   // yellow username
            textCol.AddChild(uname);
            var fac = new Label { Text = "Neutral [0]" };
            fac.AddThemeColorOverride("font_color", UITheme.TextBody); fac.AddThemeFontSizeOverride("font_size", 18);
            textCol.AddChild(fac);
            var plus = new Label { Text = "+", Position = new Vector2(CHARW - 56, 20) };
            plus.AddThemeColorOverride("font_color", UITheme.Accent); plus.AddThemeFontSizeOverride("font_size", 30);
            badge.AddChild(plus);
        }

        // The left CHARACTER panel (source characterBox, 410px): the 3D paperdoll at the top, the worn-clothing equip
        // slots below it, and the two weapon slots pinned to the BOTTOM (repositioned in LayoutDash). Built once.
        void BuildCharacterPanel()
        {
            _charBox = new Panel { Position = new Vector2(MARGIN, NAVH + MARGIN), Size = new Vector2(CHARW, 600) };   // height fixed up in LayoutDash
            StyleBox(_charBox, UI_PANEL);   // translucent + blurred like the REST of the UI (master 2026-08-26: "apply the transparency + blur the rest has"); the model floats on it with NO box
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
                lbl.AddThemeColorOverride("font_color", UITheme.TextBody);
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
            // C/S/M cosmetic-swap buttons AND the rotation slider both removed (master 2026-08-26). Row kept empty so
            // LayoutDash's null-check stays valid; drag the paperdoll itself to spin it (see the spin branch in _Input).
        }

        // Build the 3D paperdoll: a dark stage + an isolated SubViewport rendering a preview character clothed off the
        // player's worn slots, surfaced in a SubViewportContainer you can drag to spin. Built once (BuildCharacterPanel runs once).
        void BuildPaperdoll(Panel box)
        {
            var stage = new Panel { Position = new Vector2(8, PDTOP), Size = new Vector2(PDW, PDH) };
            _pdStage = stage;
            StyleBox(stage, new Color(0f, 0f, 0f, 0f));   // NO box (master 2026-08-26: "ideally i want NO box") -- the model floats straight on the character panel; the opaque panel below hides any world-bleed
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

            _pdVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30f, 150f, 0f), LightEnergy = 0.75f });   // key: 1.2 + ACES blew the head out to near-white (master 2026-09-03 "fix the lighting")                                          // key
            _pdVp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-8f, -35f, 0f), LightEnergy = 0.35f, LightColor = UITheme.Text }); // NEUTRAL fill (master: no blue tint). The world env does not reach an isolated SubViewport, so this is the ONLY light on the paperdoll -- a cool one tinted the character too, not just the panels.
            _pdVp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.42f, 0.42f, 0.44f), AmbientLightEnergy = 1.0f,   // neutral grey, was faintly blue
                    TonemapMode = Godot.Environment.ToneMapper.Filmic,   // ACES crushed the lit side to white; filmic keeps the shirt/skin colour
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
            float frameH = ab.Size.Y * 1.36f;                     // master 2026-08-26: reverted the +padding (was 1.51f). Arms fit via the WIDER viewport now, not by shrinking him.
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
            _quickTiles.Clear();   // quick-craft is rebuilt as a dashboard section in the layout below

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
            _drop.Clear(); _headerIcons.Clear();
            if (!_foldTestApplied && byte.TryParse(System.Environment.GetEnvironmentVariable("UG_INVFOLD"), out var _fp)) { _collapsed.Add(_fp); _foldTestApplied = true; }   // render harness: start with a page folded

            // weapon slots -> the character panel's bottom row (source: primary/secondary sit under the character). They
            // register as page-0/1 drop targets inside AddSlotAt, so they MUST be built AFTER _drop.Clear() -- previously
            // they were added BEFORE the clear, so their drag targets got wiped every refresh and the primary/secondary
            // slots silently rejected any dragged weapon (master: "make primary/secondary function as inv slots"). The 1/2
            // keys already equip pages 0/1 (PlayerController.EquipHotbar), so registering the drop targets completes it.
            float wSlotW = (CHARW - 2 * MARGIN - 16) / 2f;   // scale the two slots up to fill the panel width (master 2026-08-26)
            AddSlotAt(_weaponRow, "PRIMARY",   0, new Vector2(0, 0),           wSlotW);
            AddSlotAt(_weaponRow, "SECONDARY", 1, new Vector2(wSlotW + 16, 0), wSlotW);
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
                yC += _collapsed.Contains(page) ? HDRH + 10 : pg.height * CELL + GRIDPAD + PAGEADV;   // source: y += items.SizeOffset_Y + 80; a folded tab is header-height only
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
            yA = BuildQuickCraftSection(aCol, yA, colW);   // Quick Craft as a section under Nearby (master), not a floating panel
            _storageW = boxW;
            // SCROLL (master 2026-09-03): the clothing column can outgrow the screen; clip the box and hang a scrollbar on its right.
            float visibleH = vpsz.Y - NAVH - MARGIN;   // to the bottom of the screen (master 2026-09-03: "the scrollable region should go all the way to the bottom")
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_INVSCROLLTEST"), out var _vh) && _vh > 100f) visibleH = _vh;   // render harness: cap the box height so the scrollbar shows on a short column
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_INVSCROLLY"), out var _sy) && !_scrollTestApplied) { _scrollY = _sy; _scrollTestApplied = true; }   // render harness: pre-scrolled
            _storageCol.ClipContents = true; _storageCol.Size = new Vector2(boxW, visibleH);
            float maxScroll = Mathf.Max(0f, yC - visibleH);
            _scrollY = Mathf.Clamp(_scrollY, 0f, maxScroll);
            _clothingCol.Position = new Vector2(0f, -_scrollY);
            if (_vscroll == null) { _vscroll = new VScrollBar { Step = 10 }; _vscroll.ValueChanged += v => { _scrollY = (float)v; _clothingCol.Position = new Vector2(0f, -_scrollY); }; _storageCol.AddChild(_vscroll); }
            _vscroll.Visible = maxScroll > 0f;
            _vscroll.MaxValue = yC; _vscroll.Page = visibleH; _vscroll.SetValueNoSignal(_scrollY);
            _vscroll.Position = new Vector2(Mathf.Min(colW, 8 * CELL) + 8f, 0f); _vscroll.Size = new Vector2(14f, visibleH);   // hugs the widest grid (8 cells), not the split -- it was landing on the Nearby column
            _storageH = Mathf.Max(yC, split ? yA : yA) - 10f;   // source ContentSizeOffset = y - 10

            LayoutDash();
        }

        void LayoutDash()
        {
            Vector2 vp = GetViewport().GetVisibleRect().Size;
            if (_charBox != null)
            {
                _charBox.Size = new Vector2(CHARW, Mathf.Max(600f, vp.Y - NAVH - 2 * MARGIN));   // fill the height below the navbar
                if (_weaponRow != null) _weaponRow.Position = new Vector2(12, _charBox.Size.Y - CELL - (HEADER - 6) - MARGIN - 215);   // master 2026-09-03: "move primary and secondary up a bit so they are above the vitals bars" (was -155: the slots touched the bars)   // lifted ABOVE the layer-12 vitals (~180px up from the panel bottom) so they don't collide (master 2026-08-26)
                // rotation slider + cosmetic buttons sit in the reserved COSMH strip just above the weapon slots
                if (_cosmeticRow != null) _cosmeticRow.Position = new Vector2(0, _charBox.Size.Y - CELL - (HEADER - 6) - MARGIN - COSMH);

                // The paperdoll: a portrait viewport CENTERED in the character panel with margin all round
                // (master 2026-08-26: "shrink the whole viewport down by 25%, centered in its little frame"). It
                // fills 75% of the vertical region and keeps the arms-fitting aspect (PD_ASPECT), then sits centered.
                float avail = _charBox.Size.Y - PDTOP - CELL - (HEADER - 6) - 2 * MARGIN - 10f - COSMH;
                float pdH = avail * 0.75f;                            // 25% smaller than the full region
                float pdW = Mathf.Min(pdH * PD_ASPECT, CHARW - 2 * MARGIN);   // preserve the aspect so his arms fit; never wider than the panel
                float pdX = Mathf.Round((CHARW - pdW) / 2f);          // centered horizontally in the panel
                float pdY = Mathf.Round(PDTOP + (avail - pdH) / 2f);  // centered vertically in the region
                if (_pdVp != null && Mathf.Abs(_pdVp.Size.Y - pdH) > 1f) _pdFramed = false;   // re-frame for the new size
                if (_pdStage != null) { _pdStage.Position = new Vector2(pdX, pdY); _pdStage.Size = new Vector2(pdW, pdH); }
                if (_pdHit != null)   { _pdHit.Position   = new Vector2(pdX, pdY); _pdHit.Size   = new Vector2(pdW, pdH); }
                if (_pdVp != null)    _pdVp.Size = new Vector2I(Mathf.RoundToInt(pdW), Mathf.RoundToInt(pdH));
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
            col.AddChild(HeaderBar(name, pos, colW, worn, worn == null ? page.getItemCount() : -1, page.page));
            if (_collapsed.Contains(page.page)) return;   // folded: header only, the items stay in the page untouched
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
        /// <summary>Item icon by id, cached. Public so the HUD's hotbar draws the SAME texture the bag does --
        /// a second loader would be a second cache and a second chance to disagree about what an item looks
        /// like.</summary>
        public static Texture2D IconFor(int id) => Icon(id);

        static Texture2D Icon(int id)
        {
            if (_iconCache.TryGetValue(id, out var t)) return t;
            t = null;
            var p = ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png");
            if (System.IO.File.Exists(p)) { var img = ContentProvider.LoadImage(p); if (img != null) t = ImageTexture.CreateFromImage(img); }
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
            var col = new Color(0.98f, 0.98f, 0.98f);
            float c = (N - 1) / 2f, len = 13f;
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
                        for (float t = 0; t <= 4f; t += 0.5f) Dot(bx + bdx * t, by + bdy * t);
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

            // A rotated MAGAZINE uses the SHARED stand-up transform (AttachmentMenu.LoadItemIcon: Rotate90 CCW + FlipX,
            // with a DrawnWiderThanTall guard) so it stands feed-lips-UP and UN-mirrored -- a plain -90 rotate leaves it
            // mirrored, and a mag is symmetric enough that the reflection reads as fine by eye (tinyclaw). Reuse the
            // transform, don't copy it (same as CheckLoad -> MagRules).
            bool magStandUp = rotated && asset != null && asset.IsMagazine;
            var tex = magStandUp ? AttachmentMenu.LoadItemIcon(asset.id, standUp: true) : (asset != null ? Icon(asset.id) : null);
            if (tex != null)   // the real item icon fills the tile (like SleekItem's rendered item image)
            {
                var ic = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                ic.MouseFilter = Control.MouseFilterEnum.Ignore;
                int pad = (int)(CELL * 0.12f);   // breathing room around every icon inside its cell(s) (master: pad the icons)
                if (rotated && !magStandUp)   // non-mag rotated item: spin the raw icon 90 CW to follow the jar
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

            if (jar.item != null && (jar.item.amount > 1 || asset?.IsMagazine == true) && !_magOps.Exists(o => o.mag == jar.item))   // stacks show >1; a magazine ALWAYS shows its round count, incl. x0 when empty (master) -- but not while its fill WHEEL is up (the wheel shows N/cap)
            {
                var amt = new Label { Text = "x" + jar.item.amount, Position = new Vector2(0, h - 20), Size = new Vector2(w - 4, 18) };
                amt.HorizontalAlignment = HorizontalAlignment.Right;
                amt.AddThemeColorOverride("font_color", Colors.White);
                amt.AddThemeColorOverride("font_outline_color", Colors.Black);
                amt.AddThemeConstantOverride("outline_size", 3);
                amt.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                amt.MouseFilter = Control.MouseFilterEnum.Ignore;
                tile.AddChild(amt);
            }

            if (asset?.IsFuelContainer == true && jar.item != null)   // a gas can ALWAYS shows a fuel-level bar on its icon, even at 0 (master)
            {
                float frac = asset.fuelCapacity > 0f ? Mathf.Clamp(Mathf.Max(0f, jar.item.fuelLevel) / asset.fuelCapacity, 0f, 1f) : 0f;
                tile.AddChild(new ColorRect { Color = UITheme.Border, Position = new Vector2(2, h - 10), Size = new Vector2(w - 4, 8), MouseFilter = Control.MouseFilterEnum.Ignore });   // black outline -> visible on any icon
                tile.AddChild(new ColorRect { Color = new Color(0.32f, 0.32f, 0.35f, 1f), Position = new Vector2(3, h - 9), Size = new Vector2(w - 6, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // empty track (grey) -> the bar reads even at 0
                if (frac > 0f) tile.AddChild(new ColorRect { Color = new Color(0.95f, 0.78f, 0.2f), Position = new Vector2(3, h - 9), Size = new Vector2((w - 6) * frac, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // fuel fill (yellow)
            }

            if (asset?.IsFluidContainer == true && jar.item != null)   // a fluid container (bottle/canteen) shows a fill bar tinted by its fluid, even at 0 (strawberry)
            {
                FluidItem.Read(jar.item, asset, out var ftype, out var famt, out var fq);
                float frac = asset.fluidCapacity > 0f ? Mathf.Clamp(famt / asset.fluidCapacity, 0f, 1f) : 0f;
                var fcol = ftype == FluidType.None ? new Color(0.49f, 0.49f, 0.49f) : FluidDef.WaterColor(ftype, fq);   // neutral empty-state grey. The FILLED colours stay -- those are the fluid, not chrome.   // water folds its quality into the colour
                tile.AddChild(new ColorRect { Color = UITheme.Border, Position = new Vector2(2, h - 10), Size = new Vector2(w - 4, 8), MouseFilter = Control.MouseFilterEnum.Ignore });   // black outline
                tile.AddChild(new ColorRect { Color = new Color(0.30f, 0.31f, 0.34f, 1f), Position = new Vector2(3, h - 9), Size = new Vector2(w - 6, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // empty track (grey) -> reads even at 0
                if (frac > 0f) tile.AddChild(new ColorRect { Color = fcol, Position = new Vector2(3, h - 9), Size = new Vector2((w - 6) * frac, 6), MouseFilter = Control.MouseFilterEnum.Ignore });   // fluid fill, tinted by type
                // "autodrink ON" badge (strawberry): a cyan droplet-dot in the top-left — shown ONLY on the one ACTIVE
                // autodrink bottle (first enabled+safe+non-empty), so exactly one bottle is marked at a time.
                if (ReferenceEquals(jar.item, FluidItem.ActiveAutoDrink(Inv)))
                {
                    var badge = new Panel { Position = new Vector2(2, 2), Size = new Vector2(21, 21), MouseFilter = Control.MouseFilterEnum.Ignore };
                    var bs = new StyleBoxFlat { BgColor = new Color(0.25f, 0.72f, 0.95f) };
                    bs.BorderColor = UITheme.Border; bs.SetBorderWidthAll(2); bs.SetCornerRadiusAll(7);   // ~circular droplet-dot
                    badge.AddThemeStyleboxOverride("panel", bs);
                    badge.AddChild(new ColorRect { Color = new Color(0.92f, 0.98f, 1f, 0.95f), Position = new Vector2(4, 3), Size = new Vector2(4, 4), MouseFilter = Control.MouseFilterEnum.Ignore });   // a little highlight so it reads as a drop
                    tile.AddChild(badge);
                }
            }

            if (asset?.type == EItemType.FOOD && jar.item != null)   // FOOD shows its CONDITION as a coloured % in the bottom-right corner (source SleekItem quality box: red->yellow->green)
            {
                int q = jar.item.quality;
                var qcol = ItemTool.QualityColor(q / 100f);
                var bs = new StyleBoxFlat { BgColor = UITheme.Chip };
                bs.SetCornerRadiusAll(3); bs.BorderColor = qcol; bs.SetBorderWidthAll(1);   // dark chip, outlined in the condition colour so it reads on any icon
                bs.ContentMarginLeft = 4; bs.ContentMarginRight = 4; bs.ContentMarginTop = 0; bs.ContentMarginBottom = 0;   // even breathing room L/R so the text sits centred in the card
                // A PanelContainer SIZES ITSELF to the label, so the chip always wraps "{q}%" exactly (5% / 85% / 100%) and
                // the text is centred inside its card by construction. The old fixed-width Panel let a wide "100%" spill past
                // the card's edges no matter the width I picked (master: "the %s werent centered in their mini card").
                var lbl = new Label { Text = $"{q}%", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeColorOverride("font_color", qcol.Lerp(Colors.White, 0.45f));   // brighten the text so even the dark-red (spoiled) end reads on the chip; the border keeps the pure hue
                lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
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
                snow.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                badge.AddChild(snow);
                tile.AddChild(badge);
            }
            return tile;
        }


        // A page header the way retail draws it: a full-width 60px BAR (source headers[] are ISleekButton,
        // SizeOffset_Y = 60, SizeScale_X = 1) with the worn item's icon on the left, its name centred, and the
        // item's condition on the right. The old plain text label was the single biggest visual gap vs the
        // reference -- retail's inventory reads as a stack of BARS, not a list of captions.
        Control HeaderBar(string text, Vector2 pos, float width, Item worn = null, int count = -1, byte page = byte.MaxValue)
        {
            width = Mathf.Min(width, 8 * CELL);   // master 2026-08-26: clothing tabs never extend past 8 tiles' width (the widest grid, Alicepack, is 8)
            var bar = new Panel { Position = pos, Size = new Vector2(width, HDRH), MouseFilter = Control.MouseFilterEnum.Ignore };
            StyleBox(bar, UI_BAR);
            if (page != byte.MaxValue && page > PlayerInventory.SLOTS)   // a clothing page: clicking the tab folds/unfolds its grid (master 2026-09-03)
            {
                var fold = new Button { Flat = true, Position = new Vector2(HDRH, 0), Size = new Vector2(Mathf.Max(0f, width - HDRH), HDRH), MouseFilter = Control.MouseFilterEnum.Stop };
                fold.Pressed += () => { if (!_collapsed.Remove(page)) _collapsed.Add(page); Refresh(); };
                bar.AddChild(fold);
            }

            if (worn != null)
            {
                var icon = MakeTile(new ItemJar(worn), HDRH - 12, HDRH - 12);
                icon.Position = new Vector2(6, 6);
                icon.MouseFilter = Control.MouseFilterEnum.Ignore;
                bar.AddChild(icon);
                var wt = worn.GetAsset()?.type ?? EItemType.HAT;
                _headerIcons.Add((icon, wt));   // drag this icon onto a grid to take the garment off (StartDrag -> the cloth-drag path -> TakeOff)
            }

            var name = new Label { Text = text, Position = new Vector2(0, 0), Size = new Vector2(width, HDRH),
                                   HorizontalAlignment = HorizontalAlignment.Center,
                                   VerticalAlignment = VerticalAlignment.Center,
                                   MouseFilter = Control.MouseFilterEnum.Ignore };
            name.AddThemeColorOverride("font_color", UITheme.Text);
            name.AddThemeFontSizeOverride("font_size", 34);
            bar.AddChild(name);

            if (worn != null)
            {
                var pct = new Label { Text = $"{worn.quality}%", Position = new Vector2(width - 116, 0),
                                      Size = new Vector2(94, HDRH), HorizontalAlignment = HorizontalAlignment.Right,
                                      VerticalAlignment = VerticalAlignment.Center,
                                      MouseFilter = Control.MouseFilterEnum.Ignore };
                pct.AddThemeColorOverride("font_color", ItemTool.QualityColor(worn.quality / 100f).Lerp(Colors.White, 0.3f));
                pct.AddThemeFontSizeOverride("font_size", 30);
                bar.AddChild(pct);
            }
            else if (count > 0)   // storage section WITH items: show the count, right-aligned like the worn %. Hidden at 0 so an empty bag doesn't show a confusing "0" (master 2026-08-26)
            {
                var cnt = new Label { Text = count.ToString(), Position = new Vector2(width - 116, 0),
                                      Size = new Vector2(94, HDRH), HorizontalAlignment = HorizontalAlignment.Right,
                                      VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
                cnt.AddThemeColorOverride("font_color", UITheme.TextDim);
                cnt.AddThemeFontSizeOverride("font_size", 26);
                bar.AddChild(cnt);
            }
            return bar;
        }
        static Label Header(string text, Vector2 pos, float width)
        {
            var l = new Label { Text = text, Position = pos, Size = new Vector2(width, HEADER - 8) };
            l.AddThemeColorOverride("font_color", UITheme.Text);
            l.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
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
        // gridlines. STANDARDISED on the theme (strawberry: "standardize them all on the same grid"): these were a
        // raw blue-slate near-miss of UITheme.SlotEmpty; now they ARE it, so every grid -- bags, crates, quick-craft
        // -- draws the identical empty cell and can't drift apart again.
        public static Color CellFill => UITheme.SlotEmpty;
        public static Color CellEdge => UITheme.SlotEmptyEdge;

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
