using System;
using NUnit.Framework;
using SDG.NetPak;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // DUPLICATE_AUDIT 1.11. The four Quantize* helpers each allocated a writer, an 8-byte buffer and a
    // reader to round-trip ONE float, on a path that runs per replicated field per entity per tick. They now
    // share a [ThreadStatic] scratch.
    //
    // That swap is only safe if it is BIT-IDENTICAL, and it is not obviously so: a reused writer's buffer
    // carries stale bytes from the previous call where a fresh `new byte[8]` is zeroed. Reset() rewinds the
    // cursor and the reader is bounded by writeByteIndex, so it should not matter -- but "should not matter"
    // is a belief, and this is the encoding every replicated float and every StateHash passes through. A
    // silent one-bit change here would show up as an unexplained desync, days later, somewhere else.
    //
    // So these compare the shared path against a fresh-allocation reference implementation, and they run the
    // shared one REPEATEDLY FIRST with different values, so the buffer is dirty with something else's bytes
    // before each comparison. A test that only ever quantizes one value through a warm scratch would pass
    // even if stale bytes leaked.
    [TestFixture]
    public class NetQuantizationScratchTests
    {
        // the pre-dedup implementations, verbatim, as the oracle
        static float FreshClamped(float v, int i, int f)
        {
            var w = new NetPakWriter { buffer = new byte[8] };
            w.Reset(); w.WriteClampedFloat(v, i, f); w.Flush();
            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadClampedFloat(i, f, out float result);
            return result;
        }
        static float FreshSigned(float v, int bits)
        {
            var w = new NetPakWriter { buffer = new byte[8] };
            w.Reset(); w.WriteSignedNormalizedFloat(v, bits); w.Flush();
            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadSignedNormalizedFloat(bits, out float result);
            return result;
        }
        static float FreshUnsigned(float v, int bits)
        {
            var w = new NetPakWriter { buffer = new byte[8] };
            w.Reset(); w.WriteUnsignedNormalizedFloat(v, bits); w.Flush();
            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadUnsignedNormalizedFloat(bits, out float result);
            return result;
        }
        static float FreshDegrees(float v, int bits)
        {
            var w = new NetPakWriter { buffer = new byte[4] };
            w.Reset(); w.WriteDegrees(v, bits); w.Flush();
            var r = new NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            r.ReadDegrees(out float result, bits);
            return result;
        }

        static void Same(float a, float b, string what)
            => Assert.That(BitConverter.SingleToInt32Bits(a), Is.EqualTo(BitConverter.SingleToInt32Bits(b)),
                           $"{what}: shared scratch {a:R} != fresh-allocation {b:R}");

        /// <summary>Dirty the scratch with unrelated encodings so a stale-byte leak has something to leak.</summary>
        static void DirtyTheScratch()
        {
            NetQuantization.QuantizeClampedFloat(-1234.5f, 12, 2);
            NetQuantization.QuantizeDegrees(359.75f, 11);
            NetQuantization.QuantizeSignedNormalizedFloat(-0.987f, 12);
            NetQuantization.QuantizeUnsignedNormalizedFloat(0.999f, 8);
        }

        [Test]
        public void clamped_float_matches_fresh_allocation()
        {
            // the real bit budgets in use: position XZ/Y, deployable scalars, velocity, damage
            var budgets = new[] { (11, 8), (9, 8), (12, 2), (6, 6), (9, 4), (3, 4) };
            foreach (var (i, f) in budgets)
                foreach (float v in new[] { 0f, -0f, 0.00005f, -0.00005f, 1f, -1f, 0.25f, -7.5f,
                                            123.456f, -123.456f, 2047.9f, -2048f, 4095.75f, 31.99f })
                {
                    DirtyTheScratch();
                    Same(NetQuantization.QuantizeClampedFloat(v, i, f), FreshClamped(v, i, f), $"clamped({v},{i},{f})");
                }
        }

        [Test]
        public void signed_and_unsigned_normalized_match_fresh_allocation()
        {
            foreach (int bits in new[] { 8, 12 })
                foreach (float v in new[] { 0f, -0f, 1f, -1f, 0.5f, -0.5f, 0.001f, -0.001f, 0.999f, -0.999f })
                {
                    DirtyTheScratch();
                    Same(NetQuantization.QuantizeSignedNormalizedFloat(v, bits), FreshSigned(v, bits), $"signed({v},{bits})");
                    if (v >= 0f)
                        Same(NetQuantization.QuantizeUnsignedNormalizedFloat(v, bits), FreshUnsigned(v, bits), $"unsigned({v},{bits})");
                }
        }

        [Test]
        public void degrees_match_fresh_allocation()
        {
            // degrees WRAP rather than clamp, so negatives and >360 are the interesting cases
            foreach (int bits in new[] { 9, 11 })
                foreach (float v in new[] { 0f, 0.1f, 90f, 179.9f, 180f, 270f, 359.9f, 360f, -90f, -0.1f, 720.5f })
                {
                    DirtyTheScratch();
                    Same(NetQuantization.QuantizeDegrees(v, bits), FreshDegrees(v, bits), $"degrees({v},{bits})");
                }
        }

        [Test]
        public void repeated_calls_are_stable()
        {
            // the failure a shared buffer would actually produce: the FIRST call right and later ones drifting
            // as residue accumulates. Same input, many times, interleaved with other encodings.
            float expect = FreshClamped(12.34f, 11, 8);
            for (int n = 0; n < 200; n++)
            {
                DirtyTheScratch();
                Same(NetQuantization.QuantizeClampedFloat(12.34f, 11, 8), expect, $"call #{n}");
            }
        }

        [Test]
        public void the_near_zero_special_case_survives()
        {
            // WriteClampedFloat has an explicit |value| < 0.0001f path that must decode to EXACTLY 0.0
            // (public issue #3686; guarded by NetPakClampedFloatTests). Pin it through the shared scratch too,
            // since that is the one branch a dirty buffer would be most likely to corrupt.
            foreach (var (i, f) in new[] { (11, 8), (12, 2), (6, 6) })
            {
                DirtyTheScratch();
                float q = NetQuantization.QuantizeClampedFloat(0.00001f, i, f);
                Assert.That(BitConverter.SingleToInt32Bits(q), Is.EqualTo(BitConverter.SingleToInt32Bits(0f)),
                            $"tiny value decodes to exactly 0.0 at ({i},{f}), got {q:R}");
            }
        }
    }
}
