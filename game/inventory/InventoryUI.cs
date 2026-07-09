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

        const int CELL = 50;         // SleekItems cell size
        const int HEADER = 30;       // per-page header strip (source SizeOffset_Y = height*50 + 30)
        const int PAD = 12;
        const int CLOTHW = 190;      // clothing column width
        const int GUTTER = 24;       // gap between clothing column and storage

        Control _root, _dash, _storageCol;
        readonly List<(Control slot, Label label, System.Func<Item> worn)> _clothing = new();
        bool _open;
        float _storageW, _storageH;

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
            _storageCol = new Control { Position = new Vector2(CLOTHW + GUTTER, 0) };
            _dash.AddChild(_storageCol);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; Refresh(); }
        public void Close() { _open = false; Visible = false; }

        public override void _Process(double delta) { if (_open) CenterDash(); }   // keep centred as the viewport settles

        // left column: the equip slots (hat/glasses/mask/shirt/vest/backpack/pants), each showing the worn item
        void BuildClothingColumn()
        {
            var box = new Panel { Position = Vector2.Zero, Size = new Vector2(CLOTHW, 7 * (CELL + 10) + 52) };
            StyleBox(box, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            _dash.AddChild(box);
            box.AddChild(Header("CLOTHING", new Vector2(10, 8), CLOTHW - 20));

            (string name, System.Func<Item> worn)[] rows =
            {
                ("Hat",      () => Inv?.wornHat),      ("Glasses",  () => Inv?.wornGlasses),
                ("Mask",     () => Inv?.wornMask),     ("Shirt",    () => Inv?.wornShirt),
                ("Vest",     () => Inv?.wornVest),     ("Backpack", () => Inv?.wornBackpack),
                ("Pants",    () => Inv?.wornPants),
            };
            float y = 42;
            foreach (var (name, worn) in rows)
            {
                var slot = new Panel { Position = new Vector2(12, y), Size = new Vector2(CELL, CELL) };
                StyleBox(slot, new Color(0f, 0f, 0f, 0.5f));
                box.AddChild(slot);
                var lbl = new Label { Text = name, Position = new Vector2(CELL + 14, y + 14) };
                lbl.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.75f));
                box.AddChild(lbl);
                _clothing.Add((slot, lbl, worn));
                y += CELL + 10;
            }
        }

        public void Refresh()
        {
            if (Inv == null || _storageCol == null) return;

            // worn clothing into the equip slots
            foreach (var (slot, lbl, worn) in _clothing)
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
            float y = 0;
            _storageW = 5 * CELL;
            y = AddSlot("PRIMARY", 0, y);
            y = AddSlot("SECONDARY", 1, y);
            (byte page, string name)[] grids =
            {
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

            CenterDash();
        }

        void CenterDash()
        {
            float w = CLOTHW + GUTTER + _storageW;
            float h = Mathf.Max(7 * (CELL + 10) + 52, _storageH);
            Vector2 vp = GetViewport().GetVisibleRect().Size;
            _dash.Position = new Vector2(Mathf.Round((vp.X - w) / 2f), Mathf.Round((vp.Y - h) / 2f));
        }

        float AddSlot(string name, byte page, float y)
        {
            var pg = Inv.items[page];
            _storageCol.AddChild(Header(name, new Vector2(0, y), 5 * CELL));
            y += HEADER - 6;
            var box = new Panel { Position = new Vector2(0, y), Size = new Vector2(5 * CELL, CELL) };
            StyleBox(box, new Color(0f, 0f, 0f, 0.45f));
            _storageCol.AddChild(box);
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

        // one item tile: dark rarity-tinted background + rarity border + name + amount badge, clipped to its footprint
        Control MakeTile(ItemJar jar, int w, int h)
        {
            var asset = jar.GetAsset();
            Color rar = asset != null ? ItemAsset.RarityColorUI(asset.rarity) : Colors.White;
            Color bg = new Color(rar.R * 0.22f, rar.G * 0.22f, rar.B * 0.22f, 0.97f);   // BackgroundIfLight(rarity)

            var tile = new Panel { Size = new Vector2(w, h), ClipContents = true };
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = rar };
            sb.SetBorderWidthAll(2);
            tile.AddThemeStyleboxOverride("panel", sb);

            var lbl = new Label { Text = asset?.itemName ?? "?" };
            lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            lbl.AddThemeColorOverride("font_color", rar.Lerp(Colors.White, 0.35f));
            lbl.AddThemeFontSizeOverride("font_size", w <= CELL ? 9 : 12);
            lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
            tile.AddChild(lbl);

            if (jar.item != null && jar.item.amount > 1)
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
