namespace SDG.Unturned
{
    // Fixed-timestep simulation clock mirroring Unturned's retail Unity "Fixed Timestep" = 0.02s (50 Hz)
    // and "Maximum Allowed Timestep" = 0.33s (both read from the retail ProjectSettings TimeManager).
    // Deterministic: the same sequence of real frame deltas always yields the same tick counts, so the
    // sim advances identically on any client/server framerate. This is the heartbeat movement / AI /
    // combat / replication all step on (plan 0c "SimRoot tick spine").
    public sealed class SimClock
    {
        public const double FixedDelta = 0.02;      // 50 Hz -- retail Fixed Timestep
        public const double MaxFrameDelta = 0.33;   // retail Maximum Allowed Timestep (anti spiral-of-death)
        const double Epsilon = 1e-9;                 // absorb FP noise at the step boundary (0.1/0.02 != 5.0 exactly)

        public double Accumulator { get; private set; }
        public long Tick { get; private set; }       // total fixed steps elapsed since Reset
        public double SimTime => Tick * FixedDelta;   // seconds of simulated time

        // Feed a real frame delta; returns how many fixed steps should run this frame. Leftover time
        // carries in Accumulator. Frame deltas above the retail clamp are capped (a long stall must not
        // trigger an unbounded catch-up).
        public int Advance(double frameDelta)
        {
            if (frameDelta < 0.0) frameDelta = 0.0;
            if (frameDelta > MaxFrameDelta) frameDelta = MaxFrameDelta;
            Accumulator += frameDelta;
            int steps = 0;
            while (Accumulator >= FixedDelta - Epsilon)
            {
                Accumulator -= FixedDelta;
                Tick++;
                steps++;
            }
            return steps;
        }

        public void Reset()
        {
            Accumulator = 0.0;
            Tick = 0;
        }
    }
}
