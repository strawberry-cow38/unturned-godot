using Godot;

namespace UnturnedGodot
{
    /// <summary>The geiger counter (strawberry 2026-09-11: "synth a geiger counter thats frequency scales with
    /// radiation").
    ///
    /// FREQUENCY, not volume, carries the information. A dose you can only hear as "louder" is a dose you
    /// cannot judge -- but click RATE is something people read instinctively, because every geiger counter in
    /// every film taught them to. Walking toward a hot spot should be audible as the gaps closing, which is
    /// also what makes the edge of a zone a usable warning rather than a surprise.
    ///
    /// POISSON, not a metronome. Real decay is random: the tube clicks at an average rate, not on a beat, and
    /// evenly-spaced clicks read immediately as a UI beep rather than an instrument. So the wait between
    /// clicks is drawn from an exponential distribution around the current mean rate -- the same thing the
    /// physics does, and it costs one log() per click.
    ///
    /// Silent below a floor, deliberately: background radiation ticking forever in a clean world is noise the
    /// player learns to stop hearing, which is exactly the wrong thing to train when the sound's whole job is
    /// to be alarming.</summary>
    public partial class GeigerCounter : Node
    {
        public static GeigerCounter Current;

        /// <summary>Whose dose drives the rate.</summary>
        public PlayerController Player;

        /// <summary>Dose below which the counter stays quiet. Roughly "you clipped the very edge and left".</summary>
        public static float SilentBelow = 0.02f;

        /// <summary>Clicks per second at the floor of the audible range, and at a full dose. The span is wide
        /// on purpose -- a factor of ~25 -- because that ratio is what makes the rate legible as a gradient
        /// rather than as two states.</summary>
        public static float MinRate = 1.2f, MaxRate = 30f;

        public static bool Enabled = true;

        /// <summary>Did the click clips actually load? A counter with null clips is SILENT and looks exactly
        /// like a correctly-quiet one at zero dose, so this is the only way a test can tell the difference.</summary>
        public bool ClipsLoaded => _clips != null && _clips.Length > 0 && _clips[0] != null && _clips[1] != null;

        readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[4];   // several, so fast rates overlap instead of cutting each other off
        AudioStream[] _clips;
        int _voice;
        double _nextIn;
        readonly RandomNumberGenerator _rng = new();

        public override void _Ready()
        {
            Current = this;
            AddToGroup("geiger");
            _rng.Randomize();
            // ⚠ SAME BUG AS THE WALKIE'S STATIC, shipped earlier today and silent by construction: these were
            // GD.Load, which returns NULL for a .wav with no .import sidecar. The counter ran its whole
            // Poisson schedule, called Play() on a null stream every time, and made no sound -- and nothing
            // failed, because a geiger that is correctly silent at zero dose looks identical to one that can
            // never make a sound at all. Found only when the walkie's static did the same thing and I had a
            // test watching the AudioStreamPlayer instead of the flag.
            _clips = new[]
            {
                (AudioStream)PlayerController.LoadWavOneShot("res://content/audio/geiger/geiger_click_1.wav"),
                (AudioStream)PlayerController.LoadWavOneShot("res://content/audio/geiger/geiger_click_2.wav"),
            };
            if (_clips[0] == null || _clips[1] == null)
                Log.Err("[geiger] click clips failed to load -- the counter will be silent");
            for (int i = 0; i < _voices.Length; i++)
            {
                _voices[i] = new AudioStreamPlayer { Bus = "Master", VolumeDb = -6f };
                AddChild(_voices[i]);
            }
            _nextIn = 1.0;
        }

        /// <summary>Mean clicks per second for a dose. Static and null-safe so a test can assert the curve
        /// without an audio device.</summary>
        public static float RateFor(float dose)
        {
            if (dose < SilentBelow) return 0f;
            float t = Mathf.Clamp((dose - SilentBelow) / Mathf.Max(0.001f, 1f - SilentBelow), 0f, 1f);
            // Squared so the rate climbs slowly at first and sharply near the top: the difference between a
            // dangerous dose and a lethal one should be more audible than the difference between two safe
            // ones, and a linear ramp spends most of its range on doses that do not matter yet.
            return Mathf.Lerp(MinRate, MaxRate, t * t);
        }

        public float DebugRate => RateFor(DebugForcedDose ?? (Player != null && GodotObject.IsInstanceValid(Player) ? Player.Radiation : 0f));

        /// <summary>Harness override, mirroring DeadzoneOverlay's.</summary>
        public float? DebugForcedDose;

        public override void _Process(double delta)
        {
            if (!Enabled || _clips == null) return;
            float dose = DebugForcedDose ?? (Player != null && GodotObject.IsInstanceValid(Player) ? Player.Radiation : 0f);
            float rate = RateFor(dose);
            if (rate <= 0f) { _nextIn = 1.0; return; }   // reset the wait so leaving does not bank a click

            _nextIn -= delta;
            if (_nextIn > 0.0) return;

            // Exponential inter-arrival: -ln(U)/rate. Clamped away from 0 so a pathological U cannot ask for
            // an unbounded number of clicks in one frame.
            float u = Mathf.Max(1e-4f, _rng.Randf());
            _nextIn = Mathf.Max(1.0f / MaxRate * 0.25f, -Mathf.Log(u) / rate);

            var p = _voices[_voice];
            _voice = (_voice + 1) % _voices.Length;
            p.Stream = _clips[_rng.RandiRange(0, _clips.Length - 1)];
            // A little level jitter per click. Identical amplitudes are the other half of what makes a repeated
            // sample sound like a repeated sample.
            p.VolumeDb = -6f + _rng.RandfRange(-2.5f, 1.5f);
            p.Play();
        }

        /// <summary>UG_GEIGER=&lt;dose&gt; to hear/verify it without finding a deadzone, mirroring
        /// DeadzoneOverlay.DebugAttach.</summary>
        public static GeigerCounter DebugAttach(Node root)
        {
            string s = System.Environment.GetEnvironmentVariable("UG_GEIGER");
            if (string.IsNullOrEmpty(s) || root == null || !float.TryParse(s, out float dose)) return null;
            var g = new GeigerCounter { DebugForcedDose = dose };
            root.AddChild(g);
            Log.Print($"[geiger] harness on at dose {dose:0.##} -> {RateFor(dose):0.#} clicks/s");
            return g;
        }
    }
}
