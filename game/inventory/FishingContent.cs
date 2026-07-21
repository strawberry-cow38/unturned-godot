using System.Collections.Generic;

namespace SDG.Unturned
{
    // Fishing content: the PEI fishing spawn table (Spawns/Fishing/Fishing_PEI.asset) and the rod->sim config, kept
    // in the game layer so the engine-free FishingSim stays data-driven. Weights + item ids are the retail values
    // (the .asset lists GUIDs; resolved here to the numeric ids the port's ItemCatalog already loads for each).
    public static class FishingContent
    {
        // Fishing_PEI.asset, ocean table. (Minnow/Shrimp/Seaweed/Junk aren't in the port catalog yet -> the fish
        // that ARE loaded, at their real relative weights; a caught-item that isn't registered just no-ops the add.)
        public static readonly FishSpawn[] PeiOcean =
        {
            new FishSpawn(1349, 120),  // Raw Goldfish
            new FishSpawn(504,   72),  // Raw Trout
            new FishSpawn(505,   66),  // Raw Salmon
            new FishSpawn(1351,  51),  // Raw Bass
            new FishSpawn(1959,  36),  // Raw Northern Pike
        };

        // Configure a fresh FishingSim for a cast: retail Rod_Fishing.dat values + the PEI table + the caster's skill.
        // (Bite intervals: the SDK Gameplay fallback is 1.0/1.0; we widen to a playable 6-14 s window so a bite is a
        // moment you wait for, not instant.)
        public static void ConfigureForPei(FishingSim sim, byte fishingSkillLevel)
        {
            sim.MinBiteInterval = 6f;
            sim.MaxBiteInterval = 14f;
            sim.MaxStrengthBiteMultiplier = 1.5f;   // a strong cast reaches deeper water -> bites take a little longer
            sim.FishBiteIntervalMultiplier = 1f;    // Rod_Fishing Fish_Bite_Interval_Multiplier (default)
            sim.RewardExperienceMin = 3;            // Rod_Fishing Reward_Experience_Min/Max
            sim.RewardExperienceMax = 3;
            sim.RodFallbackRewardId = 504;          // Reward_ID fallback (Raw Trout stand-in)
            sim.FishingSkillLevel = fishingSkillLevel;
            sim.SetRewardTable(PeiOcean);

            // Rod_Fishing.dat CatchChallenge block (all the SDK-default values): a bite opens the tracking minigame.
            sim.EnableCatchChallenge = true;
            sim.CatchChallengeCursorSize = 2_000;                 // 0.2 * FIXED
            sim.CatchChallengeGravity = 10_000;                   // 1.0
            sim.CatchChallengeAcceleration = 10_000;              // 1.0
            sim.CatchChallengeUpperRestitution = 5_000;           // 0.5
            sim.CatchChallengeLowerRestitution = 5_000;           // 0.5
            sim.CatchChallengeCaptureSpeedMultiplier = 1f;
            sim.CatchChallengeEscapeSpeedMultiplier = 1f;
            sim.Catchable = FishingCatchableProperties.Default;
        }
    }
}
