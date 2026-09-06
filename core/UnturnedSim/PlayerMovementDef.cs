namespace SDG.Unturned
{
    // Stance enum, exact order from U3-SDK EPlayerStance.cs (values matter -- they cross the wire).
    public enum EPlayerStance
    {
        CLIMB,
        SWIM,
        SPRINT,
        STAND,
        CROUCH,
        PRONE,
        DRIVING,
        SITTING,
    }

    // Faithful port of PlayerMovement.cs's locomotion constants. These are the numbers that make movement
    // feel like Unturned -- kept exact against the source so a movement-trace diff vs retail stays tight.
    public static class PlayerMovementDef
    {
        // capsule heights (PlayerMovement.cs:45-47)
        public const float HEIGHT_STAND = 2f;
        public const float HEIGHT_CROUCH = 1.2f;
        public const float HEIGHT_PRONE = 0.8f;

        // stance speeds (PlayerMovement.cs:47-52)
        public const float SPEED_CLIMB = 4.5f;
        public const float SPEED_SWIM = 3f;
        public const float SPEED_SPRINT = 7f;

        // AIR STRAFING (PlayerMovement.cs:1283-1301). Airborne horizontal velocity ACCELERATES toward the
        // desired velocity and is clamped; it is not assigned. Ours assigned it, which is why a mid-air
        // reversal was instant (measured: -7.00 -> +7.00 m/s in a single 50 Hz tick) and why a sprint jump
        // read as "super fast" -- takeoff speed was applied whole on the jump tick with no ramp.
        // Both multipliers are modeConfigData knobs that default to 1.0.
        public const float AIR_ACCELERATION = 8f;    // * desired velocity, per second
        public const float AIR_DECELERATION = 2f;    // m/s^2 bleed when already faster than desired
        public const float SPEED_STAND = 4.5f;   // base walk/run
        public const float SPEED_CROUCH = 2.5f;
        public const float SPEED_PRONE = 1.5f;

        // +15 cm on retail's 7.0 (master 2026-09-06 "boost the player jump height by like 15cm"). DERIVED,
        // not nudged: the apex that matters is the DISCRETE one this sim actually produces, not v^2/2g. The
        // closed form says 0.833 m; stepping the real loop (assign JUMP on the grounded tick, then subtract
        // GRAVITY*dt and advance each tick after) lands at 0.903 m, because the takeoff tick moves a full
        // JUMP*dt before any gravity is taken off. Solving that same loop for 1.053 m gives 7.582 at 50 Hz and
        // 7.585 at 60 Hz -- so this one number is right at either tick rate, to under half a centimetre.
        // The MP climb envelope is unaffected: PlayerAuthority.UpRate is 16 m/s, which this is nowhere near.
        public const float JUMP = 7.583f;                // was PlayerMovement.cs:59's 7.0
        public const float GRAVITY = 9.81f * 3f;         // Physics.gravity.y (-9.81) applied *3, PlayerMovement.cs:1277
        public const float TERMINAL_VELOCITY = -100.0f;  // minVerticalVelocity, PlayerMovement.cs:1280

        public static float SpeedForStance(EPlayerStance stance)
        {
            switch (stance)
            {
                case EPlayerStance.SPRINT: return SPEED_SPRINT;
                case EPlayerStance.CROUCH: return SPEED_CROUCH;
                case EPlayerStance.PRONE:  return SPEED_PRONE;
                case EPlayerStance.SWIM:   return SPEED_SWIM;
                case EPlayerStance.CLIMB:  return SPEED_CLIMB;
                default:                   return SPEED_STAND;
            }
        }

        /// <summary>Eye height above the feet, per stance. Lives here rather than in the shell because the
        /// SERVER asks the same question when it decides whether a player's head is under water, and two
        /// copies of these numbers would drown people on one machine and not the other.</summary>
        public static float EyeHeightForStance(EPlayerStance stance)
        {
            switch (stance)
            {
                case EPlayerStance.CROUCH: return 1.2f;
                case EPlayerStance.PRONE:  return 0.35f;
                default:                   return 1.75f;
            }
        }

        public static float HeightForStance(EPlayerStance stance)
        {
            switch (stance)
            {
                case EPlayerStance.CROUCH: return HEIGHT_CROUCH;
                case EPlayerStance.PRONE:  return HEIGHT_PRONE;
                default:                   return HEIGHT_STAND;
            }
        }
    }
}
