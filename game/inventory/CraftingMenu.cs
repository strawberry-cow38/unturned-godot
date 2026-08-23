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

        const int PANELW = 1100, PANELH = 680;
        const int CATW = 180, GRIDW = 470, DETW = 390, GRIDCOLS = 5, TILE = 84;

        Control _root;
        Panel _panel;
        Label _header;
        VBoxContainer _catList;
        LineEdit _search;
        GridContainer _grid;
        Panel _detail;
        VBoxContainer _detailBox;
        BlueprintDef _sel;
        string _cat = "All";
        int _qty = 1;
        System.Collections.Generic.HashSet<string> _stationTags = new();   // crafting-station tags the player currently has (recomputed each Rebuild)
        bool _open;
        public bool IsOpen => _open;

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
        const int TILEQ = 52;
        sealed class QueueJob { public BlueprintDef Bp; public ItemAsset Out; public int Qty; public float TimeLeft; public List<(ushort id, int amt)> PerUnit; }

        static float CraftTimeFor(BlueprintDef bp) => BASE_CRAFT_SECONDS;   // the per-recipe "crafting time variable" (all 1 s)

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

            var dim = new ColorRect { Color = UITheme.Scrim };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);

            _panel = new Panel { CustomMinimumSize = new Vector2(PANELW, PANELH), Size = new Vector2(PANELW, PANELH) };
            Box(_panel, Bg, 6);
            _root.AddChild(_panel);

            var bar = new Panel { Position = new Vector2(0, 0), Size = new Vector2(PANELW, 44) };
            Box(bar, Bar);
            _panel.AddChild(bar);
            _header = new Label { Text = "CRAFTING", Position = new Vector2(18, 8), Size = new Vector2(PANELW - 140, 28) };
            _header.AddThemeFontSizeOverride("font_size", UITheme.FontTitle);
            _panel.AddChild(_header);
            var close = new Button { Text = "X", Position = new Vector2(PANELW - 42, 8), Size = new Vector2(28, 28) };
            close.Pressed += Close;
            _panel.AddChild(close);

            int top = 54, bottomPad = 96;
            // LEFT: category list
            var catScroll = new ScrollContainer { Position = new Vector2(16, top), Size = new Vector2(CATW, PANELH - top - bottomPad) };
            catScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _panel.AddChild(catScroll);
            _catList = new VBoxContainer { CustomMinimumSize = new Vector2(CATW, 0) };
            _catList.AddThemeConstantOverride("separation", 2);
            catScroll.AddChild(_catList);

            // MIDDLE: icon grid + search
            int gridX = 16 + CATW + 12;
            var gridScroll = new ScrollContainer { Position = new Vector2(gridX, top), Size = new Vector2(GRIDW, PANELH - top - bottomPad - 40) };
            gridScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _panel.AddChild(gridScroll);
            _grid = new GridContainer { Columns = GRIDCOLS, CustomMinimumSize = new Vector2(GRIDW - 16, 0) };
            _grid.AddThemeConstantOverride("h_separation", 6);
            _grid.AddThemeConstantOverride("v_separation", 6);
            gridScroll.AddChild(_grid);
            _search = new LineEdit { Position = new Vector2(gridX, PANELH - bottomPad - 34), Size = new Vector2(GRIDW, 30), PlaceholderText = "search recipes..." };
            UITheme.Field(_search);
            _search.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _search.TextChanged += _ => { _sel = null; Rebuild(); };
            _panel.AddChild(_search);

            // BOTTOM: crafting queue -- jobs fill RIGHTWARD (rightmost = active/counting; new jobs prepend on the left)
            var queue = new Panel { Position = new Vector2(16, PANELH - bottomPad + 8), Size = new Vector2(CATW + 12 + GRIDW, bottomPad - 24) };
            Box(queue, UITheme.BgSolid);
            _panel.AddChild(queue);
            var qLabel = new Label { Text = "CRAFTING QUEUE", Position = new Vector2(14, 6), Size = new Vector2(200, 22) };
            qLabel.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            qLabel.AddThemeColorOverride("font_color", UITheme.TextDim);
            queue.AddChild(qLabel);
            _qEmpty = new Label { Text = "(empty)", Position = new Vector2(14, 28), Size = new Vector2(200, 20) };
            _qEmpty.AddThemeFontSizeOverride("font_size", UITheme.FontLabel);
            _qEmpty.AddThemeColorOverride("font_color", UITheme.TextDisabled);
            queue.AddChild(_qEmpty);
            _queueRow = new Control { Position = new Vector2(150, 4), Size = new Vector2(CATW + 12 + GRIDW - 158, bottomPad - 32) };
            _queueRow.ClipContents = true;
            _queueRow.MouseFilter = Control.MouseFilterEnum.Stop;   // handles drag-scroll / click-remove / rmb-promote (tiles are mouse-transparent)
            _queueRow.GuiInput += OnQueueGuiInput;
            queue.AddChild(_queueRow);

            // RIGHT: detail
            int detX = gridX + GRIDW + 14;
            _detail = new Panel { Position = new Vector2(detX, top), Size = new Vector2(DETW, PANELH - top - 16) };
            Box(_detail, UITheme.BgSolid);
            _panel.AddChild(_detail);
            _detailBox = new VBoxContainer { Position = new Vector2(16, 14), CustomMinimumSize = new Vector2(DETW - 32, PANELH - top - 44) };
            _detailBox.AddThemeConstantOverride("separation", 6);
            _detail.AddChild(_detailBox);
        }

        public override void _Process(double delta)
        {
            if (_open && _panel != null)
                _panel.Position = new Vector2((_root.Size.X - PANELW) / 2f, (_root.Size.Y - PANELH) / 2f);
            TickQueue((float)delta);
        }

        // the queue runs even while the menu is closed (a job you started keeps cooking in the background).
        void TickQueue(float dt)
        {
            if (_queue.Count == 0 || Inv == null) return;
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
                _activeBar.Size = new Vector2((TILEQ - 4) * Mathf.Clamp(p, 0f, 1f), 3);
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
        public void Close() { _open = false; Visible = false; }

        public void Open()
        {
            _open = true; Visible = true;
            _qScroll = 0f;   // start showing the active (rightmost) side
            ComputeData();
            if (System.Array.IndexOf(CatOrder, _cat) < 0 || CountFor(_cat) == 0) _cat = "All";
            Rebuild();
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
                if (q.Length > 0) { if (!Matches(bp, q)) continue; }
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
            foreach (var cat in CatOrder)
            {
                int n = CountFor(cat);
                if (n == 0) continue;
                var row = new Panel { CustomMinimumSize = new Vector2(CATW, 30) };
                if (cat == _cat) Box(row, SelC);
                var b = new Button { Text = $"  {cat}", Flat = true, Alignment = HorizontalAlignment.Left, Size = new Vector2(CATW, 30), CustomMinimumSize = new Vector2(CATW, 30) };
                b.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                string capture = cat;
                b.Pressed += () => { _cat = capture; _search.Text = ""; _sel = null; Rebuild(); };
                row.AddChild(b);
                var cnt = new Label { Text = n.ToString(), Position = new Vector2(CATW - 40, 5), Size = new Vector2(32, 20), HorizontalAlignment = HorizontalAlignment.Right };
                cnt.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                cnt.AddThemeColorOverride("font_color", Dim);
                cnt.MouseFilter = Control.MouseFilterEnum.Ignore;
                row.AddChild(cnt);
                _catList.AddChild(row);
            }

            // grid
            foreach (Node c in _grid.GetChildren()) c.QueueFree();
            var view = View();
            int canNow = 0;
            foreach (var bp in _all) if (Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills) && Crafting.HasStations(bp, _stationTags)) canNow++;
            _header.Text = $"CRAFTING   ·   {view.Count} shown   ·   {canNow} craftable now";
            if (view.Count == 0)
                _grid.AddChild(new Label { Text = "  nothing here" });
            else
                foreach (var bp in view) _grid.AddChild(Tile(bp, inv));

            if (_sel == null || !_out.ContainsKey(_sel)) _sel = view.Count > 0 ? view[0] : null;
            ShowDetail(inv);
            RebuildQueue();
        }

        Control Tile(BlueprintDef bp, Crafting.IInv inv)
        {
            var a = _out.TryGetValue(bp, out var av) ? av : null;
            bool can = Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills) && Crafting.HasStations(bp, _stationTags);
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
                ico.OffsetLeft = 6; ico.OffsetTop = 6; ico.OffsetRight = -6; ico.OffsetBottom = -6;
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
                lbl.OffsetLeft = 4; lbl.OffsetTop = 4; lbl.OffsetRight = -4; lbl.OffsetBottom = -4;
                lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
                lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                if (!can) lbl.AddThemeColorOverride("font_color", UITheme.TextDisabled);
                tile.AddChild(lbl);
            }

            var btn = new Button { Flat = true, TooltipText = Title(bp) };   // hover -> the item name
            btn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            btn.Pressed += () => { _sel = bp; _qty = 1; Rebuild(); };
            tile.AddChild(btn);
            return tile;
        }

        void ShowDetail(Crafting.IInv inv)
        {
            foreach (Node c in _detailBox.GetChildren()) c.QueueFree();
            if (_sel == null) { _detailBox.AddChild(new Label { Text = "select a recipe" }); return; }
            var a = _out.TryGetValue(_sel, out var av) ? av : null;

            // header: icon + name (+ output count)
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 10);
            var tex = a != null ? InventoryUI.IconFor(a.id) : null;
            if (tex != null)
                head.AddChild(new TextureRect { Texture = tex, CustomMinimumSize = new Vector2(56, 56), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            var nameBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var t = new Label { Text = Title(_sel), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            t.AddThemeFontSizeOverride("font_size", UITheme.FontHeading);
            nameBox.AddChild(t);
            int outCount = _sel.Outputs.Count > 0 ? _sel.Outputs[0].Amount : 1;
            if (outCount > 1)
            {
                var oc = new Label { Text = $"makes x{outCount}" };
                oc.AddThemeFontSizeOverride("font_size", UITheme.FontLabel); oc.AddThemeColorOverride("font_color", Dim);
                nameBox.AddChild(oc);
            }
            head.AddChild(nameBox);
            _detailBox.AddChild(head);

            // gates
            if (_sel.RequiresSkill)
            {
                bool meets = Crafting.MeetsSkill(_sel, Player?.Skills);
                var sk = new Label { Text = $"requires {_sel.Skill} {_sel.SkillLevel}" };
                sk.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                sk.AddThemeColorOverride("font_color", meets ? Dim : Bad);
                _detailBox.AddChild(sk);
            }
            if (_sel.RequiresStation)
            {
                var st = new Label { Text = "requires a crafting station" };
                st.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                st.AddThemeColorOverride("font_color", Dim);
                _detailBox.AddChild(st);
            }

            // description
            if (a != null && !string.IsNullOrEmpty(a.description))
            {
                var d = new Label { Text = a.description, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                d.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
                d.AddThemeColorOverride("font_color", UITheme.TextBody);
                _detailBox.AddChild(d);
            }

            _detailBox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });   // push the table + craft to the bottom

            // clamp qty to what's craftable, so the CRAFT button stays honest
            int max = MaxCraftable(inv);
            _qty = Mathf.Clamp(_qty, 1, Mathf.Max(1, max));

            // ingredients table: AMOUNT | ITEM TYPE | TOTAL | HAVE
            var tbl = new GridContainer { Columns = 4, CustomMinimumSize = new Vector2(DETW - 32, 0) };
            tbl.AddThemeConstantOverride("h_separation", 10); tbl.AddThemeConstantOverride("v_separation", 3);
            AddHead(tbl, "AMOUNT"); AddHead(tbl, "ITEM TYPE"); AddHead(tbl, "TOTAL"); AddHead(tbl, "HAVE");
            foreach (var ing in _sel.Inputs)
            {
                var ia = Assets.findByGuid(ing.Guid);
                int total = ing.Amount * _qty;
                int have = ia != null ? inv.Count(ia.id) : 0;
                bool ok = have >= total;
                AddCell(tbl, ing.Amount.ToString(), ok ? Good : Bad);
                AddCell(tbl, (ia?.itemName ?? "?") + (ing.Consume ? "" : "  (tool)"), ok ? Good : Bad);
                AddCell(tbl, ing.Consume ? total.ToString() : "-", ok ? Good : Bad);
                AddCell(tbl, have.ToString("N0"), ok ? Good : Bad);
            }
            _detailBox.AddChild(tbl);

            // amount stepper + CRAFT
            bool canMake = Crafting.CanCraft(_sel, inv, out string why) && Crafting.MeetsSkill(_sel, Player?.Skills) && Crafting.HasStations(_sel, _stationTags);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 6);
            var minus = new Button { Text = "−", CustomMinimumSize = new Vector2(40, 40) };
            minus.Pressed += () => { _qty = Mathf.Max(1, _qty - 1); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); };
            var qty = new Label { Text = _qty.ToString(), CustomMinimumSize = new Vector2(56, 40), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            qty.AddThemeFontSizeOverride("font_size", 16);
            var plus = new Button { Text = "+", CustomMinimumSize = new Vector2(40, 40) };
            plus.Pressed += () => { _qty = Mathf.Clamp(_qty + 1, 1, Mathf.Max(1, MaxCraftable(new Crafting.PlayerInvAdapter(Inv)))); ShowDetail(new Crafting.PlayerInvAdapter(Inv)); };
            row.AddChild(minus); row.AddChild(qty); row.AddChild(plus);
            var craft = new Button { Text = "CRAFT", CustomMinimumSize = new Vector2(180, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            craft.Disabled = !canMake;
            if (!canMake) craft.TooltipText = Crafting.MeetsSkill(_sel, Player?.Skills) ? why : $"needs {_sel.Skill} skill {_sel.SkillLevel}";
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
            l.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
            l.AddThemeColorOverride("font_color", Dim);
            g.AddChild(l);
        }

        static void AddCell(GridContainer g, string s, Color c)
        {
            var l = new Label { Text = s };
            l.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            l.AddThemeColorOverride("font_color", c);
            g.AddChild(l);
        }

        // CRAFT: single-player -> escrow the ingredients into a queue job (produced on the timer). Multiplayer keeps
        // the server-authoritative immediate craft (there's no client-side limbo to reconcile there yet).
        void OnCraft()
        {
            if (_sel == null || !Crafting.MeetsSkill(_sel, Player?.Skills)) return;
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
            if (Inv == null || bp == null || !Crafting.MeetsSkill(bp, Player?.Skills)) return;
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
            GD.Print($"[craft] produced {Title(job.Bp)}");
        }

        // cancel: hand the escrowed ingredients for the REMAINING units back to the bag, drop the job.
        void Cancel(QueueJob job)
        {
            var inv = new Crafting.PlayerInvAdapter(Inv);
            foreach (var (id, amt) in job.PerUnit) inv.Add(id, amt * job.Qty);
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
                    ico.OffsetLeft = 4; ico.OffsetTop = 4; ico.OffsetRight = -4; ico.OffsetBottom = -6;
                    ico.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(ico);
                }
                else
                {
                    var lbl = new Label { Text = Title(job.Bp), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    lbl.AddThemeFontSizeOverride("font_size", 9);
                    lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(lbl);
                }
                if (job.Qty > 1)
                {
                    var badge = new Label { Text = $"x{job.Qty}", Position = new Vector2(TILEQ - 26, TILEQ - 20), Size = new Vector2(24, 16), HorizontalAlignment = HorizontalAlignment.Right };
                    badge.AddThemeFontSizeOverride("font_size", UITheme.FontLabel);
                    badge.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
                    badge.MouseFilter = Control.MouseFilterEnum.Ignore;
                    tile.AddChild(badge);
                }
                if (active)
                {
                    _activeBar = new ColorRect { Color = Good, Position = new Vector2(2, TILEQ - 5), Size = new Vector2(0, 3) };
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

        public static bool MatchesForTest(BlueprintDef bp, string q) => Matches(bp, q);   // the SAME predicate the list filters on

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
}
