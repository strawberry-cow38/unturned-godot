using Godot;

namespace UnturnedGodot
{
    // The DAY / NIGHT ambient beds (master 2026-09-07: "extract and implement ambient day sounds from the source").
    //
    // Ported from LevelLighting, which is where retail keeps this: it loads Maps/<Map>/Environment/Ambience.unity3d
    // and pulls five clips off it by NAME -- Day, Night, Water, Wind, Below -- onto five AudioSources it holds as
    // statics (LevelLighting.cs:1412-1422). So the beds are PER MAP, and PEI, Washington and Yukon genuinely ship
    // different bundles. tools/extract_ambience.py rips them; only Day and Night are wired here.
    //
    // NIGHT CAME TOO, and not as scope creep: retail's day volume is defined against the night one, they CROSSFADE
    // rather than switch, and every branch of that curve has dayVolume + nightVolume == 1 exactly. Shipping Day
    // alone would mean the world falls silent every dusk, which is a worse bug than the one being fixed.
    //
    // THE CURVE (LevelLighting.cs:853-939) is a linear crossfade with a plateau, passing through 0.5/0.5 at the
    // exact moment of dawn and of dusk -- NOT a switch, and not an ease. Six branches there reduce to that because
    // the pair always sums to 1; this is the same shape expressed once.
    //
    // ⚠ Plain AudioStreamPlayers on their own bus, NEVER SoundBus.Emit. That is the zombie-HEARING path, and a
    // looping ambient bed through it is permanent map-wide aggro -- the same reason RainAudio and the thunder pool
    // spell this out. See reference_unturned_sound_playback.
    public partial class AmbienceAudio : Node
    {
        /// <summary>Which map's beds to load: "pei", "washington"... Set beside the other per-map statics in
        /// Main (FoliageField.MapDir and friends). A map with no ripped bundle falls back to PEI's rather than
        /// running silent, because silence is indistinguishable from the feature being broken.</summary>
        public static string MapKey = "pei";

        /// <summary>0 = clear .. 1 = downpour. Ducks the beds, which is retail's own
        /// customWeatherVolumeMultiplier = 1 - maxCustomWeatherVolume (LevelLighting.cs:2232/2246): you do not
        /// hear the birds through a storm. WeatherManager feeds it the same rint RainAudio gets.</summary>
        public float WeatherDuck;

        public static float BedDb = -9f;        // full-bed level; the beds are broadband and sit UNDER everything
        public static float Transition = 0.05f; // crossfade half-width in day fraction, either side of dawn/dusk

        const float Dawn = 0.25f, Dusk = 0.75f;   // DayNightCycle.Time: 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk

        // BIRDS TURN IN BEFORE THE LIGHT DOES (strawberry 2026-09-09: "fade out birds sooner in the evening",
        // then "after 7pm should be silent"). The DAY bed is the dawn chorus, and it was riding the same crossfade
        // as everything else -- full until 0.70 and only silent at 0.80, singing right through sunset.
        //
        // Stated as CLOCK TIMES rather than as offsets from Dusk, because the requirement is a clock time and an
        // offset chain is how you end up quietly missing it: silent from 19:00, having faded over the 90 minutes
        // before. The general day/night crossfade still runs 16:48 -> 19:12 underneath.
        //
        // The pair no longer sums to 1 across that window, and that is the POINT rather than an oversight: birds
        // stopping before the night bed is fully up is the evening hush. It is a few dB and it is deliberate.
        public static float BirdQuietBy = 19f / 24f;     // 19:00 -- silent from here on, by definition
        public static float BirdFadeHours = 1.5f;        // ...having started to go this long before

        // ...AND THEY STOP IN RAIN, well before a downpour (strawberry: "fade out bird ambience when its
        // raining"). WeatherDuck already scaled both beds by 1-rint, which is retail's
        // customWeatherVolumeMultiplier -- but linear means light rain leaves birds at 70%, and birds do not sing
        // at 70% through drizzle, they stop. The day bed gets its own steeper curve; the night bed keeps retail's.
        public static float BirdRainIn = 0.05f;     // rain intensity where the birds start to go
        public static float BirdRainOut = 0.35f;    // ...and where they are gone entirely

        AudioStreamPlayer _day, _night;
        string _busName;
        bool _busAdded;
        float _dayDb = -80f, _nightDb = -80f;

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            int idx = AudioServer.BusCount;
            AudioServer.AddBus(idx);
            AudioServer.SetBusName(idx, "Ambience");
            _busName = AudioServer.GetBusName(idx);   // the name it ACTUALLY got (AudioServer dedupes) -- own it, so a second instance cannot cross-wire
            _busAdded = true;

            _day = MakeBed("day");
            _night = MakeBed("night");
            if (_day == null && _night == null) Log.Print($"[ambience] no beds for map '{MapKey}' -- silent");
        }

        AudioStreamPlayer MakeBed(string which)
        {
            var clip = GameAudio.Clip("ambience", $"{MapKey}_{which}") ?? GameAudio.Clip("ambience", $"pei_{which}");
            if (clip is not AudioStreamOggVorbis ov) return null;
            ov.Loop = true;   // one continuous bed; the retail clips are authored to loop
            var p = new AudioStreamPlayer { Stream = ov, Bus = _busName, VolumeDb = -80f };
            AddChild(p);
            p.Play();   // always rolling, mixed by volume alone -- restarting a bed on every dawn would seam audibly
            return p;
        }

        /// <summary>The DAY bed's share at this time of day, 0..1. The night bed is 1 - this, exactly as retail's
        /// six branches work out to. Full day between dawn+T and dusk-T, full night outside dawn-T..dusk+T, and a
        /// straight ramp through 0.5 at dawn and at dusk.</summary>
        public static float DayShare(float t)
        {
            t = Mathf.PosMod(t, 1f);
            if (t >= Dawn + Transition && t <= Dusk - Transition) return 1f;
            if (t >= Dawn - Transition && t < Dawn + Transition) return (t - (Dawn - Transition)) / (2f * Transition);
            if (t > Dusk - Transition && t <= Dusk + Transition) return 1f - (t - (Dusk - Transition)) / (2f * Transition);
            return 0f;
        }

        /// <summary>The DAY bed's share, on the birds' own curve: retail's dawn edge, but an evening edge that
        /// starts earlier and finishes before the general crossfade's midpoint.</summary>
        public static float BirdShare(float t)
        {
            t = Mathf.PosMod(t, 1f);
            float outEnd = Mathf.PosMod(BirdQuietBy, 1f);            // gone by here (19:00)
            float outStart = outEnd - BirdFadeHours / 24f;           // started going here
            float rise = t >= Dawn + Transition ? 1f
                       : t >= Dawn - Transition ? (t - (Dawn - Transition)) / (2f * Transition)
                       : 0f;
            float fall = t <= outStart ? 1f
                       : t >= outEnd ? 0f
                       : 1f - (t - outStart) / Mathf.Max(0.0001f, outEnd - outStart);
            return Mathf.Min(rise, fall);
        }

        /// <summary>How much of the bird bed survives this much rain. 1 = dry, 0 = gone.</summary>
        public static float BirdRainShare(float rint)
            => 1f - Mathf.SmoothStep(BirdRainIn, BirdRainOut, Mathf.Clamp(rint, 0f, 1f));

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine callback is off (SetProcess(false))
        public void HubProcess(double delta)
        {
            if (_day == null && _night == null) return;
            float t = (GetTree()?.GetFirstNodeInGroup("daynight") as DayNightCycle)?.Time ?? 0.5f;
            float day = DayShare(t);
            float duck = 1f - Mathf.Clamp(WeatherDuck, 0f, 1f);
            // The night bed keeps retail's curve and retail's linear duck; only the birds get the earlier evening
            // edge and the steeper rain response.
            SlewTo(_day, ref _dayDb, BirdShare(t) * BirdRainShare(WeatherDuck), (float)delta);
            SlewTo(_night, ref _nightDb, (1f - day) * duck, (float)delta);
        }

        // Retail lerps the VOLUME at 0.5*dt (LevelLighting.cs:2246). We mix in dB, so slew there instead: a linear
        // fade in amplitude is what clicks, and a dB slew is what the rest of this project's audio already does.
        void SlewTo(AudioStreamPlayer p, ref float db, float amp, float dt)
        {
            if (p == null) return;
            float target = amp <= 0.001f ? -80f : BedDb + 20f * Mathf.Log(Mathf.Clamp(amp, 0.0001f, 1f)) / Mathf.Log(10f);
            db = Mathf.Lerp(db, target, Mathf.Clamp(0.5f * dt, 0f, 1f));
            p.VolumeDb = db;
        }

        public override void _ExitTree()
        {
            // Give the bus back. AudioServer buses are global and survive the node; leaking one per world load
            // walks the index up until the names stop matching what the players were bound to.
            if (!_busAdded) return;
            int idx = AudioServer.GetBusIndex(_busName);
            if (idx > 0) AudioServer.RemoveBus(idx);
            _busAdded = false;
        }
    }
}
