using System;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    [TestFixture]
    public class ShotCadenceTests
    {
        [Test]
        public void Default_IsReady_QueriesAndLegacyDoNotActivate()
        {
            var cadence = new ShotCadence();
            Assert.That(cadence.Active, Is.False);
            Assert.That(cadence.IsCoolingDown(0), Is.False);
            Assert.That(cadence.CanFire(0, 950), Is.True);
            cadence.AcceptLegacyShot(0, 100);
            Assert.That(cadence.Active, Is.False);
            Assert.That(cadence.CanFire(0, 950), Is.True);
        }

        [Test]
        public void Rpm950_NineteenIntervalsAreSixtyTicks_WithThreeAndFourTickGaps()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 950);
            long previous = 0;
            int threes = 0;
            int fours = 0;
            for (int i = 1; i <= 19; ++i)
            {
                long tick = (i * 3000L + 949) / 950;
                Assert.That(cadence.CanFire(tick - 1, 950), Is.False);
                Assert.That(cadence.CanFire(tick, 950), Is.True);
                long gap = tick - previous;
                Assert.That(gap, Is.EqualTo(3).Or.EqualTo(4));
                if (gap == 3) ++threes; else ++fours;
                cadence.AcceptShot(tick, 950);
                previous = tick;
            }
            Assert.That(previous, Is.EqualTo(60));
            Assert.That(threes, Is.EqualTo(16));
            Assert.That(fours, Is.EqualTo(3));
        }

        [Test]
        public void Rpm950_RepeatedCyclesHaveExactDeadlinesAndLessThanOneTickError()
        {
            var cadence = new ShotCadence();
            const long origin = 10000000000000000L;
            cadence.AcceptShot(origin, 950);
            for (int i = 1; i <= 19000; ++i)
            {
                long elapsed = (i * 3000L + 949) / 950;
                long errorNumerator = elapsed * 950 - i * 3000L;
                Assert.That(errorNumerator, Is.InRange(0L, 949L));
                Assert.That(cadence.CanFire(origin + elapsed - 1, 950), Is.False);
                Assert.That(cadence.CanFire(origin + elapsed, 950), Is.True);
                cadence.AcceptShot(origin + elapsed, 950);
                if (i % 19 == 0)
                    Assert.That(elapsed, Is.EqualTo(i / 19 * 60L));
            }
        }

        [Test]
        public void Rpm1200_TwentyIntervalsAreFiftyTicks()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 1200);
            long previous = 0;
            for (int i = 1; i <= 20; ++i)
            {
                long tick = (i * 3000L + 1199) / 1200;
                Assert.That(tick - previous, Is.EqualTo(i % 2 == 1 ? 3 : 2));
                Assert.That(cadence.CanFire(tick - 1, 1200), Is.False);
                cadence.AcceptShot(tick, 1200);
                previous = tick;
            }
            Assert.That(previous, Is.EqualTo(50));
        }

        [Test]
        public void EqualityBoundary_PrematureAcceptanceThrowsWithoutChangingPhase()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(10, 950);
            Assert.That(cadence.IsCoolingDown(13), Is.True);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptShot(13, 950));
            Assert.That(cadence.IsCoolingDown(14), Is.False);
            cadence.AcceptShot(14, 950);
            Assert.That(cadence.CanFire(16, 950), Is.False);
            Assert.That(cadence.CanFire(17, 950), Is.True);
        }

        [Test]
        public void SameTickAndBackwardsTicksAreRejected_EvenAtMaximumRpm()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(7, 3000);
            Assert.That(cadence.CanFire(7, 3000), Is.False);
            Assert.That(cadence.CanFire(6, 3000), Is.False);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptShot(7, 3000));
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptLegacyShot(7, 0));
            cadence.AcceptShot(8, 3000);
        }

        [TestCase(5L)] // just missed the first integer due tick (4)
        [TestCase(1000L)] // idle, reload or blocked pause
        public void LateAcceptance_ReanchorsWithoutBankingCatchUp(long lateTick)
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 950);
            cadence.AcceptShot(lateTick, 950);
            Assert.That(cadence.CanFire(lateTick, 950), Is.False);
            Assert.That(cadence.CanFire(lateTick + 3, 950), Is.False);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptShot(lateTick + 1, 950));
            cadence.AcceptShot(lateTick + 4, 950);
            Assert.That(cadence.CanFire(lateTick + 7, 950), Is.True);
        }

        [Test]
        public void RateChanges_RespectBothOldDeadlineAndNewPeriod_QueriesDoNotMutate()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 600); // old deadline 5
            Assert.That(cadence.CanFire(1, 3000), Is.False);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptShot(1, 3000));
            Assert.That(cadence.CanFire(5, 300), Is.False); // new period 10
            Assert.That(cadence.CanFire(5, 600), Is.True);
            cadence.AcceptShot(5, 950); // faster, but still waited until old deadline
            Assert.That(cadence.CanFire(8, 950), Is.False);
            Assert.That(cadence.CanFire(9, 950), Is.True); // new phase anchored at 5
            Assert.That(cadence.CanFire(14, 300), Is.False);
            cadence.AcceptShot(15, 300);
            Assert.That(cadence.CanFire(24, 300), Is.False);
            Assert.That(cadence.CanFire(25, 300), Is.True);
        }

        [Test]
        public void LegacyBridge_CannotEraseCooldownInEitherDirection()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 950);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptLegacyShot(3, 1));
            cadence.AcceptLegacyShot(4, 10);
            Assert.That(cadence.Active, Is.True);
            Assert.That(cadence.CanFire(13, 3000), Is.False);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptShot(13, 3000));
            Assert.That(cadence.CanFire(14, 100), Is.False); // slower RPM also waits its own period
            cadence.AcceptShot(14, 950);
            Assert.That(cadence.CanFire(17, 950), Is.False);
            Assert.That(cadence.CanFire(18, 950), Is.True);
        }

        [Test]
        public void ZeroLegacyInterval_StillAllowsOnlyOneShotPerTick()
        {
            var cadence = new ShotCadence();
            cadence.AcceptShot(0, 3000);
            cadence.AcceptLegacyShot(1, 0);
            Assert.That(cadence.IsCoolingDown(1), Is.True);
            Assert.Throws<InvalidOperationException>(() => cadence.AcceptLegacyShot(1, 0));
            Assert.That(cadence.CanFire(2, 3000), Is.True);
        }

        [Test]
        public void StructInstancesAndCopies_HaveIndependentPhase()
        {
            var first = new ShotCadence();
            var second = new ShotCadence();
            first.AcceptShot(0, 950);
            Assert.That(second.CanFire(0, 950), Is.True);
            second.AcceptShot(1, 950);
            var copy = first;
            first.AcceptShot(4, 950);
            Assert.That(copy.CanFire(4, 950), Is.True);
            Assert.That(first.CanFire(4, 950), Is.False);
            Assert.That(second.CanFire(4, 950), Is.False);
            Assert.That(second.CanFire(5, 950), Is.True);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(3001)]
        [TestCase(int.MaxValue)]
        public void InvalidRpm_ThrowsEvenWhenInactive_WithoutActivating(int rpm)
        {
            var cadence = new ShotCadence();
            Assert.Throws<ArgumentOutOfRangeException>(() => cadence.CanFire(0, rpm));
            Assert.Throws<ArgumentOutOfRangeException>(() => cadence.AcceptShot(0, rpm));
            Assert.That(cadence.Active, Is.False);
        }

        [Test]
        public void InvalidIntervalAndOverflow_ThrowWithoutMutation()
        {
            var cadence = new ShotCadence();
            Assert.Throws<ArgumentOutOfRangeException>(() => cadence.AcceptLegacyShot(0, -1));
            Assert.Throws<OverflowException>(() => cadence.AcceptShot(long.MaxValue, 950));
            Assert.That(cadence.Active, Is.False);
            cadence.AcceptShot(0, 1);
            Assert.That(cadence.CanFire(2999, 1), Is.False);
            Assert.That(cadence.CanFire(3000, 1), Is.True);
            Assert.Throws<OverflowException>(() => cadence.AcceptLegacyShot(long.MaxValue, 1));
            Assert.That(cadence.CanFire(3000, 1), Is.True);
        }
    }
}
