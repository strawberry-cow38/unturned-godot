using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // strawberry 2026-09-10: "wire loot to roll on a deterministic seed thats chosen/rolled before pressing
    // play on the map".
    [TestFixture]
    public class LootSeedTests
    {
        [SetUp]
        public void Reset() => LootSeed.Clear();

        [Test]
        public void SameWorldSeed_SamePlace_SameStream()
        {
            LootSeed.Set(12345);
            Assert.That(LootSeed.For(10f, 2f, -30f), Is.EqualTo(LootSeed.For(10f, 2f, -30f)));
        }

        [Test]
        public void DifferentWorldSeed_SamePlace_DifferentStream()
        {
            LootSeed.Set(1);
            uint a = LootSeed.For(10f, 2f, -30f);
            LootSeed.Set(2);
            Assert.That(LootSeed.For(10f, 2f, -30f), Is.Not.EqualTo(a));
        }

        [Test]
        public void OrderIndependent_ByConstruction()
        {
            // THE WHOLE POINT. A shared sequential RNG is deterministic and still gives a crate different
            // contents depending on when it was built -- which differs between a fresh load and a save
            // restore. Asking for one crate's seed must never depend on having asked for another's.
            LootSeed.Set(99);
            uint lone = LootSeed.For(4f, 0f, 4f);
            for (int i = 0; i < 50; i++) LootSeed.For(i, 0f, i);   // "build" 50 other crates first
            Assert.That(LootSeed.For(4f, 0f, 4f), Is.EqualTo(lone));
        }

        [Test]
        public void NeighbouringCrates_DoNotGetNeighbouringSeeds()
        {
            // Crates a metre apart differ by 10 in one quantised field. Without avalanche their seeds would
            // be adjacent, and adjacent seeds in most generators give correlated first draws -- which reads
            // in game as "every crate in this room rolled the same thing".
            LootSeed.Set(7);
            uint a = LootSeed.For(0f, 0f, 0f);
            uint b = LootSeed.For(1f, 0f, 0f);
            uint diff = a ^ b;
            int bits = 0;
            for (int i = 0; i < 32; i++) if ((diff & (1u << i)) != 0) bits++;
            Assert.That(bits, Is.GreaterThan(8), $"only {bits} bits differ between neighbours ({a} vs {b})");
        }

        [Test]
        public void SpreadIsWide_AcrossAMapFullOfCrates()
        {
            // A cheap collision check: 400 positions on a grid must produce ~400 distinct seeds. A hash that
            // folded an axis away would still pass every test above and quietly give whole rows one stream.
            LootSeed.Set(2026);
            var seen = new HashSet<uint>();
            for (int x = 0; x < 20; x++)
                for (int z = 0; z < 20; z++)
                    seen.Add(LootSeed.For(x * 3f, 0f, z * 3f));
            Assert.That(seen.Count, Is.GreaterThan(395), $"only {seen.Count}/400 distinct");
        }

        [Test]
        public void AllThreeAxesMatter()
        {
            // Guards the fold-an-axis-away bug directly rather than only statistically.
            LootSeed.Set(5);
            uint o = LootSeed.For(0f, 0f, 0f);
            Assert.That(LootSeed.For(5f, 0f, 0f), Is.Not.EqualTo(o));
            Assert.That(LootSeed.For(0f, 5f, 0f), Is.Not.EqualTo(o));
            Assert.That(LootSeed.For(0f, 0f, 5f), Is.Not.EqualTo(o));
        }

        [Test]
        public void TinyFloatDrift_LandsInTheSameBucket()
        {
            // The same crate's position can arrive a hair different between a map build and a save restore.
            // Quantisation is what stops that re-rolling its contents.
            LootSeed.Set(3);
            Assert.That(LootSeed.For(10.0001f, 2f, -30f), Is.EqualTo(LootSeed.For(10f, 2f, -30f)));
        }

        [Test]
        public void SeedZero_IsAWorld_NotAnUnseededState()
        {
            // 0 must behave like any other seed. If it were treated as "not set" a legitimately-seeded world
            // would silently fall back to random loot, which is the bug this feature exists to remove.
            LootSeed.Set(0);
            uint a = LootSeed.For(1f, 1f, 1f);
            LootSeed.Set(1);
            Assert.That(LootSeed.For(1f, 1f, 1f), Is.Not.EqualTo(a));
            LootSeed.Set(0);
            Assert.That(LootSeed.For(1f, 1f, 1f), Is.EqualTo(a));
        }
    
        // ---- strawberry 2026-09-11: "unset seed should be a random seed each time. set once at the first
        // save creation" ----

        [Test]
        public void AFreshWorld_HasNoSeedYet_AndRollsOneOnDemand()
        {
            Assert.That(LootSeed.Established, Is.False, "a fresh process has a field default, not a seed");
            LootSeed.EnsureEstablished(() => 4242);
            Assert.That(LootSeed.Established, Is.True);
            Assert.That(LootSeed.World, Is.EqualTo(4242ul));
        }

        [Test]
        public void EstablishingIsIdempotent_SoALaterSaveCannotMoveTheLoot()
        {
            // Called on EVERY save, not just the first. If it re-rolled, a world's entire loot table would
            // change every time it was written -- which is the opposite of the feature.
            LootSeed.EnsureEstablished(() => 111);
            LootSeed.EnsureEstablished(() => 222);
            Assert.That(LootSeed.World, Is.EqualTo(111ul));
        }

        [Test]
        public void AWorldWhoseSeedIsZero_IsNotReRolled()
        {
            // The exact reason Established is a separate flag and not `World != 0`. One world in 2^64 would
            // otherwise re-roll its loot on every single save, and nobody would ever reproduce it.
            LootSeed.EnsureEstablished(() => 0);
            LootSeed.EnsureEstablished(() => 999);
            Assert.That(LootSeed.World, Is.EqualTo(0ul));
            Assert.That(LootSeed.Established, Is.True);
        }

        [Test]
        public void LoadingASave_AdoptsItsSeed_EvenWhenThatSeedIsZero()
        {
            LootSeed.Set(0);
            LootSeed.EnsureEstablished(() => 777);
            Assert.That(LootSeed.World, Is.EqualTo(0ul), "a loaded world must never be re-rolled");
        }
}
}
