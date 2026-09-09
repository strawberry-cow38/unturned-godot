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

        /// <summary>The level extent the map image is assumed to cover, metres. WorldToNorm divides by it, so the
        /// BAKE has to frame exactly this or every dot on the map is wrong by the ratio -- which is why the baker
        /// reads it from here rather than carrying its own copy.</summary>
        public static float LevelSize => Info().size;

        /// <summary>Our own top-down render of the world, if one has been baked (--bakemap). Sits beside the
        /// retail image rather than replacing it, so a bad bake is one file deletion away from undone.</summary>
        public static string BakedImageName => System.IO.Path.GetFileNameWithoutExtension(Info().img) + "_baked.png";

        /// <summary>The bake writes the island twice: once whole (BakedImageName, the overview) and once as a
        /// BakedChunks x BakedChunks grid of tiles at the SAME pixel count each, so the detail available when you
        /// zoom in is BakedChunks times finer without any single texture being huge. Changing this number means
        /// re-baking -- --bakemap reads it too, so the writer and the reader cannot disagree about the grid.</summary>
        public const int BakedChunks = 4;

        /// <summary>Zoom at which the tiles take over from the overview. 1 = the whole island fits the panel, so
        /// the overview is already at its best there; the swap happens once you are magnifying past what it has.</summary>
        const float ChunkZoom = 2f;

        /// <summary>JPEG, not PNG, and the reason is history rather than disk (tinyclaw supplied the number: the
        /// pack is 332 MiB, PNGs do not delta against their previous version, and a re-bake is a fresh copy that
        /// stays forever). The 16 tiles are 67.3 MB as PNG and 10.2 MB at this quality -- 6.6x -- and the check
        /// that mattered was NOT the byte count: JPEG rings on high-contrast hard edges, which is precisely what
        /// a road is. Compared at 1:1 on a junction with lane dashes, a yellow centre line and zebra crossings,
        /// q0.90 is indistinguishable from the PNG. The overview stays PNG: it is one small file and it is the
        /// base layer every tile is drawn on top of.</summary>
        public static string BakedChunkName(int row, int col)
            => System.IO.Path.GetFileNameWithoutExtension(Info().img) + $"_baked_{row}_{col}.jpg";

        // THE SAME LAYER AS ITS SIBLING TABS, so the vitals sit over it exactly as they sit over the bag
        // (strawberry 2026-09-08: "cant see vitals on map"). It was 90, above the inventory family and above the
        // vitals' own layer 12 -- and this screen's backdrop is the opaque frosted blur, so it painted straight
        // over the bars. The four dashboard tabs are mutually exclusive (ShowMenu closes the other three), so
        // there was never anything for 90 to be above; the tab hand-off it was protecting is handled by DuckLayer
        // below instead. Raising the VITALS over 90 would have been the other fix and is worse: it would also put
        // them over the pause menu at 60, which does not hide the HUD.
        public const int MapLayer = 11;                  // the inventory/craft/skills family; the vitals (12) draw over all four
        const int DuckLayer = 9;                         // ...and under the HUD (10) while a close fades out
        public const float ZoomMin = 1f, ZoomMax = 8f;   // 1 = the whole island fits the panel
        const float RosterMinW = 540f;                   // NAME + POSITION + DISTANCE and their gutters: the map never squeezes below this
        const float RosterRowH = 30f;                    // the crafting category row / skills row height
        const float RosterNameW = 220f, RosterPosW = 190f, RosterDistW = 100f;   // the roster's three columns
        const float MarkerHit = 14f;                     // px: RMB this close to a marker removes it instead of stacking another

        Control _root;
        Control _slide;      // swoop offset wrapper -- owns nothing else
        MenuSwoop _swoop;
        Control _clip;       // the map's window; ClipContents so a panned/zoomed map cannot spill over the chrome
        TextureRect _map;    // Map.png, square, sized base*zoom and moved by _pan inside _clip
        Polygon2D _arrow;    // local player marker (position + facing)
        Label _coord; Panel _coordBar;
        Panel _panel;          // the screen frame, matching the inventory/crafting panel
        Panel _playersPanel;
        Panel _rosterHead;
        ScrollContainer _rosterScroll;
        readonly System.Collections.Generic.List<(Label pos, Label dist)> _rosterCells = new();
        static Color RosterRowC => new(0.22f, 0.22f, 0.23f, 0.98f);   // CraftingMenu's TileC, the same row face the skills page uses
        VBoxContainer _playersList;
        Label _playersHead;
        readonly System.Collections.Generic.List<(Vector2 norm, Control dot, Control lbl)> _towns = new();

        // ---- STATE THAT SURVIVES A CLOSE. Instance fields, not statics: they should outlive an open/close pair
        // but NOT outlive the world, and MapUI is built once per world.
        float _zoom = 1f;
        Control _chunkLayer;
        readonly TextureRect[,] _chunks = new TextureRect[BakedChunks, BakedChunks];
        readonly bool[,] _chunkMissing = new bool[BakedChunks, BakedChunks];   // tried once and not there -- do not retry every frame
        Vector2 _pan;                 // top-left of the map image relative to the clip, in pixels
        float _baseSize = 1f;         // the un-zoomed square edge, from the last Layout
        readonly System.Collections.Generic.List<Marker> _markers = new();
        bool _dragging;
        int _markerSeq;

        sealed class Marker
        {
            public Vector2 Norm;      // map-normalised 0..1, so it is zoom/pan independent
            public Polygon2D Pin;
            public Control Tag;   // the pill, not the Label inside it
        }

        // Other players plotted on the map -- rebuilt only when the roster changes, not every frame. Their
        // NORMALISED positions are kept alongside the nodes so a pan or a zoom can re-place them immediately;
        // without that they sit at the old transform until the next roster tick and visibly lag the map.
        readonly System.Collections.Generic.List<(Control dot, Control box, Label lbl)> _peerDots = new();
        readonly System.Collections.Generic.List<Vector2> _peerNorms = new();

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Layer = MapLayer;   // under the vitals (12), the F1 console (100) and the note reader (91)
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

            // The high-res tiles live in their own layer UNDER the pins. Added before the town dots below so it
            // cannot draw over them, and sized in ApplyView off the same `s` everything else is placed against --
            // one transform for the image and the things pinned to it, which is the invariant ApplyView exists
            // to keep. Textures are loaded and freed by zoom (RefreshChunks); this is just the frame.
            _chunkLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _map.AddChild(_chunkLayer);
            for (int r = 0; r < BakedChunks; r++)
                for (int c = 0; c < BakedChunks; c++)
                {
                    var t = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
                    _chunkLayer.AddChild(t);
                    _chunks[r, c] = t;
                }

            foreach (var (name, pos) in MapNodes.Locations)
            {
                var dot = Pip(UITheme.Accent, TownPip);
                _map.AddChild(dot);
                var lbl = MapLabel(name, new Color(1f, 1f, 1f), out _);
                _map.AddChild(lbl);
                _towns.Add((WorldToNorm(pos), dot, lbl));
            }

            _arrow = new Polygon2D { Color = new Color(0.25f, 0.9f, 1f) };
            _arrow.Polygon = new Vector2[] { new(0, -17), new(11, 12), new(0, 5), new(-11, 12) };   // points up (north) at rotation 0
            _map.AddChild(_arrow);
            _arrow.AddChild(Halo(_arrow.Polygon, 3f));

            BuildPlayersPanel();

            _navbar = MenuNavbar.Build(_root, MenuNavbar.Tab.Information, t => Player?.ShowMenu(t), () => Close());   // the Information tab of the unified menu hosts the map
            // Header, matched to CraftingMenu's: FontBody in TextDim. The outline it used to carry was for sitting
            // ON the map image; it lives on the panel now, where an outline would just look heavy next to the
            // crafting screen's header.
            // The status line was a bare Label floating on the backdrop in TextDim -- the one bit of this screen
            // still dressed as raw engine output. It gets a bar of its own, and text you can actually read.
            _coordBar = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            _coordBar.AddThemeStyleboxOverride("panel", UITheme.Box(UITheme.Nav, UITheme.RadiusCell));
            _slide.AddChild(_coordBar);
            _coord = new Label { VerticalAlignment = VerticalAlignment.Center };
            _coord.AddThemeFontSizeOverride("font_size", UITheme.FontBody);
            _coord.AddThemeColorOverride("font_color", UITheme.TextBody);
            _slide.AddChild(_coord);

            _swoop = MenuSwoop.Attach(this, _root, _slide, dim);
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
                GD.Print($"[mapdbg] folder={MapFolder} levelSize={Info().size} nodes={MapNodes.Locations.Count} zoom={_zoom:0.00} pan=({_pan.X:0},{_pan.Y:0}) marks={_markers.Count} labels={LabelsShown}/{LabelsTotal} base={_baseSize:0} clip={_clip.Position}+{_clip.Size} mapPos={_map.Position} mapSize={_map.Size}");
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

            // NAME | POSITION | DISTANCE, the crafting detail pane's ingredient-table treatment (FontSmall in
            // TextDim over the columns it labels). The roster owns most of the screen now, so it is a table
            // rather than a list of names down one edge -- and a table wants to say what its columns are.
            _rosterHead = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(8, 36) };
            UITheme.Strip(_rosterHead);
            _playersPanel.AddChild(_rosterHead);
            var hh = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            hh.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            hh.OffsetLeft = 10; hh.OffsetRight = -10;
            hh.AddThemeConstantOverride("separation", 12);
            _rosterHead.AddChild(hh);
            hh.AddChild(UITheme.Label(new Label { Text = "NAME", VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, UITheme.FontSmall, UITheme.TextDim));
            hh.AddChild(UITheme.Label(new Label { Text = "POSITION", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(RosterPosW, 0) }, UITheme.FontSmall, UITheme.TextDim));
            hh.AddChild(UITheme.Label(new Label { Text = "DISTANCE", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(RosterDistW, 0) }, UITheme.FontSmall, UITheme.TextDim));

            var scroll = _rosterScroll = new ScrollContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "RosterScroll" };
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            scroll.OffsetLeft = 8; scroll.OffsetTop = 36f + RosterRowH + 4f; scroll.OffsetRight = -8; scroll.OffsetBottom = -8;
            _playersPanel.AddChild(scroll);
            _playersList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _playersList.AddThemeConstantOverride("separation", 3);
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
            _coordBar.Position = new Vector2(M, M + barH + 4f);
            _coordBar.Size = new Vector2(pw, 30f);
            _coord.Position = new Vector2(M + 12f, M + barH + 4f);
            _coord.Size = new Vector2(pw - 24f, 30f);

            // MAP RIGHT, ROSTER FILLS WHAT IS LEFT (strawberry 2026-09-08: "align map to the right of the
            // screen. change the player list to fill the screen"). The two swapped sides: the roster was a
            // fixed 264 px column pinned right and the map took the rest, so the square map -- height-capped on
            // a 16:9 screen -- had to be centred in a region far wider than itself. Anchoring the SQUARE to the
            // right edge and giving the roster everything else means the leftover width has somewhere to go
            // instead of sitting between them as a hole, and the roster gets a real page rather than a strip.
            float contentBottom = vp.Y - M - M;
            // The square is as tall as the content area -- capping it at half the width instead left a band of
            // empty panel under the island, which is the same lopsided hole this move was undoing, rotated 90
            // degrees. The only cap that earns its place is the one that keeps the roster wide enough for its
            // three columns; short of that the map takes the height and the roster takes the rest.
            float s = contentBottom - top;
            s = Mathf.Min(s, Mathf.Max(200f, vp.X - 3f * M - Gutter - RosterMinW));
            if (s < 64f) s = Mathf.Max(64f, contentBottom - top);     // very narrow window: keep a usable map rather than a sliver
            _baseSize = s;

            float mapX = vp.X - M - M - s;
            _clip.Position = new Vector2(mapX, top);
            _clip.Size = new Vector2(s, s);

            _playersPanel.Position = new Vector2(M + M, top);
            _playersPanel.Size = new Vector2(Mathf.Max(160f, mapX - Gutter - (M + M)), Mathf.Max(120f, contentBottom - top));
            if (_rosterHead != null) _rosterHead.Size = new Vector2(Mathf.Max(120f, _playersPanel.Size.X - 16f), RosterRowH);
            // The PANEL still fills the page -- master asked for that and asked for this screen to be left out of
            // the vitals reflow. The LIST inside it stops at the bars anyway, because now that they draw over this
            // screen a row behind the health bar is unreadable, and "not covered" cuts both ways.
            if (_rosterScroll != null)
            {
                float panelBottom = _playersPanel.Position.Y + _playersPanel.Size.Y;
                float listBottom = HUD.ContentBottom(vp, _playersPanel.Position.X, _playersPanel.Position.X + _playersPanel.Size.X, panelBottom);
                _rosterScroll.OffsetBottom = -Mathf.Max(8f, panelBottom - listBottom + 8f);
            }
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
            foreach (var (norm, dot, _) in _towns) dot.Position = norm * s - new Vector2(TownPip, TownPip) * 0.5f;
            foreach (var m in _markers) PlaceMarker(m, s);
            PlacePeers(s);
            LayoutLabels(s);
            RefreshChunks(s);
        }

        /// <summary>Zoom LOD: the overview alone while it still has the pixels, the high-res tiles once it does
        /// not -- and only the tiles actually on screen (strawberry 2026-09-09: "mipmap/LOD the high res chunks on
        /// the map screen, so zooming keeps detail, and zooming out doesnt have a 50000x50000px texture").
        ///
        /// A tile is dropped the moment it leaves the view, so what is resident is bounded by how many fit on the
        /// panel at once -- typically one or two -- rather than by the grid. Zooming back out frees the lot.
        /// The overview stays underneath the whole time, so a tile that has not loaded yet shows the coarse
        /// island rather than a hole.</summary>
        void RefreshChunks(float s)
        {
            if (_chunkLayer == null) return;
            bool on = _zoom >= ChunkZoom;
            _chunkLayer.Visible = on;
            _chunkLayer.Size = new Vector2(s, s);
            float tile = s / BakedChunks;
            // The visible window in map-local pixels: _map sits at _pan inside _clip, so the panel's top-left is
            // at -_pan on the image. GROWN BY A MARGIN, because load and drop test the same rect and this runs on
            // every pan: a tile sitting exactly on the edge would otherwise decode, drop and decode again as the
            // cursor jitters across the boundary, and decoding a 2048 JPEG is not something to do twice a frame.
            // The margin means a tile has to travel a quarter of its width clear of the panel before it is let go,
            // which also gets it loaded slightly before it is needed.
            var view = new Rect2(-_pan, _clip.Size).Grow(tile * 0.25f);
            for (int r = 0; r < BakedChunks; r++)
                for (int c = 0; c < BakedChunks; c++)
                {
                    var t = _chunks[r, c];
                    var rect = new Rect2(new Vector2(c * tile, r * tile), new Vector2(tile, tile));
                    t.Position = rect.Position;
                    t.Size = rect.Size;
                    bool want = on && view.Intersects(rect);
                    t.Visible = want;
                    if (want && t.Texture == null && !_chunkMissing[r, c])
                    {
                        var ct = LoadChunk(r, c);
                        if (ct == null) _chunkMissing[r, c] = true; else t.Texture = ct;
                    }
                    else if (!want && t.Texture != null) t.Texture = null;   // off screen -> let the texture go
                }
        }

        static Texture2D LoadChunk(int row, int col)
        {
            string p = ProjectSettings.GlobalizePath("res://content/" + BakedChunkName(row, col));
            if (!System.IO.File.Exists(p)) return null;
            var img = ContentProvider.LoadImage(p);
            if (img == null) return null;
            // Mipmaps so a tile still minifies cleanly in the band just above ChunkZoom, where it is drawn
            // smaller than its own pixel count.
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }

        void PlacePeers(float s)
        {
            for (int i = 0; i < _peerDots.Count; i++)
            {
                bool used = i < _peerNorms.Count;
                _peerDots[i].dot.Visible = used;
                _peerDots[i].box.Visible = used;
                if (!used) continue;
                _peerDots[i].dot.Position = _peerNorms[i] * s - new Vector2(PeerPip, PeerPip) * 0.5f;
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

        // ---- MAP FURNITURE: ONE SIZE, EVERY ZOOM (strawberry 2026-09-09: "work on the scale of all of that stuff
        // and how it should respond to zooming in/out").
        //
        // The answer is that the SIZE does not respond, and the DENSITY does. A name scaled with the map is
        // unreadable at 1x and absurd at 8x, and a pip is a pointer AT a place rather than a thing standing in it,
        // so both stay a constant number of screen pixels. What zoom changes is how much room there is between
        // anchors -- so labels are placed greedily and any that would collide with one already down is dropped.
        // Zoom in and the anchors spread and the names appear; zoom out and the island stays legible instead of
        // turning into 21 overlapping words. The PIPS never drop, only their labels, so no information ever
        // disappears -- the dot is still there to hover or read off the roster.
        // Sized so the FILL matches the old flat dot (5 px town / 7 px peer) and the ring is added OUTSIDE it.
        // First attempt kept the old 7 px overall and put a 2 px ring inside that, which leaves 3 px of colour and
        // reads as a black square at map scale -- less legible than the un-ringed dot it replaced, not more.
        const float PipRing = 3f;
        const float TownPip = 9f + 2f * PipRing, PeerPip = 12f + 2f * PipRing;
        const int MapFont = 18;   // deliberately above FontHeading: this is a full-screen chart, not a list
        static Color PeerColor => new(1f, 0.55f, 0.2f);

        /// <summary>A map pip: a round dot with a dark ring, so it reads on sand, grass, asphalt and water alike.
        /// The flat un-ringed squares this replaced vanished against any surface close to their own colour.</summary>
        static Panel Pip(Color fill, float d)
        {
            var p = new Panel { Size = new Vector2(d, d), MouseFilter = Control.MouseFilterEnum.Ignore };
            p.AddThemeStyleboxOverride("panel", UITheme.Box(fill, Mathf.RoundToInt(d * 0.5f), new Color(0f, 0f, 0f, 0.85f), Mathf.RoundToInt(PipRing)));
            return p;
        }

        /// <summary>Map label styling in one place, so towns, peers and markers cannot drift apart. Outline 3, not
        /// the 4 these used to carry: at FontSmall a 4 px outline is thicker than the strokes it is outlining.</summary>
        /// <summary>A dark copy of a polygon, grown outward from its own centroid and drawn behind it -- the
        /// Polygon2D equivalent of the ring the pips get. Added as a CHILD with ZIndex -1 so it follows the shape
        /// it outlines without anything having to keep two positions in step. The cyan arrow on pale sand and the
        /// gold pin on a road were the two that needed it.</summary>
        static Polygon2D Halo(Vector2[] poly, float grow)
        {
            Vector2 c = Vector2.Zero;
            foreach (var v in poly) c += v;
            c /= poly.Length;
            var outp = new Vector2[poly.Length];
            for (int i = 0; i < poly.Length; i++)
            {
                var d = poly[i] - c;
                outp[i] = c + d + (d.LengthSquared() > 1e-6f ? d.Normalized() * grow : Vector2.Zero);
            }
            return new Polygon2D { Polygon = outp, Color = new Color(0f, 0f, 0f, 0.85f), ZIndex = -1 };
        }

        /// <summary>A map name on its own dark pill (strawberry 2026-09-09: "the text and markers and blobs need
        /// to be bigger and clearer"). An outline alone was doing all the legibility work and losing: over a busy
        /// tile -- treetops, road markings, orange dirt -- white-on-anything needs a ground of its own, and a
        /// translucent plate reads at a glance where a 3 px stroke does not. The outline stays, thinner, just to
        /// keep the glyph edges off the plate.</summary>
        static PanelContainer MapLabel(string text, Color c, out Label inner)
        {
            var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            var sb = UITheme.Box(new Color(0f, 0f, 0f, 0.55f), 5);
            sb.ContentMarginLeft = sb.ContentMarginRight = 7;
            sb.ContentMarginTop = sb.ContentMarginBottom = 3;
            box.AddThemeStyleboxOverride("panel", sb);
            inner = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
            inner.AddThemeFontSizeOverride("font_size", MapFont);
            inner.AddThemeColorOverride("font_color", c);
            inner.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            inner.AddThemeConstantOverride("outline_size", 2);
            box.AddChild(inner);
            return box;
        }

        readonly System.Collections.Generic.List<(Control lbl, Vector2 anchor, float pip)> _labelPass = new();
        readonly System.Collections.Generic.List<Rect2> _labelPlaced = new();

        /// <summary>Place every label on the map in ONE pass, so they can be tested against each other. Ordered by
        /// what the player would miss most: markers they placed themselves, then live people, then scenery.</summary>
        void LayoutLabels(float s)
        {
            _labelPass.Clear();
            _labelPlaced.Clear();
            foreach (var m in _markers) _labelPass.Add((m.Tag, m.Norm * s + new Vector2(0f, -15f), 20f));   // beside the pin BODY, not its tip
            for (int i = 0; i < _peerDots.Count && i < _peerNorms.Count; i++) _labelPass.Add((_peerDots[i].box, _peerNorms[i] * s, PeerPip));
            foreach (var (norm, _, lbl) in _towns) _labelPass.Add((lbl, norm * s, TownPip));

            // Tested against what you can SEE, not against the whole island: a name must not run off the panel,
            // and a name scrolled off it must not go on blocking one that is on it.
            var view = new Rect2(-_pan, _clip.Size);
            int inView = 0;
            foreach (var (lbl, anchor, pip) in _labelPass)
            {
                var sz = lbl.GetMinimumSize();
                float gap = pip * 0.5f + 4f;
                var pos = new Vector2(anchor.X + gap, anchor.Y - sz.Y * 0.5f);
                if (pos.X + sz.X > view.End.X) pos.X = anchor.X - gap - sz.X;   // near the right edge, hang it off the other side
                var r = new Rect2(pos, sz);
                bool onScreen = view.Intersects(r);
                if (onScreen) inView++;
                bool clear = onScreen;
                if (clear)
                    foreach (var q in _labelPlaced)
                        if (q.Intersects(r)) { clear = false; break; }
                lbl.Visible = clear;
                if (clear) { lbl.Position = pos; _labelPlaced.Add(r.Grow(3f)); }   // a little air, so survivors are not touching
            }
            // Counted against what is ON SCREEN, not against every label on the island: a name scrolled out of the
            // panel is not a name this dropped, and lumping the two together would report a collision rate of 22
            // when the real answer is zero.
            LabelsShown = _labelPlaced.Count; LabelsTotal = inView;
        }

        /// <summary>How many labels survived the last pass, and how many were offered. The pair IS the zoom
        /// response -- it should climb as you zoom in -- so it is worth being able to read rather than squint at.</summary>
        public int LabelsShown { get; private set; }
        public int LabelsTotal { get; private set; }

        void PlaceMarker(Marker m, float s)
        {
            m.Pin.Position = m.Norm * s;   // the TIP is the marked spot; the tag is placed by LayoutLabels
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

            // Rebuild the ROWS only when the set of names actually changed -- otherwise every 0.4 s we would
            // throw away and rebuild a pile of Controls for no visible difference. The position and distance
            // cells DO change every tick, so they are held and written below rather than rebuilt with the row.
            string sig = "";
            foreach (var r in rows) sig += r.name + "|";
            if (sig != _rosterSig)
            {
                _rosterSig = sig;
                foreach (var c in _playersList.GetChildren()) c.QueueFree();
                _rosterCells.Clear();
                foreach (var r in rows)
                {
                    var row = new Panel { CustomMinimumSize = new Vector2(0, RosterRowH), MouseFilter = Control.MouseFilterEnum.Ignore };
                    row.AddThemeStyleboxOverride("panel", UITheme.Box(RosterRowC, UITheme.RadiusCell));
                    var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                    h.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    h.OffsetLeft = 10; h.OffsetRight = -10;
                    h.AddThemeConstantOverride("separation", 12);
                    row.AddChild(h);
                    h.AddChild(UITheme.Label(new Label
                    {
                        Text = r.me ? r.name + "   (you)" : r.name,
                        VerticalAlignment = VerticalAlignment.Center,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(RosterNameW, 0),
                    }, UITheme.FontBody, r.me ? new Color(0.25f, 0.9f, 1f) : UITheme.Text));   // a roster is read at a glance; FontSmall is for map labels
                    var pos = UITheme.Label(new Label { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(RosterPosW, 0) }, UITheme.FontBody, UITheme.TextDim);
                    var dist = UITheme.Label(new Label { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(RosterDistW, 0) }, UITheme.FontBody, UITheme.TextDim);
                    h.AddChild(pos); h.AddChild(dist);
                    _playersList.AddChild(row);
                    _rosterCells.Add((pos, dist));
                }
            }
            // live cells, every tick: where each player is and how far off they are.
            for (int i = 0; i < _rosterCells.Count && i < rows.Count; i++)
            {
                var r = rows[i];
                _rosterCells[i].pos.Text = $"X {r.pos.X:0}   Z {r.pos.Z:0}";
                float d = self.DistanceTo(r.pos);
                _rosterCells[i].dist.Text = r.me ? "—" : (d >= 1000f ? $"{d / 1000f:0.0} km" : $"{d:0} m");
            }

            // one dot per OTHER player, pooled
            int need = rows.Count - 1;
            while (_peerDots.Count < need)
            {
                var d = Pip(PeerColor, PeerPip);
                _map.AddChild(d);
                var box = MapLabel("", new Color(1f, 0.78f, 0.55f), out var t);
                _map.AddChild(box);
                _peerDots.Add((d, box, t));
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
            pin.Polygon = new Vector2[] { new(0, 0), new(-10, -22), new(0, -30), new(10, -22) };   // a teardrop pin whose TIP is the marked spot
            _map.AddChild(pin);
            pin.AddChild(Halo(pin.Polygon, 3f));
            var tag = MapLabel($"M{++_markerSeq}", new Color(1f, 0.9f, 0.5f), out _);
            _map.AddChild(tag);
            var m = new Marker { Norm = norm, Pin = pin, Tag = tag };
            _markers.Add(m);
            PlaceMarker(m, s);
            // The whole label set has to be re-run, not just this one: labels are placed against EACH OTHER, so a
            // new pin can legitimately displace a town name that was sitting where its tag now goes. Placing the
            // tag alone would also leave it at the origin until the next pan, which is what a player would see as
            // "my marker has no number on it".
            LayoutLabels(s);
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
            Layer = DuckLayer;
            if (_swoop == null || !_swoop.Out()) { _root.Visible = false; if (captureMouse) Input.MouseMode = Input.MouseModeEnum.Captured; }
        }

        public override void _ExitTree() { if (Current == this) Current = null; }

        static Vector2 WorldToNorm(Vector3 p) { float ls = Info().size; return new Vector2(p.X / ls + 0.5f, 0.5f + p.Z / ls); }

        static Texture2D LoadMap()
        {
            // OUR BAKE FIRST, the retail image as the fallback (strawberry 2026-09-09: "can we setup our own
            // baked map image from high above our scene?"). UG_MAPRETAIL=1 forces the shipped one, which is what
            // you want when comparing the two or when a bake has gone wrong.
            string name = Info().img;
            if (System.Environment.GetEnvironmentVariable("UG_MAPRETAIL") != "1")
            {
                string b = ProjectSettings.GlobalizePath("res://content/" + BakedImageName);
                if (System.IO.File.Exists(b)) name = BakedImageName;
            }
            string p = ProjectSettings.GlobalizePath("res://content/" + name);
            if (!System.IO.File.Exists(p)) { GD.Print($"[map] missing content/{name}"); return null; }
            var img = ContentProvider.LoadImage(p);
            if (img != null) GD.Print($"[map] {name} {img.GetWidth()}x{img.GetHeight()} for a {Info().size:0} m level");
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
    }
}
