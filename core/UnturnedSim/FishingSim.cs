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
        CatchChallenge,  // fish hooked (opt-in rods): hold/release to track it with a cursor until captured
        Reeling,         // reel animation playing (transient; game layer times it, sim returns to Idle)
    }

    // Per-fish catch-challenge tuning (ItemAsset.FishingCatchable in retail; FishingCatchableProperties). Fixed-point
    // ints (x10000) so the fish/cursor spring integration is bit-identical client<->server. Ported 1:1 with the SDK
    // defaults; the port uses Default for every fish (no per-item overrides ripped yet).
    public sealed class FishingCatchableProperties
    {
        public const int FIXED_POINT_SCALE = 10_000;   // a [0,1] position as an int
        public const int TIME_SCALE = 10_000;          // capture/escape durations scaled up for sub-tick rod multipliers

        public int minChangeTargetTicks, maxChangeTargetTicks;
        public int maxUpwardAcceleration, maxDownwardAcceleration;
        public int maxUpwardSpeed, maxDownwardSpeed;
        public int upperRestitution, lowerRestitution;
        public int minTargetDelta, maxTargetDelta;
        public int minTargetPosition, maxTargetPosition;
        public int captureTicks, escapeTicks;
        public int springStiffness, springDamping;

        // SDK defaults (FishingCatchableProperties field defaults), converted at the retail 50 Hz tock rate.
        public static FishingCatchableProperties Default => new FishingCatchableProperties
        {
            minChangeTargetTicks = 75, maxChangeTargetTicks = 100,          // 1.5s / 2.0s @ 50 Hz
            maxUpwardAcceleration = 15_000, maxDownwardAcceleration = 12_000, // 1.5 / 1.2 * FIXED
            maxUpwardSpeed = 6_000, maxDownwardSpeed = 4_500,               // 0.6 / 0.45 * FIXED
            upperRestitution = 6_000, lowerRestitution = 4_000,            // 0.6 / 0.4 * FIXED
            minTargetDelta = 3_000, maxTargetDelta = 4_000,               // 0.3 / 0.4 * FIXED
            minTargetPosition = 1_000, maxTargetPosition = 9_000,         // 0.1 / 0.9 * FIXED
            captureTicks = 100 * TIME_SCALE, escapeTicks = 100 * TIME_SCALE, // 2.0s @ 50 Hz * TIME_SCALE
            springStiffness = 16 * FIXED_POINT_SCALE, springDamping = 4 * FIXED_POINT_SCALE,
        };
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

        // --- injected from PlayerSkills each cast (affects the strength-gauge period + challenge speeds) ---
        public byte FishingSkillLevel;
        public byte FishingSkillMax = 5;   // FISHING skill max level (mastery = level/max)

        // --- catch-challenge (ItemFisherAsset); when EnableCatchChallenge, a bite opens a tracking minigame ---
        public bool EnableCatchChallenge;
        public int CatchChallengeCursorSize = 2_000;   // 0.2 * FIXED (window the fish must sit inside)
        public int CatchChallengeGravity = 10_000;     // 1.0 * FIXED (cursor falls while input released)
        public int CatchChallengeAcceleration = 10_000; // 1.0 * FIXED (cursor rises while input held)
        public int CatchChallengeUpperRestitution = 5_000, CatchChallengeLowerRestitution = 5_000;  // 0.5 * FIXED
        public float CatchChallengeCaptureSpeedMultiplier = 1f;
        public float CatchChallengeEscapeSpeedMultiplier = 1f;
        public FishingCatchableProperties Catchable = FishingCatchableProperties.Default;

        // challenge runtime (fixed-point). fish bobs on a spring toward a randomly-relocating target; the player's
        // cursor rises/falls with input; capture fills while the fish is inside the cursor (UseableFisher.tock).
        int _fishTargetPosition, _fishPosition, _fishVelocity;
        int _cursorPosition, _cursorVelocity;
        int _captureProgress, _capturePerTick, _escapePerTick;
        int _ticksUntilRelocate;
        bool _pullUp;
        FishingCatch _pendingCatch;   // set when the challenge is won inside Tock(); drained by the game layer

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
                    if (EnableCatchChallenge) { EnterChallenge(); return FishingCatch.None; }   // opt-in rods -> minigame, reward on win
                    var caught = ResolveCatch();   // challenge-disabled: press-in-window catches instantly
                    ReelToIdle();
                    return caught;
                }
                ReelToIdle();   // reeled in too early / after the fish left -> nothing
                return FishingCatch.None;
            }
            if (State == EFishingState.CatchChallenge) { _pullUp = true; return FishingCatch.None; }   // hold LMB -> pull the cursor up
            return FishingCatch.None;
        }

        // Primary released (LMB up). In PreparingToCast -> lock in the strength and cast; the bobber is now in
        // flight until the game layer confirms it reached water (ConfirmBobberInWater). Retail spawns the bobber
        // at 45% of the Cast anim with force Lerp(500,1000,strength) along aim.
        public void Release()
        {
            if (State == EFishingState.PreparingToCast)
                State = EFishingState.Casting;
            else if (State == EFishingState.CatchChallenge)
                _pullUp = false;   // release LMB -> let the cursor fall
        }

        // A challenge won inside Tock() stashes its reward here; the game layer drains it (grants fish + XP).
        public bool TryTakePendingCatch(out FishingCatch caught)
        {
            if (_pendingCatch.Success) { caught = _pendingCatch; _pendingCatch = FishingCatch.None; return true; }
            caught = FishingCatch.None; return false;
        }

        // Challenge display state (0..1), for the UI overlay + tests.
        public float ChallengeFishPos => _fishPosition / (float)FishingCatchableProperties.FIXED_POINT_SCALE;
        public float ChallengeCursorPos => _cursorPosition / (float)FishingCatchableProperties.FIXED_POINT_SCALE;
        public float ChallengeCursorSizeNorm => CatchChallengeCursorSize / (float)FishingCatchableProperties.FIXED_POINT_SCALE;
        public float ChallengeProgress => Catchable.captureTicks == 0 ? 0f : _captureProgress / (float)Catchable.captureTicks;   // -1..1
        public bool FishInCursor => _fishPosition >= _cursorPosition && _fishPosition <= _cursorPosition + CatchChallengeCursorSize;

        void EnterChallenge()
        {
            State = EFishingState.CatchChallenge;
            _biteActive = false;
            _ticksUntilRelocate = 0;
            _fishTargetPosition = _rng.Next(Catchable.minTargetPosition, Catchable.maxTargetPosition + 1);
            _fishPosition = _fishTargetPosition;
            _fishVelocity = 0;
            _captureProgress = 0;
            float mastery = FishingSkillMax == 0 ? 0f : (float)FishingSkillLevel / FishingSkillMax;
            _capturePerTick = (int)MathF.Round(FishingCatchableProperties.TIME_SCALE * (1f + mastery * 0.2f) * CatchChallengeCaptureSpeedMultiplier);
            _escapePerTick = (int)MathF.Round(FishingCatchableProperties.TIME_SCALE * (1f - mastery * 0.2f) * CatchChallengeEscapeSpeedMultiplier);
            _cursorPosition = ClampInt(_fishTargetPosition - CatchChallengeCursorSize / 2, 0, FishingCatchableProperties.FIXED_POINT_SCALE - CatchChallengeCursorSize);
            _cursorVelocity = 0;
            _pullUp = true;
        }

        // One 50 Hz step of the tracking minigame (UseableFisher.tock CatchChallenge branch): relocate the fish target,
        // spring-integrate the fish + the input cursor (fixed-point), then grow/shrink capture. Win at captureTicks
        // (reward stashed), lose at -escapeTicks (fish escapes -> back to a fresh bite).
        void ChallengeStep()
        {
            var c = Catchable;
            const int FIXED = FishingCatchableProperties.FIXED_POINT_SCALE;
            const int DELTA_TIME = 50;

            if (_ticksUntilRelocate > 0) _ticksUntilRelocate--;
            else
            {
                _ticksUntilRelocate = _rng.Next(c.minChangeTargetTicks, c.maxChangeTargetTicks);
                int delta = _rng.Next(c.minTargetDelta, c.maxTargetDelta);
                if (_fishTargetPosition + delta > c.maxTargetPosition) _fishTargetPosition = Math.Max(c.minTargetPosition, _fishTargetPosition - delta);
                else if (_fishTargetPosition - delta < c.minTargetPosition) _fishTargetPosition = Math.Min(c.maxTargetPosition, _fishTargetPosition + delta);
                else { if (_rng.NextDouble() < 0.5) delta = -delta; _fishTargetPosition += delta; }
            }

            int accel = (c.springStiffness * (_fishTargetPosition - _fishPosition)) / FIXED - (c.springDamping * _fishVelocity) / FIXED;
            accel = ClampInt(accel, -c.maxDownwardAcceleration, c.maxUpwardAcceleration);
            _fishVelocity += accel / DELTA_TIME;
            _fishVelocity = ClampInt(_fishVelocity, -c.maxDownwardSpeed, c.maxUpwardSpeed);
            _fishPosition += _fishVelocity / DELTA_TIME;
            if (_fishPosition > FIXED) { _fishPosition = FIXED - (_fishPosition - FIXED); _fishVelocity = -_fishVelocity * c.upperRestitution / FIXED; }
            else if (_fishPosition < 0) { _fishPosition = -_fishPosition; _fishVelocity = -_fishVelocity * c.lowerRestitution / FIXED; }

            if (_pullUp) _cursorVelocity += CatchChallengeAcceleration / DELTA_TIME;
            else _cursorVelocity -= CatchChallengeGravity / DELTA_TIME;
            _cursorPosition += _cursorVelocity / DELTA_TIME;
            if (_cursorPosition + CatchChallengeCursorSize > FIXED)
            {
                _cursorPosition = FIXED - CatchChallengeCursorSize - (_cursorPosition + CatchChallengeCursorSize - FIXED);
                _cursorVelocity = -_cursorVelocity * CatchChallengeUpperRestitution / FIXED;
            }
            else if (_cursorPosition < 0) { _cursorPosition = 0; _cursorVelocity = -_cursorVelocity * CatchChallengeLowerRestitution / FIXED; }

            bool within = FishInCursor;
            if (within) _captureProgress = Math.Min(Math.Max(0, _captureProgress + _capturePerTick), c.captureTicks);
            else _captureProgress = Math.Max(_captureProgress - _escapePerTick, -c.escapeTicks);

            if (_captureProgress >= c.captureTicks) { _pendingCatch = ResolveCatch(); ReelToIdle(); }        // caught!
            else if (_captureProgress <= -c.escapeTicks) { State = EFishingState.LineDeployed; ResetTimeUntilFishAppears(); }   // escaped
        }

        static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

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
            if (State == EFishingState.PreparingToCast)
            {
                _strengthTime++;
                uint period = 100u + (uint)FishingSkillLevel * 20u;
                float m = 1f - MathF.Abs(MathF.Sin(((_strengthTime + period / 2) % period) / (float)period * MathF.PI));
                StrengthMultiplier = m * m;
            }
            else if (State == EFishingState.CatchChallenge)
            {
                ChallengeStep();
            }
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
