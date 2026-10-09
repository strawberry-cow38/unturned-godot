using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The wire's position RANGE, measured through the writer rather than restated (2026-10-09): NetQuantization said
    // 11 int bits was +-2048 m, WriteClampedFloat spends one on the sign, and the real +-1024 pinned every player past
    // it. These read the range off a round trip, so a comment can never be the only thing that knows it.
    [TestFixture]
    public class PositionRangeTests
    {
        static float RoundTripXZ(float v) =>
            NetQuantization.QuantizeClampedFloat(v, NetQuantization.PositionXZIntBits, NetQuantization.PositionXZFracBits);

        [Test]
        public void TheWholeOfPeiRoundTrips()
        {
            // PEI's terrain is 4 x 1024 m tiles centred on the origin: +-2048, and the bridge reaches x -1360
            foreach (float v in new[] { -2047.5f, -1360f, -1200f, -1000f, 0.5f, 1500f, 2047.5f })
                Assert.That(RoundTripXZ(v), Is.EqualTo(v).Within(1f / 256f), $"{v} m must survive the wire");
        }

        [Test]
        public void TheStatedRangeIsTheWritersRange()
        {
            float r = NetQuantization.PositionXZRange;
            Assert.That(r, Is.EqualTo(4096f));
            Assert.That(RoundTripXZ(r - 1f), Is.EqualTo(r - 1f).Within(1f / 256f), "just inside the range survives");
            // past it the writer CLAMPS: the top of the range, and the bottom
            Assert.That(RoundTripXZ(r + 500f), Is.LessThan(r).And.GreaterThan(r - 0.01f), "past +range pins at the top");
            Assert.That(RoundTripXZ(-r - 500f), Is.EqualTo(-r), "past -range pins at -range");
        }
    }
}
