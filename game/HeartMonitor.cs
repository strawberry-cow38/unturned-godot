using Godot;

namespace UnturnedGodot
{
    // The Science_3 patient monitor (strawberry: "change the science 3 prop to show an actual pattern, with the same
    // amplitude as the vanilla one, turns on/off like a tv. it draws the same ecg every x seconds. if you can source a
    // sound too... also add a flag for flatline vs on. for now give it to random units across the map").
    //
    // The vanilla prop already draws an ECG -- as GEOMETRY. Its palette is 2x2, so there is no texture to animate: the
    // zigzag is twelve real vertices lying 2.1 mm proud of the screen face. That is why this does not modify the prop
    // at all. It lays an opaque shader quad 4 mm in front, which covers the modelled trace and animates in its place,
    // and every proportion in the shader is measured off those twelve vertices so the moving trace has the amplitude
    // the static one had.
    //
    // Power is TVDevice's gate, deliberately: mains OR a wire into its own port. A monitor is exactly the thing you
    // would run off a generator when the grid dies.
    public partial class HeartMonitor : Node3D
    {
        public const string PropName = "Science_3";

        // ---- the screen face, measured off Science_3.obj (mesh local units; the prop is authored Z-up) -------------
        internal const float ScreenY = 0.2160f;      // the dark screen quad's plane
        internal const float TraceY = 0.2181f;       // the vanilla drawn trace, 2.1 mm proud of it
        internal const float OverlayGap = 0.004f;    // ...so the overlay must clear THAT, not the screen
        internal const float ScreenX0 = -0.3171f, ScreenX1 = 0.3204f;
        internal const float ScreenZ0 = 1.2020f, ScreenZ1 = 1.7970f;

        /// <summary>Seconds per beat. 60/72 bpm -- a resting adult, and slow enough that the sweep reads as a repeat
        /// rather than as a blur.</summary>
        internal const float BeatPeriod = 60f / 72f;
        internal const float FlatlinePeriod = 2.2f;   // the sweep still travels; there is just nothing on it

        /// <summary>How many placed monitors are alive rather than flatlined. Most wards are not a morgue, but a
        /// flatline is the interesting one to come across, so it is not rare either.</summary>
        internal const float AliveChance = 0.7f;

        /// <summary>Collider meta, so looking at or shooting the prop body finds the device -- the same route
        /// TVDevice.HitMeta takes, and it buys the F toggle and the screen shoot-out off one tag.</summary>
        public static readonly StringName HitMeta = "heartmonitor";

        const float BrownoutHz = 11f, BrownoutDepth = 0.72f;
        float _brownoutLeft, _brownoutPhase;
        public bool DebugBrownout => _brownoutLeft > 0f;
        public bool DebugScreenShot => _screenShot;

        bool _alive = true, _on = true, _lit, _screenShot;
        ShaderMaterial _mat;
        MeshInstance3D _screen;
        AudioStreamPlayer3D _audio;
        float _clock, _lastBeat = -1f;
        ConnectionPort _plug;

        public bool Alive => _alive;
        public bool DebugLit => _lit;
        public ShaderMaterial DebugMaterial => _mat;
        public MeshInstance3D DebugScreen => _screen;
        public float DebugPeriod => _alive ? BeatPeriod : FlatlinePeriod;

        /// <summary>Live on the mains OR on a wire into the port -- the same two-source gate TVDevice.HasFeed uses.
        /// A ward monitor is precisely the thing someone runs off a generator once the grid is gone.</summary>
        public bool HasFeed => PowerNet.GlobalPower || (_plug != null && GodotObject.IsInstanceValid(_plug) && _plug.Powered);

        public static bool IsMonitorProp(string prop) => prop == PropName;

        public static HeartMonitor Make(MeshInstance3D bodyMi, bool alive)
        {
            var hm = new HeartMonitor { _alive = alive, Transform = bodyMi.Transform };
            hm.Build();
            return hm;
        }

        void Build()
        {
            // A quad the size of the screen face, standing in the prop's own local frame: the prop is authored Z-up,
            // so the screen spans X (width) and Z (height) and faces +Y.
            float w = ScreenX1 - ScreenX0, h = ScreenZ1 - ScreenZ0;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            void V(float u, float v)
            {
                // ROTATED 180 (strawberry: "the screen is upside down, 180 the whole display"). Both axes flip: the
                // prop's screen face is modelled with its own orientation and the overlay was laid on in the quad's,
                // so the trace ran the wrong way AND swept the wrong way. Flipping u as well as v turns the whole
                // display rather than merely mirroring it, which would have fixed the picture and left the sweep
                // travelling right to left.
                st.SetUV(new Vector2(1f - u, v));
                st.AddVertex(new Vector3(ScreenX0 + u * w, TraceY + OverlayGap, ScreenZ0 + v * h));
            }
            V(0, 0); V(1, 0); V(1, 1);
            V(0, 0); V(1, 1); V(0, 1);
            st.GenerateNormals();
            var mesh = st.Commit();

            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/ecg.gdshader") };
            _mat.SetShaderParameter("time_s", 0f);
            _mat.SetShaderParameter("alive", _alive ? 1f : 0f);
            _mat.SetShaderParameter("lit", 0f);
            _mat.SetShaderParameter("period", DebugPeriod);
            _mat.SetShaderParameter("seed", GD.Randf() * 100f);

            _screen = new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = _mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(_screen);

            _audio = new AudioStreamPlayer3D { UnitSize = 6f, MaxDistance = 22f, VolumeDb = -12f, Bus = "Master" };
            AddChild(_audio);
        }

        public override void _Ready()
        {
            AddToGroup("heartmonitors");
            AddToGroup("deployables");   // so a wire can find it the way the other fixtures are found
            Refresh();
        }

        /// <summary>Switch the unit itself (the map-making flag's other half -- a monitor can be off as well as
        /// flatlined, and they look nothing alike).</summary>
        public void Toggle() { _on = !_on; Refresh(); }

        public void SetAlive(bool alive)
        {
            if (_alive == alive) return;
            _alive = alive;
            _mat?.SetShaderParameter("alive", alive ? 1f : 0f);
            _mat?.SetShaderParameter("period", DebugPeriod);
            _lastBeat = -1f;   // don't carry a half-finished beat across the change
        }

        /// <summary>Ride a grid sag. Gated on being LIT and on being mains-fed: a monitor running off its own wire
        /// never saw the dip, and a dark one must not blink back to life for half a second.</summary>
        public void FlickerPulse(float durationSec = 0.6f)
        {
            if (!_lit || _screenShot) return;
            if (_plug != null && GodotObject.IsInstanceValid(_plug) && _plug.Powered) return;
            _brownoutLeft = Mathf.Max(0.05f, durationSec);
            _brownoutPhase = 0f;
        }

        /// <summary>One bullet kills the display for good; the stand keeps standing. Returns FALSE when it is already
        /// dead so the shot falls through to the prop's own health instead of being swallowed -- the same contract
        /// TVDevice.ShootOutScreen has, and for the same reason: swallowing it would make a shot-out monitor
        /// bulletproof.</summary>
        public bool ShootOutScreen()
        {
            if (_screenShot) return false;
            _screenShot = true;
            _brownoutLeft = 0f;
            Refresh();
            return true;
        }

        public void Refresh()
        {
            bool want = _on && HasFeed && !_screenShot;
            if (want == _lit && _screen != null && _screen.Visible == want) return;
            _lit = want;
            if (_screen != null) _screen.Visible = want;
            _mat?.SetShaderParameter("lit", want ? 1f : 0f);
            if (!want) _audio?.Stop();
        }

        bool _feedWas;

        public override void _Process(double delta)
        {
            // Poll the WHOLE feed, mains and wire together -- the same lesson TVDevice learned today: relying on a
            // push means the one caller that pushes works and every other route leaves the unit lit through a
            // blackout.
            if (HasFeed != _feedWas) { _feedWas = HasFeed; Refresh(); }
            if (!_lit) return;

            _clock += (float)delta;
            _mat?.SetShaderParameter("time_s", _clock);

            // The sag is a square stutter on the picture level, not a fade: mains droop stutters.
            if (_brownoutLeft > 0f)
            {
                _brownoutLeft -= (float)delta;
                _brownoutPhase += (float)delta * BrownoutHz;
                float f = Mathf.PosMod(_brownoutPhase, 1f) < 0.5f ? 1f - BrownoutDepth : 1f;
                _mat?.SetShaderParameter("sag", f);
                if (_brownoutLeft <= 0f) { _brownoutLeft = 0f; _brownoutPhase = 0f; _mat?.SetShaderParameter("sag", 1f); }
            }

            // The beep fires when the sweep passes the R spike, so the sound is ON the visible beat rather than merely
            // at the same rate as it -- those are indistinguishable until you watch and listen at once, and then the
            // second one is obviously wrong.
            float period = DebugPeriod;
            float phase = Mathf.PosMod(_clock, period) / period;
            if (_alive)
            {
                if (phase < _lastBeat || _lastBeat < 0f) { /* wrapped */ }
                if (_lastBeat >= 0f && _lastBeat < RPhase && phase >= RPhase) Beep(BeepHz, 0.09f);
                _lastBeat = phase;
            }
            else if (!_audio.Playing) Beep(FlatHz, 2.5f, loopish: true);
        }

        internal const float RPhase = 0.42f;   // matches the shader's R position, so sound and picture agree
        const float BeepHz = 1046f;            // C6 -- the thin electronic blip a monitor actually makes
        const float FlatHz = 880f;             // A5, held: the flatline tone

        /// <summary>Synthesised rather than sourced: nothing in the ripped audio is a monitor beep, and a beep is two
        /// numbers (a frequency and an envelope). Generating it keeps the asset out of the repo and makes the pitch a
        /// constant someone can read instead of a file someone has to open.</summary>
        void Beep(float hz, float seconds, bool loopish = false)
        {
            const int rate = 22050;
            int n = Mathf.Max(1, (int)(seconds * rate));
            var pcm = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                // A short attack and a long-ish decay; a raw gated sine clicks at both ends.
                float env = loopish
                    ? Mathf.Min(1f, t / 0.02f) * Mathf.Min(1f, (seconds - t) / 0.05f)
                    : Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t * 26f);
                short s = (short)(Mathf.Sin(t * hz * Mathf.Tau) * env * 9000f);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            _audio.Stream = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = pcm };
            _audio.Play();
        }

        /// <summary>Wire port, so a generator can run one. Mirrors the other map fixtures' single consumer input.</summary>
        public void AttachPort(ConnectionPort port) => _plug = port;
    }
}
