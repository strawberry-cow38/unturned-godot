namespace SDG.Unturned
{
    // One weather archetype -- the scalars a WeatherAsset carries (src Bundles/WeatherAssetBase.cs +
    // WeatherAsset.cs). Values come from the ripped Bundles/Assets/Weather/*.asset, nothing invented.
    public struct WeatherType
    {
        public string Name;
        public float FadeInDuration, FadeOutDuration;   // seconds (both rain assets: 20 / 20)
        public float WindMain;                          // Wind_Main -- drives the port's WindField while active
        public float FogDensity;                        // the per-time-of-day Fog_Density (both assets use one value across all four bands)
        public float ShadowStrengthMultiplier;
        public float FishBiteIntervalMultiplier;        // Fish_Bite_Interval_Multiplier -- < 1 = fish bite SOONER in the rain
        public bool HasLightning;
        public float MinLightningInterval, MaxLightningInterval;
    }

    // A level's schedulable weather entry (src LevelAsset.SchedulableWeather). Frequency/duration are in
    // DAY-CYCLES, not seconds: LightingManager multiplies by `cycle` (the day length, default 3600 s) and by
    // the mode config's Weather_Frequency/Duration_Multiplier (both default 1.0). Verified in
    // Managers/LightingManager.cs:590-595 -- not assumed.
    public struct WeatherSchedule
    {
        public int TypeIndex;
        public float MinFrequency, MaxFrequency;   // cycles between forecasts
        public float MinDuration, MaxDuration;     // cycles the weather stays active
    }

    // src LightingManager.EScheduledWeatherStage.
    public enum WeatherStage { None, Forecast, Active, PerpetuallyActive }

    // The engine-free weather scheduler: pick a weather, count down to it, run it for a while, drop back to
    // clear, repeat. A 1:1 port of the LightingManager scheduled-weather machine, kept out of the Godot node so
    // the timing (which is otherwise a multi-hour wait to observe) is unit-testable at any speed.
    //
    // BlendAlpha is this port's reading of the fade window rather than a line-for-line copy: the asset gives
    // Fade_In_Duration / Fade_Out_Duration, so alpha ramps 0->1 over the fade-in after activation and 1->0 over
    // the last Fade_Out_Duration seconds of the active window. Everything else (stage order, the cycle
    // multiply, the timers) is verbatim.
    public sealed class WeatherSim
    {
        public const float DefaultCycleSeconds = 3600f;   // src LightingManager: _cycle defaults to 3600

        readonly WeatherType[] _types;
        readonly WeatherSchedule[] _schedule;
        readonly System.Random _rng;
        readonly float _cycleSeconds;
        readonly float _frequencyMultiplier, _durationMultiplier;   // src Provider.modeConfigData.Events.*, both 1.0 by default

        public WeatherStage Stage { get; private set; } = WeatherStage.None;
        public int ActiveTypeIndex { get; private set; } = -1;
        public float ForecastTimer { get; private set; }   // seconds until it starts
        public float ActiveTimer { get; private set; }     // seconds until it stops
        float _activeTotal;                                // the full active window, for the fade-in ramp
        public float BlendAlpha { get; private set; }      // 0 = clear, 1 = fully committed weather

        public WeatherSim(WeatherType[] types, WeatherSchedule[] schedule, int seed,
                          float cycleSeconds = DefaultCycleSeconds,
                          float frequencyMultiplier = 1f, float durationMultiplier = 1f)
        {
            _types = types ?? System.Array.Empty<WeatherType>();
            _schedule = schedule ?? System.Array.Empty<WeatherSchedule>();
            _rng = new System.Random(seed);
            _cycleSeconds = cycleSeconds > 0f ? cycleSeconds : DefaultCycleSeconds;
            _frequencyMultiplier = frequencyMultiplier;
            _durationMultiplier = durationMultiplier;
        }

        public WeatherType? Active =>
            ActiveTypeIndex >= 0 && ActiveTypeIndex < _types.Length ? _types[ActiveTypeIndex] : (WeatherType?)null;

        /// <summary>Is weather actually being felt right now (any blend at all)?</summary>
        public bool IsRaining => BlendAlpha > 0.001f;

        /// <summary>Fish bite multiplier for the CURRENT conditions, scaled by how committed the weather is
        /// (clear = 1.0, full rain = the asset's value). Consumed by the fishing sim.</summary>
        public float FishBiteIntervalMultiplier
        {
            get
            {
                var a = Active;
                if (a == null || BlendAlpha <= 0f) return 1f;
                return Lerp(1f, a.Value.FishBiteIntervalMultiplier, Clamp01(BlendAlpha));
            }
        }

        /// <summary>Wind strength contribution (0 when clear), scaled by the blend.</summary>
        public float WindMain => Active is { } a ? a.WindMain * Clamp01(BlendAlpha) : 0f;

        /// <summary>Fog density contribution, scaled by the blend.</summary>
        public float FogDensity => Active is { } a ? a.FogDensity * Clamp01(BlendAlpha) : 0f;

        float NextForecastSeconds(in WeatherSchedule s)
            => Range(s.MinFrequency, s.MaxFrequency) * _frequencyMultiplier * _cycleSeconds;

        float NextActiveSeconds(in WeatherSchedule s)
            => Range(s.MinDuration, s.MaxDuration) * _durationMultiplier * _cycleSeconds;

        // src: Random.Range(index) over the schedulable list, then forecast + active timers off that entry.
        void ScheduleNext()
        {
            if (_schedule.Length == 0) { Stage = WeatherStage.None; return; }
            var s = _schedule[_rng.Next(0, _schedule.Length)];
            ActiveTypeIndex = s.TypeIndex;
            ForecastTimer = NextForecastSeconds(in s);
            ActiveTimer = NextActiveSeconds(in s);
            _activeTotal = ActiveTimer;
            Stage = WeatherStage.Forecast;
        }

        /// <summary>Force a weather on immediately (src ForecastWeatherImmediately: forecast timer 0, real
        /// active window) -- the `weather` console command's path.</summary>
        public bool ForecastImmediately(int typeIndex)
        {
            for (int i = 0; i < _schedule.Length; i++)
                if (_schedule[i].TypeIndex == typeIndex)
                {
                    ActiveTypeIndex = typeIndex;
                    ForecastTimer = 0f;
                    ActiveTimer = NextActiveSeconds(in _schedule[i]);
                    _activeTotal = ActiveTimer;
                    Stage = WeatherStage.Active;
                    return true;
                }
            return false;
        }

        /// <summary>src SetPerpetualWeather: on until someone turns it off, never deactivates naturally.</summary>
        public void SetPerpetual(int typeIndex)
        {
            ActiveTypeIndex = typeIndex;
            Stage = WeatherStage.PerpetuallyActive;
            ActiveTimer = 0f;
            _activeTotal = 0f;
            // Publish the blend NOW rather than waiting for the next Step. Clear() already does the equivalent,
            // and leaving it stale is a real trap: with the day/night clock paused (timeSpeed 0) dt is 0, Step()
            // early-returns, and perpetual weather stayed invisible forever. Found by rendering it.
            BlendAlpha = 1f;
        }

        /// <summary>Clear immediately and re-schedule (src ResetScheduledWeather).</summary>
        public void Clear()
        {
            Stage = WeatherStage.None;
            ActiveTypeIndex = -1;
            BlendAlpha = 0f;
            ForecastTimer = ActiveTimer = _activeTotal = 0f;
        }

        public void Step(float dt)
        {
            if (dt <= 0f) return;

            switch (Stage)
            {
                case WeatherStage.None:
                    ScheduleNext();
                    break;

                case WeatherStage.Forecast:
                    ForecastTimer -= dt;
                    if (ForecastTimer <= 0f) Stage = WeatherStage.Active;
                    break;

                case WeatherStage.Active:
                    ActiveTimer -= dt;
                    if (ActiveTimer <= 0f)
                    {
                        // done -- fall back to clear and let the next Step pick the following weather
                        Stage = WeatherStage.None;
                        ActiveTypeIndex = -1;
                        ActiveTimer = 0f;
                    }
                    break;

                case WeatherStage.PerpetuallyActive:
                    break;   // never times out
            }

            BlendAlpha = ComputeBlend();
        }

        float ComputeBlend()
        {
            var a = Active;
            if (a == null) return 0f;
            if (Stage == WeatherStage.PerpetuallyActive) return 1f;
            if (Stage != WeatherStage.Active) return 0f;   // a forecast is not visible yet

            float fadeIn = a.Value.FadeInDuration, fadeOut = a.Value.FadeOutDuration;
            float elapsed = _activeTotal - ActiveTimer;

            // A short weather window can be shorter than fadeIn + fadeOut; split it proportionally rather than
            // letting the two ramps overlap and double-count.
            if (fadeIn + fadeOut > _activeTotal && fadeIn + fadeOut > 0f && _activeTotal > 0f)
            {
                float scale = _activeTotal / (fadeIn + fadeOut);
                fadeIn *= scale; fadeOut *= scale;
            }

            float rising = fadeIn > 0f ? Clamp01(elapsed / fadeIn) : 1f;
            float falling = fadeOut > 0f ? Clamp01(ActiveTimer / fadeOut) : 1f;
            return Clamp01(Min(rising, falling));
        }

        float Range(float min, float max) => min >= max ? min : min + (float)_rng.NextDouble() * (max - min);
        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static float Min(float a, float b) => a < b ? a : b;
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // --- PEI's real weather table, straight from the rip. ---
        // Bundles/Assets/Weather/DefaultRain.asset + HeavyRain.asset (scalars), and PEI.asset's Weather_Types
        // block (frequency/duration). PEI schedules RAIN ONLY -- snow belongs to Yukon, so this port has none.
        public static WeatherType[] PeiTypes() => new[]
        {
            new WeatherType
            {
                Name = "Default Rain",
                FadeInDuration = 20f, FadeOutDuration = 20f,
                WindMain = 0.3f, FogDensity = 0.7f, ShadowStrengthMultiplier = 0.2f,
                FishBiteIntervalMultiplier = 0.9f,
                HasLightning = false,
            },
            new WeatherType
            {
                Name = "Heavy Rain",
                FadeInDuration = 20f, FadeOutDuration = 20f,
                WindMain = 0.5f, FogDensity = 1.0f, ShadowStrengthMultiplier = 0.15f,
                FishBiteIntervalMultiplier = 0.8f,
                HasLightning = true, MinLightningInterval = 15f, MaxLightningInterval = 60f,
            },
        };

        // PEI.asset Weather_Types: both entries carry the SAME frequency/duration band.
        public static WeatherSchedule[] PeiSchedule() => new[]
        {
            new WeatherSchedule { TypeIndex = 0, MinFrequency = 2.3f, MaxFrequency = 5.6f, MinDuration = 0.05f, MaxDuration = 0.15f },
            new WeatherSchedule { TypeIndex = 1, MinFrequency = 2.3f, MaxFrequency = 5.6f, MinDuration = 0.05f, MaxDuration = 0.15f },
        };
    }
}
