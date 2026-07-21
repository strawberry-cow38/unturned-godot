using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // FishingSim = the engine-free port of UseableFisher (U3-SDK). Pins the retail loop: Idle -> hold to charge
    // the strength gauge -> release to cast -> bobber reaches water -> server bite timer -> a WARNING+CATCH window
    // opens -> press inside it to land a fish from the level's weighted spawn table + earn XP. Pressing outside the
    // window reels the empty line in. The optional CatchChallenge minigame is a later increment (challenge-disabled
    // path here, matching UseableFisher.startPrimary's non-challenge branch).
    [TestFixture]
    public class FishingSimTests
    {
        const float Dt = 0.02f;            // 50 Hz tick
        const ushort Trout = 504, Salmon = 505, Junk = 9999;

        static FishingSim MakeSim(int seed = 7)
        {
            var s = new FishingSim(seed);
            s.MinBiteInterval = s.MaxBiteInterval = 2.0f;   // deterministic 2 s bite
            s.RewardExperienceMin = s.RewardExperienceMax = 5;
            s.SetRewardTable(new[] { new FishSpawn(Trout, 72), new FishSpawn(Salmon, 66) });
            return s;
        }

        // Charge the gauge a bit, cast, and settle the bobber into water -> LineDeployed with the bite timer armed.
        static void CastToWater(FishingSim s)
        {
            s.Equip();
            s.Press();                              // Idle -> PreparingToCast
            for (int i = 0; i < 10; i++) s.Tock();  // charge strength
            s.Release();                            // -> Casting
            s.ConfirmBobberInWater();               // -> LineDeployed
        }

        static void StepUntil(FishingSim s, System.Func<bool> cond, int maxTicks = 100000)
        {
            for (int i = 0; i < maxTicks && !cond(); i++) s.Simulate(Dt);
        }

        [Test]
        public void FullCast_Bite_PressInWindow_LandsAFishAndXP()
        {
            var s = MakeSim();
            CastToWater(s);
            Assert.That(s.State, Is.EqualTo(EFishingState.LineDeployed));

            StepUntil(s, () => s.HasBite);
            Assert.That(s.HasBite, "a fish should bite after the interval");

            StepUntil(s, () => s.IsBiteWindowOpen);
            var caught = s.Press();

            Assert.That(caught.Success, "pressing inside the window lands the fish");
            Assert.That(caught.ItemId, Is.AnyOf(Trout, Salmon), "fish comes from the spawn table");
            Assert.That(caught.Experience, Is.EqualTo(5));
            Assert.That(s.State, Is.EqualTo(EFishingState.Idle), "landing a fish reels the line back to idle");
        }

        [Test]
        public void PressBeforeWindow_ReelsInEmpty_NoFish()
        {
            var s = MakeSim();
            CastToWater(s);
            StepUntil(s, () => s.HasBite);
            // bite just fired: timeSinceFishNotification < WARNING_DURATION -> window not open yet
            Assert.That(s.IsBiteWindowOpen, Is.False);
            var caught = s.Press();
            Assert.That(caught.Success, Is.False, "reeling in before the fish commits catches nothing");
            Assert.That(s.State, Is.EqualTo(EFishingState.Idle));
        }

        [Test]
        public void MissedWindow_ReArmsForAnotherBite()
        {
            var s = MakeSim();
            CastToWater(s);
            StepUntil(s, () => s.HasBite);
            // never press; let the catch window lapse (>5 s after notification) -> the fish leaves and re-arms
            StepUntil(s, () => !s.HasBite && s.State == EFishingState.LineDeployed);
            Assert.That(s.HasBite, Is.False, "an ignored bite eventually resets");
            // and a fresh bite still comes
            StepUntil(s, () => s.HasBite);
            Assert.That(s.HasBite, "the line re-arms and another fish bites");
        }

        [Test]
        public void StrengthGauge_StaysNormalized_AndOscillates()
        {
            var s = new FishingSim();
            s.FishingSkillLevel = 0;
            s.Equip();
            s.Press();   // PreparingToCast
            float min = 1f, max = 0f;
            for (int i = 0; i < 300; i++)
            {
                s.Tock();
                min = System.MathF.Min(min, s.StrengthMultiplier);
                max = System.MathF.Max(max, s.StrengthMultiplier);
                Assert.That(s.StrengthMultiplier, Is.InRange(0f, 1f));
            }
            Assert.That(max, Is.GreaterThan(0.9f), "the bar sweeps up toward a strong cast");
            Assert.That(min, Is.LessThan(0.1f), "and back down toward a weak one");
        }

        [Test]
        public void FishingSkill_LengthensTheStrengthPeriod()
        {
            // period = 100 + level*20 tocks; the peak (strength ~= 1) lands later at higher skill.
            int PeakTick(byte level)
            {
                var s = new FishingSim();
                s.FishingSkillLevel = level;
                s.Equip(); s.Press();
                int peak = 0; float best = -1f;
                for (int i = 1; i <= 260; i++) { s.Tock(); if (s.StrengthMultiplier > best) { best = s.StrengthMultiplier; peak = i; } }
                return peak;
            }
            Assert.That(PeakTick(5), Is.GreaterThan(PeakTick(0)), "higher fishing skill = slower, easier gauge");
        }

        [Test]
        public void EmptyTable_GrantsRodFallback_And_SingleEntryTableIsDeterministic()
        {
            var s = new FishingSim(3);
            s.MinBiteInterval = s.MaxBiteInterval = 1.0f;
            s.RewardExperienceMin = s.RewardExperienceMax = 3;
            s.RodFallbackRewardId = Junk;
            s.SetRewardTable(null);                       // no table -> rod fallback
            CastToWater(s);
            StepUntil(s, () => s.HasBite);
            StepUntil(s, () => s.IsBiteWindowOpen);
            Assert.That(s.Press().ItemId, Is.EqualTo(Junk));

            var s2 = new FishingSim(3);
            s2.MinBiteInterval = s2.MaxBiteInterval = 1.0f;
            s2.SetRewardTable(new[] { new FishSpawn(Salmon, 100) });   // single entry -> always that fish
            CastToWater(s2);
            StepUntil(s2, () => s2.HasBite);
            StepUntil(s2, () => s2.IsBiteWindowOpen);
            Assert.That(s2.Press().ItemId, Is.EqualTo(Salmon));
        }

        // End-to-end reward path: a landed fish actually lands in a real PlayerInventory and pays real skill XP.
        [Test]
        public void Catch_GrantsFishIntoInventory_AndPaysSkillXP()
        {
            // register the fish as a 1x1 food item so tryAddItem can size + place it in the 5x3 pockets
            Assets.add(new ItemAsset { id = Trout, itemName = "Raw Trout", type = EItemType.FOOD, size_x = 1, size_y = 1 });
            Assets.add(new ItemAsset { id = Salmon, itemName = "Raw Salmon", type = EItemType.FOOD, size_x = 1, size_y = 1 });

            var inv = new PlayerInventory();
            var skills = new PlayerSkills();
            uint xpBefore = skills.experience;

            var s = MakeSim();
            CastToWater(s);
            StepUntil(s, () => s.HasBite);
            StepUntil(s, () => s.IsBiteWindowOpen);
            var caught = s.Press();
            Assert.That(caught.Success);

            // the grant the game layer performs (UseableFisher.GrantRewards): item into inventory + XP to skills
            Assert.That(inv.tryAddItem(new Item(caught.ItemId)), "the caught fish fits in the pockets");
            skills.AwardExperience((uint)caught.Experience);

            Assert.That(inv.getItemCount(caught.ItemId), Is.EqualTo(1), "the fish is in the inventory");
            Assert.That(skills.experience, Is.EqualTo(xpBefore + 5), "fishing paid XP");
        }

        // --- catch-challenge minigame (increment 3) ---

        static FishingSim MakeChallengeSim(int seed = 11)
        {
            var s = MakeSim(seed);
            s.EnableCatchChallenge = true;
            s.Catchable = FishingCatchableProperties.Default;
            return s;
        }

        static void HookAndEnterChallenge(FishingSim s)
        {
            CastToWater(s);
            StepUntil(s, () => s.HasBite);
            StepUntil(s, () => s.IsBiteWindowOpen);
            s.Press();   // opt-in rod -> enters the challenge instead of an instant catch
        }

        [Test]
        public void Challenge_EntersOnBite_WhenRodOptsIn()
        {
            var s = MakeChallengeSim();
            HookAndEnterChallenge(s);
            Assert.That(s.State, Is.EqualTo(EFishingState.CatchChallenge), "an opt-in rod opens the minigame on the bite");
        }

        [Test]
        public void Challenge_PerfectTracker_LandsTheFish()
        {
            var s = MakeChallengeSim();
            HookAndEnterChallenge(s);

            FishingCatch caught = FishingCatch.None;
            for (int i = 0; i < 4000 && s.State == EFishingState.CatchChallenge; i++)
            {
                // center the cursor on the fish (hold to rise, release to fall)
                float cursorCenter = s.ChallengeCursorPos + s.ChallengeCursorSizeNorm / 2f;
                if (cursorCenter < s.ChallengeFishPos) s.Press(); else s.Release();
                s.Tock();
                if (s.TryTakePendingCatch(out caught)) break;
            }
            Assert.That(caught.Success, "tracking the fish fills the capture bar and lands it");
            Assert.That(caught.ItemId, Is.AnyOf(Trout, Salmon));
            Assert.That(s.State, Is.EqualTo(EFishingState.Idle));
        }

        [Test]
        public void Challenge_NoInput_FishEscapes()
        {
            var s = MakeChallengeSim();
            HookAndEnterChallenge(s);
            bool escaped = false;
            for (int i = 0; i < 6000 && !escaped; i++)
            {
                s.Release();               // never track it -> cursor sinks, fish gets away
                s.Tock();
                escaped = s.State != EFishingState.CatchChallenge;
                Assert.That(s.TryTakePendingCatch(out _), Is.False, "you can't land a fish you never track");
            }
            Assert.That(escaped, "an untracked fish escapes");
            Assert.That(s.State, Is.EqualTo(EFishingState.LineDeployed), "the line re-arms after an escape");
        }

        [Test]
        public void Challenge_FishAndCursor_StayNormalized()
        {
            var s = MakeChallengeSim();
            HookAndEnterChallenge(s);
            for (int i = 0; i < 1500 && s.State == EFishingState.CatchChallenge; i++)
            {
                if ((i / 20) % 2 == 0) s.Press(); else s.Release();   // jerky input to slam the walls
                s.Tock();
                Assert.That(s.ChallengeFishPos, Is.InRange(0f, 1f));
                Assert.That(s.ChallengeCursorPos, Is.InRange(0f, 1f));
                s.TryTakePendingCatch(out _);
            }
        }
    }
}
