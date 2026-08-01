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

        /// <summary>
        /// Seconds of drift the gust pattern has crawled. This is the whole determinism story:
        /// the noise MAP is identical everywhere (fixed seed + frequency), so two machines agree about wind
        /// exactly when they agree about this number.
        ///
        /// It used to be `Time.GetTicksMsec()`, i.e. each process's own wall clock since ITS launch — so a
        /// server and its clients sampled different wind at the same instant, and anything wind-driven
        /// (today: the wind turbine's output cap) silently disagreed in MP. Nothing about that was visible
        /// as a desync, because the wind value itself is never replicated or hashed.
        ///
        /// Hosts point this at the SIM TICK instead (`tick * SimClock.FixedDelta`), which every machine
        /// already agrees on — the server owns it and the client reads it off the applied snapshot, the
        /// same derivation `WorldClockReplication.TimeOfDayAt` uses for day/night. No new wire field and no
        /// protocol bump: the offset is a pure function of a tick both sides already have.
        ///
        /// Default stays the local wall clock so standalone tooling (`--windmap`) and any host that has not
        /// set it still behave exactly as before.
        /// </summary>
        public static System.Func<double> TimeSeconds;

        /// <summary>Point the drift at a tick source (a sim/session tick). The one call a host makes.</summary>
        public static void UseTickClock(System.Func<long> tickSource)
            => TimeSeconds = tickSource == null ? null : () => tickSource() * SDG.Unturned.SimClock.FixedDelta;

        /// <summary>Back to each process's own wall clock — test teardown and standalone tooling.</summary>
        public static void UseLocalClock() => TimeSeconds = null;

        /// <summary>The drift offset in seconds, from whichever clock the host installed.</summary>
        public static double DriftSeconds => TimeSeconds != null ? TimeSeconds() : Time.GetTicksMsec() / 1000.0;

        // 0..1 wind strength at a world position, drifting over time. Remapped so there's usually a light breeze with
        // occasional calms + gusts (the raw Perlin is centred on 0.5).
        public static float? TestWind;   // L1: force a fixed wind (null = live noise). Set + cleared by power.wind_turbine.
        public static float SampleWind(Vector3 worldPos)
        {
            if (TestWind.HasValue) return TestWind.Value;
            float t = (float)DriftSeconds;
            float n = Noise().GetNoise2D(worldPos.X + t * DriftX, worldPos.Z + t * DriftZ);   // -1..1
            return Mathf.Clamp(0.5f + 0.65f * n, 0f, 1f);                                      // -> 0..1, slightly gusty
        }
    }
}
