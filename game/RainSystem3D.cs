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
        StandardMaterial3D _mat;   // streak material -- alpha faded with Intensity in _Process (CpuParticles3D has no AmountRatio)

        static bool _globalsRegistered;
        /// <summary>Register the rain_wetness + rain_intensity global shader uniforms ONCE, process-wide. MUST run
        /// before any material that reads them compiles, or that material dies (the GrassDisplacers lesson) -- so
        /// BuildTerrainMaterial, WeatherManager, and the --raintest / --terrain harnesses all funnel through here.</summary>
        public static void EnsureGlobals()
        {
            if (_globalsRegistered) return;
            _globalsRegistered = true;
            RenderingServer.GlobalShaderParameterAdd("rain_wetness", RenderingServer.GlobalShaderParameterType.Float, 0f);
            RenderingServer.GlobalShaderParameterAdd("rain_intensity", RenderingServer.GlobalShaderParameterType.Float, 0f);
        }

        public override void _Ready()
        {
            var quad = new QuadMesh { Size = new Vector2(0.014f, 0.62f) };   // a thin, tall streak
            _mat = new StandardMaterial3D
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
            quad.Material = _mat;
            _p = new CpuParticles3D
            {
                Mesh = quad,
                Amount = 6500,   // fixed pool; AmountRatio in _Process scales the LIVE count with Intensity (0..1, no restart)
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
            float i = Mathf.Clamp(Intensity, 0f, 1f);
            if (_mat != null) _mat.AlbedoColor = new Color(0.80f, 0.86f, 0.96f, 0.14f * i);   // fade the streaks with the rain intensity
            if (_p != null) { bool on = i > 0.02f; if (_p.Emitting != on) _p.Emitting = on; }   // stop simulating when clear
        }
    }
}
