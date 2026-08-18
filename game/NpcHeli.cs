using Godot;

namespace UnturnedGodot
{
    // An AI-flown helicopter (strawberry 2026-08-17): "spawn at a map edge, fly towards the nearest map name node,
    // maintaining steady heights (terrain height + accounting for treetops), circling the monument".
    //
    // It flies the REAL machine through DriveHeli -- the same four stick axes a player has -- rather than animating a
    // transform along a path. So it inherits the airframe's own thrust, drag, turn authority and envelope, which is
    // what "consider the heli's stats" means: a Hind and a minicopter fly this route differently because they are
    // different aircraft, not because anything here special-cases them.
    //
    // HEIGHT COMES FROM THE TERRAIN SAMPLER, NOT A RAYCAST. Trees carry only a TRUNK collider (0.5 m radius, 8 m tall
    // -- ResourceField deliberately replaced the full-tree AABB, which floated a canopy-height cylinder above the
    // ground), and those colliders are streamed out past ~320 m by ColliderBudget. So a downward ray would sense a
    // trunk at best and bare ground most of the time, and would go blind entirely once the NPC left the player's
    // neighbourhood. Terrain.SampleHeight is exact at any range, so the canopy is handled as a CLEARANCE above it.
    public partial class NpcHeli : Node
    {
        public Vehicle Heli;
        public Terrain Terr;
        public Vector3 Target;          // the map node being flown to
        public string TargetName = "";

        // Canopy clearance. ResourceField's trunk collider is 8 m and the real canopies stand well above it, so this
        // is the margin between the terrain underneath and the aircraft -- deliberately generous, because the cost of
        // being too high is cosmetic and the cost of being too low is flying through a forest.
        public const float CanopyClearance = 34f;
        public const float OrbitRadius = 90f;      // how wide it circles the monument
        const float ArriveDist = 140f;             // switch from transit to orbit inside this
        // Climb-rate authority the AI will ask for, and the deadband it stops correcting inside. The deadband
        // has to be wide enough that the neutral state's gentle sink does not retrigger every tick.
        const float MaxClimbMps = 6f, ClimbDeadband = 0.6f;

        // FLIGHT PHASES (strawberry 2026-08-17: "the nose tilt should be different states, like when it wants to
        // go fast, circling, etc"). The attitude is a PROPERTY OF THE PHASE rather than a by-product of one speed
        // gain -- with a single gain the nose angle is just however far behind the speed target it happens to be,
        // so a transiting aircraft and a loitering one sit at the same attitude and only differ in throttle.
        public enum FlightPhase { Transit, Arrive, Orbit }
        public FlightPhase Phase { get; private set; } = FlightPhase.Transit;

        readonly struct Envelope
        {
            public readonly float SpeedFrac;        // of SpeedMaxMps -- what this phase is TRYING to do
            public readonly float TiltGain;         // deg of attitude per m/s of speed error
            public readonly float MaxNoseDownDeg;   // how hard it is allowed to commit the nose
            public readonly float MaxNoseUpDeg;     // how hard it is allowed to flare to brake
            public Envelope(float f, float g, float d, float u) { SpeedFrac = f; TiltGain = g; MaxNoseDownDeg = d; MaxNoseUpDeg = u; }
        }
        // TRANSIT leans on it. ARRIVE flares nose-UP to kill the speed it built rather than sailing through the
        // monument. ORBIT sits near level, because a helicopter loitering over a target is not a diving one.
        public const float TransitSpeedFrac = 0.80f;   // exposed so the suite asserts against the REAL target
        static readonly Envelope TransitEnv = new Envelope(TransitSpeedFrac, 4.0f, 38f, 6f);
        static readonly Envelope ArriveEnv  = new Envelope(0.30f, 5.0f,  8f, 22f);
        static readonly Envelope OrbitEnv   = new Envelope(0.45f, 2.5f, 14f, 8f);

        float _wantClimb;   // m/s of climb the AI is asking for; telemetry only

        // ================= COMBAT (strawberry 2026-08-18) =================
        // "if you damage an npc copter, it will track the position that you shot it from. hinds will turn to face
        // the point where it last saw you, while the turret has line of sight on you, it will lock onto your
        // position (roughly, lagging behind a little) it will then shoot bursts as you (uses the HMG weapon from
        // the source) its pretty innaccurate, but the hind will stay locked onto your last seen position for five
        // minutes, if it doesnt see you again it will go back to its path (going to the nearest path point to it)."
        //
        // ATTACK IS THE ONLY TRIGGER (confirmed): it will not open up on someone who merely flies past. And only
        // an airframe with a mount fights -- "dont wire up the other helis for attack behavior" -- which falls out
        // of the data rather than a name check, since the Hind is the only spec carrying Turrets.
        public enum Stance { Patrol, Engaged }
        public Stance Mode { get; private set; } = Stance.Patrol;
        public Vector3 LastSeen { get; private set; }      // the point it is watching: shot origin, or you
        public bool Armed => Heli != null && Heli.Turrets.Length > 0;

        public const float LockSeconds = 300f;      // five minutes of holding the grudge
        const float TurretSlewDegPerSec = 55f;      // THE LAG: the mount cannot snap, so the aim trails a mover
        const float FireConeDeg = 6f;               // only shoot once the barrel is roughly there
        const float AimSpreadDeg = 4.0f;            // inaccuracy ON TOP of the gun's own 1.43 deg
        // BURSTS VARY IN BOTH DIMENSIONS (strawberry: "the bursts should vary in length, and time between them").
        // A fixed 7-and-1.5 reads as a metronome once you have been shot at twice; the point of a burst is that
        // you cannot time it. Re-rolled per burst, so length and gap are independent.
        const int BurstMinRounds = 4, BurstMaxRounds = 11;
        const float BurstGapMin = 0.8f, BurstGapMax = 2.6f;
        const float GunRange = 250f;                // retail HMG.dat Range

        double _seenDamageAtMsec = -1e9;            // the newest damage event already consumed
        double _lockUntilMsec = -1e9;
        float _turYaw, _turPitch;                   // the SLEWED aim, in degrees, which is what actually fires
        int _burstLeft; float _burstWait;
        public int DebugBurstLeft => _burstLeft;
        public float DebugTurretYaw => _turYaw;
        public float DebugTurretPitch => _turPitch;
        public double DebugLockLeftSec => Mathf.Max(0.0, (_lockUntilMsec - Time.GetTicksMsec()) / 1000.0);

        static readonly RandomNumberGenerator Rng = new();
        public static bool DebugCombat;

        /// <summary>Test seam: point the mount at a world position immediately, bypassing the slew. The slew is
        /// the FEEL; the angles are the CORRECTNESS, and mixing them in one check would let a wrong sign hide
        /// behind "it was still turning".</summary>
        public void DebugAimAt(Vector3 worldPoint)
        {
            var (y, p) = AimAnglesFor(worldPoint);
            _turYaw = y; _turPitch = p;
            Heli.AimTurret(Heli.Turrets[0].Seat, y, p);
        }

        /// <summary>Aim angles, in the mount's own frame, that point the barrel at a world point. Derived from the
        /// rotation AimTurret applies -- and then MEASURED, because three separate control-axis signs in this file
        /// were derived confidently and were wrong. vehicle.npc_heli_turret asserts the barrel really does end up
        /// pointing at the thing these numbers were computed for.</summary>
        (float yaw, float pitch) AimAnglesFor(Vector3 worldPoint)
        {
            Vector3 d = Heli.GlobalTransform.AffineInverse() * worldPoint;   // vehicle-local offset
            float h = Mathf.Sqrt(d.X * d.X + d.Z * d.Z);
            return (Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(Mathf.Atan2(d.Y, h)));
        }

        PlayerController NearestPlayer()
        {
            PlayerController best = null; float bestD = float.MaxValue;
            foreach (var n in GetTree().GetNodesInGroup("players"))
            {
                if (n is not PlayerController p || p.Health <= 0f) continue;
                float d = p.GlobalPosition.DistanceSquaredTo(Heli.GlobalPosition);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// <summary>Clear shot from the muzzle to `target`? Two things have to be excluded or this never returns
        /// true. The AIRCRAFT ITSELF, because a chin turret sits under the nose and would self-hit every frame.
        /// And THE TARGET, because the ray ends at the player's own capsule -- a plain "did the ray hit anything"
        /// test therefore reads the player as the thing blocking the shot at the player, and the gun would never
        /// once fire. Hitting the target IS the clear shot; only something in between is not.</summary>
        bool ClearShotTo(Vector3 from, Vector3 to, Node target)
        {
            var space = Heli.GetWorld3D()?.DirectSpaceState;
            if (space == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(from, to);
            q.Exclude = new Godot.Collections.Array<Rid> { Heli.GetRid() };
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return true;
            return target != null && hit["collider"].As<GodotObject>() == target;
        }

        void StepCombat(float dt)
        {
            double now = Time.GetTicksMsec();

            // ---- 1. NEW DAMAGE. Adopting the shot ORIGIN, which is where the shooter stood.
            if (Heli.LastAttackedAtMsec > _seenDamageAtMsec)
            {
                _seenDamageAtMsec = Heli.LastAttackedAtMsec;
                LastSeen = Heli.LastAttackedFrom;
                Mode = Stance.Engaged;
                _lockUntilMsec = now + LockSeconds * 1000.0;
            }
            if (Mode != Stance.Engaged) return;

            // ---- 2. EYES. If the turret can actually see a player, that refreshes both the aim point and the
            // clock -- "if it doesnt see you again it will go back to its path" means SEEING resets the five
            // minutes, so a player who keeps showing themselves is never let go.
            Vector3? muzzle = Heli.TurretMuzzle(Heli.Turrets[0].Seat);
            var player = NearestPlayer();
            bool eyesOn = false;
            if (player != null && muzzle.HasValue)
            {
                Vector3 eye = player.GlobalPosition + Vector3.Up * 1.2f;
                if (eye.DistanceTo(muzzle.Value) <= GunRange && ClearShotTo(muzzle.Value, eye, player))
                {
                    eyesOn = true;
                    LastSeen = eye;
                    _lockUntilMsec = now + LockSeconds * 1000.0;
                }
            }

            // ---- 3. SLEW. Rate-limited, so the mount visibly trails a moving target instead of snapping onto it.
            var (wantYaw, wantPitch) = AimAnglesFor(LastSeen);
            float step = TurretSlewDegPerSec * dt;
            // Slew the SHORT way. MoveToward on raw degrees crosses +-180 the long way round -- chasing a target
            // that drifts from +170 to -170 sweeps the mount through zero, a 340 deg traverse to cover 20.
            _turYaw += Mathf.Clamp(Mathf.Wrap(wantYaw - _turYaw, -180f, 180f), -step, step);
            _turYaw = Mathf.Wrap(_turYaw, -180f, 180f);
            _turPitch = Mathf.MoveToward(_turPitch, wantPitch, step);
            Heli.AimTurret(Heli.Turrets[0].Seat, _turYaw, _turPitch);

            // ---- 4. FIRE, in bursts, only with eyes on and the barrel roughly there. Firing at a remembered
            // point with nobody in it would be a wall of tracer through empty sky forever.
            _burstWait = Mathf.Max(0f, _burstWait - dt);
            // GATE ON WHERE THE BARREL ACTUALLY POINTS, not on the angles requested. AimTurret CLAMPS to the
            // mount's traverse (yaw +-120, pitch -60..+15), so comparing the commanded angle against itself says
            // "on target" while the gun is pegged at its stop pointing somewhere else entirely -- a Hind with a
            // target behind its shoulder would have hosed the scenery at 50 deg off and reported success. Reading
            // the real barrel axis makes the clamp part of the answer instead of something to remember.
            bool onTarget = false;
            if (muzzle.HasValue)
            {
                Vector3 want = (LastSeen - muzzle.Value);
                if (want.LengthSquared() > 1e-4f)
                    onTarget = Heli.TurretBarrelDir(Heli.Turrets[0].Seat).AngleTo(want.Normalized()) < Mathf.DegToRad(FireConeDeg);
            }
            if (DebugCombat && Engine.GetPhysicsFrames() % 25 == 0)
                GD.Print($"[COMBAT] mode={Mode} eyesOn={eyesOn} onTarget={onTarget} muzzle={(muzzle.HasValue ? "y" : "NULL")} " +
                         $"player={(player != null ? "y" : "NULL")} want=({wantYaw:0.0},{wantPitch:0.0}) cur=({_turYaw:0.0},{_turPitch:0.0}) " +
                         $"burstWait={_burstWait:0.00} burstLeft={_burstLeft}");
            if (eyesOn && onTarget && _burstWait <= 0f)
            {
                if (_burstLeft <= 0) _burstLeft = Rng.RandiRange(BurstMinRounds, BurstMaxRounds);
                if (Heli.TryTurretFire(Heli.Turrets[0].Seat, out var o, out var dir, out var gun))
                {
                    // INACCURACY IS THE BALANCE. The HMG's magazine is explosive .50 -- an accurate one would
                    // erase a player -- so the AI adds a cone well beyond the gun's own 1.43 deg.
                    Vector3 spread = new Vector3(Rng.RandfRange(-1f, 1f), Rng.RandfRange(-1f, 1f), Rng.RandfRange(-1f, 1f));
                    dir = (dir + spread.Normalized() * Mathf.Tan(Mathf.DegToRad(AimSpreadDeg)) * Rng.Randf()).Normalized();
                    NpcShot?.Invoke(o, dir, gun);
                    if (--_burstLeft <= 0) _burstWait = Rng.RandfRange(BurstGapMin, BurstGapMax);
                }
            }

            // ---- 5. LET IT GO. The phase machine then does the rest: out past ArriveDist * 1.6 it re-enters
            // Transit and flies back to its node, which is "the nearest path point to it".
            if (now > _lockUntilMsec) { Mode = Stance.Patrol; _burstLeft = 0; }
        }

        /// <summary>Raised for every round the AI fires: world muzzle, direction, gun id. A delegate rather than a
        /// direct call into the player's bullet system so this file does not have to know how shots are drawn --
        /// and so a test can count rounds without a renderer.</summary>
        public static System.Action<Vector3, Vector3, string> NpcShot;

        public override void _PhysicsProcess(double delta)
        {
            if (Heli == null || !IsInstanceValid(Heli) || Heli.Exploded) return;
            Vector3 pos = Heli.GlobalPosition;

            // ---- HEIGHT: terrain under the aircraft, plus canopy clearance.
            float ground = Terr != null ? Terr.SampleHeight(pos.X, pos.Z) : 0f;
            float wantY = ground + CanopyClearance;
            float vy = Heli.LinearVelocity.Y;
            // DriveHeli's collective is a THREE-STATE COMMAND, NOT A THROTTLE POSITION. Read it: anything above
            // +0.05 means "full up", anything below -0.05 means "full down", and everything in between means
            // "hands off" -- settle to IdleCollective, which is 0.92 of hover and therefore a gentle sink. It is
            // the same law a player's keyboard drives, and it is correct for a key.
            //
            // So the PID output this used to compute was meaningless: every positive number said the identical
            // thing. It also explains a 13 m steady-state height error the loose height check waved through --
            // the aircraft climbed until the proportional term happened to land inside the +-0.05 deadband, then
            // rode IdleCollective there. It was holding a steady height, just not the commanded one.
            //
            // Commanding the three states directly, on a CLIMB-RATE error with a deadband so it cannot chatter:
            // ask for a climb rate proportional to the height error, then pull up, push down, or let go.
            float wantClimb = Mathf.Clamp(0.35f * (wantY - pos.Y), -MaxClimbMps, MaxClimbMps);
            float climbErr = wantClimb - vy;
            float collective = climbErr > ClimbDeadband ? 1f : climbErr < -ClimbDeadband ? -1f : 0f;
            _wantClimb = wantClimb;

            if (Armed) StepCombat((float)delta);

            // ---- WHERE TO GO: run the target down, then circle it.
            Vector2 here = new Vector2(pos.X, pos.Z), goal = new Vector2(Target.X, Target.Z);
            float range = here.DistanceTo(goal);
            Vector2 aim;
            if (Mode == Stance.Engaged)
            {
                // TURN TO FACE where it last saw you. The airframe flies where its nose points, so facing the
                // contact is also closing on it -- which is what a gunship does and what "turn to face" buys you
                // visually. Height hold is untouched, so it cannot fly itself into the ground doing this.
                aim = new Vector2(LastSeen.X, LastSeen.Z);
            }
            else if (range > ArriveDist)
            {
                aim = goal;   // transit
            }
            else
            {
                // ORBIT: steer at a point on the circle a quarter-turn ahead, so the aircraft is always chasing a
                // tangent rather than the centre. Aiming AT the monument just makes it spiral in and sit on top.
                Vector2 radial = (here - goal);
                if (radial.LengthSquared() < 1f) radial = Vector2.Right;
                radial = radial.Normalized();
                Vector2 tangent = new Vector2(-radial.Y, radial.X);
                aim = goal + (radial * OrbitRadius) + tangent * (OrbitRadius * 0.9f);
            }

            // ---- HEADING. This used to turn on the PEDALS alone, because the roll sign was unverified and I would
            // not guess at it on an aircraft with no self-levelling. The signs are measured now (heli_axis_probe),
            // so the BANK does the steering and the pedals only coordinate -- see the roll block below.
            Vector3 fwd = -Heli.GlobalTransform.Basis.Z;
            float headNow = Mathf.Atan2(-fwd.X, -fwd.Z);
            Vector2 toAim = aim - here;
            float headWant = Mathf.Atan2(-toAim.X, -toAim.Y);
            // SIGNS ARE MEASURED, NOT DERIVED (vehicle.heli_axis_probe). Yaw +1 drives AngularVelocity.Y NEGATIVE,
            // which DECREASES this heading convention -- so closing a positive error needs a negative stick. I
            // derived pitch off the torque expression correctly and then guessed these two; both were inverted and
            // put the aircraft in the ground on the first flight.
            float err = Mathf.Wrap(headWant - headNow, -Mathf.Pi, Mathf.Pi);
            // Pedals do the COORDINATION, not the turning. A hard pedal turn at speed sideslips and rolls the
            // machine, and a roll loop then fights that roll -- which is what produced strawberry's "rolling back
            // and forth 45 degrees each way". Low gain here; the bank below does the steering.
            float yaw = Mathf.Clamp(-0.80f * err + 0.55f * Heli.AngularVelocity.Y, -1f, 1f);

            // ---- SPEED, VIA A TARGET ATTITUDE. The cyclic is a RATE command, so feeding it a constant derived from
            // the speed error just rotates the machine forever: from a standing start it held -0.50 and pitched
            // straight over the top -- upY 1.00 -> 0.87 -> 0.52 -> 0.04 -> -0.70, inverted in four seconds, thrust
            // pointing at the ground. Speed error now sets a BOUNDED nose-down ANGLE, and a PD loop flies the
            // aircraft onto that angle. The bound is what makes it impossible to command past the vertical.
            Vector3 flat = new Vector3(Heli.LinearVelocity.X, 0f, Heli.LinearVelocity.Z);
            float speed = flat.Length();

            // PHASE TRANSITIONS, with hysteresis so a machine sitting near a boundary does not chatter between
            // two attitudes every tick -- which would read as exactly the nose-bobbing this is meant to remove.
            switch (Phase)
            {
                case FlightPhase.Transit:
                    if (range < ArriveDist) Phase = FlightPhase.Arrive;
                    break;
                case FlightPhase.Arrive:
                    // Done braking once the speed is near the orbit figure, or once it is genuinely on the circle.
                    if (speed < Heli.SpeedMaxMps * OrbitEnv.SpeedFrac * 1.15f || range < OrbitRadius * 1.3f)
                        Phase = FlightPhase.Orbit;
                    break;
                case FlightPhase.Orbit:
                    if (range > ArriveDist * 1.6f) Phase = FlightPhase.Transit;   // blown off station, or retargeted
                    break;
            }
            Envelope env = Phase == FlightPhase.Transit ? TransitEnv : Phase == FlightPhase.Arrive ? ArriveEnv : OrbitEnv;
            float wantSpeed = Heli.SpeedMaxMps * env.SpeedFrac;
            // COMMIT THE NOSE. 20 deg was too polite to build speed (strawberry: "it should definitely tilt forward
            // more"); 38 still leaves the thrust vector doing most of its work vertically.
            // THE GAIN IS THE LIMIT, NOT THE CLAMP. Raising the clamp 20 -> 38 deg changed nothing on its own:
            // at 1.5 deg per m/s a 4.8 m/s deficit only asked for 7 deg, so it trimmed at ~9.5 m/s well short of
            // cruise and never came near the bound. The per-phase TiltGain is what actually commits the nose.
            // Signs: a speed DEFICIT commands nose DOWN (negative), an EXCESS commands a nose-up flare.
            float wantNoseUpDeg = Mathf.Clamp(-env.TiltGain * (wantSpeed - speed), -env.MaxNoseDownDeg, env.MaxNoseUpDeg);
            float noseUpDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)));
            float pitchRateDeg = Mathf.RadToDeg(Heli.AngularVelocity.Dot(Heli.GlobalTransform.Basis.X));
            float pitch = Mathf.Clamp(0.05f * (wantNoseUpDeg - noseUpDeg) - 0.015f * pitchRateDeg, -1f, 1f);

            // ---- ROLL: BANK INTO THE TURN, then hold that bank. Roll +1 puts the right wing down (Basis.X.Y
            // negative), and turning right means a negative heading error, so the target bank is +k*err directly.
            //
            // Gains matter more than the shape here. 2.2 on bank saturates the stick past ~19 deg -- bang-bang, and
            // bang-bang oscillates, which is what strawberry saw. But a big bank TARGET is no better: at 0.45
            // (27 deg) with P=1.5 the orbit's standing heading error pinned the command and it rolled to 81 deg and
            // over. So: a small lean into the turn, low P, and damping as the dominant term.
            float bank = Heli.GlobalTransform.Basis.X.Y;
            float bankTarget = Mathf.Clamp(0.30f * err, -0.22f, 0.22f);   // sin-space: a gentle ~13 deg lean into the turn
            // MEASURED (heli_axis_probe): roll +1 produces a POSITIVE rate about forward, i.e. the rate is in the
            // same sense as the stick. So damping must SUBTRACT it. It was +, which is an ANTI-damper -- it fed the
            // roll rate back in and drove the oscillation strawberry reported ("rolling back and forth 45 degrees
            // each way"). It also explains why raising the "damping" gain made the divergence worse, not better.
            float roll = Mathf.Clamp(0.85f * (bank - bankTarget) - 1.10f * Heli.AngularVelocity.Dot(fwd), -1f, 1f);

            LastColl = collective; LastYaw = yaw; LastPitch = pitch; LastRoll = roll; LastErr = err; LastBank = bank;
            LastWantNoseUpDeg = wantNoseUpDeg; LastNoseUpDeg = noseUpDeg;
            Heli.DriveHeli(collective, yaw, pitch, roll, delta);
        }

        public float DebugWantY => Terr != null && Heli != null
            ? Terr.SampleHeight(Heli.GlobalPosition.X, Heli.GlobalPosition.Z) + CanopyClearance : 0f;
        public float DebugRange => Heli != null ? new Vector2(Heli.GlobalPosition.X - Target.X, Heli.GlobalPosition.Z - Target.Z).Length() : -1f;
        public float DebugWantClimbMps => _wantClimb;
        public float LastColl, LastYaw, LastPitch, LastRoll, LastErr, LastBank;   // telemetry: which axis is diverging
        public float LastWantNoseUpDeg, LastNoseUpDeg;   // the commanded vs actual attitude, per phase

        /// <summary>Spawn `name` at the nearest MAP EDGE and send it to the closest named node. Returns null if the
        /// map has no nodes to fly to, which is the one case where there is nothing sensible to do.</summary>
        public static NpcHeli Spawn(Node world, string name, Terrain terr, Vector3 near)
        {
            var nodes = MapNodes.Locations;
            if (nodes == null || nodes.Count == 0) return null;

            // Nearest named node to the reference point (the player, normally).
            var best = nodes[0];
            float bestD = float.MaxValue;
            foreach (var n in nodes)
            {
                float d = new Vector2(n.Pos.X - near.X, n.Pos.Z - near.Z).LengthSquared();
                if (d < bestD) { bestD = d; best = n; }
            }

            // Map edge: push out from the node along the direction of whichever boundary is closest, so the
            // approach is a real run in across the map rather than a spawn just off-camera.
            Vector3 at;
            if (terr != null)
            {
                var (minX, maxX, minZ, maxZ) = terr.WorldBoundsXZ();
                float inset = 60f;
                float dW = Mathf.Abs(best.Pos.X - minX), dE = Mathf.Abs(maxX - best.Pos.X);
                float dS = Mathf.Abs(best.Pos.Z - minZ), dN = Mathf.Abs(maxZ - best.Pos.Z);
                float m = Mathf.Min(Mathf.Min(dW, dE), Mathf.Min(dS, dN));
                at = m == dW ? new Vector3(minX + inset, 0f, best.Pos.Z)
                   : m == dE ? new Vector3(maxX - inset, 0f, best.Pos.Z)
                   : m == dS ? new Vector3(best.Pos.X, 0f, minZ + inset)
                             : new Vector3(best.Pos.X, 0f, maxZ - inset);
                at.Y = terr.SampleHeight(at.X, at.Z) + CanopyClearance;
            }
            // NO TERRAIN: the flight loop reads ground = 0 and therefore flies to an ABSOLUTE CanopyClearance,
            // so the spawn has to use the same datum. It used to spawn at best.Pos.Y + CanopyClearance, which is
            // node-RELATIVE -- on a node at y = 80 the aircraft was born 80 m above its own target height and
            // commanded a full descent on the first tick. Only reachable in a test rig today, but the two
            // branches disagreeing about what "height" means is exactly the bug that hides until it doesn't.
            else at = new Vector3(best.Pos.X + 600f, CanopyClearance, best.Pos.Z);

            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true;
            v.DebugInstantStart = true;
            v.SpawnRotorRunning();   // already on station: a cold 3.2 s spool-up from cruise height is a crash, not a start
            // Point it at the target and give it cruise speed, so it arrives flying rather than falling.
            Vector2 toT = new Vector2(best.Pos.X - at.X, best.Pos.Z - at.Z).Normalized();
            v.LookAt(new Vector3(best.Pos.X, at.Y, best.Pos.Z), Vector3.Up);
            v.LinearVelocity = new Vector3(toT.X, 0f, toT.Y) * (v.SpeedMaxMps * 0.5f);

            v.InfiniteTurretBelt = true;   // nobody is aboard to reload it
            var ai = new NpcHeli { Heli = v, Terr = terr, Target = best.Pos, TargetName = best.Name };
            world.AddChild(ai);
            return ai;
        }
    }
}
