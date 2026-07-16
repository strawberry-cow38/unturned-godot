using System;

namespace SDG.Unturned
{
    // Pure combat math extracted from the game layer (proposal phase 3) so the source constants stay pinned by
    // L0 tests. The in-engine code (PlayerController/Deployable) applies these to whatever the physics found.

    // DamageTool.explode falloffs: zombies/vehicles/deployables take LINEAR falloff (Zombie.cs:270); the player
    // takes SQUARED falloff (Player.cs:1975). Out of radius = nothing.
    public static class ExplosionMath
    {
        public static float Linear(float damage, float range, float radius) =>
            range > radius ? 0f : damage * (1f - range / radius);

        public static float Squared(float damage, float range, float radius)
        {
            if (range > radius) return 0f;
            float t = 1f - (range / radius) * (range / radius);
            return t > 0f ? damage * t : 0f;
        }
    }

    // PlayerLife.onLanded: landing faster than the fall-damage threshold (map default 22 m/s) deals
    // min(101, |verticalVelocity|) rounded, scaled by the whole-body clothing multiplier x the STRENGTH skill
    // (PlayerLife:2428-2430). Legs break on any hurting fall unless worn clothing prevents it (PlayerLife:2436).
    public static class FallMath
    {
        public const float DamageThreshold = 22f;   // m/s; a normal jump lands at ~7

        public static bool Hurts(float verticalVel) => verticalVel < -DamageThreshold;

        public static int Damage(float verticalVel, float armorMultiplier = 1f) =>
            !Hurts(verticalVel) ? 0
            : (int)MathF.Round(MathF.Min(101f, MathF.Abs(verticalVel) * armorMultiplier));   // RoundAndClampToByte; mirrors Godot Mathf.RoundToInt

        public static bool BreaksLegs(float verticalVel, bool preventsBoneBreak) =>
            Hurts(verticalVel) && !preventsBoneBreak;
    }

    // PlayerStance.GetStealthDetectionRadius: the radius (m) within which a zombie can sense a player, by stance --
    // standing 12, crouched 6, sprinting 20, prone 3, x1.1 while moving; driving = 48 * forward-speed%. AlertTool
    // clamps to [1, 64]. Crouch-walking (or crawling prone) is how you sneak past a horde.
    public static class StealthDetection
    {
        public const float DETECT_STAND = 12f;
        public const float DETECT_CROUCH = 6f;
        public const float DETECT_PRONE = 3f;
        public const float DETECT_SPRINT = 20f;
        public const float DETECT_MOVE = 1.1f;
        public const float DETECT_FORWARD = 48f;   // DRIVING, scaled by forward speed
        public const float MIN = 1f, MAX = 64f;

        public static float Radius(EPlayerStance stance, bool moving)
        {
            float move = moving ? DETECT_MOVE : 1f;
            float r = stance switch
            {
                EPlayerStance.SPRINT => DETECT_SPRINT * move,
                EPlayerStance.CROUCH => DETECT_CROUCH * move,
                EPlayerStance.PRONE => DETECT_PRONE * move,
                _ => DETECT_STAND * move,
            };
            return Math.Clamp(r, MIN, MAX);
        }

        public static float DrivingRadius(float forwardSpeedPct) =>
            Math.Clamp(DETECT_FORWARD * forwardSpeedPct, MIN, MAX);
    }
}
