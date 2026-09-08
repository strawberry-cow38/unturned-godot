using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // AN AI DRIVER for a road vehicle (master 2026-09-08: "move onto an ai driver, using the sedan vehicle. tries to
    // maintain one of these lines (with some deviation) by applying gas/brakes (within reason, without spamming or
    // weird behavior) and directional controls. they may switch between lanes heading in the same direction, and upon
    // reaching near the end of a dead end, they will decide to turn around and head along the other side of the
    // road's direction").
    //
    // It drives through Vehicle.Drive(throttle, steer, handbrake) -- the SAME seam the player's hands reach, never by
    // writing EngineForce or Steering directly. Two writers on one set of controls is how you get a car that does
    // whatever ran last, and it also means everything the car already does to an input (the ignition crank, the
    // foot-brake-vs-reverse rule, the steering smoothing) applies to the AI for free.
    //
    // SIGNS, both established rather than assumed: Vehicle.Drive does `_steerTarget = -steer * angle`, so a POSITIVE
    // steer turns RIGHT; and a Godot basis is X-right / Y-up / -Z-forward, so a target with a positive LOCAL X is to
    // the right. Those two agree, which is why the steer command is just the local X of the aim point.
    public partial class VehicleAiDriver : Node
    {
        public Vehicle Car;
        public RoadField Roads;
        // THE LANE IT WAS PUT ON, handed over by whoever spawned it. Acquire() searches for the nearest lane
        // instead when this is null, and that search is a coin toss at spawn: a car sitting exactly on its own
        // lane point is 4.6 m from the OPPOSITE-direction lane beside it, which is close enough that one of three
        // cars picked the wrong one and spent the whole run at full lock trying to reach a line running the other
        // way. There is no reason to guess something the spawner knows for certain.
        public RoadField.LanePath StartPath;
        public int StartIndex;

        // --- tuning. Deliberately gentle: the brief says "within reason, without spamming or weird behavior". -----
        public static float CruiseFrac = 0.62f;      // of the car's own SpeedMax -- an AI pottering along, not racing
        public static float LatAccel = 3.2f;         // m/s^2 it is willing to pull through a bend -> the corner speed limit
        public static float ThrottleRate = 1.8f;     // max change in the throttle command per second: the anti-spam
        public static float GasBand = 0.35f;         // m/s of error below which it does not TOUCH the pedals (deadband)
        public static float BrakeBand = 1.20f;       // ...and how far over speed it must be before it brakes at all
        public static float Wheelbase = 3.0f;        // sedan front-to-rear axle, for the pure-pursuit steer angle
        public static float LaneChangeMin = 9f, LaneChangeMax = 22f;   // seconds between considering a lane change
        public static float TurnaroundAt = 32f;      // metres of path left before it starts looking for what is next
        public static float LinkRadius = 15f;        // a following lane passing this close to our end counts as a continuation
        public static float LinkAt = 14f;            // ...but only COMMIT to it this near the end, or the car cuts the corner
        public static float CrossGain = 0.55f;       // how hard the steady offset is corrected (0 = pure pursuit)
        public static float LostAt = 12f;            // metres off the line before it admits it is not on that lane any more
        public static float StuckAfter = 3f;         // seconds of "full throttle, no movement" before backing off
        public static float UnstickFor = 1.6f;       // ...and how long to reverse for when that happens

        RoadField.LanePath _path;
        int _idx;                     // index of the path point we are currently heading to
        float _throttle;              // the LAST command, so changes can be rate limited rather than snapped
        float _laneTimer;
        float _wanderPhase;           // "with some deviation": a slow lateral wander so cars do not ride the exact centreline
        bool _uTurning;               // braking for a dead end -> will flip to the opposite lane once slow enough
        float _flipping;              // seconds left of "swinging round onto a path that points the other way"
        float _stuck;                 // seconds spent asking to move and not moving
        float _unstick;               // seconds left of backing off whatever we are wedged against
        float _lost;                  // seconds spent nowhere near the line we are supposed to be on
        float _flipSteer;             // which way round the turn goes, held for the whole manoeuvre
        float _shuffleBlock;          // seconds of no progress in the current direction of the turn
        bool _shuffleRev;             // currently on the reverse leg of a three-point turn
        bool _flipDone;               // a turn-around has already run to completion since the last clean stretch
        float _flipCooldown;          // ...and how long before another may start
        readonly RandomNumberGenerator _rng = new();

        float _lastSteer;             // which way the wheels were pointing when we got wedged -> back off the other way
        public string Status { get; private set; } = "idle";   // harness/debug readout
        // UG_AIDBG=<path> writes the telemetry to that FILE rather than to stdout. Godot block-buffers a redirected
        // stdout on Windows -- a headless run sat for twelve minutes with 80 bytes in its log, all of it the engine
        // banner -- so a print-based harness reports nothing at all until the process exits. An auto-flushed writer
        // is readable WHILE the run is going, which is the whole point of watching a car drive for four minutes.
        static readonly string AiDbgPath = System.Environment.GetEnvironmentVariable("UG_AIDBG");
        static readonly bool AiDbg = !string.IsNullOrEmpty(AiDbgPath);
        static System.IO.StreamWriter _log;
        float _dbg, _chatter, _prevThr;

        public static void Log(string line)
        {
            if (!AiDbg) return;
            if (_log == null)
            {
                try { _log = new System.IO.StreamWriter(AiDbgPath, false) { AutoFlush = true }; }
                catch (System.Exception e) { GD.PrintErr($"[aidbg] cannot write {AiDbgPath}: {e.Message}"); return; }
            }
            _log.WriteLine(line);
        }

        /// <summary>Signed offset from the lane: POSITIVE when the car is to the RIGHT of the line it should be on
        /// (the same sense as the steer command, so it can be fed straight back in).</summary>
        float CrossTrackSigned(Vector3 pos)
        {
            var p = _path.Points;
            int a = Mathf.Max(0, _idx - 1), b = Mathf.Min(_idx, p.Length - 1);
            if (a == b) return 0f;
            Vector3 ab = p[b] - p[a];
            ab.Y = 0f;
            if (ab.LengthSquared() < 1e-6f) return 0f;
            Vector3 dir = ab.Normalized();
            Vector3 off = pos - p[a]; off.Y = 0f;
            return off.Dot(dir.Cross(Vector3.Up));   // dir x Up points RIGHT of travel (established by render, not reasoning)
        }

        /// <summary>Perpendicular distance to the lane segment we are on -- "does it maintain the line", as a number.</summary>
        float CrossTrack(Vector3 pos)
        {
            var p = _path.Points;
            int a = Mathf.Max(0, _idx - 1), b = Mathf.Min(_idx, p.Length - 1);
            if (a == b) return pos.DistanceTo(p[a]);
            Vector3 ab = p[b] - p[a];
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return pos.DistanceTo(p[a]);
            float t = Mathf.Clamp((pos - p[a]).Dot(ab) / len2, 0f, 1f);
            Vector3 c = p[a] + ab * t;
            return new Vector2(pos.X - c.X, pos.Z - c.Z).Length();   // horizontal only: ride height is not lane error
        }

        public const string Group = "aitraffic";   // how the drivers find EACH OTHER; nothing else is in it

        public override void _Ready()
        {
            AddToGroup(Group);
            _rng.Randomize();
            _wanderPhase = _rng.Randf() * Mathf.Tau;
            _laneTimer = _rng.RandfRange(LaneChangeMin, LaneChangeMax);
        }

        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            if (Car == null || !IsInstanceValid(Car) || Roads == null) return;
            if (_path == null && !Acquire()) { Status = "no lane"; return; }

            Vector3 pos = Car.GlobalPosition;
            AdvanceCursor(pos);

            float fwd = Car.LinearVelocity.Dot(-Car.GlobalTransform.Basis.Z);   // signed forward speed; front is -Z
            float speed = Mathf.Abs(fwd);

            // LOOKAHEAD grows with speed: a fixed one wobbles when slow and cuts corners when fast.
            float look = Mathf.Clamp(3.5f + speed * 0.75f, 5f, 18f);
            Vector3 aim = PointAhead(look, out float curvature, out float remaining);

            // "with some deviation" -- up to ~0.5 m of slow lateral drift, per driver, so a column of them does not
            // look like it is on rails. Applied to the AIM POINT, not the path, so it never accumulates.
            _wanderPhase += dt * 0.35f;
            Vector3 lateral = Car.GlobalTransform.Basis.X;
            aim += lateral * Mathf.Sin(_wanderPhase) * 0.5f;

            // Captured HERE, above every place that can swap the path -- the recovery below, HandleEnd and the
            // lane change all change it, and the re-aim at the bottom has to see any of the three.
            var was = _path;

            // ---- WEDGED? Full throttle and no movement is the one failure that never fixes itself: the car sits
            // there with the engine screaming until something else moves it, which is exactly the "weird behavior"
            // the brief rules out. Measured in the first run: one of three cars spent 95 of its 140 samples like
            // this. Back off with opposite lock for a moment and then work out where we are again.
            if (_unstick > 0f)
            {
                _unstick -= dt;
                Car.Drive(-0.6f, _lastSteer >= 0f ? -1f : 1f, false);
                _throttle = -0.6f;
                if (_unstick <= 0f) { _path = null; _stuck = 0f; Status = "unstuck"; }
                return;
            }
            if (speed < 0.5f && Mathf.Abs(_throttle) > 0.4f && _flipping <= 0f) _stuck += dt; else _stuck = 0f;
            if (_stuck > StuckAfter) { _unstick = UnstickFor; _stuck = 0f; return; }

            // ---- LOST THE LINE? A car this far from its lane is not "off the centre of it", it is on a different
            // piece of road, and pure pursuit will keep arcing toward a line it cannot reach. Take the nearest lane
            // going our way instead -- what a driver does after missing a turn. Not while swinging round: the whole
            // point of a U-turn is that the line is briefly a long way off.
            if (_flipping <= 0f && CrossTrack(pos) > LostAt) _lost += dt; else _lost = 0f;
            if (_lost > 2f) { _path = null; _lost = 0f; if (!Acquire()) { Status = "no lane"; return; } AdvanceCursor(pos); }

            HandleEnd(remaining, speed);
            _laneTimer -= dt;
            if (_laneTimer <= 0f) { _laneTimer = _rng.RandfRange(LaneChangeMin, LaneChangeMax); if (speed > 5f && !_uTurning) TryLaneChange(pos); }
            // A path swap invalidates the aim point we just computed -- it belongs to the OLD path, and on a U-turn
            // the old and new paths point OPPOSITE ways, so steering at a stale one aims the car back down the road
            // it just left for a frame. Re-aim on the path we are actually on now.
            if (!ReferenceEquals(was, _path))
            {
                aim = PointAhead(look, out curvature, out remaining);
                aim += lateral * Mathf.Sin(_wanderPhase) * 0.5f;
            }

            // ---- STEER: pure pursuit. Curvature to the aim point, through the bicycle model, normalised by the
            // car's own steering lock so the command is the -1..1 the Drive seam wants.
            Vector3 local = Car.GlobalTransform.AffineInverse() * aim;
            float dist = Mathf.Max(new Vector2(local.X, local.Z).Length(), 0.5f);
            float kappa = 2f * local.X / (dist * dist);                    // +X is right, and +steer is right: same sign
            // PURE PURSUIT ALONE PARKS ITSELF OFF THE LINE. With a lookahead of ~22 m at cruise, a steady offset
            // produces almost no curvature error, so the car happily tracks a line parallel to the one it wants --
            // measured at 2.7 m of steady cross-track, most of a lane. The second term is the offset fed straight
            // back as an angle (Stanley's correction): right of the line steers left, and it fades with speed so
            // it settles the car at 70 km/h instead of sawing at it.
            float steerRad = Mathf.Atan(kappa * Wheelbase) - CrossGain * Mathf.Atan(CrossTrackSigned(pos) / (speed + 2f));
            float steerMax = Mathf.DegToRad(Mathf.Max(Car.SteerMaxDegrees, 1f));
            float steer = Mathf.Clamp(steerRad / steerMax, -1f, 1f);
            // PURE PURSUIT IS UNDEFINED BEHIND THE CAR, and the U-turn is exactly that case. With the aim point
            // roughly astern, local.X is small however far round it is, so kappa -> 0 and the "turn around" command
            // comes out as STEER STRAIGHT AHEAD: the car would trundle off the end of the road perfectly straight.
            // Behind the axle, the right answer is not a curvature at all, it is full lock toward the side the
            // target is on. On a four-lane highway that alone completes the turn; on a narrow road it does not, and
            // the three-point shuffle further down takes over from here.
            // ...and A TARGET BEHIND THE CAR IS A TURN-AROUND, whatever put it there -- a dead end, a junction that
            // joined part-way along another road, a recovery onto a lane we had already passed. Entering the same
            // manoeuvre in every one of those cases is the difference between a three-point turn and a car pinned
            // at full lock forever: two of four cars were doing exactly that, one circling at 12 m/s and one
            // stationary with the throttle buried, because the steering override existed without the state that
            // slows it down and shunts it round. Entry needs the target CLEARLY behind, not merely abeam.
            if (local.Z > 2f && _flipping <= 0f && _flipCooldown <= 0f)
            {
                // A SECOND turn-around straight after the first is not a turn-around, it is a car being asked to
                // reach a line it cannot reach; re-entering forever is how one ends up pinned at full lock for the
                // rest of the session. Give up on the path instead and take the nearest lane we are actually on.
                if (_flipDone) { _flipDone = false; _flipCooldown = 4f; _path = null; if (!Acquire()) { Status = "no lane"; return; } AdvanceCursor(pos); }
                else { _flipping = 14f; _flipSteer = 0f; _shuffleRev = false; _shuffleBlock = 0f; }
            }
            _flipCooldown = Mathf.Max(0f, _flipCooldown - dt);
            if (local.Z > 0f) steer = local.X >= 0f ? 1f : -1f;
            if (fwd < -0.5f) steer = -steer;   // reversing: the front wheels push the nose the other way

            // ---- SPEED: the lowest of cruise, what the bend allows, and what the remaining path allows.
            float cruise = Car.SpeedMaxForward * CruiseFrac;
            float corner = curvature > 1e-4f ? Mathf.Sqrt(LatAccel / curvature) : 999f;
            float target = Mathf.Min(cruise, corner);
            if (_uTurning) target = Mathf.Min(target, 2.2f);                       // slow right down before flipping round
            else if (_flipping > 0f) target = Mathf.Min(target, 4.0f);             // ...and stay slow THROUGH the swing, not just up to it
            else if (remaining < 40f) target = Mathf.Min(target, 2.0f + remaining * 0.20f);   // ease off toward a dead end
            // ...and never close on the car in front faster than it can be given back: a two-second gap, so following
            // reads as backing off rather than as braking at the last moment.
            float ahead = GapAhead(pos, -Car.GlobalTransform.Basis.Z, 30f);
            if (ahead < 30f) target = Mathf.Min(target, Mathf.Max(0f, (ahead - 6f) * 0.5f));

            // ---- PEDALS, with a deadband and a rate limit. This is the whole of "no spamming": inside the band it
            // holds whatever it was doing, and it can never jump from full gas to full brake in one tick.
            // ...and this only runs when nothing else owns the pedal. It used to run ALWAYS, including mid-turn,
            // where the shuffle below then dragged the throttle the other way at exactly the same rate -- two
            // MoveTowards of equal rate in opposite directions cancel EXACTLY, so the throttle froze wherever it
            // happened to be and the reverse leg of the three-point turn never actually engaged. One car sat at
            // full throttle and full lock, not moving, for fifty seconds, with another queued politely behind it.
            if (_flipping <= 0f)
            {
                float err = target - fwd;
                float want;
                if (err > GasBand) want = Mathf.Clamp(err * 0.30f, 0.05f, 1f);
                else if (err < -BrakeBand) want = Mathf.Clamp(err * 0.22f, -0.75f, -0.05f);
                else want = _throttle * 0.85f;   // in the band: bleed the pedal toward neutral instead of chattering
                _throttle = Mathf.MoveToward(_throttle, want, ThrottleRate * dt);
            }

            // ---- THE TURN ITSELF, and it is a THREE-POINT TURN, not a sweep. The sedan locks to 28 deg over a 3 m
            // wheelbase: a 5.6 m radius, so a continuous 180 needs 11.3 m of tarmac. A four-lane highway has that;
            // the two-lane roads do not (9.2 m of lanes), and a car committed to one full-lock arc there drives
            // straight off the road and wedges -- measured, one car spent 95 of 140 samples stopped at full
            // throttle. So: swing forward on full lock, and the moment it stops making progress, back up on
            // OPPOSITE lock (front wheels the other way keeps the body rotating the same way) and swing again.
            // Which is what anybody does turning a car round on a country road.
            if (_flipping > 0f)
            {
                _flipping -= dt;
                if (_flipSteer == 0f) _flipSteer = steer >= 0f ? 1f : -1f;   // committed once; a turn that changes its mind never finishes
                Vector3 toAim = aim - pos;
                bool aligned = toAim.LengthSquared() > 1f && (-Car.GlobalTransform.Basis.Z).Dot(toAim.Normalized()) > 0.75f;
                if (aligned && !_shuffleRev) { _flipping = 0f; _flipSteer = 0f; _shuffleBlock = 0f; _flipDone = true; }
                else if (_flipping <= 0f) _flipDone = true;   // ran out of time: also counts as "tried that already"
                else
                {
                    if (speed < 0.6f && Mathf.Abs(_throttle) > 0.25f) _shuffleBlock += dt; else _shuffleBlock = 0f;
                    if (_shuffleBlock > 0.7f) { _shuffleRev = !_shuffleRev; _shuffleBlock = 0f; }
                    steer = _shuffleRev ? -_flipSteer : _flipSteer;
                    _throttle = Mathf.MoveToward(_throttle, _shuffleRev ? -0.45f : 0.45f, ThrottleRate * dt);
                }
            }
            else { _shuffleRev = false; _shuffleBlock = 0f; _flipSteer = 0f; if (speed > 6f) _flipDone = false; }   // a decent stretch of driving clears the "already tried that" flag

            _lastSteer = steer;
            Car.Drive(_throttle, steer, false);   // handbrake stays OFF: nothing here wants a locked axle

            // UG_AIDBG=1: the numbers that decide whether this is "within reason". CROSS-TRACK is how well it holds
            // the line, and THROTTLE CHATTER (how much the pedal moves per second) is the anti-spam claim -- neither
            // is judgeable from a video, and both are the actual brief.
            if (AiDbg)
            {
                _dbg += dt;
                float cross = CrossTrack(pos);
                _chatter += Mathf.Abs(_throttle - _prevThr); _prevThr = _throttle;
                if (_dbg >= 0.5f)
                {
                    Log($"[aidbg] {Name} t={Time.GetTicksMsec() / 1000f:0.0} r{_path.Road}/l{_path.Lane}{(_path.Forward ? "f" : "r")} v={fwd:0.0} tgt={target:0.0} thr={_throttle:+0.00;-0.00} steer={steer:+0.00;-0.00} cross={cross:0.00}m chatter={_chatter / _dbg:0.00}/s rem={remaining:0}{(_uTurning ? " UTURN" : "")}{(_flipping > 0f ? (_shuffleRev ? " FLIP-REV" : " FLIP") : "")}{(_unstick > 0f ? " UNSTICK" : "")}{(_lost > 0f ? " LOST" : "")}");
                    _dbg = 0f; _chatter = 0f;
                }
            }
            Status = $"lane {_path.Lane} {(_path.Forward ? "fwd" : "rev")} v={fwd:0.0}/{target:0.0} thr={_throttle:+0.00;-0.00} steer={steer:+0.00;-0.00} rem={remaining:0}m{(_uTurning ? " UTURN" : "")}";
        }

        /// <summary>Distance to the nearest other AI car ahead of `from` along `dir`, or a big number if the road is
        /// clear. Deliberately crude -- a cone test, not a traffic model. Master asked for a driver, not for traffic
        /// rules; this exists only so the obvious ugly outcome (one AI shunting another off the road, or two of them
        /// merging into the same patch of tarmac) does not happen in the first clip anybody watches.</summary>
        float GapAhead(Vector3 from, Vector3 dir, float within)
        {
            float gap = 9999f;
            foreach (var n in GetTree().GetNodesInGroup(Group))
            {
                if (ReferenceEquals(n, this) || n is not VehicleAiDriver o || o.Car == null || !IsInstanceValid(o.Car)) continue;
                Vector3 to = o.Car.GlobalPosition - from;
                float along = to.Dot(dir);
                if (along <= 0f || along > within) continue;
                if ((to - dir * along).Length() > 2.6f) continue;   // not in my lane-ish corridor
                gap = Mathf.Min(gap, along);
            }
            return gap;
        }

        /// <summary>Take the nearest lane path, entering it at the nearest point.</summary>
        bool Acquire()
        {
            if (StartPath != null && StartPath.Points.Length > 1)
            {
                _path = StartPath;
                _idx = Mathf.Clamp(StartIndex + 1, 1, StartPath.Points.Length - 1);
                StartPath = null;   // one-shot: a later re-acquire is a genuine "where am I" and must search
                return true;
            }
            var lanes = Roads.LanePaths;
            if (lanes == null || lanes.Count == 0) return false;
            Vector3 pos = Car.GlobalPosition;
            Vector3 nose = -Car.GlobalTransform.Basis.Z;
            float best = float.MaxValue; RoadField.LanePath bp = null; int bi = 0;
            float anyBest = float.MaxValue; RoadField.LanePath anyP = null; int anyI = 0;
            foreach (var l in lanes)
                for (int i = 0; i < l.Points.Length - 1; i++)
                {
                    // ONLY lanes that run roughly the way we are pointing. Distance alone cannot tell the two
                    // directions of a road apart -- they are one lane width away from each other -- so the
                    // nearest point is a coin toss between driving the road and driving into the oncoming side.
                    Vector3 dir = l.Points[i + 1] - l.Points[i];
                    if (dir.LengthSquared() < 1e-4f) continue;
                    float d = l.Points[i].DistanceSquaredTo(pos);
                    if (d < anyBest) { anyBest = d; anyP = l; anyI = i; }
                    if (dir.Normalized().Dot(nose) < 0.3f) continue;
                    if (d < best) { best = d; bp = l; bi = i; }
                }
            // ...but a car parked across the road, or one that has just come to rest facing a hedge, matches
            // NOTHING on direction. Returning false there leaves it with no path at all, and a driver with no path
            // never touches the controls again -- a silent permanent freeze, which is worse than any wrong lane.
            // Fall back to the nearest lane whatever way it runs; the turnaround logic can sort the direction out.
            if (bp == null) { bp = anyP; bi = anyI; }
            if (bp == null) return false;
            _path = bp; _idx = Mathf.Min(bi + 1, bp.Points.Length - 1);
            return true;
        }

        /// <summary>Walk the cursor past every point we are already level with, so the aim point is always ahead.</summary>
        void AdvanceCursor(Vector3 pos)
        {
            var p = _path.Points;
            while (_idx < p.Length - 1)
            {
                Vector3 seg = p[_idx + 1] - p[_idx];
                if (seg.LengthSquared() < 1e-4f) { _idx++; continue; }
                if ((pos - p[_idx]).Dot(seg.Normalized()) > seg.Length()) _idx++; else break;
            }
        }

        /// <summary>The point `dist` metres along the path from the cursor, plus the curvature over that stretch and
        /// how much path is left. Curvature is the turn angle per metre, which is what a corner speed limit needs.</summary>
        Vector3 PointAhead(float dist, out float curvature, out float remaining)
        {
            var p = _path.Points;
            curvature = 0f;
            remaining = 0f;
            for (int i = _idx; i < p.Length - 1; i++) remaining += p[i].DistanceTo(p[i + 1]);
            float acc = 0f;
            int j = _idx;
            Vector3 prevDir = Vector3.Zero;
            float turned = 0f, over = 0f;
            while (j < p.Length - 1 && acc < dist)
            {
                Vector3 seg = p[j + 1] - p[j];
                float len = seg.Length();
                if (len > 1e-4f)
                {
                    Vector3 d = seg / len;
                    if (prevDir != Vector3.Zero) turned += Mathf.Acos(Mathf.Clamp(prevDir.Dot(d), -1f, 1f));
                    prevDir = d; over += len;
                }
                acc += len; j++;
            }
            curvature = over > 1f ? turned / over : 0f;
            if (j >= p.Length - 1) return p[p.Length - 1];
            // interpolate the last partial segment so the aim point does not snap from node to node
            float back = acc - dist;
            Vector3 a = p[j - 1], b = p[j];
            float sl = a.DistanceTo(b);
            return sl > 1e-4f ? b - (b - a).Normalized() * Mathf.Min(back, sl) : b;
        }

        /// <summary>Near the end: hand onto a following lane if the network continues, otherwise treat it as a dead
        /// end -- slow down, and once slow enough take the lane going the other way and drive back.</summary>
        void HandleEnd(float remaining, float speed)
        {
            if (remaining > TurnaroundAt) { _uTurning = false; return; }
            Vector3 end = _path.Points[_path.Points.Length - 1];
            Vector3 endDir = EndHeading(_path);

            if (!_uTurning)
            {
                // A CONTINUATION is any lane path starting near where this one ends and pointing roughly the same
                // way. Roads meet at junctions rather than sharing indices, so this is a geometric join, not a graph.
                // A CONTINUATION IS A JUNCTION, NOT A SHARED ENDPOINT. Roads are separate splines that merely meet,
                // and they meet wherever they meet -- a stub joining a highway lands part-way ALONG the highway's
                // lanes, nowhere near their Points[0]. Matching only against lane starts found almost nothing, so
                // the cars shuttled up and down their own segment instead of touring the network: one of them turned
                // around eight times in 149 s on a 99 m stub. Take the nearest point of any lane passing close to
                // our end and heading roughly our way, and join it THERE.
                RoadField.LanePath link = null; int li = 0; float lbest = LinkRadius * LinkRadius;
                foreach (var l in Roads.LanePaths)
                {
                    // ANOTHER ROAD, never this one. A same-road lane that happens to pass close to our end is the
                    // lane NEXT DOOR, and joining it puts us just as near ITS end, which links straight back --
                    // the two lanes of a road ping-ponged 147 times in 100 s and the cars never got above walking
                    // pace, because being permanently "about to run out of road" also pins the speed cap down.
                    if (l.Road == _path.Road || l.Points.Length < 2) continue;
                    for (int i = 0; i < l.Points.Length - 1; i++)
                    {
                        float d2 = l.Points[i].DistanceSquaredTo(end);
                        if (d2 >= lbest) continue;
                        Vector3 d = l.Points[i + 1] - l.Points[i];
                        // PEI's SIDE ROADS MEET THE HIGHWAY AT RIGHT ANGLES. Dumping the whole network's junction
                        // geometry: road 1 ends 2.0 m from road 2 with a heading dot of 0.03, road 10 at 2.7 m /
                        // 0.04, road 11 at 2.0 m / -0.09 -- T-junctions, every one. Demanding the next road run
                        // within 60 degrees of this one therefore rejected EVERY junction on the island, so each
                        // side road was classified a dead end and the cars turned round at the top of it instead of
                        // turning ONTO the highway: 13 and 19 U-turns in 132 s. A junction is a TURN. All that has
                        // to be excluded is a hairpin straight back the way we came.
                        if (d.LengthSquared() < 1e-4f || d.Normalized().Dot(endDir) < -0.2f) continue;
                        // ...and it has to actually GO somewhere: joining a lane 5 m from its own end is just the
                        // same dead end wearing a different hat, and we would be straight back in here next frame.
                        float onward = 0f;
                        for (int j = i; j < l.Points.Length - 1 && onward <= TurnaroundAt; j++) onward += l.Points[j].DistanceTo(l.Points[j + 1]);
                        if (onward <= TurnaroundAt) continue;
                        lbest = d2; link = l; li = i;
                    }
                }
                // A link found from 32 m out is not one to take yet -- retargeting that early has the car leave the
                // side road diagonally and cut the corner. Keep driving (and the speed easing below is already
                // slowing us for the turn); commit when we are actually at the junction.
                if (link != null)
                {
                    if (remaining > LinkAt) return;
                    _path = link; _idx = Mathf.Min(li + 1, link.Points.Length - 1); return;
                }
                _uTurning = true;   // nothing follows at all: this really is the dead end
            }

            if (speed > 2.6f) return;   // still rolling -- keep braking before committing to the turn
            // Take the OPPOSITE-direction lane of this same road, entering at whichever end we are actually near.
            // Pick the return lane by ITS ENTRY, which for an opposite-direction lane of the same road sits at the
            // end we have just arrived at -- so this is "the nearest one going the other way", not a guess.
            RoadField.LanePath back = null; float best = float.MaxValue;
            foreach (var l in Roads.LanePaths)
            {
                if (l.Road != _path.Road || l.Forward == _path.Forward) continue;
                float d = l.Points[0].DistanceTo(Car.GlobalPosition);
                if (d < best) { best = d; back = l; }
            }
            // A one-way stub with no return lane. Do NOT just clear the flag and sit here -- that leaves the car
            // stopped at the end of the road for the rest of its life, quietly, which is the failure mode that is
            // hardest to notice and worst to watch. Drop the path and let Acquire find the nearest lane instead.
            if (back == null) { _uTurning = false; _path = null; Acquire(); return; }
            _path = back;
            _idx = 0;      // ...and 0 IS this end: BuildLanePaths pre-reverses the against-the-spline lanes, so every
                           // path's Points[0] is its own ENTRY, never the road's start. Nothing here drives a path backwards.
            _uTurning = false;
            _flipping = 14f;   // enough for a three-point turn with a couple of extra shunts, not just one sweep
        }

        static Vector3 EndHeading(RoadField.LanePath l)
        {
            var p = l.Points;
            Vector3 d = p[p.Length - 1] - p[Mathf.Max(0, p.Length - 2)];
            return d.LengthSquared() > 1e-6f ? d.Normalized() : Vector3.Forward;
        }

        /// <summary>Move to another lane going the SAME way on the SAME road -- an overtake-ish drift, not a swerve.
        /// The pure pursuit does the blending: retargeting is enough, because the aim point is metres ahead.</summary>
        void TryLaneChange(Vector3 pos)
        {
            var options = new List<RoadField.LanePath>();
            foreach (var l in Roads.LanePaths)
                if (l.Road == _path.Road && l.Forward == _path.Forward && !ReferenceEquals(l, _path)) options.Add(l);
            if (options.Count == 0) return;
            var pick = options[_rng.RandiRange(0, options.Count - 1)];
            int bi = 0; float best = float.MaxValue;
            for (int i = 0; i < pick.Points.Length; i++)
            {
                float d = pick.Points[i].DistanceSquaredTo(pos);
                if (d < best) { best = d; bi = i; }
            }
            // IS THE GAP EMPTY? The check has to run from a point IN THE TARGET LANE, not from where we are:
            // GapAhead only sees a 2.6 m corridor, and the next lane over is 4.6 m away, so checking from our own
            // position would look straight past exactly the car we are about to merge into. Look both ways -- pulling
            // out in front of somebody is as bad as pulling out into them -- and on a refusal just give up; the timer
            // comes round again in a few seconds and we do not want a car hunting for a slot every frame.
            Vector3 slot = pick.Points[bi];
            Vector3 hdg = -Car.GlobalTransform.Basis.Z;
            if (GapAhead(slot, hdg, 22f) < 22f || GapAhead(slot, -hdg, 14f) < 14f) return;
            _path = pick;
            _idx = Mathf.Min(bi + 1, pick.Points.Length - 1);
        }
    }
}
