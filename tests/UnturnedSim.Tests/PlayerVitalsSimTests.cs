using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Vitals stepping extracted from PlayerController.UpdateVitals (MP_PLAN §4 Phase 4 sim-core split).
    // Pins the shipped rates: sprint drains stamina 0.22/s, regen 0.33/s after a 1 s hold, hunger/thirst
    // 0.005/0.007/s behind the survival toggle, infection clears 0.01/s, health regens 2/s while fed +
    // hydrated + not sick, starving/dehydrated/sick bleeds 1.5/s (2/s sick), zero health reports death.
    [TestFixture]
    public class PlayerVitalsSimTests
    {
        const float Dt = 0.02f;   // the 50 Hz tick
        static readonly PlayerVitalsSim.Multipliers None = PlayerVitalsSim.Multipliers.None;

        static void Run(PlayerVitalsSim v, int ticks, bool sprinting = false, bool drain = false)
        {
            for (int i = 0; i < ticks; i++) v.Step(sprinting, drain, Dt, in None);
        }

        [Test]
        public void Sprint_DrainsStamina_AtPointTwoTwoPerSecond()
        {
            var v = new PlayerVitalsSim();
            Run(v, 50, sprinting: true);   // 1 s
            Assert.That(v.Stamina, Is.EqualTo(1f - 0.22f).Within(1e-4f));
        }

        [Test]
        public void StaminaRegen_WaitsOneSecond_ThenRefillsAtPointThreeThree()
        {
            var v = new PlayerVitalsSim { Stamina = 0.5f };
            v.Step(true, false, Dt, in None);          // one sprint tick arms the 1 s delay
            float afterSprint = v.Stamina;
            Run(v, 40);                                 // 0.8 s of the hold -- safely inside it, no regen yet
            Assert.That(v.Stamina, Is.EqualTo(afterSprint).Within(1e-5f), "regen held during the delay");
            Run(v, 20);                                 // cross the delay boundary (float-edge ticks land here)
            float regenStart = v.Stamina;
            Assert.That(regenStart, Is.GreaterThan(afterSprint), "regen kicked in after the 1 s hold");
            Run(v, 50);                                 // one clean second of pure regen
            Assert.That(v.Stamina, Is.EqualTo(regenStart + 0.33f).Within(1e-3f), "0.33/s once the delay elapsed");
        }

        [Test]
        public void HungerThirst_DrainOnlyBehindTheToggle()
        {
            var v = new PlayerVitalsSim();
            Run(v, 100);   // toggle off (SP default)
            Assert.That(v.Food, Is.EqualTo(1f), "no drain with survival off");
            Assert.That(v.Water, Is.EqualTo(1f));
            Run(v, 100, drain: true);   // 2 s of survival drain
            Assert.That(v.Food, Is.EqualTo(1f - 0.0050f * 2f).Within(1e-4f));
            Assert.That(v.Water, Is.EqualTo(1f - 0.0070f * 2f).Within(1e-4f));
        }

        [Test]
        public void Health_Regens_OnlyFedHydratedNotSick_BelowMax()
        {
            var v = new PlayerVitalsSim { Health = 50f };
            Run(v, 50);   // 1 s fed + hydrated
            Assert.That(v.Health, Is.EqualTo(52f).Within(1e-3f), "2/s regen");
            v.Infection = 0.9f;   // sick blocks regen AND bleeds
            float h = v.Health;
            v.Step(false, false, Dt, in None);
            Assert.That(v.Health, Is.LessThan(h), "heavy infection loses health");
            var hungry = new PlayerVitalsSim { Health = 50f, Food = 0.2f };
            hungry.Step(false, false, Dt, in None);
            Assert.That(hungry.Health, Is.EqualTo(50f), "food <= 0.30 blocks regen (but above zero doesn't bleed)");
        }

        [Test]
        public void Starvation_Bleeds_AndReportsDeathAtZero()
        {
            var v = new PlayerVitalsSim { Health = 0.02f, Food = 0f };
            Assert.That(v.Step(false, false, Dt, in None), Is.True, "health bottomed out -> died this step");
            Assert.That(v.Health, Is.EqualTo(0f));
            Assert.That(v.Step(false, false, Dt, in None), Is.True, "still zero (caller owns not stepping the dead)");
        }

        [Test]
        public void Infection_Clears_AtPointZeroOnePerSecond()
        {
            var v = new PlayerVitalsSim { Infection = 0.5f };
            Run(v, 100);   // 2 s
            Assert.That(v.Infection, Is.EqualTo(0.5f - 0.02f).Within(1e-4f));
        }

        [Test]
        public void Multipliers_ScaleTheRates()
        {
            var m = new PlayerVitalsSim.Multipliers { ExerciseStaminaDrain = 0.5f, CardioStaminaRegen = 2f, SurvivalDrain = 0.8f, VitalityRegen = 2f };
            var v = new PlayerVitalsSim { Health = 50f };
            for (int i = 0; i < 50; i++) v.Step(true, true, Dt, in m);
            Assert.That(v.Stamina, Is.EqualTo(1f - 0.22f * 0.5f).Within(1e-4f), "EXERCISE halves the sprint drain");
            Assert.That(v.Food, Is.EqualTo(1f - 0.0050f * 0.8f).Within(1e-4f), "SURVIVAL slows hunger");
            Assert.That(v.Health, Is.EqualTo(50f + 2f * 2f).Within(1e-3f), "VITALITY doubles regen");
        }

        // ---- oxygen (master 2026-09-06: "depletes when underwater") ------------------------------------

        static PlayerVitalsSim Fresh() => new PlayerVitalsSim { Health = 100f };
        static void Dive(PlayerVitalsSim v, float seconds, bool submerged = true)
        {
            var m = PlayerVitalsSim.Multipliers.None;
            for (int i = 0; i < (int)(seconds / Dt); i++) v.Step(false, submerged, false, Dt, in m);
        }

        [Test]
        public void oxygen_drains_only_while_the_head_is_under()
        {
            var v = Fresh();
            Dive(v, 10f);
            Assert.That(v.Oxygen, Is.EqualTo(1f - 10f / PlayerVitalsSim.OxygenSeconds).Within(1e-3f));
            var dry = Fresh();
            Dive(dry, 10f, submerged: false);
            Assert.That(dry.Oxygen, Is.EqualTo(1f), "a full breath above water stays full");
        }

        [Test]
        public void surfacing_refills_far_faster_than_diving_empties()
        {
            var v = Fresh();
            Dive(v, 15f);
            float half = v.Oxygen;
            Assert.That(half, Is.LessThan(0.6f));
            Dive(v, PlayerVitalsSim.OxygenRefillSeconds + 1f, submerged: false);
            Assert.That(v.Oxygen, Is.EqualTo(1f).Within(1e-4f), "a surfacing player gets their air back in a gulp");
        }

        [Test]
        public void nothing_hurts_until_the_air_is_actually_gone()
        {
            // The teeth: if drowning were keyed on "submerged" rather than "out of air", this fails immediately.
            var v = Fresh();
            Dive(v, PlayerVitalsSim.OxygenSeconds - 1f);
            Assert.That(v.Health, Is.EqualTo(100f), "still holding your breath");
            Assert.That(v.LastDrownDamage, Is.Zero);
            Dive(v, 3f);
            Assert.That(v.Oxygen, Is.Zero);
            Assert.That(v.Health, Is.LessThan(100f), "...and then the water starts taking health");
            Assert.That(v.LastDrownDamage, Is.GreaterThan(0f), "reported separately for the server's damage routing");
        }

        [Test]
        public void drowning_is_not_gated_behind_the_hunger_toggle()
        {
            // survivalDrain: false throughout -- hunger ships OFF, and breath must not ship off with it.
            var v = Fresh();
            Dive(v, PlayerVitalsSim.OxygenSeconds + 5f);
            Assert.That(v.Health, Is.LessThan(100f), "you drown whether or not hunger is switched on");
        }

        [Test]
        public void a_drowning_player_does_not_regenerate_out_of_it()
        {
            // Fed and hydrated and below max HP is exactly the regen branch's condition, so without the
            // LastDrownDamage guard the two would fight and a well-fed diver would tread water at ~constant HP.
            var v = new PlayerVitalsSim { Health = 50f, Food = 1f, Water = 1f, Oxygen = 0f };
            float before = v.Health;
            Dive(v, 2f);
            Assert.That(v.Health, Is.LessThan(before), "drowning outranks being well fed");
        }
    }
}
