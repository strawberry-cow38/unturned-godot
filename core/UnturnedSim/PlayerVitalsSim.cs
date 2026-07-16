using System;

namespace SDG.Unturned
{
    // The survival-vitals stepping extracted VERBATIM from PlayerController.UpdateVitals (MP_PLAN §3.4:
    // vitals belong to the player sim-core -- server-authoritative per player, owner-only on the wire).
    // The mechanism is source-accurate (PlayerLife: stamina burns while sprinting + regens otherwise;
    // health regenerates only while fed AND hydrated; you take damage when food or water bottoms out);
    // the RATES are the same stand-ins the controller carried. Skill multipliers arrive as plain floats
    // so the core stays engine- and game-layer-free (PlayerSkills lives in game/).
    public sealed class PlayerVitalsSim
    {
        public float Health = 100f;
        public float MaxHealth = 100f;
        // survival vitals (0..1)
        public float Stamina = 1f, Food = 1f, Water = 1f;
        public float Infection;            // 0..1 virus
        public float StaminaRegenDelay;    // seconds to wait after releasing sprint before stamina regenerates

        public struct Multipliers
        {
            public float ExerciseStaminaDrain;   // EXERCISE slows the drain
            public float CardioStaminaRegen;     // CARDIO speeds the regen
            public float SurvivalDrain;          // SURVIVAL slows hunger/thirst
            public float VitalityRegen;          // VITALITY speeds health regen

            public static Multipliers None => new Multipliers
            { ExerciseStaminaDrain = 1f, CardioStaminaRegen = 1f, SurvivalDrain = 1f, VitalityRegen = 1f };
        }

        /// <summary>One vitals step. Returns true if health reached zero THIS step -- the caller (shell or
        /// server) owns what death means (corpse, respawn, events). Callers must not step a dead player.</summary>
        public bool Step(bool sprinting, bool survivalDrain, float dt, in Multipliers m)
        {
            if (sprinting) { Stamina = MathF.Max(0f, Stamina - 0.22f * dt * m.ExerciseStaminaDrain); StaminaRegenDelay = 1f; }   // hold regen 1s after releasing sprint
            else { StaminaRegenDelay = MathF.Max(0f, StaminaRegenDelay - dt); if (StaminaRegenDelay <= 0f) Stamina = MathF.Min(1f, Stamina + 0.33f * dt * m.CardioStaminaRegen); }
            if (survivalDrain)   // hunger/thirst OFF by default (strawberry); F1 console `survival` toggles it
            {
                Food  = MathF.Max(0f, Food  - 0.0050f * dt * m.SurvivalDrain);
                Water = MathF.Max(0f, Water - 0.0070f * dt * m.SurvivalDrain);
            }
            Infection = MathF.Max(0f, Infection - 0.01f * dt);       // virus slowly clears if you stop getting bitten
            bool sick = Infection > 0.75f;                           // heavy infection makes you ill (loses health)
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick)
                Health = MathF.Min(MaxHealth, Health + 2f * dt * m.VitalityRegen);     // regen while fed + hydrated (blocked while sick)
            else if (Food <= 0f || Water <= 0f || sick)
                Health = MathF.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            return Health <= 0f;
        }
    }
}
