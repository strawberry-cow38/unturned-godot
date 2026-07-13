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

        // sky-dome zenith colour at midnight / dawn / noon / dusk -- noon key = Unturned Skybox.mat _SkyColor
        static readonly Color[] SkyTop = {
            new(0.020f, 0.030f, 0.070f), new(0.560f, 0.430f, 0.450f), new(0.636f, 0.720f, 0.801f), new(0.640f, 0.360f, 0.250f),
        };
        // horizon (equator) colour -- noon key = Unturned Skybox.mat _EquatorColor (0.801)
        static readonly Color[] SkyHorizon = {
            new(0.040f, 0.050f, 0.100f), new(0.760f, 0.560f, 0.460f), new(0.801f, 0.801f, 0.801f), new(0.820f, 0.460f, 0.300f),
        };
        // below-horizon ground colour -- noon key = Unturned Skybox.mat _GroundColor (0.5)
        static readonly Color[] Ground = {
            new(0.030f, 0.035f, 0.060f), new(0.330f, 0.300f, 0.290f), new(0.500f, 0.500f, 0.500f), new(0.360f, 0.280f, 0.240f),
        };
        // ambient tint at midnight / dawn / noon / dusk
        static readonly Color[] Amb = {
            new(0.05f, 0.06f, 0.12f), new(0.42f, 0.38f, 0.40f), new(0.60f, 0.62f, 0.65f), new(0.50f, 0.40f, 0.36f),
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
            Env.AmbientLightSource = Godot.Environment.AmbientSource.Color;   // keep the tuned ambient
            // Unturned's palette is far RICHER + more saturated than Godot's flat default grade washed it to (master:
            // "WAYYY off, much richer and saturated"). Add a post-process color grade on the world: strong saturation
            // boost + a touch of contrast. UG_SAT env var overrides the saturation for A/B tuning (default 1.45).
            float sat = float.TryParse(System.Environment.GetEnvironmentVariable("UG_SAT"), out var s) ? s : 1.45f;
            Env.AdjustmentEnabled = true;
            Env.AdjustmentSaturation = sat;
            Env.AdjustmentContrast = 1.08f;
        }

        public void Apply()
        {
            Vector3 sunDir = new(0f, -1f, 0f);
            if (Sun != null)
            {
                float elevation = -Mathf.Cos(Time * Mathf.Tau) * 90f;      // +90 overhead at noon, -90 below at midnight
                Sun.RotationDegrees = new Vector3(-elevation, -40f, 0f);
                Sun.LightEnergy = Mathf.Clamp(Mathf.Sin(Time * Mathf.Tau - Mathf.Pi / 2f) * 1.35f, 0.015f, 1.35f);
                Sun.LightColor = Time < 0.28f || Time > 0.72f ? new Color(0.6f, 0.62f, 0.8f)   // moonlight tint
                               : Time < 0.35f || Time > 0.65f ? new Color(1f, 0.72f, 0.5f)     // golden hour
                               : Colors.White;
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
