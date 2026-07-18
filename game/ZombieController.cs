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
        // Zombie.EZombieSpeciality survival roster: FLANKER = FLANKER_STALK (invisible while hunting), BURNER =
        // fire-explodes on death, ACID = spits acid at range. (MEGA / SPIRIT / bosses = arena/beacon, later.)
        public enum ESpeciality { NORMAL, SPRINTER, CRAWLER, FLANKER, BURNER, ACID }

        public Node3D Target;
        public ESpeciality Speciality = ESpeciality.NORMAL;
        // MP Phase 5 (MP_PLAN §3.5): the brain/puppet split. A PUPPET is a client-side replica -- it skips
        // AI/nav/physics entirely (its _PhysicsProcess returns immediately) and is driven by ZombiePuppets
        // calling PuppetFrame with the replicated transform + anim byte; the rig/anim layer renders as-is.
        // SP zombies are never puppets, so the default path is untouched.
        public bool IsPuppet;
        public int SwingSeq { get; private set; }              // increments at swing START (ZombieNetSync -> AttackSwing event)
        public bool IsAttackSwinging => _isAttacking;          // mid-swing (drives the replicated anim byte)
        [Export] public float Speed = 5.5f;         // overwritten from Speciality in _Ready (Zombie seeker.Speed)
        [Export] public float Health = 100f;
        [Export] public float AttackDamage = 15f;   // LevelZombies.tables[type].damage (map data) x the mults below
        [Export] public float ImpactForce = 9f;
        Color _tint;                // per-speciality body colour (stand-in for the real ZombieClothing skins)
        float _nextSpit;            // ACID: next allowed spit time (Zombie specialUseDelay, Random 4-8 s)
        // real zombie sounds (core.masterbundle Sounds/Zombies): roar on startle/attack/while-moving, occasional
        // idle groan, spit on acid (Zombie.cs: askStartle/askAttack/OnUpdate groan-timer/askAcid).
        static AudioStream[] _roars, _groans, _spits;
        AudioStreamPlayer3D _audio;
        float _nextGroan;

        public bool Dead { get; private set; }

        // Attack ranges (Zombie.GetHorizontalAttackRangeSquared / GetVerticalAttackRange, client + normal):
        const float ATTACK_PLAYER_SQ = 2f;   // ATTACK_PLAYER; horizontal reach = sqrt(2) ~ 1.41 m
        const float ATTACK_VEHICLE = 16f;     // Zombie.ATTACK_VEHICLE: swipe damage to the car a target player is driving (vs AttackDamage to a player)
        const float VERTICAL_ATTACK = 2.1f;
        const float ATTACK_TIME = 0.8f;      // Zombie.attackTime = Attack_0 clip length (0.8s); dmg lands at half (0.4s)
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
        Vector3 _home; bool _homeSet;   // spawn point, to wander back to on leave (Zombie isLeaving)
        // Phase 2 nav rework (master): real NavigationAgent3D pathfinding (no beeline) + vision cone + investigate-last-seen.
        NavigationAgent3D _nav;
        Vector3 _lastSeen; bool _hasLastSeen;   // where the player was last SEEN -> go investigate there when line-of-sight breaks
        [Export] public float SightRange = 20f;         // vision-cone range (m); the player SNEAKING shrinks it
        [Export] public float SightHalfAngleDeg = 60f;  // vision-cone half-angle from the eyes
        float _repathAcc;   // throttle agent re-target (perf: don't SetTargetPosition every single tick)
        float _lostSightAcc;   // seconds since we last saw the player mid-chase -> after a grace, go investigate last-seen
        // Phase 3 hearing (master): a hearing SPHERE + per-emitter loudness -> path to the LOUDEST+CLOSEST sound heard.
        [Export] public float HearingRange = 48f;   // the zombie's ears (m): a sound is heard only within this sphere AND if its loudness carries that far
        Vector3 _heardPos; float _heardSalience;    // best (loudest+closest) sound heard since the last tick; salience = loudness - dist
        float _huntSalience;   // salience of the sound we're CURRENTLY investigating -> STAY ON TASK; only a strictly LOUDER+CLOSER sound overrides it (no re-agro on every footstep). Resets when the hunt ends / goes idle.

        public override void _Ready()
        {
            if (!IsPuppet) AddToGroup("zombies");   // puppets stay out of the group: no local combat/hearing/sync interactions (server damage arrives via the wire)
            CollisionLayer = 1 << 1;   // enemy bit the gun ray masks for
            CollisionMask = 1 << 0;    // collide with ground

            LoadSounds();
            _audio = new AudioStreamPlayer3D { MaxDistance = 45f, UnitSize = 5f };   // positional zombie audio
            AddChild(_audio);
            _nextGroan = 2f + GD.Randf() * 6f;   // stagger the first groan

            Speed = Speciality switch  // Zombie.updateStates seeker.Speed (non-slow-movement defaults)
            {
                ESpeciality.SPRINTER => 6.5f,
                ESpeciality.CRAWLER => 3f,
                ESpeciality.FLANKER => 6f,
                _ => 5.5f,
            };
            // the tint now MULTIPLIES the baked ZombieClothing skin atlas (skin + shirt + pants) -> NORMAL
            // zombies show natural; the visually-distinct specials get a colour accent (burner charred, acid toxic).
            _tint = Speciality switch
            {
                ESpeciality.BURNER => new Color(1.0f, 0.58f, 0.40f),    // charred / hot
                ESpeciality.ACID => new Color(0.72f, 1.0f, 0.45f),     // toxic green
                ESpeciality.FLANKER => new Color(0.75f, 0.88f, 0.92f), // pale, cold stalker
                _ => Colors.White,                                     // NORMAL / SPRINTER / CRAWLER: natural
            };
            _rng.Randomize();

            // crawlers hug the ground (Move_4/5 crawl clips) -> a short low collider; everyone else stands tall.
            bool low = Speciality == ESpeciality.CRAWLER;
            var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Height = low ? 0.8f : 1.8f, Radius = 0.4f } };
            shape.Position = new Vector3(0, low ? 0.4f : 0.9f, 0);
            AddChild(shape);
            FloorMaxAngle = Mathf.DegToRad(55f); FloorSnapLength = 0.5f;   // climb steeper slopes + stay grounded, like the player (master)
            if (!IsPuppet)   // a puppet never paths -- keep replica worlds off the NavigationServer entirely
            {
                _nav = new NavigationAgent3D { PathDesiredDistance = 0.8f, TargetDesiredDistance = 1.3f, Radius = 0.4f, Height = 1.8f, AvoidanceEnabled = false, PathMaxDistance = 30f };
                AddChild(_nav);   // paths on the pre-baked pocket navmesh (ZombieNav) -> routes around buildings instead of beelining
            }

            // each zombie randomly wears one of the baked ZombieClothing outfits (real zombies randomise their
            // shirt/pants from the map's LevelZombies table) so the horde isn't a uniform.
            string atlas = $"res://content/zombie_atlas_{_rng.RandiRange(0, 5)}.png";
            _rig = RiggedCharacter.Build("res://content/rig.json", _tint, false, atlas, "res://content/face_19.png");
            if (_rig != null)
            {
                _rig.UsePhysicsAnimRate();   // perf: shamble the skeleton at 50 Hz, not the render rate (a POI of zombie rigs at 280fps was the CPU spike)
                // Zombie.cs: moveAnim="Move_"+move (the arms-out shamble, NOT the human walk), idleAnim="Idle_"+idle.
                // Move_0..3 = upright shambles; Move_4/5 = the CRAWLER variant. Match the clip to the speciality.
                bool crawler = Speciality == ESpeciality.CRAWLER;
                _rig.WalkClip = crawler ? "Move_" + _rng.RandiRange(4, 5) : "Move_" + _rng.RandiRange(0, 3);
                _rig.RunClip = _rig.WalkClip;             // zombies don't run; shamble at any speed
                // A crawler must NOT stand up to an upright Idle_0-3 when it stops (e.g. at the player, right after a
                // bite -- master's report). Keep it low by reusing its own crawl move (Move_4/5) as the idle, so it
                // writhes in place instead of standing. The rest use the upright idles.
                _rig.IdleClip = crawler ? _rig.WalkClip : "Idle_" + _rng.RandiRange(0, 3);
                // per-speciality attack/startle anim ids (Zombie.cs sendZombieAttack/sendZombieStartle): the
                // crawler crawls + strikes low (Attack_5), the sprinter has its own set (6-8), the rest 0-4.
                // Clip poses VERIFIED by render grids (2026-07-09): Attack_0-4 upright / Attack_5 prone / Attack_6-8
                // crouched; Startle_1-2 upright / Startle_0,3,4,5 crouched / Startle_6 prone. The extracted clip INDICES
                // never matched the source attack-IDs, so my old map handed SPRINTERS the crouched Attack_6-8 + Startle_4-5
                // -> they crouched on every swing and read as crawlers (master's bug). Correct rule: UPRIGHT clips for the
                // standing zombies (normal + sprinter, distinct subsets), the LOW/PRONE clips for crawlers only.
                if (Speciality == ESpeciality.CRAWLER) { _atkId = _rng.RandiRange(5, 8); _startleId = _rng.Randf() < 0.5f ? 3 : 6; }
                else if (Speciality == ESpeciality.SPRINTER) { _atkId = _rng.RandiRange(3, 4); _startleId = _rng.RandiRange(1, 2); }
                else { _atkId = _rng.RandiRange(0, 2); _startleId = _rng.RandiRange(1, 2); }
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

        // Approximate the source SKULL-limb headshot (info.limb == ELimb.SKULL) from the hit height: the top ~18% of the
        // zombie's collider is its head. Crawlers stand 0.8m, everyone else 1.8m.
        public bool IsHeadshot(Vector3 worldPoint)
        {
            float h = Speciality == ESpeciality.CRAWLER ? 0.8f : 1.8f;
            return worldPoint.Y - GlobalPosition.Y > h * 0.82f;
        }

        const float StepHeight = 0.5f;   // curb/threshold step-over, matches the player (master: apply movement changes to zombies; bumped 0.4->0.5)
        void StepUp(float dt)
        {
            if (!IsOnFloor()) return;
            Vector3 motion = new Vector3(Velocity.X, 0f, Velocity.Z) * dt;
            if (motion.LengthSquared() < 1e-6f) return;
            var raised = new Transform3D(GlobalTransform.Basis, GlobalPosition + Vector3.Up * StepHeight);
            if (TestMove(GlobalTransform, motion) && !TestMove(raised, motion)) GlobalPosition += Vector3.Up * StepHeight;
        }

        // Phase 2: path toward `target` on the baked pocket navmesh via the agent (NO beeline), moving at Speed. Returns
        // the horizontal velocity applied (for facing + locomotion). Off-navmesh, the agent falls back toward the target.
        Vector3 MoveTo(Vector3 target, float g, float dt)
        {
            _repathAcc += dt;
            if (_repathAcc > 0.25f) { _nav.TargetPosition = target; _repathAcc = 0f; }   // re-target ~4x/s (perf: not every tick)
            Vector3 horiz = Vector3.Zero;
            if (!_nav.IsNavigationFinished())
            {
                Vector3 to = _nav.GetNextPathPosition() - GlobalPosition; to.Y = 0f;
                if (to.LengthSquared() > 1e-4f) horiz = to.Normalized() * Speed;
            }
            Velocity = new Vector3(horiz.X, Velocity.Y - g * dt, horiz.Z);
            StepUp(dt); MoveAndSlide();
            return horiz;
        }

        void ApplyDamage(float amount, Vector3 point, Vector3 dir, bool impact)
        {
            if (Dead || IsPuppet) return;   // a puppet has no authoritative health -- its death arrives as the replicated Dead anim state
            Health -= amount;
            if (_capMat != null) _capMat.AlbedoColor = new Color(0.7f, 0.2f, 0.2f);
            if (Health <= 0f)
            {
                Dead = true;
                Velocity = Vector3.Zero;
                CollisionLayer = 0;   // corpse: bullets pass through the capsule to the ragdoll bones
                if (_rig != null)
                {
                    _rig.SetGhost(false);   // a stalker's corpse turns solid
                    // Unturned RagdollTool spine pop: (dir + up*8 + randXZ +-16) * 32, one physics step.
                    Vector3 away = impact ? dir : (Target != null ? GlobalPosition - Target.GlobalPosition : -GlobalTransform.Basis.Z);
                    away = new Vector3(away.X, 0f, away.Z);
                    away = away.LengthSquared() > 0.01f ? away.Normalized() : -GlobalTransform.Basis.Z;
                    Vector3 f = (away * 6f + Vector3.Up * 8f + new Vector3(_rng.RandfRange(-16f, 16f), 0f, _rng.RandfRange(-16f, 16f))) * 0.64f;
                    _rig.RagdollStart(f);
                    if (impact) _rig.ApplyImpact(point, dir * ImpactForce);   // shove at the exact bone hit
                    _ragdoll = true;
                }
                if (Speciality == ESpeciality.BURNER) FireExplosion();   // Zombie.askDamage: BURNER blows up on death
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (IsPuppet) return;   // puppet: ZombiePuppets drives the node from replicated state (PuppetFrame)
            float g = PlayerMovementDef.GRAVITY;
            float dt = (float)delta;

            if (Dead)
            {
                if (_ragdoll) return;   // physics ragdoll drives the body now
                Velocity = new Vector3(0, Velocity.Y - g * dt, 0);
                MoveAndSlide();
                return;
            }
            // Hunt-target resolution (MP_PLAN §3.5 "sensing generalizes to any player avatar via
            // PlayerRegistry"): an explicitly-set Target keeps the exact SP behavior (including the old
            // do-nothing on a non-player Target); a NULL Target -- server worlds, where nobody wires one --
            // hunts the nearest registered player avatar instead of idling forever.
            PlayerController player;
            if (Target != null) { if (Target is not PlayerController targetPlayer) return; player = targetPlayer; }
            else { player = PlayerRegistry.Nearest(GlobalPosition); if (player == null) return; }
            _age += delta;
            if (!_homeSet) { _home = GlobalPosition; _homeSet = true; }   // remember the spawn point

            // Zombie.OnUpdate groan timer: every ~4-8 s an idle zombie occasionally groans, a hunting one roars.
            if (_age > _nextGroan)
            {
                _nextGroan = (float)_age + _rng.RandfRange(4f, 8f);
                // Zombie.OnUpdate only groans/roars `if (isVisible)` -> a stalking (invisible) flanker stays silent.
                bool silentStalker = Speciality == ESpeciality.FLANKER && _hunt != EHunt.NONE;
                if (!silentStalker)
                {
                    if (_hunt == EHunt.NONE) { if (_rng.Randf() > 0.8f) PlaySound(_groans); }
                    else PlaySound(_roars);
                }
            }

            // --- Phase 3 HEARING: investigate the loudest+closest sound but STAY ON TASK -- only a strictly LOUDER+CLOSER
            //     sound overrides the one we're already chasing (no re-agro on every footstep, incl. equal-loudness ones).
            //     It re-evaluates when the investigation ENDS (arrived+gave up, or timed out -> idle resets _huntSalience).
            //     SIGHT still outranks sound. ---
            if (_heardSalience > 0f)
            {
                if (_hunt != EHunt.PLAYER && _heardSalience > _huntSalience)   // commit to the current task unless THIS sound is more salient (louder+closer)
                {
                    _hunt = EHunt.POINT; _startled = false;
                    _huntPoint = _heardPos; _huntSalience = _heardSalience; _lastHunted = (float)_age;   // switch target + refresh give-up clock
                }
                _heardSalience = 0f;   // consume it (a lesser sound, or one heard mid player-hunt, is just dropped)
            }

            // --- IDLE: MOTIONLESS (master's rework: zombies stand still until they SEE (vision cone) or HEAR a noise) ---
            if (_hunt == EHunt.NONE)
            {
                _huntSalience = 0f;   // idle = no current sound target -> the next sound (any loudness) can grab us fresh
                if (Speciality == ESpeciality.FLANKER && _rig != null) _rig.SetGhost(false);   // FRIENDLY: solid again
                Velocity = new Vector3(0f, Velocity.Y - g * dt, 0f);   // gravity only -- no wandering
                StepUp((float)dt);
                MoveAndSlide();
                if (_rig != null) { _rig.Tick(delta); _rig.SetLocomotion(0f); }
                TrySense(player);   // vision cone -> Alert() when the player walks into view
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
            // chase while we can SEE them; the moment sight breaks, remember the spot and (after a short grace) go
            // INVESTIGATE it instead of tracking magically through walls (master's rework).
            if (CanSee(player)) { _lastSeen = pp; _hasLastSeen = true; _lostSightAcc = 0f; }
            else { _lostSightAcc += dt; if (_lostSightAcc > 1.5f && _hasLastSeen) { _hunt = EHunt.POINT; _huntPoint = _lastSeen; _lastHunted = (float)_age; _hasLastSeen = false; return; } }

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
            bool inReach = num3 < ATTACK_PLAYER_SQ;
            Vector3 horiz;
            if (inReach) { Velocity = new Vector3(0f, Velocity.Y - g * dt, 0f); StepUp((float)dt); MoveAndSlide(); horiz = Vector3.Zero; }   // in swing range: plant + swing, don't shove into them
            else horiz = MoveTo(tp, g, (float)dt);   // PATH to the (flank) approach point on the navmesh -> routes around buildings, no beeline

            // --- ACID: spit a corrosive glob at the player from range (Zombie askSpit -> askAcid) ---
            if (Speciality == ESpeciality.ACID && _age > _nextSpit && num3 > 16f && num3 < 900f && num4 < 6f)
            {
                _nextSpit = (float)_age + _rng.RandfRange(4f, 8f);   // specialUseDelay
                SpitAcid(player);
            }

            // --- attack in range: ~1 s cadence, the hit lands mid-swing (Zombie.tick + askAttack). If the target player is
            // DRIVING, we swipe the VEHICLE instead (source Zombie targetPassengerVehicle -> VehicleManager.damage, flat
            // ATTACK_VEHICLE per swing) and reach for the car's BODY surface, since it's big + blocks us short of centre. ---
            Vehicle veh = player.IsDriving ? player.Driving : null;
            if (veh != null && veh.Exploded) veh = null;
            bool canAttack;
            if (veh != null)
            {
                Vector3 lp = veh.GlobalTransform.AffineInverse() * GlobalPosition - veh.BodyCenter;   // zombie in the car's local frame, box-centred
                Vector3 h = veh.BodyExtents;
                Vector3 nearest = new Vector3(Mathf.Clamp(lp.X, -h.X, h.X), Mathf.Clamp(lp.Y, -h.Y, h.Y), Mathf.Clamp(lp.Z, -h.Z, h.Z));
                canAttack = (lp - nearest).LengthSquared() < ATTACK_PLAYER_SQ;   // within arm's reach of the body surface
            }
            else canAttack = num3 < ATTACK_PLAYER_SQ && num4 < VERTICAL_ATTACK;

            bool swinging = false;
            if (canAttack)
            {
                if (_isAttacking)
                {
                    if (_age - _lastAttack > ATTACK_TIME / 2f)
                    {
                        _isAttacking = false;
                        if (veh != null) veh.TakeDamage(ATTACK_VEHICLE);   // source: no crawler/sprinter mult, no infect through the car
                        else
                        {
                            float mult = Speciality == ESpeciality.CRAWLER ? 2f : Speciality == ESpeciality.SPRINTER ? 0.75f : 1f;
                            player.TakeDamage(AttackDamage * mult, GlobalPosition);   // pass my position so the hurt-flinch kicks away from me
                            player.Infect((AttackDamage * mult / 3f) / 100f);   // Zombie.askDamage: askInfect(b/3)
                        }
                    }
                }
                else if (_age - _lastAttack > 1f)
                {
                    _isAttacking = true; swinging = true; SwingSeq++;   // SwingSeq: the net sync turns each new swing into an AttackSwing event
                    _lastAttack = (float)_age;             // askAttack: lastAttack = now
                    _rig?.PlayOnce("Attack_" + _atkId);
                    if (_audio != null && !_audio.Playing) PlaySound(_roars);   // roar on the swing (Zombie.askAttack)
                }
            }
            else _isAttacking = false;

            // --- animation + facing (CanTurn: face where we move while approaching, the player when close) ---
            if (_rig != null)
            {
                _rig.Tick(delta);
                if (!_startled) { _startled = true; _rig.PlayOnce("Startle_" + _startleId); PlaySound(_roars); }
                else if (!_isAttacking && !swinging) _rig.SetLocomotion(horiz.Length());
            }
            Vector3 faceDir = num3 > 4f && horiz.LengthSquared() > 1e-4f ? horiz : Flat(pp - me);
            if (faceDir.LengthSquared() > 1e-4f)
                LookAt(me + faceDir, Vector3.Up);

            // FLANKER_STALK: a faint ghost while closing in, snapping fully solid only for the swing (updateVisibility)
            if (Speciality == ESpeciality.FLANKER && _rig != null) _rig.SetGhost(!_isAttacking);
        }

        // --- MP Phase 5 puppet driver (MP_PLAN §3.5). Called every RENDER frame by ZombiePuppets with the
        // newest replicated state: glide toward the 12.5 Hz snapshot position (snap across teleport-sized
        // jumps, RemotePlayers-style), mirror the anim byte onto the rig, play the death ragdoll once.
        // No AI, no nav, no MoveAndSlide ever runs on a puppet. ---
        byte _puppetAnim = 255;   // last applied anim byte (255 = none yet -> first Attack edge still triggers)
        public void PuppetFrame(double delta, Vector3 targetPos, float yawDegrees, byte anim)
        {
            if (!IsPuppet || Dead) return;   // dead: the ragdoll owns the body
            float dt = (float)delta;
            Vector3 before = GlobalPosition;
            GlobalPosition = before.DistanceTo(targetPos) > 5f ? targetPos : before.Lerp(targetPos, 1f - Mathf.Exp(-14f * dt));
            RotationDegrees = new Vector3(0f, yawDegrees, 0f);
            if (_rig != null)
            {
                _rig.Tick(delta);
                if (anim == (byte)UnturnedGodot.Net.ZombieNetAnim.Dead) { PuppetDie(); return; }
                if (anim == (byte)UnturnedGodot.Net.ZombieNetAnim.Attack)
                { if (_puppetAnim != anim) _rig.PlayOnce("Attack_" + _atkId); }
                else _rig.SetLocomotion(dt > 1e-4f ? (GlobalPosition - before).Length() / dt : 0f);
            }
            else if (anim == (byte)UnturnedGodot.Net.ZombieNetAnim.Dead) { PuppetDie(); return; }
            _puppetAnim = anim;
        }

        void PuppetDie()
        {
            if (Dead) return;
            Dead = true;
            Velocity = Vector3.Zero;
            CollisionLayer = 0;
            if (_rig != null)
            {
                _rig.SetGhost(false);
                Vector3 f = (-GlobalTransform.Basis.Z * 6f + Vector3.Up * 8f) * 0.64f;   // the standard spine pop, minus the server's RNG scatter (cosmetic only)
                _rig.RagdollStart(f);
                _ragdoll = true;
            }
        }

        // AlertTool.check + line-of-sight: sense the player only within their stealth radius, not behind the
        // zombie's back while they sneak (anything but sprinting counts as sneaking), and with clear sight.
        // Phase 2 VISION CONE (master): the zombie sees the player only within its eye cone -- inside SightRange and
        // within SightHalfAngleDeg of where it's FACING -- and with a clear confirming line-of-sight ray. Sneaking
        // (any stance but sprint, not driving) halves the reach. Returns true if it can currently see the player.
        bool CanSee(PlayerController player)
        {
            if (player.Health <= 0f) return false;
            Vector3 toP = player.GlobalPosition - GlobalPosition;   // zombie -> player
            float distSq = toP.LengthSquared();
            bool sneak = player.Stance != EPlayerStance.SPRINT && !player.IsDriving;
            float range = sneak ? SightRange * 0.5f : SightRange;
            if (distSq > range * range) return false;
            Vector3 fwd = -GlobalTransform.Basis.Z;
            if (distSq > 1e-4f && fwd.Normalized().AngleTo(toP.Normalized()) > Mathf.DegToRad(SightHalfAngleDeg)) return false;   // outside the cone
            return HasLineOfSight(player);   // confirming ray: nothing solid between the eyes and the player
        }

        void TrySense(PlayerController player) { if (CanSee(player)) Alert(player); }

        // AlertTool raycast: eye-height ray toward the player over 95 % of the gap; any world geometry blocks it.
        bool HasLineOfSight(PlayerController player)
        {
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = GlobalPosition + Vector3.Up * 1.0f;
            Vector3 toP = (player.GlobalPosition + Vector3.Up * 1.0f) - from;
            var q = PhysicsRayQueryParameters3D.Create(from, from + toP * 0.95f);
            q.CollisionMask = 1u << 0;   // world geometry only (ground/props) = BLOCK_VISION
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            if (player.IsDriving && player.Driving != null) exclude.Add(player.Driving.GetRid());   // source BLOCK_VISION excludes vehicles -> a car doesn't hide its own driver
            q.Exclude = exclude;
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
            if (Speciality == ESpeciality.FLANKER && _rig != null) _rig.SetGhost(true);   // ghost while stalking a noise
            Vector3 me = GlobalPosition;
            float num3 = HDistSq(_huntPoint, me);
            bool arrived = num3 < 3f;                                      // Zombie isMoving = num3 > 3 (~1.73 m)
            if (_age - _lastHunted > 12f) { _hunt = EHunt.NONE; return; }             // hard cap: unreachable/stale noise -> stop, go idle (then re-attracts to the next sound)
            if (arrived && _age - _lastHunted > 3f) { _hunt = EHunt.NONE; return; }   // stop()

            Vector3 horiz;
            if (arrived) { Velocity = new Vector3(0f, Velocity.Y - g * dt, 0f); StepUp((float)dt); MoveAndSlide(); horiz = Vector3.Zero; }   // arrived: stand + look around, then give up (above)
            else horiz = MoveTo(_huntPoint, g, dt);   // PATH to where the noise / last sighting was (navmesh, no beeline)

            if (_rig != null)
            {
                _rig.Tick(dt);
                if (!_startled) { _startled = true; _rig.PlayOnce("Startle_" + _startleId); PlaySound(_roars); }
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

        // Phase 3 sound bus (SoundBus.Emit -> CallGroup "Hear"): a sound at `pos` carrying `loudness` (m). Heard only
        // within the HearingRange sphere AND if the loudness carries that far; keeps the LOUDEST+CLOSEST one this tick
        // (salience = loudness - dist) and acts on it in _PhysicsProcess -- master's hearing rework. Sight still wins.
        public void Hear(Vector3 pos, float loudness)
        {
            if (Dead) return;
            float dist = (pos - GlobalPosition).Length();
            if (dist > HearingRange || dist > loudness) return;   // outside the ears, or the sound doesn't carry this far
            float salience = loudness - dist;                     // loud + close = most salient (master: loudest + closest)
            if (salience > _heardSalience) { _heardSalience = salience; _heardPos = pos; }
        }

        // Back-compat shim: older callers (CallGroup "OnGunshot", pos, radius) map radius -> loudness through the same ears.
        public void OnGunshot(Vector3 pos, float radius) => Hear(pos, radius);

        // --heartest offline verify: the best (loudest+closest) sound accumulated so far this tick.
        public (Vector3 pos, float salience) DebugHeard() => (_heardPos, _heardSalience);

        // --heartest offline verify: given a sound we're already investigating (currentHuntSalience), would a newly-heard
        // sound OVERRIDE it? (the "stay on task" gate: only a strictly louder+closer sound wins). Mirrors _PhysicsProcess.
        public bool DebugWouldOverride(float currentHuntSalience, Vector3 soundPos, float loudness)
        {
            _huntSalience = currentHuntSalience; _heardSalience = 0f;
            Hear(soundPos, loudness);
            return _heardSalience > _huntSalience;
        }

        // Zombie.askSpit -> askAcid: lob a corrosive glob at the player. Arced so gravity drops it onto the aim point.
        void SpitAcid(PlayerController player)
        {
            _rig?.PlayOnce("Attack_" + _atkId);   // rig has no dedicated Acid clip; reuse a lunge as the spit tell
            PlaySound(_spits);                    // Zombie.askAcid: spit hiss
            Vector3 from = GlobalPosition + Vector3.Up * 1.3f;
            Vector3 to = (player.GlobalPosition + Vector3.Up) - from;
            float t = Mathf.Clamp(to.Length() / 18f, 0.35f, 1.6f);       // ~18 m/s glob; solve the lob time
            Vector3 vel = to / t + Vector3.Up * (0.5f * 9.81f * t);       // add the up-component gravity will cancel
            var spit = new AcidSpit { Target = player, Velocity = vel, Damage = 18f };
            GetParent().AddChild(spit);
            spit.GlobalPosition = from;
        }

        // Zombie.askDamage: a BURNER detonates in a 4 m fire ball on death (EExplosionDamageType.ZOMBIE_FIRE, 40 dmg).
        void FireExplosion()
        {
            if ((Target as PlayerController ?? PlayerRegistry.Nearest(GlobalPosition)) is PlayerController tp)   // Target-less server zombies burn the nearest avatar (SP: Target IS the player, identical)
            {
                float d = GlobalPosition.DistanceTo(tp.GlobalPosition);
                if (d < 4f) tp.TakeDamage(40f * (1f - d / 4f));          // radial falloff over the 4 m radius
            }
            var light = new OmniLight3D { OmniRange = 7f, LightColor = new Color(1f, 0.55f, 0.18f), LightEnergy = 6f };
            GetParent().AddChild(light);
            light.GlobalPosition = GlobalPosition + Vector3.Up * 0.5f;
            var tw = light.CreateTween();
            tw.TweenProperty(light, "light_energy", 0f, 0.4f);           // brief orange blast flash
            tw.TweenCallback(Callable.From(light.QueueFree));
        }

        static void LoadSounds()
        {
            if (_roars != null) return;
            static AudioStream[] Load(string prefix, int n)
            {
                var a = new AudioStream[n];
                for (int i = 0; i < n; i++)
                    a[i] = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/{prefix}_{i}.ogg"));
                return a;
            }
            _roars = Load("zroar", 16);   // Sounds/Zombies/Roars (startle/attack/moving)
            _groans = Load("zgroan", 5);  // Groans (occasional idle)
            _spits = Load("zspit", 4);    // Spits (acid)
        }

        void PlaySound(AudioStream[] set)
        {
            if (set == null || set.Length == 0 || _audio == null || Dead) return;
            var s = set[_rng.RandiRange(0, set.Length - 1)];
            if (s == null) return;
            _audio.Stream = s;
            _audio.Play();
        }

        static Vector3 Flat(Vector3 v) { v.Y = 0f; return v.LengthSquared() > 1e-6f ? v.Normalized() : v; }
        static float HDistSq(Vector3 a, Vector3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
    }

    // A corrosive glob spat by an ACID zombie (Zombie.askAcid's Acid_Projectile): arcs under gravity, burns the
    // player on contact, and splats out on the ground or after a few seconds.
    public partial class AcidSpit : Node3D
    {
        public PlayerController Target;
        public Vector3 Velocity;
        public float Damage = 18f;
        double _life = 4.0;

        public override void _Ready()
        {
            var mesh = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f } };
            mesh.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.55f, 0.85f, 0.1f),
                EmissionEnabled = true, Emission = new Color(0.5f, 0.9f, 0.1f), EmissionEnergyMultiplier = 2f,
            };
            AddChild(mesh);
        }

        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            Velocity += new Vector3(0f, -9.81f, 0f) * dt;   // gravity arc
            GlobalPosition += Velocity * dt;
            _life -= delta;
            if (Target != null && (Target.GlobalPosition + Vector3.Up).DistanceTo(GlobalPosition) < 0.8f)
            {
                Target.TakeDamage(Damage);
                QueueFree(); return;
            }
            if (GlobalPosition.Y < 0.05f || _life <= 0.0) QueueFree();   // splat / expire
        }
    }
}
