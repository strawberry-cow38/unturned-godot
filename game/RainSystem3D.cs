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
        ShaderMaterial _mat;   // streak material (rain_streak.gdshader) -- alpha_base faded with Intensity; canopy shadow via the rain_canopy global
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
            RenderingServer.GlobalShaderParameterAdd("rain_canopy", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(0f, 0f, 1f, 0f));   // xy=canopy XZ, z=radius, w=strength (0=none): the local rain shadow under trees
            // ROOF MAP (RainRoofMap): the topmost-surface heightmap around the player; rect.z = 0 means "no map" (every shader skips)
            var blank = Image.CreateEmpty(1, 1, false, Image.Format.Rf); blank.Fill(new Color(RainRoofMap.NoHit, 0f, 0f, 1f));   // nothing above anything
            RenderingServer.GlobalShaderParameterAdd("rain_roof", RenderingServer.GlobalShaderParameterType.Sampler2D, Variant.From(ImageTexture.CreateFromImage(blank)));
            RenderingServer.GlobalShaderParameterAdd("rain_roof_rect", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
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
            RenderingServer.GlobalShaderParameterSet("rain_canopy", new Vector4(0f, 0f, 1f, 0f));
        }

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            var quad = new QuadMesh { Size = new Vector2(0.014f, 0.62f) };   // a thin, tall streak
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/rain_streak.gdshader") };
            _mat.SetShaderParameter("tint", new Vector3(0.80f, 0.86f, 0.96f));
            _mat.SetShaderParameter("alpha_base", 0.14f);   // = 0.14 * intensity, driven in _Process; the velocity-aligned world tilt comes from ParticleFlagAlignY below, and the shader then spins the quad about that axis to face the camera (rain_streak.gdshader face_camera)
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

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        // The rain KEEPING UP with a fast camera (strawberry 2026-09-06 "when moving the 3p camera quickly, sometimes the rain
        // doesnt keep up"). The drops live in WORLD space (LocalCoords false) and the emitter box is +-16 m round the camera, so
        // the sky in front of a camera that has just whipped 20 m round the car (the 3P orbit) or is doing 30 m/s down a road is
        // sky the box has not been over for long enough to fill: empty for the ~0.7 s a drop takes to fall to eye height.
        // Two fixes, both cheap: the box LEADS the camera along its (smoothed) velocity, so at speed it is already ahead; and a
        // big displacement in a short window RESTARTS the emitter, which re-runs the 1.6 s Preprocess at the new spot and
        // fills the volume in one frame (rate-limited -- a restart re-rolls every drop, invisible mid-whip, a flicker if spammed).
        Vector3 _lastCamPos, _camVel; bool _haveLast; float _restartCd; readonly System.Collections.Generic.Queue<(float t, Vector3 p)> _trail = new(); float _t;
        const float LeadSeconds = 0.45f, MaxLead = 10f, JumpWindow = 0.25f, JumpMetres = 10f, RestartCooldown = 0.4f;

        public void HubProcess(double delta)
        {
            if (Cam != null && IsInstanceValid(Cam))
            {
                float dt = (float)delta; _t += dt; if (_restartCd > 0f) _restartCd -= dt;
                Vector3 cp = Cam.GlobalPosition;
                if (_haveLast && dt > 0f)
                {
                    Vector3 v = (cp - _lastCamPos) / dt;
                    if (v.LengthSquared() > 200f * 200f) { v = Vector3.Zero; _camVel = Vector3.Zero; }   // a teleport / map load is not a velocity
                    _camVel = _camVel.Lerp(v, Mathf.Min(1f, dt / 0.6f));                                 // EMA, 0.6 s: a whip barely moves it, a road speed settles on it
                }
                _lastCamPos = cp; _haveLast = true;
                Vector3 lead = _camVel * LeadSeconds; lead.Y = 0f;
                if (lead.Length() > MaxLead) lead = lead.Normalized() * MaxLead;
                GlobalPosition = cp + new Vector3(0f, TopOffset, 0f) + lead;
                _trail.Enqueue((_t, cp));
                while (_trail.Count > 0 && _t - _trail.Peek().t > JumpWindow) _trail.Dequeue();
                if (_p != null && _p.Emitting && _restartCd <= 0f && _trail.Count > 1 && cp.DistanceTo(_trail.Peek().p) > JumpMetres)
                { _p.Restart(); _restartCd = RestartCooldown; }   // Preprocess (1.6 s) refills the volume where the camera now is
            }
            float i = Mathf.Clamp(Intensity, 0f, 1f);
            if (_mat != null && i != _lastAlphaI) { _lastAlphaI = i; _mat.SetShaderParameter("alpha_base", 0.14f * i); }   // fade the streaks with the rain intensity (only rewrite on change)
            if (_p != null) { bool on = i > 0.02f; if (_p.Emitting != on) _p.Emitting = on; }   // stop simulating when clear
        }
    }
}
