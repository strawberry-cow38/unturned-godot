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

        // Build + show the MAG pie for a mag-fed gun: a wedge per spare mag (icon + round count) + a remove-magazine
        // wedge + a rack wedge (master). A render harness feeds mock data; gameplay feeds the real InBagInstances.
        internal void OpenMags(System.Collections.Generic.List<(Texture2D icon, string name, int rounds, Item mag, string type)> mags, bool canRemove, bool canRack, string chamberType)
        {
            if (IsOpen) return;
            _sectors.Clear();
            foreach (var (icon, name, rounds, mag, type) in mags)
                _sectors.Add(new AmmoPie.Sector { Name = name, CountText = $"{rounds} rds · {(string.IsNullOrEmpty(type) ? "FMJ" : type)}", Selectable = rounds > 0, Icon = icon, MagItem = mag });   // rounds + bullet TYPE; an EMPTY mag still SHOWS but greys out -- not loadable (master)
            _sectors.Add(new AmmoPie.Sector { Name = "remove mag", CountText = canRemove ? "eject" : "empty", Selectable = canRemove, IsRemoveMag = true });
            _sectors.Add(new AmmoPie.Sector { Name = "rack", CountText = canRack ? $"eject {(string.IsNullOrEmpty(chamberType) ? "FMJ" : chamberType)}" : "empty", Selectable = canRack, IsRack = true });   // the CHAMBER's own type -- tracked independently of the seated mag (master)
            int n = _sectors.Count;
            for (int i = 0; i < n; i++) { var s = _sectors[i]; s.MidAngle = -Mathf.Pi / 2f + i * Mathf.Tau / n; _sectors[i] = s; }
            _pie = new AmmoPie { Sectors = _sectors, HubText = "magazine", MouseFilter = Control.MouseFilterEnum.Ignore };
            _pie.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_pie);
            _highlight = _sectors.FindIndex(s => s.Selectable);
            _pie.Highlight = _highlight;
            Visible = true;
            IsOpen = true;
            _pie.QueueRedraw();
        }

        public override void _Process(double delta)
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
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }

    // The pie itself: a full-rect Control that draws N annular sectors around the screen centre + each type's icon /
    // name / count, with the pointed-at sector lit (grown + blue-bordered). Kept separate from the CanvasLayer so
    // _Draw has a Control to run on. Highlight is set by AmmoRadial; QueueRedraw re-runs _Draw.
    public partial class AmmoPie : Control
    {
        public struct Sector { public ushort Id; public string Name; public string CountText; public bool Selectable; public bool Selected; public bool IsUnload; public bool IsRemoveMag; public bool IsRack; public Item MagItem; public float MidAngle; public Texture2D Icon; }
        public System.Collections.Generic.List<Sector> Sectors;
        public string HubText = "load ammo";
        public int Highlight = -1;
        public bool CancelHover;   // cursor sits in the hub -> the "cancel" target (close, no action) is lit (master)
        const float S = 1.9f;      // master: "double the size of the pie" -- one scale applied to every dimension below
        internal const float RIn = 60f * S, ROut = 172f * S, Gap = 0f;   // no gap -> each of N types is a clean full 360/N sector (master)

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
                bool on = i == Highlight && s.Selectable && !CancelHover;   // a wedge only lights when the cursor is OUT of the cancel hub
                float a0 = s.MidAngle - seg * 0.5f + Gap, a1 = s.MidAngle + seg * 0.5f - Gap;
                float rOut = on ? ROut + 18f : ROut;
                Color fill = !s.Selectable ? new Color(0.14f, 0.14f, 0.16f, 0.82f)   // nothing to do (empty mag / no chamber) -> flat grey
                           : on            ? new Color(0.24f, 0.42f, 0.62f, 0.96f)   // pointed-at -> blue
                           : s.IsUnload || s.IsRemoveMag ? new Color(0.30f, 0.15f, 0.14f, 0.92f)   // unload / remove-mag -> dark red
                           : s.IsRack      ? new Color(0.13f, 0.22f, 0.26f, 0.92f)   // rack -> dark teal
                           : s.Selected    ? new Color(0.20f, 0.36f, 0.25f, 0.92f)   // currently loaded -> green
                           :                 new Color(0.11f, 0.12f, 0.15f, 0.92f);
                DrawAnnularSector(c, RIn, rOut, a0, a1, fill);
                DrawAnnularSectorOutline(c, RIn, rOut, a0, a1, on ? new Color(0.66f, 0.86f, 1f) : new Color(0.30f, 0.32f, 0.38f, 0.75f), on ? 3.5f : 2f);

                Vector2 p = c + new Vector2(Mathf.Cos(s.MidAngle), Mathf.Sin(s.MidAngle)) * ((RIn + rOut) * 0.5f);
                Color tint = s.Selectable ? Colors.White : new Color(1, 1, 1, 0.45f);
                if (s.IsUnload || s.IsRemoveMag)   // eject glyph (down chevron): unload shells / drop the magazine
                {
                    Vector2 g = p - new Vector2(0, 14) * S;
                    Color gc = s.Selectable ? new Color(1f, 0.6f, 0.55f) : new Color(0.6f, 0.55f, 0.55f, 0.6f);
                    DrawLine(g + new Vector2(-16, -8) * S, g + new Vector2(0, 9) * S, gc, 3.5f * S);
                    DrawLine(g + new Vector2(16, -8) * S, g + new Vector2(0, 9) * S, gc, 3.5f * S);
                }
                else if (s.IsRack)   // rack glyph: a double chevron pulling the bolt LEFT/back
                {
                    Vector2 g = p - new Vector2(2, 6) * S;
                    Color gc = s.Selectable ? new Color(0.72f, 0.86f, 1f) : new Color(0.6f, 0.6f, 0.62f, 0.6f);
                    DrawLine(g + new Vector2(8, -11) * S, g + new Vector2(-6, 0) * S, gc, 3.5f * S);
                    DrawLine(g + new Vector2(8, 11) * S, g + new Vector2(-6, 0) * S, gc, 3.5f * S);
                    DrawLine(g + new Vector2(20, -11) * S, g + new Vector2(6, 0) * S, gc, 3.5f * S);
                    DrawLine(g + new Vector2(20, 11) * S, g + new Vector2(6, 0) * S, gc, 3.5f * S);
                }
                else if (s.Icon != null)
                {
                    Vector2 tsz = s.Icon.GetSize();   // keep the icon's aspect (mags portrait, shells ~square) instead of smushing it into a box
                    float sc = tsz.X > 0 && tsz.Y > 0 ? Mathf.Min(56f * S / tsz.X, 66f * S / tsz.Y) : 1f;
                    Vector2 dsz = tsz * sc;
                    DrawTextureRect(s.Icon, new Rect2(p - dsz * 0.5f - new Vector2(0, 14) * S, dsz), false, tint);
                }
                if (font != null)
                {
                    DrawString(font, p + new Vector2(-60, 30) * S, s.Name, HorizontalAlignment.Center, (int)(120 * S), (int)(13 * S), s.Selectable ? new Color(0.92f, 0.94f, 0.98f) : new Color(0.6f, 0.6f, 0.63f));
                    DrawString(font, p + new Vector2(-60, 48) * S, s.CountText, HorizontalAlignment.Center, (int)(120 * S), (int)(12 * S), s.Selectable ? new Color(0.72f, 0.92f, 0.74f) : new Color(0.85f, 0.5f, 0.5f));
                }
            }
            DrawCircle(c, RIn, CancelHover ? new Color(0.22f, 0.40f, 0.60f, 0.95f) : new Color(0.06f, 0.07f, 0.09f, 0.92f));   // hub; lights blue when it's the cancel target (master)
            if (CancelHover) DrawArc(c, RIn - 3f, 0f, Mathf.Tau, 56, new Color(0.66f, 0.86f, 1f), 3.5f);
            if (font != null) DrawString(font, c + new Vector2(-60, 4) * S, CancelHover ? "cancel" : HubText, HorizontalAlignment.Center, (int)(120 * S), (int)(13 * S), CancelHover ? new Color(0.92f, 0.96f, 1f) : new Color(0.8f, 0.84f, 0.9f));
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
