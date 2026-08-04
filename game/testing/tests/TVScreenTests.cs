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

            // ---- SPOT AIM. SpotLight3D points down its local -Z, BeamMesh down its local -Y: two conventions for
            // "that way" in one file, which is how a cone ends up firing sideways out of the cabinet and reads as a
            // modelling mistake rather than an axis mistake. Roll is left arbitrary here on purpose -- a spot cone is
            // radially symmetric, so there is nothing on it for a roll to misalign.
            foreach (var n in new[] { Vector3.Forward, Vector3.Right, new Vector3(1, 0, 1).Normalized(), Vector3.Up, Vector3.Down })
            {
                var spot = TVDevice.AimBasis(n) * new Vector3(0f, 0f, -1f);
                T.Check($"spot aims down the normal {n} (got {spot})", spot.Normalized().Dot(n) > 0.999f);
            }

            // ---- BEAM ROLL (master: "i think flatscreen cone is 90 deg rotated roll"). He was right, and this is the
            // check that would have caught it. The old code took the beam's roll from a cross product against an
            // arbitrary seed vector and its half-extents from a SEPARATE rule, so "the wide axis of the beam" and "the
            // wide axis of the screen" only ever coincided by luck. On the CRT (0.85 x 0.79) luck is indistinguishable
            // from correct; on the flatscreen (3.55 x 1.8) the shaft comes out of the panel turned on its side.
            //
            // So the assertion is made in WORLD space off the real vertices: put the near ring where BeamFrame says,
            // and its footprint has to BE the screen's footprint. Testing the basis alone would not do it -- the bug
            // lived in the join between the basis and the extents, and each of those is defensible on its own.
            static Vector3 NearRingHalfExtents(Basis b, ArrayMesh m)
            {
                var verts = (Vector3[])m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
                Vector3 lo = new(9e9f, 9e9f, 9e9f), hi = new(-9e9f, -9e9f, -9e9f);
                foreach (var v in verts)
                {
                    if (Mathf.Abs(v.Y) > 0.01f) continue;               // the near ring only: t = 0, sitting on the glass
                    Vector3 w = b * v;
                    lo = new Vector3(Mathf.Min(lo.X, w.X), Mathf.Min(lo.Y, w.Y), Mathf.Min(lo.Z, w.Z));
                    hi = new Vector3(Mathf.Max(hi.X, w.X), Mathf.Max(hi.Y, w.Y), Mathf.Max(hi.Z, w.Z));
                }
                return (hi - lo) * 0.5f;
            }

            // A flatscreen -- deliberately NOT square -- in each of the three ways a prop's panel can face.
            foreach (var (nrm, size, where) in new[]
            {
                (Vector3.Back,  new Vector3(3.55f, 1.80f, 0.06f), "facing +Z"),
                (Vector3.Right, new Vector3(0.06f, 1.80f, 3.55f), "facing +X"),
                (Vector3.Up,    new Vector3(3.55f, 0.06f, 1.80f), "facing +Y"),
            })
            {
                var f = TVDevice.BeamFrame(new Aabb(-size * 0.5f, size), nrm);
                var beamMesh = StreetLight.BeamMesh(1f, f.HalfA, f.HalfB, 0f, keepRect: true, endScale: 1f);
                var got = NearRingHalfExtents(f.Basis, beamMesh);
                var want = size * 0.5f;
                var an = nrm.Abs();

                bool matches = true;
                for (int i = 0; i < 3; i++)
                {
                    float exp = an[i] > 0.5f ? 0f : want[i];             // the normal's own axis is the flat one
                    if (!Mathf.IsEqualApprox(got[i], exp, 0.01f)) matches = false;
                }
                T.Check($"beam footprint IS the screen {where} (want {want}, got {got})", matches);

                // TEETH. If the fixture were square, the check above would pass under a 90-degree roll and the suite
                // would report green on the exact bug it exists for. Assert the fixture can tell them apart.
                T.Check($"...and the fixture is lopsided enough to SEE a roll {where} ({f.HalfA:0.00} vs {f.HalfB:0.00})",
                    Mathf.Abs(f.HalfA - f.HalfB) > 0.4f);
                var run = (f.Basis * new Vector3(0f, -1f, 0f)).Normalized();
                T.Check($"...beam still runs down the normal {where} (got {run})", run.Dot(nrm) > 0.999f);
                // A mirrored basis would keep the footprint AND the aim and flip the gradient's winding, so pin the
                // handedness rather than trusting that the two checks above cover the frame.
                T.Check($"...right-handed and orthonormal {where} (det {f.Basis.Determinant():0.000})",
                    Mathf.IsEqualApprox(f.Basis.Determinant(), 1f, 0.001f));
            }

            // ---- CONE GRADIENT direction (master: "brighter toward the source"). BeamMesh maps v = t*0.5 with v=0
            //      at the SOURCE, and StreetLight's gradient rises with v -- faint at the lamp, dense at the ground,
            //      which its own comment records as deliberate. The TV needs the opposite, so it has its own texture.
            //      A gradient pointing the wrong way does not look broken, it looks "a bit dim at the screen", which
            //      is why this is asserted rather than eyeballed.
            var img = TVDevice.ConeGradient().GetImage();
            int h = img.GetHeight();
            float aSrc = img.GetPixel(0, 0).A;                        // v = 0   -> at the screen
            float aMid = img.GetPixel(0, h / 4).A;                    // v = 0.25
            float aFar = img.GetPixel(0, h / 2).A;                    // v = 0.5 -> far end of the shaft
            T.Check($"opaque at the screen ({aSrc:0.000})", aSrc > 0.99f);
            T.Check($"gone by the far end ({aFar:0.000})", aFar < 0.01f);
            T.Check($"...and monotonically fading between ({aSrc:0.00} > {aMid:0.00} > {aFar:0.00})", aSrc > aMid && aMid > aFar);
            // The whole ramp must fit in [0, 0.5]: CylinderMesh reserves the top half of UV space for caps, so a ramp
            // spread over [0,1] would put its midpoint past the end of the mesh and lose half the fade.
            bool tailZero = true;
            for (int y = h / 2; y < h; y++) if (img.GetPixel(0, y).A > 0.01f) tailZero = false;
            T.Check("the ramp fits inside the sampled half of UV space", tailZero);

            // ---- BEAM CROSS-SECTION stays rectangular and keeps the screen's aspect (master: "maintain a square
            //      shape"). Measured off the real vertices: the far ring's width:height must match the near ring's.
            static (float w, float hgt) RingAt(ArrayMesh m, float yTarget, float tol)
            {
                var verts = (Vector3[])m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
                float xmin = 9e9f, xmax = -9e9f, zmin = 9e9f, zmax = -9e9f;
                foreach (var v in verts)
                    if (Mathf.Abs(v.Y - yTarget) < tol)
                    { xmin = Mathf.Min(xmin, v.X); xmax = Mathf.Max(xmax, v.X); zmin = Mathf.Min(zmin, v.Z); zmax = Mathf.Max(zmax, v.Z); }
                return (xmax - xmin, zmax - zmin);
            }
            var tvBeam = StreetLight.BeamMesh(4f, 1.0f, 0.5f, 0f, keepRect: true, endScale: 2f);
            var near = RingAt(tvBeam, 0f, 0.01f);
            var far = RingAt(tvBeam, -4f, 0.01f);
            T.Check($"near ring is the screen's 2:1 ({near.w:0.00} x {near.hgt:0.00})", Mathf.IsEqualApprox(near.w / near.hgt, 2f, 0.02f));
            T.Check($"far ring keeps that aspect ({far.w:0.00} x {far.hgt:0.00})", Mathf.IsEqualApprox(far.w / far.hgt, 2f, 0.02f));
            T.Check($"...and is scaled up, not rounded off ({far.w / near.w:0.00}x wider)", far.w > near.w * 1.9f);

            // REGRESSION GUARD on the lamp, whose beam I had to touch to add those options: its default path must
            // still converge toward a circle of radius baseR, i.e. the far ring goes SQUARE regardless of the near
            // ring's aspect. If this ever reads 2:1, the streetlight silently inherited the TV's shape.
            var lampBeam = StreetLight.BeamMesh(4f, 1.0f, 0.5f, 1.5f);
            var lampFar = RingAt(lampBeam, -4f, 0.01f);
            T.Check($"lamp beam still rounds to baseR ({lampFar.w:0.00} x {lampFar.hgt:0.00})",
                Mathf.IsEqualApprox(lampFar.w / lampFar.hgt, 1f, 0.05f));

            yield break;
        }
    }
}
