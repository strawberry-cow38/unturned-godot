using Godot;

namespace UnturnedGodot
{
    // Bounded day/night cycle -- an approximation of Unturned's LevelLighting time-of-day. Arcs a sun DirectionalLight
    // across the sky over DayLength seconds and lerps the sky through midnight -> dawn -> noon -> dusk. Drives an
    // Environment whose background is a real gradient Sky (a faithful port of Unturned's Skybox.mat: a sky/equator/
    // ground colour gradient + a sharp sun disc). The noon keys ARE Unturned's extracted Skybox.mat values --
    // _SkyColor (0.636,0.720,0.801), _EquatorColor (0.801), _GroundColor (0.5); dawn/dusk/midnight tint from those.
    // (Unturned's shader also layers cloud/moon/aurora textures -- a deeper second pass; this brings the colour + sun.)
    public partial class DayNightCycle : Node
    {
        public DirectionalLight3D Sun;
        public Godot.Environment Env;
        public float DayLength = 120f;   // seconds per full cycle (short here; Unturned's is ~an hour)
        public float Time = 0.35f;       // 0..1 time of day: 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk

        // sky-dome top colour at midnight / dawn / noon / dusk -- noon key = Unturned Skybox.mat _SkyColor
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
        ProceduralSkyMaterial _skyMat;

        public override void _Process(double delta)
        {
            Time = Mathf.PosMod(Time + (float)delta / DayLength, 1f);
            Apply();
        }

        // Build the gradient Sky once (mirrors Unturned's Skybox.mat: sky/equator/ground gradient + a sharp sun disc
        // -- _SunExponent 287 / _SunThreshold 0.995 = a small tight disc, so SunAngleMax small + a hard SunCurve).
        void EnsureSky()
        {
            if (_skyMat != null || Env == null) return;
            _skyMat = new ProceduralSkyMaterial
            {
                SkyCurve = 0.18f,        // horizon->top falloff
                GroundCurve = 0.02f,
                SunAngleMax = 4.0f,      // tight sun disc (Unturned's _SunExponent is very sharp)
                SunCurve = 0.07f,
                UseDebanding = true,
            };
            _sky = new Sky { SkyMaterial = _skyMat };
            Env.BackgroundMode = Godot.Environment.BGMode.Sky;
            Env.Sky = _sky;
            Env.SkyRotation = Vector3.Zero;
            // keep the tuned explicit ambient (don't let the bright sky wash out the world)
            Env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        }

        public void Apply()
        {
            if (Sun != null)
            {
                float elevation = -Mathf.Cos(Time * Mathf.Tau) * 90f;      // +90 overhead at noon, -90 below at midnight
                Sun.RotationDegrees = new Vector3(-elevation, -40f, 0f);
                Sun.LightEnergy = Mathf.Clamp(Mathf.Sin(Time * Mathf.Tau - Mathf.Pi / 2f) * 1.35f, 0.015f, 1.35f);
                Sun.LightColor = Time < 0.28f || Time > 0.72f ? new Color(0.6f, 0.62f, 0.8f)   // moonlight tint
                               : Time < 0.35f || Time > 0.65f ? new Color(1f, 0.72f, 0.5f)     // golden hour
                               : Colors.White;
            }
            if (Env != null)
            {
                EnsureSky();
                // drive the gradient sky through the day (the sun disc itself follows the DirectionalLight above)
                _skyMat.SkyTopColor = Grad(SkyTop);
                _skyMat.SkyHorizonColor = Grad(SkyHorizon);
                _skyMat.GroundHorizonColor = Grad(SkyHorizon);
                _skyMat.GroundBottomColor = Grad(Ground);
                // dim the whole dome toward night so the daytime blue doesn't glow at midnight
                float day = Mathf.Clamp((Sun?.LightEnergy ?? 1f) / 1.35f, 0.05f, 1f);
                _skyMat.SkyEnergyMultiplier = Mathf.Lerp(0.35f, 1f, day);
                _skyMat.GroundEnergyMultiplier = Mathf.Lerp(0.35f, 1f, day);

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

        Color Grad(Color[] keys)
        {
            float f = Time * 4f;               // keys sit at t = 0, .25, .5, .75
            int i = ((int)f) % 4, j = (i + 1) % 4;
            return keys[i].Lerp(keys[j], f - Mathf.Floor(f));
        }
    }
}
