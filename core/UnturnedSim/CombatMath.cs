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

        /// <summary>ON-FOOT detection scale (strawberry 2026-09-17: "reduce em by a lot"). The four DETECT_*
        /// stance values above are RETAIL's, ported verbatim, and they stay that way -- the divergence is this one
        /// factor rather than four rewritten constants, so the source numbers remain readable and there is exactly
        /// one knob to turn if 0.4 is wrong.
        ///
        /// Gives stand 4.8 m, crouch 2.4, prone 1.2, sprint 8.0 (x1.1 while moving).
        ///
        /// ⚠ NOT applied to DrivingRadius. "Reduce em" followed a conversation about FOOTSTEPS, and a car is not
        /// sneaking -- quietening the engine to a fifth of a garden would be a change nobody asked for, hidden
        /// inside one they did.
        ///
        /// ⚠⚠ This pushes PRONE under PlayerController's `loud > 2f` emit floor, so crawling now makes NO noise at
        /// all rather than a little. That reads as the intent of "a lot" -- crawling should be how you get past
        /// something -- but it is a threshold interaction rather than a scaling, so it is called out rather than
        /// discovered later. Crouching stands at 2.4 and still just clears the floor.</summary>
        public const float DETECT_SCALE = 0.4f;

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
            r *= DETECT_SCALE;
            // ⚠ Clamp AFTER the scale, and MIN is 1 m: without care every quiet stance would pile onto the floor
            // and crouch, prone and standing-still would all detect at exactly the same distance -- the scale
            // would look applied and change nothing about how they RANK, which is the whole point of it.
            return Math.Clamp(r, MIN, MAX);
        }

        public static float DrivingRadius(float forwardSpeedPct) =>
            Math.Clamp(DETECT_FORWARD * forwardSpeedPct, MIN, MAX);
    }
}
