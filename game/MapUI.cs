using Godot;

namespace UnturnedGodot
{
    // Full-screen map (press M, Esc/M closes). Shows the map's real Map.png with the town LOCATION nodes plotted
    // and a rotating arrow for the local player's position + facing.
    //
    // Source transform — PlayerDashboardInformationUI.ProjectWorldPositionToMap (level-size fallback, PEI has
    // no cartography volume):   nx = worldX/levelSize + 0.5 ;  ny = 0.5 - worldZ_unity/levelSize
    // PEI = MEDIUM (Level.size 2048, border 64) -> levelSize = 2048 - 64*2 = 1920.
    // Our world is Godot space (godotZ = -unityZ), so ny = 0.5 + godotZ/1920. The facing arrow uses the same
    // rule as the source (localPlayerImage.RotationAngle = player yaw), computed here from the look forward.
    //
    // INTERACTIVE (strawberry 2026-09-08: "rework the map ui to be a lot more in depth. panning with lmb,
    // zooming, placing markers with rmb, all preserving through opens/closes. add a player list to this ui too,
    // on the right, lists all players on the server"). Pan/zoom/markers live in instance fields that Open() does
    // not touch, which is what "preserving through opens/closes" means here: the map is a thing you leave where
    // you had it, not a screen that resets every time you glance at it.
    public partial class MapUI : CanvasLayer
    {
        public PlayerController Player;
        // MAP-AWARE (was PEI-hardcoded): Main sets MapFolder when it resolves the map, and the image / level-size /
        // label all follow. levelSize = ELevelSize SIZE - 2*BORDER (source Level.cs). BOTH shipped maps are MEDIUM
        // (2048-128=1920): PEI, and Washington -- Washington has a 4096 LANDSCAPE (16 tiles) but its playable/map
        // level is MEDIUM, confirmed by aligning the town nodes to Map.png (a LARGE 3968 scaled them 2x too small).
        // The town dots come from MapNodes, which is already map-aware.
        public static string MapFolder = "PEI";
        static (string img, float size, string label) Info() => MapFolder switch
        {
            "Washington" => ("washington_map.png", 1920f, "Washington"),   // MEDIUM level (2048-2*64) despite a 4096 landscape -- verified by aligning the town nodes to the Map.png
            "Yukon"      => ("yukon_map.png",      1920f, "Yukon"),         // MEDIUM level -- town nodes span ~+-830 (Mount Logan..Off Limits), fits 1920; verify M-map alignment in-render
            _            => ("pei_map.png",        1920f, "PEI"),
        };

        public const int MapLayer = 90;                  // under the F1 console (100), above the inventory family (11)
        public const float ZoomMin = 1f, ZoomMax = 8f;   // 1 = the whole island fits the panel
        const float PlayersW = 264f;                     // right-hand roster column
        const float MarkerHit = 14f;                     // px: RMB this close to a marker removes it instead of stacking another

        Control _root;
        Control _slide;      // swoop offset wrapper -- owns nothing else
        MenuSwoop _swoop;
        Control _clip;       // the map's window; ClipContents so a panned/zoomed map cannot spill over the chrome
        TextureRect _map;    // Map.png, square, sized base*zoom and moved by _pan inside _clip
        Polygon2D _arrow;    // local player marker (position + facing)
        Label _coord;
        Panel _panel;          // the screen frame, matching the inventory/crafting panel
        Panel _playersPanel;
        VBoxContainer _playersList;
        Label _playersHead;
        readonly System.Collections.Generic.List<(Vector2 norm, Control dot, Label lbl)> _towns = new();

        // ---- STATE THAT SURVIVES A CLOSE. Instance fields, not statics: they should outlive an open/close pair
        // but NOT outlive the world, and MapUI is built once per world.
        float _zoom = 1f;
        Vector2 _pan;                 // top-left of the map image relative to the clip, in pixels
        float _baseSize = 1f;         // the un-zoomed square edge, from the last Layout
        readonly System.Collections.Generic.List<Marker> _markers = new();
        bool _dragging;
        int _markerSeq;

        sealed class Marker
        {
            public Vector2 Norm;      // map-normalised 0..1, so it is zoom/pan independent
            public Polygon2D Pin;
            public Label Tag;
        }

        // Other players plotted on the map -- rebuilt only when the roster changes, not every frame. Their
        // NORMALISED positions are kept alongside the nodes so a pan or a zoom can re-place them immediately;
        // without that they sit at the old transform until the next roster tick and visibly lag the map.
        readonly System.Collections.Generic.List<(Control dot, Label lbl)> _peerDots = new();
        readonly System.Collections.Generic.List<Vector2> _peerNorms = new();

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Layer = MapLayer;   // under the F1 console (100)
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };   // eat clicks so the map doesn't shoot the gun underneath
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);

            var dim = new ColorRect { MouseFilter = Control.MouseFilterEnum.Stop };   // frosted-glass backdrop, same as the other menu screens
            dim.Material = new ShaderMaterial { Shader = new Shader { Code = InventoryUI.BACKDROP_BLUR } };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);
            Current = this;

            _slide = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _slide.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(_slide);

            // THE SAME PANEL FRAME THE OTHER TABS HAVE (strawberry 2026-09-08: "fix the formatting of the skills
            // page and information page to more closely match the style of the inv and crafting menus"). Added
            // FIRST so it draws behind the map and the roster. UITheme.Panel is Box(Bg, RadiusPanel=6), which is
            // byte-for-byte what CraftingMenu does with Box(_panel, UITheme.Bg, 6).
            _panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            UITheme.Panel(_panel);
            _slide.AddChild(_panel);

            _clip = new Control { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            _slide.AddChild(_clip);

            _map = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            var tex = LoadMap();
            if (tex != null) _map.Texture = tex;
            _clip.AddChild(_map);

            foreach (var (name, pos) in MapNodes.Locations)
            {
                var dot = new ColorRect { Color = UITheme.Accent, Size = new Vector2(5, 5), MouseFilter = Control.MouseFilterEnum.Ignore };
                _map.AddChild(dot);
                var lbl = new Label { Text = name, MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
                lbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
                lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
                lbl.AddThemeConstantOverride("outline_size", 4);
                _map.AddChild(lbl);
                _towns.Add((WorldToNorm(pos), dot, lbl));
            }

            _arrow = new Polygon2D { Color = new Color(0.25f, 0.9f, 1f) };
            _arrow.Polygon = new Vector2[] { new(0, -11), new(7, 8), new(0, 3), new(-7, 8) };   // points up (north) at rotation 0
            _map.AddChild(_arrow);

            BuildPlayersPanel();

            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Information, t => Player?.ShowMenu(t), () => Close());   // the Information tab of the unified menu hosts the map
            // Header, matched to CraftingMenu's: FontBody in TextDim. The outline it used to carry was for sitting
            // ON the map image; it lives on the panel now, where an outline would just look heavy next to the
            // crafting screen's header.
            _coord = new Label();
            _coord.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _coord.AddThemeColorOverride("font_color", UITheme.TextDim);
            _slide.AddChild(_coord);

            _swoop = MenuSwoop.Attach(this, _root, _slide);
            // Give the mouse back only once the panel has actually gone; doing it in Close() hides the cursor
            // while the map is still fading, which reads as the screen freezing rather than closing.
            _swoop.Closed += () => { if (_wantCapture) Input.MouseMode = Input.MouseModeEnum.Captured; };

            GetViewport().SizeChanged += Layout;
            Layout();

            if (System.Environment.GetEnvironmentVariable("UG_MAPOPEN") == "1")   // debug: open the M-map at start + log node projections so a render can verify alignment
            {
                _root.Visible = true;
                _swoop.Snap(true);
                // UG_MAPVIEW=<zoom>[,<panx>,<pany>] and UG_MAPMARKS=nx:ny[,nx:ny...] drive the interactive state from
                // outside, so a single still can SHOW a zoomed, panned, pinned map. Pan/zoom/markers are the whole
                // of this feature and none of them is visible in a screenshot of the default view.
                var mv = System.Environment.GetEnvironmentVariable("UG_MAPVIEW");
                if (!string.IsNullOrEmpty(mv))
                {
                    var f = mv.Split(',');
                    if (f.Length >= 1 && float.TryParse(f[0], out float z)) _zoom = z;
                    if (f.Length >= 3 && float.TryParse(f[1], out float px) && float.TryParse(f[2], out float py)) _pan = new Vector2(px, py);
                    ApplyView();
                }
                var mk = System.Environment.GetEnvironmentVariable("UG_MAPMARKS");
                if (!string.IsNullOrEmpty(mk))
                    foreach (var one in mk.Split(','))
                    {
                        var xy = one.Split(':');
                        if (xy.Length == 2 && float.TryParse(xy[0], out float nx) && float.TryParse(xy[1], out float ny))
                            AddMarker(new Vector2(nx, ny));
                    }
                GD.Print($"[mapdbg] folder={MapFolder} levelSize={Info().size} nodes={MapNodes.Locations.Count} zoom={_zoom:0.00} pan=({_pan.X:0},{_pan.Y:0}) marks={_markers.Count} base={_baseSize:0} clip={_clip.Position}+{_clip.Size} mapPos={_map.Position} mapSize={_map.Size}");
                foreach (var m in _markers) GD.Print($"[mapmark] norm=({m.Norm.X:0.000},{m.Norm.Y:0.000}) pin={m.Pin.Position} vis={m.Pin.Visible}");
                foreach (var (nm, pos) in MapNodes.Locations)
                {
                    var n = WorldToNorm(pos);
                    GD.Print($"[mapnode] {nm} world=({pos.X:0},{pos.Z:0}) norm=({n.X:0.000},{n.Y:0.000})");
                }
            }
        }

        void BuildPlayersPanel()
        {
            // OPAQUE, like crafting's right-hand detail pane -- Box(_detail, UITheme.BgSolid) at the default
            // RadiusCell. It was translucent Bg, which over a blurred forest left the roster's names sitting on
            // treetops; the analogous element on the crafting screen is a solid dark slab and reads as one.
            _playersPanel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            UITheme.Panel(_playersPanel, true, UITheme.RadiusCell);
            _slide.AddChild(_playersPanel);

            // FontBody/TextDim, crafting's treatment for a section label ("CRAFTING QUEUE"). It was Accent, and
            // Accent is already spent on this screen -- every town dot and every marker pin is that yellow, so a
            // yellow column title is the fourth thing claiming to be the important one.
            _playersHead = new Label { Position = new Vector2(14, 10), MouseFilter = Control.MouseFilterEnum.Ignore };
            _playersHead.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _playersHead.AddThemeColorOverride("font_color", UITheme.TextDim);
            _playersPanel.AddChild(_playersHead);

            var scroll = new ScrollContainer { Position = new Vector2(8, 40), MouseFilter = Control.MouseFilterEnum.Ignore, Name = "RosterScroll" };
            scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            scroll.OffsetLeft = 8; scroll.OffsetTop = 40; scroll.OffsetRight = -8; scroll.OffsetBottom = -8;
            _playersPanel.AddChild(scroll);
            _playersList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _playersList.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            scroll.AddChild(_playersList);
        }

        void Layout()
        {
            // CraftingMenu.Layout's numbers, measured rather than eyeballed: outer margin M=16, header at
            // (16, navbar+8) sized (pw-32, 24), content starting at navbar+40. This page used 12 / navbar+36 and
            // hung its label off the map's own left edge, so it sat a few pixels out from every other tab.
            var vp = GetViewport().GetVisibleRect().Size;
            const float M = 16f, Gutter = 16f;
            const float barH = MenuNavbar.Height;
            // +M BECAUSE THESE CONTROLS ARE SCREEN-SPACE. Crafting's header and columns are children of its
            // panel, so its (16, barH+8) and barH+40 are PANEL-relative and land at (32, barH+24) and barH+56 on
            // screen. This page hangs everything off _slide instead, so the same literals put it a whole outer
            // margin higher -- 8 px under the navbar rather than 24. Copying the numbers without the parent is
            // how a screen ends up "nearly" aligned, which is the failure mode this task is about.
            const float top = M + barH + 40f;
            float pw = vp.X - 2f * M, ph = vp.Y - 2f * M;

            _panel.Position = new Vector2(M, M);
            _panel.Size = new Vector2(pw, ph);
            _coord.Position = new Vector2(M + 16f, M + barH + 8f);
            _coord.Size = new Vector2(pw - 32f, 24f);

            // Roster hard against the panel's inner right edge, map filling what is left, one gutter between.
            float contentBottom = vp.Y - M - M;
            float rosterX = vp.X - M - M - PlayersW;
            float regionX = M + M, regionW = rosterX - Gutter - regionX;
            float s = Mathf.Min(regionW, contentBottom - top);
            if (s < 64f) s = Mathf.Max(64f, contentBottom - top);   // very narrow window: keep a usable map rather than a sliver
            _baseSize = s;

            // CENTRED in what is left, not jammed against the left margin. The map window is SQUARE and the
            // space beside the roster is not, so on a 16:9 screen the square is height-capped and roughly 500 px
            // narrower than its region -- left-aligned, all of that slack piles up as one lopsided hole between
            // the island and the roster. Splitting it puts a matching gutter on both sides instead.
            _clip.Position = new Vector2(regionX + Mathf.Max(0f, (regionW - s) * 0.5f), top);
            _clip.Size = new Vector2(s, s);

            _playersPanel.Position = new Vector2(rosterX, top);
            _playersPanel.Size = new Vector2(PlayersW, Mathf.Max(120f, contentBottom - top));
            ApplyView();
        }

        /// <summary>Push _zoom/_pan onto the map and everything pinned to it. Called by Layout, by a pan, by a
        /// zoom -- one place, so the town dots, the markers, the peers and the player arrow can never end up
        /// positioned against a different transform than the image they sit on.</summary>
        void ApplyView()
        {
            _zoom = Mathf.Clamp(_zoom, ZoomMin, ZoomMax);
            float s = _baseSize * _zoom;
            ClampPan(s);
            _map.Position = _pan;
            _map.Size = new Vector2(s, s);
            foreach (var (norm, dot, lbl) in _towns)
            {
                dot.Position = norm * s - new Vector2(2.5f, 2.5f);
                lbl.Position = norm * s + new Vector2(5f, -7f);
            }
            foreach (var m in _markers) PlaceMarker(m, s);
            PlacePeers(s);
        }

        void PlacePeers(float s)
        {
            for (int i = 0; i < _peerDots.Count; i++)
            {
                bool used = i < _peerNorms.Count;
                _peerDots[i].dot.Visible = used;
                _peerDots[i].lbl.Visible = used;
                if (!used) continue;
                _peerDots[i].dot.Position = _peerNorms[i] * s - new Vector2(3.5f, 3.5f);
                _peerDots[i].lbl.Position = _peerNorms[i] * s + new Vector2(7f, -8f);
            }
        }

        /// <summary>Keep the image overlapping its window. Fully unclamped panning loses the map off-screen with
        /// no way back except reopening, and at zoom 1 the image exactly fills the window so the only valid pan
        /// is zero -- both fall out of the same two lines.</summary>
        void ClampPan(float s)
        {
            float minX = Mathf.Min(0f, _clip.Size.X - s), minY = Mathf.Min(0f, _clip.Size.Y - s);
            _pan = new Vector2(Mathf.Clamp(_pan.X, minX, 0f), Mathf.Clamp(_pan.Y, minY, 0f));
        }

        void PlaceMarker(Marker m, float s)
        {
            m.Pin.Position = m.Norm * s;
            m.Tag.Position = m.Norm * s + new Vector2(7f, -8f);
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (!_root.Visible || Player == null) return;
            float s = _baseSize * _zoom;
            var pos = Player.GlobalPosition;
            _arrow.Position = WorldToNorm(pos) * s;
            _arrow.Rotation = Player.MapFacingAngle();
            _coord.Text = $"{Info().label}    X {pos.X:0}  Z {pos.Z:0}    zoom {_zoom:0.0}x    LMB drag  ·  wheel zoom  ·  RMB marker  ·  {Keybinds.Get(GameAction.Map).Label}/Esc close";
            _rosterTimer -= (float)delta;
            if (_rosterTimer <= 0f) { _rosterTimer = 0.4f; RefreshRoster(s); }
        }
        float _rosterTimer;

        /// <summary>The roster column, plus a dot on the map for everyone in it. Rebuilt on a 0.4 s tick rather
        /// than per frame: it is a list of names, and nobody joins a server sixty times a second.</summary>
        void RefreshRoster(float s)
        {
            var rows = new System.Collections.Generic.List<(string name, Vector3 pos, bool me)>();
            var self = Player != null ? Player.GlobalPosition : Vector3.Zero;
            rows.Add((PlayerProfile.Name, self, true));
            if (RemotePlayers.Current != null && GodotObject.IsInstanceValid(RemotePlayers.Current))
                foreach (var (_, name, p) in RemotePlayers.Current.Roster()) rows.Add((name, p, false));

            _playersHead.Text = rows.Count == 1 ? "PLAYERS  (1)" : $"PLAYERS  ({rows.Count})";

            // Rebuild the label column only when the set of names actually changed -- otherwise every 0.4 s we
            // would throw away and rebuild a pile of Controls for no visible difference.
            string sig = "";
            foreach (var r in rows) sig += r.name + "|";
            if (sig != _rosterSig)
            {
                _rosterSig = sig;
                foreach (var c in _playersList.GetChildren()) c.QueueFree();
                foreach (var r in rows)
                {
                    var l = new Label { Text = r.me ? r.name + "  (you)" : r.name, MouseFilter = Control.MouseFilterEnum.Ignore };
                    l.AddThemeFontSizeOverride("font_size", UITheme.FontBody);   // a roster is read at a glance; FontSmall is for map labels
                    l.AddThemeColorOverride("font_color", r.me ? new Color(0.25f, 0.9f, 1f) : new Color(0.92f, 0.92f, 0.92f));
                    _playersList.AddChild(l);
                }
            }

            // one dot per OTHER player, pooled
            int need = rows.Count - 1;
            while (_peerDots.Count < need)
            {
                var d = new ColorRect { Color = new Color(1f, 0.55f, 0.2f), Size = new Vector2(7, 7), MouseFilter = Control.MouseFilterEnum.Ignore };
                _map.AddChild(d);
                var t = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
                t.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
                t.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.5f));
                t.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
                t.AddThemeConstantOverride("outline_size", 4);
                _map.AddChild(t);
                _peerDots.Add((d, t));
            }
            _peerNorms.Clear();
            for (int i = 0; i < need; i++)
            {
                _peerNorms.Add(WorldToNorm(rows[i + 1].pos));
                _peerDots[i].lbl.Text = rows[i + 1].name;
            }
            PlacePeers(s);
        }
        string _rosterSig = "";

        public override void _Input(InputEvent e)
        {
            // The M key is routed by PlayerController.ShowMenu (unified menu) so opening the map closes the other tabs; Esc closes here.
            if (!IsOpen) return;
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); return; }

            if (e is InputEventMouseButton mb)
            {
                bool over = _clip.GetGlobalRect().HasPoint(mb.GlobalPosition);
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed && over) { _dragging = true; GetViewport().SetInputAsHandled(); }
                    else if (!mb.Pressed && _dragging) { _dragging = false; GetViewport().SetInputAsHandled(); }
                }
                else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && over)
                {
                    ToggleMarkerAt(mb.GlobalPosition);
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.Pressed && over && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
                {
                    ZoomAt(mb.GlobalPosition, mb.ButtonIndex == MouseButton.WheelUp ? 1.18f : 1f / 1.18f);
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (e is InputEventMouseMotion mm && _dragging)
            {
                _pan += mm.Relative;
                ApplyView();
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>Zoom about the cursor: whatever bit of the island is under the pointer stays under it. Zooming
        /// about the panel's centre instead means aiming at something and then hunting for it again afterwards.</summary>
        void ZoomAt(Vector2 globalMouse, float factor)
        {
            Vector2 local = globalMouse - _clip.GetGlobalRect().Position;
            float before = _baseSize * _zoom;
            Vector2 norm = before > 0.01f ? (local - _pan) / before : Vector2.Zero;
            _zoom = Mathf.Clamp(_zoom * factor, ZoomMin, ZoomMax);
            _pan = local - norm * (_baseSize * _zoom);
            ApplyView();
        }

        void ToggleMarkerAt(Vector2 globalMouse)
        {
            float s = _baseSize * _zoom;
            Vector2 local = globalMouse - _clip.GetGlobalRect().Position - _pan;
            if (s <= 0.01f) return;
            Vector2 norm = local / s;
            if (norm.X < 0f || norm.X > 1f || norm.Y < 0f || norm.Y > 1f) return;

            // RMB on top of one you already placed REMOVES it -- otherwise a mis-click is permanent and the map
            // silts up with pins nobody can get rid of.
            for (int i = 0; i < _markers.Count; i++)
                if ((_markers[i].Norm * s - local).Length() <= MarkerHit)
                {
                    _markers[i].Pin.QueueFree(); _markers[i].Tag.QueueFree();
                    _markers.RemoveAt(i);
                    return;
                }

            AddMarker(norm);
        }

        void AddMarker(Vector2 norm)
        {
            float s = _baseSize * _zoom;
            var pin = new Polygon2D { Color = new Color(1f, 0.85f, 0.2f) };
            pin.Polygon = new Vector2[] { new(0, 0), new(-6, -14), new(0, -19), new(6, -14) };   // a teardrop pin whose TIP is the marked spot
            _map.AddChild(pin);
            var tag = new Label { Text = $"M{++_markerSeq}", MouseFilter = Control.MouseFilterEnum.Ignore };
            tag.AddThemeFontSizeOverride("font_size", UITheme.FontSmall);
            tag.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.5f));
            tag.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            tag.AddThemeConstantOverride("outline_size", 4);
            _map.AddChild(tag);
            var m = new Marker { Norm = norm, Pin = pin, Tag = tag };
            _markers.Add(m);
            PlaceMarker(m, s);
        }

        public static MapUI Current;   // the live map screen (one per world); PlayerController routes the Information tab here
        MenuNavbar _navbar;
        bool _wantCapture = true;
        public bool IsOpen => _root != null && _root.Visible && !_closing;
        bool _closing;
        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            _closing = false;
            _root.Visible = true;
            Visible = true;
            Layer = MapLayer;
            Layout();               // keeps _zoom/_pan/_markers -- ApplyView re-applies them to the new size
            _navbar?.SetActive(MenuNavbar.Tab.Information);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _swoop?.In();
        }

        public void Close(bool captureMouse = true)
        {
            if (_root == null || _closing) return;
            _closing = true;
            _dragging = false;
            _wantCapture = captureMouse;
            // DUCK UNDER THE OTHER SCREENS WHILE FADING. Switching tabs closes this and opens the inventory in the
            // same frame, and the map lives on layer 90 against their 11 -- so for the length of the swoop its
            // full-screen frosted backdrop would sit ON TOP of the screen you just asked for. Dropping below them
            // for those few frames makes the hand-off read as a cross-fade instead of a flash of the old screen.
            Layer = MapLayer - 81;
            if (_swoop == null || !_swoop.Out()) { _root.Visible = false; if (captureMouse) Input.MouseMode = Input.MouseModeEnum.Captured; }
        }

        public override void _ExitTree() { if (Current == this) Current = null; }

        static Vector2 WorldToNorm(Vector3 p) { float ls = Info().size; return new Vector2(p.X / ls + 0.5f, 0.5f + p.Z / ls); }

        static Texture2D LoadMap()
        {
            string p = ProjectSettings.GlobalizePath("res://content/" + Info().img);
            if (!System.IO.File.Exists(p)) { GD.Print($"[map] missing content/{Info().img}"); return null; }
            var img = ContentProvider.LoadImage(p);
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
    }
}
