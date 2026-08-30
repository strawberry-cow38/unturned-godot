using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Rain masking. bitvox approved the mechanic but asked for "pretty limited, not a free pass", so these
    // tests are mostly about the LIMIT rather than the effect -- the numbers are the feature, and this is
    // where they can be argued with.
    [TestFixture]
    public class NoiseMaskingTests
    {
        /// <summary>The cap at a given intensity, written out rather than called from the implementation:
        /// a test that asks the code what it does agrees with the code whatever it does.</summary>
        static float KeepAt(float rint) => 1f - (1f - NoiseMasking.MinKeep) * rint;

        // The real SoundBus radii, so the assertions below are about the actual game and not a toy.
        const float SneakWalk = 2f, CrouchWalk = 5f, Walk = 10f, Sprint = 18f, Gunshot = 48f, Explosion = 64f;

        [Test]
        public void DryWeatherChangesNothingAtAll()
        {
            // The load-bearing one. If this drifts, every player on a clear day is quieter than they were and
            // nobody asked for that.
            foreach (float l in new[] { SneakWalk, Walk, Gunshot, Explosion })
                Assert.That(NoiseMasking.Carry(l, 0f), Is.EqualTo(l), $"dry must not touch {l}");
        }

        [Test]
        public void RainHelpsYouMoveMuchMoreThanItHelpsYouShoot()
        {
            // THE POINT OF THE DESIGN. A flat multiplier would take the same fraction off both; subtracting a
            // distance takes most of a footstep and almost none of an explosion. If this inverts, the model
            // has been replaced by the free pass it exists to avoid.
            float moveLoss = 1f - NoiseMasking.Carry(Walk, 1f) / Walk;
            float shootLoss = 1f - NoiseMasking.Carry(Gunshot, 1f) / Gunshot;
            float boomLoss = 1f - NoiseMasking.Carry(Explosion, 1f) / Explosion;

            Assert.That(moveLoss, Is.GreaterThan(shootLoss * 1.5f),
                        $"moving ({moveLoss:P0}) must benefit far more than shooting ({shootLoss:P0})");
            Assert.That(shootLoss, Is.GreaterThan(boomLoss), "and a gunshot more than an explosion");
        }

        [Test]
        public void NoStormCanEverTakeMoreThanAQuarter()
        {
            // "not a free pass" in assertable form. Without the cap a sneaking player (2 m) would go to zero
            // and be literally unhearable.
            foreach (float l in new[] { SneakWalk, CrouchWalk, Walk, Sprint, Gunshot, Explosion })
            {
                float kept = NoiseMasking.Carry(l, 1f) / l;

                Assert.That(kept, Is.GreaterThanOrEqualTo(NoiseMasking.MinKeep - 1e-5f),
                            $"{l} m kept only {kept:P0}");
            }
            Assert.That(NoiseMasking.Carry(SneakWalk, 1f), Is.GreaterThan(0f),
                        "a sneaking player must still be audible at all");
        }

        [Test]
        public void TheQuietEndIsCapBoundAndTheLoudEndIsSubtractionBound()
        {
            // Which of the two terms wins, where. Documents the shape rather than just the bounds: if someone
            // retunes MaskMetres or MinKeep, this says which sounds they just changed.
            Assert.That(NoiseMasking.Carry(Walk, 1f), Is.EqualTo(Walk * NoiseMasking.MinKeep).Within(1e-4f),
                        "10 m walk is held up by the cap, not the subtraction");
            Assert.That(NoiseMasking.Carry(Gunshot, 1f),
                        Is.EqualTo(Gunshot - NoiseMasking.MaskMetres).Within(1e-4f),
                        "48 m gunshot is limited by the subtraction, well above the cap");
        }

        [Test]
        public void ItRampsWithTheWeatherRatherThanSwitchingOn()
        {
            // rint is the same blended scalar the visuals use, so masking fades in with the storm. A step
            // change would be audible as zombies suddenly losing you.
            float dry = NoiseMasking.Carry(Sprint, 0f);
            float half = NoiseMasking.Carry(Sprint, 0.5f);
            float full = NoiseMasking.Carry(Sprint, 1f);
            Assert.That(half, Is.LessThan(dry));
            Assert.That(full, Is.LessThan(half), "monotonic in intensity");

            // The CAP scales with intensity too, so half a storm caps at 12.5% rather than 25%. An earlier
            // version of this test asserted the pure subtraction here (18 - 3 = 15) and was right about the
            // old fixed-cap model; the model changed because that flat cap made Default Rain and Heavy Rain
            // mask footsteps identically, which killed the tier distinction.
            float keepHalf = 1f - (1f - NoiseMasking.MinKeep) * 0.5f;
            Assert.That(half, Is.EqualTo(Sprint * keepHalf).Within(1e-4f),
                        "at half intensity a sprint is cap-bound, not subtraction-bound");
        }

        [Test]
        public void DefaultRainMasksLessThanHeavyRainForEverySound()
        {
            // THE TIER TEST. Default Rain tops out at rint 0.7, Heavy at 1.0. Under a FIXED cap this was
            // false for every quiet sound -- a 10 m footstep hit the cap at rint 0.42, so both tiers masked
            // identically and the two weather types were indistinguishable where it mattered most. That is
            // why the cap eases in with intensity.
            foreach (float l in new[] { SneakWalk, CrouchWalk, Walk, Sprint, Gunshot, Explosion })
            {
                float light = NoiseMasking.Carry(l, 0.7f);
                float heavy = NoiseMasking.Carry(l, 1.0f);
                Assert.That(light, Is.GreaterThan(heavy), $"{l} m must carry further in light rain");
                Assert.That(light, Is.LessThan(l), $"{l} m must still be masked somewhat in light rain");
            }
        }

        [Test]
        public void SilenceStaysSilentAndIntensityIsClamped()
        {
            // A suppressed shot or a standing player emits 0 and must not become audible; and a caller
            // handing in a rint above 1 must not push past the cap.
            Assert.That(NoiseMasking.Carry(0f, 1f), Is.EqualTo(0f));
            Assert.That(NoiseMasking.Carry(-3f, 1f), Is.EqualTo(0f));
            Assert.That(NoiseMasking.Carry(Gunshot, 5f), Is.EqualTo(NoiseMasking.Carry(Gunshot, 1f)).Within(1e-4f));
        }
    }
}
