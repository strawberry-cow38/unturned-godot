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
            // PUDDLE LEVEL: how much standing water is lying about, 0..1. Deliberately NOT rain_wetness -- puddles take
            // minutes to fill and longer to dry, so they lag the rain instead of tracking it (master 2026-09-06: "puddles
            // should hang around for a while after the rain, and take a little bit of raining before they gradually fade
            // in, im talking minutes"). WeatherManager integrates it.
            RenderingServer.GlobalShaderParameterAdd("rain_puddle", RenderingServer.GlobalShaderParameterType.Float, 0f);
            // RAIN SLANT: ground-plane metres of drift per metre of fall, pointing downwind (so its LENGTH is
            // tan of the tilt from vertical). rain_impacts.gdshaderinc leans the splashback crown along it, and
            // PushWindDrift below sets it from the same drift/fall numbers it puts in the particle gravity --
            // one source, so the splash cannot lean a different way from the streak that made it.
            RenderingServer.GlobalShaderParameterAdd("rain_slant", RenderingServer.GlobalShaderParameterType.Vec2, Vector2.Zero);
            RenderingServer.GlobalShaderParameterAdd("rain_canopy", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(0f, 0f, 1f, 0f));   // xy=canopy XZ, z=radius, w=strength (0=none): the local rain shadow under trees
            // DAYLIGHT, 0..1 (master 2026-09-08: "the raindrops look oddly 'lit' at night"). The streaks render
            // `unshaded` -- deliberately, they are thin alpha threads and real shading on them is neither cheap nor
            // convincing -- which means nothing about the time of day reaches them and a midnight drop was exactly
            // as bright as a noon one. This carries the day factor to the streak shader so it can dim them itself.
            // Defaults to 1 so anything that renders rain without a DayNightCycle looks the way it always did.
            RenderingServer.GlobalShaderParameterAdd("rain_daylight", RenderingServer.GlobalShaderParameterType.Float, 1f);
            // ROOF MAP (RainRoofMap): the topmost-surface heightmap around the player; rect.z = 0 means "no map" (every shader skips)
            var blank = Image.CreateEmpty(1, 1, false, Image.Format.Rf); blank.Fill(new Color(RainRoofMap.NoHit, 0f, 0f, 1f));   // nothing above anything
            RenderingServer.GlobalShaderParameterAdd("rain_roof", RenderingServer.GlobalShaderParameterType.Sampler2D, Variant.From(ImageTexture.CreateFromImage(blank)));
            RenderingServer.GlobalShaderParameterAdd("rain_roof_rect", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
            // SEA LEVEL: a drop that has reached the water has landed -- it must not carry on falling through it
            // (master 2026-09-07: "the water level should kill raindrops falling below it, so they dont fall
            // underwater"). Same idea as the roof map, one plane instead of a heightfield. NoSea is far below any
            // real terrain, so a map with no water (Yukon's seaLevel = 1.0) kills nothing.
            RenderingServer.GlobalShaderParameterAdd("rain_sea_level", RenderingServer.GlobalShaderParameterType.Float, NoSea);
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
            RenderingServer.GlobalShaderParameterSet("rain_puddle", 0f);
            RenderingServer.GlobalShaderParameterSet("rain_canopy", new Vector4(0f, 0f, 1f, 0f));
            RenderingServer.GlobalShaderParameterSet("rain_sea_level", NoSea);
        }

        public const float NoSea = -100000f;   // "this map has no water": below every drop, so the sea test never fires
        float _lastSea = float.NaN;

        /// <summary>Push the water plane's world Y to the rain shader, so drops stop AT the surface instead of
        /// continuing underwater. Cheap and idempotent -- it only writes when the value actually moves (a fresh
        /// StringName per literal every frame is the allocation tinyclaw caught in the intensity push).</summary>
        void PushSeaLevel()
        {
            float sea = Terrain.HasWater ? Terrain.SeaLevelY : NoSea;
            if (sea == _lastSea) return;
            _lastSea = sea;
            RenderingServer.GlobalShaderParameterSet("rain_sea_level", sea);
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
                Gravity = new Vector3(BaseDrift, -22f, 0f),   // sideways drift; driven by the weather's wind in HubProcess
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

        // THE RAIN LEANS WITH THE WEATHER'S WIND (strawberry 2026-09-08: "varying degrees of wind"). The sideways
        // gravity used to be a hardcoded +X 5 -- so a squall bent the trees and the flags while the rain itself
        // fell at exactly the same angle as a drizzle, which is the one place the wind is unmissable. Now the
        // horizontal component scales with WindField.WeatherWind and points the way the wind is actually blowing,
        // so a gale drives the rain across and a still downpour falls near-vertically. ParticleFlagAlignY already
        // turns each streak to its velocity, so tilting gravity tilts the streaks for free.
        const float BaseDrift = 5f, GaleDrift = 26f;   // horizontal gravity at zero weather-wind, and at full
        Vector2 _slantPushed = new Vector2(float.NaN, float.NaN);   // NaN so the first push always fires

        /// <summary>How hard the splashback leans, and WHICH WAY, relative to the rain's own drift.
        ///
        /// NEGATIVE: the crown leans INTO the rain, not with it. I shipped it the other way -- a drop moving
        /// downwind throws its water downwind, which is what the arithmetic says -- and master looked at it and
        /// said "they splash against the rain". They are right and the derivation was the wrong model: the part
        /// of a slanted impact you SEE standing up is the upwind wall of the crown, thrown back against the
        /// drop's travel, the way a wave breaks back off a beach. Sign from the screen, not from the algebra.
        ///
        /// 0.35 because the raw drift/fall ratio put a gale at about 50 degrees off vertical -- master: "they
        /// come in at wayy too steep of an angle". This keeps a still downpour near-upright and a gale at
        /// roughly 20. UG_SPLASHLEAN retunes it live; it is a look, and a look wants a knob.</summary>
        public static readonly float SplashLean =
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_SPLASHLEAN"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out float _sl) ? _sl : -0.35f;
        void PushWindDrift()
        {
            float w = Mathf.Clamp(WindField.WeatherWind, 0f, 1f);
            float mag = Mathf.Lerp(BaseDrift, GaleDrift, w);
            Vector2 dir = _p.GlobalPosition == Vector3.Zero ? new Vector2(1f, 0f) : WindField.WindXZ(_p.GlobalPosition);
            var g = new Vector3(dir.X * mag, -22f, dir.Y * mag);
            if (!_p.Gravity.IsEqualApprox(g)) _p.Gravity = g;   // skip the setter churn when nothing moved
            // ...and the SAME lean, as a ratio, for the splashback crown on the ground. Derived from g rather
            // than recomputed so a future change to the drift can only move both together.
            var slant = new Vector2(g.X, g.Z) / Mathf.Max(1f, Mathf.Abs(g.Y)) * SplashLean;
            if (!_slantPushed.IsEqualApprox(slant)) { _slantPushed = slant; RenderingServer.GlobalShaderParameterSet("rain_slant", slant); }
        }

        public void HubProcess(double delta)
        {
            PushSeaLevel();   // outside the camera guard: the water plane exists whether or not the rain has a camera yet
            PushWindDrift();
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
