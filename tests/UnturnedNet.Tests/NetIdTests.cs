using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §2.6 / §5 item 2: NetId is a session-scoped uint32, server-minted, monotonic, 0 = invalid.
    [TestFixture]
    public class NetIdTests
    {
        [Test]
        public void Invalid_IsZero_AndIsValidIsFalse()
        {
            Assert.That(NetId.Invalid.Value, Is.Zero);
            Assert.That(NetId.Invalid.IsValid, Is.False);
            Assert.That(new NetId(1).IsValid, Is.True);
        }

        [Test]
        public void Equality_IsByValue()
        {
            Assert.That(new NetId(7), Is.EqualTo(new NetId(7)));
            Assert.That(new NetId(7) == new NetId(7), Is.True);
            Assert.That(new NetId(7) != new NetId(8), Is.True);
        }

        [Test]
        public void Minter_StartsAtOne_MonotonicIncreasing_NeverMintsZero()
        {
            var minter = new NetIdMinter();
            var a = minter.Mint();
            var b = minter.Mint();
            var c = minter.Mint();

            Assert.That(a.Value, Is.EqualTo(1));
            Assert.That(b.Value, Is.EqualTo(2));
            Assert.That(c.Value, Is.EqualTo(3));
            Assert.That(a.IsValid, Is.True);
            Assert.That(minter.MintedCount, Is.EqualTo(3));

            for (int i = 0; i < 100; i++) Assert.That(minter.Mint().Value, Is.Not.Zero);
        }

        [Test]
        public void Registry_AddTryGetRemove_RoundTrips()
        {
            var registry = new NetEntityRegistry<string>();
            var id = new NetId(42);

            Assert.That(registry.TryGet(id, out _), Is.False, "nothing registered yet");
            registry.Add(id, "hello");
            Assert.That(registry.TryGet(id, out string value), Is.True);
            Assert.That(value, Is.EqualTo("hello"));
            Assert.That(registry.Contains(id), Is.True);
            Assert.That(registry.Count, Is.EqualTo(1));

            Assert.That(registry.Remove(id), Is.True);
            Assert.That(registry.Remove(id), Is.False, "already removed");
            Assert.That(registry.TryGet(id, out _), Is.False);
            Assert.That(registry.Count, Is.Zero);
        }

        [Test]
        public void Registry_DoesNotMint_OnlyTracks()
        {
            // NetEntityRegistry deliberately has no Mint() of its own -- ids come from a shared NetIdMinter
            // (MP_PLAN §2.6: "one flat space") so multiple systems never hand out colliding ids.
            var minter = new NetIdMinter();
            var registry = new NetEntityRegistry<int>();
            var id = minter.Mint();
            registry.Add(id, 99);
            Assert.That(registry.TryGet(id, out int v) && v == 99, Is.True);
        }
    }
}
