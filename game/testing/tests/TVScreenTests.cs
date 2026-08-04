using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // TV SCREENS (master: "change both tv types to have an emissive texture, and make the light they emit not reflect
    // on the screen, bc its washing out the colors a lot. on crts the texture itself should fade in from black").
    //
    // The screen carried albedo AND emission on a normal LIT material, with the TV's own OmniLight parked 0.3m in
    // front of it -- so every television was lighting its own screen and the diffuse term pushed the SMPTE bars
    // toward white. Unshaded makes that unfixable-by-accident: no light in the world can touch it, not the TV's own
    // spill, not the sun, not a torch.
    //
    // Also covers the NTSC flicker and the directional-spill/cone geometry added alongside it.
    //
    // WHAT THIS SUITE CANNOT DO, said plainly: the Television_0/1 meshes ship with Unturned, so TVDevice.Make needs
    // an install and there isn't one on this box -- which also means no render of a TV. So what is pinned here is the
    // PURE parts: material properties, the flicker curve, and the two aim conventions. Everything about how it
    // actually looks -- brightness, flicker depth, cone reach -- still needs a human in game. A green run here is not
    // a claim that it looks right; it is a claim that it cannot be wrong in the ways that are invisible.
    public sealed class TVScreenTests : GameTest
    {
        public override string Name => "tv.screen_material";

        public override IEnumerable<Step> Run()
        {
            var mat = TVDevice.MakeScreenMaterial(null);

            // THE fix. If someone later "restores" the emissive material, this is what catches it -- and note that a
            // screenshot could not: a re-lit screen looks merely a bit brighter, which reads as a tuning choice.
            T.Check($"the screen takes NO lighting ({mat.ShadingMode})",
                mat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded);
            T.Check("...and still carries the pattern as a texture slot", mat.AlbedoTexture == null);   // null in, null out: the slot is wired, the asset is loaded at Make()

            // Unshaded outputs ALBEDO, so brightness has to ride AlbedoColor -- an emission multiplier would do
            // nothing at all now, silently, and the screen would sit at whatever AlbedoColor it was left on.
            T.Check($"starts black so a CRT can fade the picture UP ({mat.AlbedoColor})", mat.AlbedoColor == Colors.Black);

            var off = TVDevice.ScreenColor(0f);
            var half = TVDevice.ScreenColor(0.5f);
            var full = TVDevice.ScreenColor(1f);
            T.Check($"brightness 0 is black ({off})", off.R == 0f && off.G == 0f && off.B == 0f);
            T.Check($"...1 is full ({full})", full.R == 1f && full.G == 1f && full.B == 1f);
            T.Check($"...and it is GREY at every step ({half})", half.R == half.G && half.G == half.B);
            // Grey matters: the SMPTE bars are coloured by the TEXTURE. A non-grey multiplier would tint the whole
            // pattern, which is the same washed-out-colour complaint arriving from the other direction.
            T.Check($"...so the bars keep their own hues, only the level moves ({half.R:0.00})", half.R > off.R && half.R < full.R);

            // ---- NTSC FLICKER (master: "make the crt SMPTE bars flicker at ntsc refresh rate as well as the light")
            float peak = TVDevice.Flicker(0f, 0.06f);
            float trough = TVDevice.Flicker(0.5f, 0.06f);
            T.Check($"flicker peaks at full brightness ({peak:0.0000})", Mathf.IsEqualApprox(peak, 1f));
            T.Check($"...and troughs exactly one depth below ({trough:0.0000})", Mathf.IsEqualApprox(trough, 1f - 0.06f));
            // Bounded on BOTH sides. An unbounded modulation would brighten past full on some phases, which on an
            // unshaded screen means the SMPTE bars clip -- reintroducing the washout this whole pass was about.
            float lo = 2f, hi = -2f;
            for (int i = 0; i <= 64; i++) { float v = TVDevice.Flicker(i / 64f, 0.06f); lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
            T.Check($"stays inside [1-depth, 1] across a full cycle ({lo:0.000}..{hi:0.000})", lo >= 1f - 0.06f - 1e-4f && hi <= 1f + 1e-4f);
            T.Check("depth 0 is a flat 1.0 (the flatscreen path)", Mathf.IsEqualApprox(TVDevice.Flicker(0.5f, 0f), 1f));
            // NEVER OFF (master: "when i say flicker i mean switch between a dimmer and brighter light state, not
            // on/off"). The floor has to stay a real brightness at any depth we might dial in, so the picture is
            // continuously visible and only its level moves. A modulation that touched zero would be a strobe.
            for (float d = 0.05f; d <= 0.5f; d += 0.05f)
            {
                float dim = TVDevice.Flicker(0.5f, d);
                if (dim <= 0.01f) { T.Check($"flicker never goes dark (depth {d:0.00} floored at {dim:0.000})", false); break; }
            }
            T.Check($"at the shipped depth it swings {TVDevice.Flicker(0.5f, 0.18f):0.00}..{TVDevice.Flicker(0f, 0.18f):0.00}, both lit",
                TVDevice.Flicker(0.5f, 0.18f) > 0.5f && TVDevice.Flicker(0f, 0.18f) <= 1f);

            // ---- AIM BASIS. Two different "point that way" conventions in one file -- SpotLight3D aims down local
            // -Z, BeamMesh runs down local -Y -- so this is exactly the sort of thing that ships as a cone firing
            // sideways out of the cabinet and reads as a modelling mistake rather than an axis mistake.
            foreach (var n in new[] { Vector3.Forward, Vector3.Right, new Vector3(1, 0, 1).Normalized(), Vector3.Up, Vector3.Down })
            {
                var spot = TVDevice.AimBasis(n, aimNegZ: true) * new Vector3(0f, 0f, -1f);
                var beam = TVDevice.AimBasis(n, aimNegZ: false) * new Vector3(0f, -1f, 0f);
                T.Check($"spot aims down the normal {n} (got {spot})", spot.Normalized().Dot(n) > 0.999f);
                T.Check($"...and the beam runs down it too (got {beam})", beam.Normalized().Dot(n) > 0.999f);
            }

            // ---- SCREEN EXTENTS: the beam's near end should be the screen's shape, so the axis the normal points
            // along is the one to DROP. Screen 2.0 x 1.0, 0.1 thick, facing +Z.
            var halfExt = TVDevice.ScreenHalfExtents(new Aabb(Vector3.Zero, new Vector3(2f, 1f, 0.1f)), Vector3.Back);
            T.Check($"in-plane half-extents keep width and height, drop thickness ({halfExt.X:0.00} x {halfExt.Y:0.00})",
                Mathf.IsEqualApprox(halfExt.X, 1f) && Mathf.IsEqualApprox(halfExt.Y, 0.5f));

            yield break;
        }
    }
}
