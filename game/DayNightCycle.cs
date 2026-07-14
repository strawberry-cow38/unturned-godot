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
        public float DayLength = 120f;   // seconds per full cycle (short here; Unturned's is ~an hour)
        public float Time = 0.35f;       // 0..1 time of day: 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk

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
        const string SkyShaderCode = @"
shader_type sky;

uniform vec3 sky_color;
uniform vec3 equator_color;
uniform vec3 ground_color;
uniform vec3 ambient_ground;      // _SkyHackAmbientGround
uniform vec3 ambient_equator;     // _SkyHackAmbientEquator
uniform vec3 sun_direction;       // direction the sunlight travels
uniform vec3 sun_color;
uniform float sun_inner;
uniform float sun_outer;
uniform sampler2D stars_tex : repeat_enable, filter_linear;
uniform float stars_cutoff;
uniform vec3 moon_direction;
uniform vec3 moon_light_direction;
uniform vec3 moon_color;
uniform float sqr_moon_radius;
uniform sampler2D clouds_tex : repeat_enable, filter_linear;
uniform vec3 cloud_rim_color;
uniform float cloud_intensity;
uniform vec4 cloud_params;        // R: macro cutoff, G: macro saturation

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

        float cloudsBodyAlpha = clamp(macroAlpha + macroAlpha * cloudsMedium + macroAlpha * cloudsSmall, 0.0, 1.0);
        float cloudsAlpha = cloudsBodyAlpha * clamp(viewDir.y * 2.0, 0.0, 1.0);
        col = mix(col, mix(cRimColor, cloudBodyColor, cloudsBodyAlpha), cloudsAlpha);
    }

    COLOR = col;
}
";

        public override void _Process(double delta)
        {
            Time = Mathf.PosMod(Time + (float)delta / DayLength, 1f);
            Apply();
        }

        void EnsureSky()
        {
            if (_skyMat != null || Env == null) return;
            _skyMat = new ShaderMaterial { Shader = new Shader { Code = SkyShaderCode } };
            _skyMat.SetShaderParameter("clouds_tex", LoadTex("res://content/sky_clouds.png"));
            _skyMat.SetShaderParameter("stars_tex", LoadTex("res://content/sky_stars.png"));
            // constants straight from Skybox.mat
            _skyMat.SetShaderParameter("ambient_ground", new Vector3(0.8f, 0.8f, 0.8f));
            _skyMat.SetShaderParameter("ambient_equator", new Vector3(0.8f, 0.8f, 0.8f));
            _skyMat.SetShaderParameter("sun_inner", 0.995f);        // _SunInnerThreshold
            _skyMat.SetShaderParameter("sun_outer", 0.993f);        // _SunOuterThreshold
            _skyMat.SetShaderParameter("stars_cutoff", 0.0f);       // _StarsCutoff
            _skyMat.SetShaderParameter("moon_light_direction", new Vector3(0f, -1f, 0f));
            _skyMat.SetShaderParameter("moon_color", new Vector3(0.749f, 0.804f, 0.808f));
            _skyMat.SetShaderParameter("sqr_moon_radius", 0.01f);   // _SqrMoonRadius
            _skyMat.SetShaderParameter("cloud_rim_color", new Vector3(0.8f, 0.6f, 0.4f));
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
                _skyMat.SetShaderParameter("sky_color", V3(Grad(SkyTop)));
                _skyMat.SetShaderParameter("equator_color", V3(Grad(SkyHorizon)));
                _skyMat.SetShaderParameter("ground_color", V3(Grad(Ground)));
                _skyMat.SetShaderParameter("sun_direction", sunDir);
                _skyMat.SetShaderParameter("moon_direction", -sunDir);    // moon rides opposite the sun
                _skyMat.SetShaderParameter("sun_color", V3(Sun != null ? Sun.LightColor : Colors.White));
                // Day->night factor from the sun's height (1 = day, 0 = night). ambient_ground/equator + cloud_rim_color were
                // CONSTANTS (bright 0.8), so the clouds (cloudBodyColor = ambient_ground + cloud_rim_color) GLOWED at night.
                // Darken them with the sun so night clouds go dim blue-grey (master: clouds shouldn't glow at night).
                float dayF = Mathf.Clamp(-sunDir.Y * 1.0f + 0.15f, 0f, 1f);
                float amb = Mathf.Lerp(0.05f, 0.8f, dayF);
                _skyMat.SetShaderParameter("ambient_ground", new Vector3(amb, amb, amb));
                _skyMat.SetShaderParameter("ambient_equator", new Vector3(amb, amb, amb));
                _skyMat.SetShaderParameter("cloud_rim_color", V3(new Color(0.8f, 0.6f, 0.4f).Lerp(new Color(0.05f, 0.06f, 0.10f), 1f - dayF)));

                Env.AmbientLightColor = Grad(Amb);
                // depth fog tinted to the horizon -- thin at noon, thick at dawn/dusk/night (extra when Overcast)
                float noon = 1f - Mathf.Abs(Time - 0.5f) * 2f;             // 1 at noon, 0 at midnight
                Env.FogEnabled = true;
                Env.FogLightColor = Grad(SkyHorizon).Lerp(new Color(0.55f, 0.57f, 0.6f), 0.35f);
                Env.FogDensity = Mathf.Lerp(0.012f, 0.0025f, noon) * (Overcast ? 2.4f : 1f);
                Env.FogSkyAffect = 0.4f;
            }
        }

        public bool Overcast;   // denser fog + greyer feel (a simple weather state)

        static ImageTexture LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        static Vector3 V3(Color c) => new(c.R, c.G, c.B);

        Color Grad(Color[] keys)
        {
            float f = Time * 4f;               // keys sit at t = 0, .25, .5, .75
            int i = ((int)f) % 4, j = (i + 1) % 4;
            return keys[i].Lerp(keys[j], f - Mathf.Floor(f));
        }
    }
}
