using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // R-HOLD ammo-type PIE menu for loose-shell shotguns (master: "radial pie menu"). Hold R -> a ring of pie sectors,
    // one per shell type of the gun's gauge (buckshot / slug); the mouse DIRECTION lights the sector it points at;
    // releasing R loads it (PlayerController.ChooseShellType -> select the type + reload, so pellets follow: slug=1,
    // buckshot=6-8). A quick R tap never opens this -- it's a normal reload. Shotguns only (CanChooseShellType). While
    // open the mouse is freed (which also suppresses the FP look, gated on Captured) only so the cursor's angle picks a
    // sector; the pie ignores the mouse so a stray click falls through. PlayerController owns open/close + recapture.
    public partial class AmmoRadial : CanvasLayer
    {
        public PlayerController Player;
        public bool IsOpen { get; private set; }

        AmmoPie _pie;
        readonly System.Collections.Generic.List<AmmoPie.Sector> _sectors = new();
        int _highlight = -1;
        const float Deadzone = 34f;

        public override void _Ready() { TickHub.AddProcess(this, HubProcess); SetProcess(false); Layer = 60; Visible = false; }   // PERF: hub-ticked (see TickHub.AddProcess)

        // Build + show the pie for the player's current gun. No-op if there's nothing to pick.
        public void Open(PlayerController p)
        {
            Player = p;
            if (p != null && p.CanChooseMag)   // mag-fed gun -> the mag pie (spare mags + remove + rack)
            {
                var mags = new System.Collections.Generic.List<(Texture2D icon, string name, int rounds, Item mag, string type)>();
                foreach (var (asset, item, _, _) in p.SpareMags())
                    mags.Add((AttachmentMenu.LoadItemIcon((ushort)asset.id, standUp: true), asset.itemName, item.amount, item, asset.ammoType));
                mags.Sort((a, b) => b.rounds.CompareTo(a.rounds));   // fuller mags sit higher/earlier in the ring (master); the unload + rack wedges are appended AFTER, so they keep their spots
                OpenMags(mags, p.HasMagLoaded, p.HasChamberedRound, p.LoadedAmmoType);
                return;
            }
            var choices = p?.ShellTypeChoices() ?? new System.Collections.Generic.List<(ItemAsset asset, int count, bool selected)>();
            bool canUnload = p != null && p.HasLoadedShells;
            // open if there's a carried type to load OR loaded rounds to eject -- so unload stays reachable even with no
            // spare shells. Segments spread over the carried types + the always-present unload (greyed when empty).
            if (choices.Count > 0 || canUnload) OpenWith(choices, canUnload);
        }

        // Build + show from an explicit choice list (+ whether an unload segment is offered). Open() feeds the player's;
        // a render harness feeds mock data to screenshot the UI without a live gun (Player stays null -> _Process
        // no-ops, the shot is static).
        internal void OpenWith(System.Collections.Generic.List<(ItemAsset asset, int count, bool selected)> choices, bool canUnload)
        {
            if (IsOpen) return;
            _sectors.Clear();
            foreach (var (asset, count, selected) in choices)   // one segment per CARRIED shell type
                _sectors.Add(new AmmoPie.Sector { Id = (ushort)asset.id, Name = PlayerController.PluralAmmo(asset.itemName, count), CountText = $"x{count}", Selectable = true, Selected = selected, Icon = LoadIcon(asset.id) });
            // UNLOAD segment (master): ejects the loaded rounds back to the bag; greyed when nothing's chambered.
            _sectors.Add(new AmmoPie.Sector { Id = 0, Name = "unload", CountText = canUnload ? "eject" : "empty", Selectable = canUnload, IsUnload = true });
            int n = _sectors.Count;   // angles depend on the TOTAL (types + unload), so assign them after
            for (int i = 0; i < n; i++) { var s = _sectors[i]; s.MidAngle = -Mathf.Pi / 2f + i * Mathf.Tau / n; _sectors[i] = s; }
            _pie = new AmmoPie { Sectors = _sectors, MouseFilter = Control.MouseFilterEnum.Ignore, Animate = Player != null };
            _pie.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_pie);
            _highlight = _sectors.FindIndex(s => s.Selected && s.Selectable);
            if (_highlight < 0) _highlight = _sectors.FindIndex(s => s.Selectable);
            _pie.Highlight = _highlight;
            Visible = true;
            IsOpen = true;
            _pie.QueueRedraw();
        }

        // Build + show the MAG pie for a mag-fed gun: a wedge per spare mag (icon + round count) + a remove-magazine
        // wedge + a rack wedge (master). A render harness feeds mock data; gameplay feeds the real InBagInstances.
        internal void OpenMags(System.Collections.Generic.List<(Texture2D icon, string name, int rounds, Item mag, string type)> mags, bool canRemove, bool canRack, string chamberType)
        {
            if (IsOpen) return;
            _sectors.Clear();
            foreach (var (icon, name, rounds, mag, type) in mags)
                _sectors.Add(new AmmoPie.Sector { Name = name, CountText = rounds > 0 ? $"{rounds} rds · {(string.IsNullOrEmpty(type) ? "FMJ" : type)}" : "0 rds", Selectable = rounds > 0, Icon = icon, MagItem = mag });   // rounds + bullet TYPE; an EMPTY mag SHOWS but greys out + drops the type (no rounds = no type) (master)
            _sectors.Add(new AmmoPie.Sector { Name = "remove mag", CountText = canRemove ? "eject" : "empty", Selectable = canRemove, IsRemoveMag = true });
            _sectors.Add(new AmmoPie.Sector { Name = "rack", CountText = canRack ? $"eject {(string.IsNullOrEmpty(chamberType) ? "FMJ" : chamberType)}" : "empty", Selectable = canRack, IsRack = true });   // the CHAMBER's own type -- tracked independently of the seated mag (master)
            int n = _sectors.Count;
            for (int i = 0; i < n; i++) { var s = _sectors[i]; s.MidAngle = -Mathf.Pi / 2f + i * Mathf.Tau / n; _sectors[i] = s; }
            _pie = new AmmoPie { Sectors = _sectors, HubText = "magazine", MouseFilter = Control.MouseFilterEnum.Ignore, Animate = Player != null };
            _pie.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_pie);
            _highlight = _sectors.FindIndex(s => s.Selectable);
            _pie.Highlight = _highlight;
            Visible = true;
            IsOpen = true;
            _pie.QueueRedraw();
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (!IsOpen || Player == null || _pie == null) return;   // PlayerController owns closing + mouse recapture
            Vector2 v = GetViewport().GetMousePosition() - GetViewport().GetVisibleRect().Size * 0.5f;
            bool cancel = v.Length() < AmmoPie.RIn;   // cursor in the centre hub -> "cancel": light nothing, confirm just closes with NO action (master)
            int hl = _highlight;
            if (!cancel)
            {
                Vector2 vn = v.Normalized();
                float best = -2f; int bi = -1;
                for (int i = 0; i < _sectors.Count; i++)
                {
                    Vector2 dir = new(Mathf.Cos(_sectors[i].MidAngle), Mathf.Sin(_sectors[i].MidAngle));
                    float dot = vn.Dot(dir);
                    if (dot > best) { best = dot; bi = i; }
                }
                hl = bi;
            }
            else hl = -1;   // in the hub -> no wedge selected (ConfirmAndClose sees _highlight < 0 -> just closes)
            if (hl != _highlight || cancel != _pie.CancelHover) { _highlight = hl; _pie.Highlight = hl; _pie.CancelHover = cancel; _pie.QueueRedraw(); }
        }

        // Load the pointed-at type (if carried) + close. Called from PlayerController on R release.
        public void ConfirmAndClose()
        {
            if (IsOpen && _highlight >= 0 && _highlight < _sectors.Count && _sectors[_highlight].Selectable)
            {
                var s = _sectors[_highlight];
                if (s.IsUnload) Player?.UnloadShells();
                else if (s.IsRemoveMag) Player?.RemoveMagazine();
                else if (s.IsRack) Player?.RackGun();
                else if (s.MagItem != null) Player?.LoadMagInstance(s.MagItem);
                else Player?.ChooseShellType(s.Id);
            }
            Close();
        }

        public void Close()
        {
            if (!IsOpen && _pie == null) return;
            Visible = false;
            IsOpen = false;
            _highlight = -1;
            if (_pie != null) { _pie.QueueFree(); _pie = null; }
            _sectors.Clear();
        }

        // the real ground-truth inventory icon (content/items/icons/<id>.png), same source the grid + attachment menu use
        static Texture2D LoadIcon(ushort id)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/items/icons/{id}.png");
            if (System.IO.File.Exists(p)) { var img = ContentProvider.LoadImage(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }

    // The pie itself: a full-rect Control that draws N annular sectors around the screen centre + each type's icon /
    // name / count, with the pointed-at sector lit (grown + blue-bordered). Kept separate from the CanvasLayer so
    // _Draw has a Control to run on. Highlight is set by AmmoRadial; QueueRedraw re-runs _Draw.
    public partial class AmmoPie : Control
    {
        // the action icons, loaded once from content/ui (tools/gen_radial_icons.py draws them). No fallback glyph on a
        // miss: a missing file says so in the log instead of quietly drawing the old chevron and looking shipped.
        static readonly System.Collections.Generic.Dictionary<string, Texture2D> _actionIcons = new();
        internal static Texture2D ActionIcon(string name)
        {
            if (_actionIcons.TryGetValue(name, out var t)) return t;
            string path = ProjectSettings.GlobalizePath($"res://content/ui/{name}.png");
            var img = System.IO.File.Exists(path) ? ContentProvider.LoadImage(path) : null;
            // 20x20 pixel art like the retail HUD icons: blown up NEAREST here, so the pie's linear filter at ~110 px
            // keeps hard pixel edges instead of smearing 20 texels into a blur
            if (img != null && !img.IsEmpty() && img.GetWidth() <= 32) img.Resize(img.GetWidth() * 6, img.GetHeight() * 6, Image.Interpolation.Nearest);
            t = img != null && !img.IsEmpty() ? ImageTexture.CreateFromImage(img) : null;
            if (t == null) Log.Err($"[radial] action icon missing: {path}");
            _actionIcons[name] = t;
            return t;
        }

        public struct Sector { public ushort Id; public string Name; public string CountText; public bool Selectable; public bool Selected; public bool IsUnload; public bool IsRemoveMag; public bool IsRack; public Item MagItem; public float MidAngle; public Texture2D Icon; }
        public System.Collections.Generic.List<Sector> Sectors;
        public string HubText = "load ammo";
        public int Highlight = -1;
        public bool CancelHover;   // cursor sits in the hub -> the "cancel" target (close, no action) is lit (master)
        const float S = 1.9f;      // master: "double the size of the pie" -- one scale applied to every dimension below
        internal const float RIn = 45f * S, ROut = 172f * S;   // hub 25% smaller (master, 60->45); no gap -> each of N types is a clean full 360/N sector (split by hairlines, not gaps)

        // ---- LOOK (strawberry 2026-10-05: "rework this menu to look much nicer. its very bland and feels very godot
        // default"). What made it read as default: flat single-colour wedges, a 2 px grey outline round every one, a
        // flat black rectangle over the world, and every label at one size in one weight. Now:
        //   * the world behind is FROSTED (the same blur the inventory and craft menu sit on) and darkened toward the
        //     edges, so the wheel is the brightest thing on screen instead of a shape on a grey sheet;
        //   * the ring sits on its own dark frame, each wedge a soft inner->outer gradient split by hairlines, with the
        //     wedge's KIND as a coloured tab on the inner edge (red: unload / drop, teal: rack, green: loaded) rather
        //     than a whole-wedge fill;
        //   * the pointed-at wedge lifts out, lights in the accent blue, and carries a glow on its rim, all eased;
        //   * a needle on the hub points at it, and its full name + detail sit in a chip under the wheel, so each
        //     wedge only has to carry its icon and one short line.
        // Geometry, angles and the cancel hub are unchanged: the input code reads RIn and MidAngle and nothing else.
        public bool Animate = true;          // the render harness has no frames to ease over: it opens settled
        float _open;                         // 0 -> 1 open ease
        float[] _pop;                        // per-wedge lift, eased toward 1 on the highlighted one
        ColorRect _backdrop;

        const string BACKDROP = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float fade = 1.0;
void fragment() {
    vec2 px = SCREEN_PIXEL_SIZE;
    vec3 c = vec3(0.0); float total = 0.0;
    for (int x = -2; x <= 2; x++) for (int y = -2; y <= 2; y++) {
        float w = 1.0 / (1.0 + float(x*x + y*y));
        c += textureLod(screen_tex, SCREEN_UV + vec2(float(x), float(y)) * px * 3.0, 1.8).rgb * w; total += w;
    }
    c /= total;
    vec2 d = (SCREEN_UV - 0.5) * vec2(px.y / px.x, 1.0);
    float v = smoothstep(0.12, 0.80, length(d));
    c = mix(c * 0.80, vec3(0.025, 0.03, 0.045), 0.30 + 0.50 * v);
    COLOR = vec4(c, fade);
}";

        public override void _Ready()
        {
            _backdrop = new ColorRect { ShowBehindParent = true, MouseFilter = MouseFilterEnum.Ignore };
            _backdrop.Material = new ShaderMaterial { Shader = new Shader { Code = BACKDROP } };
            _backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_backdrop);
            if (!Animate) _open = 1f;
        }

        public override void _Process(double delta)
        {
            if (Sectors == null) return;
            if (_pop == null || _pop.Length != Sectors.Count) _pop = new float[Sectors.Count];
            float dt = (float)delta; bool moving = false;
            float o = Animate ? Mathf.MoveToward(_open, 1f, dt / 0.12f) : 1f;
            if (o != _open) { _open = o; moving = true; }
            float k = Animate ? 1f - Mathf.Exp(-22f * dt) : 1f;
            for (int i = 0; i < _pop.Length; i++)
            {
                float target = i == Highlight && Sectors[i].Selectable && !CancelHover ? 1f : 0f;
                float v = Mathf.Lerp(_pop[i], target, k);
                if (Mathf.Abs(v - target) < 0.002f) v = target;
                if (v != _pop[i]) { _pop[i] = v; moving = true; }
            }
            if (_backdrop?.Material is ShaderMaterial sm) sm.SetShaderParameter("fade", Ease(_open));
            if (moving) QueueRedraw();
        }

        static float Ease(float t) => 1f - Mathf.Pow(1f - Mathf.Clamp(t, 0f, 1f), 3f);
        static Color A(Color c, float a) => new(c.R, c.G, c.B, c.A * a);

        // the wedge's KIND, as the colour of its inner tab (null = an ordinary choice, no tab)
        static Color? KindColour(Sector s)
            => !s.Selectable ? null
             : s.IsUnload || s.IsRemoveMag ? new Color(0.90f, 0.42f, 0.36f)
             : s.IsRack ? new Color(0.42f, 0.78f, 0.86f)
             : s.Selected ? new Color(0.50f, 0.84f, 0.52f)
             : null;

        public override void _Draw()
        {
            if (Sectors == null || Sectors.Count == 0) return;
            if (_pop == null || _pop.Length != Sectors.Count) _pop = new float[Sectors.Count];
            Vector2 vp = GetViewportRect().Size;
            Vector2 c = vp * 0.5f;
            var font = GetThemeDefaultFont();
            int n = Sectors.Count;
            float seg = Mathf.Tau / n;
            float e = Ease(_open), fa = e;                       // open ease: alpha, and a slight grow-in
            float sc = 0.92f + 0.08f * e;
            float rIn = RIn * sc, rBase = ROut * sc;
            var accent = new Color(0.40f, 0.62f, 0.90f);

            // the frame the wedges sit in
            DrawRing(c, rIn - 7f, rBase + 7f, A(new Color(0.03f, 0.035f, 0.05f, 0.80f), fa));
            DrawArc(c, rBase + 7f, 0f, Mathf.Tau, 128, A(new Color(1f, 1f, 1f, 0.10f), fa), 2f, true);

            for (int i = 0; i < n; i++)
            {
                var s = Sectors[i];
                float pop = Ease(_pop[i]);
                bool on = pop > 0.01f;
                float a0 = s.MidAngle - seg * 0.5f, a1 = s.MidAngle + seg * 0.5f;
                float rOut = rBase + 16f * pop;
                Color cIn, cOut;
                if (!s.Selectable) { cIn = new Color(0.10f, 0.105f, 0.12f, 0.62f); cOut = new Color(0.12f, 0.125f, 0.14f, 0.62f); }
                else
                {
                    cIn = new Color(0.10f, 0.115f, 0.15f, 0.90f).Lerp(new Color(0.14f, 0.24f, 0.38f, 0.94f), pop);
                    cOut = new Color(0.17f, 0.19f, 0.23f, 0.90f).Lerp(new Color(0.26f, 0.44f, 0.68f, 0.96f), pop);
                }
                DrawWedge(c, rIn, rOut, a0, a1, A(cIn, fa), A(cOut, fa));

                // the KIND tab along the inner edge
                if (KindColour(s) is Color kc) DrawArc(c, rIn + 4f, a0 + 0.02f, a1 - 0.02f, 32, A(kc, fa * (0.75f + 0.25f * pop)), 6f, true);

                // the lit rim: a bright edge, then a soft glow outside it
                if (on)
                {
                    for (int g = 3; g >= 1; g--) DrawArc(c, rOut + g * 4f, a0 + 0.01f, a1 - 0.01f, 48, A(accent, fa * pop * 0.12f * (4 - g)), 8f, true);
                    DrawArc(c, rOut - 2f, a0 + 0.01f, a1 - 0.01f, 48, A(new Color(0.72f, 0.86f, 1f), fa * pop), 4f, true);
                }

                // contents: the icon, and ONE short line under it (the full name lives in the chip below the wheel)
                Vector2 dir = new(Mathf.Cos(s.MidAngle), Mathf.Sin(s.MidAngle));
                Vector2 p = c + dir * ((rIn + rOut) * 0.5f);
                float grow = 1f + 0.10f * pop;
                Color tint = s.Selectable ? Colors.White : new Color(1, 1, 1, 0.35f);
                if (s.IsUnload || s.IsRemoveMag || s.IsRack)
                {
                    var tex = ActionIcon(s.IsUnload ? "radial_unload" : s.IsRemoveMag ? "radial_remove_mag" : "radial_rack");
                    Color gc = !s.Selectable ? new Color(0.6f, 0.58f, 0.6f, 0.45f) : s.IsRack ? new Color(0.74f, 0.90f, 1f) : new Color(1f, 0.66f, 0.60f);
                    float sz = 54f * S * grow;
                    if (tex != null) DrawTextureRect(tex, new Rect2(p - new Vector2(sz * 0.5f, sz * 0.5f + 12f * S), new Vector2(sz, sz)), false, A(gc, fa));
                }
                else if (s.Icon != null)
                {
                    Vector2 tsz = s.Icon.GetSize();   // keep the icon's aspect (mags portrait, shells ~square)
                    float k = tsz.X > 0 && tsz.Y > 0 ? Mathf.Min(54f * S / tsz.X, 62f * S / tsz.Y) * grow : 1f;
                    Vector2 dsz = tsz * k;
                    DrawTextureRect(s.Icon, new Rect2(p - dsz * 0.5f - new Vector2(0, 12) * S, dsz), false, A(tint, fa));
                }
                if (font != null)
                {
                    bool action = s.IsUnload || s.IsRemoveMag || s.IsRack;
                    string line = action ? s.Name.ToUpperInvariant() : s.CountText;
                    Color lc = !s.Selectable ? new Color(0.55f, 0.56f, 0.60f) : on ? new Color(0.96f, 0.98f, 1f) : action ? new Color(0.86f, 0.88f, 0.92f) : new Color(0.70f, 0.90f, 0.72f);
                    DrawString(font, p + new Vector2(-70, 36) * S, line, HorizontalAlignment.Center, (int)(140 * S), (int)(12 * S), A(lc, fa));
                }
            }
            // hairlines between wedges, over the fills, so neighbours read as separate keys without a gap
            for (int i = 0; i < n; i++)
            {
                float a = Sectors[i].MidAngle - seg * 0.5f;
                Vector2 d = new(Mathf.Cos(a), Mathf.Sin(a));
                DrawLine(c + d * (rIn + 1f), c + d * (rBase + 6f), A(new Color(0.02f, 0.02f, 0.03f, 0.85f), fa), 2.5f, true);
            }

            // hub: a disc with a rim; lights up as the cancel target, and carries a needle toward the lit wedge
            DrawCircle(c, rIn, A(CancelHover ? new Color(0.18f, 0.30f, 0.46f, 0.96f) : new Color(0.05f, 0.06f, 0.08f, 0.96f), fa));
            DrawArc(c, rIn, 0f, Mathf.Tau, 64, A(CancelHover ? new Color(0.72f, 0.86f, 1f) : new Color(1f, 1f, 1f, 0.16f), fa), CancelHover ? 3.5f : 2f, true);
            int hi = Highlight;
            if (!CancelHover && hi >= 0 && hi < n && Sectors[hi].Selectable)
            {
                float ang = Sectors[hi].MidAngle;
                Vector2 d = new(Mathf.Cos(ang), Mathf.Sin(ang)), t = new(-d.Y, d.X);
                Vector2 tip = c + d * (rIn + 1f), b0 = c + d * (rIn - 13f) + t * 11f, b1 = c + d * (rIn - 13f) - t * 11f;
                DrawColoredPolygon(new[] { tip, b0, b1 }, A(new Color(0.72f, 0.86f, 1f), fa));
            }
            if (font != null)
                DrawString(font, c + new Vector2(-60, 5) * S, CancelHover ? "CANCEL" : HubText.ToUpperInvariant(), HorizontalAlignment.Center, (int)(120 * S), (int)(11 * S), A(CancelHover ? new Color(0.94f, 0.97f, 1f) : new Color(0.62f, 0.66f, 0.74f), fa));

            // the chip under the wheel: the lit wedge's full name and detail
            if (font != null && !CancelHover && hi >= 0 && hi < n)
            {
                var s = Sectors[hi];
                string name = s.Name, detail = s.CountText;
                int fsName = (int)(15 * S), fsDetail = (int)(12 * S);
                float w = Mathf.Max(font.GetStringSize(name, HorizontalAlignment.Left, -1, fsName).X, font.GetStringSize(detail, HorizontalAlignment.Left, -1, fsDetail).X) + 44f * S;
                float h = 52f * S;
                var box = new Rect2(c.X - w * 0.5f, c.Y + rBase + 30f * S, w, h);
                DrawStyleBox(UITheme.Box(A(new Color(0.04f, 0.05f, 0.07f, 0.92f), fa), 10, A(s.Selectable ? new Color(0.40f, 0.62f, 0.90f, 0.85f) : new Color(1f, 1f, 1f, 0.15f), fa), 2), box);
                DrawString(font, new Vector2(box.Position.X, box.Position.Y + 22f * S), name, HorizontalAlignment.Center, w, fsName, A(s.Selectable ? new Color(0.94f, 0.96f, 1f) : new Color(0.62f, 0.63f, 0.67f), fa));
                DrawString(font, new Vector2(box.Position.X, box.Position.Y + 42f * S), detail, HorizontalAlignment.Center, w, fsDetail, A(s.Selectable ? new Color(0.70f, 0.90f, 0.72f) : new Color(0.86f, 0.52f, 0.46f), fa));
            }
        }

        void DrawRing(Vector2 c, float rIn, float rOut, Color col) => DrawWedge(c, rIn, rOut, 0f, Mathf.Tau, col, col);

        // an annular sector with an inner->outer gradient (per-vertex colours)
        void DrawWedge(Vector2 c, float rIn, float rOut, float a0, float a1, Color cIn, Color cOut)
        {
            int steps = Mathf.Max(6, (int)((a1 - a0) / 0.05f));
            var pts = new Vector2[(steps + 1) * 2];
            var cols = new Color[pts.Length];
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, (float)i / steps);
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                pts[i] = c + d * rOut; cols[i] = cOut;
                pts[pts.Length - 1 - i] = c + d * rIn; cols[pts.Length - 1 - i] = cIn;
            }
            DrawPolygon(pts, cols);
        }
    }
}
