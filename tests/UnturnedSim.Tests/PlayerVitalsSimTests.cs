using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Vitals stepping extracted from PlayerController.UpdateVitals (MP_PLAN §4 Phase 4 sim-core split).
    // Pins the shipped rates: stamina regen 0.33/s after a 1 s hold, starving/dehydrated/sick bleeds 1.5/s
    // (2/s sick), zero health reports death. Infection self-clear and health regen are asserted as DURATIONS
    // and CONDITIONS below, not as rates -- see the note on days vs decimals.
    //
    // ⚠ HUNGER/THIRST/SPRINT ARE ASSERTED AS DURATIONS, NOT DECIMALS (2026-09-13). They used to read
    // `1f - 0.0050f * 2f`, which restates the implementation in the assertion: the test could only fail if the
    // constant was EDITED, never if it was WRONG, and it said nothing about what the number was chosen to mean.
    // strawberry asked for "food 100->0 over 2 days, water over 1" -- so that is what these now check, against
    // the same day length the game runs on. A rate change that still empties the bar in the intended time passes;
    // one that quietly turns two days into two minutes fails and says so.
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
        public void Sprint_EmptiesAFullBar_InAboutThirteenSeconds()
        {
            var v = new PlayerVitalsSim();
            Run(v, 50, sprinting: true);   // 1 s
            Assert.That(v.Stamina, Is.EqualTo(1f - PlayerVitalsSim.SprintDrainPerSecond).Within(1e-4f));

            // The number that matters is how long a sprint LASTS. 0.22/s was 4.5 s -- a sprint that ends before
            // it starts. strawberry asked for double the capacity and a lower decay on top of that.
            float seconds = 1f / PlayerVitalsSim.SprintDrainPerSecond;
            Assert.That(seconds, Is.GreaterThan(9f), "a full bar must outlast the old 4.5 s by at least 2x");
            Assert.That(seconds, Is.LessThan(30f), "...but sprinting should still be a resource, not free");
        }

        // ⚠ The stamina CEILING is 1.0 by wire contract, not by choice: PlayerVitalsReplication writes it with
        // WriteUnsignedNormalizedFloat(Clamp01(...)), so a sim value above 1 is silently clamped in MP and the
        // client reads a full bar while the server holds more. If anyone "doubles the maximum" by raising this
        // clamp without bumping the wire, the desync is invisible in singleplayer -- so it is pinned here.
        [Test]
        public void StaminaNeverExceedsOne_BecauseTheWireCannotCarryIt()
        {
            var v = new PlayerVitalsSim();
            Run(v, 2000);   // 40 s of pure regen from full
            Assert.That(v.Stamina, Is.LessThanOrEqualTo(1f),
                "stamina above 1.0 is clamped by WriteUnsignedNormalizedFloat -> MP desync");
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
            Assert.That(v.Food, Is.EqualTo(1f - PlayerVitalsSim.FoodDrainPerSecond * 2f).Within(1e-4f));
            Assert.That(v.Water, Is.EqualTo(1f - PlayerVitalsSim.WaterDrainPerSecond * 2f).Within(1e-4f));

            // THE INTENT, not the constant: a full bar has to last the number of DAYS it was specified in.
            Assert.That(1f / PlayerVitalsSim.FoodDrainPerSecond,
                Is.EqualTo(2f * PlayerVitalsSim.GameDaySeconds).Within(1f), "food: 100 -> 0 over two game days");
            Assert.That(1f / PlayerVitalsSim.WaterDrainPerSecond,
                Is.EqualTo(1f * PlayerVitalsSim.GameDaySeconds).Within(1f), "water: 100 -> 0 over one game day");
        }

        [Test]
        public void Health_Regens_OnlyFedHydratedNotSick_BelowMax()
        {
            var v = new PlayerVitalsSim { Health = 50f };
            Run(v, 50);   // 1 s fed + hydrated
            Assert.That(v.Health, Is.EqualTo(50f + v.HealthRegenPerSecond).Within(1e-3f), "one second of regen");
            // THE INTENT (strawberry 2026-09-13 "have health slowly regen"): a full 0 -> 100 takes a quarter of
            // a game day. Asserted as the duration so the number keeps meaning something if MaxHealth moves.
            Assert.That(v.MaxHealth / v.HealthRegenPerSecond,
                Is.EqualTo(PlayerVitalsSim.HealthHealDays * PlayerVitalsSim.GameDaySeconds).Within(1f),
                "a full heal takes HealthHealDays of a game day");
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
        public void Infection_ClearsAFullBarOverOneGameDay()
        {
            // strawberry 2026-09-13: "have infection heal over a full day". Asserted as the DURATION rather than
            // as 0.40 - 0.02, which was the implementation restated -- that form could only fail if someone
            // edited the constant, never if the constant was wrong for what it was chosen to mean.
            Assert.That(1f / PlayerVitalsSim.InfectionClearPerSecond,
                Is.EqualTo(PlayerVitalsSim.InfectionDaysToClear * PlayerVitalsSim.GameDaySeconds).Within(1f),
                "infection: a full bar clears over one game day");
            var v = new PlayerVitalsSim { Infection = 0.40f };
            Run(v, 100);   // 2 s
            Assert.That(v.Infection, Is.EqualTo(0.40f - 2f * PlayerVitalsSim.InfectionClearPerSecond).Within(1e-5f));
        }

        [Test]
        public void Multipliers_ScaleTheRates()
        {
            var m = new PlayerVitalsSim.Multipliers { ExerciseStaminaDrain = 0.5f, CardioStaminaRegen = 2f, SurvivalDrain = 0.8f, VitalityRegen = 2f };
            var v = new PlayerVitalsSim { Health = 50f };
            for (int i = 0; i < 50; i++) v.Step(true, true, Dt, in m);
            Assert.That(v.Stamina, Is.EqualTo(1f - PlayerVitalsSim.SprintDrainPerSecond * 0.5f).Within(1e-4f), "EXERCISE halves the sprint drain");
            Assert.That(v.Food, Is.EqualTo(1f - PlayerVitalsSim.FoodDrainPerSecond * 0.8f).Within(1e-4f), "SURVIVAL slows hunger");
            Assert.That(v.Health, Is.EqualTo(50f + v.HealthRegenPerSecond * 2f).Within(1e-3f), "VITALITY doubles regen");
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
        public void running_out_of_air_costs_you_nothing_but_the_bar()
        {
            // Master asked for a bar that depletes and nothing more; I added drowning damage unasked and they
            // said to pull it (2026-09-07). This PINS the absence, so re-adding it is a deliberate act with a
            // red test in front of it rather than something that drifts back in.
            var v = Fresh();
            Dive(v, PlayerVitalsSim.OxygenSeconds + 20f);
            Assert.That(v.Oxygen, Is.Zero, "the bar bottoms out");
            Assert.That(v.Health, Is.EqualTo(100f), "...and stays there, costing nothing");
        }

        [Test]
        public void a_spent_breath_comes_back_after_surfacing()
        {
            var v = Fresh();
            Dive(v, PlayerVitalsSim.OxygenSeconds + 5f);
            Assert.That(v.Oxygen, Is.Zero);
            Dive(v, PlayerVitalsSim.OxygenRefillSeconds + 1f, submerged: false);
            Assert.That(v.Oxygen, Is.EqualTo(1f).Within(1e-4f), "empty refills as readily as part-spent");
        }
    
        // ---- strawberry 2026-09-10: "bleeding should slowly drain hp ... infection kills you at 100%.
        // below 50% infection drains slowly on its own" ----
        //
        // These three were HUD decorations before this: bleeding's own comment said "purely COSMETIC ...
        // no HP drain", infection decayed unconditionally so any bite could be walked off, and 100% virus
        // merely sat at the -2/s sick drain. Each test below fails on the shipped behaviour, not just on a
        // mutation of the new one.

        static void RunBleed(PlayerVitalsSim v, int ticks, bool bleeding)
        {
            for (int i = 0; i < ticks; i++) v.Step(false, false, false, bleeding, Dt, in None);
        }

        static void RunBroken(PlayerVitalsSim v, int ticks, bool broken)
        {
            for (int i = 0; i < ticks; i++)
                v.Step(false, false, false, false, broken, PlayerTemperatureSim.Band.Comfortable, Dt, in None);
        }

        // ---- what BLOCKS passive regen (strawberry 2026-09-13: "never when taking damage, or having a
        // bleeding or broken leg effect") -----------------------------------------------------------------
        //
        // ⚠ Each of these pairs a blocked run with an IDENTICAL unblocked one. A test that only asserts "health
        // did not rise" passes just as happily if regen is broken outright, or if the run was too short to move
        // a 0.28/s rate past the tolerance -- so the control is the half that makes the blocking claim mean
        // anything. Both halves are the same length and differ only in the flag under test.

        [Test]
        public void BrokenLegs_BlockRegen_AndAnIntactPlayerHeals()
        {
            var hurt = new PlayerVitalsSim { Health = 50f };
            var fine = new PlayerVitalsSim { Health = 50f };
            RunBroken(hurt, 250, broken: true);    // 5 s
            RunBroken(fine, 250, broken: false);   // 5 s -- the control
            Assert.That(hurt.Health, Is.EqualTo(50f).Within(1e-6f), "a fracture does not heal itself while it is a fracture");
            Assert.That(fine.Health, Is.GreaterThan(50f), "...and the identical run WITHOUT it does heal, or the test proves nothing");
        }

        [Test]
        public void TakingDamage_BlocksRegen_UntilTheLockExpires()
        {
            var v = new PlayerVitalsSim { Health = 50f };
            v.NotifyDamaged();
            Assert.That(v.RegenLockDelay, Is.EqualTo(PlayerVitalsSim.RegenDamageLockSeconds).Within(1e-6f));

            // most of the way through the lock, but not past it
            int ticks = (int)((PlayerVitalsSim.RegenDamageLockSeconds - 1f) / Dt);
            RunBroken(v, ticks, broken: false);
            Assert.That(v.Health, Is.EqualTo(50f).Within(1e-6f), "no regen while the post-damage lock is running");

            RunBroken(v, (int)(2f / Dt), broken: false);   // step past the lock
            Assert.That(v.Health, Is.GreaterThan(50f), "regen resumes once the lock expires");
        }

        [Test]
        public void RepeatedHits_KeepTheLockArmed_RatherThanLettingItRunDown()
        {
            // Sustained fire must not heal you between hits. Re-arming is the whole point of a timer over a bool.
            var v = new PlayerVitalsSim { Health = 50f };
            for (int i = 0; i < 20; i++)
            {
                v.NotifyDamaged();
                RunBroken(v, (int)(1f / Dt), broken: false);   // 1 s between hits, 20 s total
            }
            Assert.That(v.Health, Is.EqualTo(50f).Within(1e-6f),
                "20 s of being hit once a second must not regen, even though the lock is only 10 s");
        }

        [Test]
        public void Bleeding_DrainsHealth_AtThreeQuartersPerSecond()
        {
            var v = new PlayerVitalsSim();
            RunBleed(v, 50, bleeding: true);   // 1 s
            Assert.That(v.Health, Is.EqualTo(v.MaxHealth - PlayerVitalsSim.BleedHealthPerSecond).Within(1e-3f));
        }

        [Test]
        public void Bleeding_BlocksRegen_SoADressedWoundIsTheOnlyWayBack()
        {
            // Fed and hydrated and wounded: regen would otherwise add 2/s and MASK the bleed entirely --
            // net +1.25/s, i.e. bleeding would HEAL you. Asserting the sign is the point.
            var v = new PlayerVitalsSim { Health = 50f };
            RunBleed(v, 50, bleeding: true);
            Assert.That(v.Health, Is.LessThan(50f), "a bleeding player must not out-regen the wound");
        }

        [Test]
        public void NotBleeding_StillRegens()
        {
            var v = new PlayerVitalsSim { Health = 50f };
            RunBleed(v, 50, bleeding: false);
            Assert.That(v.Health, Is.GreaterThan(50f));
        }

        [Test]
        public void Infection_BelowTheSickLine_ClearsItself()
        {
            var v = new PlayerVitalsSim { Infection = 0.40f };
            RunBleed(v, 50, bleeding: false);   // 1 s
            Assert.That(v.Infection, Is.LessThan(0.40f), "a mild infection comes down on its own");
            Assert.That(v.Infection, Is.EqualTo(0.40f - PlayerVitalsSim.InfectionClearPerSecond).Within(1e-6f));
        }

        // ⚠ 0.60 USED TO BE THE "HOLDS FOREVER" CASE and is now the clearing one, because the gate moved from
        // "below 50%" to "not currently taking infection damage" (strawberry 2026-09-13). Kept as its own test
        // rather than deleted: the band between the old threshold and the new one is exactly where the rule
        // changed, so if it ever stops clearing again, something has quietly restored the old gate.
        [Test]
        public void Infection_BetweenTheOldThresholdAndTheSickLine_NowClears()
        {
            var v = new PlayerVitalsSim { Infection = 0.60f };
            RunBleed(v, 250, bleeding: false);   // 5 s
            Assert.That(v.Infection, Is.LessThan(0.60f), "0.60 is under the sick line, so it heals");
        }

        [Test]
        public void Infection_WhileItIsCostingYouHealth_DoesNotClearItself()
        {
            // strawberry 2026-09-13: "never heal while taking infection damage". Above InfectionSickAbove the
            // virus is at -2 HP/s, and that is exactly when it must refuse to fall on its own -- otherwise the
            // worst state in the game is also a self-solving one.
            var v = new PlayerVitalsSim { Infection = 0.80f, Health = 100f };
            RunBleed(v, 250, bleeding: false);   // 5 s
            Assert.That(v.Infection, Is.EqualTo(0.80f).Within(1e-6f), "only antibiotics bring a sick player down");
            Assert.That(v.Health, Is.LessThan(100f), "...and it is charging health the whole time, which is the reason");
        }

        [Test]
        public void Infection_AtOneHundredPercent_Kills()
        {
            var v = new PlayerVitalsSim { Infection = 1f };
            bool died = v.Step(false, false, false, false, Dt, in None);
            Assert.That(died, Is.True);
            Assert.That(v.Health, Is.EqualTo(0f));
        }

        [Test]
        public void Infection_JustBelowFatal_DoesNotKillOutright()
        {
            // Distinguishes "kills at 100" from "kills whenever you are very infected": 0.99 is still the
            // slow sick drain, so a single tick must not be lethal from full health.
            var v = new PlayerVitalsSim { Infection = 0.99f };
            bool died = v.Step(false, false, false, false, Dt, in None);
            Assert.That(died, Is.False);
            Assert.That(v.Health, Is.GreaterThan(0f));
        }

        // ---- temperature effects (strawberry 2026-09-10: "being cold drains food and water faster, being
        // freezing hurts you. being hot drains water faster, being boiling hurts you") ----

        static PlayerVitalsSim Exposed(PlayerTemperatureSim.Band band, int ticks, bool drain = true)
        {
            var v = new PlayerVitalsSim();
            for (int i = 0; i < ticks; i++) v.Step(false, false, drain, false, band, Dt, in None);
            return v;
        }

        [Test]
        public void Cold_DrainsFoodAndWater_FasterThanComfortable()
        {
            var warm = Exposed(PlayerTemperatureSim.Band.Comfortable, 500);
            var cold = Exposed(PlayerTemperatureSim.Band.Cold, 500);
            Assert.That(cold.Food, Is.LessThan(warm.Food));
            Assert.That(cold.Water, Is.LessThan(warm.Water));
        }

        [Test]
        public void Hot_DrainsWaterFaster_ButNotFood()
        {
            // The asymmetry IS the feature -- a desert and a blizzard have to be different problems, not one
            // problem at two speeds. Asserting food is UNCHANGED is what pins that.
            var warm = Exposed(PlayerTemperatureSim.Band.Comfortable, 500);
            var hot = Exposed(PlayerTemperatureSim.Band.Hot, 500);
            Assert.That(hot.Water, Is.LessThan(warm.Water));
            Assert.That(hot.Food, Is.EqualTo(warm.Food).Within(1e-5f));
        }

        [Test]
        public void Freezing_And_Boiling_CostHealth()
        {
            Assert.That(Exposed(PlayerTemperatureSim.Band.Freezing, 50).Health,
                Is.EqualTo(100f - PlayerVitalsSim.ExposureHealthPerSecond).Within(1e-3f));
            Assert.That(Exposed(PlayerTemperatureSim.Band.Boiling, 50).Health,
                Is.EqualTo(100f - PlayerVitalsSim.ExposureHealthPerSecond).Within(1e-3f));
        }

        [Test]
        public void MerelyColdOrHot_DoesNotCostHealth()
        {
            // Control: the bands either side of the lethal ones must be survivable indefinitely, or "dress for
            // the weather" becomes "never go outside".
            Assert.That(Exposed(PlayerTemperatureSim.Band.Cold, 500).Health, Is.EqualTo(100f));
            Assert.That(Exposed(PlayerTemperatureSim.Band.Hot, 500).Health, Is.EqualTo(100f));
        }

        [Test]
        public void Exposure_IgnoresTheSurvivalToggle_ButTheDrainMultipliersDoNot()
        {
            // Hunger is a MODE, weather is a HAZARD. With survival off the cold must still kill you and must
            // still not touch food or water.
            var frozen = Exposed(PlayerTemperatureSim.Band.Freezing, 50, drain: false);
            Assert.That(frozen.Health, Is.LessThan(100f));
            Assert.That(frozen.Food, Is.EqualTo(1f));
            Assert.That(frozen.Water, Is.EqualTo(1f));
        }

        [Test]
        public void Exposure_BlocksRegen()
        {
            var v = new PlayerVitalsSim { Health = 50f };
            for (int i = 0; i < 50; i++) v.Step(false, false, false, false, PlayerTemperatureSim.Band.Freezing, Dt, in None);
            Assert.That(v.Health, Is.LessThan(50f), "you cannot out-heal a blizzard");
        }
}
}
