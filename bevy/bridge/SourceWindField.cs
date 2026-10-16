// Noise path transcribed from FastNoiseLite (MIT License, Copyright(c) 2023 Jordan Peck, Contributors) via the
// fastnoise-lite 1.1.1 Rust crate; matches Godot 4.6 FastNoiseLite defaults.
using System.Globalization;

namespace UnturnedBevy;

/// <summary>Server-side port of game/WindField.cs SampleWind. Godot FastNoiseLite defaults (TYPE_SIMPLEX_SMOOTH =
/// OpenSimplex2S, FBm fractal, lacunarity 2, gain 0.5, weighted strength 0, no domain warp) with the source's Seed
/// 1337, Frequency UG_WINDFREQ (default 0.0025) and FractalOctaves (int)UG_WINDOCT (default 2). Time is the caller's
/// server seconds instead of Godot's process uptime. The source's TestWind/UG_WIND override is applied by the
/// caller; GraphicsOptions.Wind is a client display option and cannot zero authoritative turbine output. Only the 2D OpenSimplex2S + FBm path is ported, in f32 like the crate's default (non-f64) build.</summary>
public sealed class SourceWindField
{
    public const float MaxAmbient = 0.8f, DriftX = 2.5f, DriftZ = 1.2f;
    const int Seed = 1337, PrimeX = 501125321, PrimeY = 1136930381;
    const float Lacunarity = 2f, Gain = 0.5f, WeightedStrength = 0f;
    readonly float _frequency, _bounding;
    readonly int _octaves;

    public SourceWindField()
    {
        _frequency = EnvF("UG_WINDFREQ", 0.0025f);
        _octaves = (int)EnvF("UG_WINDOCT", 2f);
        float amp = Math.Abs(Gain), ampFractal = 1f;   // calculate_fractal_bounding
        for (int i = 1; i < _octaves; i++) { ampFractal += amp; amp *= Gain; }
        _bounding = 1f / ampFractal;
    }

    static float EnvF(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable(n), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var v) ? v : d;

    /// <summary>Source SampleWind minus TestWind/GraphicsOptions: a 0..MaxAmbient ambient breeze drifting with
    /// <paramref name="seconds"/>, screen-blended up toward 1 by the active weather's wind.</summary>
    public float Sample(float x, float z, float seconds, float weatherWind)
    {
        float n = Noise(x + seconds * DriftX, z + seconds * DriftZ);
        float wind = Math.Clamp(0.5f + 0.65f * n, 0f, MaxAmbient);
        if (weatherWind > 0.001f) wind += weatherWind * (1f - wind);
        return wind;
    }

    /// <summary>== Godot FastNoiseLite.GetNoise2D(x, y) for the configured noise (get_noise_2d, FBm branch).</summary>
    public float Noise(float x, float y)
    {
        // transform_noise_coordinate_2d: frequency, then the OpenSimplex2/2S F2 skew
        x *= _frequency; y *= _frequency;
        const float sqrt3 = 1.7320508075688772935274463415059f;
        float f2 = 0.5f * (sqrt3 - 1f), t = (x + y) * f2;
        x += t; y += t;
        // gen_fractal_fbm_2d
        int seed = Seed;
        float sum = 0f, amp = _bounding;
        for (int o = 0; o < _octaves; o++)
        {
            float noise = OpenSimplex2S(seed, x, y);
            seed++;
            sum += noise * amp;
            amp *= Lerp(1f, Math.Min(noise + 1f, 2f) * 0.5f, WeightedStrength);
            x *= Lacunarity; y *= Lacunarity;
            amp *= Gain;
        }
        return sum;
    }

    static float Lerp(float a, float b, float t) => a + t * (b - a);
    static int FastFloor(float f) => f >= 0f ? (int)f : (int)f - 1;
    static int Hash(int seed, int xPrimed, int yPrimed) => unchecked((seed ^ xPrimed ^ yPrimed) * 0x27d4eb2d);

    static float GradCoord(int seed, int xPrimed, int yPrimed, float xd, float yd)
    {
        int hash = Hash(seed, xPrimed, yPrimed);
        hash ^= hash >> 15;
        hash &= 127 << 1;
        return xd * Gradients2D[hash] + yd * Gradients2D[hash | 1];
    }

    // single_open_simplex_2s_2d (the skew already happened in Noise)
    static float OpenSimplex2S(int seed, float x, float y)
    {
        unchecked
        {
            const float sqrt3 = 1.7320508075688772935274463415059f;
            float g2 = (3f - sqrt3) / 6f;
            int i = FastFloor(x), j = FastFloor(y);
            float xi = x - i, yi = y - j;
            i *= PrimeX; j *= PrimeY;
            int i1 = i + PrimeX, j1 = j + PrimeY;

            float t = (xi + yi) * g2;
            float x0 = xi - t, y0 = yi - t;
            float a0 = (2f / 3f) - x0 * x0 - y0 * y0;
            float value = (a0 * a0) * (a0 * a0) * GradCoord(seed, i, j, x0, y0);

            float a1 = (2f * (1f - 2f * g2) * (1f / g2 - 2f)) * t + ((-2f * (1f - 2f * g2) * (1f - 2f * g2)) + a0);
            float x1 = x0 - (1f - 2f * g2), y1 = y0 - (1f - 2f * g2);
            value += (a1 * a1) * (a1 * a1) * GradCoord(seed, i1, j1, x1, y1);

            // Nested conditionals were faster than compact bit logic/arithmetic (upstream).
            float xmyi = xi - yi;
            if (t > g2)
            {
                if (xi + xmyi > 1f) value += Corner(seed, i + (PrimeX << 1), j + PrimeY, x0 + (3f * g2 - 2f), y0 + (3f * g2 - 1f));
                else value += Corner(seed, i, j + PrimeY, x0 + g2, y0 + (g2 - 1f));
                if (yi - xmyi > 1f) value += Corner(seed, i + PrimeX, j + (PrimeY << 1), x0 + (3f * g2 - 1f), y0 + (3f * g2 - 2f));
                else value += Corner(seed, i + PrimeX, j, x0 + (g2 - 1f), y0 + g2);
            }
            else
            {
                if (xi + xmyi < 0f) value += Corner(seed, i - PrimeX, j, x0 + (1f - g2), y0 - g2);
                else value += Corner(seed, i + PrimeX, j, x0 + (g2 - 1f), y0 + g2);
                if (yi < xmyi) value += Corner(seed, i, j - PrimeY, x0 - g2, y0 - (g2 - 1f));
                else value += Corner(seed, i, j + PrimeY, x0 + g2, y0 + (g2 - 1f));
            }
            return value * 18.24196194486065f;
        }
    }

    // Upstream's repeated `a = 2/3 - x² - y²; if a > 0 { value += a⁴ * grad }` corner term.
    static float Corner(int seed, int xPrimed, int yPrimed, float x2, float y2)
    {
        float a2 = (2f / 3f) - x2 * x2 - y2 * y2;
        return a2 > 0f ? (a2 * a2) * (a2 * a2) * GradCoord(seed, xPrimed, yPrimed, x2, y2) : 0f;
    }

    static readonly float[] Gradients2D =
    {
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.38268343236509f,   0.923879532511287f,  0.923879532511287f,  0.38268343236509f,   0.923879532511287f, -0.38268343236509f,   0.38268343236509f,  -0.923879532511287f,
        -0.38268343236509f,  -0.923879532511287f, -0.923879532511287f, -0.38268343236509f,  -0.923879532511287f,  0.38268343236509f,  -0.38268343236509f,   0.923879532511287f,
    };
}
