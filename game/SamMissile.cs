using Godot;

namespace UnturnedGodot
{
    // A HOMING SAM ROUND (strawberry 2026-09-09: "a barrage of rockets that home onto the heli").
    //
    // Kinematic, not a RigidBody. A missile is not a thing being pushed around by the world -- it is a thing with an
    // opinion, and the whole behaviour is "how fast may it change its mind", which is a turn rate. Putting that on a
    // rigid body means fighting its inertia to express a number the body does not have.
    //
    // ⚠ VELOCITY IS INTEGRATED, the position is never computed as direction x elapsed-time. That shortcut looks
    // identical for a projectile that flies straight and comes apart the moment it steers: the direction is
    // re-multiplied by the WHOLE elapsed time every frame, so a missile that has been flying four seconds and turns
    // ten degrees teleports sideways. Same trap the cloud layer hit.
    //
    // BOOST THEN HOME, because a barrage that homes from the muzzle is six straight lines that happen to converge.
    // Real launches leave the tube on the rail and pitch over afterwards, and it is also what makes the salvo read
    // as six separate missiles: for the boost they fly where their tube pointed, so they fan.
    public partial class SamMissile : Node3D
    {
        public const float BoostTime = 0.42f;      // seconds flying the launch heading before the seeker takes over
        public const float LaunchSpeed = 34f;      // off the rail
        public const float MaxSpeed = 118f;
        public const float Accel = 90f;            // m/s^2 while the motor burns
        public const float TurnRateDeg = 155f;     // seeker authority -- the whole character of the weapon
        public const float MaxLife = 14f;
        public const float FuseRadius = 6.5f;      // proximity fuse: a SAM does not need to touch the airframe
        public const float BlastRadius = 11f;
        public const float BlastDamage = 145f;

        public Vehicle Target;

        Vector3 _vel;
        float _life;
        bool _spent;
        CpuParticles3D _trail;

        public void Fire(Vector3 dir)
        {
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.Up;
            _vel = dir.Normalized() * LaunchSpeed;
            if (_trail != null) _trail.Emitting = true;   // now that the muzzle transform is on the node
        }

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)

            var body = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.72f, 0.74f), Metallic = 0.3f, Roughness = 0.45f };
            AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.11f, Height = 1.5f }, MaterialOverride = body, RotationDegrees = new Vector3(90f, 0f, 0f) });
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.11f, Height = 0.4f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.16f, 0.13f), Roughness = 0.6f },
                RotationDegrees = new Vector3(-90f, 0f, 0f), Position = new Vector3(0f, 0f, -0.95f),
            });

            _trail = new CpuParticles3D
            {
                // Emitting starts FALSE and Fire() turns it on. The missile is positioned AFTER AddChild (the
                // launcher needs the muzzle transform), so a trail already emitting during _Ready would lay its
                // first puffs at the world origin -- world-space particles, so they would just stay there.
                Amount = 44, Lifetime = 0.85, Emitting = false, LocalCoords = false,
                Mesh = new QuadMesh { Size = new Vector2(0.55f, 0.55f) },
                Direction = new Vector3(0f, 0f, 1f), Spread = 8f,
                InitialVelocityMin = 1.5f, InitialVelocityMax = 4f,
                ScaleAmountMin = 0.6f, ScaleAmountMax = 1.5f,
                Gravity = new Vector3(0f, 1.2f, 0f),
                // ⚠ BillboardKeepScale: BillboardMode.Particles silently DROPS ScaleAmount without it, and every
                // puff comes out a 1 m quad -- a trail of dinner plates behind a 1.5 m missile.
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    BillboardKeepScale = true,
                    AlbedoColor = new Color(0.85f, 0.85f, 0.88f, 0.5f),
                },
            };
            AddChild(_trail);
        }

        public override void _ExitTree() => TickHub.RemoveProcess(this);

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready)

        public void HubProcess(double delta)
        {
            if (_spent) return;
            float dt = (float)delta;
            _life += dt;

            bool live = Target != null && IsInstanceValid(Target) && !Target.Exploded;
            if (_life > BoostTime && live)
            {
                // PURSUIT, rate-limited. Rotating the velocity toward the bearing (rather than snapping it) is what
                // makes TurnRateDeg mean anything: a missile that can turn 155 deg/s will still be out-turned by a
                // heli that breaks hard across it late, which is the behaviour worth having.
                var want = (Target.GlobalPosition - GlobalPosition).Normalized();
                var cur = _vel.LengthSquared() > 1e-6f ? _vel.Normalized() : want;
                float ang = cur.AngleTo(want);
                float maxStep = Mathf.DegToRad(TurnRateDeg) * dt;
                // The rotation axis is cur x want, which VANISHES when the two are parallel OR anti-parallel --
                // normalising a zero vector there would hand Rotated a NaN axis and the missile would disappear.
                // Parallel is the ang<=maxStep case; anti-parallel needs any perpendicular, and a 180 deg error
                // means every direction is equally correct.
                var axis = cur.Cross(want);
                if (axis.LengthSquared() < 1e-10f) axis = Mathf.Abs(cur.Y) < 0.9f ? cur.Cross(Vector3.Up) : cur.Cross(Vector3.Right);
                var dir = ang <= maxStep || ang < 1e-5f ? want : cur.Rotated(axis.Normalized(), maxStep);
                _vel = dir * _vel.Length();
            }

            float sp = Mathf.Min(MaxSpeed, _vel.Length() + Accel * dt);
            _vel = (_vel.LengthSquared() > 1e-6f ? _vel.Normalized() : Vector3.Up) * sp;
            GlobalPosition += _vel * dt;                 // integrate; never direction * total-elapsed
            // LookAt throws when the look direction is parallel to the up vector, and a SAM launched at 82 deg
            // is close enough to straight up that a couple of frames of seeker correction can reach it.
            if (_vel.LengthSquared() > 1e-6f)
            {
                var fwd = _vel.Normalized();
                LookAt(GlobalPosition + _vel, Mathf.Abs(fwd.Y) > 0.995f ? Vector3.Forward : Vector3.Up);
            }

            if (live && GlobalPosition.DistanceTo(Target.GlobalPosition) <= FuseRadius) { Detonate(); return; }
            if (_life >= MaxLife || GlobalPosition.Y < -50f) { Detonate(); return; }
        }

        /// <summary>Flash and fireball. Local rather than reaching for PlayerController.SpawnBlastFx, which is
        /// private to a class this weapon has no instance of and no business crediting the kill to.</summary>
        void BlastFx(Vector3 at)
        {
            var host = GetParent();
            if (host == null) return;
            var light = new OmniLight3D { OmniRange = 22f, LightColor = new Color(1f, 0.72f, 0.35f), LightEnergy = 12f, ShadowEnabled = false };
            light.Position = at;
            host.AddChild(light);
            var lt = GetTree()?.CreateTimer(0.22f);
            if (lt != null) lt.Timeout += () => { if (IsInstanceValid(light)) light.QueueFree(); };

            var burst = new CpuParticles3D
            {
                Amount = 60, Lifetime = 1.1, OneShot = true, Explosiveness = 0.95f, LocalCoords = false,
                Mesh = new QuadMesh { Size = new Vector2(1.5f, 1.5f) },
                Direction = Vector3.Up, Spread = 180f,
                InitialVelocityMin = 4f, InitialVelocityMax = 16f,
                ScaleAmountMin = 0.7f, ScaleAmountMax = 2.2f,
                Gravity = new Vector3(0f, -2f, 0f),
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    BillboardKeepScale = true,
                    AlbedoColor = new Color(1f, 0.62f, 0.24f, 0.85f),
                },
                // ⚠ An explosive one-shot emits at the transform it had ENTERING the tree, so the position is set
                // BEFORE AddChild -- written after, the whole burst goes off at the world origin.
                Position = at,
                VisibilityAabb = new Aabb(new Vector3(-40f, -40f, -40f), new Vector3(80f, 80f, 80f)),
            };
            host.AddChild(burst);
            burst.Emitting = true;
            burst.Finished += burst.QueueFree;
        }

        /// <summary>The same blast shape the deployables use -- players, vehicles and deployables inside the radius,
        /// linear falloff -- plus a rotor hit when it goes off right on top of the airframe, because a SAM taking a
        /// rotor off is the outcome the weapon exists for.</summary>
        void Detonate()
        {
            if (_spent) return;
            _spent = true;
            Vector3 p = GlobalPosition;
            var tree = GetTree();
            // The PARENT, not this node: QueueFree() is three lines down, and a one-shot parented to a node that
            // frees itself in the same frame is a blast you never hear.
            GameAudio.Explosion(GetParent() ?? this, p, BlastRadius);
            PlayerRegistry.FlinchAllFromExplosion(p, BlastRadius * 2.4f, 30f);
            BlastFx(p);

            if (tree != null)
            {
                foreach (var n in tree.GetNodesInGroup("players"))
                    if (n is PlayerController pl)
                    {
                        float d = pl.GlobalPosition.DistanceTo(p);
                        if (d <= BlastRadius) pl.TakeDamage(SDG.Unturned.ExplosionMath.Linear(BlastDamage, d, BlastRadius));
                    }
                foreach (var n in tree.GetNodesInGroup("vehicles"))
                    if (n is Vehicle v && !v.Exploded)
                    {
                        float d = v.GlobalPosition.DistanceTo(p);
                        if (d > BlastRadius) continue;
                        v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(BlastDamage, d, BlastRadius));
                        if (v.IsHeli && d <= FuseRadius) v.DamageMainRotor(SDG.Unturned.ExplosionMath.Linear(0.55f, d, FuseRadius));
                    }
            }

            // The node goes, but the trail it already emitted must not vanish with it -- LocalCoords is off, so the
            // puffs are world-space and simply need something to live under for their remaining lifetime.
            if (_trail != null && IsInstanceValid(_trail))
            {
                _trail.Emitting = false;
                var keep = _trail;
                RemoveChild(keep);
                GetParent()?.AddChild(keep);
                keep.GlobalPosition = p;
                var t = tree?.CreateTimer(1.2f);
                if (t != null) t.Timeout += () => { if (IsInstanceValid(keep)) keep.QueueFree(); };
            }
            QueueFree();
        }
    }
}
