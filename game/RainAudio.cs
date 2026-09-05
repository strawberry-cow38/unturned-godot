using Godot;

namespace UnturnedGodot
{
    // Layered rain SOUNDSCAPE (Fable's build plan): intensity is WHICH LAYERS play, not one loop turned up. A light
    // bed from the first drop, a heavy roar that only joins in a downpour. Both on a dedicated Rain bus so a single
    // low-pass (24 dB/oct) can actually muffle the whole mix under a roof. Volumes lerp in dB and SLEW per-frame so
    // start/stop + duck don't click. Loop seams are BAKED into the assets (2s crossfade, RMS-matched), so LoopEnd is
    // set to the whole file and LoopMode.Forward does the looping -- one fewer clock under the movie writer.
    //
    // ⚠⚠ plain AudioStreamPlayers -> the Rain bus -> Master, NEVER SoundBus.Emit (zombie-HEARING path; a continuous
    // rain loop through it = permanent map-wide aggro). freesound CC0: _lynks #595717 (light, high-passed to drizzle),
    // AdrianoAnjos #616446 (heavy) -- see content/CREDITS.md.
    public partial class RainAudio : Node
    {
        public float Intensity;    // rint 0..1 (WeatherManager drives it)
        public float Shelter = 1f; // 1 = open sky .. 0 = fully under a roof (WeatherManager drives it)

        AudioStreamPlayer _light, _heavy, _retail;   // _retail = retail's own defaultrainambience bed (ripped 2026-09-03), under the two freesound layers
        AudioEffectLowPassFilter _lp;
        string _busName;           // the name AudioServer ACTUALLY gave us (it dedupes "Rain"->"Rain 2"); own it for the players + the removal
        bool _busAdded;
        float _lightDb = -80f, _heavyDb = -80f, _retailDb = -80f, _lastCut = -1f;

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            int idx = AudioServer.BusCount;
            AudioServer.AddBus(idx);
            AudioServer.SetBusName(idx, "Rain");
            _busName = AudioServer.GetBusName(idx);   // the real name -> use THIS everywhere so a second instance can't cross-wire
            _busAdded = true;
            _lp = new AudioEffectLowPassFilter { CutoffHz = 20500f, Resonance = 0.25f };
            _lp.Set("db", 3);   // FILTER_24DB = 24 dB/oct (6 dB is just a -7dB tilt); low Q so the log sweep doesn't wah. Set by property name -- the C# enum type for Db shifted across Godot versions; the int constant is stable.
            AudioServer.AddBusEffect(idx, _lp);

            _light = MakeLoop("res://content/rain_light.wav");
            _heavy = MakeLoop("res://content/rain_heavy.wav");
            if (_light != null) AddChild(_light);
            if (_heavy != null) AddChild(_heavy);
            if (GameAudio.Clip("ambience", "defaultrainambience") is AudioStreamOggVorbis _rov) { _rov.Loop = true; _retail = new AudioStreamPlayer { Stream = _rov, Bus = _busName, VolumeDb = -80f }; AddChild(_retail); }
        }

        AudioStreamPlayer MakeLoop(string res)
        {
            var p = ProjectSettings.GlobalizePath(res);
            if (!System.IO.File.Exists(p)) return null;
            if (AudioStreamWav.LoadFromFile(p) is not AudioStreamWav w) return null;
            // ⚠ LoopMode.Forward loops to LoopEnd, which DEFAULTS TO 0 -> the player wraps at sample 0 and plays one
            // frame, silent (tinyclaw/fable). The wavs have no smpl chunk, so set the loop end to the whole file in
            // SAMPLES explicitly.
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopEnd = (int)(w.GetLength() * w.MixRate + 0.5f);
            return new AudioStreamPlayer { Stream = w, Bus = _busName, VolumeDb = -80f };   // silent until _Process slews it up
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            float rint = Mathf.Clamp(Intensity, 0f, 1f);
            float shelter = Mathf.Clamp(Shelter, 0f, 1f);
            float dt = (float)delta;

            // LIGHT bed: audible from the first drop, up to full by ~half intensity (light rain IS mostly this layer).
            float lightTgt = Mathf.Lerp(-28f, -6f, Mathf.Clamp(rint / 0.5f, 0f, 1f));   // -6dB global trim (master: rain falling sound too loud)
            // HEAVY roar: only joins past ~0.5 rint (Default Rain tops out at 0.7, so heavy rain gets its own voice).
            float heavyAmt = Mathf.Clamp((rint - 0.5f) / 0.5f, 0f, 1f);
            float heavyTgt = Mathf.Lerp(-26f, -9f, heavyAmt);   // -6dB global trim to match the light bed

            float shelterDb = Mathf.Lerp(-7f, 0f, shelter);   // the roof cuts direct rain a touch; the low-pass does the muffle. Depth is ROOF-correct -- a permeable CANOPY is softened by capping its Shelter contribution in WeatherManager (tinyclaw), not by weakening this shared curve.

            // NO thunder duck (master dropped it): the claps clear the rain bed by ~8-9dB on their own, so dipping the
            // rain under them was pure impact-polish that read as a mixer glitch. Rain level tracks intensity + shelter only.
            _lightDb = SlewLayer(_light, _lightDb, rint > 0.02f ? lightTgt + shelterDb : -80f, dt);
            _retailDb = SlewLayer(_retail, _retailDb, rint > 0.02f ? lightTgt + shelterDb - 4f : -80f, dt);   // retail bed sits 4 dB under the light layer
            _heavyDb = SlewLayer(_heavy, _heavyDb, heavyAmt > 0.02f ? heavyTgt + shelterDb : -80f, dt);

            // shelter low-pass: sweep the cutoff in LOG domain (a linear sweep sounds like a wah) -- ~20kHz open
            // outdoors, ~900Hz fully under cover. Change-guarded so it's not rewritten every idle frame.
            // 2200 Hz floor, was 900 (strawberry 2026-09-05 "change the muffle effect in buildings to not mute rain,
            // but make it still audible"). 900 with a 24 dB/oct slope puts the whole hiss of rain -- which lives well
            // above 2 kHz -- below the knee, so indoors read as silence rather than as shelter. 2200 keeps the body of
            // the sound present and still audibly dulled. The -7 dB shelter trim below is untouched; it was never the
            // part doing the swallowing.
            float cut = Mathf.Exp(Mathf.Lerp(Mathf.Log(20500f), Mathf.Log(2200f), 1f - shelter));
            if (_lp != null && Mathf.Abs(cut - _lastCut) > 1f) { _lastCut = cut; _lp.CutoffHz = cut; }
        }

        // SLEW the volume toward the goal (~180 dB/s) so a layer coming in or going out ramps instead of
        // stepping (a 55 dB jump in one frame is an audible click; the light loop's first sample is -3.7 dBFS). Only
        // Stop() when genuinely silent. Start at a RANDOM offset so every shower isn't the same excerpt.
        static float SlewLayer(AudioStreamPlayer pl, float cur, float goal, float dt)
        {
            if (pl == null) return cur;
            float next = Mathf.MoveToward(cur, goal, 180f * dt);
            if (next > -70f)
            {
                if (!pl.Playing)
                {
                    double len = (pl.Stream as AudioStreamWav)?.GetLength() ?? 0.0;
                    pl.Play(len > 0.0 ? (float)(GD.Randf() * len) : 0f);
                }
                if (Mathf.Abs(pl.VolumeDb - next) > 0.05f) pl.VolumeDb = next;   // change-guard when stable
            }
            else if (pl.Playing) pl.Stop();
            return next;
        }

        public override void _ExitTree()
        {
            if (!_busAdded) return;
            int idx = AudioServer.GetBusIndex(_busName);   // remove BY the name we were actually given (index shifts) + only if we added it
            if (idx >= 0) AudioServer.RemoveBus(idx);
            _busAdded = false;
        }
    }
}
