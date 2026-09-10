using System;

namespace SDG.Unturned
{
    /// <summary>WHAT THE PLAYER ACTUALLY FEELS, in degrees C, and how fast they get there (strawberry
    /// 2026-09-10: "wire a separate player temperature; affected by clothing, activity ... wetness ... which
    /// affects their response to the world temperature").
    ///
    /// Separate from WorldTemperature on purpose: the world has one ambient, and two players standing in it
    /// with different coats, different exertion and different degrees of soaked are having different days.
    /// This is the half that turns one number into theirs.
    ///
    /// Engine-free. Everything that needs a raycast or a node -- the LOS test on a campfire, whether the sky
    /// is overhead -- is resolved by the caller and arrives here as a scalar or a bool.</summary>
    public sealed class PlayerTemperatureSim
    {
        // The comfortable band. Outside it things start costing you; see Band and the vitals effects.
        public const float ComfortLowC = 10f;
        public const float ComfortHighC = 28f;
        public const float FreezingBelowC = -5f;
        public const float BoilingAboveC = 40f;

        /// <summary>Hard work raises your own temperature. Sprinting in a parka in summer should be a mistake,
        /// and this is the term that makes it one.</summary>
        public const float ExertionC = 6f;

        /// <summary>Soaked through reads this much colder. It applies at ANY ambient, not just a cold one,
        /// because evaporative cooling is real in both directions -- being wet in a heatwave genuinely helps,
        /// and a player who works that out has found something true rather than a bug.</summary>
        public const float SoakedChillC = 10f;

        /// <summary>Seconds to close ~63% of the gap to the target. Slow enough that stepping indoors is
        /// relief rather than a reset, fast enough that a fire is worth walking to.</summary>
        public const float BodyTimeConstantSeconds = 45f;

        /// <summary>Seconds to go from dry to soaked in the open, and from soaked to dry under cover.
        /// Drying is slower than soaking, which is the way round that makes shelter worth planning for.</summary>
        public const float SoakSeconds = 40f;
        public const float DrySeconds = 120f;

        public enum Band { Freezing, Cold, Comfortable, Hot, Boiling }

        /// <summary>Perceived temperature. Starts mid-comfort rather than at 0 so a fresh spawn is not
        /// instantly hypothermic while the sim eases toward the real ambient.</summary>
        public float BodyC = (ComfortLowC + ComfortHighC) * 0.5f;

        /// <summary>0 = dry, 1 = soaked.</summary>
        public float Wetness;

        public Band CurrentBand => BandFor(BodyC);

        public static Band BandFor(float c)
            => c < FreezingBelowC ? Band.Freezing
             : c < ComfortLowC ? Band.Cold
             : c <= ComfortHighC ? Band.Comfortable
             : c <= BoilingAboveC ? Band.Hot
             : Band.Boiling;

        /// <summary>The temperature this player is heading toward, before the body's lag.
        ///
        /// ORDER MATTERS and is deliberate: sources and exertion and wetness all move the raw figure first,
        /// and clothing is applied LAST, against the result. Insulation that ran before them would be
        /// protecting you from a temperature you are not actually experiencing -- a coat cannot help with heat
        /// you are generating inside it.</summary>
        public static float TargetC(float ambientC, float sourceC, bool exerting, float wetness,
                                    float insulationColdC, float insulationHeatC)
        {
            if (wetness < 0f) wetness = 0f; else if (wetness > 1f) wetness = 1f;
            float t = ambientC + sourceC;
            if (exerting) t += ExertionC;
            t -= wetness * SoakedChillC;

            // Clothing closes the gap toward comfort and NEVER overshoots past it: a parka in a blizzard can
            // make you comfortable, never warm. Capping at the band edge is what stops stacked insulation
            // turning -30 C into a sauna, which a plain additive bonus would.
            if (t < ComfortLowC) t += MathF.Min(MathF.Max(insulationColdC, 0f), ComfortLowC - t);
            else if (t > ComfortHighC) t -= MathF.Min(MathF.Max(insulationHeatC, 0f), t - ComfortHighC);
            return t;
        }

        /// <summary>`sheltered` = something solid overhead (RainShelter.IsSheltered). `waterproof` = any worn
        /// piece carries Proof_Water, which already exists on ClothingDef and was doing nothing until now.</summary>
        public void Step(float ambientC, float sourceC, bool exerting, bool raining, bool sheltered,
                         bool waterproof, float insulationColdC, float insulationHeatC, float dt)
        {
            if (dt <= 0f) return;

            bool gettingWet = raining && !sheltered && !waterproof;
            float wetRate = dt / (gettingWet ? SoakSeconds : DrySeconds);
            Wetness = gettingWet ? MathF.Min(1f, Wetness + wetRate) : MathF.Max(0f, Wetness - wetRate);

            float target = TargetC(ambientC, sourceC, exerting, Wetness, insulationColdC, insulationHeatC);
            // Exponential approach rather than a fixed step per tick, so the result does not depend on the
            // tick rate -- the same wall-clock second moves you the same distance at 50 Hz or 20.
            BodyC += (target - BodyC) * (1f - MathF.Exp(-dt / BodyTimeConstantSeconds));
        }
    }
}
