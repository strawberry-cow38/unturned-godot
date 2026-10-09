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
            if (!GraphicsOptions.Wind) return 0f;   // retail IsWindEnabled: no foliage sway / flag ripple
            if (!_envRead) { _envRead = true; var e = System.Environment.GetEnvironmentVariable("UG_WIND"); if (float.TryParse(e, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) TestWind = w; }   // UG_WIND=0..1 forces a fixed strength (flag droop / turbine tests)
            if (TestWind.HasValue) return TestWind.Value;
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            float n = Noise().GetNoise2D(worldPos.X + t * DriftX, worldPos.Z + t * DriftZ);   // -1..1
            float wind = Mathf.Clamp(0.5f + 0.65f * n, 0f, MaxAmbient);                        // -> 0..MaxAmbient, slightly gusty
            // WEATHER WIND (strawberry 2026-09-08: "varying degrees of wind"). WeatherType.WindMain has carried a
            // comment saying it "drives the port's WindField while active" since the port went in -- and nothing
            // outside the struct ever read it, so every weather blew at the same fair-weather breeze and the field
            // was decorative. WeatherManager pushes the ACTIVE type's wind here (already blend-scaled, 0 when clear).
            //
            // It raises the FLOOR rather than replacing the noise: a squall is windy EVERYWHERE, but it is still
            // gusty -- the calm patches just stop being calm. Screen-blend keeps it monotonic and bounded at 1.
            // Deliberately allowed past MaxAmbient: that cap exists so fair-weather flags do not flap like crazy,
            // and a gale is exactly when they should.
            if (WeatherWind > 0.001f) wind += WeatherWind * (1f - wind);
            return wind;
        }

        /// <summary>0..1 wind the ACTIVE weather is adding on top of the ambient breeze. Set by WeatherManager from
        /// WeatherType.WindMain (already scaled by the fade blend, so it eases in and out with the storm) and left
        /// at 0 in clear weather. Static because the wind field is queried from shaders' CPU feeders, flags,
        /// foliage and the turbine, none of which have a WeatherManager reference.</summary>
        public static float WeatherWind;

        public const float MaxAmbient = 0.8f;      // master: cap the windmap's upper end so flags don't flap like crazy

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
        // ---- the `wind_vec` global -------------------------------------------------------------------------
        //
        // ⚠⚠ THIS USED TO LIVE ONLY IN PlayerController.UpdateGrassDisplacement, which means it only ran in a world
        // that HAS a player -- and the MAP EDITOR does not have one. Every sway shader in the editor was therefore
        // reading a global that nothing ever wrote, i.e. zero: grass, flowers, flags and now the power lines all
        // stood perfectly still while you authored them. That is the "a global uniform reads 0 until someone
        // registers AND drives it" trap from [[reference_godot_traps_index]], and it hid because the game looked
        // right -- only the editor was wrong, and nobody sways-tests an editor.
        //
        // The wind is WindField's business, so the integration lives here and the callers just say "drive it".
        static float _phase;
        static long _lastPushFrame = long.MinValue / 2, _lastIdleFrame = long.MinValue / 2;
        static double _sinceAuthoritative = double.MaxValue;   // seconds of frames since the player last pushed

        /// <summary>Drive `wind_vec` from an authoritative position -- the local player. Always pushes.</summary>
        public static void PushGlobals(Vector3 at, double delta)
        {
            _lastPushFrame = (long)Engine.GetProcessFrames();
            _sinceAuthoritative = 0;
            Integrate(at, delta);
        }

        /// <summary>Drive `wind_vec` only if nothing authoritative has recently. This is what lets the EDITOR and the
        /// render harnesses have wind without fighting the player for it in a live game.
        ///
        /// ⚠⚠ "THE WIND ON THE INF MAP IS REALLY FAST" (strawberry 2026-10-09). Two holes, both here. Every
        /// PowerLineField calls this from its _Process, and nothing stopped SEVERAL of them integrating in the same
        /// frame -- one field on PEI, but one per region in the infinite world (13 round the spawn), so the phase ran
        /// up to 13x. And "recently" was counted in FRAMES (<= 2), while the player pushes at 60 Hz: above ~120 fps there
        /// are frames between its pushes, and every one of them let the idle callers in. Now: one integration per frame
        /// whoever asks, and "recently" is measured in time.</summary>
        public static void PushGlobalsIfIdle(Vector3 at, double delta)
        {
            // PROCESS frames, not frames drawn: a headless run draws none, and a counter that never moves made this
            // guard swallow every call after the first -- the wind stopped dead (the L1 test caught it at 0.00/s)
            long f = (long)Engine.GetProcessFrames();
            if (f == _lastIdleFrame) return;   // another idle caller already had this frame
            _lastIdleFrame = f;
            if (f != _lastPushFrame) _sinceAuthoritative += delta;
            if (_sinceAuthoritative < 0.25) return;   // the player is driving it (at 60 Hz, not necessarily every frame)
            Integrate(at, delta);
        }

        /// <summary>Test seam: the integrated sway phase (wind_vec.w).</summary>
        public static float PhaseForTest => _phase;

        static void Integrate(Vector3 at, double delta)
        {
            float windZ = SampleWind(at);
            // 0.55x dead calm .. 1.45x full gale, exactly 1.0x at the fair-weather 0.5, so sway already signed off
            // keeps its rhythm. ⚠ ACCUMULATED, never `TIME * f(wind)`: strength changes every frame, and scaling a
            // running clock by a changing factor re-maps the phase and makes every blade JUMP.
            _phase += (float)delta * (0.55f + 0.9f * windZ);
            // ⚠ Wrapped at 20*PI, which has to stay a WHOLE number of cycles for every consumer: a consumer reading
            // `sin(k * w)` is only continuous across the wrap when k*10 is an integer. The existing ones use 1.3,
            // 1.5 and 1.6; powerline_wire.gdshader uses 1.1 and 1.7. Pick multiples of 0.1 or the whole world
            // stutters together once a cycle.
            _phase = Mathf.PosMod(_phase, Mathf.Tau * 10f);
            var wd = WindXZ(at);
            RenderingServer.GlobalShaderParameterSet(GrassDisplacers.WindParam, new Vector4(wd.X, wd.Y, windZ, _phase));
        }

        public static Vector2 WindXZ(Vector3 worldPos) { float a = WindAngle(worldPos); return new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }
    }
}
