using Godot;

namespace UnturnedGodot
{
    // Layered rain SOUNDSCAPE (Fable's build plan): intensity is WHICH LAYERS play, not one loop turned up. A light
    // bed from the first drop, a heavy roar that only joins in a downpour. Both on a dedicated "Rain" bus so a single
    // low-pass filter can muffle the whole mix under a roof. Volumes lerp in dB (broadband noise is logarithmic, so dB
    // is the perceptually-linear curve). The loop seams are BAKED into the assets (2s crossfade), so LoopMode.Forward
    // and the runtime does no crossfade -- one fewer clock to get right under the movie writer.
    //
    // ⚠⚠ plain AudioStreamPlayers -> the Rain bus -> Master, NEVER SoundBus.Emit (that's the zombie-HEARING path;
    // a continuous rain loop through it would be permanent map-wide zombie aggro). freesound CC0: _lynks #595717,
    // AdrianoAnjos #616446 (see content/CREDITS.md).
    public partial class RainAudio : Node
    {
        public float Intensity;    // rint 0..1 (WeatherManager drives it)
        public float Shelter = 1f; // 1 = open sky .. 0 = fully under a roof (WeatherManager drives it)
        public float Duck = 1f;    // 1 = normal .. <1 = pre-dipped so a thunderclap lands in a hole (WeatherManager drives it)

        AudioStreamPlayer _light, _heavy;
        AudioEffectLowPassFilter _lp;
        int _busIdx = -1;

        public override void _Ready()
        {
            // The Rain bus MUST exist before any player assigns Bus="Rain" -- a bad/missing name silently falls back
            // to Master and the shelter filter would quietly do nothing (Fable). New buses send to Master by default.
            _busIdx = AudioServer.BusCount;
            AudioServer.AddBus(_busIdx);
            AudioServer.SetBusName(_busIdx, "Rain");
            _lp = new AudioEffectLowPassFilter { CutoffHz = 20500f };   // wide open outdoors
            AudioServer.AddBusEffect(_busIdx, _lp);

            _light = MakeLoop("res://content/rain_light.wav");
            _heavy = MakeLoop("res://content/rain_heavy.wav");
            if (_light != null) AddChild(_light);
            if (_heavy != null) AddChild(_heavy);
        }

        static AudioStreamPlayer MakeLoop(string res)
        {
            var p = ProjectSettings.GlobalizePath(res);
            if (!System.IO.File.Exists(p)) return null;
            if (AudioStreamWav.LoadFromFile(p) is not AudioStreamWav w) return null;
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;   // the seam is baked into the asset -> just loop the whole file
            return new AudioStreamPlayer { Stream = w, Bus = "Rain", VolumeDb = -80f };   // silent until _Process brings it up
        }

        public override void _Process(double delta)
        {
            float rint = Mathf.Clamp(Intensity, 0f, 1f);
            float shelter = Mathf.Clamp(Shelter, 0f, 1f);
            float duck = Mathf.Clamp(Duck, 0f, 1f);

            // LIGHT bed: audible from the first drop, up to full by ~half intensity (light rain IS mostly this layer).
            float lightDb = Mathf.Lerp(-26f, -8f, Mathf.Clamp(rint / 0.5f, 0f, 1f));
            // HEAVY roar: only joins past ~0.5 rint (Default Rain tops out at 0.7, so heavy rain gets its own voice).
            float heavyAmt = Mathf.Clamp((rint - 0.5f) / 0.5f, 0f, 1f);
            float heavyDb = Mathf.Lerp(-24f, -6f, heavyAmt);

            float shelterDb = Mathf.Lerp(-7f, 0f, shelter);   // a roof cuts the direct rain a touch (the low-pass does the muffle)
            float duckDb = Mathf.Lerp(-9f, 0f, duck);         // pre-dip for a thunderclap

            SetLayer(_light, rint > 0.02f ? lightDb + shelterDb + duckDb : -80f);
            SetLayer(_heavy, heavyAmt > 0.02f ? heavyDb + shelterDb + duckDb : -80f);

            // shelter low-pass: sweep the cutoff in LOG domain (a linear sweep sounds like a wah) -- ~20kHz open
            // outdoors, ~900Hz fully under cover.
            if (_lp != null) _lp.CutoffHz = Mathf.Exp(Mathf.Lerp(Mathf.Log(20500f), Mathf.Log(900f), 1f - shelter));
        }

        // Play only while audible; stop when silent so clear weather costs nothing. Transitions happen at ~-80 dB
        // so there's no start/stop pop.
        static void SetLayer(AudioStreamPlayer pl, float db)
        {
            if (pl == null) return;
            if (db > -79f) { pl.VolumeDb = db; if (!pl.Playing) pl.Play(); }
            else if (pl.Playing) pl.Stop();
        }

        public override void _ExitTree()
        {
            int idx = AudioServer.GetBusIndex("Rain");   // remove by NAME (index can shift) so we don't leak the bus across a scene reload
            if (idx >= 0) AudioServer.RemoveBus(idx);
        }
    }
}
