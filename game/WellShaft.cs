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
        public const float DiscZ = 0.04f;         // just above the ground plane the ring stands on, so it never z-fights the terrain
        public const float MaxViewDist = 10f;
        static Shader _shader;

        public static MeshInstance3D Make(MeshInstance3D ring, Color wall)
        {
            _shader ??= GD.Load<Shader>("res://content/well_depth.gdshader");
            if (_shader == null) return null;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            const int segs = 40;
            for (int i = 0; i < segs; i++)   // a fan in the XY plane facing +Z (object "up" for this Z-up prop); cull is off in the shader anyway
            {
                float a0 = Mathf.Tau * i / segs, a1 = Mathf.Tau * (i + 1) / segs;
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(0f, 0f, DiscZ));
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(Mathf.Cos(a1) * InnerRadius, Mathf.Sin(a1) * InnerRadius, DiscZ));
                st.SetNormal(Vector3.Back); st.AddVertex(new Vector3(Mathf.Cos(a0) * InnerRadius, Mathf.Sin(a0) * InnerRadius, DiscZ));
            }
            var mat = new ShaderMaterial { Shader = _shader };
            mat.SetShaderParameter("wall_color", wall);
            mat.SetShaderParameter("radius", InnerRadius + 0.02f);
            mat.SetShaderParameter("max_view_dist", MaxViewDist);
            var mi = new MeshInstance3D { Name = "WellShaft", Mesh = st.Commit(), MaterialOverride = mat, VisibilityRangeEnd = MaxViewDist, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            ring.AddChild(mi);   // child of the ring's own mesh node -> rides its placement transform in object space
            return mi;
        }

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
    }
}
