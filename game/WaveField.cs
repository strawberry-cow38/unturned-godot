using System;
using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// CPU twin of <c>content/water.gdshader</c>'s swell_at(). The VISUAL waves live in the shader (GPU, cosmetic,
    /// vertex-displaced); this DUPLICATES the exact same ANISOTROPIC noise swell so buoyancy / swim can sample a
    /// matching wave height on the CPU without GPU readback. The field is procedural noise sampled at a world point
    /// scrolled by time -- NOT a tiled loop, so it scrolls forever with nothing to repeat (matches the shader).
    /// Keep the noise + constants BYTE-FOR-BYTE in sync with the shader. (master 2026-08-16 "drawing board" redesign.)
    /// </summary>
    public static class WaveField
    {
        // --- must mirror the matching uniforms in content/water.gdshader ---
        public const float SwellAmp    = 0.5f;    // metres of vertical swell (CALM baseline; scaled by AmpScale)

        /// <summary>Weather scale on the swell height, mirroring the GPU's `swell_scale` global.
        ///
        /// Boats float on THIS, the water is drawn from the shader -- so if the two ever disagree the sea visibly
        /// roughens while the runabout keeps bobbing to the calm-water height. They are therefore written by ONE
        /// setter (RainSystem3D.SetWeatherSwell) and never assigned anywhere else.</summary>
        public static float AmpScale = 1f;
        public const float SwellDirDeg = 30.0f;
        public const float SwellFu     = 0.081f;  // freq along travel (matches the shader; waves ~10% bigger)
        /// <summary>fu/fw -- how stretched the swell is (3 = crests three times longer than wide). THE ONE OWNER
        /// of this number: pushed to the GPU global `swell_aniso` by RainSystem3D, so the sea the shader draws and
        /// the sea boats float on cannot disagree. UG_SWELLANISO tunes it without a rebuild, because "less
        /// stretched" is a look and the only instrument for a look is master's eye.</summary>
        public static float SwellAniso =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_SWELLANISO"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out float _an) && _an >= 0.25f ? _an : 3.0f;
        public static float SwellFw => SwellFu / System.Math.Max(SwellAniso, 0.25f);   // freq along crest

        /// <summary>How much the shore may bend the swell, 0..1. ⚠ DEFAULT 0: the first implementation sheared
        /// the wave field badly (rotating the sample basis per position multiplies a tiny direction change by a
        /// world-scale lever arm). UG_SHOREBEND=1 to test. Mirrors the GPU global `shore_bend`, one owner.</summary>
        public static float ShoreBend =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_SHOREBEND"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out float _sb) && _sb >= 0f ? _sb : 0f;
        public const float SwellSpeed  = 3.0f;

        // GRADIENT (Perlin) noise -- identical formula to the shader's hashv/grad2/gnoise (no axis-aligned cell
        // artifacts, so the CPU-sampled swell matches the smooth visual one).
        static float Hashv(float ix, float iz)
        {
            // precision-robust hash (Hoskins), matches the shader -- fract() bounds the input first, no sin degradation at large coords
            float ax = ix * 0.1031f; ax -= MathF.Floor(ax);
            float ay = iz * 0.1031f; ay -= MathF.Floor(ay);
            float az = ax;   // == fract(px * 0.1031) per the shader's vec3(p.xyx)
            float dt = ax * (ay + 33.33f) + ay * (az + 33.33f) + az * (ax + 33.33f);
            ax += dt; ay += dt; az += dt;
            float r = (ax + ay) * az;
            return r - MathF.Floor(r);
        }
        static (float, float) Grad2(float ix, float iz) { float h = Hashv(ix, iz) * 6.2831853f; return (MathF.Cos(h), MathF.Sin(h)); }
        static float Gnoise(float x, float z)
        {
            float ix = MathF.Floor(x), iz = MathF.Floor(z), fx = x - ix, fz = z - iz;
            float ux = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f), uz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);
            var ga = Grad2(ix, iz); var gb = Grad2(ix + 1f, iz); var gc = Grad2(ix, iz + 1f); var gd = Grad2(ix + 1f, iz + 1f);
            float a = ga.Item1 * fx + ga.Item2 * fz, b = gb.Item1 * (fx - 1f) + gb.Item2 * fz;
            float c = gc.Item1 * fx + gc.Item2 * (fz - 1f), d = gd.Item1 * (fx - 1f) + gd.Item2 * (fz - 1f);
            float ab = a + ux * (b - a), cd = c + ux * (d - c);
            return (ab + uz * (cd - ab)) * 0.8f + 0.5f;
        }
        static float Fbm3(float x, float z)
        {
            float s = 0f, a = 0.5f;
            for (int i = 0; i < 3; i++) { s += a * (Gnoise(x, z) - 0.5f); x *= 2.03f; z *= 2.03f; a *= 0.5f; }
            return s / 0.4375f;   // -> ~[-1, 1]
        }

        /// <summary>Normalised swell height ~[-1,1] at world XZ + phase-time (mirrors swell_at()).
        ///
        /// ⚠⚠ THE SHORE BEND IS MIRRORED HERE DELIBERATELY, line for line with the shader. Boats float on THIS.
        /// Bend the drawn waves toward the coast and leave this straight and the sea visibly turns while the
        /// runabout keeps bobbing to a swell running the old way -- the precise failure the include warns about
        /// twice, which is why the field is BAKED DATA both sides read rather than a constant copied by hand.</summary>
        public static float SwellAt(float wx, float wz, float tphase)
        {
            float a = Mathf.DegToRad(SwellDirDeg);
            float ox = MathF.Cos(a), oz = MathF.Sin(a);     // the open-ocean heading
            float dx = ox, dz = oz;
            if (ShoreField.Active != null)
            {
                ShoreField.Active.Sample(wx, wz, out var sdir, out float shoreness);
                shoreness = Mathf.Clamp(shoreness, 0f, 1f) * Mathf.Clamp(ShoreBend, 0f, 1f);
                dx = ox + (sdir.X - ox) * shoreness;        // mix(open, shore, shoreness) -- same as the shader
                dz = oz + (sdir.Y - oz) * shoreness;
                float dl = MathF.Sqrt(dx * dx + dz * dz);
                // ⚠ Two near-opposite headings can cancel; fall back to the open one rather than divide by ~0.
                if (dl > 1e-3f) { dx /= dl; dz /= dl; } else { dx = ox; dz = oz; }
            }
            float u = wx * dx + wz * dz;    // along travel
            float w = -wx * dz + wz * dx;   // along the crest line
            return Fbm3(u * SwellFu + tphase, w * SwellFw);
        }

        /// <summary>Engine time in seconds -- matches the shader's TIME.</summary>
        public static float Now() => (float)(Time.GetTicksMsec() / 1000.0);

        /// <summary>Vertical wave offset (m) at a world point, at the current engine time.</summary>
        public static float Height(float wx, float wz) => Height(wx, wz, Now());

        /// <summary>Vertical wave offset (m) at a world point, at an explicit time (deterministic).</summary>
        public static float Height(float wx, float wz, float timeSec)
            => SwellAt(wx, wz, timeSec * SwellSpeed * SwellFu) * SwellAmp * AmpScale;

        /// <summary>Wave-surface normal at a world point (finite-difference, matches the shader) -- for buoyancy tilt.</summary>
        public static Vector3 Normal(float wx, float wz, float timeSec)
        {
            float tp = timeSec * SwellSpeed * SwellFu;
            float h  = SwellAt(wx, wz, tp);
            float hx = SwellAt(wx + 1f, wz, tp);
            float hz = SwellAt(wx, wz + 1f, tp);
            return new Vector3((h - hx) * SwellAmp * AmpScale, 1f, (h - hz) * SwellAmp * AmpScale).Normalized();
        }
    }
}
