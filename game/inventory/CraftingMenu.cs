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
        bool _open;
        public bool IsOpen => _open;

        // resolved once per Open(): every indexed recipe, its output asset, and its category bucket
        readonly List<BlueprintDef> _all = new();
        readonly Dictionary<BlueprintDef, ItemAsset> _out = new();
        readonly Dictionary<BlueprintDef, string> _catOf = new();

        static Color Bg => new(0.08f, 0.10f, 0.13f, 0.97f);
        static Color Bar => new(0.17f, 0.24f, 0.32f, 0.95f);
        static Color SelC => new(0.30f, 0.42f, 0.54f, 0.95f);
        static Color TileC => new(0.14f, 0.17f, 0.21f, 0.95f);
        static Color Dim => new(0.60f, 0.60f, 0.64f);
        static Color Good => new(0.62f, 0.82f, 0.60f);
        static Color Bad => new(0.86f, 0.52f, 0.46f);

        static void Box(Control p, Color c, int r = 4)
        {
            var sb = new StyleBoxFlat { BgColor = c, CornerRadiusTopLeft = r, CornerRadiusTopRight = r, CornerRadiusBottomLeft = r, CornerRadiusBottomRight = r };
            p.AddThemeStyleboxOverride("panel", sb);
        }

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

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f) };
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
            _header.AddThemeFontSizeOverride("font_size", 20);
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
            _search.AddThemeFontSizeOverride("font_size", 14);
            _search.TextChanged += _ => { _sel = null; Rebuild(); };
            _panel.AddChild(_search);

            // BOTTOM: crafting queue -- STUB (space reserved)
            var queue = new Panel { Position = new Vector2(16, PANELH - bottomPad + 8), Size = new Vector2(CATW + 12 + GRIDW, bottomPad - 24) };
            Box(queue, new Color(0.10f, 0.12f, 0.15f, 0.95f));
            _panel.AddChild(queue);
            var qLabel = new Label { Text = "CRAFTING QUEUE", Position = new Vector2(14, 8), Size = new Vector2(300, 24) };
            qLabel.AddThemeFontSizeOverride("font_size", 15);
            qLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.54f));
            queue.AddChild(qLabel);
            var qStub = new Label { Text = "(empty)", Position = new Vector2(14, 30), Size = new Vector2(300, 20) };
            qStub.AddThemeFontSizeOverride("font_size", 12);
            qStub.AddThemeColorOverride("font_color", new Color(0.38f, 0.38f, 0.42f));
            queue.AddChild(qStub);

            // RIGHT: detail
            int detX = gridX + GRIDW + 14;
            _detail = new Panel { Position = new Vector2(detX, top), Size = new Vector2(DETW, PANELH - top - 16) };
            Box(_detail, new Color(0.11f, 0.14f, 0.18f, 0.96f));
            _panel.AddChild(_detail);
            _detailBox = new VBoxContainer { Position = new Vector2(16, 14), CustomMinimumSize = new Vector2(DETW - 32, PANELH - top - 44) };
            _detailBox.AddThemeConstantOverride("separation", 6);
            _detail.AddChild(_detailBox);
        }

        public override void _Process(double delta)
        {
            if (_open && _panel != null)
                _panel.Position = new Vector2((_root.Size.X - PANELW) / 2f, (_root.Size.Y - PANELH) / 2f);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Close() { _open = false; Visible = false; }

        public void Open()
        {
            _open = true; Visible = true;
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

        static ItemAsset OutAsset(BlueprintDef bp)
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

            // categories (only non-empty ones)
            foreach (Node c in _catList.GetChildren()) c.QueueFree();
            foreach (var cat in CatOrder)
            {
                int n = CountFor(cat);
                if (n == 0) continue;
                var row = new Panel { CustomMinimumSize = new Vector2(CATW, 30) };
                if (cat == _cat) Box(row, SelC);
                var b = new Button { Text = $"  {cat}", Flat = true, Alignment = HorizontalAlignment.Left, Size = new Vector2(CATW, 30), CustomMinimumSize = new Vector2(CATW, 30) };
                b.AddThemeFontSizeOverride("font_size", 14);
                string capture = cat;
                b.Pressed += () => { _cat = capture; _search.Text = ""; _sel = null; Rebuild(); };
                row.AddChild(b);
                var cnt = new Label { Text = n.ToString(), Position = new Vector2(CATW - 40, 5), Size = new Vector2(32, 20), HorizontalAlignment = HorizontalAlignment.Right };
                cnt.AddThemeFontSizeOverride("font_size", 13);
                cnt.AddThemeColorOverride("font_color", Dim);
                cnt.MouseFilter = Control.MouseFilterEnum.Ignore;
                row.AddChild(cnt);
                _catList.AddChild(row);
            }

            // grid
            foreach (Node c in _grid.GetChildren()) c.QueueFree();
            var view = View();
            int canNow = 0;
            foreach (var bp in _all) if (Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills)) canNow++;
            _header.Text = $"CRAFTING   ·   {view.Count} shown   ·   {canNow} craftable now";
            if (view.Count == 0)
                _grid.AddChild(new Label { Text = "  nothing here" });
            else
                foreach (var bp in view) _grid.AddChild(Tile(bp, inv));

            if (_sel == null || !_out.ContainsKey(_sel)) _sel = view.Count > 0 ? view[0] : null;
            ShowDetail(inv);
        }

        Control Tile(BlueprintDef bp, Crafting.IInv inv)
        {
            var a = _out.TryGetValue(bp, out var av) ? av : null;
            bool can = Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills);
            var tile = new Panel { CustomMinimumSize = new Vector2(TILE, TILE) };
            Box(tile, ReferenceEquals(bp, _sel) ? SelC : TileC);

            var tex = a != null ? InventoryUI.IconFor(a.id) : null;
            if (tex != null)
            {
                var ico = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
                ico.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                ico.OffsetLeft = 6; ico.OffsetTop = 6; ico.OffsetRight = -6; ico.OffsetBottom = -6;
                ico.MouseFilter = Control.MouseFilterEnum.Ignore;
                if (!can) ico.Modulate = new Color(1f, 1f, 1f, 0.4f);
                tile.AddChild(ico);
            }
            else
            {
                var lbl = new Label { Text = Title(bp), AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                lbl.OffsetLeft = 4; lbl.OffsetTop = 4; lbl.OffsetRight = -4; lbl.OffsetBottom = -4;
                lbl.AddThemeFontSizeOverride("font_size", 11);
                lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
                if (!can) lbl.AddThemeColorOverride("font_color", Dim);
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
            t.AddThemeFontSizeOverride("font_size", 18);
            nameBox.AddChild(t);
            int outCount = _sel.Outputs.Count > 0 ? _sel.Outputs[0].Amount : 1;
            if (outCount > 1)
            {
                var oc = new Label { Text = $"makes x{outCount}" };
                oc.AddThemeFontSizeOverride("font_size", 12); oc.AddThemeColorOverride("font_color", Dim);
                nameBox.AddChild(oc);
            }
            head.AddChild(nameBox);
            _detailBox.AddChild(head);

            // gates
            if (_sel.RequiresSkill)
            {
                bool meets = Crafting.MeetsSkill(_sel, Player?.Skills);
                var sk = new Label { Text = $"requires {_sel.Skill} {_sel.SkillLevel}" };
                sk.AddThemeFontSizeOverride("font_size", 13);
                sk.AddThemeColorOverride("font_color", meets ? Dim : Bad);
                _detailBox.AddChild(sk);
            }
            if (_sel.RequiresStation)
            {
                var st = new Label { Text = "requires a crafting station" };
                st.AddThemeFontSizeOverride("font_size", 13);
                st.AddThemeColorOverride("font_color", Dim);
                _detailBox.AddChild(st);
            }

            // description
            if (a != null && !string.IsNullOrEmpty(a.description))
            {
                var d = new Label { Text = a.description, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                d.AddThemeFontSizeOverride("font_size", 13);
                d.AddThemeColorOverride("font_color", new Color(0.78f, 0.79f, 0.82f));
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
            bool canMake = Crafting.CanCraft(_sel, inv, out string why) && Crafting.MeetsSkill(_sel, Player?.Skills);
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
        int MaxCraftable(Crafting.IInv inv)
        {
            if (_sel == null) return 0;
            int max = int.MaxValue;
            foreach (var ing in _sel.Inputs)
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
            l.AddThemeFontSizeOverride("font_size", 11);
            l.AddThemeColorOverride("font_color", Dim);
            g.AddChild(l);
        }

        static void AddCell(GridContainer g, string s, Color c)
        {
            var l = new Label { Text = s };
            l.AddThemeFontSizeOverride("font_size", 13);
            l.AddThemeColorOverride("font_color", c);
            g.AddChild(l);
        }

        void OnCraft()
        {
            if (_sel == null || !Crafting.MeetsSkill(_sel, Player?.Skills)) return;
            int n = Mathf.Max(1, _qty);
            for (int k = 0; k < n; k++)
            {
                if (Player?.NetCraft != null)
                {
                    int idx = -1;
                    for (int i = 0; i < BlueprintRegistry.All.Count; i++)
                        if (ReferenceEquals(BlueprintRegistry.All[i], _sel)) { idx = i; break; }
                    if (idx < 0) break;
                    Player.NetCraft((ushort)idx);
                }
                else
                {
                    var inv = new Crafting.PlayerInvAdapter(Inv);
                    if (!Crafting.CanCraft(_sel, inv, out _)) break;
                    if (!Crafting.DoCraft(_sel, inv)) break;
                }
            }
            GD.Print($"[craft] made {Title(_sel)} x{n}");
            _qty = 1;
            Rebuild();
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
