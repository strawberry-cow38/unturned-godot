using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>Where a drop is in its life. Deliberately explicit rather than inferred from position,
    /// so a client that joins mid-descent can be told the phase rather than having to guess it.</summary>
    public enum AirdropPhase : byte
    {
        None = 0,
        /// <summary>A cargo plane is crossing the map toward the release point. The crate does not
        /// exist yet -- this phase IS the telegraph, and it is the whole point of the event.</summary>
        Inbound = 3,
        Falling = 1,
        Landed = 2,
    }

    /// <summary>
    /// When a supply drop happens, where it lands, and how it gets there.
    ///
    /// Engine-free and deterministic: given the same tick and the same seed, every machine computes
    /// the same drop at the same place. That is what lets the server own the decision while a client
    /// renders a crate that agrees with it, and it is why descent is a closed-form function of elapsed
    /// time rather than an accumulating velocity -- a client that joins halfway through, or drops a
    /// frame, still puts the crate exactly where the server has it.
    ///
    /// The sim does NOT own loot. What is in the crate is the container system's business; this owns
    /// only the event and the trajectory.
    /// </summary>
    public sealed class AirdropSim
    {
        /// <summary>Seconds between drops. Retail's cadence is a server setting rather than a constant,
        /// so this is a field, not a const.</summary>
        public float IntervalSeconds = 600f;

        /// <summary>How high a drop starts above its landing point.</summary>
        public float DropHeight = 220f;

        /// <summary>Descent speed. A parachuted crate falls slowly and at a CONSTANT rate -- it is not
        /// in free fall, and making it accelerate would put it on the ground in about six seconds and
        /// rob the event of the thing that makes it interesting: everyone can see it coming.</summary>
        public float FallSpeed = 18f;

        /// <summary>How fast the cargo plane crosses the map, m/s.</summary>
        public float PlaneSpeed = 90f;

        /// <summary>How far above the release point the plane flies. Retail randomises 450-475 m; the
        /// server rolls it and SENDS it, so a client never has to reproduce the roll.</summary>
        public float FlightHeightMin = 450f, FlightHeightMax = 475f;

        /// <summary>How far back from the map edge the plane is spawned, so it is already at cruising
        /// speed and altitude when it becomes visible rather than popping into existence.</summary>
        public float ApproachRunway = 2048f;

        double _clock;
        double _nextAt;

        public AirdropPhase Phase { get; private set; } = AirdropPhase.None;

        /// <summary>True for the single Step in which a crate touched down.
        ///
        /// Reported explicitly rather than left for a caller to infer by comparing Phase before and
        /// after, because BOTH transitions can happen in one Step: a crate lands and, on the same
        /// tick, the next drop begins and puts the phase straight back to Falling. A before/after
        /// comparison sees Falling -> Falling and silently loses the landing, so the crate never lands
        /// for any client. The sim owns the transition, so the sim reports it.</summary>
        public bool JustLanded { get; private set; }

        /// <summary>True for the single Step in which the plane let go of the crate. Same reasoning as
        /// JustLanded: the caller must not have to infer a transition it can miss.</summary>
        public bool JustReleased { get; private set; }

        /// <summary>Whether the aircraft still exists.
        ///
        /// Deliberately NOT derived from Phase, which was the bug: drawing the plane only while Inbound
        /// made it blink out of existence at the exact instant it dropped the crate, in full view of
        /// anyone who had followed it across the map. Retail keeps flying and removes the model only
        /// once it has passed clean off the far side -- state.x * sign(velocity.x) beyond
        /// (Level.size / 2) + 2048, and likewise for z. The crate's phase and the plane's lifetime are
        /// two different clocks and this one outlives the other.</summary>
        public bool PlaneVisible { get; private set; }

        /// <summary>Where the current (or last) drop is headed. Meaningless until the crate is released
        /// -- during Inbound this is the PREDICTED landing point, which is exactly what a player is
        /// trying to work out by watching the plane.</summary>
        public Vector3 Target { get; private set; }
        /// <summary>When the crate was released, in sim clock seconds.</summary>
        public double StartedAt { get; private set; }

        /// <summary>Where the plane entered, its velocity, and when it releases. These three are the
        /// ENTIRE wire payload for a drop.
        ///
        /// Retail does not send the landing coordinate, and the source says why in one line -- "delay
        /// is calculated here because we don't send the drop coordinate". The client is told a plane
        /// and a timer, and has to watch it to learn where the crate is going. Sending the target would
        /// be less bandwidth and would quietly delete the mechanic.</summary>
        public Vector3 PlaneStart { get; private set; }
        public Vector3 PlaneVelocity { get; private set; }
        /// <summary>Sim-clock instant the crate detaches.</summary>
        public double ReleaseAt { get; private set; }

        /// <summary>Seconds until the next drop begins. Negative is clamped to 0 rather than reported,
        /// because "overdue" is not a state a caller can do anything useful with.</summary>
        public double SecondsUntilNext => Math.Max(0.0, _nextAt - _clock);

        public AirdropSim(double firstDropAfterSeconds = 600.0)
        {
            _nextAt = firstDropAfterSeconds;
        }

        /// <summary>Total descent time for the configured height and speed.</summary>
        public float FallSeconds => FallSpeed > 0.001f ? DropHeight / FallSpeed : 0f;

        /// <summary>
        /// Advance the clock. Returns true on the tick a NEW drop begins, so a caller can broadcast it.
        /// A drop already in the air suppresses the next one rather than queueing it -- two crates in
        /// the sky at once reads as a bug, and an unbounded queue would let a paused server dump a
        /// dozen crates the moment it resumes.
        /// </summary>
        public bool Step(double dt, Func<Vector3> pickTarget) => Step(dt, pickTarget, null);

        /// <summary>
        /// Advance the clock. Returns true on the tick a NEW drop begins -- meaning the PLANE launches,
        /// not the crate. `roll` supplies the two server-side random choices (approach axis, flight
        /// height); pass null for a deterministic centre-of-range plane, which is what tests want.
        /// </summary>
        public bool Step(double dt, Func<Vector3> pickTarget, Func<double> roll)
        {
            _clock += dt;
            JustLanded = false;
            JustReleased = false;

            // Release comes first: the plane reaching its mark is what creates the crate.
            if (Phase == AirdropPhase.Inbound && _clock >= ReleaseAt)
            {
                var at = PlanePositionAt(ReleaseAt);
                Target = new Vector3(at.x, Target.y, at.z);   // keep the ground height picked at launch
                StartedAt = ReleaseAt;                        // NOT _clock: the crate left on the mark,
                                                              // so a late tick must not shift the fall
                Phase = AirdropPhase.Falling;
                JustReleased = true;
            }

            if (Phase == AirdropPhase.Falling && _clock - StartedAt >= FallSeconds)
            {
                Phase = AirdropPhase.Landed;
                JustLanded = true;
            }

            if (PlaneVisible && HasLeftTheLevel(PlanePositionAt(_clock))) PlaneVisible = false;

            if (_clock < _nextAt) return false;
            _nextAt = _clock + IntervalSeconds;          // reschedule regardless, so a suppressed drop
                                                          // does not fire the instant the sky clears
            if (Phase == AirdropPhase.Inbound || Phase == AirdropPhase.Falling) return false;

            var target = pickTarget != null ? pickTarget() : Vector3.zero;
            LaunchPlaneToward(target, roll);
            return true;
        }

        /// <summary>
        /// Work out the plane's entry point, heading and release instant for a given landing spot.
        ///
        /// Mirrors retail's geometry: a coin-flip between a horizontal and a vertical approach, an
        /// entry on the opposite side of the map from the target, a cruising height 450-475 m above it,
        /// and the entry pushed a further ApproachRunway metres back along the heading so the plane is
        /// already up to speed before anyone can see it.
        /// </summary>
        void LaunchPlaneToward(Vector3 target, Func<double> roll)
        {
            double r1 = roll != null ? roll() : 0.75;   // >=0.5 -> vertical approach when undetermined
            double r2 = roll != null ? roll() : 0.5;
            double r3 = roll != null ? roll() : 0.5;

            float half = MapHalfSize;
            var start = new Vector3(0f, 0f, 0f);
            if (r1 < 0.5)                                // horizontal, e.g. east -> west
            {
                start.x = half * -Sign(target.x);
                start.z = (float)(r2 * half) * -Sign(target.z);
            }
            else                                         // vertical, e.g. north -> south
            {
                start.x = (float)(r2 * half) * -Sign(target.x);
                start.z = half * -Sign(target.z);
            }

            float flightHeight = target.y + FlightHeightMin + (float)(r3 * (FlightHeightMax - FlightHeightMin));

            // Heading is computed on the FLAT plane so the plane cruises level; the height is applied
            // to both ends afterwards. Mixing the climb into the heading would tilt the whole approach.
            var flatTarget = new Vector3(target.x, 0f, target.z);
            var flatStart = new Vector3(start.x, 0f, start.z);
            var dir = Normalize(flatTarget - flatStart);
            flatStart -= dir * ApproachRunway;

            PlaneStart = new Vector3(flatStart.x, flightHeight, flatStart.z);
            LaunchedAt = _clock;
            PlaneVelocity = dir * PlaneSpeed;
            double distance = Magnitude(new Vector3(flatTarget.x - flatStart.x, 0f, flatTarget.z - flatStart.z));
            ReleaseAt = _clock + (PlaneSpeed > 0.001f ? distance / PlaneSpeed : 0.0);

            Target = target;            // provisional: the true landing point is re-derived on release
            Phase = AirdropPhase.Inbound;
            PlaneVisible = true;
        }

        /// <summary>Half the playable extent, used to place the plane's entry edge.</summary>
        public float MapHalfSize = 1024f;

        /// <summary>Retail's exit test, verbatim in effect: the aircraft is gone once its position along
        /// EITHER axis, measured in the direction it is travelling, is past the map's half-extent plus
        /// the approach runway. Comparing the signed coordinate rather than a distance is what makes the
        /// test one-sided -- a plane that entered at -3000 heading east is not "far away" at +2000, it
        /// has crossed, and only then does it count as having left.</summary>
        bool HasLeftTheLevel(Vector3 at)
        {
            float edge = MapHalfSize + ApproachRunway;
            return at.x * Sign(PlaneVelocity.x) > edge || at.z * Sign(PlaneVelocity.z) > edge;
        }

        static float Sign(float v) => v < 0f ? -1f : 1f;
        static float Magnitude(Vector3 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        static Vector3 Normalize(Vector3 v)
        {
            float m = Magnitude(v);
            return m < 1e-5f ? new Vector3(1f, 0f, 0f) : new Vector3(v.x / m, v.y / m, v.z / m);
        }

        /// <summary>When the plane entered. Stored rather than derived from ReleaseAt minus a flight
        /// time, because Target moves on release and deriving it from Target would make the plane's
        /// own past depend on something that changes underneath it.</summary>
        public double LaunchedAt { get; private set; }

        /// <summary>Where the plane is at a given clock. Closed-form for the same reason the descent is:
        /// a client that joins mid-flight must draw it exactly where the server has it. Keeps flying
        /// past the release point -- the plane does not stop when the crate leaves.</summary>
        public Vector3 PlanePositionAt(double clock)
        {
            float t = (float)Math.Max(0.0, clock - LaunchedAt);
            return new Vector3(PlaneStart.x + PlaneVelocity.x * t,
                               PlaneStart.y + PlaneVelocity.y * t,
                               PlaneStart.z + PlaneVelocity.z * t);
        }

        /// <summary>Seconds of flight before the crate detaches.</summary>
        public double FlightSeconds => Math.Max(0.0, ReleaseAt - LaunchedAt);

        /// <summary>Adopt a plane the server announced. The client is given exactly these facts and
        /// derives everything else, including where the crate will land.</summary>
        public void AdoptPlane(Vector3 start, Vector3 velocity, double launchedAt, double releaseAt, float groundY)
        {
            PlaneStart = start;
            PlaneVelocity = velocity;
            LaunchedAt = launchedAt;
            ReleaseAt = releaseAt;
            Target = new Vector3(0f, groundY, 0f);
            Phase = AirdropPhase.Inbound;
            PlaneVisible = true;
        }

        /// <summary>Reschedule the NEXT drop, relative to now.
        ///
        /// Exists because setting IntervalSeconds alone is a trap: the first drop is scheduled at
        /// construction, so changing the interval afterwards leaves that first one where it was and a
        /// caller who set a 1-second interval waits the original ten minutes. Found by a test that
        /// looked correct and timed out; better to give the intent a name than to expect every caller
        /// to know the constructor already booked one.</summary>
        public void ScheduleNextIn(double seconds) => _nextAt = _clock + Math.Max(0.0, seconds);

        /// <summary>Force a drop now, for an admin command or a test. Returns false if one is already
        /// falling, for the same reason Step suppresses.</summary>
        public bool ForceDrop(Vector3 target)
        {
            if (Phase == AirdropPhase.Falling) return false;
            Target = target;
            StartedAt = _clock;
            Phase = AirdropPhase.Falling;
            _nextAt = _clock + IntervalSeconds;
            return true;
        }

        /// <summary>Adopt a start time announced by the server. Taken wholesale rather than adjusted to
        /// local time: the whole point of a closed-form trajectory is that both machines integrate from
        /// the SAME origin, and re-basing it here would put the drift straight back.</summary>
        public void AdoptStart(double startedAt) => StartedAt = startedAt;

        /// <summary>Where the crate is right now. Closed-form in elapsed time, so it does not drift
        /// between machines and a late joiner can be placed correctly from the start time alone.</summary>
        public Vector3 PositionAt(double clock)
        {
            // No crate exists during None or Inbound -- the plane still has it.
            if (Phase == AirdropPhase.None || Phase == AirdropPhase.Inbound) return Vector3.zero;
            float elapsed = (float)Math.Max(0.0, clock - StartedAt);
            float fallen = Math.Min(elapsed * FallSpeed, DropHeight);
            return new Vector3(Target.x, Target.y + (DropHeight - fallen), Target.z);
        }

        public Vector3 CurrentPosition => PositionAt(_clock);
        public double Clock => _clock;

        /// <summary>Mark the current drop collected/cleared so the cycle can start again.</summary>
        public void Clear() => Phase = AirdropPhase.None;
    }
}
