using Godot;

namespace UnturnedGodot
{
    // Bounded day/night cycle -- an approximation of Unturned's LevelLighting time-of-day. The real thing is driven by
    // per-map sky-gradient assets we don't have here, so this arcs a sun DirectionalLight across the sky over DayLength
    // seconds and lerps the sky + ambient colours through midnight -> dawn -> noon -> dusk. Drives an Environment.
    public partial class DayNightCycle : Node
    {
        public DirectionalLight3D Sun;
        public Godot.Environment Env;
        public float DayLength = 120f;   // seconds per full cycle (short here; Unturned's is ~an hour)
        public float Time = 0.35f;       // 0..1 time of day: 0 midnight, 0.25 dawn, 0.5 noon, 0.75 dusk

        // key colours at midnight / dawn / noon / dusk (wraps back to midnight)
        static readonly Color[] Sky = {
            new(0.03f, 0.04f, 0.09f), new(0.62f, 0.46f, 0.42f), new(0.42f, 0.55f, 0.72f), new(0.70f, 0.40f, 0.28f),
        };
        static readonly Color[] Amb = {
            new(0.05f, 0.06f, 0.12f), new(0.42f, 0.38f, 0.40f), new(0.60f, 0.62f, 0.65f), new(0.50f, 0.40f, 0.36f),
        };

        public override void _Process(double delta)
        {
            Time = Mathf.PosMod(Time + (float)delta / DayLength, 1f);
            Apply();
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
                Env.BackgroundColor = Grad(Sky);
                Env.AmbientLightColor = Grad(Amb);
                // depth fog tinted to the sky -- thin at noon, thick at dawn/dusk/night (extra when Overcast)
                float noon = 1f - Mathf.Abs(Time - 0.5f) * 2f;             // 1 at noon, 0 at midnight
                Env.FogEnabled = true;
                Env.FogLightColor = Grad(Sky).Lerp(new Color(0.55f, 0.57f, 0.6f), 0.35f);
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
