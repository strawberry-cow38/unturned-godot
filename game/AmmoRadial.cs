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

        public override void _Ready() { Layer = 60; Visible = false; }

        // Build + show the pie for the player's current gun. No-op if there's nothing to pick.
        public void Open(PlayerController p)
        {
            Player = p;
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
            _pie = new AmmoPie { Sectors = _sectors, MouseFilter = Control.MouseFilterEnum.Ignore };
            _pie.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_pie);
            _highlight = _sectors.FindIndex(s => s.Selected && s.Selectable);
            if (_highlight < 0) _highlight = _sectors.FindIndex(s => s.Selectable);
            _pie.Highlight = _highlight;
            Visible = true;
            IsOpen = true;
            _pie.QueueRedraw();
        }

        public override void _Process(double delta)
        {
            if (!IsOpen || Player == null || _pie == null) return;   // PlayerController owns closing + mouse recapture
            Vector2 v = GetViewport().GetMousePosition() - GetViewport().GetVisibleRect().Size * 0.5f;
            int hl = _highlight;
            if (v.Length() >= Deadzone)   // near the centre keeps the current pick, so a tiny wobble can't flip it
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
            if (hl != _highlight) { _highlight = hl; _pie.Highlight = hl; _pie.QueueRedraw(); }
        }

        // Load the pointed-at type (if carried) + close. Called from PlayerController on R release.
        public void ConfirmAndClose()
        {
            if (IsOpen && _highlight >= 0 && _highlight < _sectors.Count && _sectors[_highlight].Selectable)
            {
                var s = _sectors[_highlight];
                if (s.IsUnload) Player?.UnloadShells();
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
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }

    // The pie itself: a full-rect Control that draws N annular sectors around the screen centre + each type's icon /
    // name / count, with the pointed-at sector lit (grown + blue-bordered). Kept separate from the CanvasLayer so
    // _Draw has a Control to run on. Highlight is set by AmmoRadial; QueueRedraw re-runs _Draw.
    public partial class AmmoPie : Control
    {
        public struct Sector { public ushort Id; public string Name; public string CountText; public bool Selectable; public bool Selected; public bool IsUnload; public float MidAngle; public Texture2D Icon; }
        public System.Collections.Generic.List<Sector> Sectors;
        public int Highlight = -1;
        const float RIn = 60f, ROut = 172f, Gap = 0f;   // no gap -> each of N types is a clean full 360/N sector (2 types = two touching 180° halves) (master)

        public override void _Draw()
        {
            if (Sectors == null || Sectors.Count == 0) return;
            Vector2 vp = GetViewportRect().Size;
            Vector2 c = vp * 0.5f;
            var font = GetThemeDefaultFont();
            int n = Sectors.Count;
            float seg = Mathf.Tau / n;
            DrawRect(new Rect2(Vector2.Zero, vp), new Color(0f, 0f, 0f, 0.34f));   // dim backdrop
            for (int i = 0; i < n; i++)
            {
                var s = Sectors[i];
                bool on = i == Highlight && s.Selectable;
                float a0 = s.MidAngle - seg * 0.5f + Gap, a1 = s.MidAngle + seg * 0.5f - Gap;
                float rOut = on ? ROut + 10f : ROut;
                Color fill = !s.Selectable ? new Color(0.14f, 0.14f, 0.16f, 0.82f)   // nothing to do -> flat grey
                           : on            ? new Color(0.24f, 0.42f, 0.62f, 0.96f)   // pointed-at -> blue
                           : s.IsUnload    ? new Color(0.30f, 0.15f, 0.14f, 0.92f)   // unload -> dark red
                           : s.Selected    ? new Color(0.20f, 0.36f, 0.25f, 0.92f)   // currently loaded -> green
                           :                 new Color(0.11f, 0.12f, 0.15f, 0.92f);
                DrawAnnularSector(c, RIn, rOut, a0, a1, fill);
                DrawAnnularSectorOutline(c, RIn, rOut, a0, a1, on ? new Color(0.66f, 0.86f, 1f) : new Color(0.30f, 0.32f, 0.38f, 0.75f), on ? 3f : 1.5f);

                Vector2 p = c + new Vector2(Mathf.Cos(s.MidAngle), Mathf.Sin(s.MidAngle)) * ((RIn + rOut) * 0.5f);
                Color tint = s.Selectable ? Colors.White : new Color(1, 1, 1, 0.45f);
                if (s.IsUnload)   // an eject (down-chevron) glyph in place of an item icon
                {
                    Vector2 g = p - new Vector2(0, 14);
                    Color gc = s.Selectable ? new Color(1f, 0.6f, 0.55f) : new Color(0.6f, 0.55f, 0.55f, 0.6f);
                    DrawLine(g + new Vector2(-16, -8), g + new Vector2(0, 9), gc, 3.5f);
                    DrawLine(g + new Vector2(16, -8), g + new Vector2(0, 9), gc, 3.5f);
                }
                else if (s.Icon != null) { var isz = new Vector2(56, 56); DrawTextureRect(s.Icon, new Rect2(p - isz * 0.5f - new Vector2(0, 16), isz), false, tint); }
                if (font != null)
                {
                    DrawString(font, p + new Vector2(-60, 30), s.Name, HorizontalAlignment.Center, 120, 13, s.Selectable ? new Color(0.92f, 0.94f, 0.98f) : new Color(0.6f, 0.6f, 0.63f));
                    DrawString(font, p + new Vector2(-60, 48), s.CountText, HorizontalAlignment.Center, 120, 12, s.Selectable ? new Color(0.72f, 0.92f, 0.74f) : new Color(0.85f, 0.5f, 0.5f));
                }
            }
            DrawCircle(c, RIn, new Color(0.06f, 0.07f, 0.09f, 0.92f));   // hub hole
            if (font != null) DrawString(font, c + new Vector2(-48, 4), "load ammo", HorizontalAlignment.Center, 96, 13, new Color(0.8f, 0.84f, 0.9f));
        }

        void DrawAnnularSector(Vector2 c, float rIn, float rOut, float a0, float a1, Color col)
        {
            int steps = Mathf.Max(4, (int)((a1 - a0) / 0.12f));
            var pts = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i <= steps; i++) { float a = Mathf.Lerp(a0, a1, (float)i / steps); pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rOut); }
            for (int i = steps; i >= 0; i--) { float a = Mathf.Lerp(a0, a1, (float)i / steps); pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rIn); }
            DrawColoredPolygon(pts.ToArray(), col);
        }

        void DrawAnnularSectorOutline(Vector2 c, float rIn, float rOut, float a0, float a1, Color col, float w)
        {
            int steps = Mathf.Max(4, (int)((a1 - a0) / 0.12f));
            var outer = new Vector2[steps + 1];
            var inner = new Vector2[steps + 1];
            for (int i = 0; i <= steps; i++) { float a = Mathf.Lerp(a0, a1, (float)i / steps); var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); outer[i] = c + d * rOut; inner[i] = c + d * rIn; }
            DrawPolyline(outer, col, w);
            DrawPolyline(inner, col, w);
            DrawLine(inner[0], outer[0], col, w);
            DrawLine(inner[steps], outer[steps], col, w);
        }
    }
}
