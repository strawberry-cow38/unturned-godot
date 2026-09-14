using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // EMISSIVE LENSES (strawberry 2026-09-10: "add emissive lenses to both nightvisions the headlamp and the
    // flashlight. should only glow when they are on, in 3p too").
    //
    // Retail ships no emission map for any of these, so the mask is DERIVED from the albedo: keep the brightest
    // colour, black the rest. That derivation is the part that can silently be wrong -- a mask that lights nothing
    // renders identically to "the lamp is off", and a mask that lights everything renders as a glowing brick, and
    // neither throws. So this asserts the shape of the mask itself rather than that a call succeeded.
    public sealed class LensEmissionTests : GameTest
    {
        public override string Name => "content.lens_emission_mask";

        static (int lit, int total, Color first) Count(Texture2D t)
        {
            var img = (t as ImageTexture)?.GetImage();
            if (img == null) return (-1, 0, default);
            int lit = 0, total = img.GetWidth() * img.GetHeight();
            Color first = default;
            for (int y = 0; y < img.GetHeight(); y++)
                for (int x = 0; x < img.GetWidth(); x++)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 0.01f || c.G > 0.01f || c.B > 0.01f) { if (lit == 0) first = c; lit++; }
                }
            return (lit, total, first);
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            yield return Ticks(1);

            // ---- the three worn devices: 2x2 palettes, so exactly ONE of four texels is the lens
            foreach (var (id, name) in new[] { (334, "nightvision military"), (1044, "nightvision civilian"), (1199, "headlamp") })
            {
                var m = ClothingContent.LensMask(id);
                T.Check($"{name} ({id}) derives a lens mask", m != null);
                if (m == null) continue;
                var (lit, total, col) = Count(m);
                T.Check($"...{name} lights part of the texture, not none ({lit}/{total})", lit > 0);
                T.Check($"...and not all of it ({lit}/{total})", lit < total);
                GD.Print($"[lenstest] {name}: {lit}/{total} lit, first {col}");
            }

            // ⭐ THE ONE THAT CAUGHT A REAL BUG. The first version of the derivation kept the single brightest
            // PIXEL, which is correct for a 2x2 palette and nonsense for anything larger -- the flashlight's albedo
            // is 128x128 with a 784-texel bulb, and it would have lit exactly one of them. Every check above still
            // passed on that version, because 1 is both "> 0" and "< total".
            var torch = ClothingContent.EmissionMaskFrom("flashlight_albedo.png", "flashlight");
            T.Check("the flashlight derives a bulb mask", torch != null);
            if (torch != null)
            {
                var (lit, total, col) = Count(torch);
                GD.Print($"[lenstest] flashlight: {lit}/{total} lit, first {col}");
                T.Check($"...the WHOLE bulb is lit, not one texel of it ({lit} of {total})", lit > 100);
                T.Check($"...and it is still a mask, not the whole body ({lit} of {total})", lit < total / 4);
            }

            // ---- ⚠⚠ THE MASK IS NOT THE FEATURE. THE MATERIAL IS.
            //
            // Every check above passed, for months, while the headlamp and both nightvisions glowed over their
            // ENTIRE MODEL (strawberry 2026-09-13). Nothing above could see it: the masks really are correct,
            // and this file only ever looked at the mask IMAGE. The bug was one property on the material --
            // Godot's emission operator defaults to ADD, and the shader is
            //     EMISSION = (emission_color + emission_texture) * emission_energy
            // so a WHITE emission colour emits at full energy through the mask's BLACK texels. The mask was
            // computed perfectly and then combined with the wrong operator.
            //
            // So this builds the real material through the real call and asserts the thing that was wrong. A
            // check on the mask can never reject this bug; only a check on the material can.
            // ⚠ A BARE `new RiggedCharacter()` HAS NO SKELETON, and AttachGear returns immediately without one --
            // so the glasses never attach and DebugGlassesMaterial is null before a single mask check runs.
            // Skeleton is only ever set by BuildFrom, which is what Build() ends at. Same call the real clothing
            // path uses (ClothingTests builds its body this way too).
            var rig = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f));
            if (rig == null) { T.Fail("the player rig loads"); yield break; }
            World.AddChild(rig);
            yield return Ticks(2);
            var lensTex = ClothingContent.LensMask(1199);   // the headlamp, which master saw glowing whole
            rig.AttachGlasses(new BoxMesh { Size = Vector3.One * 0.2f }, null, Vector3.Zero, lensTex);
            yield return Ticks(1);
            var gm = rig.DebugGlassesMaterial();
            T.Check("the glasses material exists", gm != null);
            if (gm != null)
            {
                T.Check($"...it has the lens mask bound ({gm.EmissionTexture != null})", gm.EmissionTexture != null);
                T.Check($"⭐ ...and MULTIPLIES it rather than ADDING ({gm.EmissionOperator})",
                        gm.EmissionOperator == BaseMaterial3D.EmissionOperatorEnum.Multiply);
                // The control for WHY multiply is required: the emission colour is white. Under Add, white is
                // exactly what leaks through the black texels -- so if someone later "fixes" this by making the
                // colour black instead, this check tells them the operator still has to be right.
                T.Check($"...with a white emission colour, which is only safe under Multiply ({gm.Emission})",
                        gm.Emission.R > 0.9f && gm.Emission.G > 0.9f && gm.Emission.B > 0.9f);
            }
            if (GodotObject.IsInstanceValid(rig)) rig.QueueFree();

            // ---- CONTROL: this is opt-in. Without it, "derives a mask" would pass on a rule that lit the
            // brightest patch of every hat in the game.
            int optedOut = 0;
            foreach (var id in new[] { 3, 209, 253 })   // hoodie, cargo pants, alicepack
                if (ClothingContent.LensMask(id) == null) optedOut++;
            T.Check($"ordinary garments get NO lens mask ({optedOut}/3 correctly null)", optedOut == 3);
            yield break;
        }
    }
}
