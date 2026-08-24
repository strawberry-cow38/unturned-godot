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
        const float Freq = 0.0025f;                 // BIG fat regional blobs (~400 m; master) -> whole neighbourhoods share wind, distant regions differ

        static FastNoiseLite Noise() => _noise ??= new FastNoiseLite
        {
            Frequency = EnvF("UG_WINDFREQ", Freq), Seed = 1337, FractalOctaves = (int)EnvF("UG_WINDOCT", 2f),   // few octaves = big smooth blobs, no fine detail (default smooth-simplex)
        };
        static float EnvF(string n, float d) => float.TryParse(System.Environment.GetEnvironmentVariable(n), out var v) ? v : d;

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

        public static float? TestAngle;   // L1: force a fixed wind bearing (null = live)
        // Which way the wind BLOWS, as a bearing in radians in the world XZ plane. The prevailing direction is the
        // gust-drift bearing; a per-region noise offset (so distant flags differ) + a slow global swing make it shift.
        public static float WindAngle(Vector3 worldPos)
        {
            if (TestAngle.HasValue) return TestAngle.Value;
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            float baseAng = Mathf.Atan2(DriftZ, DriftX);                                        // prevailing bearing
            float region = Noise().GetNoise2D(worldPos.X * 0.5f + 5000f, worldPos.Z * 0.5f + 5000f);   // -1..1, decorrelated from strength
            return baseAng + region * 0.8f + 0.3f * Mathf.Sin(t * 0.06f);                       // ±~46 deg region swing + a slow global drift
        }

        // Unit XZ vector the wind blows TOWARD (a flag streams this way from its pole).
        public static Vector2 WindXZ(Vector3 worldPos) { float a = WindAngle(worldPos); return new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }
    }
}
