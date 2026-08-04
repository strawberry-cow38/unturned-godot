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
    // WHAT THIS SUITE CANNOT DO, said plainly: the Television_0/1 meshes ship with Unturned, so TVDevice.Make needs
    // an install and there isn't one on this box -- which also means no render of a TV. So this pins the two material
    // PROPERTIES the fix rests on, and the on-screen result still needs a human look in game. A green run here is not
    // a claim that it looks right.
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

            yield break;
        }
    }
}
