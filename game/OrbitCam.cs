using Godot;

namespace UnturnedGodot
{
    // Orbits a camera in a horizontal circle around Center, always looking at it.
    // Advances a FIXED angle per _Process frame so it renders deterministically under
    // --write-movie --fixed-fps (N frames = N * DegPerFrame degrees). Prop-showcase orbits.
    public partial class OrbitCam : Node3D
    {
        public Camera3D Cam;
        public Vector3 Center;
        public float Radius = 58f;
        public float VOffset = 10f;      // camera height above Center
        public float DegPerFrame = 2f;   // 180 frames -> a full 360 orbit
        float _ang = 30f;                // start angle (a 3/4 front view)
        int _frame;

        public override void _Process(double delta)
        {
            float r = Mathf.DegToRad(_ang);
            Cam.GlobalPosition = Center + new Vector3(Mathf.Cos(r) * Radius, VOffset, Mathf.Sin(r) * Radius);
            Cam.LookAt(Center, Vector3.Up);
            _ang += DegPerFrame;
            if (++_frame >= 186) GetTree().Quit();   // ~one full loop (372) then stop the movie render
        }
    }
}
