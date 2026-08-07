using Godot;

namespace UnturnedGodot
{
    // Interactive barricade placement sandbox (--barricadeplay): a standalone way to TEST placement feel before the
    // in-game held-item flow is wired (that's a shared-PlayerController follow-up). Hold RMB to free-fly/look
    // (EditorCamera); a BarricadePlacer ghost follows the screen-centre aim each frame; LMB plants the current
    // barricade on a valid (blue) surface via Barricade.PlaceOnSurface. Keys: [1..3] cycle def, [Tab] cycle the mount
    // family, [R] rotate 90.
    //
    // The place / cycle logic lives in plain methods (PlaceCurrent / CycleDef / CycleMount / Rotate) so it's L1-testable;
    // _UnhandledInput is a thin wire onto them (raw input events can't be driven headless).
    public partial class BarricadePlayground : Node3D
    {
        Camera3D _cam;
        BarricadePlacer _placer;
        DeployableDef[] _defs;
        int _defIx;
        Label _hud;

        public BarricadePlacer Placer => _placer;
        public DeployableDef Current => _defs[_defIx];

        public void Setup(Camera3D cam, DeployableDef[] defs = null)
        {
            _cam = cam;
            _defs = defs ?? new[] { DeployableDef.MetalBarricade, DeployableDef.Generator, DeployableDef.Spotlight };
            _placer = new BarricadePlacer();
            AddChild(_placer);
            _placer.SetDef(_defs[0]);
            var layer = new CanvasLayer();
            AddChild(layer);
            _hud = new Label { Position = new Vector2(16f, 14f) };
            layer.AddChild(_hud);
            UpdateHud();
        }

        void UpdateHud()
        {
            if (_hud == null) return;
            _hud.Text = $"Def: {Current.Name}    Mount: {_placer.Mount}\n[1-{_defs.Length}] def   [Tab] mount family   [R] rotate   LMB place   hold RMB to fly";
        }

        // Aim the ghost at the camera's screen-centre. Called from _Process (live) + directly by tests (the headless
        // test loop steps physics only, so render/idle frames never fire).
        public void Aim() { if (_placer != null && _cam != null) _placer.Aim(_cam); }

        public override void _Process(double delta) => Aim();   // ghost follows the screen-centre aim each render frame

        // Plant the current barricade if the ghost is on a valid surface. Returns the placed node (or null).
        public Deployable PlaceCurrent()
        {
            if (_placer == null || !_placer.Valid) return null;
            var d = Barricade.PlaceOnSurface(GetParent(), Current, _placer.Point, _placer.Normal, _placer.Yaw, _placer.Mount);
            GD.Print($"[barricadeplay] placed {Current.Name} ({_placer.Mount}) at {_placer.Point}");
            return d;
        }

        public void CycleDef(int slot)   // 0..N-1
        {
            if (_defs == null || _defs.Length == 0) return;
            _defIx = ((slot % _defs.Length) + _defs.Length) % _defs.Length;
            _placer.SetDef(Current);   // SetDef adopts the def's own mount family
            GD.Print($"[barricadeplay] def -> {Current.Name} (mount {_placer.Mount})");
            UpdateHud();
        }

        public void CycleMount()   // override the def's mount family to try Floor/Wall/Sticky on any surface
        {
            _placer.Mount = (BarricadeMount)(((int)_placer.Mount + 1) % 3);
            GD.Print($"[barricadeplay] mount -> {_placer.Mount}");
            UpdateHud();
        }

        public void Rotate() { if (_placer != null) _placer.YawOffset += 90f; }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                PlaceCurrent();
            else if (ev is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9) CycleDef((int)(k.Keycode - Key.Key1));
                else if (k.Keycode == Key.Tab) CycleMount();
                else if (k.Keycode == Key.R) Rotate();
            }
        }
    }
}
