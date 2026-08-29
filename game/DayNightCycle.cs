using Godot;

namespace UnturnedGodot
{
    // Bounded day/night cycle -- arcs a sun DirectionalLight across the sky over DayLength seconds and lerps the sky
    // through midnight -> dawn -> noon -> dusk. The background is a FAITHFUL port of Unturned's sky shader
    // (Assets/Game/Sources/Shaders/Sky/Skybox-Sky.shader, vanilla keywords WITH_CLOUDS + WITH_STARS): the sky/equator/
    // ground gradient + the real RGB-packed Clouds.png projected onto the dome (viewDir.xz/viewDir.y, R=macro G=medium
    // B=small, scrolling) + a sun disc + the real Stars texture + a procedural moon. Colours/params ARE the extracted
    // Skybox.mat values (_SkyColor 0.636/0.720/0.801, _EquatorColor 0.801, _GroundColor 0.5, _CloudParams 0.6/10,
    // _CloudRimColor 0.8/0.6/0.4, _MoonColor 0.749/0.804/0.808, sun thresholds 0.995/0.993). The cycle drives the
    // time-of-day colours + the sun/moon directions each frame.
    public partial class DayNightCycle : Node
    {
        public DirectionalLight3D Sun;
        public Godot.Environment Env;
        /// <summary>Seconds in ONE FULL CYCLE -- midnight through noon and back, not the daylight half. 24 real
        /// minutes (strawberry), so one game hour is one real minute, which is also the unit the dev console's
        /// `dayLength &lt;minutes&gt;` command speaks in. Was a dev-short 120 s; Unturned's own is ~an hour.
        ///
        /// A CONSTANT rather than a bare default, because the default was NOT what the game ran on: WorldBuilder set
        /// its own 300f at both call sites, so editing the field here changed nothing in play while every test still
        /// passed. One number, referenced from both.</summary>
        public const float DefaultDayLength = 24f * 60f;
        public float DayLength = DefaultDayLength;   // seconds per full cycle
        public float Time = 0.35f;       // 0..1 time of day: 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk
        public float Speed = 1f;         // day/night clock multiplier (console `timeSpeed`); 0 = frozen. Distinct from Engine.TimeScale.
        // Running in-game day count, bumped once per forward midnight crossing (natural cycle OR a dev `timeAdd` that
        // laps midnight). Drives food spoilage -- a watcher ticks spoilage each time this advances. Monotonic:
        // a rewind (negative timeAdd) repositions the clock but never decrements Day (spoilage doesn't reverse).
        public int Day;

        // --- lore blackout (master): global power dies for GOOD on a random day (14..30), decided once at load, with
        // 1-2 warning brownouts each of the 2 nights before. SESSION-BASED -- no world save yet, so it re-rolls every
        // load (dateset/timeadd to test). BlackoutDay 0 = not decided.
        public int BlackoutDay;
        bool _blackoutFired;
        readonly System.Collections.Generic.List<(int day, float time)> _brownouts = new();
        readonly System.Collections.Generic.HashSet<int> _brownoutFired = new();

        // MP Phase 8 (§3.7): a net sync owns Time (server: tick-derived; client: derived from the synced
        // clock + snapshot tick) -- _Process stops free-running it. SP default (false) is byte-identical.
        public bool ExternalTime;
        // Dedicated fx hygiene (§2.1/§5): a headless server needs the CLOCK (authoritative time) but not
        // the sky shader / sun / fog / glow work every frame. Default (true) is byte-identical for SP.
        public bool VisualsEnabled = true;

        // ── PEI's REAL per-time-of-day lighting, ripped byte-exact from Maps/PEI/Environment/Lighting.dat (v12: the
        //    LightingInfo[4] table = DAWN/MIDDAY/DUSK/MIDNIGHT x ELightingColor, readColor = 3 bytes RGB /255).
        //    Arrays ordered [midnight, dawn, noon, dusk] to match Grad(). tools/parse_lighting.py dumps the full table.
        // sky-dome zenith = SKY_SKY
        static readonly Color[] SkyTop = {
            new(0.020f, 0.071f, 0.180f), new(0.878f, 0.753f, 0.584f), new(0.400f, 0.627f, 0.808f), new(0.757f, 0.188f, 0.267f),
        };
        // horizon (equator) = SKY_EQUATOR
        static readonly Color[] SkyHorizon = {
            new(0.078f, 0.071f, 0.180f), new(0.761f, 0.482f, 0.176f), new(0.784f, 0.784f, 0.784f), new(1.000f, 0.341f, 0.204f),
        };
        // below-horizon = SKY_GROUND
        static readonly Color[] Ground = {
            new(0.000f, 0.071f, 0.102f), new(0.651f, 0.251f, 0.098f), new(0.329f, 0.518f, 0.780f), new(0.216f, 0.118f, 0.141f),
        };
        // ambient = AMBIENT_SKY/EQUATOR/GROUND averaged. MIDDAY is a WARM TAN (0.74,0.63,0.47), NOT grey -- this is the
        // washout fix: Unturned's real midday ambient is warm + bright, my old flat grey (0.60,0.62,0.65) desaturated it.
        static readonly Color[] Amb = {
            new(0.098f, 0.196f, 0.294f), new(0.329f, 0.340f, 0.345f), new(0.735f, 0.625f, 0.467f), new(0.561f, 0.106f, 0.248f),
        };
        // sun light colour = SUN (midnight = black; night light comes from the moon tint below)
        static readonly Color[] SunCol = {
            new(0.000f, 0.000f, 0.000f), new(0.718f, 0.463f, 0.098f), new(0.933f, 0.863f, 0.757f), new(1.000f, 0.000f, 0.000f),
        };

        Sky _sky;
        ShaderMaterial _skyMat;

        // Faithful port of Skybox/Sky (Skybox-Sky.shader). EYEDIR == Unturned's viewDir; rayDir == -EYEDIR.
        // Unity _Time.x -> TIME/20, _Time.y -> TIME. Clouds + stars gated to above-horizon (guards the /viewDir.y).
        internal const string SkyShaderCode = @"
shader_type sky;

uniform vec3 sky_color : source_color;
uniform vec3 equator_color : source_color;
uniform vec3 ground_color : source_color;
uniform vec3 ambient_ground : source_color;      // _SkyHackAmbientGround
uniform vec3 ambient_equator : source_color;     // _SkyHackAmbientEquator
uniform vec3 sun_direction;       // direction the sunlight travels
uniform vec3 sun_color : source_color;
uniform float sun_inner;
uniform float sun_outer;
uniform sampler2D stars_tex : repeat_enable, filter_linear;
uniform float stars_cutoff;
uniform vec3 moon_direction;
uniform vec3 moon_light_direction;
uniform vec3 moon_color : source_color;
uniform float sqr_moon_radius;
uniform sampler2D clouds_tex : repeat_enable, filter_linear;
uniform vec3 cloud_rim_color : source_color;
uniform float cloud_intensity;
uniform vec4 cloud_params;        // R: macro cutoff, G: macro saturation
uniform float lightning_flash;                   // 0..1+ strike envelope (WeatherManager drives it via DayNightCycle; 0 = skip the whole block)
uniform vec3 lightning_tint : source_color;      // warm-white flash colour
uniform vec3 lightning_dir;                      // unit XZ azimuth of the strike -- one part of the sky carries the glow

void sky() {
    vec3 viewDir = EYEDIR;
    vec3 rayDir = -EYEDIR;

    // sky/equator/ground gradient (Skybox-Sky.shader frag)
    vec3 col;
    float scale = 1.0 - pow(1.0 - clamp(abs(rayDir.y), 0.0, 1.0), 4.0);
    float overHorizonMask;
    if (rayDir.y < 0.0) { col = mix(equator_color, sky_color, scale); overHorizonMask = 1.0; }
    else { col = mix(equator_color, ground_color, scale); overHorizonMask = 0.0; }

    float tX = TIME / 20.0;   // Unity _Time.x
    float tY = TIME;          // Unity _Time.y

    float sunAlignment = dot(rayDir, sun_direction);
    float sunAlpha = smoothstep(sun_outer, sun_inner, sunAlignment) * overHorizonMask;
    float sunIntensity = 4.0;

    // procedural moon: ray vs a unit-distance sphere in the moon direction
    vec3 moonCenter = -moon_direction;
    float moonCenterDistAlongView = dot(viewDir, moonCenter);
    float moonMask = step(0.0, moonCenterDistAlongView) * overHorizonMask;
    float sqrDistNearest = 1.0 - moonCenterDistAlongView * moonCenterDistAlongView;
    moonMask *= step(sqrDistNearest, sqr_moon_radius);
    float distWithinMoon = sqrt(max(0.0, sqr_moon_radius - sqrDistNearest));
    vec3 moonHitNormal = normalize(viewDir * (moonCenterDistAlongView - distWithinMoon) - moonCenter);
    float ndotl = clamp(dot(moonHitNormal, -moon_light_direction), 0.0, 1.0);

    // stars (projected on an infinite plane) -- above horizon only, obstructed by the moon
    if (viewDir.y > 0.0001) {
        vec2 starsCoord = rayDir.xz / rayDir.y;
        starsCoord.x += tX * 0.01;
        starsCoord.y += tY * 0.004;
        vec4 starsColor = texture(stars_tex, starsCoord * 0.6);
        float starsMask = clamp(-rayDir.y, 0.0, 1.0) * (1.0 - moonMask);
        col = mix(col, starsColor.rgb, max(0.0, starsColor.a - stars_cutoff) * starsMask);
    }

    col = mix(col, sun_color * sunIntensity, sunAlpha);
    col = mix(col, moon_color, moonMask * ndotl);

    // clouds: real Clouds.png projected viewDir.xz/viewDir.y, R=macro / G=medium / B=small, scrolling
    if (viewDir.y > 0.0001) {
        vec2 texcoord = viewDir.xz / viewDir.y;
        float macroAlpha = texture(clouds_tex, texcoord * 0.1 - vec2(0.0, tX * 0.01)).r;
        macroAlpha += cloud_intensity * 0.25 * texture(clouds_tex, texcoord * 0.1 + 0.5 - vec2(0.0, tX * 0.01)).r;
        macroAlpha = clamp((macroAlpha - cloud_params.r) * cloud_params.g, 0.0, 1.0);

        float sunAtmosphereFactor = clamp(sun_direction.y * -2.0 + 1.0, 0.0, 1.0);
        float sunViewFactor = clamp(0.5 - dot(viewDir, sun_direction), 0.0, 1.0);
        float sunFactor = sunAtmosphereFactor * sunViewFactor;

        float moonAtmosphereFactor = clamp(moon_direction.y * -2.0 + 1.0, 0.0, 1.0);
        float moonViewFactor = clamp(-dot(viewDir, moon_direction), 0.0, 1.0);
        float moonFactor = moonAtmosphereFactor * moonViewFactor;

        float cloudsMedium = texture(clouds_tex, texcoord * 0.2 - vec2(0.0, tX * 0.04)).g;
        float cloudsSmall = texture(clouds_tex, texcoord - vec2(0.0, tX * 0.2)).b;

        vec3 cloudBodyColor = ambient_ground + cloud_rim_color;
        cloudBodyColor = mix(cloudBodyColor, sun_color, sunFactor * cloudsMedium * 0.5);
        cloudBodyColor = mix(cloudBodyColor, moon_color, moonFactor * cloudsMedium * 0.05);

        vec3 cRimColor = ambient_equator + equator_color + cloud_rim_color;
        cRimColor = mix(cRimColor, sun_color, sunFactor);
        cRimColor = mix(cRimColor, moon_color, moonFactor * 0.25);

        // LIGHTNING (Fable's pick): light the cloud layer from WITHIN. One azimuth of the sky carries the flash,
        // brightest where the view aligns with the strike, shaped by cloud density so it reveals structure instead of
        // flat-brightening. Rim pushed harder than body -> backlit silhouette reads as 'lit from within'. Zero cost off.
        if (lightning_flash > 0.001) {
            // ⚠ zenith guard: normalize(vec3(0)) is NaN looking straight up, and the azimuth pinwheels around it.
            // Fade the directional term out as the view approaches vertical (tinyclaw) -- kills the NaN AND the pinwheel.
            vec2 vxz = vec2(viewDir.x, viewDir.z);
            float vlen = length(vxz);
            float zdamp = smoothstep(0.0, 0.15, vlen);
            vec3 vdir = vlen > 1e-5 ? vec3(vxz.x, 0.0, vxz.y) / vlen : lightning_dir;
            float dirW = pow(clamp(dot(vdir, lightning_dir), 0.0, 1.0) * 0.5 + 0.5, 3.0) * zdamp;
            float shaped = lightning_flash * dirW * macroAlpha * (1.0 - cloudsSmall * 0.4);
            cloudBodyColor += lightning_tint * shaped * 1.6;
            cRimColor += lightning_tint * shaped * 2.4;
            col += lightning_tint * lightning_flash * dirW * 0.08;   // small horizon lift so the clear sky under the clouds participates
        }

        float cloudsBodyAlpha = clamp(macroAlpha + macroAlpha * cloudsMedium + macroAlpha * cloudsSmall, 0.0, 1.0);
        float cloudsAlpha = cloudsBodyAlpha * clamp(viewDir.y * 2.0, 0.0, 1.0);
        col = mix(col, mix(cRimColor, cloudBodyColor, cloudsBodyAlpha), cloudsAlpha);
    }

    COLOR = col;
}
";

        public override void _Ready()
        {
            AddToGroup("daynight");   // so the dev console (time/timeSpeed/dayLength/date cmds) can find it
            BlackoutDay = (int)GD.RandRange(14, 31);   // [14..30] inclusive; the day the grid dies for good
            for (int d = BlackoutDay - 2; d <= BlackoutDay - 1; d++)
                for (int i = 0, n = (int)GD.RandRange(1, 3); i < n; i++)   // 1-2 brownouts that night
                    _brownouts.Add((d, 0.77f + GD.Randf() * 0.20f));       // a random EVENING-night time (streetlights lit)
        }

        public override void _Process(double delta)
        {
            using var _prof = Prof.Scope("DayNightCycle");
            if (!ExternalTime) Advance((float)delta * Speed / DayLength);   // Speed = the console timeSpeed multiplier
            if (VisualsEnabled) { Apply(); DriveStreetlights((float)delta); DriveMoteFade(); }
            DriveBlackout();   // gameplay (sets the grid flag) -> runs even headless/server, unlike the visual sweep
        }

        // Street lamps light dusk->dawn AND only while the town grid is live (auto-grid municipal consumers -- the
        // console `toggleGlobalPower` darkens the town). Edge-triggered: the group sweep runs only when night OR grid
        // flips (a handful of times per session), never per-frame -- PEI has hundreds of lamps. Dedicated skips it
        // (VisualsEnabled is false; no visual lamps there anyway).
        bool? _lampsNight, _lampsGrid;
        // A brownout is a FLICKER SIGNAL, not a real power cut (master): the lights just stutter briefly while the
        // grid stays up. This side-steps MainsLive's 0.25s poll entirely (a short physical dip fell between samples
        // and was never seen), and keeps the warning brownouts purely cosmetic -- the actual blackout below is the
        // only real power loss. Supplies/deployables could get the same pulse (follow-up).
        public void TriggerGlobalBrownout(float durationSec = 0.6f)
        {
            var tree = GetTree();
            if (tree == null) return;
            foreach (Node n in tree.GetNodesInGroup("streetlights"))
                if (n is StreetLight sl) sl.FlickerPulse(durationSec);
            foreach (Node n in tree.GetNodesInGroup("gridlights"))
                if (n is GridLight gl) gl.FlickerPulse(durationSec);   // indoor lamps ride the same brownout pulse
            foreach (Node n in tree.GetNodesInGroup("heartmonitors"))
                if (n is HeartMonitor hm) hm.FlickerPulse(durationSec);   // ward monitors sag with everything else
            foreach (Node n in tree.GetNodesInGroup("tvdevices"))
                if (n is TVDevice tv) tv.FlickerPulse(durationSec);   // televisions + monitors sag too (strawberry) --
                                                                      //  TVDevice.FlickerPulse ignores it on a set fed
                                                                      //  by its own wire, which never saw the dip
            // NOT traffic signals: their cabinets carry a battery back-up system, so a sag never reaches the lamps
            // (strawberry). TrafficLight.FlickerPulse is an explicit no-op documenting that.
        }

        // Fire each scheduled warning brownout as its evening-time arrives (once), then kill the grid for good on the
        // blackout day. Cheap -- a 2-4 item list checked off the day/night clock.
        void DriveBlackout()
        {
            for (int i = 0; i < _brownouts.Count; i++)
            {
                if (_brownoutFired.Contains(i)) continue;
                var (bd, bt) = _brownouts[i];
                if (Day == bd && Time >= bt) { _brownoutFired.Add(i); TriggerGlobalBrownout(); }
                else if (Day > bd) _brownoutFired.Add(i);   // clock jumped past it (dateset/timeadd) -> mark missed, don't fire late
            }
            if (!_blackoutFired && BlackoutDay > 0 && Day >= BlackoutDay) { _blackoutFired = true; PowerNet.SetGlobalPower(false); }
        }

        void DriveStreetlights(float delta)
        {
            var tree = GetTree();
            if (tree == null) return;   // out-of-tree _Process (some headless harnesses) -> nothing to sweep
            bool night = IsNightTime(Time);
            bool grid = MainsLive(tree, delta);
            if (_lampsNight == night && _lampsGrid == grid) return;
            _lampsNight = night; _lampsGrid = grid;
            foreach (Node n in tree.GetNodesInGroup("streetlights"))
                if (n is StreetLight sl) { sl.SetNight(night, animate: true); sl.SetPowered(grid, animate: true); }   // reaction-delay + flicker so the street powers up/down raggedly, not all at once (master)
            foreach (Node n in tree.GetNodesInGroup("gridlights"))
                if (n is GridLight gl) gl.SetPowered(grid, animate: true);   // indoor lamps follow the grid only -- always-on when powered, not night-gated
            // Traffic signals ride the same mains edge but NOT the night edge -- a junction runs its cycle around the
            // clock. Losing the grid drops them to a backup flash rather than dark; TrafficLight owns that state.
            foreach (Node n in tree.GetNodesInGroup("traffic_lights"))
                if (n is TrafficLight tl) tl.SetPowered(grid);
            // Glass-front coolers (lit even closed) + any open fridge re-check global power here so their interior
            // glow drops with the grid -- it goes dark at the blackout, not only when you next open the door.
            foreach (Node n in tree.GetNodesInGroup("glowcontainers"))
                if (n is StoreShelf ss) ss.RefreshGlow();
            // Televisions ride the same mains edge: a TV toggled on goes dark the instant the grid dies (blackout)
            // and warms back up when it returns -- Refresh reads PowerNet.GlobalPower live, mirroring the glow above.
            foreach (Node n in tree.GetNodesInGroup("tvdevices"))
                if (n is TVDevice tv) tv.Refresh();
        }


        // The mains have TWO representations, and the lamps were only reading one -- which is why toggling global
        // power left the streetlights burning. Direct SP flips the process-global PowerNet.GlobalPower. A joined
        // client / consuming loopback routes the console toggle to the SERVER, which flips each GridPowerSource's
        // replicated ToggledOn; the local flag never moves. So derive the mains the way an actual consumer does --
        // believe the breakers when the world has any, and fall back to the flag only when it has none (a bare
        // test sandbox, or a map with no Circuit_0 placed).
        //
        // Throttled: DriveStreetlights is called per-frame and early-returns on no change, so this scan must not
        // run every frame -- PEI has hundreds of deployables. 4Hz is imperceptible for a mains switch.
        float _mainsCheckT;
        bool _mainsCached = true;
        bool MainsLive(SceneTree tree, float delta)
        {
            _mainsCheckT -= delta;   // REAL delta: a hardcoded 1/60 made the throttle expire after a fixed
                                     // COUNT of calls rather than a fixed time, so the mains-flip latency moved
                                     // with the frame rate (and made a 6-tick test pass or fail on timing).
            if (_mainsCheckT > 0f) return _mainsCached;
            _mainsCheckT = 0.25f;
            bool sawSource = false, live = false;
            foreach (Node n in tree.GetNodesInGroup("deployables"))
                if (n is GridPowerSource g) { sawSource = true; if (g.IsProducing) { live = true; break; } }
            _mainsCached = sawSource ? live : PowerNet.GlobalPower;
            return _mainsCached;
        }


        // Mote opacity tracks the clock CONTINUOUSLY (unlike the lamps, which are a hard on/off edge), so this
        // cannot ride the edge-triggered sweep above. Stepped: only re-sweeps when the fade actually moved a
        // couple of percent, which is ~50 sweeps spread across each dusk/dawn rather than one per frame.
        float _lastMoteFade = -1f;
        void DriveMoteFade()
        {
            var tree = GetTree();
            if (tree == null) return;
            float a = StreetLight.MoteFadeFor(Time);
            if (_lastMoteFade >= 0f && Mathf.Abs(a - _lastMoteFade) < 0.02f) return;
            _lastMoteFade = a;
            foreach (Node n in tree.GetNodesInGroup("streetlights"))
                if (n is StreetLight sl) sl.SetMoteFade(a);
            // Vehicle headlight beams ride the SAME curve and the same stepped sweep -- strawberry asked for the
            // identical fade in/out timings, so they read one value rather than each deriving its own.
            foreach (Node n in tree.GetNodesInGroup("vehicles"))
                if (n is Vehicle vh) vh.SetHeadlightMoteFade(a);
        }

        // Sun sits at the horizon at t=0.25 (dawn) / 0.75 (dusk); lamps are lit while it's below, with a small dusk margin.
        public static bool IsNightTime(float t) => t < 0.26f || t > 0.74f;

        // Advance the day/night clock by `frac` cycles (delta/DayLength per frame, or timeAdd's hours/24). Wraps Time into
        // [0,1) exactly as before AND bumps the running Day counter on each forward midnight crossing (frac can exceed 1
        // for a big timeAdd -> multiple days at once). Negative frac rewinds Time but leaves Day untouched (monotonic).
        public void Advance(float frac)
        {
            float nt = Time + frac;
            if (nt >= 1f) Day += (int)nt;   // one Day per whole cycle crossed forward (usually exactly 1)
            Time = Mathf.PosMod(nt, 1f);
        }

        void EnsureSky()
        {
            if (_skyMat != null || Env == null) return;
            _skyMat = new ShaderMaterial { Shader = new Shader { Code = SkyShaderCode } };
            _skyMat.SetShaderParameter("clouds_tex", LoadTex("res://content/sky_clouds.png"));
            _skyMat.SetShaderParameter("stars_tex", LoadTex("res://content/sky_stars.png"));
            // constants straight from Skybox.mat
            _skyMat.SetShaderParameter("ambient_ground", new Color(0.8f, 0.8f, 0.8f));
            _skyMat.SetShaderParameter("ambient_equator", new Color(0.8f, 0.8f, 0.8f));
            _skyMat.SetShaderParameter("sun_inner", 0.995f);        // _SunInnerThreshold
            _skyMat.SetShaderParameter("sun_outer", 0.993f);        // _SunOuterThreshold
            _skyMat.SetShaderParameter("stars_cutoff", 0.0f);       // _StarsCutoff
            _skyMat.SetShaderParameter("moon_light_direction", new Vector3(0f, -1f, 0f));
            _skyMat.SetShaderParameter("moon_color", new Color(0.749f, 0.804f, 0.808f));
            _skyMat.SetShaderParameter("sqr_moon_radius", 0.01f);   // _SqrMoonRadius
            _skyMat.SetShaderParameter("cloud_rim_color", new Color(0.8f, 0.6f, 0.4f));
            _skyMat.SetShaderParameter("cloud_intensity", 1.0f);    // _CloudIntensity
            _skyMat.SetShaderParameter("cloud_params", new Vector4(0.6f, 10f, 0f, 0f));  // _CloudParams
            _sky = new Sky { SkyMaterial = _skyMat };
            Env.BackgroundMode = Godot.Environment.BGMode.Sky;
            Env.Sky = _sky;
            // SOURCE-ACCURATE ambient. Unturned's ambient is a warm sky/equator/ground gradient (RenderSettings ambient
            // Trilight) from the level's AMBIENT_SKY/EQUATOR/GROUND -- at midday a WARM TAN, not grey (that grey was the
            // washout). Godot has no Trilight, so use a single flat ambient = the per-time AMBIENT colour (Grad(Amb), set
            // each frame in Apply); at midday the 3 bands are near-identical warm tan so flat is faithful. NOT sky-sourced
            // -- the sky is blue but the AMBIENT is warm tan; they're separate slots in the src.
            Env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            Env.AmbientLightEnergy = float.TryParse(System.Environment.GetEnvironmentVariable("UG_AMB"), out var ae) ? ae : 1.0f;
            // src has NO post-process saturation grade -- the warm ambient is what reads rich. UG_SAT = optional override.
            float sat = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SAT"), out var s) ? s : 1.0f;
            if (System.Math.Abs(sat - 1.0f) > 0.001f) { Env.AdjustmentEnabled = true; Env.AdjustmentSaturation = sat; }
            // optional exposure knob for tuning (default 1.0 = neutral). UG_EXP-tunable.
            Env.TonemapExposure = float.TryParse(System.Environment.GetEnvironmentVariable("UG_EXP"), out var ex) ? ex : 1.0f;

            // ── "show off Godot" post-processing (master 2026-07-13): GLOW/BLOOM -- the cheapest + most dramatic pass.
            // Bright things bloom + halo: the sun disc, muzzle flashes, headlights/lightbar, fire, campfires. HDR-thresholded
            // so only the genuinely bright areas glow (not flat surfaces). UG_NOGLOW=1 to A/B; UG_GLOW / UG_GLOWTHRESH tune it.
            if (System.Environment.GetEnvironmentVariable("UG_NOGLOW") != "1")
            {
                Env.GlowEnabled = true;
                Env.GlowIntensity = float.TryParse(System.Environment.GetEnvironmentVariable("UG_GLOW"), out var gi) ? gi : 0.8f;
                Env.GlowStrength = 1.0f;
                Env.GlowBloom = 0.1f;                                                                                     // a touch of full-screen bloom under the threshold
                Env.GlowHdrThreshold = float.TryParse(System.Environment.GetEnvironmentVariable("UG_GLOWTHRESH"), out var gt) ? gt : 0.9f;  // only the top brightness blooms
                Env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen;                                            // natural bloom (not additive blowout)
            }
            // ACES filmic tonemap: cinematic highlight rolloff (near-free) vs clipping to flat white. UG_LINEAR=1 reverts.
            Env.TonemapMode = System.Environment.GetEnvironmentVariable("UG_LINEAR") == "1"
                ? Godot.Environment.ToneMapper.Linear
                : Godot.Environment.ToneMapper.Aces;
        }

        public void Apply()
        {
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_TIME"), out var ft)) Time = ft;   // freeze time-of-day for lighting A/B tests (0.5 = noon)
            Vector3 sunDir = new(0f, -1f, 0f);
            if (Sun != null)
            {
                float elevation = -Mathf.Cos(Time * Mathf.Tau) * 90f;      // +90 overhead at noon, -90 below at midnight
                Sun.RotationDegrees = new Vector3(-elevation, -40f, 0f);
                Sun.LightEnergy = Mathf.Clamp(Mathf.Sin(Time * Mathf.Tau - Mathf.Pi / 2f) * 1.35f, 0.015f, 1.35f);
                // real PEI SUN colour per time (dawn amber -> noon warm white -> dusk red -> midnight black); the
                // LightEnergy curve above fades it out at night, and the warm AMBIENT (Grad(Amb)) carries the daylight fill.
                Sun.LightColor = Grad(SunCol);
                sunDir = (-Sun.GlobalTransform.Basis.Z).Normalized();     // direction the light travels
            }
            if (Env != null)
            {
                EnsureSky();
                // day/night colours + sun/moon directions drive the ported sky shader
                _skyMat.SetShaderParameter("sky_color", Grad(SkyTop));
                _skyMat.SetShaderParameter("equator_color", Grad(SkyHorizon));
                _skyMat.SetShaderParameter("ground_color", Grad(Ground));
                _skyMat.SetShaderParameter("sun_direction", sunDir);
                _skyMat.SetShaderParameter("moon_direction", -sunDir);    // moon rides opposite the sun
                _skyMat.SetShaderParameter("sun_color", Sun != null ? Sun.LightColor : Colors.White);
                // Day->night factor from the sun's height (1 = day, 0 = night). ambient_ground/equator + cloud_rim_color were
                // CONSTANTS (bright 0.8), so the clouds (cloudBodyColor = ambient_ground + cloud_rim_color) GLOWED at night.
                // Darken them with the sun so night clouds go dim blue-grey (master: clouds shouldn't glow at night).
                float dayF = Mathf.Clamp(-sunDir.Y * 1.0f + 0.15f, 0f, 1f);
                float amb = Mathf.Lerp(0.05f, 0.8f, dayF);
                _skyMat.SetShaderParameter("ambient_ground", new Color(amb, amb, amb));
                _skyMat.SetShaderParameter("ambient_equator", new Color(amb, amb, amb));
                _skyMat.SetShaderParameter("cloud_rim_color", new Color(0.8f, 0.6f, 0.4f).Lerp(new Color(0.05f, 0.06f, 0.10f), 1f - dayF));
                // lightning cloud-flash uniforms -- WeatherManager drives these; pushed only while flashing (+ one frame
                // to switch off), not every idle frame. 0 flash = the sky-shader block skips (no-WM/golden stays 0).
                if (LightningFlash > 0.001f || _lightningActive)
                {
                    _lightningActive = LightningFlash > 0.001f;
                    _skyMat.SetShaderParameter("lightning_flash", LightningFlash);
                    _skyMat.SetShaderParameter("lightning_tint", LightningTint);
                    _skyMat.SetShaderParameter("lightning_dir", LightningDir);
                }

                Env.AmbientLightColor = Grad(Amb);
                // depth fog tinted to the horizon -- thin at noon, thick at dawn/dusk/night (extra when Overcast)
                float noon = 1f - Mathf.Abs(Time - 0.5f) * 2f;             // 1 at noon, 0 at midnight
                Env.FogEnabled = true;
                // master: derive the fog straight from the (corrected) sky horizon so it tracks the sky day/night.
                // ⚠ RAW sRGB, NOT SrgbToLinear -- Env properties take sRGB and linearise internally, the OPPOSITE of the
                // sky SHADER uniforms one screen up (tinyclaw). The old `.Lerp` toward a fixed light-grey (0.55/0.57/0.6)
                // read too light against the now-correct dark night sky, which is what master flagged.
                Env.FogLightColor = Grad(SkyHorizon);
                // Thinned AGAIN (master: "turn down the global fog a little more"), 0.012/0.0008 -> 0.008/0.0005.
                // Scaled both ends by the same ~2/3 rather than flattening the curve: the day/night contrast is the
                // part that reads as weather, so dropping only the night end would leave noon unchanged and take the
                // atmosphere out of dusk, which is the opposite of "a little more".
                // master "push fog end back a lot, start back a little": dropped 0.008->0.003 (night). Exponential fog ->
                // lowering density moves the thick/FAR end back a LOT and the near/START only a little (same %, but a much
                // bigger ABSOLUTE shift far out), which is exactly the ask -- no FogMode.Depth begin/end switch needed.
                Env.FogDensity = Mathf.Lerp(0.003f, 0.0004f, noon) * (Overcast ? 2.4f : 1f);   // noon already "sunny isle" thin; night/overcast stay atmospheric but pushed back
                Env.FogSkyAffect = Mathf.Lerp(0.4f, 0.15f, noon);   // sky stays clear/blue at noon, fogged at dawn/dusk/night

                // STORM: master wants heavy weather to match the moody --raintest demo (grey overcast sky, thick fog,
                // dim cool light). Blend the whole scene toward that by StormAmount (0..1, from the rain intensity) so
                // light rain is only mildly grey and heavy is a full storm. Runs LAST -> supersedes the fair-weather env.
                float storm = Mathf.Clamp(StormAmount, 0f, 1f);
                if (storm > 0.001f)
                {
                    _skyMat.SetShaderParameter("sky_color", Grad(SkyTop).Lerp(new Color(0.35f, 0.39f, 0.45f), storm));
                    _skyMat.SetShaderParameter("equator_color", Grad(SkyHorizon).Lerp(new Color(0.45f, 0.48f, 0.53f), storm));
                    float ca = amb * Mathf.Lerp(1f, 0.32f, storm);   // grey the cloud BODY down (bright white -> dark storm grey)
                    _skyMat.SetShaderParameter("ambient_ground", new Color(ca, ca, ca));
                    _skyMat.SetShaderParameter("ambient_equator", new Color(ca, ca, ca));
                    Color fairRim = new Color(0.8f, 0.6f, 0.4f).Lerp(new Color(0.05f, 0.06f, 0.10f), 1f - dayF);
                    _skyMat.SetShaderParameter("cloud_rim_color", fairRim.Lerp(new Color(0.22f, 0.24f, 0.28f), storm));   // and the RIM -> storm clouds go dark grey, not bright white
                    if (Sun != null)
                    {
                        Sun.LightEnergy *= Mathf.Lerp(1f, 0.35f, storm);                                     // storm dims the sun
                        Sun.LightColor = Sun.LightColor.Lerp(new Color(0.72f, 0.76f, 0.84f), storm);         // and cools it
                        _skyMat.SetShaderParameter("sun_color", Sun.LightColor);
                    }
                    Env.AmbientLightColor = Env.AmbientLightColor.Lerp(new Color(0.50f, 0.53f, 0.57f), storm);
                    Env.FogLightColor = Env.FogLightColor.Lerp(new Color(0.50f, 0.54f, 0.60f), storm);       // grey-blue fog
                    Env.FogDensity = Mathf.Lerp(Env.FogDensity, 0.008f, storm);                             // thick moody haze
                    Env.FogSkyAffect = Mathf.Lerp(Env.FogSkyAffect, 0.6f, storm);                           // fog greys into the sky/horizon too
                }
            }
        }

        public bool Overcast;   // denser fog + greyer feel (a simple weather state; map-editor toggle)
        public float StormAmount;   // 0..1 storm intensity (WeatherManager sets it from the rain) -> blends the scene toward the moody overcast look in Apply(). Defaults 0 = fair weather, so editor/demos/golden are unchanged.
        // Lightning flash on the CLOUD LAYER (Fable's pick). WeatherManager sets these; Apply() pushes them to the sky
        // shader IN the same writer so there's no two-writer fight. Default flash 0 -> the shader block skips (golden safe).
        public float LightningFlash;
        public Color LightningTint = new(1f, 0.96f, 0.85f);
        public Vector3 LightningDir = Vector3.Forward;
        bool _lightningActive;   // push the sky lightning uniforms only while flashing (+ one frame to switch off), not every idle frame

        internal static ImageTexture LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        // The sky-shader colour uniforms are declared `: source_color`, so passing a Color to SetShaderParameter
        // does the sRGB->linear conversion in-engine -- the repo standard (e.g. clothes.gdshader skin_color). NB: only a
        // Color converts, NOT a Vector3 -- that gotcha is Vehicle.cs:438, which is why every sky uniform is fed a Color.

        Color Grad(Color[] keys)
        {
            float f = Time * 4f;               // keys sit at t = 0, .25, .5, .75
            int i = ((int)f) % 4, j = (i + 1) % 4;
            return keys[i].Lerp(keys[j], f - Mathf.Floor(f));
        }
    }
}
