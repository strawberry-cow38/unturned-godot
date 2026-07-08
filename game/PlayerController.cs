using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player driving the ported PlayerMovementSim on Godot's physics tick (set to 50 Hz in
    // project.godot to match retail's Fixed Timestep). Movement CONSTANTS are exact (PlayerMovementDef);
    // collision/feel go through Jolt, which can't be byte-identical to Unity PhysX -- the accepted plan
    // tradeoff. Look = mouse yaw on the body, pitch on the camera. WASD / Shift=sprint / Ctrl=crouch / Space=jump.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        Camera3D _cam;
        float _pitchDeg;

        [Export] public float MouseSensitivity = 0.12f;

        public override void _Ready()
        {
            _cam = GetNodeOrNull<Camera3D>("Camera3D");
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                if (_cam != null) _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
            }
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            _move.Stance = Input.IsPhysicalKeyPressed(Key.Shift) ? EPlayerStance.SPRINT
                         : Input.IsPhysicalKeyPressed(Key.Ctrl) ? EPlayerStance.CROUCH
                         : EPlayerStance.STAND;

            float forward = (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f);
            float strafe  = (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f);
            bool jump = Input.IsPhysicalKeyPressed(Key.Space);

            var v = _move.Step(new UnityEngine.Vector2(strafe, forward), jump, IsOnFloor(), (float)delta);

            // sim velocity is body-local (x=right, z=forward). Godot local forward is -Z; rotate into world.
            Vector3 localHoriz = new Vector3(v.x, 0f, -v.z);
            Vector3 worldHoriz = GlobalTransform.Basis * localHoriz;
            Velocity = new Vector3(worldHoriz.X, v.y, worldHoriz.Z);
            MoveAndSlide();
        }
    }
}
