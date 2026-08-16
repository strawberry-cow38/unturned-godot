using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// CPU twin of <c>content/water.gdshader</c>'s wave function. The VISUAL waves live in the shader (GPU,
    /// purely cosmetic, vertex-displaced); this is the separate "physical" math layer that DUPLICATES the
    /// exact same summed-sine formula so buoyancy / swim surface can sample a matching wave height on the CPU
    /// without ever reading vertices back off the GPU. Keep the constants + octaves BYTE-FOR-BYTE in sync with
    /// the shader's wave_at()/uniforms. (master 2026-08-16: "waves as a shader, purely visual; the physical
    /// waves become a separate duplicate math layer".)
    /// </summary>
    public static class WaveField
    {
        // --- these MUST mirror the matching uniforms/const in content/water.gdshader ---
        public const float WaveLen   = 14.0f;   // longest swell wavelength (m)
        public const float WaveSpeed = 0.7f;
        public const float WaveAmp   = 0.45f;    // metres of vertical displacement

        /// <summary>Normalised wave height ~[-1,1] at world XZ and phase-time t (t = seconds * WaveSpeed). Mirrors the shader's wave_at().</summary>
        public static float Raw(float wx, float wz, float t)
        {
            float k = Mathf.Tau / WaveLen;   // 2*pi / len, == the shader's 6.2831853 / wave_len
            float h = 0f;
            h += Mathf.Sin((wx *  0.86f + wz *  0.51f) * k * 0.70f + t * 0.90f) * 0.50f;
            h += Mathf.Sin((wx * -0.31f + wz *  0.95f) * k * 1.07f + t * 1.15f) * 0.30f;
            h += Mathf.Sin((wx *  0.67f + wz * -0.74f) * k * 1.53f + t * 0.80f) * 0.20f;
            h += Mathf.Sin((wx * -0.99f + wz *  0.16f) * k * 1.97f + t * 1.33f) * 0.12f;
            return h / 1.12f;   // back to ~[-1, 1]
        }

        /// <summary>Engine time in seconds — matches the shader's TIME (both = ticks since engine start), so the CPU waves stay ~in phase with the drawn ones.</summary>
        public static float Now() => (float)(Time.GetTicksMsec() / 1000.0);

        /// <summary>Vertical wave offset (m, ~[-WaveAmp, WaveAmp]) at a world point, at the current engine time.</summary>
        public static float Height(float wx, float wz) => Raw(wx, wz, Now() * WaveSpeed) * WaveAmp;

        /// <summary>Vertical wave offset (m) at a world point, at an explicit time (deterministic — for tests / server tick).</summary>
        public static float Height(float wx, float wz, float timeSec) => Raw(wx, wz, timeSec * WaveSpeed) * WaveAmp;

        /// <summary>Wave-surface normal at a world point (finite-difference, matches the shader) — for buoyancy tilt / floater alignment.</summary>
        public static Vector3 Normal(float wx, float wz, float timeSec)
        {
            float t = timeSec * WaveSpeed;
            float h  = Raw(wx, wz, t);
            float hx = Raw(wx + 1f, wz, t);
            float hz = Raw(wx, wz + 1f, t);
            return new Vector3((h - hx) * WaveAmp, 1f, (h - hz) * WaveAmp).Normalized();
        }
    }
}
