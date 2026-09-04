using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>WET PROPS (strawberry 2026-09-04: "building roofs should get wetness and ripple effects. and all the road
    /// props should get wetness and ripples too"). Wraps a prop's StandardMaterial3D in the wet_surface shader so its
    /// up-facing surfaces darken, sheen and show raindrop rings in the rain (roofed parts stay dry via the roof map).
    /// Only OPAQUE, non-emissive materials are wrapped -- alpha-scissor cutouts (fences, leaves), glass and lit lenses
    /// keep their own material. The original material rides along as metadata so the code that reads a prop's albedo
    /// (break-effect tint, lamp fixtures, the well shaft) still gets a StandardMaterial3D through BaseOf.</summary>
    public static class WetSurface
    {
        public static bool Enabled = System.Environment.GetEnvironmentVariable("UG_WETPROPS") != "0";
        const string BaseMeta = "wet_base";
        static Shader _shader;
        static readonly Dictionary<ulong, ShaderMaterial> _cache = new();   // one wet material per base material (props share materials by name)

        public static bool Eligible(StandardMaterial3D m) =>
            m != null && m.Transparency == BaseMaterial3D.TransparencyEnum.Disabled && !m.EmissionEnabled && m.AlbedoColor.A >= 0.999f;

        public static Material Wrap(StandardMaterial3D m)
        {
            if (!Enabled || !Eligible(m)) return m;
            ulong id = m.GetInstanceId();
            if (_cache.TryGetValue(id, out var have)) return have;
            RainSystem3D.EnsureGlobals();   // the shader links the rain globals -- they must exist before it compiles
            _shader ??= GD.Load<Shader>("res://content/wet_surface.gdshader");
            var sm = new ShaderMaterial { Shader = _shader };
            var c = m.AlbedoColor;
            bool tex = m.AlbedoTexture != null;
            bool nomip = m.TextureFilter == BaseMaterial3D.TextureFilterEnum.Nearest;   // palette textures: nearest, NO mipmaps (cells would average to black)
            if (tex) sm.SetShaderParameter(nomip ? "albedo_tex_nomip" : "albedo_tex", m.AlbedoTexture);
            sm.SetShaderParameter("use_tex", tex && !nomip);
            sm.SetShaderParameter("use_nomip", tex && nomip);
            sm.SetShaderParameter("use_vcol", m.VertexColorUseAsAlbedo);
            sm.SetShaderParameter("tint", new Color(c.R, c.G, c.B, 1f));
            sm.SetShaderParameter("dry_albedo", new Vector3(c.R, c.G, c.B));
            sm.SetShaderParameter("dry_roughness", m.Roughness);
            sm.SetShaderParameter("impact_amount", 1.0f);
            sm.SetShaderParameter("splash_scale", 1.0f);
            sm.SetMeta(BaseMeta, m);
            _cache[id] = sm;
            return sm;
        }

        /// <summary>The StandardMaterial3D behind a prop material: the material itself, or the base a wet wrapper was made from.</summary>
        public static StandardMaterial3D BaseOf(Material m) =>
            m as StandardMaterial3D ?? (m is ShaderMaterial sm && sm.HasMeta(BaseMeta) ? sm.GetMeta(BaseMeta).As<StandardMaterial3D>() : null);
    }
}
