using System;

namespace UnturnedSim
{
    /// <summary>How far a sound carries to an AI listener when it is raining. bitvox: "approve the masking
    /// but make sure it is pretty limited, not a free pass."
    ///
    /// SUBTRACTS A DISTANCE, IT DOES NOT SCALE ONE, and that is the whole design. The obvious version --
    /// multiply every radius by something like 0.6 in a storm -- takes a gunshot from 48 m to 29 m, which is
    /// the free pass. It is also wrong about what rain does: rain raises the NOISE FLOOR, and a raised floor
    /// drowns quiet sounds far more than loud ones. Subtracting a fixed distance reproduces that for free --
    /// six metres is most of a footstep and almost none of an explosion.
    ///
    /// The cap is what keeps it honest at the quiet end, where a plain subtraction would erase a sneaking
    /// player entirely (2 m - 6 m is negative). With both terms, heavy rain buys roughly a quarter off
    /// moving and about a tenth off shooting: a stealth window, not an invisibility cloak.
    ///
    /// Engine-free and pure so the numbers can be argued with in a test rather than in the game.</summary>
    public static class NoiseMasking
    {
        /// <summary>Metres of carry the loudest possible storm removes, before the cap applies.</summary>
        public const float MaskMetres = 6f;

        /// <summary>The least a sound may retain AT A FULL STORM; the cap eases in with intensity, so
        /// lighter rain caps higher. 0.75 = "the worst storm can never take more than a quarter".
        /// This is the knob to turn if it plays too strong or too weak; MaskMetres shapes WHO benefits,
        /// this shapes HOW MUCH anyone does.</summary>
        public const float MinKeep = 0.75f;

        /// <summary>Carry radius after rain masking.
        ///
        /// <paramref name="rint"/> is the renderer's rain scalar (BlendAlpha * Severity), 0 dry .. 1 storm,
        /// so the masking ramps with the weather fade instead of switching on -- and a silent emission stays
        /// silent rather than becoming a tiny audible one.</summary>
        public static float Carry(float loudness, float rint)
        {
            if (loudness <= 0f) return 0f;                  // silent stays silent (suppressed shot, standing still)
            if (rint <= 0f) return loudness;                // dry: untouched, so no weather means no behaviour change
            float r = rint > 1f ? 1f : rint;

            // THE CAP SCALES WITH THE STORM. A fixed floor looks right until you notice it BINDS for every
            // quiet sound at any real intensity -- a 10 m footstep hits a flat 25% cap once rint passes 0.42,
            // so Default Rain (which tops out at 0.7) and Heavy Rain masked footsteps identically. The tiers
            // are supposed to feel different, and the thing players spend most of their time doing is moving.
            // Found by a test comparing the two tiers coming back 7.5 vs 7.5.
            float keep = 1f - (1f - MinKeep) * r;
            return Math.Max(loudness - MaskMetres * r, loudness * keep);
        }
    }
}
