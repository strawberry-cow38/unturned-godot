using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Felling a tree. Engine-free because the drops are real items and the XP is real progression, so
    // the server owns it -- a client cannot be allowed to announce that a tree fell.
    [TestFixture]
    public class ResourceHarvestSimTests
    {
        // The real birch numbers, straight off the extracted table: 800 HP, 7-10 rewards, and a WEIGHTED
        // drop table (Birch Log 60 / Birch Stick 40) resolved from spawn table 515 -- not item 515, which
        // is Cooked Venison.
        static ResourceHarvestDef Birch() => new ResourceHarvestDef
        {
            AssetId = 3, Health = 800, RewardXp = 4, ResetSeconds = 450f,
            RewardMin = 7, RewardMax = 10, HasDebris = true,
            Drops = new (ushort, int)[] { (37, 60), (38, 40) },
        };

        static ResourceHarvestSim NewSim(out ResourceHarvestDef birch)
        {
            var s = new ResourceHarvestSim();
            birch = Birch();
            s.RegisterDef(birch);
            s.RegisterInstance(0, 3);
            return s;
        }

        [Test]
        public void ATreeTakesItsFullHealthBeforeItFalls()
        {
            var s = NewSim(out _);
            Assert.That(s.HealthOf(0), Is.EqualTo(800), "a standing tree is at full health without a stored row");

            Assert.That(s.Damage(0, 300, out _), Is.False);
            Assert.That(s.HealthOf(0), Is.EqualTo(500));
            Assert.That(s.Damage(0, 499, out _), Is.False, "one short is still standing");
            Assert.That(s.Damage(0, 1, out var def), Is.True, "the hit that crosses zero fells it");
            Assert.That(def.AssetId, Is.EqualTo(3), "and hands back the type, so the caller knows what dropped");
            Assert.That(s.IsFelled(0), Is.True);
        }

        [Test]
        public void AFelledTreeCannotBeFelledTwice()
        {
            // Two players swinging at the same trunk on the same tick is normal, not an error. If the
            // second swing also returned true the drops would be claimed twice.
            var s = NewSim(out _);
            Assert.That(s.Damage(0, 800, out _), Is.True);
            Assert.That(s.Damage(0, 800, out _), Is.False, "the second axe gets nothing");
        }

        [Test]
        public void ItGrowsBackAfterItsResetTime()
        {
            var s = NewSim(out _);
            s.Damage(0, 800, out _);

            var due = new List<int>();
            for (float t = 0; t < 449f; t += 1f) due.AddRange(s.Step(1f));
            Assert.That(due, Is.Empty, "still a stump at 449 s of a 450 s reset");
            Assert.That(s.IsFelled(0), Is.True);

            for (int i = 0; i < 3; i++) due.AddRange(s.Step(1f));
            Assert.That(due, Does.Contain(0), "the index comes back so the caller can flip the alive bit");
            Assert.That(s.IsFelled(0), Is.False);
            Assert.That(s.HealthOf(0), Is.EqualTo(800), "and it stands at full health again");
        }

        [Test]
        public void RewardCountSpansTheAssetsRangeInclusive()
        {
            var d = Birch();
            Assert.That(ResourceHarvestSim.RewardCount(d, 0.0), Is.EqualTo(7), "the bottom of 7-10");
            Assert.That(ResourceHarvestSim.RewardCount(d, 0.999), Is.EqualTo(10), "the top");
            // The boundary that bites: Random.Range(min, max+1) INCLUDES max, so a roll of exactly 1.0
            // must not produce 11.
            Assert.That(ResourceHarvestSim.RewardCount(d, 1.0), Is.EqualTo(10), "a roll of 1.0 must not overshoot");
            for (int i = 0; i <= 100; i++)
            {
                int n = ResourceHarvestSim.RewardCount(d, i / 100.0);
                Assert.That(n, Is.InRange(7, 10), $"roll {i / 100.0} gave {n}");
            }
        }

        [Test]
        public void RewardCountIsClampedSoAMultiplierCannotCrashAnyone()
        {
            var d = Birch();
            Assert.That(ResourceHarvestSim.RewardCount(d, 0.5, 1000f), Is.EqualTo(100), "retail clamps at 100");
            Assert.That(ResourceHarvestSim.RewardCount(d, 0.5, 0f), Is.Zero);
        }

        [Test]
        public void DropsRollTheWeightedTablePerItem()
        {
            var d = Birch();
            Assert.That(ResourceHarvestSim.RollDrop(d, 0.0), Is.EqualTo(37), "the low end of the table is the log");
            Assert.That(ResourceHarvestSim.RollDrop(d, 0.99), Is.EqualTo(38), "the high end is the stick");
            Assert.That(ResourceHarvestSim.RollDrop(d, 1.0), Is.EqualTo(38), "a roll of 1.0 lands on the last entry, not nothing");

            // 60/40 by weight, not 50/50 by count -- the thing a naive "pick a random entry" gets wrong.
            int logs = 0, n = 1000;
            for (int i = 0; i < n; i++) if (ResourceHarvestSim.RollDrop(d, i / (double)n) == 37) logs++;
            Assert.That(logs, Is.InRange(580, 620), $"~60% logs, got {logs}/{n}");
        }

        [Test]
        public void ABushWithNoTableDropsNothingRatherThanItemZero()
        {
            // Bush_0..11 are real: 1000 HP, choppable, no Reward_ID and no log/stick. They must fell
            // cleanly and yield nothing, not spawn a phantom item 0.
            var s = new ResourceHarvestSim();
            var bush = new ResourceHarvestDef { AssetId = 7, Health = 1000, ResetSeconds = 300f };
            s.RegisterDef(bush);
            s.RegisterInstance(5, 7);
            Assert.That(s.Damage(5, 1000, out var def), Is.True);
            Assert.That(ResourceHarvestSim.RollDrop(def, 0.5), Is.Zero, "no table -> no item");
            Assert.That(def.TotalWeight, Is.Zero);
        }

        [Test]
        public void OnlyAWeaponCarryingTheRightBladeCanChop()
        {
            // The gate that is easy to skip entirely, because skipping it looks fine in play: every melee
            // weapon fells everything. Trees declare no BladeID at all, so they default to 0, and a weapon
            // has to list 0 to cut one.
            var birch = Birch();   // BladeId 0, not vulnerable to all melee
            System.Func<int, bool> axe = id => id == 0 || id == 1;   // an axe carries blade 0
            System.Func<int, bool> knife = id => id == 3;            // a knife does not

            Assert.That(ResourceHarvestSim.CanChop(birch, axe), Is.True, "the axe lists blade 0");
            Assert.That(ResourceHarvestSim.CanChop(birch, knife), Is.False, "the knife does not");
            Assert.That(ResourceHarvestSim.CanChop(birch, null), Is.False, "no blades at all chops nothing");
        }

        [Test]
        public void VulnerableToAllMeleeSkipsTheBladeCheckEntirely()
        {
            var soft = Birch();
            soft.VulnerableToAllMelee = true;
            System.Func<int, bool> knife = id => id == 3;
            Assert.That(ResourceHarvestSim.CanChop(soft, knife), Is.True);
        }

        [Test]
        public void FistsTakeTheirOwnRouteNotTheBladeList()
        {
            // Retail gates bare hands on Vulnerable_To_Fists, not on a blade list -- a fist has no blades,
            // so running it through hasBladeID would make every resource punchable-proof by accident.
            var birch = Birch();
            Assert.That(ResourceHarvestSim.CanChop(birch, null, isFists: true), Is.False,
                        "a birch is not vulnerable to fists");
            birch.VulnerableToFists = true;
            Assert.That(ResourceHarvestSim.CanChop(birch, null, isFists: true), Is.True);
        }

        [Test]
        public void AnUnregisteredInstanceIsNotHarvestable()
        {
            // Every index the replication knows about is not necessarily a resource we have a def for.
            var s = NewSim(out _);
            Assert.That(s.Damage(999, 5000, out var def), Is.False);
            Assert.That(def, Is.Null);
            Assert.That(s.IsFelled(999), Is.False);
        }
    }
}
