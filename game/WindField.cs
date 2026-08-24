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
        static bool _envRead;
        public static float SampleWind(Vector3 worldPos)
        {
            if (!_envRead) { _envRead = true; var e = System.Environment.GetEnvironmentVariable("UG_WIND"); if (float.TryParse(e, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) TestWind = w; }   // UG_WIND=0..1 forces a fixed strength (flag droop / turbine tests)
            if (TestWind.HasValue) return TestWind.Value;
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            float n = Noise().GetNoise2D(worldPos.X + t * DriftX, worldPos.Z + t * DriftZ);   // -1..1
            float ws = Mathf.Clamp(0.5f + 0.65f * n, 0f, MaxAmbient);                          // -> 0..MaxAmbient, slightly gusty
            return DownwashBoost(worldPos, ws);                                                // + heli rotor downwash (local, not baked into the map)
        }

        public const float MaxAmbient = 0.8f;      // master: cap the windmap's upper end so flags don't flap like crazy
        const float DownwashHeight = 30f;          // how far below a heli its rotor downwash reaches

        // HELI ROTOR DOWNWASH (master): a flying heli registers a local high-wind source that the foliage/flag sway feels,
        // WITHOUT baking anything into the windmap noise. Keyed by the heli instance id; the heli clears it on landing.
        static readonly System.Collections.Generic.Dictionary<ulong, (Vector3 Pos, float R, float S)> _downwash = new();
        public static void SetDownwash(ulong id, Vector3 pos, float radius, float strength) => _downwash[id] = (pos, radius, strength);
        public static void ClearDownwash(ulong id) => _downwash.Remove(id);

        // The max of the ambient wind and any heli downwash reaching `pos` (strongest right under the rotor, fading out + down).
        static float DownwashBoost(Vector3 pos, float baseW)
        {
            if (_downwash.Count == 0) return baseW;
            float w = baseW;
            foreach (var d in _downwash.Values)
            {
                float dy = d.Pos.Y - pos.Y;
                if (dy < 0f || dy > DownwashHeight) continue;       // only in the column BELOW the heli
                float horiz = new Vector2(pos.X - d.Pos.X, pos.Z - d.Pos.Z).Length();
                if (horiz > d.R) continue;
                w = Mathf.Max(w, d.S * (1f - horiz / d.R) * (1f - dy / DownwashHeight));
            }
            return w;
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

        // Unit XZ vector the wind blows TOWARD (a flag streams this way from its pole). Under a heli, the rotor downwash
        // blows RADIALLY OUTWARD from the rotor axis; blend the ambient bearing toward that as the downwash dominates.
        public static Vector2 WindXZ(Vector3 worldPos)
        {
            float a = WindAngle(worldPos);
            Vector2 amb = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            if (_downwash.Count == 0) return amb;
            float bestW = 0f; Vector2 bestDir = amb;
            foreach (var d in _downwash.Values)
            {
                float dy = d.Pos.Y - worldPos.Y;
                if (dy < 0f || dy > DownwashHeight) continue;
                Vector2 radial = new Vector2(worldPos.X - d.Pos.X, worldPos.Z - d.Pos.Z);
                float horiz = radial.Length();
                if (horiz > d.R || horiz < 0.05f) continue;
                float w = d.S * (1f - horiz / d.R) * (1f - dy / DownwashHeight);
                if (w > bestW) { bestW = w; bestDir = radial / horiz; }   // outward from the rotor
            }
            return bestW <= 0f ? amb : amb.Lerp(bestDir, Mathf.Clamp(bestW / 0.6f, 0f, 1f)).Normalized();
        }
    }
}
