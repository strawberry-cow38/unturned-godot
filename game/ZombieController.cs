using Godot;

namespace UnturnedGodot
{
    // Direct-chase zombie for the single-player playable build: chases the target, melees it in range, dies
    // when shot, topples over. Body = the REAL ripped character mesh (green tint) when loaded, else a capsule.
    public partial class ZombieController : CharacterBody3D
    {
        public Node3D Target;
        [Export] public float Speed = 3.2f;
        [Export] public float MeleeRange = 1.4f;
        [Export] public float Health = 100f;
        [Export] public float AttackDamage = 12f;
        [Export] public float AttackInterval = 0.8f;

        public bool Dead { get; private set; }

        Node3D _body;
        StandardMaterial3D _capMat;
        float _attackCd, _deadTimer;

        public override void _Ready()
        {
            AddToGroup("zombies");
            CollisionLayer = 1 << 1;   // enemy bit the gun ray masks for
            CollisionMask = 1 << 0;    // collide with ground

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.4f } };
            shape.Position = new Vector3(0, 0.9f, 0);
            AddChild(shape);

            if (CharacterModel.Loaded)
            {
                _body = CharacterModel.Build(new Color(0.55f, 0.95f, 0.55f));
            }
            else
            {
                _capMat = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.30f) };
                var mi = new MeshInstance3D { Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f }, MaterialOverride = _capMat, Position = new Vector3(0, 0.9f, 0) };
                _body = new Node3D();
                _body.AddChild(mi);
            }
            AddChild(_body);
        }

        public void Damage(float amount)
        {
            if (Dead) return;
            Health -= amount;
            if (_capMat != null) _capMat.AlbedoColor = new Color(0.7f, 0.2f, 0.2f);
            if (Health <= 0f) { Dead = true; Velocity = Vector3.Zero; }
        }

        public override void _PhysicsProcess(double delta)
        {
            float g = SDG.Unturned.PlayerMovementDef.GRAVITY;

            if (Dead)
            {
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

            if (_attackCd > 0f) _attackCd -= (float)delta;
            if (dist <= MeleeRange && _attackCd <= 0f && Target is PlayerController p)
            {
                p.TakeDamage(AttackDamage);
                _attackCd = AttackInterval;
            }

            if (dist > 0.1f)
                LookAt(new Vector3(Target.GlobalPosition.X, GlobalPosition.Y, Target.GlobalPosition.Z), Vector3.Up);
        }
    }
}
