using Godot;

namespace UnturnedGodot
{
    /// <summary>The "bottomless" well (strawberry 2026-09-04): a flat disc laid across the inside of Well_0's stone ring,
    /// in the prop's own object space, wearing content/well_depth.gdshader -- which ray-casts each pixel into an
    /// infinite cylinder so the shaft appears to drop away forever. The disc is not drawn past 10 m (both the
    /// shader's own discard and the node's visibility range), since at that distance it is a dark dot.</summary>
    public static class WellShaft
    {
        public const float InnerRadius = 0.76f;   // Well_0.obj: the ring's inner surface sits at r 0.763..0.80 (measured off the verts)
        public const float DiscZ = 0.30f;         // well up inside the ring (it is 1.25 tall): placed on a slope the terrain cuts through the ring's base, and 0.04 z-fought it (master 2026-09-04)
        public const float MaxViewDist = 10f;
        static Shader _shader;
        /// <summary>KILL SWITCH (2026-09-04): master saw GPU-driver timeouts (LiveKernelEvent 141) start the evening the shaft
        /// landed. Off by default until cleared; UG_WELLSHAFT=1 forces it on (renders / the A-B on master's machine).</summary>
        public static bool Enabled => GraphicsOptions.WellShaft || System.Environment.GetEnvironmentVariable("UG_WELLSHAFT") == "1";

        // The shader is UNSHADED and reads daylight from this global (DayNightCycle writes it every tick). Registered
        // before any shaft material exists -- see GrassDisplacers.EnsureGlobals for why the order matters.
        public const string DaylightParam = "well_daylight";
        static bool _globalsReady;
        public static void EnsureGlobals()
        {
            if (_globalsReady) return;
            _globalsReady = true;
            RenderingServer.GlobalShaderParameterAdd(DaylightParam, RenderingServer.GlobalShaderParameterType.Float, Variant.From(1.0f));
        }
        public static void SetDaylight(float v) { EnsureGlobals(); RenderingServer.GlobalShaderParameterSet(DaylightParam, Variant.From(Mathf.Clamp(v, 0f, 1f))); }

        public static MeshInstance3D Make(MeshInstance3D ring, Color wall)
        {
            if (!Enabled) return null;
            var mi = BuildDisc(wall, InnerRadius);
            if (mi == null) return null;
            ring.AddChild(mi);   // child of the ring's own mesh node -> rides its placement transform in object space
            return mi;
        }

        /// <summary>The disc + its ShaderMaterial, unattached. Shared by the world attach and the load-time warm draw.</summary>
        public static MeshInstance3D BuildDisc(Color wall, float radius)
        {
            EnsureGlobals();
            _shader ??= GD.Load<Shader>("res://content/well_depth.gdshader");
            if (_shader == null) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            const int segs = 40;
            for (int i = 0; i < segs; i++)   // a fan in the XY plane whose FRONT faces +Z (object "up" for this Z-up prop): counter-clockwise seen from +Z, since the shader culls back faces now
            {
                float a0 = Mathf.Tau * i / segs, a1 = Mathf.Tau * (i + 1) / segs;
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(0f, 0f, DiscZ));
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius, DiscZ));
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius, DiscZ));
            }
            var mat = new ShaderMaterial { Shader = _shader };
            mat.SetShaderParameter("wall_color", wall);
            mat.SetShaderParameter("radius", radius + 0.02f);
            return new MeshInstance3D { Name = "WellShaft", Mesh = st.Commit(), MaterialOverride = mat, VisibilityRangeEnd = MaxViewDist, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        }

        /// <summary>WARM DRAW (tinyclaw 2026-09-04: a brand-new shader's first draw is a synchronous pipeline compile; on a
        /// teleport, on top of streaming uploads, with the separate render thread mid-frame, that is a plausible GPU
        /// timeout). Draw the disc once, right in front of the camera, for a few frames after the world builds --
        /// the PowerNet wire-arrow prewarm pattern -- so the pipeline exists before the first well comes into view.</summary>
        /// <summary>The ring's stone colour, read off the prop atlas at the inner wall's UV swatch (Well_0 is flat-shaded:
        /// its stone faces map to a 7x7-texel patch around (0.754, 0.769)). Falls back to a mid grey.</summary>
        public static Color WallColor(Texture2D atlas)
        {
            try
            {
                var img = atlas?.GetImage();
                if (img == null) return new Color(0.42f, 0.40f, 0.37f);
                // OBJ vt has V from the BOTTOM, the image has rows from the top: flip. (Well_0's atlas is a 2x2 of flat colours;
                // the stone swatch is the grey one, the roof's the brown -- read the wrong row and the shaft goes brown.)
                int x = Mathf.Clamp((int)(0.754f * img.GetWidth()), 0, img.GetWidth() - 1), y = Mathf.Clamp((int)((1f - 0.769f) * img.GetHeight()), 0, img.GetHeight() - 1);
                return img.GetPixel(x, y);
            }
            catch { return new Color(0.42f, 0.40f, 0.37f); }
        }

        static bool _warmed;
        public static void WarmOnce(Node root)
        {
            if (_warmed || !Enabled || root == null) return;
            _warmed = true;
            root.AddChild(new WellShaftWarm());
        }
    }

    public sealed partial class WellShaftWarm : Node3D
    {
        int _frames = 4;
        MeshInstance3D _disc;
        public override void _Ready()
        {
            _disc = WellShaft.BuildDisc(new Color(0.42f, 0.40f, 0.37f), 0.25f);
            if (_disc == null) { QueueFree(); return; }
            _disc.VisibilityRangeEnd = 0f;   // the warm draw must not be range-culled
            AddChild(_disc);
        }
        public override void _Process(double delta)
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam != null) GlobalTransform = new Transform3D(cam.GlobalTransform.Basis, cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 1.5f);   // 1.5 m ahead, facing the camera (the disc's +Z normal toward it)
            if (--_frames <= 0) QueueFree();
        }
    }
}
