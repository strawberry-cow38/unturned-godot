using Godot;

namespace UnturnedGodot
{
    // Orbits a camera in a horizontal circle around Center, always looking at it.
    // Advances a FIXED angle per _Process frame so it renders deterministically under
    // --write-movie --fixed-fps. Self-quits after one loop so the movie render terminates.
    // Radius / height / center-lift are env-tunable (UG_ORBITR / UG_ORBITH / UG_ORBITCY)
    // so the same harness frames a single prop (lighthouse) or a whole town.
    public partial class OrbitCam : Node3D
    {
        public Camera3D Cam;
        public Vector3 Center;           // Main passes the spawn/prop base; _Ready lifts it by UG_ORBITCY
        public float Radius = 58f;
        public float VOffset = 10f;      // camera height above Center
        public float DegPerFrame = 2f;   // 180 frames -> a full 360 orbit
        float _ang = 30f;                // start angle (a 3/4 front view)
        int _frame;

        public override void _Ready()
        {
            Radius = EnvF("UG_ORBITR", Radius);
            VOffset = EnvF("UG_ORBITH", VOffset);
            Center += new Vector3(0f, EnvF("UG_ORBITCY", 20f), 0f);   // lift the look-at target off the ground
        }

        public override void _Process(double delta)
        {
            float r = Mathf.DegToRad(_ang);
            Cam.GlobalPosition = Center + new Vector3(Mathf.Cos(r) * Radius, VOffset, Mathf.Sin(r) * Radius);
            Cam.LookAt(Center, Vector3.Up);
            _ang += DegPerFrame;
            if (++_frame >= 186) GetTree().Quit();   // ~one full loop (372) then stop the movie render
        }

        static float EnvF(string n, float d)
        {
            var v = System.Environment.GetEnvironmentVariable(n);
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : d;
        }
    }
}
