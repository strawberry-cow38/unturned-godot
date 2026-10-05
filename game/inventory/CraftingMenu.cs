using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // THE CRAFTING MENU — categorised icon-grid layout (strawberry 2026-08-22, from a ref screenshot).
    //   LEFT   : category list stacked vertically, each with a recipe count; click to filter.
    //   MIDDLE : a scrollable GRID of item icons (hover a tile -> the item name tooltip) + a search box.
    //   BOTTOM : the crafting queue -- a STUB for now, its space reserved under the grid/categories.
    //   RIGHT  : the selected recipe -- icon, name, station/skill gate, description, an INGREDIENTS table
    //            (AMOUNT / ITEM TYPE / TOTAL / HAVE) and an amount stepper + CRAFT button.
    //
    // Recipes come from BlueprintRegistry.Index() (the 195 that resolve every ingredient in this port -- see the
    // registry for the 1875->195 filter). Icons are InventoryUI.IconFor(id) (the SAME textures the bag/hotbar draw;
    // no icon -> a name-label tile). Categories are the output item's EItemType, grouped; recolours get their own
    // "Dyes" bucket so 126 daypack repaints don't bury the 69 real crafts.
    public partial class CraftingMenu : CanvasLayer
    {
        public PlayerInventory Inv;
        public PlayerController Player;

        const int DETW = 600, GRIDCOLS = 5;   // the category column's width is the VITALS' width now (Layout), the grid's is what is left

        // SIZES (strawberry 2026-10-05: "make the text and iconography (within elements bounds) of the craft menu
        // bigger to fill space properly"). This screen is laid out on the 2560x1440 canvas the project stretches
        // from, and it was drawing on it in UITheme's body sizes -- 13 px, which a 1600x900 window shows at 8. Named
        // here, not in UITheme, because they are this screen's answer to its own empty space; the inventory has its own.
        const int FontCat = 26, FontText = 23, FontSmallText = 18, FontInfo = 20, FontName = 34, FontQty = 30, FontButton = 26;
        const int CATROWH = 56, CATCOUNTW = 72, SEARCHH = 52, TILE = 116, DETICON = 128, BTNH = 60;

        Control _root;
        Panel _panel;
        Label _header;
        VBoxContainer _catList;
        LineEdit _search;
        GridContainer _grid;
        Panel _detail;
        VBoxContainer _detailBox;
        Panel _queuePanel;                        // fullscreen layout refs, repositioned in Layout()
        ScrollContainer _catScroll, _gridScroll;
        Label _qLabel;
        Vector2 _lastVp;                          // relayout only when the viewport size changes
        BlueprintDef _sel;
        string _cat = "All";

        // ITEM LOOKUP (strawberry 2026-10-04): the bag's U / R over an item lands here showing only the recipes that
        // USE that item, or only the ones that MAKE it. It behaves as a temporary category: it sits selected at the
        // top of the category list, and picking a real category, typing a search or reopening the menu (Y, the
        // navbar tab) drops it -- so a lookup never outlives the question that opened it.
        public enum ItemLookup { None, Uses, Recipes }
        ItemLookup _look;
        ushort _lookId;
        public ItemLookup Lookup => _look;
        public ushort LookupItemId => _lookId;
        int _qty = 1;
        System.Collections.Generic.HashSet<string> _stationTags = new();   // crafting-station tags the player currently has (recomputed each Rebuild)
        bool _open;
        public bool IsOpen => _open;
        Control _slide; MenuSwoop _swoop;   // swoop in/out (strawberry 2026-09-08)

        // crafting queue: index 0 = LEFTMOST (newest); last = RIGHTMOST (active, counting its timer down).
        // ingredients are consumed into "limbo" (PerUnit) when a job is queued and returned if it's cancelled;
        // each craft-time tick produces one output and drops the qty, so a xN job pops one item per second.
        readonly List<QueueJob> _queue = new();
        Control _queueRow;
        Label _qEmpty;
        ColorRect _activeBar;
        float _qScroll, _qDragStartX, _qScroll0;   // drag-to-scroll state
        bool _qDragging;
        QueueJob _qPressJob;
        const float BASE_CRAFT_SECONDS = 1f;   // master 2026-08-22: every recipe 1 s for now (per-recipe knob later)
        const int TILEQ = 84, QROWX = 200;   // queue tile, and where the tile row starts (clear of the "CRAFTING QUEUE" label)
        sealed class QueueJob { public BlueprintDef Bp; public ItemAsset Out; public int Qty; public float TimeLeft; public List<(ushort id, int amt)> PerUnit; }

        /// <summary>How long one unit of this recipe takes. Reads the recipe's own time when it declares one
        /// (blueprints.tsv column 9) and falls back to the base otherwise -- so the 1875 archived retail rows,
        /// which have 8 columns, keep behaving exactly as they did (master 2026-09-06: "add crafting times to
        /// each log -> plank plank -> stick recipe").</summary>
        static float CraftTimeFor(BlueprintDef bp) => bp != null && bp.Seconds > 0f ? bp.Seconds : BASE_CRAFT_SECONDS;

        // resolved once per Open(): every indexed recipe, its output asset, and its category bucket
        readonly List<BlueprintDef> _all = new();
        readonly Dictionary<BlueprintDef, ItemAsset> _out = new();
        readonly Dictionary<BlueprintDef, string> _catOf = new();

        // Every one of these used to be a near-miss of the inventory's value -- Bg was 0.08/0.10/0.13 against
        // its 0.10/0.12/0.15, Dim 0.60 against its 0.55. Nobody chose that; it is what two screens written
        // months apart look like. They now forward to UITheme so there is one place to change them.
        static Color Bg => UITheme.BgSolid;
        static Color Bar => UITheme.BarSolid;
        static Color SelC => new(0.34f, 0.36f, 0.38f, 0.98f);
        static Color TileC => new(0.22f, 0.22f, 0.23f, 0.98f);
        static Color Dim => UITheme.TextDim;
        static Color Good => UITheme.Good;
        static Color Bad => UITheme.Bad;

        static void Box(Control p, Color c, int r = UITheme.RadiusCell)
            => p.AddThemeStyleboxOverride("panel", UITheme.Box(c, r));

        // frosted-glass backdrop -- the SAME blur the inventory uses (master 2026-08-26: "same ui design down to the background").
        const string BACKDROP_BLUR = @"
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

        static readonly string[] CatOrder = { "All", "Weapons", "Attachments", "Ammo", "Clothing", "Medical", "Food", "Building", "Resources", "Tools", "Other", "Dyes" };

        // group the output item's EItemType (by name, so a type this build doesn't know just falls to Other).
        static string CategoryOf(ItemAsset a)
        {
            switch ((a?.type.ToString() ?? "").ToUpperInvariant())
            {
                case "GUN": case "MELEE": case "THROWABLE": return "Weapons";
                case "SIGHT": case "TACTICAL": case "GRIP": case "BARREL": case "OPTIC": return "Attachments";
                case "MAGAZINE": case "CHARGE": case "DETONATOR": return "Ammo";
                case "SHIRT": case "PANTS": case "HAT": case "VEST": case "MASK": case "GLASSES": case "BACKPACK": return "Clothing";
                case "MEDICAL": return "Medical";
                case "FOOD": case "WATER": return "Food";
                case "BARRICADE": case "STRUCTURE": case "STORAGE": case "BOX": case "TRAP": case "SENTRY": case "BEACON": case "FARM": case "GROWER": case "TANK": return "Building";
                case "SUPPLY": case "FUEL": case "REFILL": case "TIRE": return "Resources";
                case "TOOL": case "FISHER": case "MAP": case "COMPASS": case "GENERATOR": case "OIL_PUMP": case "FILTER": case "KEY": return "Tools";
                default: return "Other";
            }
        }

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;

            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);

            // frosted-glass backdrop -- the SAME blur the inventory uses (master 2026-08-26: "same ui design ... background")
            var dim = new ColorRect();
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = BACKDROP_BLUR } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);

            // FULLSCREEN, translucent panel matching the inventory (was a centred 1100x680 SOLID box); sized in Layout()
            // The swoop's slider: a full-rect Control that owns NOTHING but an offset, so the animation cannot
            // fight this screen's own Layout() (which sets the panel's position from the viewport size).
            _slide = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _slide.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_slide);
            _panel = new Panel();
            Box(_panel, UITheme.Bg, 6);
            _slide.AddChild(_panel);
            _swoop = MenuSwoop.Attach(this, _root, _slide, dim);

            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Craft, t => Player?.ShowMenu(t), () => { Close(); Input.MouseMode = Input.MouseModeEnum.Captured; });   // the SHARED strip -- on the full-screen ROOT, not the inset panel, so it sits exactly where the inventory's does (the 16 px panel inset was the "bar moves slightly")
            // "N shown / M craftable" info line, small, below the navbar (text set in Rebuild)
            _header = new Label();
            _header.AddThemeFontSizeOverride("font_size", FontInfo);
            _header.AddThemeColorOverride("font_color", UITheme.TextDim);
            _panel.AddChild(_header);

            // LEFT: category list
            _catScroll = new ScrollContainer();
            _catScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _panel.AddChild(_catScroll);
            // its width follows the scroll and the rows fill it, so it tracks the vitals with no constant. EXPAND is what
            // makes a ScrollContainer hand its width down: without it the list sat at its minimum, which with the old
            // CATW gone is 0, and every row was a zero-wide panel with its text hanging out of it.
            _catList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _catList.AddThemeConstantOverride("separation", 2);
            _catScroll.AddChild(_catList);

            // MIDDLE: icon grid + search
            _gridScroll = new ScrollContainer();
            _gridScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _panel.AddChild(_gridScroll);
            _grid = new GridContainer { Columns = GRIDCOLS };
            _grid.AddThemeConstantOverride("h_separation", 8);
            _grid.AddThemeConstantOverride("v_separation", 8);
            _gridScroll.AddChild(_grid);
            _search = new LineEdit { PlaceholderText = "search recipes..." };
            UITheme.Field(_search);
            _search.AddThemeFontSizeOverride("font_size", FontText);
            _search.TextChanged += _ => { _sel = null; _look = ItemLookup.None; Rebuild(); };   // typing is a new question: the lookup goes
            _panel.AddChild(_search);

            // BOTTOM: crafting queue -- jobs fill RIGHTWARD (rightmost = active/counting; new jobs prepend on the left)
            _queuePanel = new Panel();
            Box(_queuePanel, UITheme.BarSolid);
            _panel.AddChild(_queuePanel);
            _qLabel = new Label { Text = "CRAFTING QUEUE", Position = new Vector2(16, 10), Size = new Vector2(QROWX - 24, 30) };
            _qLabel.AddThemeFontSizeOverride("font_size", FontInfo);
            _qLabel.AddThemeColorOverride("font_color", UITheme.TextDim);
            _queuePanel.AddChild(_qLabel);
            _qEmpty = new Label { Text = "(empty)", Position = new Vector2(16, 48), Size = new Vector2(QROWX - 24, 26) };
            _qEmpty.AddThemeFontSizeOverride("font_size", FontSmallText);
            _qEmpty.AddThemeColorOverride("font_color", UITheme.TextDisabled);
            _queuePanel.AddChild(_qEmpty);
            _queueRow = new Control { Position = new Vector2(QROWX, 4) };
            _queueRow.ClipContents = true;
            _queueRow.MouseFilter = Control.MouseFilterEnum.Stop;   // handles drag-scroll / click-remove / rmb-promote (tiles are mouse-transparent)
            _queueRow.GuiInput += OnQueueGuiInput;
            _queuePanel.AddChild(_queueRow);

            // RIGHT: detail
            _detail = new Panel();
            Box(_detail, UITheme.BgSolid);
            _panel.AddChild(_detail);
            _detailBox = new VBoxContainer { Position = new Vector2(16, 14) };
            _detailBox.AddThemeConstantOverride("separation", 6);
            _detail.AddChild(_detailBox);

            Layout();
        }

        public override void _Process(double delta)
        {
            if (_open && _root != null && (_root.Size - _lastVp).LengthSquared() > 1f) { _lastVp = _root.Size; Layout(); }
            TickQueue((float)delta);
        }

        // Fullscreen responsive layout (master 2026-08-26): the panel fills the screen, sections reflow to it.
        void Layout()
        {
            if (_panel == null || _root == null) return;
            Vector2 vp = _root.Size;
            if (vp.X < 1f) vp = GetViewport().GetVisibleRect().Size;
            const float M = 16f;
            float pw = vp.X - 2f * M, ph = vp.Y - 2f * M;
            _panel.Position = new Vector2(M, M);
            _panel.Size = new Vector2(pw, ph);

            const float barH = MenuNavbar.Height, top = barH + 48f, bottomPad = 150f;   // content clears the shared navbar + the info line

            // THE LEFT COLUMN IS THE VITALS' COLUMN (strawberry 2026-10-05: "match the vertical list of categories'
            // width to the width of the vitals (same w search bar, move that just above the craft queue"). The
            // category list used to be a fixed 300 beside bars 589 wide, so the left edge of the screen had two
            // different right edges stacked on it. Now both come off HUD.VitalsRect: the list starts where the bars
            // start, is exactly as wide, and stops one gutter above them -- the vitals ARE the bottom of the column.
            // That also lines the grid up with the crafting queue, which already started one gutter clear of the
            // bars (strawberry 2026-09-08: "built around the vitals panel being visable and not covered"), so the
            // middle is one column too. HUD coordinates are screen, these are panel-relative (inset by M).
            var vit = HUD.VitalsRect(vp);
            float catX = vit.Position.X - M, catW = vit.Size.X;
            float gridX = catX + catW + 16f;
            float detW = DETW;
            float detX = pw - detW - M;
            float gridW = Mathf.Max(TILE + 16f, detX - gridX - 16f);
            _header.Position = new Vector2(catX, barH + 10f); _header.Size = new Vector2(pw - catX - 16f, 30f);

            float catBottom = HUD.ContentBottom(vp, M + catX, M + catX + catW, M + ph - bottomPad) - M;
            _catScroll.Position = new Vector2(catX, top);
            _catScroll.Size = new Vector2(catW, Mathf.Max(120f, catBottom - top));

            // the queue: the bottom strip of the middle column, under the grid
            float queueY = ph - bottomPad + 8f;
            _queuePanel.Position = new Vector2(gridX, queueY);
            _queuePanel.Size = new Vector2(gridW, bottomPad - 24f);
            _queueRow.Size = new Vector2(Mathf.Max(80f, gridW - QROWX - 8f), bottomPad - 32f);

            // the search: vitals-wide, sitting on the queue's left end; the grid runs down to it
            float searchY = queueY - 10f - SEARCHH;
            _search.Position = new Vector2(gridX, searchY);
            _search.Size = new Vector2(Mathf.Min(catW, gridW), SEARCHH);

            _gridScroll.Position = new Vector2(gridX, top);
            _gridScroll.Size = new Vector2(gridW, Mathf.Max(120f, searchY - 12f - top));
            _grid.CustomMinimumSize = new Vector2(gridW - 16f, 0);
            int cols = Mathf.Max(GRIDCOLS, (int)((gridW - 16f + 8f) / (TILE + 8f)));
            if (_grid.Columns != cols) _grid.Columns = cols;

            _detail.Position = new Vector2(detX, top);
            _detail.Size = new Vector2(detW, ph - top - 16f);
            _detailBox.CustomMinimumSize = new Vector2(detW - 32f, ph - top - 44f);
        }

        /// <summary>Test seam: the grid tile showing this recipe, in canvas coordinates (where a click would land), or
        /// null when it is not in the grid. Read off the live tile, so it is wherever the swoop has it right now.</summary>
        public Rect2? DebugTileRect(BlueprintDef bp)
        {
            var view = View();
            int i = view.IndexOf(bp);
            if (i < 0 || i >= _grid.GetChildCount()) return null;
            return _grid.GetChild<Control>(i).GetGlobalRect();
        }

        /// <summary>Test seam: where each block of this screen is, in SCREEN coordinates, with the swoop's slide
        /// taken out -- so a test can hold the layout against HUD.VitalsRect without waiting for an animation.</summary>
        public Dictionary<string, Rect2> DebugRects()
        {
            Rect2 R(Control c) { var r = c.GetGlobalRect(); return new Rect2(r.Position - (_slide?.Position ?? Vector2.Zero), r.Size); }
            var d = new Dictionary<string, Rect2>
            {
                ["categories"] = R(_catScroll), ["grid"] = R(_gridScroll), ["search"] = R(_search),
                ["queue"] = R(_queuePanel), ["detail"] = R(_detail),
            };
            return d;
        }

        /// <summary>Test seam: every piece of text this screen is showing that does NOT fit inside its ELEMENT -- the
        /// nearest Panel or scroll area around it ("within elements bounds"), measured with the font that draws it.
        /// Against the element, not the control: a Label or unclipped Button GROWS to its text, so its own size always
        /// "fits" -- the overflow shows up as the control poking out of the box it sits in. Clipped labels are exempt:
        /// they are told to cut, and a long item name cut with an ellipsis is the intended behaviour.</summary>
        public List<string> DebugOverflows(out int measured)
        {
            var bad = new List<string>(); int seen = 0;
            void Walk(Node n, Control element)
            {
                if (n is Control c && c.IsVisibleInTree())
                {
                    string text = c is Label l && !l.ClipText ? l.Text : c is Button b && !b.ClipText ? b.Text : null;
                    if (!string.IsNullOrEmpty(text) && element != null)
                    {
                        seen++;
                        var font = c.GetThemeFont("font"); int fs = c.GetThemeFontSize("font_size");
                        bool wraps = c is Label wl && wl.AutowrapMode != TextServer.AutowrapMode.Off;
                        var sz = wraps ? Vector2.Zero : font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
                        var r = c.GetGlobalRect(); var box = element.GetGlobalRect();
                        float right = r.Position.X + Mathf.Max(r.Size.X, sz.X), bottom = r.Position.Y + Mathf.Max(r.Size.Y, sz.Y);
                        if (r.Position.X < box.Position.X - 1f || right > box.End.X + 1f || r.Position.Y < box.Position.Y - 1f || bottom > box.End.Y + 1f)
                            bad.Add($"{c.GetType().Name} \"{text.Trim()}\" {r.Position.X:0}..{right:0} x {r.Position.Y:0}..{bottom:0} outside its {element.GetType().Name} {box.Position.X:0}..{box.End.X:0} x {box.Position.Y:0}..{box.End.Y:0}");
                    }
                    if (c is Panel || c is ScrollContainer) element = c;
                }
                foreach (Node k in n.GetChildren()) Walk(k, element);
            }
            Walk(_panel, null);
            measured = seen;
            return bad;
        }

        /// <summary>MP: adopt the SERVER's craft queue (v31 EventCraftQueue). The local queue is a display
        /// mirror here and nothing else -- it must not tick, produce, or spend, because the server already did
        /// all three. Without this the MP client showed an empty queue through an entire timed craft, so an 8 s
        /// recipe read as a command that vanished.</summary>
        public void AdoptServerQueue((ushort bp, float left, float of)[] jobs)
        {
            _serverDriven = true;
            _queue.Clear();
            if (jobs != null)
            {
                // Reversed: the server sends oldest-first (jobs[0] is the one running), and this list draws the
                // ACTIVE job rightmost. Getting this backwards would count down the wrong tile.
                for (int i = jobs.Length - 1; i >= 0; i--)
                {
                    var (bpIdx, left, of) = jobs[i];
                    if (bpIdx >= BlueprintRegistry.All.Count) continue;
                    var bp = BlueprintRegistry.All[bpIdx];
                    _queue.Add(new QueueJob { Bp = bp, Out = OutAsset(bp), Qty = 1, TimeLeft = left, PerUnit = null });
                }
            }
            if (_open) { RebuildQueue(); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); }
        }
        bool _serverDriven;

        // the queue runs even while the menu is closed (a job you started keeps cooking in the background).
        void TickQueue(float dt)
        {
            if (_queue.Count == 0 || Inv == null) return;
            if (_serverDriven)
            {
                // Count the displayed job down locally between server updates so the bar moves smoothly, but
                // never PRODUCE -- the server owns the payout and will send a fresh queue when it happens.
                // Producing here would hand out a second copy of everything the server already granted.
                var live = _queue[_queue.Count - 1];
                live.TimeLeft = Mathf.Max(0f, live.TimeLeft - dt);
                if (_open) RebuildQueue();
                return;
            }
            var job = _queue[_queue.Count - 1];   // RIGHTMOST = active
            job.TimeLeft -= dt;
            if (job.TimeLeft <= 0f)
            {
                Produce(job);
                job.Qty--;
                if (job.Qty > 0) job.TimeLeft += CraftTimeFor(job.Bp);   // next unit of a xN job
                else _queue.RemoveAt(_queue.Count - 1);
                if (_open) { RebuildQueue(); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); }   // HAVE counts + queue changed
            }
            else if (_open && _activeBar != null)
            {
                float p = 1f - job.TimeLeft / Mathf.Max(0.01f, CraftTimeFor(job.Bp));
                _activeBar.Size = new Vector2((TILEQ - 6) * Mathf.Clamp(p, 0f, 1f), 4);
            }
        }

        // test hooks (craft.queue) -- drive the queue headless without a scene tree.
        public void DebugEnqueue(BlueprintDef bp, int n) => Enqueue(bp, n);
        public void DebugTick(float dt) => TickQueue(dt);
        public int DebugQueueCount => _queue.Count;
        public void DebugCancelActive() { if (_queue.Count > 0) Cancel(_queue[_queue.Count - 1]); }
        public void DebugMoveToStart(int index) { if (index >= 0 && index < _queue.Count) MoveToStart(_queue[index]); }
        public BlueprintDef DebugActiveBp => _queue.Count > 0 ? _queue[_queue.Count - 1].Bp : null;

        public void Toggle() { if (_open) Close(); else Open(); }
        MenuNavbar _navbar;   // tabs route through PlayerController.ShowMenu (MenuNavbar)

        // _open goes false NOW so input routing stops; the swoop hides the pixels when it lands.
        public void Close() { _open = false; if (_swoop == null || !_swoop.Out()) Visible = false; }

        public void Open()
        {
            _look = ItemLookup.None;   // Y / the navbar tab open the ordinary view; SetLookup runs AFTER this when it is a lookup
            _open = true; Visible = true;
            if (_root != null) _root.Visible = true;
            _qScroll = 0f;   // start showing the active (rightmost) side
            ComputeData();
            if (System.Array.IndexOf(CatOrder, _cat) < 0 || CountFor(_cat) == 0) _cat = "All";
            Rebuild();
            _swoop?.In();
        }

        /// <summary>Show only the recipes that use (Uses) or make (Recipes) item `id`. Called on an OPEN menu, after
        /// Open() -- PlayerController.ShowCraftingLookup. An item nothing uses still lands here, on an empty grid that
        /// says so: the key always visibly does something, and "nothing uses this" is an answer worth having.</summary>
        public void SetLookup(ItemLookup mode, ushort id)
        {
            _look = mode; _lookId = id;
            _sel = null; _qty = 1;
            if (_search != null) _search.Text = "";   // a leftover query must not read as part of the lookup (setting Text does not fire TextChanged)
            if (_open) Rebuild();
        }

        /// <summary>Does this recipe take item `id` -- as an ingredient OR as a tool (a tool is an input that is not
        /// consumed, and "what can I do with this blowtorch" is exactly the question U asks).</summary>
        public static bool UsesItem(BlueprintDef bp, ushort id)
        {
            foreach (var ing in bp.Inputs)
                if (Assets.findByGuid(ing.Guid)?.id == id) return true;
            return false;
        }

        /// <summary>Does this recipe produce item `id`. Every output counts, not just the first; a recipe with no
        /// output list makes its owner item (the Title/OutAsset rule).</summary>
        public static bool MakesItem(BlueprintDef bp, ushort id)
        {
            if (bp.Outputs.Count > 0)
            {
                foreach (var o in bp.Outputs)
                    if (Assets.findByGuid(o.Guid)?.id == id) return true;
                return false;
            }
            return ushort.TryParse(bp.OwnerItemId, out var oid) && oid == id;
        }

        // ---- BLUEPRINT KNOWLEDGE (v55, strawberry 2026-10-04: "these recipes dont show unless a result of a search the
        // user did in the craft menu, these appear grayed out with padlock over them") ----
        /// <summary>Does the player know this recipe. No player (a bare test menu) = knows everything, which is what the
        /// menu meant before recipes could be locked.</summary>
        bool Known(BlueprintDef bp) => Player == null || Player.KnowsBlueprint(bp);
        public void RefreshIfOpen() { if (_open) Rebuild(); }

        /// <summary>How an unknown recipe could be learned, as a line for the detail panel -- so a padlock is a
        /// direction, not a wall. Names the item that teaches it, the skill level that unlocks it, or says plainly that
        /// nothing in the world does.</summary>
        static string HowToLearn(BlueprintDef bp)
        {
            var ways = new List<string>();
            foreach (var id in bp.TaughtByItems()) ways.Add($"read {Assets.find(id)?.itemName ?? $"item {id}"}");
            foreach (var (skill, level) in bp.SkillUnlocks()) ways.Add($"reach {skill} {level}");
            return ways.Count > 0 ? "learn it: " + string.Join(" or ", ways) : "not learnable in the world";
        }

        /// <summary>WHY this recipe cannot be crafted right now, as one line for the player, or null when it can
        /// (strawberry 2026-10-04: "do we give a reason for why we cant craft it? a line that says insufficient
        /// resources or not unlocked or needs workbench or whatever the error is"). The FIRST blocker, in the order a
        /// player has to clear them -- learn it, level up, stand at the station, gather the stuff -- so the line
        /// is always the next thing to do rather than a list of everything wrong.</summary>
        public string CraftBlocker(BlueprintDef bp, Crafting.IInv inv, int qty = 1)
        {
            if (bp == null) return "Select a recipe";
            if (!Known(bp)) return $"Not unlocked  ·  {HowToLearn(bp)}";
            if (!Crafting.MeetsSkill(bp, Player?.Skills)) return $"Needs {bp.Skill} skill {bp.SkillLevel}";
            if (!Crafting.HasStations(bp, _stationTags)) return $"Needs {StationNames(bp, _stationTags)} nearby";
            foreach (var ing in bp.Inputs)
            {
                var ia = Assets.findByGuid(ing.Guid);
                if (ia == null) return "Needs an item this build does not ship";
                int need = ing.Consume ? ing.Amount * Mathf.Max(1, qty) : ing.Amount;   // a tool is needed once, however many you make
                int have = inv.Count(ia.id);
                if (have < need) return ing.Consume ? $"Not enough {ia.itemName}  ({have}/{need})" : $"Needs a {ia.itemName} (tool)";
            }
            return null;
        }

        /// <summary>The stations that would satisfy the tags this recipe still lacks, by name -- "Workbench", or for the
        /// Heat tag every placeable that gives it ("Campfire or Kiln or Brick Oven").</summary>
        static string StationNames(BlueprintDef bp, ICollection<string> have)
        {
            var names = new List<string>();
            foreach (var tag in bp.StationTags)
            {
                if (have != null && have.Contains(tag)) continue;
                foreach (var d in DeployableDef.All)
                    if (d.CraftingTags != null && System.Array.IndexOf(d.CraftingTags, tag) >= 0 && !names.Contains(d.Name)) names.Add(d.Name);
            }
            if (names.Count == 0) return "a crafting station";
            if (names.Count > 3) names = names.GetRange(0, 3);
            return string.Join(" or ", names);
        }

        static bool LookupMatches(BlueprintDef bp, ItemLookup mode, ushort id)
            => mode == ItemLookup.Uses ? UsesItem(bp, id) : mode == ItemLookup.Recipes && MakesItem(bp, id);

        string LookupLabel()
        {
            string name = Assets.find(_lookId)?.itemName ?? $"item {_lookId}";
            return _look == ItemLookup.Uses ? $"Uses of {name}" : $"Recipes for {name}";
        }

        void ComputeData()
        {
            _all.Clear(); _out.Clear(); _catOf.Clear();
            if (Inv == null) return;
            foreach (var bp in BlueprintRegistry.Index())
            {
                _all.Add(bp);
                var a = OutAsset(bp);
                _out[bp] = a;
                _catOf[bp] = BlueprintRegistry.IsRecolour(bp) ? "Dyes" : CategoryOf(a);
            }
        }

        public static ItemAsset OutAsset(BlueprintDef bp)
        {
            if (bp.Outputs.Count > 0) { var o = Assets.findByGuid(bp.Outputs[0].Guid); if (o != null) return o; }
            if (ushort.TryParse(bp.OwnerItemId, out var oid)) return Assets.find(oid);
            return null;
        }

        int CountFor(string cat)
        {
            int n = 0;
            foreach (var bp in _all)
            {
                if (!Known(bp)) continue;   // the category counts are what browsing would show, and browsing hides the unknown
                if (cat == "All") { if (_catOf[bp] != "Dyes") n++; }
                else if (_catOf[bp] == cat) n++;
            }
            return n;
        }

        // recipes for the current view: a search query overrides the category (matches across everything); otherwise
        // the selected category ("All" = everything except Dyes).
        List<BlueprintDef> View()
        {
            string q = (_search?.Text ?? "").Trim();
            var res = new List<BlueprintDef>();
            foreach (var bp in _all)
            {
                // An UNKNOWN recipe surfaces only for a search the player TYPED. Browsing a category, or the bag's U/R
                // lookup, never reveals one -- the lookup answers "what can I do with this", and a recipe you do not
                // know is not something you can do.
                if (q.Length == 0 || _look != ItemLookup.None) { if (!Known(bp)) continue; }
                if (_look != ItemLookup.None) { if (!LookupMatches(bp, _look, _lookId)) continue; }
                else if (q.Length > 0) { if (!Matches(bp, q)) continue; }
                else if (_cat == "All") { if (_catOf[bp] == "Dyes") continue; }
                else if (_catOf[bp] != _cat) continue;
                res.Add(bp);
            }
            res.Sort((a, b) => string.Compare(Title(a), Title(b), System.StringComparison.OrdinalIgnoreCase));
            return res;
        }

        void Rebuild()
        {
            if (Inv == null) return;
            var inv = new Crafting.PlayerInvAdapter(Inv);
            _stationTags = Player?.CraftingStationTags() ?? new System.Collections.Generic.HashSet<string>();   // nearby workbench/station access

            // categories (only non-empty ones)
            foreach (Node c in _catList.GetChildren()) c.QueueFree();
            if (_look != ItemLookup.None)
            {
                // the lookup, shown where a category would be and selected like one. Clicking it clears it.
                var lrow = CatRow($"  \u00d7  {LookupLabel()}", View().Count, true, () => { _look = ItemLookup.None; _cat = "All"; _sel = null; Rebuild(); }, clip: true);
                ((Button)lrow.GetChild(0)).TooltipText = "clear";
                _catList.AddChild(lrow);
            }
            foreach (var cat in CatOrder)
            {
                int n = CountFor(cat);
                if (n == 0) continue;
                string capture = cat;
                _catList.AddChild(CatRow($"  {cat}", n, _look == ItemLookup.None && cat == _cat,
                    () => { _cat = capture; _look = ItemLookup.None; _search.Text = ""; _sel = null; Rebuild(); }));
            }

            // grid
            foreach (Node c in _grid.GetChildren()) c.QueueFree();
            var view = View();
            int canNow = 0;
            foreach (var bp in _all) if (Known(bp) && Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills) && Crafting.HasStations(bp, _stationTags)) canNow++;
            _header.Text = _look != ItemLookup.None
                ? $"CRAFTING   ·   {LookupLabel()}   ·   {view.Count} recipe{(view.Count == 1 ? "" : "s")}   ·   {canNow} craftable now"
                : $"CRAFTING   ·   {view.Count} shown   ·   {canNow} craftable now";
            if (view.Count == 0)
            {
                string none = "  nothing here";
                if (_look != ItemLookup.None)
                {
                    string name = Assets.find(_lookId)?.itemName ?? "this";
                    none = _look == ItemLookup.Uses ? $"  nothing uses {name}" : $"  {name} can't be crafted";
                }
                _grid.AddChild(new Label { Text = none });
            }
            else
                foreach (var bp in view) _grid.AddChild(Tile(bp, inv));

            if (_sel == null || !_out.ContainsKey(_sel)) _sel = view.Count > 0 ? view[0] : null;
            ShowDetail(inv);
            RebuildQueue();
        }

        /// <summary>One row of the category column: the name as a button, its recipe count on the right. Anchored, not
        /// sized, so a row is as wide as the column is -- which is the vitals' width, and that moves with the window.</summary>
        Control CatRow(string text, int count, bool selected, System.Action onPress, bool clip = false)
        {
            var row = new Panel { CustomMinimumSize = new Vector2(0, CATROWH) };
            if (selected) Box(row, SelC);
            // only the lookup row clips: an item name has no bound, but the categories are a fixed list, so one of
            // them overflowing is a sizing bug, and clipping it would hide that from the overflow check
            var b = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left, ClipText = clip };
            b.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            b.OffsetRight = -CATCOUNTW;
            b.AddThemeFontSizeOverride("font_size", FontCat);
            b.Pressed += onPress;
            row.AddChild(b);
            var cnt = new Label { Text = count.ToString(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            cnt.AnchorLeft = 1f; cnt.AnchorRight = 1f; cnt.AnchorTop = 0f; cnt.AnchorBottom = 1f;
            cnt.OffsetLeft = -CATCOUNTW; cnt.OffsetRight = -14f;
            cnt.AddThemeFontSizeOverride("font_size", FontCat);
            cnt.AddThemeColorOverride("font_color", Dim);
            cnt.MouseFilter = Control.MouseFilterEnum.Ignore;
            row.AddChild(cnt);
            return row;
        }

        Control Tile(BlueprintDef bp, Crafting.IInv inv)
        {
            var a = _out.TryGetValue(bp, out var av) ? av : null;
            bool locked = !Known(bp);
            bool can = !locked && Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills) && Crafting.HasStations(bp, _stationTags);
            // GREY OUT WHAT YOU CANNOT MAKE, AND MARK WHAT YOU CAN.
            //
            // Only the icon used to dim, to 40% alpha, while the tile behind it stayed identical to a
            // craftable one. That reads fine when most things are craftable and not at all when one recipe
            // in sixty-nine is -- which is the actual state of a fresh character, and the state in which
            // someone opens this menu and asks why everything looks the same.
            //
            // So the emphasis is inverted: rather than trying to make sixty-eight tiles look "off", the one
            // you CAN craft gets a green edge and pops out of the grid. Dimming alone cannot do that job,
            // because at that ratio the dim IS the background.
            var tile = new Panel { CustomMinimumSize = new Vector2(TILE, TILE) };
            bool sel = ReferenceEquals(bp, _sel);
            tile.AddThemeStyleboxOverride("panel", UITheme.Box(
                sel ? SelC : can ? TileC : new Color(0.15f, 0.15f, 0.16f, 0.96f),
                UITheme.RadiusCell,
                can ? UITheme.Good : (Color?)null,
                can ? 2 : 0));

            var tex = a != null ? InventoryUI.IconFor(a.id) : null;
            if (tex != null)
            {
                var ico = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                ico.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                ico.OffsetLeft = 10; ico.OffsetTop = 10; ico.OffsetRight = -10; ico.OffsetBottom = -10;
                ico.MouseFilter = Control.MouseFilterEnum.Ignore;
                // Grey AND dim, not just dim. Modulate multiplies, so pulling the RGB down desaturates the
                // icon toward the panel as well as fading it -- a half-transparent full-colour icon still
                // reads as "an item", which is exactly the thing being distinguished against.
                if (!can) ico.Modulate = new Color(0.55f, 0.55f, 0.58f, 0.55f);
                tile.AddChild(ico);
            }
            else
            {
                var lbl = new Label { Text = Title(bp), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                lbl.OffsetLeft = 6; lbl.OffsetTop = 6; lbl.OffsetRight = -6; lbl.OffsetBottom = -6;
                lbl.AddThemeFontSizeOverride("font_size", FontSmallText);
                lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                if (!can) lbl.AddThemeColorOverride("font_color", UITheme.TextDisabled);
                tile.AddChild(lbl);
            }

            if (locked)
            {
                // the PADLOCK, over the greyed icon: drawn, not a glyph -- the UI font has no lock character, and a
                // tofu box over every locked recipe would read as a rendering bug rather than as "locked".
                const float lkW = TILE * 0.36f, lkH = TILE * 0.43f;   // the 30x36 it was on an 84 tile, kept in proportion
                var lk = new PadlockGlyph { Size = new Vector2(lkW, lkH), Position = new Vector2((TILE - lkW) * 0.5f, (TILE - lkH) * 0.5f) };
                lk.MouseFilter = Control.MouseFilterEnum.Ignore;
                tile.AddChild(lk);
            }
            var btn = new Button { Flat = true, TooltipText = locked ? $"{Title(bp)}  (locked)" : Title(bp) };   // hover -> the item name
            btn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            btn.Pressed += () => { _sel = bp; _qty = 1; Rebuild(); };
            // DOUBLE-CLICK CRAFTS ONE (strawberry 2026-10-05: "add double click grid tile to craft (if we can craft it)").
            // The engine's own double-click flag, so it keeps the player's OS timing rather than a number of ours. The
            // first click has already rebuilt the grid, so the second lands on a NEW button that never saw the first --
            // which is fine, because the flag rides on the event, not on the control. Accepting it stops the Button
            // seeing the press at all, so a double-click is one craft and not also a re-select.
            //
            // "if we can craft it" is QueueCraft's own gate -- the same one the bag's quick-craft goes through (known,
            // skill, station, ingredients) -- so one that cannot be made is only selected, and its reason line says why.
            btn.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }) return;
                btn.AcceptEvent();
                _sel = bp; _qty = 1;
                if (CraftBlocker(bp, new Crafting.PlayerInvAdapter(Inv), 1) == null) QueueCraft(bp, 1);   // rebuilds itself
                else Rebuild();
            };
            tile.AddChild(btn);
            return tile;
        }

        void ShowDetail(Crafting.IInv inv)
        {
            foreach (Node c in _detailBox.GetChildren()) c.QueueFree();
            if (_sel == null) { var none = new Label { Text = "select a recipe" }; none.AddThemeFontSizeOverride("font_size", FontText); _detailBox.AddChild(none); return; }
            var a = _out.TryGetValue(_sel, out var av) ? av : null;

            // header: icon + name (+ output count)
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 14);
            var tex = a != null ? InventoryUI.IconFor(a.id) : null;
            if (tex != null)
                head.AddChild(new TextureRect { Texture = tex, CustomMinimumSize = new Vector2(DETICON, DETICON), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            var nameBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var t = new Label { Text = Title(_sel), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            t.AddThemeFontSizeOverride("font_size", FontName);
            nameBox.AddChild(t);
            int outCount = _sel.Outputs.Count > 0 ? _sel.Outputs[0].Amount : 1;
            if (outCount > 1)
            {
                var oc = new Label { Text = $"makes x{outCount}" };
                oc.AddThemeFontSizeOverride("font_size", FontSmallText); oc.AddThemeColorOverride("font_color", Dim);
                nameBox.AddChild(oc);
            }
            head.AddChild(nameBox);
            _detailBox.AddChild(head);

            // gates
            bool selLocked = !Known(_sel);
            if (selLocked)
            {
                var lk = new Label { Text = "LOCKED" };   // the HOW is the reason line above CRAFT, not repeated here
                lk.AddThemeFontSizeOverride("font_size", FontText);
                lk.AddThemeColorOverride("font_color", Bad);
                _detailBox.AddChild(lk);
            }
            if (_sel.RequiresSkill)
            {
                bool meets = Crafting.MeetsSkill(_sel, Player?.Skills);
                var sk = new Label { Text = $"requires {_sel.Skill} {_sel.SkillLevel}" };
                sk.AddThemeFontSizeOverride("font_size", FontText);
                sk.AddThemeColorOverride("font_color", meets ? Dim : Bad);
                _detailBox.AddChild(sk);
            }
            if (_sel.RequiresStation)
            {
                var st = new Label { Text = "requires a crafting station" };
                st.AddThemeFontSizeOverride("font_size", FontText);
                st.AddThemeColorOverride("font_color", Dim);
                _detailBox.AddChild(st);
            }

            // description
            if (a != null && !string.IsNullOrEmpty(a.description))
            {
                var d = new Label { Text = a.description, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                d.AddThemeFontSizeOverride("font_size", FontText);
                d.AddThemeColorOverride("font_color", UITheme.TextBody);
                _detailBox.AddChild(d);
            }

            _detailBox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });   // push the table + craft to the bottom

            // clamp qty to what's craftable, so the CRAFT button stays honest
            int max = MaxCraftable(inv);
            _qty = Mathf.Clamp(_qty, 1, Mathf.Max(1, max));

            // ingredients table: AMOUNT | ITEM TYPE | TOTAL | HAVE
            var tbl = new GridContainer { Columns = 4 };   // as wide as the detail box (a VBox fills its children)
            tbl.AddThemeConstantOverride("h_separation", 14); tbl.AddThemeConstantOverride("v_separation", 4);
            AddHead(tbl, "AMOUNT"); AddHead(tbl, "ITEM TYPE"); AddHead(tbl, "TOTAL"); AddHead(tbl, "HAVE");
            foreach (var ing in _sel.Inputs)
            {
                var ia = Assets.findByGuid(ing.Guid);
                int total = ing.Amount * _qty;
                int have = ia != null ? inv.Count(ia.id) : 0;
                bool ok = have >= total;
                AddCell(tbl, ing.Amount.ToString(), ok ? Good : Bad);
                AddCell(tbl, (ia?.itemName ?? "?") + (ing.Consume ? "" : "  (tool)"), ok ? Good : Bad, fill: true);
                AddCell(tbl, ing.Consume ? total.ToString() : "-", ok ? Good : Bad);
                AddCell(tbl, have.ToString("N0"), ok ? Good : Bad);
            }
            _detailBox.AddChild(tbl);

            // WHY NOT, in words, right above the button it disables. It used to live only in that button's hover
            // tooltip -- and the tooltip said "ok" when the station was the problem, because it only ever reported
            // the item check.
            string blocker = CraftBlocker(_sel, inv, _qty);
            bool canMake = blocker == null;
            if (!canMake)
            {
                var why = new Label { Text = blocker, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                why.AddThemeFontSizeOverride("font_size", FontText);
                why.AddThemeColorOverride("font_color", Bad);
                _detailBox.AddChild(why);
            }
            // amount stepper + CRAFT
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
            var minus = new Button { Text = "−", CustomMinimumSize = new Vector2(BTNH, BTNH) };
            minus.AddThemeFontSizeOverride("font_size", FontButton);
            minus.Pressed += () => { _qty = Mathf.Max(1, _qty - 1); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); };
            var qty = new Label { Text = _qty.ToString(), CustomMinimumSize = new Vector2(76, BTNH), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            qty.AddThemeFontSizeOverride("font_size", FontQty);
            var plus = new Button { Text = "+", CustomMinimumSize = new Vector2(BTNH, BTNH) };
            plus.AddThemeFontSizeOverride("font_size", FontButton);
            plus.Pressed += () => { _qty = Mathf.Clamp(_qty + 1, 1, Mathf.Max(1, MaxCraftable(new Crafting.PlayerInvAdapter(Inv)))); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); };
            row.AddChild(minus); row.AddChild(qty); row.AddChild(plus);
            var craft = new Button { Text = "CRAFT", CustomMinimumSize = new Vector2(200, BTNH), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            craft.AddThemeFontSizeOverride("font_size", FontButton);
            craft.Disabled = !canMake;
            if (!canMake) craft.TooltipText = blocker;
            craft.Pressed += OnCraft;
            row.AddChild(craft);
            _detailBox.AddChild(row);
        }

        // how many times this recipe can be crafted from the current bag (min over consumed inputs).
        int MaxCraftable(Crafting.IInv inv, BlueprintDef bp = null)
        {
            bp ??= _sel;
            if (bp == null) return 0;
            int max = int.MaxValue;
            foreach (var ing in bp.Inputs)
            {
                if (!ing.Consume || ing.Amount <= 0) continue;
                var ia = Assets.findByGuid(ing.Guid);
                int have = ia != null ? inv.Count(ia.id) : 0;
                max = Mathf.Min(max, have / ing.Amount);
            }
            return max == int.MaxValue ? 1 : max;
        }

        static void AddHead(GridContainer g, string s)
        {
            var l = new Label { Text = s };
            l.AddThemeFontSizeOverride("font_size", FontSmallText);
            l.AddThemeColorOverride("font_color", Dim);
            g.AddChild(l);
        }

        // fill: the ITEM TYPE column takes the table's spare width and CUTS a name too long for it with an ellipsis,
        // rather than pushing the HAVE column out of the panel -- the bigger text made that reachable.
        static void AddCell(GridContainer g, string s, Color c, bool fill = false)
        {
            var l = new Label { Text = s };
            if (fill) { l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; l.ClipText = true; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; l.TooltipText = s; l.MouseFilter = Control.MouseFilterEnum.Pass; }
            l.AddThemeFontSizeOverride("font_size", FontText);
            l.AddThemeColorOverride("font_color", c);
            g.AddChild(l);
        }

        // CRAFT: single-player -> escrow the ingredients into a queue job (produced on the timer). Multiplayer keeps
        // the server-authoritative immediate craft (there's no client-side limbo to reconcile there yet).
        void OnCraft()
        {
            if (_sel == null || !Known(_sel) || !Crafting.MeetsSkill(_sel, Player?.Skills)) return;
            var inv = new Crafting.PlayerInvAdapter(Inv);
            int n = Mathf.Clamp(_qty, 1, Mathf.Max(1, MaxCraftable(inv)));
            if (Player?.NetCraft != null)
            {
                int idx = -1;
                for (int i = 0; i < BlueprintRegistry.All.Count; i++)
                    if (ReferenceEquals(BlueprintRegistry.All[i], _sel)) { idx = i; break; }
                if (idx >= 0) for (int k = 0; k < n; k++) Player.NetCraft((ushort)idx);
            }
            else Enqueue(_sel, n);
            _qty = 1;
            Rebuild();
        }

        // the quick-craft entry point (InventoryUI's bottom-right bar): queue a specific recipe. SP escrows into the
        // queue like the CRAFT button; MP sends the immediate NetCraft. Clamps to what the bag can actually make.
        public void QueueCraft(BlueprintDef bp, int qty)
        {
            if (Inv == null || bp == null || !Known(bp) || !Crafting.MeetsSkill(bp, Player?.Skills)) return;
            if (!Crafting.HasStations(bp, Player?.CraftingStationTags())) return;   // require the recipe's workbench/station
            var inv = new Crafting.PlayerInvAdapter(Inv);
            if (!Crafting.CanCraft(bp, inv, out _)) return;
            int n = Mathf.Clamp(qty, 1, Mathf.Max(1, MaxCraftable(inv, bp)));
            if (Player?.NetCraft != null)
            {
                int idx = -1;
                for (int i = 0; i < BlueprintRegistry.All.Count; i++)
                    if (ReferenceEquals(BlueprintRegistry.All[i], bp)) { idx = i; break; }
                if (idx >= 0) for (int k = 0; k < n; k++) Player.NetCraft((ushort)idx);
            }
            else Enqueue(bp, n);
            if (_open) Rebuild();
        }

        // queue a job: resolve + consume its per-unit ingredients x n into limbo, then prepend it on the LEFT.
        void Enqueue(BlueprintDef bp, int n)
        {
            var inv = new Crafting.PlayerInvAdapter(Inv);
            var perUnit = new List<(ushort id, int amt)>();
            foreach (var ing in bp.Inputs)
            {
                if (!ing.Consume) continue;   // tools stay in the bag
                var a = Assets.findByGuid(ing.Guid);
                if (a != null) perUnit.Add(((ushort)a.id, ing.Amount));
            }
            foreach (var (id, amt) in perUnit) inv.Remove(id, amt * n);   // ingredients -> limbo
            _queue.Insert(0, new QueueJob { Bp = bp, Out = OutAsset(bp), Qty = n, TimeLeft = CraftTimeFor(bp), PerUnit = perUnit });
        }

        void Produce(QueueJob job)
        {
            var inv = new Crafting.PlayerInvAdapter(Inv);
            int outAmt = job.Bp.Outputs.Count > 0 ? job.Bp.Outputs[0].Amount : 1;
            if (job.Out != null) inv.Add((ushort)job.Out.id, outAmt);
            Log.Print($"[craft] produced {Title(job.Bp)}");
        }

        // cancel: hand the escrowed ingredients for the REMAINING units back to the bag, drop the job.
        //
        // MULTIPLAYER ASKS, IT DOES NOT ACT. A server-driven job has PerUnit == null -- AdoptServerQueue has no
        // idea what the server actually spent -- so the local path below used to throw an NRE inside the gui
        // handler on the first click and leave the tile sitting there while the server carried on crafting.
        // Refunding locally instead would have been worse: the client would print the ingredients AND still get
        // the product. Same discipline as TickQueue, which counts down but never produces.
        void Cancel(QueueJob job)
        {
            if (_serverDriven)
            {
                int i = _queue.IndexOf(job);
                if (i < 0 || Player?.NetCraftCancel == null) return;
                // the display list is reversed against the server's (active = rightmost here, jobs[0] there)
                int slot = _queue.Count - 1 - i;
                if (slot >= 0 && slot <= byte.MaxValue) Player.NetCraftCancel((byte)slot);
                return;   // the tile leaves when the server's next queue says so
            }
            var inv = new Crafting.PlayerInvAdapter(Inv);
            if (job.PerUnit != null) foreach (var (id, amt) in job.PerUnit) inv.Add(id, amt * job.Qty);
            _queue.Remove(job);
            if (_open) { RebuildQueue(); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); }
        }

        // draw the queue tiles RIGHT-aligned (rightmost = active); click a tile to cancel it.
        void RebuildQueue()
        {
            if (_queueRow == null) return;
            foreach (Node c in _queueRow.GetChildren()) c.QueueFree();
            _activeBar = null;
            if (_qEmpty != null) _qEmpty.Visible = _queue.Count == 0;
            ClampScroll();
            int n = _queue.Count, step = TILEQ + 8;
            for (int i = 0; i < n; i++)
            {
                var job = _queue[i];
                bool active = i == n - 1;
                var tile = new Panel { Size = new Vector2(TILEQ, TILEQ), CustomMinimumSize = new Vector2(TILEQ, TILEQ) };
                Box(tile, active ? SelC : TileC);
                var tex = job.Out != null ? InventoryUI.IconFor(job.Out.id) : null;
                if (tex != null)
                {
                    var ico = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                    ico.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    ico.OffsetLeft = 7; ico.OffsetTop = 7; ico.OffsetRight = -7; ico.OffsetBottom = -9;
                    ico.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(ico);
                }
                else
                {
                    var lbl = new Label { Text = Title(job.Bp), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    lbl.AddThemeFontSizeOverride("font_size", 13);
                    lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(lbl);
                }
                if (job.Qty > 1)
                {
                    var badge = new Label { Text = $"x{job.Qty}", Position = new Vector2(TILEQ - 50, TILEQ - 30), Size = new Vector2(46, 24), HorizontalAlignment = HorizontalAlignment.Right };
                    badge.AddThemeFontSizeOverride("font_size", FontSmallText);
                    badge.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
                    badge.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(badge);
                }
                if (active)
                {
                    _activeBar = new ColorRect { Color = Good, Position = new Vector2(3, TILEQ - 7), Size = new Vector2(0, 4) };
                    _activeBar.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(_activeBar);
                }
                tile.MouseFilter = Control.MouseFilterEnum.Ignore;   // _queueRow owns the mouse (drag / click / rmb)
                tile.Position = new Vector2(_queueRow.Size.X - (n - i) * step + _qScroll, (_queueRow.Size.Y - TILEQ) / 2f);
                _queueRow.AddChild(tile);
            }
        }

        // queue interaction (master's spec): DRAG the icons to scroll; LMB CLICK an icon to remove it (refund);
        // RMB an icon to move it to the START (rightmost = active). A drag past a few px suppresses the click.
        void OnQueueGuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed) { _qDragStartX = mb.Position.X; _qScroll0 = _qScroll; _qDragging = false; _qPressJob = JobAt(mb.Position.X); }
                    else { if (!_qDragging && _qPressJob != null) Cancel(_qPressJob); _qPressJob = null; _qDragging = false; }
                }
                else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    var j = JobAt(mb.Position.X); if (j != null) MoveToStart(j);
                }
            }
            else if (e is InputEventMouseMotion mm && mm.ButtonMask.HasFlag(MouseButtonMask.Left))
            {
                float dx = mm.Position.X - _qDragStartX;
                if (Mathf.Abs(dx) > 6f) _qDragging = true;
                if (_qDragging) { _qScroll = _qScroll0 + dx; ClampScroll(); LayoutQueue(); }
            }
        }

        QueueJob JobAt(float mouseX)
        {
            int n = _queue.Count, step = TILEQ + 8;
            for (int i = 0; i < n; i++)
            {
                float x = _queueRow.Size.X - (n - i) * step + _qScroll;
                if (mouseX >= x && mouseX <= x + TILEQ) return _queue[i];
            }
            return null;
        }

        void LayoutQueue()   // reposition existing tiles for a smooth drag (no free/rebuild)
        {
            var kids = _queueRow.GetChildren();
            int n = kids.Count, step = TILEQ + 8;
            for (int i = 0; i < n; i++)
                if (kids[i] is Control c) c.Position = new Vector2(_queueRow.Size.X - (n - i) * step + _qScroll, (_queueRow.Size.Y - TILEQ) / 2f);
        }

        void ClampScroll()
        {
            int step = TILEQ + 8;
            float max = Mathf.Max(0f, _queue.Count * step - (_queueRow?.Size.X ?? 0f));
            _qScroll = Mathf.Clamp(_qScroll, 0f, max);
        }

        // RMB: promote a job to the START (rightmost = active) so it crafts next; give it a fresh timer.
        void MoveToStart(QueueJob job)
        {
            // MP has no reorder command, and shuffling a display mirror would show a promotion the server never
            // made -- reverted on its next queue update. Do nothing visible rather than lie about the order.
            if (_serverDriven) return;
            if (!_queue.Remove(job)) return;
            job.TimeLeft = CraftTimeFor(job.Bp);
            _queue.Add(job);
            if (_open) RebuildQueue();
        }

        // test/render hook (UG_CRAFTQUEUE): queue the first `jobs` craftable recipes so a --craftmenu shot shows the queue.
        public void DebugQueueCraftable(int jobs, int qtyEach)
        {
            int done = 0;
            foreach (var bp in _all)
            {
                if (done >= jobs) break;
                var inv = new Crafting.PlayerInvAdapter(Inv);
                if (Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills))
                {
                    _sel = bp;
                    Enqueue(bp, Mathf.Clamp(qtyEach, 1, Mathf.Max(1, MaxCraftable(inv))));
                    done++;
                }
            }
            if (_open) Rebuild();
        }

        /// <summary>Search matches the OUTPUT name, any INGREDIENT name, or the skill name -- so "metal scrap"
        /// finds what it feeds, not just a recipe called that.</summary>
        static bool Matches(BlueprintDef bp, string q)
        {
            if (Title(bp).Contains(q, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(bp.Skill) && bp.Skill.Contains(q, System.StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var ing in bp.Inputs)
            {
                var a = Assets.findByGuid(ing.Guid);
                if (a?.itemName != null && a.itemName.Contains(q, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool MatchesForTest(BlueprintDef bp, string q) => Matches(bp, q);
        /// <summary>Test seam: the recipes the grid is showing right now -- the SAME View() Rebuild draws from.</summary>
        public List<BlueprintDef> DebugView() => View();
        /// <summary>Test seam: type a search. Setting LineEdit.Text from code does not emit TextChanged, so this does
        /// what that handler does.</summary>
        public void DebugSetSearch(string q) { if (_search != null) _search.Text = q; _sel = null; _look = ItemLookup.None; Rebuild(); }
        /// <summary>Test seam: how many tiles in the grid carry the padlock right now.</summary>
        public string DebugBlocker(BlueprintDef bp) => CraftBlocker(bp, new Crafting.PlayerInvAdapter(Inv), 1);
        public int DebugPadlocks() { int n = 0; foreach (Node t in _grid.GetChildren()) foreach (Node c in t.GetChildren()) if (c is PadlockGlyph) n++; return n; }
        public BlueprintDef DebugSelected => _sel;
        public string DebugHeader => _header?.Text ?? "";   // the SAME predicate the list filters on

        /// <summary>The crafted item's name. A Craft blueprint's OUTPUT IS ITS OWNER ITEM -- the outputs column is
        /// empty on every catalog row, so read Outputs first then fall back to the owner item.</summary>
        public static string Title(BlueprintDef bp)
        {
            if (bp.Outputs.Count > 0)
            {
                var o = Assets.findByGuid(bp.Outputs[0].Guid);
                if (o != null) return bp.Outputs[0].Amount > 1 ? $"{o.itemName} x{bp.Outputs[0].Amount}" : o.itemName;
            }
            if (ushort.TryParse(bp.OwnerItemId, out var oid))
            {
                var owner = Assets.find(oid);
                if (owner != null) return owner.itemName;
            }
            return string.IsNullOrEmpty(bp.Name) ? bp.Operation : bp.Name;
        }
    }

    /// <summary>A padlock, drawn: a rounded body with a keyhole and a shackle arc over it. Fills its own Size.</summary>
    public partial class PadlockGlyph : Control
    {
        public override void _Draw()
        {
            var s = Size;
            var ink = new Color(0.92f, 0.86f, 0.62f, 0.95f);       // brass, readable on both the grey tile and a dark icon
            var shade = new Color(0f, 0f, 0f, 0.55f);
            float bw = s.X, bh = s.Y * 0.58f, by = s.Y - bh;
            float r = bw * 0.30f, cx = s.X * 0.5f, cy = by + 1f;
            // shackle: an upper half-ring, drawn twice so it keeps a dark edge against a light icon
            DrawArc(new Vector2(cx, cy), r, Mathf.Pi, Mathf.Tau, 20, shade, s.X * 0.22f, true);
            DrawArc(new Vector2(cx, cy), r, Mathf.Pi, Mathf.Tau, 20, ink, s.X * 0.14f, true);
            DrawLine(new Vector2(cx - r, cy), new Vector2(cx - r, by + 2f), ink, s.X * 0.14f);
            DrawLine(new Vector2(cx + r, cy), new Vector2(cx + r, by + 2f), ink, s.X * 0.14f);
            // body
            DrawRect(new Rect2(-1f, by - 1f, bw + 2f, bh + 2f), shade);
            DrawRect(new Rect2(0f, by, bw, bh), ink);
            // keyhole
            var hole = new Color(0.18f, 0.16f, 0.12f, 1f);
            DrawCircle(new Vector2(cx, by + bh * 0.40f), bw * 0.10f, hole);
            DrawRect(new Rect2(cx - bw * 0.04f, by + bh * 0.40f, bw * 0.08f, bh * 0.32f), hole);
        }
    }
}
