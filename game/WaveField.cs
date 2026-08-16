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
        public const float SwellAmp    = 0.5f;    // metres of vertical swell
        public const float SwellDirDeg = 30.0f;
        public const float SwellFu     = 0.09f;   // freq along travel
        public const float SwellFw     = 0.030f;  // freq along crest (fu/fw = 3:1)
        public const float SwellSpeed  = 3.0f;

        // sin-hash value-noise fbm -- identical formula to the shader's hashv/vnoise/fbm3
        static float Hashv(float ix, float iz)
        {
            double s = Math.Sin(ix * 127.1 + iz * 311.7) * 43758.5453;
            return (float)(s - Math.Floor(s));
        }
        static float Vnoise(float x, float z)
        {
            float ix = MathF.Floor(x), iz = MathF.Floor(z), fx = x - ix, fz = z - iz;
            float u = fx * fx * (3f - 2f * fx), v = fz * fz * (3f - 2f * fz);
            float a = Hashv(ix, iz), b = Hashv(ix + 1f, iz), c = Hashv(ix, iz + 1f), d = Hashv(ix + 1f, iz + 1f);
            return a * (1 - u) * (1 - v) + b * u * (1 - v) + c * (1 - u) * v + d * u * v;
        }
        static float Fbm3(float x, float z)
        {
            float s = 0f, a = 0.5f;
            for (int i = 0; i < 3; i++) { s += a * (Vnoise(x, z) - 0.5f); x *= 2.03f; z *= 2.03f; a *= 0.5f; }
            return s / 0.4375f;   // -> ~[-1, 1]
        }

        /// <summary>Normalised swell height ~[-1,1] at world XZ + phase-time (mirrors swell_at()).</summary>
        public static float SwellAt(float wx, float wz, float tphase)
        {
            float a = Mathf.DegToRad(SwellDirDeg); float c = MathF.Cos(a), s = MathF.Sin(a);
            float u = wx * c + wz * s;    // along travel
            float w = -wx * s + wz * c;   // along the crest line
            return Fbm3(u * SwellFu + tphase, w * SwellFw);
        }

        /// <summary>Engine time in seconds -- matches the shader's TIME.</summary>
        public static float Now() => (float)(Time.GetTicksMsec() / 1000.0);

        /// <summary>Vertical wave offset (m) at a world point, at the current engine time.</summary>
        public static float Height(float wx, float wz) => Height(wx, wz, Now());

        /// <summary>Vertical wave offset (m) at a world point, at an explicit time (deterministic).</summary>
        public static float Height(float wx, float wz, float timeSec)
            => SwellAt(wx, wz, timeSec * SwellSpeed * SwellFu) * SwellAmp;

        /// <summary>Wave-surface normal at a world point (finite-difference, matches the shader) -- for buoyancy tilt.</summary>
        public static Vector3 Normal(float wx, float wz, float timeSec)
        {
            float tp = timeSec * SwellSpeed * SwellFu;
            float h  = SwellAt(wx, wz, tp);
            float hx = SwellAt(wx + 1f, wz, tp);
            float hz = SwellAt(wx, wz + 1f, tp);
            return new Vector3((h - hx) * SwellAmp, 1f, (h - hz) * SwellAmp).Normalized();
        }
    }
}
