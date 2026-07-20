using Godot;

namespace UnturnedGodot
{
    // A drifting wind field: a FastNoiseLite noise map sampled at a turbine's world X/Z, scrolling over time so the
    // gust pattern crawls across the map like weather fronts (master's idea). SampleWind returns a 0..1 local strength.
    // Cheap + stateless: every turbine just samples its own spot, no per-turbine bookkeeping.
    public static class WindField
    {
        static FastNoiseLite _noise;
        const float DriftX = 2.5f, DriftZ = 1.2f;   // m/s the gust pattern crawls across the map (a slow weather drift)
        const float Freq = 0.010f;                  // spatial scale of gusts (~100 m per feature) -> nearby turbines correlate, far ones differ

        static FastNoiseLite Noise() => _noise ??= new FastNoiseLite { Frequency = Freq, Seed = 1337 };   // default NoiseType (smooth simplex) is ideal for a wind field

        // 0..1 wind strength at a world position, drifting over time. Remapped so there's usually a light breeze with
        // occasional calms + gusts (the raw Perlin is centred on 0.5).
        public static float? TestWind;   // L1: force a fixed wind (null = live noise). Set + cleared by power.wind_turbine.
        public static float SampleWind(Vector3 worldPos)
        {
            if (TestWind.HasValue) return TestWind.Value;
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            float n = Noise().GetNoise2D(worldPos.X + t * DriftX, worldPos.Z + t * DriftZ);   // -1..1
            return Mathf.Clamp(0.5f + 0.65f * n, 0f, 1f);                                      // -> 0..1, slightly gusty
        }
    }
}
