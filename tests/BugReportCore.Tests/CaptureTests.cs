using System;
using System.IO;
using NUnit.Framework;
using UnturnedGodot.BugReport;

namespace UnturnedGodot.BugReport.Tests
{
    [TestFixture]
    public class WavTests
    {
        static float[] Tone(int frames, float amp = 0.5f, int rate = 44100)
        {
            var a = new float[frames];
            for (int i = 0; i < frames; i++) a[i] = amp * (float)Math.Sin(2 * Math.PI * 440 * i / rate);
            return a;
        }

        [Test]
        public void EncodesAValidRiffHeaderAtTheGivenRate()
        {
            var wav = Wav.Encode(Tone(44100), 44100);
            Assert.That(System.Text.Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(System.Text.Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
            Assert.That(BitConverter.ToInt16(wav, 20), Is.EqualTo(1), "format must be PCM");
            Assert.That(BitConverter.ToInt16(wav, 22), Is.EqualTo(1), "must be mono");
            Assert.That(BitConverter.ToInt32(wav, 24), Is.EqualTo(44100), "sample rate must be what was asked for");
            Assert.That(BitConverter.ToInt16(wav, 34), Is.EqualTo(16), "must be 16-bit");
            Assert.That(BitConverter.ToInt32(wav, 40), Is.EqualTo(44100 * 2), "data chunk size");
            Assert.That(wav.Length, Is.EqualTo(Wav.HeaderBytes + 44100 * 2));
        }

        [Test]
        public void WritesTheRateItIsGivenRatherThanAConstant()
        {
            // Control against a hard-coded 44100: the mix rate is read from the engine at runtime and a
            // machine reporting 48000 must produce a 48000 file, or everything plays back at the wrong pitch.
            Assert.That(BitConverter.ToInt32(Wav.Encode(Tone(100), 48000), 24), Is.EqualTo(48000));
            Assert.That(BitConverter.ToInt32(Wav.Encode(Tone(100), 22050), 24), Is.EqualTo(22050));
        }

        [Test]
        public void RejectsAnImpossibleRateRatherThanEmittingAFileNothingCanPlay()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Wav.Encode(Tone(10), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Wav.Encode(Tone(10), 192000));
        }

        [Test]
        public void ClampsInsteadOfWrappingOnOverdrivenSamples()
        {
            // A float above 1.0 cast without clamping wraps to a large NEGATIVE short, turning a loud
            // passage into a burst of noise. Assert the SIGN, not just the magnitude.
            var wav = Wav.Encode(new[] { 2.0f, -2.0f }, 44100);
            Assert.That(BitConverter.ToInt16(wav, 44), Is.EqualTo(short.MaxValue));
            Assert.That(BitConverter.ToInt16(wav, 46), Is.EqualTo(-short.MaxValue));
        }

        [Test]
        public void DurationComesFromTheDataChunkNotTheHeaderClaim()
        {
            var wav = Wav.Encode(Tone(44100), 44100);
            Assert.That(Wav.DurationSeconds(wav), Is.EqualTo(1.0).Within(0.01));
            // Truncate the payload without touching the header: a header-trusting implementation would
            // still report 1.0 s.
            var truncated = new byte[Wav.HeaderBytes + 44100];
            Array.Copy(wav, truncated, truncated.Length);
            Assert.That(Wav.DurationSeconds(truncated), Is.EqualTo(0.5).Within(0.01));
        }

        [Test]
        public void EmptyCaptureStillProducesAWellFormedFile()
        {
            var wav = Wav.Encode(Array.Empty<float>(), 44100);
            Assert.That(wav.Length, Is.EqualTo(Wav.HeaderBytes));
            Assert.That(Wav.DurationSeconds(wav), Is.EqualTo(0));
        }

        [Test]
        public void EveryStatusHasAWireNameTheServerActuallyAccepts()
        {
            // The defect this pins SHIPPED: ToString().ToLowerInvariant() turned NoMic into "nomic" while
            // the server accepts {"ok","no_mic","disabled","error","typed"} and stored anything else as
            // "unknown" -- silently discarding the one verdict the feature exists to produce.
            var accepted = new[] { "ok", "no_mic", "disabled", "error", "typed" };
            foreach (AudioStatus st in Enum.GetValues(typeof(AudioStatus)))
                Assert.That(accepted, Does.Contain(AudioStatusWire.Of(st)),
                            $"{st} has no wire name the server accepts");
            // CONTROL: the naive spelling really is different, so this test is not asserting a tautology.
            Assert.That(AudioStatusWire.Of(AudioStatus.NoMic), Is.EqualTo("no_mic"));
            Assert.That(AudioStatus.NoMic.ToString().ToLowerInvariant(), Is.EqualTo("nomic"));
        }

        [Test]
        public void WritesTheFileTheEndToEndScriptUploads()
        {
            // Closes the loop between L0 and the live E2E. The service test previously uploaded a WAV made
            // by a python script that shares NO code with this encoder, so a broken encoder left the E2E
            // green while the field was broken. Now the E2E uploads THIS file.
            var path = Environment.GetEnvironmentVariable("UG_BUGREPORT_WAV_OUT");
            if (string.IsNullOrEmpty(path)) Assert.Pass("UG_BUGREPORT_WAV_OUT not set; nothing to hand over");
            File.WriteAllBytes(path, Wav.Encode(Tone(44100 * 2), 44100));
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(Wav.HeaderBytes));
        }
    }

    [TestFixture]
    public class AudioBufferTests
    {
        [Test]
        public void SilenceIsClassifiedNoMicEvenThoughFramesArrived()
        {
            // THE defect this whole type exists to prevent. Measured on a box with no audio hardware: the
            // capture still delivers tens of thousands of frames of exact silence, so a frame-count guard
            // reports a working microphone and ships a silent report tagged "ok".
            var b = new AudioBuffer(48000);
            b.AppendStereo(new float[16000], new float[16000], 16000);
            Assert.That(b.Count, Is.EqualTo(16000), "frames DID arrive");
            Assert.That(b.Peak, Is.EqualTo(0f));
            Assert.That(b.Classify(), Is.EqualTo(AudioStatus.NoMic));
        }

        [Test]
        public void RealSignalIsClassifiedOk()
        {
            // CONTROL for the test above. Without this leg, a Classify() that returned NoMic
            // unconditionally would pass -- the two must be distinguishable.
            var b = new AudioBuffer(48000);
            var l = new float[1000];
            for (int i = 0; i < l.Length; i++) l[i] = 0.3f * (float)Math.Sin(i * 0.1);
            b.AppendStereo(l, l, l.Length);
            Assert.That(b.Peak, Is.GreaterThan(AudioBuffer.SilenceFloor));
            Assert.That(b.Classify(), Is.EqualTo(AudioStatus.Ok));
        }

        [Test]
        public void NoFramesAtAllIsAlsoNoMic()
        {
            Assert.That(new AudioBuffer(100).Classify(), Is.EqualTo(AudioStatus.NoMic));
        }

        [Test]
        public void DownmixesStereoToTheMeanRatherThanDroppingAChannel()
        {
            var b = new AudioBuffer(10);
            b.AppendStereo(new[] { 1.0f }, new[] { 0.0f }, 1);
            Assert.That(b.ToArray()[0], Is.EqualTo(0.5f).Within(1e-6));
        }

        [Test]
        public void MonoInputIsAcceptedWithoutARightChannel()
        {
            var b = new AudioBuffer(10);
            b.AppendStereo(new[] { 0.8f }, null, 1);
            Assert.That(b.ToArray()[0], Is.EqualTo(0.8f).Within(1e-6));
        }

        [Test]
        public void TheSilenceFloorSitsBelowAnyRealSpeechAndAboveTrueSilence()
        {
            // The VALUE of SilenceFloor had no coverage: the silence test uses peak 0.0 and the signal test
            // uses 0.3, which is 3000x the floor, so SilenceFloor = 0.2f passed both. That mutation is not
            // theoretical -- it would tag every player speaking at a normal indoor level (peak ~0.05-0.15)
            // as "no mic", print "no mic -- screenshot only", and attach a perfectly good WAV anyway.
            Assert.That(AudioBuffer.SilenceFloor, Is.LessThan(0.01f),
                        "must sit below quiet-but-real speech");
            Assert.That(AudioBuffer.SilenceFloor, Is.GreaterThan(0f),
                        "must sit above exact digital silence, or dither from a dead mic reads as signal");

            // Drive it through Classify at both sides of the boundary rather than only asserting the number.
            var quiet = new AudioBuffer(64);
            quiet.AppendStereo(new[] { 0.02f, -0.02f }, null, 2);       // a softly-spoken player
            Assert.That(quiet.Classify(), Is.EqualTo(AudioStatus.Ok), "quiet speech is still speech");

            var dither = new AudioBuffer(64);
            dither.AppendStereo(new[] { 1e-6f, -1e-6f }, null, 2);      // a dead mic emitting noise
            Assert.That(dither.Classify(), Is.EqualTo(AudioStatus.NoMic));
        }

        [Test]
        public void StopsAtCapacityInsteadOfOverrunning()
        {
            var b = new AudioBuffer(4);
            b.AppendStereo(new float[10], new float[10], 10);
            Assert.That(b.Count, Is.EqualTo(4));
            Assert.That(b.Full, Is.True);
        }
    }

    [TestFixture]
    public class ReportSessionTests
    {
        [Test]
        public void PressStartsRecordingAndARepeatPressDoesNotRestartIt()
        {
            var s = new ReportSession();
            Assert.That(s.Press(), Is.True);
            s.Tick(1.0);
            Assert.That(s.Press(), Is.False, "key auto-repeat must not restart the recording");
            Assert.That(s.Elapsed, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void ShortHoldIsAScreenshotOnlyReportNotAFailure()
        {
            var s = new ReportSession();
            s.Press();
            s.Tick(0.1);
            Assert.That(s.Release(), Is.True);
            Assert.That(s.WasTap, Is.True);
        }

        [Test]
        public void LongHoldIsNotATap()
        {
            var s = new ReportSession();
            s.Press();
            s.Tick(3.0);
            s.Release();
            Assert.That(s.WasTap, Is.False);
        }

        [Test]
        public void TheCapForcesAFinalizeAndSaysSo()
        {
            var s = new ReportSession();
            s.Press();
            Assert.That(s.Tick(ReportSession.MaxSeconds - 0.01), Is.False, "must not fire early");
            Assert.That(s.Tick(0.02), Is.True);
            Assert.That(s.HitTimeout, Is.True);
            Assert.That(s.State, Is.EqualTo(ReportState.Finalizing));
        }

        [Test]
        public void ReleaseWhileIdleDoesNothing()
        {
            Assert.That(new ReportSession().Release(), Is.False);
        }

        [Test]
        public void TickWhileIdleDoesNotAccumulateTime()
        {
            var s = new ReportSession();
            s.Tick(5.0);
            Assert.That(s.Elapsed, Is.EqualTo(0));
        }
    }
}
