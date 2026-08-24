using Godot;

namespace UnturnedGodot
{
    // The rippling, wind-facing flag CLOTH (master 2026-08-24: split the flag into flag + pole, ripple the cloth by the
    // wind speed where it is, and swivel it round the pole to face the wind direction). The POLE stays as the prop's
    // main mesh; this node carries the split-out cloth on a PIVOT that rotates around the pole axis (the prop's local Z,
    // which the placement stands vertical) so the flag streams downwind, and drives flag.gdshader's `wind` uniform from
    // WindField. FlagCloth itself holds the FIXED placement; only the pivot turns.
    public partial class FlagCloth : Node3D
    {
        Node3D _pivot;
        ShaderMaterial _mat;
        static Shader _shader;

        // clothMesh in the prop's LOCAL base coords (+Y = flying, X = thin/perp the cloth waves along, Z = along the
        // pole). `basis`/`gpos` = the prop's world placement (WorldBuilder keeps the root at the origin, so the flag
        // carries its own placement). `tex` = the flag's albedo.
        public static FlagCloth Attach(Node root, ArrayMesh clothMesh, Texture2D tex, Basis basis, Vector3 gpos, float cull)
        {
            var fc = new FlagCloth { Transform = new Transform3D(basis, gpos) };   // the placed, FIXED frame
            root.AddChild(fc);
            fc._pivot = new Node3D();                                              // turns around local Z (the pole) to face wind
            fc.AddChild(fc._pivot);
            var aabb = clothMesh.GetAabb();
            float len = Mathf.Max(0.5f, aabb.Position.Y + aabb.Size.Y);            // free-edge Y = flying length (amplitude ramp)
            _shader ??= GD.Load<Shader>("res://content/flag.gdshader");
            fc._mat = new ShaderMaterial { Shader = _shader };
            fc._mat.SetShaderParameter("flag_tex", tex);
            fc._mat.SetShaderParameter("flag_len", len);
            fc._pivot.AddChild(new MeshInstance3D
            {
                Mesh = clothMesh,
                MaterialOverride = fc._mat,
                VisibilityRangeEnd = cull,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            });
            return fc;
        }

        public override void _Process(double delta)
        {
            if (_mat == null || _pivot == null) return;
            Vector3 wp = GlobalTransform.Origin;                                    // the placed flag's world position = wind sample point
            _mat.SetShaderParameter("wind", WindField.SampleWind(wp));
            // FACE THE WIND: rotate the pivot around the pole axis (local Z) so the cloth's +Y flying direction aims
            // where the wind blows. Convert the world wind into this flag's own (placement) frame, then its bearing in
            // the local X-Y plane.
            var w = WindField.WindXZ(wp);
            Vector3 lw = GlobalTransform.Basis.Inverse() * new Vector3(w.X, 0f, w.Y);
            if (lw.LengthSquared() > 1e-6f)
                _pivot.Rotation = new Vector3(0f, 0f, -Mathf.Atan2(lw.X, lw.Y));    // around local Z (pole); sign/axis verified by render
        }
    }
}
