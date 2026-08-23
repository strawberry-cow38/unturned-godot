using System;
using System.IO;

namespace UnturnedGodot.BugReport
{
    /// <summary>What the reporter's microphone actually delivered.
    ///
    /// `Ok` is decided from AMPLITUDE, never from a frame count or a device list, and that is the whole
    /// point of this type. Measured on this box (2026-08-23): with NO audio hardware at all,
    /// <c>AudioServer.GetInputDeviceList()</c> still returns <c>["Default"]</c> and an
    /// <c>AudioEffectCapture</c> still delivers 32256-90112 frames per second — of exact digital silence
    /// (peak 0.000000000, zero non-zero samples). So both of the obvious "is there a mic?" checks pass on a
    /// machine that cannot possibly hear anything, and would pass identically on a player's machine with a
    /// muted, unplugged or permission-denied microphone. A silent report tagged "ok" is worse than no
    /// report, because nobody can tell it from a quiet bug.</summary>
    public static class AudioStatusWire
    {
        /// <summary>The name the SERVER's whitelist expects. NOT ToString().ToLowerInvariant().
        ///
        /// That is how this shipped and it was wrong in the one case that matters: AudioStatus.NoMic
        /// stringifies to "nomic", the server accepts {"ok","no_mic","disabled","error","typed"}, and
        /// anything else is stored as "unknown". So the single verdict this entire feature exists to
        /// produce -- "there was no usable microphone" -- was being silently discarded on arrival, and the
        /// report read "audio: unknown", indistinguishable from a schema-version skew.
        ///
        /// Three green suites missed it because none of them crossed the boundary: the in-engine test
        /// asserted the CLIENT's spelling (so it agreed with the bug), the service tests never touched
        /// audio_status at all, and the live end-to-end script hand-wrote "ok" -- the one value that
        /// round-trips. A real assertion, on a real file, answering a different question than the one that
        /// mattered. The mapping is explicit here so the wire name cannot drift with a C# rename.</summary>
        public static string Of(AudioStatus s) => s switch
        {
            AudioStatus.Ok => "ok",
            AudioStatus.NoMic => "no_mic",
            AudioStatus.Disabled => "disabled",
            AudioStatus.Error => "error",
            AudioStatus.Typed => "typed",
            _ => "unknown",
        };
    }

    public enum AudioStatus
    {
        Ok,          // real signal present
        NoMic,       // frames arrived but they were silence -- no device, muted, or denied
        Disabled,    // capture never started (turned off, or no capture path on this build)
        Error,       // the audio stack threw
        Typed,       // console `report <text>` -- there was never any audio
    }

    /// <summary>Signed 16-bit PCM WAV, mono, written at the engine's own mix rate.
    ///
    /// The rate is NOT a guess: the capture effect sits on an output bus, so <c>AudioServer.GetMixRate()</c>
    /// is the rate those frames were produced at. Measured 44100 with continuous draining giving 45042 Hz
    /// over 3 s (and 47020 at a forced 48000), which is the mix rate within measurement noise.</summary>
    public static class Wav
    {
        public const int HeaderBytes = 44;

        public static byte[] Encode(float[] mono, int sampleRate)
        {
            if (mono == null) mono = Array.Empty<float>();
            if (sampleRate < 8000 || sampleRate > 48000)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "expected 8000..48000");

            int dataBytes = mono.Length * 2;
            var ms = new MemoryStream(HeaderBytes + dataBytes);
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);                       // PCM fmt chunk size
            w.Write((short)1);                 // format 1 = PCM
            w.Write((short)1);                 // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);           // byte rate = rate * channels * bytesPerSample
            w.Write((short)2);                 // block align
            w.Write((short)16);                // bits
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            foreach (float f in mono)
            {
                // Clamp before scaling: a float above 1.0 would wrap to a large negative short and turn a
                // loud passage into a burst of noise.
                float c = f > 1f ? 1f : (f < -1f ? -1f : f);
                w.Write((short)Math.Round(c * short.MaxValue));
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Duration from the DATA, the way a server should compute it -- never from a header field,
        /// which is independent of the payload and is written by whoever sent the file.</summary>
        public static double DurationSeconds(byte[] wav)
        {
            if (wav == null || wav.Length < HeaderBytes) return 0;
            int rate = BitConverter.ToInt32(wav, 24);
            int dataLen = BitConverter.ToInt32(wav, 40);
            if (rate <= 0) return 0;
            return Math.Min(dataLen, wav.Length - HeaderBytes) / (double)(rate * 2);
        }
    }

    /// <summary>Accumulates captured audio and decides whether a microphone was really there.</summary>
    public sealed class AudioBuffer
    {
        readonly float[] _buf;
        int _count;
        public long DiscardedFrames;   // reported so a garbled transcript can be blamed correctly
        public float Peak { get; private set; }
        double _sumSquares;

        /// <summary>Anything at or below this is treated as no signal. Dummy-driver silence measures
        /// EXACTLY 0.0, so the floor exists for a real-but-dead mic emitting dither, not for this box.</summary>
        public const float SilenceFloor = 1e-4f;

        public AudioBuffer(int maxFrames) { _buf = new float[Math.Max(1, maxFrames)]; }

        public int Count => _count;
        public bool Full => _count >= _buf.Length;
        public float Rms => _count == 0 ? 0f : (float)Math.Sqrt(_sumSquares / _count);

        /// <summary>Godot hands back stereo pairs; the report only needs mono, and halving the size halves
        /// the upload. Takes (left,right) as two parallel arrays so this stays free of engine types.</summary>
        public void AppendStereo(float[] left, float[] right, int frames)
        {
            for (int i = 0; i < frames && _count < _buf.Length; i++)
            {
                float m = right == null ? left[i] : 0.5f * (left[i] + right[i]);
                _buf[_count++] = m;
                float a = m < 0 ? -m : m;
                if (a > Peak) Peak = a;
                _sumSquares += (double)m * m;
            }
        }

        public float[] ToArray()
        {
            var outp = new float[_count];
            Array.Copy(_buf, outp, _count);
            return outp;
        }

        /// <summary>Silence means no usable microphone, however many frames arrived.</summary>
        public AudioStatus Classify() =>
            _count == 0 ? AudioStatus.NoMic : (Peak > SilenceFloor ? AudioStatus.Ok : AudioStatus.NoMic);
    }

    public enum ReportState { Idle, Recording, Finalizing }

    /// <summary>The press/hold/release machine, engine-free so it can be tested without booting Godot.</summary>
    public sealed class ReportSession
    {
        public const double MaxSeconds = 60.0;
        public const double TapSeconds = 0.4;

        public ReportState State { get; private set; } = ReportState.Idle;
        public double Elapsed { get; private set; }
        public bool WasTap { get; private set; }
        public bool HitTimeout { get; private set; }

        /// <summary>Returns true if this press actually starts a recording. Echo (key auto-repeat) and a
        /// press while already recording must not restart it.</summary>
        public bool Press()
        {
            if (State != ReportState.Idle) return false;
            State = ReportState.Recording;
            Elapsed = 0;
            WasTap = false;
            HitTimeout = false;
            return true;
        }

        /// <summary>Advance time. Returns true when the cap forces a finalize, so the caller can say so
        /// rather than appearing to drop the recording.</summary>
        public bool Tick(double dt)
        {
            if (State != ReportState.Recording) return false;
            Elapsed += dt;
            if (Elapsed < MaxSeconds) return false;
            HitTimeout = true;
            State = ReportState.Finalizing;
            return true;
        }

        /// <summary>Release, focus loss, or alt-tab. Returns true if this ends a live recording. A very
        /// short hold is a screenshot-only report, which is a feature rather than a failed recording.</summary>
        public bool Release()
        {
            if (State != ReportState.Recording) return false;
            WasTap = Elapsed < TapSeconds;
            State = ReportState.Finalizing;
            return true;
        }

        public void Done() { State = ReportState.Idle; Elapsed = 0; }
    }
}
