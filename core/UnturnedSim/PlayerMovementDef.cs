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
        public const float SPEED_STAND = 4.5f;   // base walk/run
        public const float SPEED_CROUCH = 2.5f;
        public const float SPEED_PRONE = 1.5f;

        public const float JUMP = 7.0f;                  // PlayerMovement.cs:59
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
