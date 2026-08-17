using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Drives the world's weather: ticks the engine-free WeatherSim (the src LightingManager scheduled-weather
    // machine) and pushes the result into the things that show it -- the rain overlay, the day/night overcast
    // flag, wind, and lightning. Other systems read the live conditions off `Current` (fishing wants the bite
    // multiplier; a rain barrel wants to know it's raining).
    //
    // src: Managers/LightingManager.cs + Bundles/WeatherAsset.cs; values from Bundles/Assets/Weather/*.asset
    // and PEI.asset's Weather_Types block.
    public partial class WeatherManager : Node
    {
        public static WeatherManager Current { get; private set; }

        public WeatherSim Sim { get; private set; }
        public RainOverlay Overlay;
        public DayNightCycle Cycle;

        // The src schedules against `cycle` = the day length in seconds (default 3600). This port's day is much
        // shorter (DayNightCycle.DayLength, 120 s by default), and the honest thing is to feed the REAL day
        // length into the same formula rather than hardcode 3600 -- weather should track the world's clock, not
        // a number from a different game's pacing. Consequence, stated plainly: on a 120 s day, PEI's 2.3-5.6
        // cycle frequency is a shower every ~5-11 min lasting ~6-18 s. The two multipliers below exist so that
        // can be tuned without touching the ripped table.
        public static float FrequencyMultiplier =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_WEATHER_FREQ"), out var f) ? f : 1f;
        public static float DurationMultiplier =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_WEATHER_DUR"), out var d) ? d : 1f;

        // lightning (Heavy Rain only: Has_Lightning, Min/Max_Lightning_Interval 15/60)
        float _nextLightning = -1f;
        float _flash;                  // 0..1 screen flash envelope
        ColorRect _flashRect;
        CanvasLayer _flashLayer;
        RandomNumberGenerator _rng = new();
        int _dbgFrames;
        // Flash strength/decay. The first pass used 0.55 peak over ~0.3 s and the render showed it was not a
        // lightning flash at all -- it washed the whole frame to near-white (red crates came out pink, ground
        // almost white). A strike should read as a brief brightening through cloud, so: weaker peak, faster
        // decay. Numbers are a judgement call, not from the asset -- the .asset only says lightning EXISTS and
        // how often, never how bright.
        const float FlashPeak = 0.22f, FlashDecay = 5.0f;

        /// <summary>How heavy the ACTIVE weather type is, 0..1, taken from the asset's Fog_Density (Default Rain
        /// 0.7, Heavy Rain 1.0). Multiplies the streak density so heavy rain actually looks heavier.</summary>
        public float Severity => Sim?.Active is { } t ? Mathf.Clamp(t.FogDensity, 0.1f, 1f) : 1f;

        public bool IsRaining => Sim != null && Sim.IsRaining;
        public float RainIntensity => Sim?.BlendAlpha ?? 0f;
        /// <summary>Multiplier other systems apply to a fishing bite interval (< 1 = bites sooner).</summary>
        public static float FishBiteInterval => Current?.Sim?.FishBiteIntervalMultiplier ?? 1f;

        public static WeatherManager Attach(Node parent, RainOverlay overlay, DayNightCycle cycle, int seed = 0)
        {
            var w = new WeatherManager { Overlay = overlay, Cycle = cycle };
            w.Sim = new WeatherSim(WeatherSim.PeiTypes(), WeatherSim.PeiSchedule(),
                                   seed != 0 ? seed : (int)GD.Randi(),
                                   cycleSeconds: cycle != null && cycle.DayLength > 0f ? cycle.DayLength : WeatherSim.DefaultCycleSeconds,
                                   frequencyMultiplier: FrequencyMultiplier,
                                   durationMultiplier: DurationMultiplier);
            parent.AddChild(w);
            return w;
        }

        public override void _Ready()
        {
            Current = this;
            AddToGroup("weather");   // the dev console finds it here
            _rng.Randomize();

            // lightning flash: a white full-screen rect above the rain overlay, alpha driven by _flash
            _flashLayer = new CanvasLayer { Layer = 90 };
            _flashRect = new ColorRect { Color = new Color(1f, 1f, 1f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
            _flashRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _flashLayer.AddChild(_flashRect);
            AddChild(_flashLayer);
        }

        public override void _ExitTree() { if (Current == this) Current = null; }

        public override void _Process(double delta)
        {
            if (Sim == null) return;
            // weather rides the same clock as the day/night cycle, so `timeSpeed` speeds the sky AND the weather
            float dt = (float)delta * (Cycle != null ? Mathf.Max(0f, Cycle.Speed) : 1f);
            Sim.Step(dt);

            float a = Sim.BlendAlpha;
            if (Overlay != null)
            {
                Overlay.Raining = a > 0.001f;
                // Blend alone made Default Rain and Heavy Rain render IDENTICALLY (caught by rendering them side
                // by side -- both peak at blend 1.0). The assets do differ, so scale the streak density by the
                // type's SEVERITY rather than inventing a number: Fog_Density is 0.7 for Default Rain and 1.0 for
                // Heavy Rain, which is exactly the "how bad is this weather" axis the .asset already encodes.
                Overlay.Intensity = a * Severity;
            }
            if (Cycle != null) Cycle.Overcast = a > 0.35f;   // the sky turns grey once the weather commits

            if (_dbgFrames < 8 && System.Environment.GetEnvironmentVariable("UG_WEATHER") != null)
            {
                _dbgFrames++;
                GD.Print($"[WDBG] f{_dbgFrames} stage={Sim.Stage} blend={a:0.000} severity={Severity:0.00} overlayIntensity={(Overlay?.Intensity ?? -1f):0.000} raining={Overlay?.Raining}");
            }

            TickLightning(dt);
        }

        void TickLightning(float dt)
        {
            var active = Sim.Active;
            bool canStrike = active is { HasLightning: true } && Sim.BlendAlpha > 0.5f;

            if (!canStrike) { _nextLightning = -1f; }
            else
            {
                if (_nextLightning < 0f)
                    _nextLightning = _rng.RandfRange(active.Value.MinLightningInterval, active.Value.MaxLightningInterval);
                _nextLightning -= dt;
                if (_nextLightning <= 0f)
                {
                    Strike();
                    _nextLightning = _rng.RandfRange(active.Value.MinLightningInterval, active.Value.MaxLightningInterval);
                }
            }

            if (_flash > 0f)
            {
                _flash = Mathf.Max(0f, _flash - dt * FlashDecay);
                if (_flashRect != null) _flashRect.Color = new Color(1f, 1f, 1f, _flash * FlashPeak);
            }
        }

        /// <summary>One lightning flash. Public so the console + tests can fire it without waiting 15-60 s.</summary>
        public void Strike()
        {
            _flash = 1f;
            if (_flashRect != null) _flashRect.Color = new Color(1f, 1f, 1f, FlashPeak);
            GD.Print("[weather] lightning");
        }

        public int StrikeCountDebug { get; private set; }

        // --- console surface (src CommandWeather) ---
        public bool ApplyCommand(string arg)
        {
            switch ((arg ?? "").Trim().ToLowerInvariant())
            {
                case "clear": case "none": Sim.Clear(); return true;
                case "rain": case "light": Sim.ForecastImmediately(0); return true;
                case "heavy": case "storm": Sim.ForecastImmediately(1); return true;
                case "lightning": Strike(); return true;
                default: return false;
            }
        }
    }
}
