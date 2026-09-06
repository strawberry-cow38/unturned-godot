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
        public float Oxygen = 1f;          // 0..1 breath: drains with the head under water, refills above it
        public float StaminaRegenDelay;    // seconds to wait after releasing sprint before stamina regenerates

        /// <summary>Seconds of air from a full breath, and seconds to refill it at the surface.</summary>
        public const float OxygenSeconds = 30f, OxygenRefillSeconds = 4f;
        /// <summary>HP per second once the air is gone -- ~10 s from full health to drowned.</summary>
        public const float DrownDamagePerSecond = 10f;

        /// <summary>HP this step's drowning took, separately from everything else Step did to Health.
        ///
        /// It is reported apart because the SERVER routes it apart: the food/water/regen delta is gated behind
        /// the SurvivalDrain toggle (hunger is off by default here), and drowning must not be. Folding the two
        /// together would have made a switched-off survival toggle mean you cannot drown either.</summary>
        public float LastDrownDamage;

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
            => Step(sprinting, false, survivalDrain, dt, m);

        /// <summary>submerged = the player's HEAD is under water (not merely their feet, and not merely "is
        /// swimming" -- treading water at the surface has your face in the air and must not cost you a breath).</summary>
        public bool Step(bool sprinting, bool submerged, bool survivalDrain, float dt, in Multipliers m)
        {
            LastDrownDamage = 0f;
            if (sprinting) { Stamina = MathF.Max(0f, Stamina - 0.22f * dt * m.ExerciseStaminaDrain); StaminaRegenDelay = 1f; }   // hold regen 1s after releasing sprint
            else { StaminaRegenDelay = MathF.Max(0f, StaminaRegenDelay - dt); if (StaminaRegenDelay <= 0f) Stamina = MathF.Min(1f, Stamina + 0.33f * dt * m.CardioStaminaRegen); }
            if (survivalDrain)   // hunger/thirst OFF by default (strawberry); F1 console `survival` toggles it
            {
                Food  = MathF.Max(0f, Food  - 0.0050f * dt * m.SurvivalDrain);
                Water = MathF.Max(0f, Water - 0.0070f * dt * m.SurvivalDrain);
            }
            // BREATH. Drains only with the head under, refills far faster than it empties (a surfacing player
            // gets their air back in a gulp, not over half a minute), and once it is gone the water starts
            // taking health -- a bar that empties and does nothing is not a mechanic.
            if (submerged)
            {
                Oxygen = MathF.Max(0f, Oxygen - dt / OxygenSeconds);
                if (Oxygen <= 0f)
                {
                    LastDrownDamage = MathF.Min(Health, DrownDamagePerSecond * dt);
                    Health = MathF.Max(0f, Health - LastDrownDamage);
                }
            }
            else Oxygen = MathF.Min(1f, Oxygen + dt / OxygenRefillSeconds);

            Infection = MathF.Max(0f, Infection - 0.01f * dt);       // virus slowly clears if you stop getting bitten
            bool sick = Infection > 0.75f;                           // heavy infection makes you ill (loses health)
            if (Food > 0.30f && Water > 0.30f && Health < MaxHealth && !sick && LastDrownDamage <= 0f)   // no topping yourself up while you are drowning
                Health = MathF.Min(MaxHealth, Health + 2f * dt * m.VitalityRegen);     // regen while fed + hydrated (blocked while sick)
            else if (Food <= 0f || Water <= 0f || sick)
                Health = MathF.Max(0f, Health - (sick ? 2f : 1.5f) * dt);   // starve / dehydrate / infection sickness
            return Health <= 0f;
        }
    }
}
