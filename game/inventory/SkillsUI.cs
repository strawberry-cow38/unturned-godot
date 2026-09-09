using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // THE SKILLS SCREEN IS A TREE (strawberry 2026-09-09: "convert the skills menu into a skills tree. at the top
    // we have our 'pillars'. main skills ... under those are separate nodes that advance. new recipes, character
    // buffs, etc. there are then 'sub pillars' which are advanced skills. engineering, etc. which unlock their own
    // nodes. some nodes may unlock multiple things, some nodes may require multiple things to unlock.").
    //
    // The graph, the placeholder content and the unlock rules live in SkillTree.cs; this file is only the view.
    // Two consequences of that split worth keeping:
    //   - the multi-prerequisite case is a GRAPH, not a tree. `improvised_armour` needs a node from Craft and one
    //     from Might, and `field_surgery` reaches from Medicine into Craft, so a link is drawn per requirement and
    //     may cross columns. Anything that assumes one parent per node will be wrong the first time it is used.
    //   - an ADVANCED pillar (sub pillar) has no nodes you can reach until the node that opens it is taken; its
    //     whole column renders dimmed with its opener named, rather than being hidden, so the player can see what
    //     they are working towards.
    //
    // Kept from the list version this replaces: the panel/backdrop/swoop/navbar shell, the measured margins
    // (M = 16, header at MenuNavbar.Height + 8, content at +40), and the rule that Accent is spent on exactly one
    // number per screen -- here, as before, the XP you have to spend.
    public partial class SkillsUI : CanvasLayer
    {
        public PlayerController Player;
        public SDG.Unturned.PlayerSkills SkillsSource;   // optional direct skills (render harness); else Player.Skills

        const float M = 16f;
        // Sized for a full-screen chart, not a corner panel -- the same lesson the map furniture just taught.
        const float NodeW = 244f, NodeH = 82f;       // a node box
        const float ColGap = 48f, RowGap = 56f;      // between columns, between tiers
        const float HeadH = 44f;                     // a pillar's own header box
        const float BandGap = 72f;                   // the gap that separates the main band from the advanced band
        const float DetailW = 380f;                  // the pane on the right
        const float XpW = 200f;

        Control _root, _slide, _clip, _canvas;
        Panel _panel, _detailBg;
        Label _header, _xp, _detailTitle, _detailBlurb, _detailReq, _detailGrant, _detailWhy;
        Button _takeBtn;
        MenuSwoop _swoop;
        MenuNavbar _navbar;
        bool _open;
        public bool IsOpen => _open;

        readonly SkillTree _tree = SkillTree.Placeholder();
        readonly SkillProgress _localProgress = new();   // harness/no-player fallback; the player owns the real one
        SkillProgress Prog => Player?.SkillTree ?? _localProgress;

        string _selected;
        Vector2 _pan;
        bool _dragging;

        // Node boxes and pillar heads, kept so Layout can place them and Refresh can recolour them.
        readonly System.Collections.Generic.Dictionary<string, (Panel bg, Label title, Label cost, Button hit)> _nodeUi = new();
        readonly System.Collections.Generic.Dictionary<string, (Panel bg, Label title)> _pillarUi = new();
        readonly System.Collections.Generic.Dictionary<string, Vector2> _nodePos = new();   // canvas-space top-left
        readonly System.Collections.Generic.Dictionary<string, Vector2> _pillarPos = new();

        static Color NodeTaken => new(0.30f, 0.42f, 0.30f, 0.98f);
        static Color NodeReady => new(0.24f, 0.28f, 0.34f, 0.98f);
        static Color NodeLocked => new(0.16f, 0.16f, 0.17f, 0.94f);
        static Color PillarC => new(0.20f, 0.26f, 0.33f, 0.98f);
        static Color PillarShut => new(0.15f, 0.15f, 0.17f, 0.92f);

        public override void _Ready()
        {
            Layer = 11;
            Visible = false;
            _root = new Control();
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(_root);
            var dim = new ColorRect();   // the same frosted-glass backdrop as the inventory/crafting screens
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = InventoryUI.BACKDROP_BLUR } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.MouseFilter = Control.MouseFilterEnum.Ignore;
            _root.AddChild(dim);
            _slide = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _slide.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_slide);
            // Ignore, not the Panel default STOP -- this is chrome, and a screen-sized panel that takes clicks is
            // the "translucent overlay that eats clicks" bug all over again.
            _panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            UITheme.Panel(_panel);
            _slide.AddChild(_panel);
            _swoop = MenuSwoop.Attach(this, _root, _slide, dim);

            _header = UITheme.Label(new Label(), UITheme.FontBody, UITheme.TextDim);
            _panel.AddChild(_header);
            _xp = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, UITheme.FontHeading, UITheme.Accent);
            _panel.AddChild(_xp);

            // The tree scrolls by DRAG rather than by scrollbars: it is a graph with links crossing columns, so it
            // is read by moving around it, and a pair of bars either side would cut the links at the edge.
            _clip = new Control { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Stop };
            _panel.AddChild(_clip);
            _clip.GuiInput += OnCanvasInput;
            _canvas = new TreeCanvas { Owner2 = this, MouseFilter = Control.MouseFilterEnum.Ignore };
            _clip.AddChild(_canvas);

            BuildGraphControls();
            BuildDetail();

            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Skills, t => Player?.ShowMenu(t), () => { Close(); Input.MouseMode = Input.MouseModeEnum.Captured; });

            GetViewport().SizeChanged += Layout;
            Layout();
        }

        void BuildGraphControls()
        {
            foreach (var p in _tree.Pillars)
            {
                var bg = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
                bg.AddThemeStyleboxOverride("panel", UITheme.Box(PillarC, UITheme.RadiusCell));
                _canvas.AddChild(bg);
                var t = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore }, UITheme.FontHeading, UITheme.Text);
                t.Text = p.Title;
                _canvas.AddChild(t);
                _pillarUi[p.Id] = (bg, t);
            }
            foreach (var n in _tree.Nodes)
            {
                var bg = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
                bg.AddThemeStyleboxOverride("panel", UITheme.Box(NodeLocked, UITheme.RadiusCell));
                _canvas.AddChild(bg);
                var title = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore }, UITheme.FontHeading, UITheme.Text);
                title.Text = n.Title;
                _canvas.AddChild(title);
                var cost = UITheme.Label(new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore }, UITheme.FontBody, UITheme.TextDim);
                _canvas.AddChild(cost);
                var hit = new Button { Flat = true, MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
                string id = n.Id;
                hit.Pressed += () => { _selected = id; RefreshDetail(); };
                _canvas.AddChild(hit);
                _nodeUi[n.Id] = (bg, title, cost, hit);
            }
        }

        void BuildDetail()
        {
            _detailBg = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            _detailBg.AddThemeStyleboxOverride("panel", UITheme.Box(new Color(0.13f, 0.13f, 0.14f, 0.96f), UITheme.RadiusCell));
            _panel.AddChild(_detailBg);
            _detailTitle = UITheme.Label(new Label(), UITheme.FontHeading, UITheme.Text);
            _panel.AddChild(_detailTitle);
            _detailBlurb = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }, UITheme.FontBody, UITheme.TextBody);
            _panel.AddChild(_detailBlurb);
            _detailReq = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }, UITheme.FontSmall, UITheme.TextDim);
            _panel.AddChild(_detailReq);
            _detailGrant = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }, UITheme.FontBody, UITheme.TextBody);
            _panel.AddChild(_detailGrant);
            _detailWhy = UITheme.Label(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }, UITheme.FontSmall, UITheme.TextDim);
            _panel.AddChild(_detailWhy);
            _takeBtn = new Button { Text = "Learn", MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            UITheme.Button(_takeBtn, true);
            _takeBtn.Pressed += TakeSelected;
            _panel.AddChild(_takeBtn);
        }

        /// <summary>Where every box sits in CANVAS space. Main pillars occupy the top band, advanced ones the band
        /// below it, so "advanced" is a place on the screen and not just a flag -- and the link from the node that
        /// opens a sub pillar crosses that gap, which is the thing worth being able to see.</summary>
        void PlaceGraph()
        {
            _nodePos.Clear(); _pillarPos.Clear();
            int mainTiers = 0, advTiers = 0;
            foreach (var n in _tree.Nodes)
            {
                var p = _tree.PillarOf(n.Pillar);
                if (p == null) continue;
                if (p.Advanced) advTiers = Mathf.Max(advTiers, n.Tier + 1);
                else mainTiers = Mathf.Max(mainTiers, n.Tier + 1);
            }
            float bandTop = HeadH + RowGap;                                   // first main tier
            float advHeadY = bandTop + mainTiers * (NodeH + RowGap) + BandGap; // advanced pillar heads
            float advTop = advHeadY + HeadH + RowGap;
            foreach (var p in _tree.Pillars)
            {
                float x = p.Column * (NodeW + ColGap);
                _pillarPos[p.Id] = new Vector2(x, p.Advanced ? advHeadY : 0f);
            }
            foreach (var n in _tree.Nodes)
            {
                var p = _tree.PillarOf(n.Pillar);
                if (p == null) continue;
                float x = p.Column * (NodeW + ColGap);
                float y = (p.Advanced ? advTop : bandTop) + n.Tier * (NodeH + RowGap);
                _nodePos[n.Id] = new Vector2(x, y);
            }
        }

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

            // KEEP OFF THE VITALS, the same rule the list version followed: the bars are the bottom-left of the
            // screen, and HUD.ContentBottom is measured in viewport coords, hence the +M / -M either side.
            float bottom = HUD.ContentBottom(vp, M + 16f, M + pw - DetailW - 32f, M + ph - M) - M;
            float treeW = Mathf.Max(200f, pw - DetailW - 48f);
            _clip.Position = new Vector2(16f, top);
            _clip.Size = new Vector2(treeW, Mathf.Max(140f, bottom - top));

            PlaceGraph();
            float cw = 0f, chh = 0f;
            foreach (var kv in _nodePos) { cw = Mathf.Max(cw, kv.Value.X + NodeW); chh = Mathf.Max(chh, kv.Value.Y + NodeH); }
            _canvas.Size = new Vector2(cw, chh);
            // CENTRE a graph narrower than the space it is in. Three placeholder pillars do not fill a 2.5k panel,
            // and a tree pinned to the left with a screen of nothing beside it reads as broken rather than sparse.
            // Once the content grows past the panel this does nothing and the drag takes over.
            if (cw < _clip.Size.X) _pan.X = (_clip.Size.X - cw) * 0.5f;
            ClampPan();
            _canvas.Position = _pan;
            foreach (var p in _tree.Pillars)
            {
                if (!_pillarPos.TryGetValue(p.Id, out var pos) || !_pillarUi.TryGetValue(p.Id, out var ui)) continue;
                ui.bg.Position = pos; ui.bg.Size = new Vector2(NodeW, HeadH);
                ui.title.Position = pos; ui.title.Size = new Vector2(NodeW, HeadH);
            }
            foreach (var n in _tree.Nodes)
            {
                if (!_nodePos.TryGetValue(n.Id, out var pos) || !_nodeUi.TryGetValue(n.Id, out var ui)) continue;
                ui.bg.Position = pos; ui.bg.Size = new Vector2(NodeW, NodeH);
                ui.title.Position = pos + new Vector2(0f, 14f); ui.title.Size = new Vector2(NodeW, 26f);
                ui.cost.Position = pos + new Vector2(0f, 46f); ui.cost.Size = new Vector2(NodeW, 22f);
                ui.hit.Position = pos; ui.hit.Size = new Vector2(NodeW, NodeH);
            }
            _canvas.QueueRedraw();

            float dx = pw - DetailW - 16f;
            _detailBg.Position = new Vector2(dx, top);
            _detailBg.Size = new Vector2(DetailW, Mathf.Max(140f, bottom - top));
            float ix = dx + 14f, iw = DetailW - 28f, y = top + 14f;
            _detailTitle.Position = new Vector2(ix, y); _detailTitle.Size = new Vector2(iw, 24f); y += 30f;
            _detailBlurb.Position = new Vector2(ix, y); _detailBlurb.Size = new Vector2(iw, 54f); y += 60f;
            _detailGrant.Position = new Vector2(ix, y); _detailGrant.Size = new Vector2(iw, 96f); y += 102f;
            _detailReq.Position = new Vector2(ix, y); _detailReq.Size = new Vector2(iw, 60f); y += 66f;
            _detailWhy.Position = new Vector2(ix, y); _detailWhy.Size = new Vector2(iw, 36f); y += 44f;
            // Under the text it belongs to, not pinned to the floor. The pane is as tall as the screen and the
            // content is a few lines, so a bottom-anchored button sits alone with 500 px of nothing above it.
            _takeBtn.Position = new Vector2(ix, y);
            _takeBtn.Size = new Vector2(iw, 34f);
        }

        void ClampPan()
        {
            if (_clip == null) return;
            // A graph SMALLER than the view is centred, not clamped to 0 -- clamping to [min,0] would drag it
            // back to the left edge the moment anything called this.
            float maxX = Mathf.Max(0f, _clip.Size.X - _canvas.Size.X);
            float maxY = Mathf.Max(0f, _clip.Size.Y - _canvas.Size.Y);
            float minX = Mathf.Min(0f, _clip.Size.X - _canvas.Size.X);
            float minY = Mathf.Min(0f, _clip.Size.Y - _canvas.Size.Y);
            _pan = new Vector2(Mathf.Clamp(_pan.X, minX, maxX), Mathf.Clamp(_pan.Y, minY, maxY));
        }

        void OnCanvasInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left) _dragging = mb.Pressed;
            else if (e is InputEventMouseMotion mm && _dragging)
            {
                _pan += mm.Relative;
                ClampPan();
                _canvas.Position = _pan;
            }
        }

        /// <summary>Repaint when the numbers behind the screen move. The list version polled this for MP (the
        /// server applies the upgrade and the levels change underneath you); the tree needs it for a plainer
        /// reason too -- XP is awarded while the menu is open, and the harness's own grant lands a frame or two
        /// AFTER Open() has already drawn. Dropping this in the rewrite is why the first render showed "0 XP" on
        /// a player who had just been given 500.</summary>
        public override void _Process(double delta)
        {
            if (!_open) return;
            if (_debugTakeIdx < _debugTake.Length) DebugTakeTick();
            var sk = SkillsSource ?? Player?.Skills;
            long sig = ((long)(sk?.experience ?? 0u) * 397L) ^ Prog.Count;
            if (sig != _lastSig) { _lastSig = sig; Refresh(); }
        }
        long _lastSig = -1;

        // UG_SKILLTAKE=id,id,... -- learn these nodes, in order, as soon as each becomes affordable. A render can
        // show the tree but cannot CLICK it, so without this the whole unlock path (prerequisite gating, the XP
        // spend, an advanced pillar opening, a two-parent node going live only once BOTH parents are in) ships on
        // the strength of a screenshot of its resting state. Applied from _Process rather than at build: the XP
        // grant it needs lands a frame or two after the screen is up.
        readonly string[] _debugTake = (System.Environment.GetEnvironmentVariable("UG_SKILLTAKE") ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        int _debugTakeIdx;

        void DebugTakeTick()
        {
            var sk = SkillsSource ?? Player?.Skills;
            if (sk == null) return;
            while (_debugTakeIdx < _debugTake.Length)
            {
                var n = _tree.NodeOf(_debugTake[_debugTakeIdx]);
                if (n == null) { GD.PrintErr($"[skilltake] no node '{_debugTake[_debugTakeIdx]}'"); _debugTakeIdx++; continue; }
                uint cost = Prog.Take(_tree, n, sk.experience);
                if (cost == 0u)
                {
                    Prog.CanTake(_tree, n, sk.experience, out string why);
                    GD.Print($"[skilltake] holding at '{n.Id}': {why}");
                    return;   // not yet -- try again next tick rather than skipping past it
                }
                sk.TrySpend(cost);
                _debugTakeIdx++;
                GD.Print($"[skilltake] learned '{n.Id}' for {cost} XP ({sk.experience} left)");
            }
        }

        public void Toggle() { if (_open) Close(); else Open(); }
        public void Open() { _open = true; Visible = true; if (_root != null) _root.Visible = true; Refresh(); _swoop?.In(); }
        public void Close() { _open = false; if (_swoop == null || !_swoop.Out()) Visible = false; }

        void TakeSelected()
        {
            var sk = SkillsSource ?? Player?.Skills;
            var n = _tree.NodeOf(_selected);
            if (sk == null || n == null) return;
            // Take() re-checks everything against the live pool, so a stale button cannot buy a node -- and it
            // returns the price rather than deducting, because the pool is not its to touch.
            uint cost = Prog.Take(_tree, n, sk.experience);
            if (cost == 0u) return;
            sk.TrySpend(cost);
            Refresh();
        }

        void Refresh()
        {
            var sk = SkillsSource ?? Player?.Skills;
            uint xp = sk?.experience ?? 0u;
            int taken = 0;
            foreach (var n in _tree.Nodes) if (Prog.Has(n.Id)) taken++;
            int ready = 0;
            foreach (var n in _tree.Nodes) if (Prog.CanTake(_tree, n, xp, out _)) ready++;
            _header.Text = $"SKILLS   ·   {taken} / {_tree.Nodes.Length} learned   ·   {ready} available now";
            _xp.Text = $"{xp} XP";

            foreach (var p in _tree.Pillars)
            {
                if (!_pillarUi.TryGetValue(p.Id, out var ui)) continue;
                bool open = Prog.PillarOpen(p);
                ui.bg.AddThemeStyleboxOverride("panel", UITheme.Box(open ? PillarC : PillarShut, UITheme.RadiusCell));
                ui.title.AddThemeColorOverride("font_color", open ? UITheme.Text : UITheme.TextDim);
            }
            foreach (var n in _tree.Nodes)
            {
                if (!_nodeUi.TryGetValue(n.Id, out var ui)) continue;
                bool has = Prog.Has(n.Id);
                bool can = !has && Prog.CanTake(_tree, n, xp, out _);
                ui.bg.AddThemeStyleboxOverride("panel", UITheme.Box(has ? NodeTaken : can ? NodeReady : NodeLocked, UITheme.RadiusCell,
                    can ? UITheme.Accent : null, can ? 2 : 0));
                ui.title.AddThemeColorOverride("font_color", has || can ? UITheme.Text : UITheme.TextDim);
                ui.cost.Text = has ? "learned" : $"{n.Cost} XP";
                ui.cost.AddThemeColorOverride("font_color", has ? UITheme.TextDim : can ? UITheme.Accent : UITheme.TextDim);
            }
            RefreshDetail();
            _canvas?.QueueRedraw();
        }

        void RefreshDetail()
        {
            var sk = SkillsSource ?? Player?.Skills;
            uint xp = sk?.experience ?? 0u;
            var n = _tree.NodeOf(_selected);
            if (n == null)
            {
                _detailTitle.Text = "Select a node";
                _detailBlurb.Text = "Pillars run across the top. Advanced pillars sit below and open once the node that unlocks them is learned.";
                _detailGrant.Text = ""; _detailReq.Text = ""; _detailWhy.Text = "";
                _takeBtn.Disabled = true; _takeBtn.Text = "Learn";
                return;
            }
            _detailTitle.Text = n.Title;
            _detailBlurb.Text = n.Blurb ?? "";

            var g = new System.Text.StringBuilder("GRANTS\n");
            foreach (var gr in n.Grants) g.Append("  • ").Append(gr.Describe()).Append('\n');
            if (n.Grants.Length == 0) g.Append("  • —\n");
            _detailGrant.Text = g.ToString();

            var r = new System.Text.StringBuilder("REQUIRES\n");
            if (n.Requires.Length == 0) r.Append("  • nothing\n");
            foreach (var req in n.Requires)
                r.Append(Prog.Has(req) ? "  ✓ " : "  ✗ ").Append(_tree.NodeOf(req)?.Title ?? req).Append('\n');
            _detailReq.Text = r.ToString();

            bool has = Prog.Has(n.Id);
            bool can = Prog.CanTake(_tree, n, xp, out string why);
            _detailWhy.Text = has ? "" : can ? "" : why ?? "";
            _takeBtn.Disabled = !can;
            _takeBtn.Text = has ? "Learned" : $"Learn  ·  {n.Cost} XP";
        }

        /// <summary>Draws the links. A separate Control rather than lines parented to the nodes, because a link is
        /// a relationship between TWO boxes and belongs to neither -- and because the requirement edges cross
        /// columns, so there is no parent that contains both ends.</summary>
        partial class TreeCanvas : Control
        {
            public SkillsUI Owner2;

            public override void _Draw()
            {
                var o = Owner2;
                if (o?._tree == null) return;
                foreach (var n in o._tree.Nodes)
                {
                    if (!o._nodePos.TryGetValue(n.Id, out var to)) continue;
                    var toPt = to + new Vector2(NodeW * 0.5f, 0f);
                    // A node with no requirements hangs off its own pillar head; one with requirements gets a line
                    // per requirement, which is what makes a multi-parent node legible as multi-parent.
                    if (n.Requires.Length == 0)
                    {
                        if (o._pillarPos.TryGetValue(n.Pillar, out var ph))
                            Link(ph + new Vector2(NodeW * 0.5f, HeadH), toPt, o.Prog.PillarOpen(o._tree.PillarOf(n.Pillar)));
                        continue;
                    }
                    foreach (var req in n.Requires)
                        if (o._nodePos.TryGetValue(req, out var fr))
                            Link(fr + new Vector2(NodeW * 0.5f, NodeH), toPt, o.Prog.Has(req));
                }
                // ...and the link from the node that OPENS an advanced pillar down to that pillar's head, which is
                // the one edge that is not node-to-node.
                foreach (var p in o._tree.Pillars)
                {
                    if (!p.Advanced || !o._pillarPos.TryGetValue(p.Id, out var php)) continue;
                    foreach (var req in p.Requires)
                        if (o._nodePos.TryGetValue(req, out var fr))
                            Link(fr + new Vector2(NodeW * 0.5f, NodeH), php + new Vector2(NodeW * 0.5f, 0f), o.Prog.Has(req));
                }
            }

            /// <summary>Elbowed rather than straight: a diagonal across two columns reads as a scribble over the
            /// boxes it passes, where a vertical-across-vertical stays legible however far it reaches.</summary>
            void Link(Vector2 a, Vector2 b, bool live)
            {
                var c = live ? UITheme.Accent with { A = 0.85f } : new Color(1f, 1f, 1f, 0.16f);
                float w = live ? 2.5f : 1.5f;
                float mid = (a.Y + b.Y) * 0.5f;
                DrawLine(a, new Vector2(a.X, mid), c, w);
                DrawLine(new Vector2(a.X, mid), new Vector2(b.X, mid), c, w);
                DrawLine(new Vector2(b.X, mid), b, c, w);
            }
        }
    }
}
