using Godot;
namespace UnturnedGodot
{
    /// <summary>Ctrl-HOLD radial while driving an emergency vehicle (strawberry 2026-09-04): three lightbar flash patterns (each with its
    /// own siren variant -- the same clip at three pitches until the real ones are sourced) plus "lightbar off". Same AmmoPie wedge
    /// drawing + hover rules as the R-hold ammo radial; PlayerController owns open/confirm/close and the mouse mode.</summary>
    public partial class LightbarRadial : CanvasLayer
    {
        public Vehicle Vehicle;
        public bool IsOpen { get; private set; }
        AmmoPie _pie;
        readonly System.Collections.Generic.List<AmmoPie.Sector> _sectors = new();
        int _highlight = -1;
        public override void _Ready() { TickHub.AddProcess(this, HubProcess); SetProcess(false); Layer = 60; Visible = false; }   // PERF: hub-ticked (see TickHub.AddProcess)
        public void Open(Vehicle v)
        {
            if (IsOpen || v == null || !v.HasSiren) return;
            Vehicle = v;
            _sectors.Clear();
            for (int i = 0; i < Vehicle.LightbarPatternNames.Length; i++)
                _sectors.Add(new AmmoPie.Sector { Id = (ushort)i, Name = Vehicle.LightbarPatternNames[i], CountText = v.SirenOn && v.LightbarPattern == i ? "on" : "", Selectable = true, Selected = v.SirenOn && v.LightbarPattern == i });
            _sectors.Add(new AmmoPie.Sector { Id = 0, Name = "lightbar off", CountText = v.SirenOn ? "" : "off", Selectable = v.SirenOn, IsUnload = true });
            int n = _sectors.Count;
            for (int i = 0; i < n; i++) { var s = _sectors[i]; s.MidAngle = -Mathf.Pi / 2f + i * Mathf.Tau / n; _sectors[i] = s; }
            _pie = new AmmoPie { Sectors = _sectors, HubText = "lightbar", MouseFilter = Control.MouseFilterEnum.Ignore };
            _pie.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_pie);
            _highlight = _sectors.FindIndex(s => s.Selected);
            if (_highlight < 0) _highlight = 0;
            _pie.Highlight = _highlight;
            Visible = true; IsOpen = true;
            _pie.QueueRedraw();
        }
        public override void _Process(double delta) => HubProcess(delta);
        public void HubProcess(double delta)
        {
            if (!IsOpen || _pie == null) return;
            Vector2 v = GetViewport().GetMousePosition() - GetViewport().GetVisibleRect().Size * 0.5f;
            bool cancel = v.Length() < AmmoPie.RIn;
            int hl = _highlight;
            if (!cancel)
            {
                Vector2 vn = v.Normalized(); float best = -2f; int bi = -1;
                for (int i = 0; i < _sectors.Count; i++)
                {
                    Vector2 dir = new(Mathf.Cos(_sectors[i].MidAngle), Mathf.Sin(_sectors[i].MidAngle));
                    float dot = vn.Dot(dir);
                    if (dot > best) { best = dot; bi = i; }
                }
                hl = bi;
            }
            else hl = -1;
            if (hl != _highlight || cancel != _pie.CancelHover) { _highlight = hl; _pie.Highlight = hl; _pie.CancelHover = cancel; _pie.QueueRedraw(); }
        }
        public void ConfirmAndClose()
        {
            if (IsOpen && _highlight >= 0 && _highlight < _sectors.Count && _sectors[_highlight].Selectable && Vehicle != null && GodotObject.IsInstanceValid(Vehicle))
            {
                var s = _sectors[_highlight];
                Vehicle.SetLightbar(s.IsUnload ? -1 : s.Id);
            }
            Close();
        }
        public void Close()
        {
            if (!IsOpen && _pie == null) return;
            Visible = false; IsOpen = false; _highlight = -1;
            if (_pie != null) { _pie.QueueFree(); _pie = null; }
            _sectors.Clear();
        }
    }
}
