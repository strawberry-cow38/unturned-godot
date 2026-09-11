using Godot;

namespace UnturnedGodot
{
    /// <summary>WHERE THE PUDDLES ACTUALLY ARE, on the CPU.
    ///
    /// This is a line-for-line mirror of `puddle_mask` in content/puddles.gdshaderinc -- the same two noise
    /// octaves, the same rising waterline, the same fade-in over the first 12% of the fill. It exists because
    /// the footstep splash had no way to ask: it tested "hard ground, open sky, and it has rained", which is
    /// the whole outdoors rather than the puddles in it (master 2026-09-11: "ONLY when walking over puddles
    /// themselves, not just wet surface"). The shader knows the answer and the sound could not hear it.
    ///
    /// ⚠ A SECOND IMPLEMENTATION OF A FIELD, which this project has been bitten by before -- the rain ring and
    /// its splashback were one loop for exactly this reason. It is unavoidable here (a fragment shader cannot
    /// be asked a question from C#), so the mitigation is that the constants are NOT retyped from memory: they
    /// are the four numbers in the include, named the same, and any change to that file has to come here. The
    /// one deliberate difference is the shoreline: the shader antialiases its edge over about one pixel of the
    /// depth field via fwidth(), which has no meaning for a point sample, so this takes the crisp centre of
    /// that band. A footstep within a few centimetres of a puddle's edge may disagree with what you see by the
    /// width of the edge itself, which is smaller than a boot.</summary>
    public static class PuddleField
    {
        // The include's own constants, same names. depth is ground HEIGHT: everything BELOW `line` holds water.
        const float LineLow = 0.10f, LineHigh = 0.46f;   // waterline at no rain -> at a full fill
        const float Octave0 = 0.19f, Octave1 = 0.62f;    // hollows a few metres across + a shoreline wobble
        const float Weight0 = 0.72f, Weight1 = 0.28f;
        const float FadeIn = 0.12f;                      // the first ~18 s of rain: faint as well as small

        static float Fract(float v) => v - Mathf.Floor(v);

        // pud_hash: fract(vec3(p.xyx) * 0.1031), p3 += dot(p3, p3.yzx + 33.33), fract((p3.x+p3.y)*p3.z)
        static float Hash(float px, float py)
        {
            float x = Fract(px * 0.1031f), y = Fract(py * 0.1031f), z = x;   // vec3(p.xyx) -> the third lane IS the first
            float d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f);
            x += d; y += d; z += d;
            return Fract((x + y) * z);
        }

        static void Grad(float ix, float iy, out float gx, out float gy)
        {
            float h = Hash(ix, iy) * 6.2831853f;
            gx = Mathf.Cos(h); gy = Mathf.Sin(h);
        }

        // pud_noise: gradient noise with the quintic fade, remapped to ~0..1 by *0.8 + 0.5
        static float Noise(float px, float py)
        {
            float ix = Mathf.Floor(px), iy = Mathf.Floor(py);
            float fx = px - ix, fy = py - iy;
            float ux = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
            float uy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
            Grad(ix, iy, out float ax, out float ay);
            Grad(ix + 1f, iy, out float bx, out float by);
            Grad(ix, iy + 1f, out float cx, out float cy);
            Grad(ix + 1f, iy + 1f, out float dx, out float dy);
            float a = ax * fx + ay * fy;
            float b = bx * (fx - 1f) + by * fy;
            float c = cx * fx + cy * (fy - 1f);
            float d = dx * (fx - 1f) + dy * (fy - 1f);
            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy) * 0.8f + 0.5f;
        }

        /// <summary>Is there standing water at this world XZ, 0..1, for a fill level of <paramref name="level"/>
        /// (the rain_puddle global -- the SLOW one, not rain_wetness). Upness and the distance fade are the
        /// shader's business: underfoot you are on the ground and standing on it.</summary>
        public static float MaskAt(float worldX, float worldZ, float level)
        {
            if (level <= 0.01f) return 0f;   // the include's early out, same threshold
            float depth = Noise(worldX * Octave0, worldZ * Octave0) * Weight0
                        + Noise(worldX * Octave1, worldZ * Octave1) * Weight1;
            float line = Mathf.Lerp(LineLow, LineHigh, Mathf.Clamp(level, 0f, 1f));
            if (depth >= line) return 0f;   // above the waterline = dry ground
            float t = Mathf.Clamp(level / FadeIn, 0f, 1f);
            return t * t * (3f - 2f * t);   // smoothstep(0, FadeIn, level)
        }

        /// <summary>Enough standing water here to splash through. The threshold is where the shader's own
        /// fade-in has brought the puddle up to something you can see rather than a faint stain.</summary>
        public static bool IsWet(float worldX, float worldZ, float level) => MaskAt(worldX, worldZ, level) > 0.5f;
    }
}
