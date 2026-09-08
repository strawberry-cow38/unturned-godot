using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // The skills menu: the 3 specialities x their skills, each showing level/max + an Upgrade button that spends the
    // player's XP pool (PlayerSkills.TryUpgrade at the source cost). Toggled by a keybind (PlayerController).
    // Source surface = PlayerDashboardSkillsUI.
    //
    // LAID OUT OFF CraftingMenu.Layout, MEASURED RATHER THAN EYEBALLED (strawberry 2026-09-08: "fix the formatting
    // of the skills page and information page to more closely match the style of the inv and crafting menus.
    // actual measured detail"). Every number below is that file's:
    //   M = 16            outer margin on all four edges
    //   panel             = viewport - 2M, Box(UITheme.Bg, RadiusPanel=6)
    //   header            (16, MenuNavbar.Height + 8), size (pw-32, 24), FontBody in TextDim
    //   content top       MenuNavbar.Height + 40
    //   Gutter = 16       crafting's gap between its columns (gridX = M + catW + 16)
    //   RowH = HeadH = 30 crafting's category-row height, separation 2
    // This screen was the odd one out on every one of them: a fixed 640x680 box floating in the middle of a
    // full-screen backdrop, header 24 px in the default white at (20, 14) INSIDE that box, rows built from
    // unstyled Labels and Buttons -- Godot's default light chrome, which is the "one screen looks like a
    // different app" effect arriving from controls nobody remembered to style.
    //
    // THREE COLUMNS, ONE PER SPECIALITY. Fitting the panel is not the same as filling it: the first attempt kept
    // the single flat list and just stretched it to the full-screen panel, which flung every "Up" button to the
    // far screen edge with a metre of dead space between each label and its button -- worse than the fixed box it
    // replaced. Crafting earns its width with STRUCTURE (categories | tile grid | detail pane), so this screen
    // does the same with the structure it actually has: OFFENSE | DEFENSE | SUPPORT side by side. The leftover
    // width inside a row goes to the level bar, which is why a wide row reads better here rather than worse.
    public partial class SkillsUI : CanvasLayer
    {
        public PlayerController Player;
        public SDG.Unturned.PlayerSkills SkillsSource;   // optional direct skills (render harness); else Player.Skills

        const float M = 16f, Gutter = 16f;   // CraftingMenu.Layout's outer margin and inter-column gap
        const int RowH = 30, HeadH = 30;     // CraftingMenu's category-row height
        const int RowGap = 3;                // its _catList separation is 2; a row carrying a level bar wants one more
        const int NameW = 112;               // "Sharpshooter", the longest name, at FontBody
        const int LvlW = 34;                 // "0/7"
        const int BtnW = 96;                 // "120 XP"
        const int PipH = 10, PipGap = 3;
        const int XpW = 200;                 // the XP readout reserved out of the header line's right end

        Control _root;
        Panel _panel;
        Label _header, _xp;
        readonly ScrollContainer[] _cols = new ScrollContainer[PlayerSkills.SPECIALITIES];
        readonly VBoxContainer[] _colBox = new VBoxContainer[PlayerSkills.SPECIALITIES];
        bool _open;
        public bool IsOpen => _open;
        Control _slide; MenuSwoop _swoop;   // swoop in/out (strawberry 2026-09-08)

        static readonly string[] SpecNames = { "OFFENSE", "DEFENSE", "SUPPORT" };
        static readonly string[][] SkillNames =
        {
            new[] { "Overkill", "Sharpshooter", "Dexterity", "Cardio", "Exercise", "Diving", "Parkour" },
            new[] { "Sneakybeaky", "Vitality", "Immunity", "Toughness", "Strength", "Warmblooded", "Survival" },
            new[] { "Healing", "Crafting", "Outdoors", "Cooking", "Fishing", "Agriculture", "Mechanic", "Engineer" },
        };

        // Crafting's own row colours, so a skill row and a recipe tile are the same object at rest and selected.
        static Color RowC => new(0.22f, 0.22f, 0.23f, 0.98f);              // CraftingMenu.TileC
        static Color RowDone => new(0.15f, 0.15f, 0.16f, 0.96f);           // its "cannot craft" tile: a maxed skill is spent, not offered

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;
            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);
            var dim = new ColorRect();   // the same frosted-glass backdrop as the inventory/crafting screens (unified UI, master 2026-09-03)
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = InventoryUI.BACKDROP_BLUR } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);
            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Skills, t => Player?.ShowMenu(t), () => { Close(); Input.MouseMode = Input.MouseModeEnum.Captured; });
            // The swoop's slider: a full-rect Control that owns NOTHING but an offset, so the animation cannot
            // fight this screen's own Layout() (which sets the panel's position from the viewport size).
            _slide = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _slide.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_slide);
            _panel = new Panel();
            UITheme.Panel(_panel);   // Box(Bg, RadiusPanel=6) -- identical to crafting's Box(_panel, UITheme.Bg, 6)
            _slide.AddChild(_panel);
            _swoop = MenuSwoop.Attach(this, _root, _slide);

            // Header line: crafting's "CRAFTING . N shown . M craftable now", same font, same colour, same place.
            _header = UITheme.Label(new Label(), UITheme.FontBody, UITheme.TextDim);
            _panel.AddChild(_header);
            // ...and the ONE accent on the screen. UITheme reserves Accent for "the single most important number
            // on a screen", and on the skills page that is unambiguously the XP you have to spend.
            _xp = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, UITheme.FontHeading, UITheme.Accent);
            _panel.AddChild(_xp);

            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var sc = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };   // crafting does the same on both of its lists
                _panel.AddChild(sc);
                var box = new VBoxContainer();
                box.AddThemeConstantOverride("separation", RowGap);
                sc.AddChild(box);
                _cols[s] = sc; _colBox[s] = box;
            }

            GetViewport().SizeChanged += Layout;
            Layout();
        }

        /// <summary>The crafting screen's own numbers, so the two read as one UI. Called on resize rather than
        /// every frame -- the panel used to be re-centred in _Process, which is a layout pass per frame for a
        /// value that only changes when the window does.</summary>
        void Layout()
        {
            if (_panel == null || _root == null) return;
            Vector2 vp = _root.Size;
            if (vp.X < 1f) vp = GetViewport().GetVisibleRect().Size;
            const float barH = MenuNavbar.Height;
            const float top = barH + 40f;
            float pw = vp.X - 2f * M, ph = vp.Y - 2f * M;
            _panel.Position = new Vector2(M, M);
            _panel.Size = new Vector2(pw, ph);
            _header.Position = new Vector2(16f, barH + 8f);
            _header.Size = new Vector2(Mathf.Max(120f, pw - 32f - XpW), 24f);
            _xp.Position = new Vector2(pw - 16f - XpW, barH + 4f);
            _xp.Size = new Vector2(XpW, 26f);

            float colW = (pw - 2f * M - 2f * Gutter) / PlayerSkills.SPECIALITIES;
            float colH = ph - top - M;
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                _cols[s].Position = new Vector2(M + s * (colW + Gutter), top);
                _cols[s].Size = new Vector2(colW, colH);
                _colBox[s].CustomMinimumSize = new Vector2(colW - 16f, 0f);   // crafting: _grid.CustomMinimumSize = gridW - 16
            }
        }

        public override void _Process(double delta)
        {
            // MP only: the upgrade is applied by the SERVER (the levels/XP change in the background when
            // the owner skills echo adopts) -- repaint off a cheap signature, like the InventoryUI poll.
            if (_open && Player?.NetUpgradeSkill != null)
            {
                long sig = SkillsSignature();
                if (sig != _lastSig) { _lastSig = sig; Refresh(); }
            }
        }

        long _lastSig = -1;
        long SkillsSignature()
        {
            var sk = SkillsSource ?? Player?.Skills;
            if (sk == null) return 0;
            long h = sk.experience;
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
                foreach (var skill in sk.skills[s]) h = (h * 31) ^ skill.level;
            return h;
        }

        MenuNavbar _navbar;
        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; if (_root != null) _root.Visible = true; Refresh(); _swoop?.In(); }
        // _open goes false NOW so input routing stops; the swoop hides the pixels when it lands.
        public void Close() { _open = false; if (_swoop == null || !_swoop.Out()) Visible = false; }

        void Refresh()
        {
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
                foreach (Node c in _colBox[s].GetChildren()) c.QueueFree();
            var sk = SkillsSource ?? Player?.Skills;
            if (sk == null) return;

            int lv = 0, cap = 0, ready = 0;
            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
                foreach (var k in sk.skills[s])
                {
                    lv += k.level; cap += k.max;
                    if (k.level < k.max && sk.experience >= k.Cost) ready++;
                }
            _header.Text = $"SKILLS   ·   {lv} / {cap} levels   ·   {ready} upgradable now";   // crafting: "CRAFTING · N shown · M craftable now"
            _xp.Text = $"{sk.experience} XP";

            for (int s = 0; s < PlayerSkills.SPECIALITIES; s++)
            {
                var arr = sk.skills[s];
                int slv = 0, scap = 0;
                foreach (var k in arr) { slv += k.level; scap += k.max; }
                _colBox[s].AddChild(SpecHeader(SpecNames[s], slv, scap));
                for (int i = 0; i < arr.Length; i++) _colBox[s].AddChild(SkillRow(sk, s, i));
            }
        }

        /// <summary>A column's title strip. Crafting's category row is a Panel of height 30 with the name at the
        /// left in FontBody and a count hard against the right in TextDim; this is that, with UITheme.Strip for
        /// the raised-bar treatment the inventory's section headers use.</summary>
        static Control SpecHeader(string name, int lv, int cap)
        {
            var p = new Panel { CustomMinimumSize = new Vector2(0, HeadH) };
            UITheme.Strip(p);
            var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            h.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            h.OffsetLeft = 10; h.OffsetRight = -10;
            p.AddChild(h);
            h.AddChild(UITheme.Label(new Label { Text = name, VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, UITheme.FontBody, UITheme.TextDim));
            h.AddChild(UITheme.Label(new Label { Text = $"{lv}/{cap}", VerticalAlignment = VerticalAlignment.Center }, UITheme.FontBody, UITheme.TextDim));
            return p;
        }

        /// <summary>One skill: name, a level bar of `max` segments, the level readout, and the spend button.
        ///
        /// The bar is what makes a wide row work. Name and button are both fixed-width, so on a full-screen
        /// panel every pixel between them would otherwise be dead air -- give that span to the levels and a
        /// wider column reads BETTER, which is the difference between fitting the panel and filling it. It is
        /// also what the source screen shows (PlayerDashboardSkillsUI draws level boxes, not a number).</summary>
        Control SkillRow(PlayerSkills sk, int spec, int idx)
        {
            var skill = sk.skills[spec][idx];
            bool maxed = skill.level >= skill.max;
            bool afford = !maxed && sk.experience >= skill.Cost;

            // NO GREEN EDGE, and that is the one place this screen deliberately departs from the crafting tile.
            // Crafting outlines the craftable tile in Good because ONE recipe in sixty-nine is craftable and the
            // edge is what makes it findable. Skills invert that ratio -- with any XP banked, nearly every row is
            // affordable -- so the same device paints eighteen of twenty-two rows green and stops meaning
            // anything. Rendered it to be sure, and it read as a wall. Affordability is carried by the button
            // instead: UITheme.Button's primary face when it can be pressed, its disabled face and TextDisabled
            // when it cannot, which is the token that already means "you cannot have this".
            var row = new Panel { CustomMinimumSize = new Vector2(0, RowH) };
            row.AddThemeStyleboxOverride("panel", UITheme.Box(maxed ? RowDone : RowC, UITheme.RadiusCell));

            var h = new HBoxContainer();
            h.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            h.OffsetLeft = 10; h.OffsetRight = -6; h.OffsetTop = 3; h.OffsetBottom = -3;
            h.AddThemeConstantOverride("separation", 10);
            row.AddChild(h);   // left at the container default (PASS) on purpose -- it holds the Up button

            h.AddChild(UITheme.Label(new Label
            {
                Text = SkillNames[spec][idx],
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(NameW, 0),
            }, UITheme.FontBody, maxed ? UITheme.TextDim : UITheme.Text));

            var pips = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(0, PipH),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            pips.AddThemeConstantOverride("separation", PipGap);
            for (int p = 0; p < skill.max; p++)
            {
                var seg = new Panel { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(8, PipH) };
                // Selected (the lit-tab grey), NOT Accent: the accent is spent once per screen and it is spent
                // on the XP total. Twenty rows of it would leave none of them meaning anything.
                seg.AddThemeStyleboxOverride("panel", UITheme.Box(p < skill.level ? UITheme.Selected : UITheme.SlotEmpty, 2));
                pips.AddChild(seg);
            }
            h.AddChild(pips);

            h.AddChild(UITheme.Label(new Label
            {
                Text = $"{skill.level}/{skill.max}",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(LvlW, 0),
            }, UITheme.FontSmall, UITheme.TextDim));

            var btn = new Button { Text = maxed ? "MAX" : $"{skill.Cost} XP", CustomMinimumSize = new Vector2(BtnW, RowH - 8) };
            UITheme.Button(btn, afford);   // primary face only when it can actually be pressed
            btn.Disabled = !afford;
            // MP: the spend is a REQUEST -- the server's TryUpgrade validates cost/cap and the
            // owner skills echo re-levels (the _Process poll repaints). SP: the direct local spend.
            int cSpec = spec, cIdx = idx;   // capture for the closure
            btn.Pressed += () =>
            {
                if (Player != null && Player.RequestUpgradeSkill((byte)cSpec, (byte)cIdx)) return;
                if (sk.TryUpgrade(cSpec, cIdx)) Refresh();
            };
            h.AddChild(btn);
            return row;
        }
    }
}
