using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    /// <summary>A gun can feed from more than one magazine caliber GROUP.
    ///
    /// master 2026-10-05: "make the m249 take stanag magazines as well as its own box mags".
    ///
    /// ⭐ THIS IS A PORT, NOT AN INVENTION. Retail's ItemGunAsset has carried `magazineCalibers` as an ARRAY all
    /// along -- parsed from `Magazine_Calibers` / `Magazine_Caliber_N`, falling back to a one-element array
    /// holding plain `Caliber` -- and UseableGun.cs:2865 accepts on an INTERSECTION of the gun's groups with the
    /// magazine's. The port only had the scalar half, so one gun could feed from exactly one group and there was
    /// no way to say what master asked for.
    ///
    /// The fallback is the load-bearing half of the design: a gun whose set was never populated must behave
    /// EXACTLY as it did before, or this change quietly re-decides what every other gun in the game accepts.</summary>
    [TestFixture]
    public class MagazineCaliberSetTests
    {
        const int Stanag = 1, Saw = 12, Other = 2;

        static ItemAsset Gun(int caliber, int[] set = null)
            => new ItemAsset { id = 9990, itemName = "Test Gun", type = EItemType.GUN, gunCaliber = caliber, gunMagazineCalibers = set };

        [Test]
        public void a_gun_with_no_set_keeps_the_old_single_caliber_rule()
        {
            // ⚠ THE REGRESSION GUARD. Most guns will never declare a set, and if an unpopulated array stopped
            // meaning "just my own caliber" every one of them would change what it accepts at once.
            var g = Gun(Stanag);
            Assert.That(g.AcceptsMagazineCaliber(Stanag), Is.True, "its own group still fits");
            Assert.That(g.AcceptsMagazineCaliber(Saw), Is.False, "and nothing else does");

            var empty = Gun(Stanag, new int[0]);
            Assert.That(empty.AcceptsMagazineCaliber(Stanag), Is.True, "an EMPTY set is treated as unpopulated, not as 'accepts nothing'");
            Assert.That(empty.AcceptsMagazineCaliber(Saw), Is.False);
        }

        [Test]
        public void a_gun_with_two_groups_accepts_both_and_only_those()
        {
            var saw = Gun(Saw, new[] { Saw, Stanag });
            Assert.That(saw.AcceptsMagazineCaliber(Saw), Is.True, "its own box magazine");
            Assert.That(saw.AcceptsMagazineCaliber(Stanag), Is.True, "...and STANAG");
            // The control that stops this being "accepts anything": a third group must still be refused.
            Assert.That(saw.AcceptsMagazineCaliber(Other), Is.False, "a group it was never given must still be refused");
        }

        [Test]
        public void the_extra_group_does_not_leak_to_other_guns()
        {
            // The divergence is per GUN ASSET, not a global "group 12 and group 1 are now interchangeable".
            // A STANAG-fed rifle must NOT gain the M249's box magazine as a side effect.
            var rifle = Gun(Stanag, new[] { Stanag });
            Assert.That(rifle.AcceptsMagazineCaliber(Stanag), Is.True);
            Assert.That(rifle.AcceptsMagazineCaliber(Saw), Is.False,
                        "letting the M249 take STANAG must not let every STANAG rifle take a 200-round box");
        }
    }
}
