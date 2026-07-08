using Godot;

namespace UnturnedGodot
{
    // Minimal direct-chase zombie for the vertical slice: steer straight at the target on the ground each
    // physics tick, stop at melee range, fall under gravity, die when shot. Ported animations + the real
    // per-type Unturned speeds come later; this proves the chase+kill loop on the 50 Hz tick.
    public partial class ZombieController : CharacterBody3D
    {
        public Node3D Target;
        [Export] public float Speed = 3.2f;      // placeholder chase speed (m/s)
        [Export] public float MeleeRange = 1.3f;
        [Export] public float Health = 100f;

        public bool Dead { get; private set; }

        MeshInstance3D _body;
        StandardMaterial3D _mat;
        float _deadTimer;

        public override void _Ready()
        {
            AddToGroup("zombies");
            CollisionLayer = 1 << 1;   // "enemy" bit the gun ray masks for
            CollisionMask = 1 << 0;    // collides with ground (bit 0)

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.4f } };
            shape.Position = new Vector3(0, 0.9f, 0);
            AddChild(shape);

            _mat = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.30f) }; // sickly green
            _body = new MeshInstance3D { Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f }, MaterialOverride = _mat };
            _body.Position = new Vector3(0, 0.9f, 0);
            AddChild(_body);
        }

        public void Damage(float amount)
        {
            if (Dead) return;
            Health -= amount;
            _mat.AlbedoColor = new Color(0.7f, 0.2f, 0.2f); // flash red on hit
            if (Health <= 0f)
            {
                Dead = true;
                Velocity = Vector3.Zero;
                _mat.AlbedoColor = new Color(0.4f, 0.1f, 0.1f);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            float g = SDG.Unturned.PlayerMovementDef.GRAVITY;

            if (Dead)
            {
                // topple over then settle
                _deadTimer += (float)delta;
                _body.RotationDegrees = new Vector3(Mathf.Min(90f, _deadTimer * 220f), _body.RotationDegrees.Y, 0);
                Velocity = new Vector3(0, Velocity.Y - g * (float)delta, 0);
                MoveAndSlide();
                return;
            }
            if (Target == null) return;

            Vector3 to = Target.GlobalPosition - GlobalPosition;
            to.Y = 0f;
            float dist = to.Length();
            Vector3 horiz = dist > MeleeRange && dist > 0.001f ? to.Normalized() * Speed : Vector3.Zero;
            Velocity = new Vector3(horiz.X, Velocity.Y - g * (float)delta, horiz.Z);
            MoveAndSlide();

            if (dist > 0.1f)
                LookAt(new Vector3(Target.GlobalPosition.X, GlobalPosition.Y, Target.GlobalPosition.Z), Vector3.Up);
        }
    }
}
