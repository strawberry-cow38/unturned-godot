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
        RiggedCharacter _rig;
        bool _startled;
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

            _rig = RiggedCharacter.Build("res://content/rig.json", new Color(0.45f, 0.72f, 0.40f));
            if (_rig != null)
            {
                // Unturned zombies (Zombie.cs): moveAnim="Move_"+move, idleAnim="Idle_"+idle, per-zombie bytes.
                // These are the arms-out shamble clips, NOT the human Move_Walk. Randomise for variety.
                // Move_0..3 = normal upright shambles; Move_4/5 = the CRAWLER variant (separate zombie type).
                // Normal zombies must NOT use the crawler moves -> restrict to 0..3.
                var rng = new RandomNumberGenerator(); rng.Randomize();
                _rig.WalkClip = "Move_" + rng.RandiRange(0, 3);
                _rig.RunClip = _rig.WalkClip;                 // zombies don't run; shamble at any speed
                _rig.IdleClip = "Idle_" + rng.RandiRange(0, 3);
                _startleId = rng.RandiRange(0, 1);
                _atkId = rng.RandiRange(0, 2);
                _body = _rig;
                _rig.Play(_rig.IdleClip);
            }
            else if (CharacterModel.Loaded)
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

        bool _ragdoll;
        int _atkId, _startleId;
        [Export] public float ImpactForce = 9f;

        public void Damage(float amount) => ApplyDamage(amount, GlobalPosition, Vector3.Zero, false);

        // Gun hit: carries the impact point + bullet direction so the death ragdoll gets shoved there.
        public void DamageHit(float amount, Vector3 point, Vector3 dir) => ApplyDamage(amount, point, dir, true);

        void ApplyDamage(float amount, Vector3 point, Vector3 dir, bool impact)
        {
            if (Dead) return;
            Health -= amount;
            if (_capMat != null) _capMat.AlbedoColor = new Color(0.7f, 0.2f, 0.2f);
            if (Health <= 0f)
            {
                Dead = true;
                Velocity = Vector3.Zero;
                CollisionLayer = 0;   // corpse: bullets pass through the capsule to the ragdoll bones
                if (_rig != null)
                {
                    // Unturned RagdollTool spine pop: (dir + up*8 + randXZ +-16) * 32, one physics step.
                    Vector3 away = impact ? dir : (Target != null ? GlobalPosition - Target.GlobalPosition : -GlobalTransform.Basis.Z);
                    away = new Vector3(away.X, 0f, away.Z);
                    away = away.LengthSquared() > 0.01f ? away.Normalized() : -GlobalTransform.Basis.Z;
                    var r = new RandomNumberGenerator(); r.Randomize();
                    Vector3 f = (away * 6f + Vector3.Up * 8f + new Vector3(r.RandfRange(-16f, 16f), 0f, r.RandfRange(-16f, 16f))) * 0.64f;
                    _rig.RagdollStart(f);
                    if (impact) _rig.ApplyImpact(point, dir * ImpactForce);  // shove at the exact bone hit
                    _ragdoll = true;
                }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            float g = SDG.Unturned.PlayerMovementDef.GRAVITY;

            if (Dead)
            {
                if (_ragdoll) return;   // physics ragdoll drives the body now (no scripted topple)
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
            bool attacked = false;
            if (dist <= MeleeRange && _attackCd <= 0f && Target is PlayerController p)
            {
                p.TakeDamage(AttackDamage);
                _attackCd = AttackInterval;
                attacked = true;
            }

            // animation state: startle on first aggro, attack swing on hit, else walk/idle by speed
            if (_rig != null)
            {
                _rig.Tick(delta);
                if (!_startled) { _startled = true; _rig.PlayOnce("Startle_" + _startleId); }
                else if (attacked) _rig.PlayOnce("Attack_" + _atkId);
                else _rig.SetLocomotion(horiz.Length());
            }

            if (dist > 0.1f)
                LookAt(new Vector3(Target.GlobalPosition.X, GlobalPosition.Y, Target.GlobalPosition.Z), Vector3.Up);
        }
    }
}
