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
        RainSystem3D _rain3d;
        RainRoofMap _roofMap;
        int _roofCheckTicks;   // the top-down roof heightmap around the player (per-drop roof occlusion)   // worldspace 3D rain -- supersedes the 2D overlay streaks
        RainAudio _rainAudio;   // layered rain soundscape (Rain bus, shelter low-pass)
        RainMaterialAudio _rainMatAudio;   // positional rain-on-material: nearest car/tree/... emits its own rain sound within a radius
        AudioStreamPlayer[] _thunderPool;   // a few plain players on Master (NOT SoundBus -> never lures zombies) so overlapping claps don't cut each other
        AudioStream[] _thunderStreams;      // varied freesound samples: [0]=medium clap, [1]=sharp close crack, [2]=deep distant rumble
        int _thunderPoolNext;               // round-robin index into the pool
        // Pending booms QUEUE, not a single slot -- a fresh strike inside the last one's flash→boom gap must NOT cancel
        // it (reachable via `weather lightning` spam or the demo's UG_STRIKE_AT clustering; tinyclaw). Each entry ticks
        // on the _Process FRAME delta so it fires even when the day clock is frozen (renders).
        readonly System.Collections.Generic.List<(float t, float vol, int pick)> _pendingThunder = new();
        int _flashesLeft; float _reflashIn = -1f;   // multi-stroke flicker: a strike can re-peak the flash 1-2 more times a few frames apart

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
        float _lastRint = -1f;   // last rint pushed to the globals -- skip the per-frame GlobalShaderParameterSet when unchanged (tinyclaw)
        // Flash strength/decay. The first pass used 0.55 peak over ~0.3 s and the render showed it was not a
        // lightning flash at all -- it washed the whole frame to near-white (red crates came out pink, ground
        // almost white). A strike should read as a brief brightening through cloud, so: weaker peak, faster
        // decay. Numbers are a judgement call, not from the asset -- the .asset only says lightning EXISTS and
        // how often, never how bright.
        const float FlashPeak = 0.09f, FlashDecay = 7.0f;   // overlay peak is LOW (additive supporting layer) -- the sky cloud-flash is the main event now; FlashDecay = EXPONENTIAL envelope rate
        Vector3 _strikeDir = Vector3.Forward;   // XZ azimuth of the current strike -> the sky glows on that side
        float _strikeDist;                      // 0 overhead .. 1 far -> warms the flash tint (distant strikes redder, Fable)

        /// <summary>How heavy the ACTIVE weather type is, 0..1, taken from the asset's Fog_Density (Default Rain
        /// 0.7, Heavy Rain 1.0). Multiplies the streak density so heavy rain actually looks heavier.</summary>
        public float Severity => Sim?.Active is { } t ? Mathf.Clamp(t.FogDensity, 0.1f, 1f) : 1f;

        public bool IsRaining => Sim != null && Sim.IsRaining;
        public float RainIntensity => Sim?.BlendAlpha ?? 0f;
        /// <summary>rint (0..1): the value the rain density, wetness, splashes and storm sky all scale off --
        /// BlendAlpha x Severity. This is what the retired 2D overlay's Intensity used to carry.</summary>
        public float Rain3DIntensity => _rain3d?.Intensity ?? 0f;   // the ACTUAL 3D-rain input (rint x shelter) RainSystem3D fades the rain off. Tests assert THIS, not a parallel re-derivation of rint (tinyclaw finding 3).
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
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Current = this;
            AddToGroup("weather");   // the dev console finds it here
            _rng.Randomize();

            // lightning flash overlay: a full-screen rect, ADDITIVE (Fable) -- brightens the frame instead of alpha-
            // washing it to white (the old white-over-frame dragged red crates to pink). SUPPORTING layer only now; the
            // sky cloud-flash carries the strike. Hidden until it fires so a transparent rect doesn't rasterise.
            _flashLayer = new CanvasLayer { Layer = 90 };
            _flashRect = new ColorRect { Color = new Color(1f, 1f, 1f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
                Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
            _flashRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _flashLayer.AddChild(_flashRect);
            AddChild(_flashLayer);

            // WORLDSPACE 3D rain + the wetness/splash globals (registered before ANY wettable material -- see EnsureGlobals)
            RainSystem3D.EnsureGlobals();
            _rain3d = new RainSystem3D { Intensity = 0f };
            _roofMap = null;
            AddChild(_rain3d);
            _rainAudio = new RainAudio();
            AddChild(_rainAudio);
            _rainMatAudio = new RainMaterialAudio();
            AddChild(_rainMatAudio);

            // THUNDER: a few varied samples it picks from per strike -- a sharp clap for close hits, a deep rumble for
            // distant ones (bitvox: "different matching sound effects"). Plain AudioStreamPlayers on Master, deliberately
            // NOT SoundBus.Emit so a thunderclap never lures zombies the way a gunshot does. A pool so overlapping claps
            // don't cut each other. freesound CC0/CC-BY: Kinoton #760216, hifijohn #242586, klankbeeld #322210.
            string[] tf = { "thunder.wav", "thunder2.wav", "thunder3.wav" };
            _thunderStreams = new AudioStream[tf.Length];
            bool anyThunder = false;
            for (int i = 0; i < tf.Length; i++)
            {
                var tp = ProjectSettings.GlobalizePath("res://content/" + tf[i]);
                if (System.IO.File.Exists(tp)) { _thunderStreams[i] = AudioStreamWav.LoadFromFile(tp); anyThunder |= _thunderStreams[i] != null; }
            }
            // + retail's own three lightning rumbles (effects/weather/lightning), ripped 2026-09-03 -- they join the pick pool
            var rumbles = GameAudio.Bank("ambience", "thunder_lightning_strike_rumble");
            if (rumbles.Length > 0)
            {
                var merged = new System.Collections.Generic.List<AudioStream>(); foreach (var t in _thunderStreams) if (t != null) merged.Add(t); merged.AddRange(rumbles);
                _thunderStreams = merged.ToArray(); anyThunder = true;
            }
            if (anyThunder)
            {
                _thunderPool = new AudioStreamPlayer[4];
                for (int i = 0; i < _thunderPool.Length; i++) { _thunderPool[i] = new AudioStreamPlayer { Bus = "Master" }; AddChild(_thunderPool[i]); }
            }
        }

        public override void _ExitTree() { if (Current == this) Current = null; RainSystem3D.ResetGlobals(); }   // a departing storm mustn't leave the wet globals stuck for the next scene (tinyclaw)

        /// <summary>Whether the camera is under a roof, polled at a few Hz rather than every frame.
        ///
        /// The rain overlay is SCREEN-SPACE, so without this it falls through ceilings -- stand in a
        /// finished building in a storm and the streaks carry on across your view. strawberry_cow: "make
        /// floors/roofs occlude rain."
        ///
        /// EASED, not switched. A hard cut at the threshold makes the whole screen flick between wet and dry
        /// as you walk under a doorway, or as the camera crosses an opening; the ease turns that into a
        /// short fade, which is also roughly what stepping under real cover looks like.
        ///
        /// Polled on a timer because a raycast per frame per viewer is exactly the "million rebuilds each
        /// frame" cost strawberry_cow warned about, and shelter changes at walking pace.</summary>
        float _shelter = 1f, _shelterPoll;
        const float ShelterPollSeconds = 0.15f;
        const float ShelterFadeSeconds = 0.35f;

        float ShelterFactor(float dt)
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam == null) return _shelter;

            _shelterPoll -= dt;
            if (_shelterPoll <= 0f)
            {
                _shelterPoll = ShelterPollSeconds;
                _shelterTarget = ShelterProbe.IsSheltered(cam.GetWorld3D(), cam.GlobalPosition) ? 0f : 1f;
            }
            _shelter = Mathf.MoveToward(_shelter, _shelterTarget, dt / ShelterFadeSeconds);
            return _shelter;
        }
        float _shelterTarget = 1f;

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (Sim == null) return;
            // weather rides the same clock as the day/night cycle, so `timeSpeed` speeds the sky AND the weather
            float dt = (float)delta * (Cycle != null ? Mathf.Max(0f, Cycle.Speed) : 1f);
            Sim.Step(dt);

            float a = Sim.BlendAlpha;
            // WORLDSPACE 3D rain (supersedes the 2D overlay streaks): drive its intensity + the wetness/splash globals
            // off the weather. Severity (Fog_Density: 0.7 Default Rain / 1.0 Heavy) scales it so heavy looks heavier.
            float rint = a * Severity;
            // Shelter fade on the FALLING rain: CpuParticles3D have no collision + spawn ~10m above the camera, so
            // without this they fall straight through a roof and render around you INDOORS (tinyclaw's catch -- my
            // earlier "geometry occludes the drops" was wrong, it only covers line of sight). ShelterFactor polls the
            // up-raycast at a few Hz + eases. The wetness globals stay global for now (per-surface shelter = TODO).
            float shelter = ShelterFactor((float)delta);   // still drives the audio muffle; the FALLING rain no longer switches off under cover --
            // the roof map kills each drop under whatever is above it (strawberry 2026-09-04: "each building's roof should kill rain that reaches it")
            var wcam = GetViewport()?.GetCamera3D();
            if (_rain3d != null) { _rain3d.Cam = wcam; _rain3d.Intensity = rint; }
            if (_roofMap == null && wcam != null) { _roofMap = new RainRoofMap { Follow = wcam }; AddChild(_roofMap); }
            else if (_roofMap != null) _roofMap.Follow = wcam;
            if (_roofMap != null && System.Environment.GetEnvironmentVariable("UG_ROOFCHECK") == "1" && ++_roofCheckTicks % 90 == 80)   // self-check against fresh rays (harness only)
                _roofMap.DebugCheck(GetViewport().World3D.DirectSpaceState);
            if (rint != _lastRint)   // push only on change -- else a fresh StringName per literal every frame forever, even in clear weather (tinyclaw)
            {
                _lastRint = rint;
                RenderingServer.GlobalShaderParameterSet("rain_intensity", rint);
                RenderingServer.GlobalShaderParameterSet("rain_wetness", rint);   // TODO: per-surface shelter so roofed floors stay dry
            }
            if (Overlay != null) Overlay.Raining = false;   // the 3D rain replaces the 2D streak overlay
            if (Cycle != null) { Cycle.Overcast = a > 0.35f; Cycle.StormAmount = rint; }   // rint drives the moody storm-env blend (grey sky + fog + dim cool light) in DayNightCycle
            // rain audio: layered soundscape off the same rint + shelter. NO thunder duck -- master dropped it (the claps
            // already clear the rain bed by ~8-9dB on their own, so dipping the rain under them read as a mixer glitch).
            if (_rainMatAudio != null) { _rainMatAudio.Intensity = rint; _rainMatAudio.Cam = GetViewport()?.GetCamera3D(); }
            // muffle under a roof OR a tree canopy. NOTE: only the roof `shelter` fades the 3D rain globally (above) --
            // a canopy instead cuts a LOCAL hole in the streak shader (rain_canopy), so rain still falls outside it.
            // The 900Hz/-7dB/24dB-oct shelter curve is ROOF-correct + deliberate; a permeable CANOPY must NOT pull that
            // same lever as hard as concrete (master: muffle too strong). So cap its reach: a tree only ever takes Shelter
            // to 0.7 (a slight top-off), a solid roof still all the way to 0 (full muffle). Two knobs, tunable apart. (tinyclaw)
            if (_rainAudio != null) { _rainAudio.Intensity = rint; _rainAudio.Shelter = Mathf.Min(shelter, Mathf.Lerp(1f, 0.7f, 1f - (_rainMatAudio?.CanopyShelter ?? 1f))); }

            if (_dbgFrames < 8 && System.Environment.GetEnvironmentVariable("UG_WEATHER") != null)
            {
                _dbgFrames++;
                GD.Print($"[WDBG] f{_dbgFrames} stage={Sim.Stage} blend={a:0.000} severity={Severity:0.00} rint={rint:0.000}");
            }

            TickLightning(dt);
            TickStrikeFx((float)delta);   // the _Process FRAME delta -> flash fade + thunder gap don't stick when the day clock is frozen; deterministic under --write-movie, NOT wall-clock (see TickStrikeFx)
            // Apply the flash envelope to the VISUALS: the sky cloud-flash (main event, via DayNightCycle) + a low
            // additive screen lift. Tint warms as it fades + with distance (Fable). _flash can exceed 1 on the bright
            // re-pulse -> brighter cloud glow; the overlay alpha is clamped so it stays a supporting lift.
            if (Cycle != null) Cycle.LightningFlash = _flash;   // cheap field write; DayNightCycle.Apply guards the shader push
            if (_flash > 0.001f)
            {
                float warmth = Mathf.Clamp((1f - Mathf.Clamp(_flash, 0f, 1f)) * 0.55f + _strikeDist * 0.5f, 0f, 1f);
                Color ltint = new Color(1f, 0.98f, 0.94f).Lerp(new Color(1f, 0.90f, 0.72f), warmth);
                if (Cycle != null) { Cycle.LightningTint = ltint; Cycle.LightningDir = _strikeDir; }
                if (_flashRect != null) { if (!_flashRect.Visible) _flashRect.Visible = true; _flashRect.Color = new Color(ltint, Mathf.Min(_flash, 1.2f) * FlashPeak); }
            }
            else if (_flashRect != null && _flashRect.Visible) _flashRect.Visible = false;
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
            // NB: the flash FADE is NOT here -- it moved to TickStrikeFx on the _Process FRAME delta. This runs on the
            // weather-scaled dt (= delta * Cycle.Speed), which is 0 when the day clock is frozen (renders) and slow when
            // time-scaled, so a flash faded here would stick or crawl. Only the strike FREQUENCY belongs on the weather clock.
        }

        // The per-strike consequences that must ignore the WEATHER clock: the flash FADE and the delayed thunder. Both
        // run on the _Process FRAME delta, NOT TickLightning's dt (= delta*Cycle.Speed = 0 when the day clock is frozen).
        // ⚠ NOT true wall-clock either (tinyclaw): under --write-movie delta is the fixed 1/fps step, so the flash decays
        // over movie frames + renders identically offline; real elapsed time would fully decay it between two frames at
        // lavapipe's ~0.4fps and it'd vanish from every demo. cyc.Speed=0 left it stuck full-on (bitvox "lights up but
        // stays lit up"; tinyclaw traced it to `_flash -= dt*FlashDecay` at dt=0).
        void TickStrikeFx(float dt)
        {
            // multi-stroke flicker: re-peak the flash a couple times a few frames apart (real lightning has return
            // strokes). The re-pulses are BRIGHTER than the first (Fable: the second pulse should be the brightest).
            if (_flashesLeft > 0)
            {
                _reflashIn -= dt;
                if (_reflashIn <= 0f) { _flash = 1.3f; _flashesLeft--; _reflashIn = _flashesLeft > 0 ? _rng.RandfRange(0.04f, 0.10f) : -1f; }
            }
            if (_flash > 0f)
            {
                _flash *= Mathf.Exp(-dt * FlashDecay);   // EXPONENTIAL decay + natural tail (Fable) -- reads like light dying in cloud, not a linear ramp
                if (_flash < 0.003f) _flash = 0f;         // the sky + overlay are driven from _flash in _Process
            }
            for (int i = _pendingThunder.Count - 1; i >= 0; i--)   // tick every pending boom; fire + remove the ready ones (backwards for safe removal)
            {
                var p = _pendingThunder[i];
                p.t -= dt;
                if (p.t <= 0f) { PlayThunder(p.pick, p.vol); _pendingThunder.RemoveAt(i); }
                else _pendingThunder[i] = p;
            }
        }

        // Play one clap on the next free pool player (round-robin so a fresh strike doesn't cut a still-rumbling one).
        void PlayThunder(int pick, float volDb)
        {
            if (_thunderPool == null) return;
            var stream = _thunderStreams[pick] ?? _thunderStreams[0] ?? _thunderStreams[1] ?? _thunderStreams[2];
            if (stream == null) return;
            var pl = _thunderPool[_thunderPoolNext];
            _thunderPoolNext = (_thunderPoolNext + 1) % _thunderPool.Length;
            pl.Stream = stream; pl.VolumeDb = volDb; pl.Play();
            GD.Print($"[weather] thunder #{pick} {volDb:0.0}dB");
        }

        /// <summary>One lightning flash. Public so the console + tests can fire it without waiting 15-60 s.</summary>
        public void Strike()
        {
            _flash = 0.9f;   // first pulse moderate; the flicker re-pulses go brighter (Fable). Applied to the sky + overlay in _Process.
            float dist = _rng.Randf();   // 0 = right overhead, 1 = far off -- drives the flicker, the boom delay, the sound, and the tint warmth
            _strikeDist = dist;
            float az = _rng.Randf() * Mathf.Tau;   // random sky azimuth -> the cloud glow + horizon lift land on ONE side of the sky
            _strikeDir = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            // multi-stroke flicker: close strikes flicker 2-3x, distant ones are a single flash
            int strokes = dist < 0.4f ? (_rng.Randf() < 0.5f ? 3 : 2) : (dist < 0.72f && _rng.Randf() < 0.5f ? 2 : 1);
            _flashesLeft = strokes - 1;
            _reflashIn = _flashesLeft > 0 ? _rng.RandfRange(0.04f, 0.10f) : -1f;
            // thunder: closer -> sooner + louder + a SHARP clap; farther -> later + quieter + a DEEP rumble
            // queue this strike's boom: flash→boom gap cut ~40% (bitvox); sample by distance (near=sharp crack, far=deep rumble)
            if (_thunderPool != null)
                _pendingThunder.Add((Mathf.Lerp(0.24f, 2.4f, dist), Mathf.Lerp(-0.5f, -8.5f, dist), dist < 0.4f ? 1 : (dist > 0.72f ? 2 : 0)));   // vol range -0.5..-8.5 (compressed from -3..-18, then +1.5dB for bitvox's "+15%"). near still clears clipping vs the deep-ducked rain (measured crack peak ~-1.9dBFS)
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
