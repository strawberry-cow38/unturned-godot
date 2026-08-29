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
        float _lastAlphaI = -1f;   // last intensity written to the material alpha -- skip the per-frame AlbedoColor churn when unchanged

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

        /// <summary>Zero the rain globals. They're process-wide and OUTLIVE a scene change (the Add is Nil-guarded
        /// precisely so), so a scene left mid-storm would leave every wet_surface/terrain shader reading that last
        /// wetness in whatever loads next -- and the menu has no WeatherManager to drive it back down (tinyclaw's
        /// catch). Called from ResourceCaches.ClearAll (the scene-transition hook) + WeatherManager._ExitTree.</summary>
        public static void ResetGlobals()
        {
            if (!_globalsRegistered) return;   // never registered -> nothing to reset (and Set on a missing global warns)
            RenderingServer.GlobalShaderParameterSet("rain_wetness", 0f);
            RenderingServer.GlobalShaderParameterSet("rain_intensity", 0f);
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
                Amount = 6500,   // FIXED pool -- CpuParticles3D has NO AmountRatio, so _Process fades the material ALPHA with Intensity, not the count. Constant per-frame cost while raining: a deliberate trade for a gap-free intensity blend (resizing the pool at runtime restarts the emitter and pops the rain). (tinyclaw flagged the old comment's AmountRatio claim as false.)
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
            if (_mat != null && i != _lastAlphaI) { _lastAlphaI = i; _mat.AlbedoColor = new Color(0.80f, 0.86f, 0.96f, 0.14f * i); }   // fade the streaks with the rain intensity (only rewrite on change)
            if (_p != null) { bool on = i > 0.02f; if (_p.Emitting != on) _p.Emitting = on; }   // stop simulating when clear
        }
    }
}
