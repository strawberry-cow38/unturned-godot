using Godot;

namespace UnturnedGodot
{
    // Worldspace 3D rain: a CPU-particle streak field that follows the camera and falls in WORLD space, so drops are
    // occluded by geometry and have real perspective/parallax (master 2026-08-29 "do worldspace rain"). It replaces
    // the old screen-space RainOverlay streaks and pairs with the wet_surface splashes/wetness + the overcast fog.
    // NOTE: CpuParticles3D, not GpuParticles3D -- GPU particles do NOT render in Godot's movie-maker / offline render
    // pipeline (the same reason the original overlay was 2D); CPU particles are deterministic and render everywhere.
    public partial class RainSystem3D : Node3D
    {
        public Camera3D Cam;
        public float Intensity = 1f;
        public float TopOffset = 10f;    // emit this far above the camera so drops fall PAST it
        CpuParticles3D _p;

        public override void _Ready()
        {
            var quad = new QuadMesh { Size = new Vector2(0.014f, 0.62f) };   // a thin, tall streak
            quad.Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.80f, 0.86f, 0.96f, 0.14f),
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled,   // no billboard -> the velocity-aligned world tilt shows honestly
                BillboardKeepScale = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableReceiveShadows = true,
                DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,   // fade drops right at the lens so they don't read as fat bars
                DistanceFadeMinDistance = 1.5f,
                DistanceFadeMaxDistance = 3.2f,
            };
            _p = new CpuParticles3D
            {
                Mesh = quad,
                Amount = Mathf.Max(250, (int)(6500f * Mathf.Clamp(Intensity, 0.12f, 1f))),   // density tracks intensity
                Lifetime = 1.4f,
                LocalCoords = false,        // fall in WORLD space, not with the camera
                Preprocess = 1.6f,          // warm up so it's already raining on frame 0
                Explosiveness = 0f,
                Randomness = 0.7f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(16f, 2f, 16f),
                Direction = new Vector3(0.12f, -1f, 0f),
                Spread = 3f,
                Gravity = new Vector3(5f, -22f, 0f),   // stronger wind drift so the lean is visible
                InitialVelocityMin = 10f, InitialVelocityMax = 14f,
                ScaleAmountMin = 0.8f, ScaleAmountMax = 1.5f,
                ParticleFlagAlignY = true,   // align each streak's Y to its VELOCITY -> leans the way it actually falls
                Emitting = true,
            };
            AddChild(_p);
        }

        public override void _Process(double delta)
        {
            if (Cam != null && IsInstanceValid(Cam)) GlobalPosition = Cam.GlobalPosition + new Vector3(0f, TopOffset, 0f);
        }
    }
}
