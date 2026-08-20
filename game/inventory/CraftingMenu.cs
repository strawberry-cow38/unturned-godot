using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // THE CRAFTING MENU (strawberry: "lets get a crafting menu. accessible via the inventory crafting button.
    // indexed list of all available crafting recipes ... ONLY relevant items that are accessible right now, none
    // of the bullshit recipes from curated maps", then "forget the current ui").
    //
    // A BROWSABLE INDEX, not a supplies panel. The old CraftingUI listed BlueprintRegistry.Applicable() -- what the
    // current bag can make -- so an empty bag showed an empty menu, which reads as broken rather than as "you have
    // no materials". This lists every recipe the port can express and shows craftability per row, so a recipe you
    // cannot afford is visible and greyed with the shortfall on its button, not absent.
    //
    // WHAT "AVAILABLE" MEANS HERE, measured rather than assumed: blueprints.tsv is 1875 rows; 1569 carry no inputs
    // (Salvage/Repair/Fill target-ops, excluded by design); 252 are Craft recipes with inputs; 195 have an owner
    // item AND every ingredient present in this port's catalog. The 57 dropped are precisely the curated-map
    // recipes -- they name ingredients that do not exist here, so they were never craftable and listing them is the
    // noise being complained about. Item resolution is the filter because it is CHECKABLE; a hand-kept list of
    // which map each item spawns on is not, and Washington/Yukon are not even installed on this box to check
    // against (only PEI's Spawns/ tables are local).
    //
    // 126 of the 195 are pure recolours (Blue Daypack <- White Daypack). They are real recipes so they are listed,
    // but they outnumber the 69 genuine crafts two to one, so they sit in their own section at the bottom rather
    // than burying metal bars, planks, sentries and generators.
    public partial class CraftingMenu : CanvasLayer
    {
        public PlayerInventory Inv;
        public PlayerController Player;

        const int PANELW = 900, PANELH = 620;
        const int LISTW = 400;

        Control _root;
        Panel _panel;
        Label _header;
        LineEdit _search;
        VBoxContainer _list;
        Panel _detail;
        VBoxContainer _detailBox;
        BlueprintDef _sel;
        bool _open;
        public bool IsOpen => _open;

        static Color Bg => new(0.08f, 0.10f, 0.13f, 0.96f);
        static Color Bar => new(0.17f, 0.24f, 0.32f, 0.90f);
        static Color RowOn => new(0.30f, 0.40f, 0.50f, 0.85f);
        static Color Dim => new(0.58f, 0.58f, 0.62f);

        static void Box(Panel p, Color c)
        {
            var sb = new StyleBoxFlat { BgColor = c, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
            p.AddThemeStyleboxOverride("panel", sb);
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
            Box(_panel, Bg);
            _root.AddChild(_panel);

            var bar = new Panel { Position = new Vector2(0, 0), Size = new Vector2(PANELW, 44) };
            Box(bar, Bar);
            _panel.AddChild(bar);

            _header = new Label { Text = "CRAFTING", Position = new Vector2(16, 8), Size = new Vector2(PANELW - 120, 28) };
            _header.AddThemeFontSizeOverride("font_size", 20);
            _panel.AddChild(_header);

            var close = new Button { Text = "X", Position = new Vector2(PANELW - 44, 8), Size = new Vector2(30, 28) };
            close.Pressed += Close;
            _panel.AddChild(close);

            _search = new LineEdit { Position = new Vector2(14, 54), Size = new Vector2(LISTW, 30), PlaceholderText = "search recipes..." };
            _search.AddThemeFontSizeOverride("font_size", 14);
            _search.TextChanged += _ => Rebuild();
            _panel.AddChild(_search);

            var scroll = new ScrollContainer { Position = new Vector2(14, 92), Size = new Vector2(LISTW, PANELH - 106) };
            scroll.CustomMinimumSize = scroll.Size;
            _panel.AddChild(scroll);
            _list = new VBoxContainer { CustomMinimumSize = new Vector2(LISTW - 16, 0) };
            _list.AddThemeConstantOverride("separation", 2);
            scroll.AddChild(_list);

            _detail = new Panel { Position = new Vector2(LISTW + 26, 54), Size = new Vector2(PANELW - LISTW - 40, PANELH - 68) };
            Box(_detail, new Color(0.11f, 0.14f, 0.18f, 0.95f));
            _panel.AddChild(_detail);
            _detailBox = new VBoxContainer { Position = new Vector2(14, 12), CustomMinimumSize = new Vector2(PANELW - LISTW - 68, 0) };
            _detailBox.AddThemeConstantOverride("separation", 6);
            _detail.AddChild(_detailBox);
        }

        public override void _Process(double delta)
        {
            if (_open && _panel != null)
                _panel.Position = new Vector2((_root.Size.X - PANELW) / 2f, (_root.Size.Y - PANELH) / 2f);
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; Rebuild(); }
        public void Close() { _open = false; Visible = false; }

        void Rebuild()
        {
            foreach (Node c in _list.GetChildren()) c.QueueFree();
            if (Inv == null) return;
            var inv = new Crafting.PlayerInvAdapter(Inv);

            var all = BlueprintRegistry.Index();
            string q = (_search?.Text ?? "").Trim();
            if (q.Length > 0) all = all.FindAll(bp => Title(bp).Contains(q, System.StringComparison.OrdinalIgnoreCase));

            var real = new List<BlueprintDef>();
            var dyes = new List<BlueprintDef>();
            foreach (var bp in all) (BlueprintRegistry.IsRecolour(bp) ? dyes : real).Add(bp);
            real.Sort((a, b) => string.Compare(Title(a), Title(b), System.StringComparison.OrdinalIgnoreCase));
            dyes.Sort((a, b) => string.Compare(Title(a), Title(b), System.StringComparison.OrdinalIgnoreCase));

            int can = 0;
            foreach (var bp in all) if (Crafting.CanCraft(bp, inv, out _)) can++;
            _header.Text = $"CRAFTING   ·   {all.Count} recipes   ·   {can} craftable now";

            if (all.Count == 0)
            {
                _list.AddChild(new Label { Text = q.Length > 0 ? $"nothing matches \"{q}\"" : "no recipes available" });
                return;
            }

            foreach (var bp in real) _list.AddChild(Row(bp, inv));
            if (dyes.Count > 0)
            {
                var h = new Label { Text = $"—  DYES & RECOLOURS  ({dyes.Count})" };
                h.AddThemeFontSizeOverride("font_size", 12);
                h.AddThemeColorOverride("font_color", Dim);
                _list.AddChild(h);
                foreach (var bp in dyes) _list.AddChild(Row(bp, inv));
            }

            if (_sel == null || !all.Contains(_sel)) _sel = real.Count > 0 ? real[0] : (dyes.Count > 0 ? dyes[0] : null);
            ShowDetail(inv);
        }

        Control Row(BlueprintDef bp, Crafting.IInv inv)
        {
            bool can = Crafting.CanCraft(bp, inv, out _) && Crafting.MeetsSkill(bp, Player?.Skills);
            var p = new Panel { CustomMinimumSize = new Vector2(0, 30) };
            if (ReferenceEquals(bp, _sel)) Box(p, RowOn);
            var b = new Button { Text = Title(bp), Flat = true, Size = new Vector2(LISTW - 20, 30),
                                 Alignment = HorizontalAlignment.Left };
            b.AddThemeFontSizeOverride("font_size", 14);
            if (!can) b.AddThemeColorOverride("font_color", Dim);
            b.Pressed += () => { _sel = bp; Rebuild(); };
            p.AddChild(b);
            return p;
        }

        void ShowDetail(Crafting.IInv inv)
        {
            foreach (Node c in _detailBox.GetChildren()) c.QueueFree();
            if (_sel == null) return;

            var t = new Label { Text = Title(_sel) };
            t.AddThemeFontSizeOverride("font_size", 19);
            _detailBox.AddChild(t);

            if (_sel.RequiresSkill)
            {
                var sk = new Label { Text = $"requires {_sel.Skill} {_sel.SkillLevel}" };
                sk.AddThemeFontSizeOverride("font_size", 13);
                sk.AddThemeColorOverride("font_color", Crafting.MeetsSkill(_sel, Player?.Skills) ? Dim : new Color(0.85f, 0.45f, 0.40f));
                _detailBox.AddChild(sk);
            }
            if (_sel.RequiresStation)
            {
                var st = new Label { Text = "requires a crafting station" };
                st.AddThemeFontSizeOverride("font_size", 13);
                st.AddThemeColorOverride("font_color", Dim);
                _detailBox.AddChild(st);
            }

            var ih = new Label { Text = "INGREDIENTS" };
            ih.AddThemeFontSizeOverride("font_size", 12);
            ih.AddThemeColorOverride("font_color", Dim);
            _detailBox.AddChild(ih);

            // PER-INGREDIENT HAVE/NEED. The whole reason to show a recipe you cannot make is to say what is short.
            foreach (var ing in _sel.Inputs)
            {
                var a = Assets.findByGuid(ing.Guid);
                int have = a != null ? inv.Count(a.id) : 0;
                bool ok = have >= ing.Amount;
                var l = new Label { Text = $"   {have}/{ing.Amount}   {a?.itemName ?? "?"}{(ing.Consume ? "" : "   (tool, not consumed)")}" };
                l.AddThemeFontSizeOverride("font_size", 14);
                l.AddThemeColorOverride("font_color", ok ? new Color(0.62f, 0.82f, 0.60f) : new Color(0.85f, 0.50f, 0.45f));
                _detailBox.AddChild(l);
            }

            bool canMake = Crafting.CanCraft(_sel, inv, out string why) && Crafting.MeetsSkill(_sel, Player?.Skills);
            var btn = new Button { Text = "CRAFT", CustomMinimumSize = new Vector2(160, 40) };
            btn.Disabled = !canMake;
            if (!canMake) btn.TooltipText = Crafting.MeetsSkill(_sel, Player?.Skills) ? why : $"needs {_sel.Skill} skill {_sel.SkillLevel}";
            btn.Pressed += OnCraft;
            _detailBox.AddChild(btn);
        }

        void OnCraft()
        {
            if (_sel == null) return;
            if (!Crafting.MeetsSkill(_sel, Player?.Skills)) return;
            if (Player?.NetCraft != null)
            {
                for (int i = 0; i < BlueprintRegistry.All.Count; i++)
                    if (ReferenceEquals(BlueprintRegistry.All[i], _sel)) { Player.NetCraft((ushort)i); break; }
                return;
            }
            var inv = new Crafting.PlayerInvAdapter(Inv);
            if (!Crafting.DoCraft(_sel, inv)) return;
            GD.Print($"[craft] made {Title(_sel)}");
            Rebuild();
        }

        /// <summary>The crafted item's name. A Craft blueprint's OUTPUT IS ITS OWNER ITEM -- the outputs column is
        /// empty on every row in the catalog (measured: 0 of 1875), so reading Outputs first and falling back is
        /// what keeps this from printing "item" for everything.</summary>
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
