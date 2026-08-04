using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE COMPOSITE SCREEN TEXTURE: the picture with a blanking bar under it, in colour and desaturated.
    //
    // Two of master's asks land in one texture:
    //   - "with the vertical scroll, should have a small blank screen color horizontal zone between the 'two' test
    //     pattern displays" -- the vertical blanking interval, which is what you see at the seam of a rolling picture.
    //   - "when we do the beam collapse for turning off, change the picture to monochrome".
    //
    // Baking the bar into the texture is what makes the roll correct for free: the slip is a UV offset, so a bar that
    // lives in UV space scrolls with the picture by construction. A separately-animated quad would have to be kept in
    // step with the offset by hand, and the failure -- bar and seam drifting apart -- only appears mid-roll, for under
    // a second, at random, which is close to untestable and easy to never notice.
    //
    // The load-bearing number is the WINDOW. Uv1Scale has to be exactly the picture's share of the composite: too big
    // and every television permanently shows a slice of blanking at the bottom; too small and the picture is cropped.
    // Both are static, both look like a texture authoring mistake rather than a UV one.
    public sealed class TVCompositeTests : GameTest
    {
        public override string Name => "tv.screen_composite";

        public override IEnumerable<Step> Run()
        {
            // A stand-in "pattern": 8x64, three saturated bars over a black field. Real SMPTE would do, but a
            // synthetic one makes the desaturation checks exact rather than approximate -- and the Television meshes
            // are not on this box anyway, so nothing here can come from a real device.
            const int W = 8, H = 64;
            var src = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    src.SetPixel(x, y, y < 20 ? Colors.Red : y < 40 ? new Color(0f, 1f, 0f) : new Color(0f, 0f, 1f));
            var pattern = ImageTexture.CreateFromImage(src);

            const float glassL = 53f / 255f;                     // Television_1's real screen texel, rgb 53,53,53
            var glass = new Color(glassL, glassL, glassL);
            var (colour, mono, frac) = TVDevice.ScreenTextures(pattern, glass);

            T.Check("a composite is produced", colour != null && mono != null);
            T.Check($"...and a null pattern degrades to no texture rather than throwing ({TVDevice.ScreenTextures(null, Colors.White).Colour == null})",
                TVDevice.ScreenTextures(null, Colors.White).Colour == null);

            var ci = colour.GetImage();
            int ch = ci.GetHeight();
            T.Check($"the composite is TALLER than the picture ({ch} vs {H}) -- the extra rows are the blanking bar", ch > H);

            // THE WINDOW. Uv1Scale.y is set from this, so it has to be the picture's exact share. A value that is
            // merely close leaves a permanent sliver of bar on every screen in the map.
            T.Check($"the window is exactly the picture's share of it ({frac:0.0000} vs {(float)H / ch:0.0000})",
                Mathf.IsEqualApprox(frac, (float)H / ch, 1e-5f));
            T.Check($"...and the bar is a SMALL zone, not half the screen ({(1f - frac) * 100f:0.0}% of the composite)",
                1f - frac > 0.02f && 1f - frac < 0.20f);

            // The picture survives the copy intact -- a composite that quietly rescales or reorders the source would
            // still pass every size check above.
            T.Check($"row 0 of the picture is untouched ({ci.GetPixel(0, 0)})", ci.GetPixel(0, 0).IsEqualApprox(Colors.Red));
            T.Check($"...and its last row too ({ci.GetPixel(0, H - 1)})", ci.GetPixel(0, H - 1).IsEqualApprox(new Color(0f, 0f, 1f)));

            // The bar is the GLASS colour (master: "blank screen color"), not black and not white. Black would read as
            // a gap in the set; white as a flash.
            var barPix = ci.GetPixel(0, ch - 1);
            T.Check($"the bar is the tube's own glass colour ({barPix})",
                Mathf.IsEqualApprox(barPix.R, glassL, 0.01f) && Mathf.IsEqualApprox(barPix.G, glassL, 0.01f) && Mathf.IsEqualApprox(barPix.B, glassL, 0.01f));
            T.Check($"...opaque, so it covers rather than tints ({barPix.A:0.00})", barPix.A > 0.99f);
            bool wholeBar = true;
            for (int y = H; y < ch; y++) if (!Mathf.IsEqualApprox(ci.GetPixel(0, y).R, glassL, 0.01f)) wholeBar = false;
            T.Check("...and every row of it, not just the last", wholeBar);

            // ---- MONOCHROME. Same geometry, no colour.
            var mi = mono.GetImage();
            T.Check($"the mono copy is the same size ({mi.GetWidth()}x{mi.GetHeight()})",
                mi.GetWidth() == ci.GetWidth() && mi.GetHeight() == ci.GetHeight());
            bool grey = true;
            for (int y = 0; y < H; y++)
            {
                var p = mi.GetPixel(0, y);
                if (!Mathf.IsEqualApprox(p.R, p.G, 1e-3f) || !Mathf.IsEqualApprox(p.G, p.B, 1e-3f)) grey = false;
            }
            T.Check("every picture row of the mono copy is grey", grey);

            // Rec.709 luma, not a channel average. This is the check with teeth: a flat (R+G+B)/3 makes pure red,
            // green and blue land on the SAME grey, so the SMPTE bars merge into one block the instant the set is
            // switched off -- and "the collapse looks like a grey smear" would be blamed on the collapse, not on the
            // desaturation. Under 709 they are 0.21 / 0.72 / 0.07 and stay legible.
            float r = mi.GetPixel(0, 0).R, g = mi.GetPixel(0, 25).R, b = mi.GetPixel(0, 50).R;
            T.Check($"...and the bars stay DISTINCT under it (r {r:0.00} / g {g:0.00} / b {b:0.00})",
                Mathf.Abs(r - g) > 0.1f && Mathf.Abs(g - b) > 0.1f && Mathf.Abs(r - b) > 0.1f);
            T.Check($"...specifically Rec.709 luma, so green reads brightest ({g:0.00} > {r:0.00} > {b:0.00})",
                g > r && r > b);
            T.Check($"the mono copy keeps the same glass bar ({mi.GetPixel(0, ch - 1)})",
                Mathf.IsEqualApprox(mi.GetPixel(0, ch - 1).R, glassL, 0.01f));

            // Cached per glass colour: every CRT on the map shares one pair, and the map has a lot of televisions.
            var again = TVDevice.ScreenTextures(pattern, glass);
            T.Check("asking twice returns the SAME textures rather than rebuilding per set",
                ReferenceEquals(again.Colour, colour) && ReferenceEquals(again.Mono, mono));
            // ...but a different glass colour is a different bar, so it must not be served the cached one. The two TVs
            // have different screen texels (CRT 53, flatscreen 39), so this is not hypothetical.
            var other = TVDevice.ScreenTextures(pattern, new Color(39f / 255f, 39f / 255f, 39f / 255f));
            T.Check("...and a set with a different screen texel gets its own",
                !ReferenceEquals(other.Colour, colour)
                && Mathf.IsEqualApprox(other.Colour.GetImage().GetPixel(0, ch - 1).R, 39f / 255f, 0.01f));

            yield break;
        }
    }
}
