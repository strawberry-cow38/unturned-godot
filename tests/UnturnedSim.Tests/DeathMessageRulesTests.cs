using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Every field of a death line is an independent opt-in, so the thing worth testing is the MATRIX rather
    // than one happy path: each clause must appear when its flag is on AND be absent when it is off. A test
    // that only asserts presence passes on a formatter that ignores its flags entirely.
    public class DeathMessageRulesTests
    {
        static DeathMessageOptions All => new DeathMessageOptions
            { ShowKiller = true, ShowWeapon = true, ShowDistance = true, ShowLocation = true };
        static DeathMessageOptions None => default;

        static string Fmt(DeathMessageOptions o, bool hasKiller = true, string weapon = "eaglefire",
                          float dist = 42.4f, bool hasLoc = true, string victim = "bob", string killer = "alice")
            => DeathMessageRules.Format(victim, killer, weapon, hasKiller, dist, hasLoc, 1.4f, 2.5f, -3.6f, o);

        [Test]
        public void Default_is_bare_death_line()
        {
            Assert.That(Fmt(None, hasKiller: false), Is.EqualTo("bob died"),
                "an unconfigured server must say exactly 'playername died'");
        }

        [Test]
        public void Killer_exists_but_nothing_opted_in_names_nobody()
        {
            string s = Fmt(None);
            Assert.That(s, Is.EqualTo("bob was killed"));
            Assert.That(s, Does.Not.Contain("alice"), "killer name leaked with ShowKiller off");
            Assert.That(s, Does.Not.Contain("eaglefire"), "weapon leaked with ShowWeapon off");
            Assert.That(s, Does.Not.Contain("42"), "distance leaked with ShowDistance off");
        }

        [Test]
        public void Each_flag_adds_only_its_own_clause()
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowKiller = true }), Is.EqualTo("bob was killed by alice"));
            Assert.That(Fmt(new DeathMessageOptions { ShowWeapon = true }), Is.EqualTo("bob was killed with eaglefire"));
            Assert.That(Fmt(new DeathMessageOptions { ShowDistance = true }), Is.EqualTo("bob was killed from 42m"));
            Assert.That(Fmt(new DeathMessageOptions { ShowLocation = true }), Is.EqualTo("bob was killed at 1, 3, -4"));
        }

        [Test]
        public void All_flags_compose_in_order()
        {
            Assert.That(Fmt(All), Is.EqualTo("bob was killed by alice with eaglefire from 42m at 1, 3, -4"));
        }

        // The weapon and distance clauses are gated on a killer EXISTING, not on being named -- so an
        // environmental death must never claim one, however the flags are set.
        [Test]
        public void Environmental_death_never_claims_a_weapon_or_a_range()
        {
            string s = Fmt(All, hasKiller: false);
            Assert.That(s, Is.EqualTo("bob died at 1, 3, -4"));
            Assert.That(s, Does.Not.Contain("eaglefire"));
            Assert.That(s, Does.Not.Contain("from"));
            Assert.That(s, Does.Not.Contain("alice"));
        }

        [Test]
        public void Missing_weapon_drops_the_clause_rather_than_printing_nothing()
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowWeapon = true }, weapon: null),
                Is.EqualTo("bob was killed"), "a null weapon must not produce 'with '");
            Assert.That(Fmt(new DeathMessageOptions { ShowWeapon = true }, weapon: "   "),
                Is.EqualTo("bob was killed"), "whitespace weapon must not produce 'with '");
        }

        // A caller that could not resolve the attacker's position passes a negative; printing "from -1m"
        // would read as a real measurement taken at a real range.
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Unresolvable_distance_is_omitted_not_printed(float d)
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowDistance = true }, dist: d), Is.EqualTo("bob was killed"));
        }

        [Test]
        public void Zero_distance_is_a_real_measurement_and_is_kept()
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowDistance = true }, dist: 0f),
                Is.EqualTo("bob was killed from 0m"), "point blank is a fact, not a failed lookup");
        }

        [Test]
        public void Location_is_suppressed_when_the_caller_has_none()
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowLocation = true }, hasLoc: false),
                Is.EqualTo("bob was killed"));
        }

        [Test]
        public void Missing_names_fall_back_rather_than_leaving_a_gap()
        {
            Assert.That(Fmt(All, victim: "", hasKiller: false, hasLoc: false), Is.EqualTo("a player died"));
            Assert.That(Fmt(new DeathMessageOptions { ShowKiller = true }, killer: null),
                Is.EqualTo("bob was killed by a player"));
        }

        [Test]
        public void Names_are_trimmed_so_padding_cannot_fake_a_gap()
        {
            Assert.That(Fmt(new DeathMessageOptions { ShowKiller = true }, victim: "  bob  ", killer: " alice "),
                Is.EqualTo("bob was killed by alice"));
        }
    }
}
