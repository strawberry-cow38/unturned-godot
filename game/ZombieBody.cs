using Godot;

namespace UnturnedGodot
{
    // Zombie AI rewrite -- PHASE 3: the HOT (visible, collidable, killable) zombie. ONLY the few zombies within ~45 m
    // of a player get one of these; everyone else stays WARM/COLD data (docs/ZOMBIE_REDESIGN.md). A CharacterBody3D
    // with the ripped rig, on the ENEMY collision layer (1<<1) so the player's gun ray + melee sweep hit it. Movement
    // is EXTERNALLY driven -- ZombieChunkField sets DesiredVel each frame from the flow field + separation; this body
    // just applies gravity + MoveAndSlide, faces its heading, and shambles. The per-zombie MoveAndSlide that sank the
    // old system is affordable HERE because only the handful near a player ever run it.
    public partial class ZombieBody : CharacterBody3D
    {
        public Vector2 DesiredVel;         // XZ target velocity, set by the field each frame
        public float Health = 100f;
        public bool Dead { get; private set; }
        RiggedCharacter _rig; MeshInstance3D _cap; float _yaw;
        Vector3 _windowPos; float _windowT, _escapeT; bool _posInit;   // unstuck: slide a straggler along a wall it's pinned on

        public override void _Ready()
        {
            CollisionLayer = 1 << 1;        // enemy -- the gun ray + melee mask this bit
            CollisionMask = 1 << 0;         // ground+buildings ONLY. Agent-vs-agent is SOFT boids separation, NOT hard collision:
                                            // hard collision JAMS a horde against a wall at a bottleneck (a corner) -> permanently
                                            // stuck zombies (nothing can free a body pinned on all sides). Separation still keeps them
                                            // "aware of each other" (steer apart) but lets them squeeze past under pressure instead of pinning.
            FloorMaxAngle = Mathf.DegToRad(55f); FloorSnapLength = 0.5f;
            AddToGroup("zombies");
            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.4f } };
            shape.Position = new Vector3(0f, 0.9f, 0f);
            AddChild(shape);

            int atlas = (int)(GetInstanceId() % 6u);   // vary the outfit so a horde isn't a uniform
            _rig = RiggedCharacter.Build("res://content/rig.json", Colors.White, false, $"res://content/zombie_atlas_{atlas}.png", "res://content/face_19.png");
            if (_rig != null)
            {
                _rig.UsePhysicsAnimRate();   // pose the skeleton at 50 Hz, not the render rate (the old POI CPU spike)
                _rig.WalkClip = "Move_" + (atlas % 4); _rig.IdleClip = "Idle_" + (atlas % 4); _rig.RunClip = _rig.WalkClip;
                AddChild(_rig);
                _rig.Play(_rig.WalkClip);
            }
            else
            {
                _cap = new MeshInstance3D { Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.40f, 0.60f, 0.35f) }, Position = new Vector3(0f, 0.9f, 0f) };
                AddChild(_cap);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (Dead) return;
            float dt = (float)delta;

            // UNSTUCK: a straggler can park against a wall (or its corner) where the flow points into the face and the
            // tangential part is ~0. When that happens, SLIDE ALONG the wall we're actually touching, in whichever tangent
            // direction heads toward the target -- from the real contact normal, so it's deterministic, not a guess.
            Vector2 drive = DesiredVel;
            if (_escapeT > 0f && DesiredVel.LengthSquared() > 0.04f)
            {
                _escapeT -= dt;
                Vector3 nAcc = Vector3.Zero;
                for (int i = 0; i < GetSlideCollisionCount(); i++) nAcc += GetSlideCollision(i).GetNormal();
                var n2 = new Vector2(nAcc.X, nAcc.Z);
                if (n2.LengthSquared() > 0.01f)
                {
                    n2 = n2.Normalized();
                    var tan = new Vector2(-n2.Y, n2.X);
                    if (tan.Dot(DesiredVel) < 0f) tan = -tan;   // pick the along-wall direction that heads toward the target
                    drive = (tan * 0.9f + DesiredVel.Normalized() * 0.1f).Normalized() * DesiredVel.Length();
                }
            }

            var v = Velocity;
            v.X = drive.X; v.Z = drive.Y;
            if (IsOnFloor()) v.Y = 0f; else v.Y -= 22f * dt;   // gravity
            Velocity = v;
            MoveAndSlide();

            // stuck bookkeeping (after the move): did we actually travel? If we wanted to and barely did, count it; past a
            // short grace, fire an escape burst. Escaping itself doesn't re-trigger (guarded), and alternates side so a
            // dead-end on one side tries the other next time.
            if (!_posInit) { _windowPos = GlobalPosition; _posInit = true; }
            _windowT += dt;
            if (_windowT >= 1.0f)   // progress over a WINDOW, not per-tick: a zombie OSCILLATING against a wall moves every
            {                       // tick but nets ~0, so a per-tick check never fires. <0.5 m of real progress in 1 s = stuck.
                float progress = new Vector2(GlobalPosition.X - _windowPos.X, GlobalPosition.Z - _windowPos.Z).Length();
                if (_escapeT <= 0f && DesiredVel.LengthSquared() > 0.2f && progress < 0.5f) _escapeT = 1.0f;
                _windowPos = GlobalPosition; _windowT = 0f;
            }

            if (drive.LengthSquared() > 0.04f)                  // face the heading. Rig forward is -Z, so RotateY(yaw)*(-Z)
            {                                                   // = dir needs yaw = atan2(-x,-z). VERIFIED forward via --zface (arms/face point at travel).
                float want = Mathf.Atan2(-drive.X, -drive.Y);
                _yaw = Mathf.LerpAngle(_yaw, want, 1f - Mathf.Exp(-10f * dt));
                Rotation = new Vector3(0f, _yaw, 0f);           // rotate the BODY; the rig (child, forward -Z) follows
            }
            // idle when stopped, shamble when moving -- at the clip's OWN 1x pace. Master: DON'T speed up the anim; instead
            // ZombieSpeed (ZombieChunkField) is tuned DOWN to the shamble clip's natural stride so the feet don't skate.
            if (_rig != null) _rig.SetLocomotion(new Vector2(Velocity.X, Velocity.Z).Length());
        }

        // PHASE 3b wires the gun/melee hit into this. Present now so ZombieChunkField can retire a dead body cleanly.
        public void Damage(float amount, Vector3 from)
        {
            if (Dead) return;
            Health -= amount;
            if (Health <= 0f) Die(from);
        }

        void Die(Vector3 from)
        {
            Dead = true;
            RemoveFromGroup("zombies");
            CollisionLayer = 0;   // stop blocking / stop being shot again
            if (_rig != null) _rig.RagdollStart((GlobalPosition - from).Normalized() * 6f + Vector3.Up * 2f);
            var t = GetTree().CreateTimer(8.0);   // let the corpse lie, then clean up
            t.Timeout += QueueFree;
        }
    }
}
