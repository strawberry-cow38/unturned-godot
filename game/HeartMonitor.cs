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

        /// <summary>Seconds per beat. 60 bpm, not a human resting rate (strawberry: "the only place where there are
        /// live creatures are the polysol federation aliens in the basement of scorpion 7 who are in deep hibernation,
        /// not actively excercising"). Also reads better: one beat per second is slow enough that the sweep is
        /// legibly a repeat rather than a blur.</summary>
        internal const float BeatPeriod = 60f / 60f;
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
        /// <summary>What the player was last handed. Exposed because "the assets load" and "the monitor makes a noise"
        /// are different claims, and the bug that put a silent monitor on the ward passed the first one.</summary>
        public AudioStream DebugStream => _audio?.Stream;
        public bool DebugAudioPlaying => _audio != null && _audio.Playing;

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

            _audio = new AudioStreamPlayer3D { UnitSize = 7f, MaxDistance = 26f, VolumeDb = -3f, Bus = "Master" };
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
                if (_lastBeat >= 0f && _lastBeat < RPhase && phase >= RPhase) Beep(sustained: false);
                _lastBeat = phase;
            }
            else if (!_audio.Playing) Beep(sustained: true);   // the clip loops, so this fires once and holds
        }

        internal const float RPhase = 0.42f;   // matches the shader's R position, so sound and picture agree

        // ---- THE BEEP: real recordings, not a synthesised tone --------------------------------------------------
        //
        // strawberry: "the beep tones are very soft and generic. not like an actual ECG". VoX: source a royalty-free
        // effect and pull the pieces out of it with the same amplitude analysis the CRT work used.
        //
        // Source: "Heart Monitor Beep" by samfk360, CC0 1.0 Universal (public domain dedication), via Wikimedia
        // Commons. 28 s of a monitor beeping, accelerating, then flatlining -- so both halves are in one clip.
        // The pieces were CUT BY MEASUREMENT rather than by ear: a 5 ms amplitude envelope over the whole file found
        // 18 discrete blips of ~180 ms each and then one unbroken 11.4 s run, which is the flatline. One steady early
        // blip became ecg_beep.ogg. The flatline half is not a cut of that run at all -- see below.
        //
        // The FLATLINE half is SYNTHESISED, and that is not a shortcut -- it is what the measurement asked for.
        //
        // Cutting it from the recording went wrong twice, and both failures are worth keeping written down:
        //
        //   1. As a crossfaded .ogg, the crossfade was correct in PCM and then wrecked by the encoder. Vorbis is a
        //      lapped transform and does not return the sample count it was given: a 13230-frame body decoded to
        //      13312, putting the real wrap 82 samples from the faded join, and the loop stepped 22801 -- 4.9x the
        //      wave's own p95, 35% of full scale, a tick every 600 ms. My "verified the seam" check had read frame
        //      13230, an ordinary interior sample, and reported a clean 4572. A check at the wrong offset agrees
        //      with the bug.
        //   2. As a WAV that fixed the wrap, it still PULSED. The tone is exactly 880 Hz and 880 x 0.600 s is 528
        //      whole cycles, so the two ends of the body were already in phase -- and an equal-POWER (cos/sin)
        //      crossfade of two IN-PHASE copies sums to 1.414, a +3.01 dB bulge over the 50 ms fade, once per loop.
        //      Equal-power is the right law for uncorrelated signals and the wrong one here. The pulse was mine.
        //
        // The FFT that diagnosed (2) also showed the source needs no repair: 880.00 Hz with zero measurable drift
        // over the whole 11.4 s run and +/-0.5% of level. So the tone is rebuilt from its own measured partials --
        // odd harmonics only, H1 1.0 / H3 0.1104 / H5 0.0396 / H7 0.0200 / H9 0.0123, at the measured phases -- which
        // keeps the timbre and drops the noise floor. Level is matched by RMS rather than by peak, because a
        // synthesised tone has a different crest factor and peak-matching would ship it audibly louder than the clip
        // that was approved.
        //
        // The loop body is 4410 samples = 176 WHOLE cycles at 880 Hz, so the wrap is exact by construction and there
        // is no crossfade left to get wrong. A 30 ms lead-in ramped from silence plays once (LoopBegin sits after it)
        // because the bare tone starts at full amplitude, and switching a flatlined monitor on was itself a click.
        //
        // The BEEP stays a real recording, and stays .ogg: it is one-shot, so it has no seam to protect, and its
        // edges already decay to near zero on their own (its last 35 ms fall 12325 -> 7). Trimming its tail, as was
        // first suggested, would cut into that decay and MAKE a click where measurement found none -- all 7
        // discontinuities in the first posted preview were the flatline's wrap, none were in the beep. A beep is a
        // struck, resonant thing worth recording; a flatline is a steady electronic tone, which is worth generating.
        internal const string BeepPath = "res://content/ecg_beep.ogg";
        internal const string FlatPath = "res://content/ecg_flat.wav";
        internal const float SourceHz = 880f;   // measured off the clip; both pieces share it

        static AudioStream _beepStream, _flatStream;

        /// <summary>Loaded off DISK, not through res://. An asset dropped into content/ has never been through the
        /// editor's import step, so it has no .import sidecar and GD.Load returns null for it -- silently, which reads
        /// exactly like "the sound is quiet". Every other runtime asset in this project (gun shots, albedos, prop
        /// meshes) is loaded the same way, by absolute path: Viewmodel.LoadOgg.
        ///
        /// A .wav is loaded as a NATIVELY-LOOPING AudioStreamWav; anything else as one-shot Vorbis. The extension
        /// picks the path because the two roles genuinely differ -- see the note above on why the looping half cannot
        /// be an .ogg.</summary>
        internal static AudioStream LoadClip(string path, bool loop)
        {
            string p = ProjectSettings.GlobalizePath(path);
            if (!System.IO.File.Exists(p)) return null;
            if (path.EndsWith(".wav")) return LoadWavLooped(p, loop);
            var ogg = AudioStreamOggVorbis.LoadFromFile(p);
            if (ogg != null) ogg.Loop = loop;
            return ogg;
        }

        /// <summary>Minimal RIFF parse -> AudioStreamWav with real loop points, read from the file's own `smpl` chunk.
        ///
        /// The loop point lives in the ASSET, not in a constant here, because it is a property of how the waveform was
        /// cut and a number in C# would quietly drift from the file it describes the first time either is regenerated.
        ///
        /// The flatline does not loop over its whole length. It opens with a 30 ms lead-in ramped up from silence --
        /// otherwise the clip's first sample is most of full scale and switching a flatlined monitor on is a click --
        /// and `smpl` puts LoopBegin AFTER that lead, so the fade plays once and every wrap thereafter lands on the
        /// crossfaded join instead. That is exactly what LoopBegin is for, and it is why this cannot simply loop 0..n.
        ///
        /// Deliberately NOT Viewmodel.LoadWavLooped, which trims leading and trailing silence before setting its loop
        /// points. That is right for a ripped clip padded with silence and would be actively wrong here twice over: it
        /// would eat the lead-in that exists precisely to start at silence, and it would move the two edges the body
        /// was crossfaded to join.</summary>
        static AudioStream LoadWavLooped(string absPath, bool loop)
        {
            var b = System.IO.File.ReadAllBytes(absPath);
            int channels = 1, rate = 22050, bits = 16, dataOff = -1, dataLen = 0, loopBegin = -1, loopEnd = -1;
            for (int i = 12; i + 8 <= b.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
                int sz = System.BitConverter.ToInt32(b, i + 4);
                if (id == "fmt ") { channels = System.BitConverter.ToInt16(b, i + 10); rate = System.BitConverter.ToInt32(b, i + 12); bits = System.BitConverter.ToInt16(b, i + 22); }
                else if (id == "data") { dataOff = i + 8; dataLen = sz; }
                else if (id == "smpl" && sz >= 60 && System.BitConverter.ToInt32(b, i + 8 + 28) >= 1)
                {
                    loopBegin = System.BitConverter.ToInt32(b, i + 8 + 36 + 8);
                    loopEnd = System.BitConverter.ToInt32(b, i + 8 + 36 + 12) + 1;   // smpl `end` is INCLUSIVE
                }
                i += 8 + sz + (sz & 1);
            }
            if (dataOff < 0 || bits != 16 || dataOff + dataLen > b.Length) return null;
            int frames = dataLen / (2 * channels);
            if (loopBegin < 0 || loopEnd <= loopBegin || loopEnd > frames) { loopBegin = 0; loopEnd = frames; }
            var pcm = new byte[dataLen];
            System.Array.Copy(b, dataOff, pcm, 0, dataLen);
            return new AudioStreamWav
            {
                Data = pcm,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = rate, Stereo = channels == 2,
                LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
                LoopBegin = loopBegin, LoopEnd = loopEnd,
            };
        }

        void Beep(bool sustained)
        {
            if (sustained) _flatStream ??= LoadClip(FlatPath, true);
            else _beepStream ??= LoadClip(BeepPath, false);
            var st = sustained ? _flatStream : _beepStream;
            if (st == null) return;   // asset missing -> silence, not a crash
            _audio.Stream = st;
            _audio.Play();
        }

        /// <summary>Wire port, so a generator can run one. Mirrors the other map fixtures' single consumer input.</summary>
        public void AttachPort(ConnectionPort port) => _plug = port;
    }
}
