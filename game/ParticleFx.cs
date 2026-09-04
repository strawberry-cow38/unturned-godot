using Godot;

namespace UnturnedGodot
{
    /// <summary>Global particle tunables. SizeScale multiplies every emitter's particle size so all particles
    /// can be retuned in one place (master 2026-08-29: "scale every particle everywhere to 25% their current size").</summary>
    public static class ParticleFx
    {
        public static float QualityMul = 1f;   // GraphicsOptions.EffectQuality (retail EffectQuality): scales count + size at emitter creation
        public static float SizeScale => 0.25f * QualityMul;
        public static float AmountScale => 0.25f * QualityMul;   // master 2026-08-29: reduce particle COUNT to 25% too (density, separate from size)
        /// <summary>Scale an emitter's particle count by AmountScale, never below 1.</summary>
        public static int Amount(int n) => Mathf.Max(1, Mathf.RoundToInt(n * AmountScale));
    }
}
