using System;

namespace SDG.Unturned
{
    /// <summary>How much a heat or cooling source is worth at a given distance (strawberry 2026-09-10: "wire
    /// heat sources, that warm the player when in radius + LOS raycast (only when in range). also a cooling
    /// source, same thing, but cools the player").
    ///
    /// Split out from the node so the curve is testable without a scene: the engine's job is "which sources
    /// are in range and can I see them", and this is "and what is each one worth".</summary>
    public static class ThermalFalloff
    {
        /// <summary>Fraction of a source's full strength at `distance`, 1 at the centre falling to 0 at
        /// `radius`, and exactly 0 beyond it.
        ///
        /// SMOOTHSTEP rather than linear. A linear falloff has a hard kink at the edge -- the last step toward
        /// a fire is worth as much as the first, and walking past the boundary snaps the number. This eases in
        /// at both ends, so a fire feels like it has a warm middle and a vague edge, and no single footstep
        /// changes your temperature visibly.
        ///
        /// Not inverse-square, deliberately: a real point source is unusably peaky (half the radius is worth a
        /// quarter, and the last metre is worth everything), and a campfire the player has to stand exactly on
        /// top of is worse than one with an honest, readable warm zone.</summary>
        public static float Strength(float distance, float radius)
        {
            if (radius <= 0f) return 0f;
            if (distance <= 0f) return 1f;
            if (distance >= radius) return 0f;
            float t = 1f - distance / radius;      // 1 at the centre, 0 at the rim
            return t * t * (3f - 2f * t);          // smoothstep
        }

        /// <summary>What one source contributes, in degrees C. Sign is carried by `deltaC` -- a positive
        /// source warms, a negative one cools, and the same curve serves both so a cooler cannot accidentally
        /// behave differently from a fire at the same range.</summary>
        public static float Contribution(float deltaC, float distance, float radius)
            => deltaC * Strength(distance, radius);

        /// <summary>Combine several sources. A plain sum, then clamped, because two fires ARE warmer than one
        /// but a hundred of them must not be a hundred times warmer -- an unclamped stack is how a player
        /// discovers they can cook themselves to death by tidying their campfires into a pile, or survive a
        /// blizzard behind a wall of them.</summary>
        public const float MaxCombinedC = 40f;

        public static float Clamp(float summedC)
            => summedC > MaxCombinedC ? MaxCombinedC
             : summedC < -MaxCombinedC ? -MaxCombinedC
             : summedC;
    }
}
