using System;
using UnityEngine; // SDG.Compat Vector3

namespace SDG.Unturned
{
    /// <summary>Where a drop is in its life. Deliberately explicit rather than inferred from position,
    /// so a client that joins mid-descent can be told the phase rather than having to guess it.</summary>
    public enum AirdropPhase : byte
    {
        None = 0,
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
        /// <summary>Where the current (or last) drop is headed. Meaningless while Phase is None.</summary>
        public Vector3 Target { get; private set; }
        /// <summary>When the active drop began, in sim clock seconds.</summary>
        public double StartedAt { get; private set; }

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
        public bool Step(double dt, Func<Vector3> pickTarget)
        {
            _clock += dt;
            JustLanded = false;

            if (Phase == AirdropPhase.Falling && _clock - StartedAt >= FallSeconds)
            {
                Phase = AirdropPhase.Landed;
                JustLanded = true;
            }

            if (_clock < _nextAt) return false;
            _nextAt = _clock + IntervalSeconds;          // reschedule regardless, so a suppressed drop
                                                          // does not fire the instant the sky clears
            if (Phase == AirdropPhase.Falling) return false;

            Target = pickTarget != null ? pickTarget() : Vector3.zero;
            StartedAt = _clock;
            Phase = AirdropPhase.Falling;
            return true;
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
            if (Phase == AirdropPhase.None) return Vector3.zero;
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
