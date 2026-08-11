using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // FOLIAGE PAINTING (strawberry: "implement foliage into the map editor", "go with whatever the source does").
    //
    // Modelled on retail's FoliageEditor, read for behaviour and values rather than copied:
    //   * modes PAINT / EXACT / BAKE, with BAKE the default in retail's own menu
    //   * a circular brush: radius 16 default (clamped 0..2048), falloff 0.5, strength 0.05
    //   * each sample lands by raycasting DOWN from brushPos + (x, radius, y) over 2*radius against a surface
    //     mask, and is seated on the HIT NORMAL -- so foliage lies along a slope instead of standing upright in it
    //   * removal is filtered by ManuallyPlaced / Baked / All
    //
    // BAKE is deliberately not implemented here. It needs the per-asset spawn rules (surface, slope, density)
    // that live in FoliageInfoAsset, none of which this port has extracted, and faking those rules would produce
    // a scatter that looks authored but matches nothing in the source. PAINT and EXACT need no rule data.
    //
    // Everything hand-placed is flagged manual, which is what stops a future BAKE from clearing it --
    // retail's `clearWhenBaked = false; // Manually placed, should not be cleared`. That flag is already in the
    // v2 .bin format (see FoliageAuthoring), so painting today is forward-compatible with baking later.
    public partial class EditorFoliage : Node3D
    {
        public enum EMode { Paint, Exact }

        const uint TerrainLayer = 1u << 0;

        readonly Editor _ed;
        readonly Camera3D _cam;
        readonly FoliageField _field;
        readonly List<string> _types = new();

        EMode _mode = EMode.Paint;
        int _type;
        float _radius = 16f;      // retail DevkitFoliageToolOptions defaults
        float _falloff = 0.5f;
        float _strength = 0.05f;
        double _accum;            // fractional instances carried between frames, so a low rate still paints

        public EditorFoliage(Editor ed, Camera3D cam, FoliageField field)
        {
            _ed = ed; _cam = cam; _field = field;
            foreach (var t in field.AuthoringTypes) _types.Add(t);
            _types.Sort();
        }

        public int Mode { get => (int)_mode; set => _mode = (EMode)Mathf.Clamp(value, 0, 1); }
        public string TypeName => _types.Count == 0 ? "(none)" : _types[Mathf.Clamp(_type, 0, _types.Count - 1)];
        public int TypeCount => _types.Count;
        public void CycleType(int d) { if (_types.Count > 0) _type = (int)Mathf.PosMod(_type + d, _types.Count); }
        public float RadiusVal { get => _radius; set => _radius = Mathf.Clamp(value, 0.5f, 2048f); }
        public float StrengthVal { get => _strength; set => _strength = Mathf.Clamp(value, 0.001f, 1f); }
        public float FalloffVal { get => _falloff; set => _falloff = Mathf.Clamp(value, 0f, 1f); }
        public string ModeText => $"{_mode} · {TypeName} · r{_radius:0} s{_strength:0.###}";

        /// <summary>Test seam: paint one stroke at a world point without needing a mouse or a camera.</summary>
        public int PaintAt(Vector3 centre, int count) => Scatter(centre, count);

        /// <summary>Test seam: the erase half, with retail's population filter.</summary>
        public int EraseAt(Vector3 centre, bool manual, bool baked)
            => _types.Count == 0 ? 0 : _field.RemoveInSphere(TypeName, centre, _radius, manual, baked);

        public override void _Process(double dt)
        {
            if (_ed == null || _ed.Mode != EEditorMode.Environment || _types.Count == 0) return;
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) { _accum = 0; return; }
            if (!CursorOnGround(out var at)) return;

            // ALT erases. Which population it erases follows retail: alt+shift takes baked foliage, plain alt
            // takes what a human placed -- so a mistaken brush stroke is undoable without destroying the
            // generated field underneath it.
            if (Input.IsKeyPressed(Key.Alt))
            {
                bool baked = Input.IsKeyPressed(Key.Shift);
                _field.RemoveInSphere(TypeName, at, _radius, manual: !baked, baked: baked);
                return;
            }

            if (_mode == EMode.Exact) { Scatter(at, 1); return; }

            // Rate, not a fixed count: retail scales by brush AREA, so a wide brush lays down proportionally
            // more per stroke instead of spreading the same handful thinner. Accumulated across frames so a
            // strength of 0.05 still paints rather than rounding to zero every tick.
            _accum += Mathf.Pi * _radius * _radius * _strength * dt;
            int n = (int)_accum;
            if (n <= 0) return;
            _accum -= n;
            Scatter(at, Mathf.Min(n, 64));   // cap per frame: a huge brush should be slow, not a frame spike
        }

        int Scatter(Vector3 centre, int count)
        {
            if (_types.Count == 0) return 0;
            string type = TypeName;
            var space = GetWorld3D().DirectSpaceState;
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                // sqrt on the random radius, or the samples bunch at the centre: uniform r gives uniform
                // ANGULAR density, not uniform area density, and the brush paints a visible bullseye.
                float r = _radius * Mathf.Sqrt(GD.Randf());
                float a = GD.Randf() * Mathf.Tau;
                float fall = Mathf.Lerp(1f, 1f - _falloff, r / Mathf.Max(_radius, 0.001f));
                if (GD.Randf() > fall) continue;   // falloff thins the edge rather than hard-cutting it

                var probe = centre + new Vector3(Mathf.Cos(a) * r, _radius, Mathf.Sin(a) * r);
                var q = new PhysicsRayQueryParameters3D
                {
                    From = probe, To = probe + Vector3.Down * (_radius * 2f), CollisionMask = TerrainLayer,
                };
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;      // nothing under this sample: a cliff edge or off the map

                var pos = (Vector3)hit["position"];
                var nrm = ((Vector3)hit["normal"]).Normalized();
                // Seat on the surface normal so foliage lies along a slope. Yaw is random, or an entire hillside
                // faces the same way and reads as wallpaper.
                var basis = BasisFromNormal(nrm, GD.Randf() * Mathf.Tau);
                if (_field.AddInstance(type, new Transform3D(basis, pos), manual: true)) placed++;
            }
            return placed;
        }

        static Basis BasisFromNormal(Vector3 up, float yaw)
        {
            if (up.LengthSquared() < 1e-6f) up = Vector3.Up;
            var fwd = Mathf.Abs(up.Dot(Vector3.Forward)) > 0.99f ? Vector3.Right : Vector3.Forward;
            var right = fwd.Cross(up).Normalized();
            var f2 = up.Cross(right).Normalized();
            return new Basis(right, up, f2).Rotated(up, yaw);
        }

        bool CursorOnGround(out Vector3 at)
        {
            at = Vector3.Zero;
            if (_cam == null) return false;
            var screen = GetViewport().GetMousePosition();
            var from = _cam.ProjectRayOrigin(screen);
            var q = new PhysicsRayQueryParameters3D
            {
                From = from, To = from + _cam.ProjectRayNormal(screen) * 12000f, CollisionMask = TerrainLayer,
            };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            at = (Vector3)hit["position"];
            return true;
        }
    }
}
