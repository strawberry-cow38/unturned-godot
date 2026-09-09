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
        // 11.2 g. Was 85 (8.7 g), which could not fly the shot it claimed to (strawberry's SAM is meant to hit;
        // tinyclaw found vehicle.sam_site red on main). A missile launched 45 deg off the bearing closed from
        // 153 m to 28 m and then sailed past: pure pursuit needs a turn rate that grows as 1/range, so the
        // heading error GREW as it closed -- 34 deg at 92 m, 46 at 45 m, 76 at 29 -- until it swung wide and the
        // miss rule correctly called the pass over.
        //
        // Simulated the alternatives rather than nudging it: proportional navigation changes NOTHING here (both
        // laws saturate at the g-limit, and a saturated missile turns the same however it decided to), so this
        // was never a guidance-law problem -- it was simply not agile enough. 110 closes that shot at 5.7 m.
        //
        // ...and it does NOT cost evadability, which is the paired requirement ("make it possible to evade the
        // missiles"). The hard 90 deg break at 45 m still beats it by 44.1 m -- the same margin as at 85, and
        // unchanged all the way up to 170, because a 70 m jump at that range is inside any turn radius this
        // missile can fly. Agility and dodgeability are not on the same axis here; the break wins on geometry.
        public const float LatAccel = 110f;        // m/s^2, about 11.2 g
        public const float MaxTurnRateDeg = 150f;  // ...and a ceiling for the slow launch phase, so it cannot pirouette off the rail
        // ONCE BEATEN, STAY BEATEN. A missile that sails past and swings round for another go means the break
        // that defeated it did not matter -- with a 14 s life and 118 m/s it has 1.6 km to keep trying, so
        // "evadable" would only mean "delayed". The test is CLOSEST APPROACH, not a frame-to-frame range
        // comparison: a hard break can add 40 m to the range in a single frame, which sails straight past any
        // fixed "am I close AND opening" gate and leaves the seeker happily re-attacking. Having once come within
        // MissRange, opening MissMargin beyond the closest point it ever managed means the pass is over.
        public const float MissRange = 120f;
        public const float MissMargin = 15f;
        // WARNING CADENCE (strawberry 2026-09-09: "increase frequency of beeps by a LOT. when its close it should
        // be SPAMMING"). Interval = dist / BeepRange, clamped. That is roughly half a second out at the edge of a
        // launch and 22 beeps a SECOND inside 22 m -- past the point where separate beeps are audible as separate
        // beeps, which is the intent: at that range it is not a warning any more, it is the last second of one.
        public const float BeepRange = 500f, BeepFastest = 0.045f, BeepSlowest = 0.6f;
        public const float MaxLife = 14f;
        public const float FuseRadius = 6.5f;      // proximity fuse: a SAM does not need to touch the airframe
        public const float BlastRadius = 11f;
        public const float BlastDamage = 145f;
        // WHAT IT CAN HIT (strawberry 2026-09-09: "make sure the missiles cant noclip through props"). The same
        // layers StepBullets sweeps, minus the two the zombie removal left behind: world (bit0, terrain AND tree
        // trunks), vehicles (bit5), props (bit6), water surface (bit9).
        public const uint HitMask = (1u << 0) | (1u << 5) | (1u << 6) | (1u << 9);

        public Vehicle Target;
        /// <summary>The launcher's own body, excluded from the sweep. Now that a site IS a collider, the first
        /// metre of every flight leaves through it.</summary>
        public Rid Ignore;

        // Loaded once for every missile ever fired -- a barrage must not re-parse an .obj six times, and a site
        // that reloads every nine seconds would do it forever.
        static ArrayMesh _rocketMesh; static bool _rocketTried;

        /// <summary>LAY THE ROCKET DOWN (strawberry 2026-09-09: "rockets r broken everywhere they are used. they
        /// sit perfectly vertical instead of pointing the durection of flight"). content/rocket_projectile.txt is
        /// authored STANDING UP -- measured off the mesh: it spans 0.72 in Y against 0.23 in X and Z, and the
        /// tapered end (17 verts at y +0.2247, radius 0.047) is the nose while the motor sits at y -0.4956.
        ///
        /// Both users point the NODE down the velocity with LookAt, which aims its -Z; the mesh child was never
        /// turned to match, so the model stayed upright no matter where the round was going. Rx(-90) carries +Y
        /// onto -Z, nose first. The fallback cylinder below wants the same lay-down and has always had it, which
        /// is the tell that the real mesh was simply missed.</summary>
        public static readonly Vector3 RocketMeshFix = new(-90f, 0f, 0f);
        static AudioStream _fireSnd; static bool _fireTried;

        Vector3 _vel;
        float _life;
        bool _spent;
        bool _lost;             // overshot: the seeker is off and this round is now a dumb rocket
        float _minDist = float.MaxValue;   // closest it has ever been to the target
        PhysicsRayQueryParameters3D _sweep;   // reused: one allocation per missile, not one per frame
        /// <summary>Has it gone off? The suite reads it, because "detonated on the airframe" and "flew past" are
        /// the two outcomes worth telling apart and a distance alone cannot.</summary>
        public bool Spent => _spent;
        float _beepT;           // seconds until this missile's next warning beep
        CpuParticles3D _trail;

        // Every missile in the air. The WARNING is per aircraft, not per missile: six rounds each beeping their
        // own rate is a wall of noise that tells the pilot nothing, so only the closest missile to a given target
        // sounds, and its rate is the one that matters anyway.
        static readonly System.Collections.Generic.List<SamMissile> Live = new();

        /// <summary>Is a live round already warning this aircraft? The SITE asks, so its slower lock tone can get
        /// out of the way -- a tracking beep and a closure beep sounding together is two clocks in one cockpit,
        /// and the pilot cannot read either.</summary>
        /// <summary>Flares: break every round currently homing on this aircraft. Called by Flares.Deploy rather
        /// than left for each missile to notice next tick, so the warning tone stops on the frame the button is
        /// pressed -- that silence IS the feedback that it worked.</summary>
        public static void Decoy(Vehicle target)
        {
            if (target == null) return;
            foreach (var m in Live) if (ReferenceEquals(m.Target, target)) m._lost = true;
        }

        public static bool AnyWarning(Vehicle target)
        {
            if (target == null) return false;
            foreach (var m in Live) if (!m._spent && !m._lost && ReferenceEquals(m.Target, target)) return true;
            return false;
        }

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
                    RotationDegrees = RocketMeshFix,
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
            if (live && Target.Flared) _lost = true;   // flared mid-flight (a second salvo, or one fired after launch)

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
            // SWEPT, NOT TELEPORTED. At 118 m/s a frame is nearly 2 m, so a missile advanced by a bare position
            // add walks straight through a wall thinner than its own step -- and every prop in this game is
            // thinner than 2 m. Raycast the segment it is about to cross and go off on the first thing in it.
            var from = GlobalPosition;
            var to = from + _vel * dt;                   // integrate; never direction * total-elapsed
            var space = GetWorld3D()?.DirectSpaceState;
            if (space != null)
            {
                _sweep ??= new PhysicsRayQueryParameters3D { CollisionMask = HitMask };
                if (Ignore.IsValid && _sweep.Exclude.Count == 0) _sweep.Exclude = new Godot.Collections.Array<Rid> { Ignore };
                _sweep.From = from; _sweep.To = to;
                var swept = space.IntersectRay(_sweep);
                if (swept.Count > 0)
                {
                    GlobalPosition = (Vector3)swept["position"];
                    Detonate();   // a hull strike, a tree, a wall or the ground -- all the same answer
                    return;
                }
            }
            GlobalPosition = to;
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
        /// without this having to work out which PlayerController is the local one. The interval is the RANGE, so
        /// the pilot hears the closure rate rather than a fact -- and it ends in a solid stutter rather than a
        /// countdown, because the useful signal at 20 m is "now". A missile that has been shaken off stops
        /// beeping, which is the whole point of letting it be shaken off.</summary>
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
            _beepT = Mathf.Clamp(dist / BeepRange, BeepFastest, BeepSlowest);
            var clip = GameAudio.Pick("misc", "general_beep");
            // Pitch climbs with closure too. The rate is the information, but a rising tone is what makes it read
            // as panic rather than as a metronome running fast.
            float near01 = 1f - Mathf.Clamp(dist / BeepRange, 0f, 1f);
            if (clip != null) GameAudio.PlayAt(GetParent() ?? this, clip, Target.GlobalPosition, 3f, 8f, 160f, Mathf.Lerp(0.95f, 1.45f, near01));
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
                foreach (var n in tree.GetNodesInGroup("samsites"))
                    if (n is SamSite sam && !sam.Destroyed)
                    {
                        float d = sam.GlobalPosition.DistanceTo(p);
                        if (d <= BlastRadius) sam.TakeDamage(SDG.Unturned.ExplosionMath.Linear(BlastDamage, d, BlastRadius));
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
