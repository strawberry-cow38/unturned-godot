using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Hedges sway with the wind (master 2026-09-07: "add the grass wind effect to hedges").
    ///
    /// A hedge is the same thing grass is -- a sprawl of thin alpha-cutout planes -- and it stood dead still
    /// next to grass that moves, which is what reads as wrong.
    ///
    /// The sway itself is a vertex effect and a still frame cannot show it, so what is worth pinning here is
    /// everything AROUND it that can silently be wrong: that the right props are selected, that the material
    /// actually becomes the wind shader rather than staying flat, that the albedo survives the swap (a hedge
    /// that sways but renders blank is a worse bug than one that stands still), and that a prop with no
    /// texture degrades to plain instead of to invisible.</summary>
    public sealed class HedgeWindTests : GameTest
    {
        public override string Name => "world.hedge_wind";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            T.Check("hedges sway", WorldBuilder.SwayProp("Hedge_0"));
            // Not everything green: bushes and tree leaves already sway through ResourceField's own path, and
            // roads/props must not start waving. A predicate that said yes to everything would pass the check
            // above and wreck the world.
            T.Check("roads do not", !WorldBuilder.SwayProp("Road_0"));
            T.Check("buildings do not", !WorldBuilder.SwayProp("Apartment_0"));
            T.Check("a null name is not a crash", !WorldBuilder.SwayProp(null));

            // A prop material as MatFor builds one for a cutout: a texture plus an alpha scissor.
            var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            img.Fill(new Color(0.2f, 0.6f, 0.2f, 1f));
            var tex = ImageTexture.CreateFromImage(img);
            var flat = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.5f,
            };

            var swayed = WorldBuilder.SwayMat(flat);
            T.Check($"a hedge material becomes a ShaderMaterial (got {swayed?.GetType().Name})", swayed is ShaderMaterial);
            var sm = swayed as ShaderMaterial;
            T.Check("...running wind_sway.gdshader",
                    sm?.Shader != null && sm.Shader.ResourcePath.EndsWith("wind_sway.gdshader"));
            // THE ONE THAT MATTERS VISUALLY: the albedo has to come across, or the hedges go blank and the
            // "fix" is far worse than the thing it fixed.
            T.Check("...keeping its albedo texture",
                    sm != null && sm.GetShaderParameter("albedo_tex").As<Texture2D>() == tex);
            T.Check("...and its cutout threshold",
                    sm != null && Mathf.IsEqualApprox((float)sm.GetShaderParameter("alpha_scissor"), 0.5f));
            T.Check("...with alpha on, since a hedge is a cutout",
                    sm != null && (bool)sm.GetShaderParameter("use_alpha"));

            // No texture -> hand back the plain material rather than a shader sampling nothing.
            var bare = new StandardMaterial3D();
            T.Check("a textureless prop degrades to plain, not to invisible",
                    ReferenceEquals(WorldBuilder.SwayMat(bare), bare));
            yield break;
        }
    }
}
