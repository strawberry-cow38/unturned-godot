using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A wandering animal: a CharacterBody3D (so bullets/melee hit it and the player can't walk through it) wrapping a
    // RiggedCharacter and roaming a small home range. Ambles (Walk clip) to a random nearby point facing the way it
    // moves, then grazes/idles in place (Idle/Eat/Glance) for a few seconds, repeat. Terrain-following, water-avoiding.
    // Shooting/hitting it drains Health -> ragdolls a corpse; a non-fatal hit makes it bolt. AnimalField spawns these.
    public partial class AnimalAgent : CharacterBody3D
    {
        public RiggedCharacter Rig;
        public Terrain Terr;
        public Vector3 Home;                                        // spawn point; targets stay within HomeRange of it
        public float Foot;                                          // feet-on-terrain offset (also sizes the hit capsule)
        public uint Seed;
        public byte Species;                                        // A5: AnimalCatalog index (deer/pig/cow), set by AnimalField -> published by AnimalNetSync
        public float Health = 100f;                                 // set per-species by AnimalField
        public byte NetAnim { get; private set; }                   // A5: current anim byte for the replica (idle/eat/glance/walk)
        public bool Dead { get; private set; }

        // The animal rigs (deer/pig/cow) import facing local -X, NOT Godot's -Z. The LookAt aligns the body's -Z to
        // travel, so the model walked SIDEWAYS. A +270 yaw on the RIG child (only the visual needs it -- the capsule is
        // rotationally symmetric) turns -X round to -Z = travel. MEASURED top-down via --animaltest (UG_ANIMALYAW sweep:
        // 0=-X, 90=+Z, 180=+X, 270=-Z), so 270 is the one that faces travel. (180 was my first, wrong-by-90 guess.)
        const float RigYawFix = 270f;

        Vector3 _target;
        bool _walking;
        double _idleTimer, _fleeTimer;
        Vector3 _faceDir = Vector3.Forward;   // smoothed heading: the body turns toward travel instead of snapping to it
        const float Speed = 1.35f, FleeSpeed = 5.5f, HomeRange = 12f, Arrive = 0.8f, TurnLerp = 5f;
        static readonly string[] Ambient = { "Idle", "Eat", "Glance_0", "Idle", "Eat", "Glance_1" };

        uint R() { Seed = Seed * 1664525u + 1013904223u; return Seed >> 9; }

        public void Begin()
        {
            AddToGroup("animals");                                  // A5: the group AnimalNetSync publishes from + melee/blast sweep
            BuildHitBody();
            if (Rig != null) Rig.RotationDegrees = new Vector3(0f, RigYawFix, 0f);   // face-fix, rig-local so the body's -Z still leads
            StartIdle();
        }

        // A capsule on the enemy bit (1<<1) that the gun ray masks and the player body collides with -- the same layer
        // ZombieController uses, so both "can't shoot it" and "walk through it" fall out of one shape. Sized off Foot
        // (the leg height) as a size proxy; the wander drives GlobalPosition directly (terrain-followed), so the body
        // needs no mask of its own -- it is a thing to be hit, not a thing that resolves collisions.
        void BuildHitBody()
        {
            CollisionLayer = 1u << 1;
            CollisionMask = 0;
            float r = Mathf.Clamp(Foot * 0.9f, 0.30f, 0.70f);
            float h = Mathf.Clamp(Foot * 2.6f + 0.5f, 0.9f, 2.0f);
            AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = r, Height = h }, Position = new Vector3(0f, h * 0.25f, 0f) });
        }

        void StartIdle()
        {
            _walking = false;
            var clip = Ambient[(int)(R() % (uint)Ambient.Length)];
            Rig?.Play(clip);
            NetAnim = clip == "Eat" ? (byte)AnimalNetAnim.Eat : clip.StartsWith("Glance") ? (byte)AnimalNetAnim.Glance : (byte)AnimalNetAnim.Idle;
            _idleTimer = 3.0 + (R() % 600) / 100.0;                 // graze/idle 3-9 s
        }

        void PickTarget()
        {
            float ang = (R() % 628) / 100f;
            float dist = 4f + (R() % 800) / 100f;                   // 4-12 m amble
            float tx = Home.X + Mathf.Cos(ang) * dist, tz = Home.Z + Mathf.Sin(ang) * dist;
            if (Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(tx, tz))) { StartIdle(); return; }   // don't wade in
            _target = new Vector3(tx, 0f, tz);
            _walking = true;
            NetAnim = (byte)AnimalNetAnim.Walk;
            Rig?.Play("Walk");
        }

        // Gun/melee/blast damage. A non-fatal hit makes it bolt away from the impact; a fatal one ragdolls a corpse in
        // the shot direction and despawns it after it settles. Mirrors ZombieController.ApplyDamage (corpse layer 0 so
        // rounds pass the capsule to the ragdoll bones). Returns nothing -- callers read Dead to score the kill.
        public void DamageHit(float amount, Vector3 point, Vector3 dir)
        {
            if (Dead) return;
            Health -= amount;
            if (Health <= 0f)
            {
                Dead = true;
                CollisionLayer = 0;                                 // corpse: bullets pass through to the ragdoll bones
                RemoveFromGroup("animals");
                NetAnim = (byte)AnimalNetAnim.Idle;
                Vector3 f = dir.LengthSquared() > 0.01f ? dir.Normalized() : -GlobalTransform.Basis.Z;
                Rig?.RagdollStart((f + Vector3.Up * 0.5f).Normalized() * 6f);   // flop in the shot direction (zombie spine-pop scale)
                var timer = GetTree().CreateTimer(12.0);            // let the corpse settle, then clean up
                timer.Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
                return;
            }
            // survived -> bolt away from the threat for a few seconds
            _fleeTimer = 3.5;
            Vector3 away = GlobalPosition - point; away.Y = 0f;
            if (away.LengthSquared() < 0.01f) { away = dir; away.Y = 0f; }
            away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
            _target = GlobalPosition + away * 10f;
            _walking = true;
            NetAnim = (byte)AnimalNetAnim.Walk;
            Rig?.Play("Walk");
        }

        public override void _Process(double delta)
        {
            if (Dead) return;                                       // the ragdoll owns the body now
            if (Rig != null && !IsInstanceValid(Rig)) return;       // a FREED rig bails; a null rig (dedicated, rig-less) still wanders so AnimalNetSync has a moving transform to publish
            if (_fleeTimer > 0) _fleeTimer -= delta;
            var pos = GlobalPosition;
            float nx = pos.X, nz = pos.Z;
            bool moved = false;
            if (_walking)
            {
                float dx = _target.X - pos.X, dz = _target.Z - pos.Z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < Arrive) StartIdle();
                else
                {
                    float speed = _fleeTimer > 0 ? FleeSpeed : Speed;
                    float inv = 1f / d, step = Mathf.Min(speed * (float)delta, d);
                    nx = pos.X + dx * inv * step; nz = pos.Z + dz * inv * step;
                    var want = new Vector3(dx, 0f, dz).Normalized();
                    _faceDir = _faceDir.LengthSquared() < 1e-4f ? want : _faceDir.Slerp(want, Mathf.Min(1f, TurnLerp * (float)delta));   // SLERP the heading -> a smooth turn, never a snap on a new target
                    moved = true;
                }
            }
            else
            {
                _idleTimer -= delta;
                if (_idleTimer <= 0) PickTarget();
            }
            // GROUND the feet EVERY frame on the real collision surface (what the player/zombies stand on), NOT the
            // heightmap guess -- that SampleHeight-vs-collision gap is what left them hovering above the ground.
            float gy = GroundY(nx, nz) + Foot;
            GlobalPosition = new Vector3(nx, gy, nz);
            if (moved) LookAt(new Vector3(nx + _faceDir.X, gy, nz + _faceDir.Z), Vector3.Up);   // body -Z leads the smoothed heading (level); the rig's RigYawFix turns the model to match
        }

        // True ground height under (x,z): raycast the world collision (layer 1<<0, what physics bodies rest on),
        // starting from the heightmap guess; fall back to SampleHeight where there's no collider / no space state.
        float GroundY(float x, float z)
        {
            float sampled = Terr != null ? Terr.SampleHeight(x, z) : GlobalPosition.Y - Foot;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return sampled;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(x, sampled + 4f, z), new Vector3(x, sampled - 4f, z));
            q.CollisionMask = 1u << 0;
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(q);
            return hit.Count > 0 ? ((Vector3)hit["position"]).Y : sampled;
        }
    }
}
