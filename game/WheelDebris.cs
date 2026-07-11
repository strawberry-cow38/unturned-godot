using Godot;

namespace UnturnedGodot
{
    // A wheel that flew off an exploding vehicle: physics debris that lives ~10s, fades out over its last
    // second, then despawns (master 2026-07-11). Uses a per-instance material so the fade never touches the
    // car's own wheels.
    public partial class WheelDebris : RigidBody3D
    {
        public StandardMaterial3D Mat;
        double _life = 10.0;

        public override void _Process(double delta)
        {
            _life -= delta;
            if (_life <= 1.0 && Mat != null)
            {
                Mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                var c = Mat.AlbedoColor; c.A = (float)Mathf.Clamp(_life, 0.0, 1.0); Mat.AlbedoColor = c;
            }
            if (_life <= 0.0) QueueFree();
        }
    }
}
