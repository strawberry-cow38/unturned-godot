using Godot;
using SDG.Unturned;   // EPlayerStance + PlayerMovementDef.GRAVITY

namespace UnturnedGodot
{
    // Source-accurate zombie AI ported from SDG.Unturned.Zombie. A zombie IDLES until it SENSES the player
    // (AlertTool: within the player's stance-based stealth radius, not tucked behind the zombie's back while
    // the player sneaks, and with line of sight), then HUNTS along an approach path chosen from the player's
    // agro count -- every 3rd zombie RUSHes straight, the rest peel LEFT/RIGHT and flankers swing wide, so a
    // horde fans out to surround you. In range it swings on a ~1 s cadence with the hit landing mid-swing. It
    // gives up if the player dies or breaks 64 m. Body = the real ripped character mesh; death drops a physics
    // ragdoll. Not yet ported: point-investigation of noises (gunshots) + wander-home on leave.
    public partial class ZombieController : CharacterBody3D
    {
        public enum EPath { RUSH, LEFT, RIGHT, LEFT_FLANK, RIGHT_FLANK }   // Zombie.EZombiePath
        public enum ESpeciality { NORMAL, SPRINTER, CRAWLER, FLANKER }     // Zombie.EZombieSpeciality (subset)

        public Node3D Target;
        public ESpeciality Speciality = ESpeciality.NORMAL;
        [Export] public float Speed = 5.5f;         // overwritten from Speciality in _Ready (Zombie seeker.Speed)
        [Export] public float Health = 100f;
        [Export] public float AttackDamage = 15f;   // LevelZombies.tables[type].damage (map data) x the mults below
        [Export] public float ImpactForce = 9f;

        public bool Dead { get; private set; }

        // Attack ranges (Zombie.GetHorizontalAttackRangeSquared / GetVerticalAttackRange, client + normal):
        const float ATTACK_PLAYER_SQ = 2f;   // ATTACK_PLAYER; horizontal reach = sqrt(2) ~ 1.41 m
        const float VERTICAL_ATTACK = 2.1f;
        const float ATTACK_TIME = 0.5f;      // Attack_0 clip-length fallback (Zombie.attackTime); dmg at half
        const float LEAVE_SQ = 4096f;        // 64 m: the player has broken away (Zombie.tick)

        Node3D _body;
        RiggedCharacter _rig;
        StandardMaterial3D _capMat;
        readonly RandomNumberGenerator _rng = new();
        int _atkId, _startleId;
        bool _ragdoll;

        // AI state (Zombie.cs)
        enum EHunt { NONE, PLAYER, POINT }   // Zombie.EHuntType (+ NONE = idle, not hunting)
        EHunt _hunt = EHunt.NONE;
        Vector3 _huntPoint;         // POINT target: where a noise (gunshot) came from
        float _lastHunted;          // when the current POINT alert fired (Zombie.lastHunted); ~3 s later -> give up
        bool _startled, _isAttacking;
        EPath _path;
        double _age;                // local clock (Time.time analogue)
        float _lastAttack = -100f;  // last swing START (Zombie.lastAttack); the hit lands at +ATTACK_TIME/2

        public override void _Ready()
        {
            AddToGroup("zombies");
            CollisionLayer = 1 << 1;   // enemy bit the gun ray masks for
            CollisionMask = 1 << 0;    // collide with ground

            Speed = Speciality switch  // Zombie.updateStates seeker.Speed (non-slow-movement defaults)
            {
                ESpeciality.SPRINTER => 6.5f,
                ESpeciality.CRAWLER => 3f,
                ESpeciality.FLANKER => 6f,
                _ => 5.5f,
            };
            _rng.Randomize();

            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.4f } };
            shape.Position = new Vector3(0, 0.9f, 0);
            AddChild(shape);

            _rig = RiggedCharacter.Build("res://content/rig.json", new Color(0.45f, 0.72f, 0.40f));
            if (_rig != null)
            {
                // Zombie.cs: moveAnim="Move_"+move (the arms-out shamble, NOT the human walk), idleAnim="Idle_"+idle.
                // Move_0..3 = upright shambles; Move_4/5 = the CRAWLER variant. Match the clip to the speciality.
                bool crawler = Speciality == ESpeciality.CRAWLER;
                _rig.WalkClip = crawler ? "Move_" + _rng.RandiRange(4, 5) : "Move_" + _rng.RandiRange(0, 3);
                _rig.RunClip = _rig.WalkClip;             // zombies don't run; shamble at any speed
                _rig.IdleClip = "Idle_" + _rng.RandiRange(0, 3);
                _startleId = _rng.RandiRange(0, 1);
                _atkId = _rng.RandiRange(0, 2);
                _body = _rig;
                _rig.Play(_rig.IdleClip);
            }
            else if (CharacterModel.Loaded) { _body = CharacterModel.Build(new Color(0.55f, 0.95f, 0.55f)); }
            else
            {
                _capMat = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.30f) };
                var mi = new MeshInstance3D { Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f }, MaterialOverride = _capMat, Position = new Vector3(0, 0.9f, 0) };
                _body = new Node3D(); _body.AddChild(mi);
            }
            AddChild(_body);
        }

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
                    Vector3 f = (away * 6f + Vector3.Up * 8f + new Vector3(_rng.RandfRange(-16f, 16f), 0f, _rng.RandfRange(-16f, 16f))) * 0.64f;
                    _rig.RagdollStart(f);
                    if (impact) _rig.ApplyImpact(point, dir * ImpactForce);   // shove at the exact bone hit
                    _ragdoll = true;
                }
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            float g = PlayerMovementDef.GRAVITY;
            float dt = (float)delta;

            if (Dead)
            {
                if (_ragdoll) return;   // physics ragdoll drives the body now
                Velocity = new Vector3(0, Velocity.Y - g * dt, 0);
                MoveAndSlide();
                return;
            }
            if (Target is not PlayerController player) return;
            _age += delta;

            // --- idle: stand still and try to sense the player (AlertTool) ---
            if (_hunt == EHunt.NONE)
            {
                Velocity = new Vector3(0, Velocity.Y - g * dt, 0);
                MoveAndSlide();
                _rig?.Tick(delta);
                _rig?.SetLocomotion(0f);
                TrySense(player);
                return;
            }

            // --- POINT: investigate a noise; sight can still promote it to a full player hunt ---
            if (_hunt == EHunt.POINT)
            {
                TrySense(player);                                   // may set _hunt = EHunt.PLAYER
                if (_hunt == EHunt.POINT) { TickPoint(g, dt); return; }
            }

            // --- PLAYER hunt: give up if the player died or broke away (Zombie.tick leave) ---
            Vector3 me = GlobalPosition, pp = player.GlobalPosition;
            float num3 = HDistSq(pp, me);
            float num4 = Mathf.Abs(pp.Y - me.Y);
            if (player.Health <= 0f || num3 > LEAVE_SQ) { _hunt = EHunt.NONE; return; }

            // --- pick the approach point for this path (Zombie.tick banded flanking) ---
            Vector3 pFwd = Flat(-player.GlobalTransform.Basis.Z);
            Vector3 pRight = Flat(player.GlobalTransform.Basis.X);
            Vector3 zFwd = Flat(-GlobalTransform.Basis.Z);
            Vector3 zRight = Flat(GlobalTransform.Basis.X);
            bool inFront = num3 > 20f || (me - pp).Normalized().Dot(pFwd) > 0f;   // past 4.5 m, or ahead of the player
            Vector3 tp = pp;
            switch (_path)
            {
                case EPath.RUSH:  if (num3 > 4f) tp -= zFwd; break;
                case EPath.LEFT:  if (num3 > 4f) tp -= zRight; break;
                case EPath.RIGHT: if (num3 > 4f) tp += zRight; break;
                case EPath.LEFT_FLANK:
                    if (num3 > 100f) tp += pRight * 9f + pFwd * -4f;    // far: swing wide to the player's right
                    else if (inFront) tp += pRight * 3f + pFwd * -3f;   // mid / in-view: ease onto the flank
                    else if (num3 > 4f) tp -= pFwd;                     // behind + close: fall in behind
                    break;
                case EPath.RIGHT_FLANK:
                    if (num3 > 100f) tp += pRight * -9f + pFwd * -4f;
                    else if (inFront) tp += pRight * -3f + pFwd * -3f;
                    else if (num3 > 4f) tp -= pFwd;
                    break;
            }

            // --- steer toward the point until within reach, then plant and swing. In the source the seeker
            // moves every tick regardless of the derived isMoving flag; only entering attack range stops it, so
            // gating movement on isMoving (as a first pass did) leaves a dead zone short of the swing range. ---
            Vector3 to = tp - me; to.Y = 0f;
            bool inReach = num3 < ATTACK_PLAYER_SQ;
            Vector3 horiz = (!inReach && to.LengthSquared() > 1e-4f) ? to.Normalized() * Speed : Vector3.Zero;
            Velocity = new Vector3(horiz.X, Velocity.Y - g * dt, horiz.Z);
            MoveAndSlide();

            // --- attack in range: ~1 s cadence, the hit lands mid-swing (Zombie.tick + askAttack) ---
            bool swinging = false;
            if (num3 < ATTACK_PLAYER_SQ && num4 < VERTICAL_ATTACK)
            {
                if (_isAttacking)
                {
                    if (_age - _lastAttack > ATTACK_TIME / 2f)
                    {
                        _isAttacking = false;
                        float mult = Speciality == ESpeciality.CRAWLER ? 2f : Speciality == ESpeciality.SPRINTER ? 0.75f : 1f;
                        player.TakeDamage(AttackDamage * mult);
                    }
                }
                else if (_age - _lastAttack > 1f)
                {
                    _isAttacking = true; swinging = true;
                    _lastAttack = (float)_age;             // askAttack: lastAttack = now
                    _rig?.PlayOnce("Attack_" + _atkId);
                }
            }
            else _isAttacking = false;

            // --- animation + facing (CanTurn: face where we move while approaching, the player when close) ---
            if (_rig != null)
            {
                _rig.Tick(delta);
                if (!_startled) { _startled = true; _rig.PlayOnce("Startle_" + _startleId); }
                else if (!_isAttacking && !swinging) _rig.SetLocomotion(horiz.Length());
            }
            Vector3 faceDir = num3 > 4f && horiz.LengthSquared() > 1e-4f ? horiz : Flat(pp - me);
            if (faceDir.LengthSquared() > 1e-4f)
                LookAt(me + faceDir, Vector3.Up);
        }

        // AlertTool.check + line-of-sight: sense the player only within their stealth radius, not behind the
        // zombie's back while they sneak (anything but sprinting counts as sneaking), and with clear sight.
        void TrySense(PlayerController player)
        {
            if (player.Health <= 0f) return;
            Vector3 offset = GlobalPosition - player.GlobalPosition;   // player -> zombie
            float radius = player.GetStealthDetectionRadius();
            if (offset.LengthSquared() > radius * radius) return;
            bool sneak = player.Stance != EPlayerStance.SPRINT;
            Vector3 fwd = -GlobalTransform.Basis.Z;
            if (offset.LengthSquared() > 1e-4f && sneak && fwd.Normalized().Dot(offset.Normalized()) > 0.5f) return;
            if (!HasLineOfSight(player)) return;
            Alert(player);
        }

        // AlertTool raycast: eye-height ray toward the player over 95 % of the gap; any world geometry blocks it.
        bool HasLineOfSight(PlayerController player)
        {
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = GlobalPosition + Vector3.Up * 1.0f;
            Vector3 toP = (player.GlobalPosition + Vector3.Up * 1.0f) - from;
            var q = PhysicsRayQueryParameters3D.Create(from, from + toP * 0.95f);
            q.CollisionMask = 1u << 0;   // world geometry only (ground/props) = BLOCK_VISION
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            return space.IntersectRay(q).Count == 0;
        }

        // Zombie.alert(player): latch onto the player and choose an approach path from their agro count.
        void Alert(PlayerController player)
        {
            _hunt = EHunt.PLAYER;
            _startled = false;    // replay the startle roar on the first hunt tick
            if (Speciality == ESpeciality.FLANKER)
                _path = _rng.Randf() < 0.5f ? EPath.LEFT_FLANK : EPath.RIGHT_FLANK;
            else if (player.Agro % 3 == 0)
                _path = EPath.RUSH;
            else
                _path = _rng.Randf() < 0.5f ? EPath.LEFT : EPath.RIGHT;
            player.Agro++;
        }

        // Zombie.tick with huntType POINT + no player: shamble to the noise, then give up ~3 s after arriving.
        void TickPoint(float g, float dt)
        {
            Vector3 me = GlobalPosition;
            float num3 = HDistSq(_huntPoint, me);
            bool arrived = num3 < 3f;                                      // Zombie isMoving = num3 > 3 (~1.73 m)
            if (arrived && _age - _lastHunted > 3f) { _hunt = EHunt.NONE; return; }   // stop()

            Vector3 to = _huntPoint - me; to.Y = 0f;
            Vector3 horiz = (!arrived && to.LengthSquared() > 1e-4f) ? to.Normalized() * Speed : Vector3.Zero;
            Velocity = new Vector3(horiz.X, Velocity.Y - g * dt, horiz.Z);
            MoveAndSlide();

            if (_rig != null)
            {
                _rig.Tick(dt);
                if (!_startled) { _startled = true; _rig.PlayOnce("Startle_" + _startleId); }
                else _rig.SetLocomotion(horiz.Length());
            }
            if (horiz.LengthSquared() > 1e-4f) LookAt(me + horiz, Vector3.Up);
        }

        // Zombie.alert(Vector3, isStartling): investigate a noise. Can't override an active player hunt; a closer
        // noise just re-points the target (keeping the existing give-up clock); NONE -> POINT starts a fresh clock.
        void AlertPoint(Vector3 point)
        {
            if (_hunt == EHunt.PLAYER) return;
            if (_hunt == EHunt.NONE)
            {
                _hunt = EHunt.POINT; _startled = false;
                _huntPoint = point; _lastHunted = (float)_age;
            }
            else if (HDistSq(point, GlobalPosition) < HDistSq(_huntPoint, GlobalPosition))
                _huntPoint = point;
        }

        // Broadcast from PlayerController.Fire (AlertTool.alert point-noise): a gunshot within earshot pulls the
        // zombie over to investigate where it came from -- and once there, normal sight tends to spot the player.
        public void OnGunshot(Vector3 pos, float radius)
        {
            if (Dead) return;
            if ((pos - GlobalPosition).LengthSquared() < radius * radius) AlertPoint(pos);
        }

        static Vector3 Flat(Vector3 v) { v.Y = 0f; return v.LengthSquared() > 1e-6f ? v.Normalized() : v; }
        static float HDistSq(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
    }
}
