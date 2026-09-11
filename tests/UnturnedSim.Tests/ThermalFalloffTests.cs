using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Layer 3 of the temperature work, minus the raycast: what a heat or cooling source is worth at range.
    [TestFixture]
    public class ThermalFalloffTests
    {
        [Test]
        public void FullStrengthAtTheCentre_ZeroAtTheRim_AndZeroBeyond()
        {
            Assert.That(ThermalFalloff.Strength(0f, 5f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(ThermalFalloff.Strength(5f, 5f), Is.EqualTo(0f));
            Assert.That(ThermalFalloff.Strength(50f, 5f), Is.EqualTo(0f), "out of range is OFF, not faint");
        }

        [Test]
        public void Falloff_IsMonotonic()
        {
            float prev = float.MaxValue;
            for (float d = 0f; d <= 5f; d += 0.25f)
            {
                float s = ThermalFalloff.Strength(d, 5f);
                Assert.That(s, Is.LessThanOrEqualTo(prev), $"strength rose at d={d}");
                prev = s;
            }
        }

        [Test]
        public void EasesAtBothEnds_RatherThanKinkingAtTheRim()
        {
            // The reason it is smoothstep and not linear. Near the rim the curve must be FLATTER than linear
            // (so the last step in is worth little) and near the centre flatter too -- a linear falloff would
            // sit exactly on the diagonal at both samples.
            Assert.That(ThermalFalloff.Strength(4.5f, 5f), Is.LessThan(0.10f), "linear would be 0.10 here");
            Assert.That(ThermalFalloff.Strength(0.5f, 5f), Is.GreaterThan(0.90f), "linear would be 0.90 here");
        }

        [Test]
        public void ZeroOrNegativeRadius_ContributesNothing_RatherThanDividingByZero()
        {
            Assert.That(ThermalFalloff.Strength(1f, 0f), Is.EqualTo(0f));
            Assert.That(ThermalFalloff.Strength(1f, -4f), Is.EqualTo(0f));
        }

        [Test]
        public void ACoolerIsAFireWithTheOppositeSign_SameCurve()
        {
            // One curve for both, so a cooler cannot drift into behaving differently at the same range.
            float warm = ThermalFalloff.Contribution(20f, 2f, 5f);
            float cool = ThermalFalloff.Contribution(-20f, 2f, 5f);
            Assert.That(cool, Is.EqualTo(-warm).Within(1e-5f));
        }

        [Test]
        public void StackedSources_AddUp_ButAreClamped()
        {
            float ten = 0f;
            for (int i = 0; i < 10; i++) ten += ThermalFalloff.Contribution(20f, 0f, 5f);
            Assert.That(ten, Is.EqualTo(200f).Within(1e-3f), "the raw sum is unclamped");
            Assert.That(ThermalFalloff.Clamp(ten), Is.EqualTo(ThermalFalloff.MaxCombinedC));
            Assert.That(ThermalFalloff.Clamp(-ten), Is.EqualTo(-ThermalFalloff.MaxCombinedC));
        }

        [Test]
        public void TwoFiresAreWarmerThanOne_TheClampIsNotAFlatCeiling()
        {
            // Control on the clamp: it must only bite at the extreme, or "build a second fire" stops meaning
            // anything the moment you have one.
            float one = ThermalFalloff.Clamp(ThermalFalloff.Contribution(12f, 1f, 5f));
            float two = ThermalFalloff.Clamp(2f * ThermalFalloff.Contribution(12f, 1f, 5f));
            Assert.That(two, Is.GreaterThan(one));
        }
    }
}
