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
        // SEEKER AUTHORITY AS A LATERAL ACCELERATION, not a fixed angular rate (strawberry 2026-09-09: "make it
        // possible to evade the missiles"). A flat degrees-per-second is what made it inescapable: it turned just
        // as hard at 118 m/s as at 34, so speed cost it nothing and there was no manoeuvre that beat it. A real
        // seeker is limited by the g it can pull, and rad/s = a / v falls as it accelerates -- so the missile is
        // nimble on the way off the rail and committed by the time it arrives, which is exactly the window a
        // helicopter breaks into. At the 118 m/s cap this is 0.72 rad/s (41 deg/s) and a 164 m turn radius.
        public const float LatAccel = 85f;         // m/s^2, about 8.7 g
        public const float MaxTurnRateDeg = 150f;  // ...and a ceiling for the slow launch phase, so it cannot pirouette off the rail
        // ONCE BEATEN, STAY BEATEN. A missile that sails past and swings round for another go means the break
        // that defeated it did not matter -- with a 14 s life and 118 m/s it has 1.6 km to keep trying, so
        // "evadable" would only mean "delayed". The test is CLOSEST APPROACH, not a frame-to-frame range
        // comparison: a hard break can add 40 m to the range in a single frame, which sails straight past any
        // fixed "am I close AND opening" gate and leaves the seeker happily re-attacking. Having once come within
        // MissRange, opening MissMargin beyond the closest point it ever managed means the pass is over.
        public const float MissRange = 120f;
        public const float MissMargin = 15f;
        public const float MaxLife = 14f;
        public const float FuseRadius = 6.5f;      // proximity fuse: a SAM does not need to touch the airframe
        public const float BlastRadius = 11f;
        public const float BlastDamage = 145f;

        public Vehicle Target;

        // Loaded once for every missile ever fired -- a barrage must not re-parse an .obj six times, and a site
        // that reloads every nine seconds would do it forever.
        static ArrayMesh _rocketMesh; static bool _rocketTried;
        static AudioStream _fireSnd; static bool _fireTried;

        Vector3 _vel;
        float _life;
        bool _spent;
        bool _lost;             // overshot: the seeker is off and this round is now a dumb rocket
        float _minDist = float.MaxValue;   // closest it has ever been to the target
        float _beepT;           // seconds until this missile's next warning beep
        CpuParticles3D _trail;

        // Every missile in the air. The WARNING is per aircraft, not per missile: six rounds each beeping their
        // own rate is a wall of noise that tells the pilot nothing, so only the closest missile to a given target
        // sounds, and its rate is the one that matters anyway.
        static readonly System.Collections.Generic.List<SamMissile> Live = new();

        public void Fire(Vector3 dir)
        {
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.Up;
            _vel = dir.Normalized() * LaunchSpeed;
            if (_trail != null) _trail.Emitting = true;   // now that the muzzle transform is on the node
        }

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            Live.Add(this);

            // THE GAME'S OWN ROCKET, not a primitive of my own (strawberry asked whether this fires the existing
            // projectile). The FLIGHT cannot be the existing one -- the launcher and the tank cannon are Action
            // Rocket rounds that go through SpawnBullet as stepped BALLISTIC bullets with a cosmetic mesh riding
            // along, and a bullet cannot be told to turn, which is the entire job here. But the LOOK and the SOUND
            // are shared retail assets (projectile.prefab: content/rocket_projectile.txt, its olive _Color, and the
            // looping projectile_fire roar the launcher and the cannon both carry), and there is no reason for a
            // SAM round to be the one rocket in the game that looks different. Primitive only if the rip is missing.
            if (_rocketMesh == null && !_rocketTried)
            {
                _rocketTried = true;
                try { _rocketMesh = ContentProvider.ParseObj("res://content/rocket_projectile.txt"); } catch { _rocketMesh = null; }
            }
            if (_rocketMesh != null)
            {
                AddChild(new MeshInstance3D
                {
                    Mesh = _rocketMesh,
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.324f, 0.397f, 0.331f), Roughness = 0.75f, Metallic = 0f },   // projectile.prefab _Color + _Glossiness 0.25
                });
            }
            else
            {
                var body = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.72f, 0.74f), Metallic = 0.3f, Roughness = 0.45f };
                AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.11f, Height = 1.5f }, MaterialOverride = body, RotationDegrees = new Vector3(90f, 0f, 0f) });
                AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.11f, Height = 0.4f },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.16f, 0.13f), Roughness = 0.6f },
                    RotationDegrees = new Vector3(-90f, 0f, 0f), Position = new Vector3(0f, 0f, -0.95f),
                });
            }

            // ...and its roar. Rides the missile, so it dies with the round exactly as it does on a launcher shot.
            if (_fireSnd == null && !_fireTried)
            {
                _fireTried = true;
                string fp = ProjectSettings.GlobalizePath("res://content/projectile_fire.ogg");
                if (System.IO.File.Exists(fp)) { _fireSnd = AudioStreamOggVorbis.LoadFromFile(fp); if (_fireSnd is AudioStreamOggVorbis ov) ov.Loop = true; }
            }
            if (_fireSnd != null) AddChild(new AudioStreamPlayer3D { Stream = _fireSnd, UnitSize = 12f, MaxDistance = 320f, VolumeDb = 2f, Autoplay = true });

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

        public override void _ExitTree() { TickHub.RemoveProcess(this); Live.Remove(this); }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready)

        public void HubProcess(double delta)
        {
            if (_spent) return;
            float dt = (float)delta;
            _life += dt;

            bool live = Target != null && IsInstanceValid(Target) && !Target.Exploded;

            // Has it been beaten? Measured on the RANGE, which is the only thing that says "this pass is over" --
            // an angle test fires early on any hard crossing shot the missile is still winning.
            if (live && !_lost)
            {
                float d = GlobalPosition.DistanceTo(Target.GlobalPosition);
                if (d < _minDist) _minDist = d;
                if (_minDist < MissRange && d > _minDist + MissMargin) _lost = true;
            }

            Warn(live, dt);

            if (_life > BoostTime && live && !_lost)
            {
                // PURSUIT, acceleration-limited. Rotating the velocity toward the bearing (rather than snapping it)
                // is what makes the g-limit mean anything: the faster it goes the wider it has to turn, so a heli
                // that breaks hard and late across the nose gets outside the circle the missile can fly.
                var want = (Target.GlobalPosition - GlobalPosition).Normalized();
                var cur = _vel.LengthSquared() > 1e-6f ? _vel.Normalized() : want;
                float ang = cur.AngleTo(want);
                float sp0 = Mathf.Max(1f, _vel.Length());
                float maxStep = Mathf.Min(LatAccel / sp0, Mathf.DegToRad(MaxTurnRateDeg)) * dt;
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

            if (live && !_lost && GlobalPosition.DistanceTo(Target.GlobalPosition) <= FuseRadius) { Detonate(); return; }
            if (_life >= MaxLife || GlobalPosition.Y < -50f) { Detonate(); return; }
        }

        /// <summary>The cockpit warning: retail's general_beep, played AT the aircraft so anyone aboard hears it
        /// without this having to work out which PlayerController is the local one. The interval is the range --
        /// a second out at 220 m, a tenth of a second when it is about to go off -- so the pilot hears the closure
        /// rate rather than a fact. A missile that has been shaken off stops beeping, which is the whole point of
        /// letting it be shaken off.</summary>
        void Warn(bool live, float dt)
        {
            _beepT -= dt;
            if (!live || _lost || _spent) return;
            float dist = GlobalPosition.DistanceTo(Target.GlobalPosition);
            // Only the closest round to this aircraft sounds. Six overlapping trains at six rates is noise.
            foreach (var o in Live)
            {
                if (o == this || o._spent || o._lost || !ReferenceEquals(o.Target, Target)) continue;
                if (o.GlobalPosition.DistanceTo(Target.GlobalPosition) < dist) return;
            }
            if (_beepT > 0f) return;
            _beepT = Mathf.Clamp(dist / 220f, 0.11f, 1.1f);
            var clip = GameAudio.Pick("misc", "general_beep");
            if (clip != null) GameAudio.PlayAt(GetParent() ?? this, clip, Target.GlobalPosition, 2f, 8f, 140f, Mathf.Lerp(1.25f, 0.95f, Mathf.Clamp(dist / 220f, 0f, 1f)));
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
            Live.Remove(this);
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
