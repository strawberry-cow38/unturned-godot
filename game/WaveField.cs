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
        /// <summary>⭐ THE MASTER SWITCH FOR THE WHOLE WATER SYSTEM. Master 2026-10-09: "rip out our entire water
        /// system until we are left with a totally flat water plane with no effects."
        ///
        /// Flat = true zeroes the swell AT ITS SOURCE, and that is deliberately ONE place rather than three,
        /// because the swell has three consumers that must agree: this CPU twin (buoyancy, Terrain.WaterSurfaceY),
        /// the GPU `swell_scale` global that water.gdshader used to displace by, and rain_streak.gdshader, which
        /// reads the same surface height to know where a raindrop stops. Flattening the picture alone would have
        /// floated boats and landed rain on a swell nobody could see.
        ///
        /// Setting it false restores the wave FIELD; the surface EFFECTS (chop, foam, Fresnel, reflection) were
        /// removed from water.gdshader in the same commit and come back from git, not from this flag.</summary>
        public const bool Flat = true;

        // --- must mirror the matching uniforms in content/water.gdshader ---
        public const float SwellAmp    = 0.5f;    // metres of vertical swell (CALM baseline; scaled by AmpScale)

        /// <summary>Weather scale on the swell height, mirroring the GPU's `swell_scale` global.
        ///
        /// Boats float on THIS, the water is drawn from the shader -- so if the two ever disagree the sea visibly
        /// roughens while the runabout keeps bobbing to the calm-water height. They are therefore written by ONE
        /// setter (RainSystem3D.SetWeatherSwell) and never assigned anywhere else.</summary>
        /// ⚠ Defaults from Flat, not to 1: a harness that never runs RainSystem3D's setter would otherwise leave
        /// the twin at calm-swell amplitude, and Terrain.SwellReach (which reads this directly) would claim the
        /// sea can rise half a metre above a plane that cannot move.
        public static float AmpScale = Flat ? 0f : 1f;
        public const float SwellDirDeg = 30.0f;
        public const float SwellFu     = 0.081f;  // freq along travel (matches the shader; waves ~10% bigger)
        /// <summary>fu/fw -- how stretched the swell is (2 = crests twice as long as they are wide). THE ONE OWNER
        /// of this number: pushed to the GPU global `swell_aniso` by RainSystem3D, so the sea the shader draws and
        /// the sea boats float on cannot disagree. UG_SWELLANISO tunes it without a rebuild, because "less
        /// stretched" is a look and the only instrument for a look is master's eye.
        ///
        /// ⚠ 3.0 -> 2.0, 2026-10-04. Master asked for the sea to be "a lot less stretched on one axis", and
        /// the first pass only made the number TUNABLE -- it left the default at the value being complained
        /// about, so nothing changed for master at all unless they went and set an env var. A knob is not a change.
        /// ⭐ 2.0 rather than lower because 1.4 was rendered too: at 1.4 the crest foam breaks into scattered
        /// blobs and the swell stops reading as swell. 2 is visibly shorter-crested and still directional.</summary>
        public static float SwellAniso =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_SWELLANISO"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out float _an) && _an >= 0.25f ? _an : 2.0f;
        public static float SwellFw => SwellFu / System.Math.Max(SwellAniso, 0.25f);   // freq along crest

        /// <summary>How much the shore may bend the swell, 0..1 -- scales ShoreField's baked phase correction, so
        /// 0 reduces the phase to exactly <c>dot(wp, open)</c> and gives the sea as it was before the feature
        /// existed. Mirrors the GPU global `shore_bend`, one owner, UG_SHOREBEND to tune.
        ///
        /// ⚠ DEFAULT 1 since 2026-10-04. It shipped at 0 while master signed off on the look -- the FIRST
        /// implementation sheared the field badly ("whys it all scrunchy") and a feature that is ON and wrong
        /// costs more than one that is off. But master runs THIS branch, so an env-gated default meant they had
        /// to type a flag to see work that was reported as done: "why would u make it a separate launch command?
        /// just push to ur branch with new changes, simple." UG_SHOREBEND=0 still turns it off.</summary>
        public static float ShoreBend =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_SHOREBEND"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out float _sb) && _sb >= 0f ? _sb : 1f;
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
        /// twice, which is why the field is BAKED DATA both sides read rather than a constant copied by hand.
        ///
        /// ⭐⭐ WHAT IS READ IS A SCALAR PHASE, NOT A DIRECTION. The previous version mixed the open heading with
        /// a baked one and built the sample basis from the result; that multiplies the direction change by a
        /// world-coordinate lever arm and collapses the wavelength (see ShoreField's header). A phase correction
        /// added to the phase cannot do that -- there is no basis to rotate.</summary>
        public static float SwellAt(float wx, float wz, float tphase)
        {
            float a = Mathf.DegToRad(SwellDirDeg);
            float ox = MathF.Cos(a), oz = MathF.Sin(a);     // the open-ocean heading
            float u = wx * ox + wz * oz;                   // along travel -- the linear part, exact
            if (ShoreField.Active != null)
                u += Mathf.Clamp(ShoreBend, 0f, 1f) * ShoreField.Active.PhaseAt(wx, wz);
            // ⚠ The along-crest coordinate stays in the OPEN frame, matching the shader: bending it would need a
            // second (conjugate) field, and it only exists to break crests into finite ridges anyway.
            float w = -wx * oz + wz * ox;
            return Fbm3(u * SwellFu + tphase, w * SwellFw);
        }

        /// <summary>Engine time in seconds -- matches the shader's TIME.</summary>
        public static float Now() => (float)(Time.GetTicksMsec() / 1000.0);

        /// <summary>Vertical wave offset (m) at a world point, at the current engine time.</summary>
        public static float Height(float wx, float wz) => Height(wx, wz, Now());

        /// <summary>Vertical wave offset (m) at a world point, at an explicit time (deterministic).</summary>
        public static float Height(float wx, float wz, float timeSec)
            => Flat ? 0f : SwellAt(wx, wz, timeSec * SwellSpeed * SwellFu) * SwellAmp * AmpScale;

        /// <summary>Wave-surface normal at a world point (finite-difference, matches the shader) -- for buoyancy tilt.</summary>
        public static Vector3 Normal(float wx, float wz, float timeSec)
        {
            if (Flat) return Vector3.Up;
            float tp = timeSec * SwellSpeed * SwellFu;
            float h  = SwellAt(wx, wz, tp);
            float hx = SwellAt(wx + 1f, wz, tp);
            float hz = SwellAt(wx, wz + 1f, tp);
            return new Vector3((h - hx) * SwellAmp * AmpScale, 1f, (h - hz) * SwellAmp * AmpScale).Normalized();
        }
    }
}
