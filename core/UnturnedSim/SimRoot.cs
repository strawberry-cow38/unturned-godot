using System.Collections.Generic;

namespace SDG.Unturned
{
    // A system that advances once per fixed sim step.
    public interface ISimStepped
    {
        void SimStep(long tick, double dt);
    }

    // The simulation root: owns the SimClock and the ordered set of stepped systems (movement, AI,
    // combat, replication...). The engine (Godot _PhysicsProcess or a headless loop) calls Frame(dt)
    // once per rendered/loop frame; SimRoot runs the right number of fixed steps, in registration order,
    // with consecutive tick numbers. Engine-agnostic on purpose -- identical on client and dedicated server.
    public sealed class SimRoot
    {
        readonly SimClock _clock = new SimClock();
        readonly List<ISimStepped> _systems = new List<ISimStepped>();

        public SimClock Clock => _clock;
        public int SystemCount => _systems.Count;

        public void Add(ISimStepped system) => _systems.Add(system);
        public bool Remove(ISimStepped system) => _systems.Remove(system);

        // Drive the sim by one engine frame. Returns the number of fixed steps executed.
        public int Frame(double frameDelta)
        {
            int steps = _clock.Advance(frameDelta);
            long endTick = _clock.Tick;
            for (int i = 0; i < steps; i++)
            {
                long tick = endTick - steps + 1 + i;
                for (int s = 0; s < _systems.Count; s++)
                    _systems[s].SimStep(tick, SimClock.FixedDelta);
            }
            return steps;
        }

        public void Reset() => _clock.Reset();
    }
}
