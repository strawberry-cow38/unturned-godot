using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    /// <summary>Whether a gun feeds from a magazine is TWO independent questions, and both have to be asked.
    ///
    /// master 2026-10-05: "or just verify mag TYPE + caliber separately" -> "a flag on the mag and a flag on the
    /// gun too". So the rule is:
    ///   1. the MAGWELL  -- the caliber GROUP, exact: your own magazine, always;
    ///   2. the PATTERN + CARTRIDGE -- a standard-pattern body, in a gun that takes standard bodies, loaded with
    ///      the cartridge this gun chambers.
    ///
    /// ⭐ WHY A FLAG BEATS THE CALIBER-GROUP LIST IT REPLACED. The group number was doing two jobs: the Augewehr
    /// sits in group 201 *while firing 5.56* purely so its magwell can differ from its cartridge -- the group was
    /// secretly encoding "proprietary". Saying that out loud leaves the group meaning only "which magwell", and
    /// the M249 needs no special entry: it is simply a gun that takes standard bodies and also has its own box.</summary>
    [TestFixture]
    public class MagazineCaliberSetTests
    {
        const string Nato556 = "5.56x45mm NATO", Blk300 = ".300 AAC Blackout", Nato762 = "7.62x51mm NATO";
        const int StanagGroup = 1, SawGroup = 12, AugGroup = 201;

        static ItemAsset Gun(int group, string cartridge, bool takesStandard) => new ItemAsset
        { id = 9990, itemName = "Test Gun", type = EItemType.GUN, gunCaliber = group, gunCaliberName = cartridge, gunTakesStandardMags = takesStandard };

        static ItemAsset Mag(int group, string cartridge, bool standard) => new ItemAsset
        { id = 9991, itemName = "Test Mag", type = EItemType.MAGAZINE, magCapacity = 30, magCaliber = group, magRound = cartridge, magStandardPattern = standard };

        [Test]
        public void your_own_magwell_always_fits_even_when_proprietary()
        {
            var aug = Gun(AugGroup, Nato556, takesStandard: false);
            Assert.That(aug.AcceptsMagazine(Mag(AugGroup, Nato556, standard: false)), Is.True,
                        "a gun must always take its own magazine, flags or not");
        }

        [Test]
        public void the_m249_shape_takes_a_standard_body_in_its_own_cartridge()
        {
            var saw = Gun(SawGroup, Nato556, takesStandard: true);
            Assert.That(saw.AcceptsMagazine(Mag(SawGroup, Nato556, standard: false)), Is.True, "its own 200-round box");
            Assert.That(saw.AcceptsMagazine(Mag(StanagGroup, Nato556, standard: true)), Is.True, "...and a STANAG body");
        }

        [Test]
        public void a_standard_body_in_the_wrong_cartridge_is_still_refused()
        {
            // ⭐ THE CONTROL THAT STOPS CLAUSE 2 BEING "ANY STANDARD MAG FITS". STANAG-pattern bodies genuinely
            // exist in more than one cartridge -- the .300 Blackout magazine is one -- so dropping the cartridge
            // test would feed subsonic .300 into a 5.56 belt-fed and look perfectly fine doing it.
            var saw = Gun(SawGroup, Nato556, takesStandard: true);
            Assert.That(saw.AcceptsMagazine(Mag(StanagGroup, Blk300, standard: true)), Is.False,
                        "a STANAG-pattern .300 BLK magazine is the right body and the WRONG round");
            Assert.That(saw.AcceptsMagazine(Mag(StanagGroup, Nato762, standard: true)), Is.False);
        }

        [Test]
        public void a_proprietary_body_does_not_leak_to_guns_that_take_standard()
        {
            // The M249's box must not become loadable by every 5.56 rifle just because they take standard mags.
            var rifle = Gun(StanagGroup, Nato556, takesStandard: true);
            Assert.That(rifle.AcceptsMagazine(Mag(StanagGroup, Nato556, standard: true)), Is.True, "control: STANAG still fits it");
            Assert.That(rifle.AcceptsMagazine(Mag(SawGroup, Nato556, standard: false)), Is.False,
                        "the 200-round box is proprietary -- same cartridge is not enough");
        }

        [Test]
        public void a_proprietary_gun_still_refuses_standard_magazines()
        {
            // The AUG/G36/FAMAS case, which a cartridge-only rule would have broken: same round, must not fit.
            var aug = Gun(AugGroup, Nato556, takesStandard: false);
            Assert.That(aug.AcceptsMagazine(Mag(StanagGroup, Nato556, standard: true)), Is.False,
                        "\"Does not interchange with STANAG\" is a property of the GUN, and it has to survive this");
        }

        [Test]
        public void a_gun_with_no_flags_behaves_exactly_as_before()
        {
            // ⚠ THE REGRESSION GUARD. Most guns will never set either flag; if an unset flag stopped meaning the
            // old single-group rule, every gun in the catalog would re-decide what it accepts at once.
            var plain = new ItemAsset { id = 9992, type = EItemType.GUN, gunCaliber = StanagGroup };
            Assert.That(plain.AcceptsMagazine(Mag(StanagGroup, Nato556, standard: true)), Is.True);
            Assert.That(plain.AcceptsMagazine(Mag(SawGroup, Nato556, standard: true)), Is.False);
            Assert.That(plain.AcceptsMagazine(null), Is.False, "and nothing is not a magazine");
        }
    }
}
