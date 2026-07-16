using NUnit.Framework;
using SDG.NetPak;

namespace SDG.NetPak.Tests
{
    // Regression guard for the WriteClampedFloat +1.0 encode bug (MP Phase 4): the int field was biased on
    // the RAW FLOAT ((uint)(value + absMinValue)) instead of the floored int part, so any value within
    // float-epsilon below an integer (2.9999976f + 1024f rounds to 1027.0f exactly) encoded its int field
    // one too high while the fraction still encoded ~0.996 against floor(value) -- the decode came back
    // +1.0 off (2.9999976 -> 3.996). Both halves must agree on floor(value).
    [TestFixture]
    public class NetPakClampedFloatTests
    {
        static float RoundTrip(float value, int intBits, int fracBits)
        {
            var w = new NetPakWriter { buffer = new byte[16] };
            w.Reset();
            Assert.That(w.WriteClampedFloat(value, intBits, fracBits), Is.True);
            w.Flush();
            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(r.ReadClampedFloat(intBits, fracBits, out float result), Is.True);
            return result;
        }

        [Test]
        public void ValueJustBelowAnInteger_DoesNotDecodeOneWholeUnitHigh()
        {
            // 0.03f accumulated 100x -- the exact float that exposed the bug (encoded as 3.996 before)
            const float hazard = 2.9999976f;
            float decoded = RoundTrip(hazard, 11, 8);
            Assert.That(decoded, Is.EqualTo(hazard).Within(1f / 256f + 1e-4f), "decode stays within one fraction step");
        }

        [Test]
        public void HazardValues_AcrossTheRange_StayWithinOneFractionStep()
        {
            // every value where (value + 2^(intBits-1)) rounds UP to the next float integer used to mis-encode
            float[] hazards = { 2.9999976f, 0.99999994f, 511.99997f, -0.00000012f + 1f, 99.99999f, -100.00001f + 200f - 200f };
            foreach (float v in hazards)
            {
                float decoded = RoundTrip(v, 11, 8);
                Assert.That(decoded, Is.EqualTo(v).Within(1f / 256f + 1e-3f), $"value {v:R} must not jump a whole unit");
            }
        }

        [Test]
        public void NegativeAndPlainValues_RoundTrip_WithinOneFractionStep()
        {
            foreach (float v in new[] { -3.0000024f, -2.5f, -0.125f, 0.5f, 3.25f, 1023.5f, -1023.5f })
            {
                float decoded = RoundTrip(v, 11, 8);
                Assert.That(decoded, Is.EqualTo(v).Within(1f / 256f + 1e-3f), $"value {v:R}");
            }
        }
    }
}
