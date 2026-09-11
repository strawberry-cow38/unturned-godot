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

        // ---- GPU emitters -------------------------------------------------------------------------------
        //
        // ⚠ WHY THIS EXISTS. 25 sites across 15 files build CpuParticles3D, which simulates on the MAIN THREAD.
        // The reason was real and is now stale: RainSystem3D.cs:8 recorded that GPU particles do not render in
        // godot's movie-maker/offline pipeline, which is how every capture and every visual golden is produced.
        // Re-tested on 4.6.2 (2026-09-11, UG_PARTTEST): a GPU emitter and a CPU emitter side by side BOTH render
        // under --shot and under --write-movie. The limitation is gone, so the cost has no remaining reason.
        //
        // The two APIs are NOT interchangeable, which is why this is a factory and not a find-and-replace:
        // CpuParticles3D carries Direction/Spread/Gravity/InitialVelocity/Scale/Angle/Damping ON THE NODE, and
        // GpuParticles3D wants every one of them on a ParticleProcessMaterial. Translating in one place keeps
        // the 25 call sites reading the way they do now -- each of them carries hard-won tuning and comments
        // about past bugs, and rewriting them by hand 25 times is 25 chances to drop one.
        //
        // master 2026-09-11: "we can have millions of particles on screen with no hit" / "it scales and its a
        // easy toggle right now". Determinism is deliberately NOT preserved -- master: "particles in their
        // nature shouldnt be deterministic ... repeatable PARTICLES are not really high priority".

        /// <summary>One emitter's worth of settings, named as CpuParticles3D names them so a migrated call site
        /// reads like the one it replaced. Everything is optional; the defaults are godot's.</summary>
        public sealed class Spec
        {
            public int Amount = 8;
            public float Lifetime = 1f;
            public bool OneShot, Emitting;
            public float Explosiveness, Randomness;
            public float Preprocess;

            public Vector3 Direction = Vector3.Up;
            public float Spread = 45f;
            public float InitialVelocityMin, InitialVelocityMax;
            public Vector3 Gravity = new(0f, -9.8f, 0f);
            public float ScaleAmountMin = 1f, ScaleAmountMax = 1f;
            public float AngleMin, AngleMax;
            public float AngularVelocityMin, AngularVelocityMax;
            public float DampingMin, DampingMax;
            /// <summary>Sprite-sheet frame pick. AnimSpeed 0 with a random AnimOffset makes each particle HOLD
            /// one static frame for its whole life -- ImpactFx depends on that: with speed>0 the anim advanced
            /// over the lifetime and clamped onto the blank past-the-end frame, so chips sat invisible.</summary>
            public float AnimOffsetMin, AnimOffsetMax, AnimSpeedMin, AnimSpeedMax;

            public Mesh Mesh;
            public Material MaterialOverride;
            /// <summary>A FIXED cull box. Fast particles leave an auto-AABB within a frame and godot culls the
            /// whole system mid-flight -- the "flicker + derender" bug ImpactFx.Guard exists for. Carried across
            /// verbatim: GPU emitters have exactly the same auto-AABB behaviour.</summary>
            public Aabb? VisibilityAabb;
            public GeometryInstance3D.ShadowCastingSetting CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            /// <summary>⚠ 0 = simulate at the FRAME RATE, which is what CpuParticles3D does and what every
            /// migrated call site was tuned against. GpuParticles3D defaults this to 30, so at 500 fps a burst
            /// gets a sixteenth of the simulation steps and its particles have travelled far less by any given
            /// instant -- which is exactly the "GPU bursts are tighter" difference the ImpactFx before/after
            /// showed. Left settable because a slow ambient emitter genuinely does not need frame-rate steps.</summary>
            public int FixedFps;
        }

        /// <summary>Build a GPU emitter from a Spec. Nothing is emitted until Emitting is set -- several call
        /// sites deliberately arm AFTER positioning, because a one-shot armed in the constructor can burn its
        /// cycle inside a physics tick before the first _process and fire empty (see ImpactFx).</summary>
        public static GpuParticles3D Emitter(Spec s)
        {
            var mat = new ParticleProcessMaterial
            {
                Direction = s.Direction,
                Spread = s.Spread,
                InitialVelocityMin = s.InitialVelocityMin,
                InitialVelocityMax = s.InitialVelocityMax,
                Gravity = s.Gravity,
                ScaleMin = s.ScaleAmountMin,
                ScaleMax = s.ScaleAmountMax,
                AngleMin = s.AngleMin,
                AngleMax = s.AngleMax,
                AngularVelocityMin = s.AngularVelocityMin,
                AngularVelocityMax = s.AngularVelocityMax,
                DampingMin = s.DampingMin,
                DampingMax = s.DampingMax,
                AnimOffsetMin = s.AnimOffsetMin,
                AnimOffsetMax = s.AnimOffsetMax,
                AnimSpeedMin = s.AnimSpeedMin,
                AnimSpeedMax = s.AnimSpeedMax,
            };
            var p = new GpuParticles3D
            {
                Amount = Mathf.Max(1, s.Amount),
                Lifetime = s.Lifetime,
                OneShot = s.OneShot,
                Explosiveness = s.Explosiveness,
                Randomness = s.Randomness,
                Preprocess = s.Preprocess,
                ProcessMaterial = mat,
                DrawPass1 = s.Mesh,
                MaterialOverride = s.MaterialOverride,
                CastShadow = s.CastShadow,
                FixedFps = s.FixedFps,
                Emitting = s.Emitting,
            };
            if (s.VisibilityAabb.HasValue) p.VisibilityAabb = s.VisibilityAabb.Value;
            return p;
        }
    }
}
