using System;
using System.Collections.Generic;

namespace SDG.Unturned
{
    // Engine-free port of Unturned's fishing loop (SDG.Unturned.UseableFisher + ItemFisherAsset). Ground truth:
    // U3-SDK Assets/Runtime/Assembly-CSharp/Unturned/Useable/UseableFisher.cs. Deterministic + Godot-free so it
    // unit-tests the same way PlayerMovementSim / PlayerVitalsSim do -- the game layer (PlayerController) owns the
    // rod hold, the bobber node, the viewmodel anims and the water raycast; this owns the STATE + TIMING + reward.
    //
    // Retail state machine: Idle -> PreparingToCast (strength gauge) -> [Cast anim -> bobber flies -> lands in
    // water] LineDeployed (server bite timer) -> fish notification -> a catch WINDOW opens -> press to catch ->
    // reward. The optional CatchChallenge minigame (ItemFisherAsset.EnableCatchChallenge) is a later increment;
    // this is the "challenge disabled" path (UseableFisher.startPrimary: press-in-window -> GrantRewards+ReelIn).
    public enum EFishingState
    {
        Idle,            // rod out, not cast
        PreparingToCast, // holding primary: strength gauge oscillating
        Casting,         // released: bobber in flight, waiting to reach water
        LineDeployed,    // bobber floating; server bite timer running; press-in-window catches
        Reeling,         // reel animation playing (transient; game layer times it, sim returns to Idle)
    }

    // One weighted fish entry from a level fishing spawn table (Spawns/Fishing/Fishing_PEI.asset).
    public readonly struct FishSpawn
    {
        public readonly ushort ItemId;
        public readonly int Weight;
        public FishSpawn(ushort itemId, int weight) { ItemId = itemId; Weight = weight; }
    }

    // Result of a catch attempt. Success => the game layer grants ItemId to the inventory and pays Experience XP.
    public readonly struct FishingCatch
    {
        public readonly bool Success;
        public readonly ushort ItemId;
        public readonly int Experience;
        public FishingCatch(bool success, ushort itemId, int experience) { Success = success; ItemId = itemId; Experience = experience; }
        public static FishingCatch None => new FishingCatch(false, 0, 0);
    }

    public sealed class FishingSim
    {
        // --- retail UseableFisher constants (seconds) ---
        public const float WARNING_DURATION = 1.0f;        // bubbles/tug before the fish takes the bait
        public const float CATCH_WINDOW = 1.4f;            // how long the player has to press before it escapes
        public const float SERVER_LENIENCY_WINDOW = 1.0f;  // extra grace the server allows for net delay
        private const float MISSED_BITE_TIMEOUT = 5.0f;    // UseableFisher.simulate: >5s after notify with no catch -> re-arm

        // --- gameplay config (retail Provider.modeConfigData.Gameplay; SDK fallbacks all 1.0) ---
        public float MinBiteInterval = 1.0f;
        public float MaxBiteInterval = 1.0f;
        public float MaxStrengthBiteMultiplier = 1.0f;     // strong casts wait longer (config; 1.0 = no effect)
        public float WeatherBiteMultiplier = 1.0f;         // LevelLighting.GetFishingBiteIntervalMultiplier (rain speeds bites)

        // --- per-rod (ItemFisherAsset) ---
        public float FishBiteIntervalMultiplier = 1.0f;    // Fish_Bite_Interval_Multiplier
        public int RewardExperienceMin = 3;                // Reward_Experience_Min
        public int RewardExperienceMax = 3;                // Reward_Experience_Max

        // --- injected from PlayerSkills each cast (affects the strength-gauge period) ---
        public byte FishingSkillLevel;

        // Weighted reward table (the level's fishing spawn table). If empty, RodFallbackRewardId is granted
        // (retail EFishingRewardMode.Rod / the ItemFisherAsset.rewardID fallback).
        public ushort RodFallbackRewardId;
        private readonly List<FishSpawn> _rewardTable = new List<FishSpawn>();
        private int _rewardWeightTotal;

        public EFishingState State { get; private set; } = EFishingState.Idle;
        public float StrengthMultiplier { get; private set; }   // 0..1, captured at release; drives cast distance + bite interval

        private readonly Random _rng;
        private uint _strengthTime;
        private float _serverTimeUntilFishAppears;
        private bool _biteActive;                 // server has sent the fish notification; the catch window is live
        private float _timeSinceFishNotification = 999f;

        public FishingSim(int seed = 12345) { _rng = new Random(seed); }

        // The catch window: press primary while this is open to land the fish (UseableFisher.startPrimary /
        // UpdateBobber tug window). Opens WARNING_DURATION after the bite, stays open for CATCH_WINDOW.
        public bool IsBiteWindowOpen => _biteActive
            && _timeSinceFishNotification >= WARNING_DURATION
            && _timeSinceFishNotification <= WARNING_DURATION + CATCH_WINDOW;

        public bool HasBite => _biteActive;

        public void SetRewardTable(IEnumerable<FishSpawn> table)
        {
            _rewardTable.Clear();
            _rewardWeightTotal = 0;
            if (table != null)
                foreach (var f in table)
                    if (f.Weight > 0) { _rewardTable.Add(f); _rewardWeightTotal += f.Weight; }
        }

        public void Equip()
        {
            State = EFishingState.Idle;
            _strengthTime = 0;
            StrengthMultiplier = 0f;
            _biteActive = false;
            _timeSinceFishNotification = 999f;
        }

        // Primary pressed (LMB). In Idle -> start the strength gauge. In LineDeployed -> attempt the catch:
        // a press inside the window lands the fish; a press outside just reels the empty line back in.
        public FishingCatch Press()
        {
            if (State == EFishingState.Idle)
            {
                State = EFishingState.PreparingToCast;
                _strengthTime = 0;
                StrengthMultiplier = 0f;
                return FishingCatch.None;
            }
            if (State == EFishingState.LineDeployed)
            {
                if (IsBiteWindowOpen)
                {
                    var caught = ResolveCatch();
                    ReelToIdle();
                    return caught;
                }
                ReelToIdle();   // reeled in too early / after the fish left -> nothing
                return FishingCatch.None;
            }
            return FishingCatch.None;
        }

        // Primary released (LMB up). In PreparingToCast -> lock in the strength and cast; the bobber is now in
        // flight until the game layer confirms it reached water (ConfirmBobberInWater). Retail spawns the bobber
        // at 45% of the Cast anim with force Lerp(500,1000,strength) along aim.
        public void Release()
        {
            if (State == EFishingState.PreparingToCast)
                State = EFishingState.Casting;
        }

        // The game layer calls this once the bobber rigidbody has settled below a water surface
        // (UseableFisher.UpdateBobber -> ReceiveBobberInWaterConfirmation). Starts the server bite timer.
        public void ConfirmBobberInWater()
        {
            if (State == EFishingState.Casting)
            {
                State = EFishingState.LineDeployed;
                ResetTimeUntilFishAppears();
            }
        }

        // 50 Hz tock: advance the cast-strength gauge oscillation (UseableFisher.tock, PreparingToCast branch).
        // period grows with fishing skill (100 + level*20 tocks) so higher skill = a slower, easier-to-time bar.
        public void Tock()
        {
            if (State != EFishingState.PreparingToCast) return;
            _strengthTime++;
            uint period = 100u + (uint)FishingSkillLevel * 20u;
            float m = 1f - MathF.Abs(MathF.Sin(((_strengthTime + period / 2) % period) / (float)period * MathF.PI));
            StrengthMultiplier = m * m;
        }

        // Per-frame server update (UseableFisher.simulate): once the line's in water, count down to a bite; after
        // the bite, if the window is blown for MISSED_BITE_TIMEOUT the fish leaves and a new interval is rolled.
        public void Simulate(float dt)
        {
            _timeSinceFishNotification += dt;
            if (State != EFishingState.LineDeployed) return;

            if (!_biteActive)
            {
                _serverTimeUntilFishAppears -= dt;
                if (_serverTimeUntilFishAppears <= 0f)
                {
                    _biteActive = true;
                    _timeSinceFishNotification = 0f;
                }
            }
            else if (_timeSinceFishNotification > MISSED_BITE_TIMEOUT)
            {
                ResetTimeUntilFishAppears();   // client missed their chance -> re-arm
            }
        }

        // UseableFisher.ResetTimeUntilFishAppears: random interval scaled by cast strength, rod, and weather.
        private void ResetTimeUntilFishAppears()
        {
            _biteActive = false;
            _timeSinceFishNotification = 999f;
            float t = Lerp(MinBiteInterval, MaxBiteInterval, (float)_rng.NextDouble());
            t *= Lerp(1f, MaxStrengthBiteMultiplier, StrengthMultiplier);
            t *= FishBiteIntervalMultiplier;
            t *= WeatherBiteMultiplier;
            _serverTimeUntilFishAppears = t;
        }

        // UseableFisher.GrantRewards: pick the fish from the spawn table, roll XP in [min,max].
        private FishingCatch ResolveCatch()
        {
            ushort item = RollRewardItem();
            int xp = _rng.Next(RewardExperienceMin, RewardExperienceMax + 1);
            return new FishingCatch(true, item, xp);
        }

        private ushort RollRewardItem()
        {
            if (_rewardTable.Count == 0 || _rewardWeightTotal <= 0) return RodFallbackRewardId;
            int roll = _rng.Next(_rewardWeightTotal);
            foreach (var f in _rewardTable)
            {
                roll -= f.Weight;
                if (roll < 0) return f.ItemId;
            }
            return _rewardTable[_rewardTable.Count - 1].ItemId;
        }

        private void ReelToIdle()
        {
            _biteActive = false;
            _timeSinceFishNotification = 999f;
            State = EFishingState.Idle;   // the game layer plays the Reel anim over this transition
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
