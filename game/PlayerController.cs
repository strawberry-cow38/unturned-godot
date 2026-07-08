using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // First-person player: ported PlayerMovementSim on Godot's 50 Hz physics tick + mouse look + a hitscan
    // gun (raycast from the camera vs the zombie collision layer). Movement CONSTANTS are exact; feel goes
    // through Jolt. Builds its own camera + capsule collider so it can be spawned from code.
    // WASD move / Shift sprint / Ctrl crouch / Space jump / LMB fire / Esc release mouse.
    public partial class PlayerController : CharacterBody3D
    {
        readonly PlayerMovementSim _move = new PlayerMovementSim();
        Camera3D _cam;
        float _pitchDeg;

        [Export] public float MouseSensitivity = 0.12f;
        [Export] public int Ammo = 30;
        [Export] public float Damage = 34f;
        public int Kills { get; private set; }

        public Camera3D Camera => _cam;

        public override void _Ready()
        {
            CollisionLayer = 1 << 3;   // player bit
            CollisionMask = 1 << 0;    // walk on ground (bit 0)

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.35f } };
            shape.Position = new Vector3(0, 0.9f, 0);
            AddChild(shape);

            _cam = new Camera3D { Position = new Vector3(0, 1.6f, 0), Current = true };
            AddChild(_cam);

            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                RotateY(Mathf.DegToRad(-mm.Relative.X * MouseSensitivity));
                _pitchDeg = Mathf.Clamp(_pitchDeg - mm.Relative.Y * MouseSensitivity, -89f, 89f);
                _cam.RotationDegrees = new Vector3(_pitchDeg, 0f, 0f);
            }
            else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                Fire();
            else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        }

        // Hitscan: ray from the camera along its forward, masked to the zombie layer.
        public bool Fire()
        {
            if (Ammo <= 0 || _cam == null) return false;
            Ammo--;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition;
            Vector3 to = from + (-_cam.GlobalTransform.Basis.Z) * 200f;
            var query = PhysicsRayQueryParameters3D.Create(from, to, 1u << 1); // zombie/enemy bit
            var hit = space.IntersectRay(query);
            if (hit.Count > 0 && hit["collider"].As<GodotObject>() is ZombieController z)
            {
                bool wasDead = z.Dead;
                z.Damage(Damage);
                if (!wasDead && z.Dead) Kills++;
                return true;
            }
            return false;
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
            Vector3 world = GlobalTransform.Basis * new Vector3(v.x, 0f, -v.z);
            Velocity = new Vector3(world.X, v.y, world.Z);
            MoveAndSlide();
        }
    }
}
